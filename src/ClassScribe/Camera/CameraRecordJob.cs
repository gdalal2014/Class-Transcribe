using System.Globalization;
using System.Text.Json;
using ClassScribe.Audio.Timeline;
using ClassScribe.Link;
using ClassScribe.Storage;

namespace ClassScribe.Camera;

/// <summary>
/// The record job (teach-video 6.6 to 6.12, slice 7): a lesson's picture written into one piece,
/// <c>&lt;base&gt;.cam.mp4</c>, or <c>&lt;base&gt;.cam@hh-mm-ss.fff.mp4</c> when it begins later on the clock, at the run's
/// picture size with the fixed encoder settings (<see cref="PieceSettings"/>), the hardware encoder asked for and Windows'
/// software encoder taken once when it refuses. The camera is opened only shared, its reader converting to NV12 at the
/// picture's size, through <see cref="CameraRoute"/> on the process's STA thread. Each frame waits half a second in the
/// queue (<see cref="FrameQueue"/>) and is then placed on the run's clock, by the latest clock line, with the camera's
/// offset (<see cref="FramePlacer"/>). The starter's off lets the camera go for a pause, its on takes it again; its stop
/// writes the waiting frames, lets the last last to the end position, finishes the piece and says so; its cancel, or its
/// end of the lines closing, finishes the piece as far as it goes.
/// </summary>
/// <remarks>
/// <para>Watched once every <see cref="LookEvery"/>: a camera that cannot be opened is said once per cause and tried again
/// every <see cref="RetryEvery"/>; one that opens but sends no frame in <see cref="FirstFrameLimit"/> is said (a security
/// program may be blocking it) and let go; one whose frames stop for <see cref="StoppedAfter"/> (unplugged, folded) gets a
/// black frame right after its last one, is let go and said, and is opened again every <see cref="RetryEvery"/>; the same piece
/// goes on. A picture black for <see cref="DarkAfter"/> is said once, and the status says dark until it brightens. A status
/// line goes out once a second. An encoder or a disk that fails finishes the piece as far as it goes and ends the job with
/// failed, exit 1, for the starter to start a new piece; so does a call on the route that does not return.</para>
/// </remarks>
/// <param name="channel">The lines to and from the starter.</param>
/// <param name="job">The job: the camera, the picture's size, the offset, the recording's files and the piece's start.</param>
/// <param name="captures">Makes the camera's capture.</param>
/// <param name="writers">Makes the piece's writer.</param>
/// <param name="thread">The camera process's STA thread, on which every call on the route is made.</param>
/// <param name="time">The clock the job is timed on; the frames' stamps are on its counter.</param>
/// <param name="callLimit">How long the route's calls may take before the job gives up.</param>
public sealed class CameraRecordJob(LineChannel channel, CameraJob job, ICameraCaptureFactory captures, IPieceWriterFactory writers, CameraThread thread,
    TimeProvider time, TimeSpan callLimit)
{
    /// <summary>How often a camera that cannot be opened, or that was lost, is opened again.</summary>
    public static readonly TimeSpan RetryEvery = TimeSpan.FromSeconds(2);

    /// <summary>How long without a frame, after frames came, before the camera counts as lost.</summary>
    public static readonly TimeSpan StoppedAfter = TimeSpan.FromSeconds(3);

    /// <summary>How long an open may go without its first frame before the camera counts as giving no picture.</summary>
    public static readonly TimeSpan FirstFrameLimit = TimeSpan.FromSeconds(5);

    /// <summary>How long the picture must stay black before it is said.</summary>
    public static readonly TimeSpan DarkAfter = TimeSpan.FromSeconds(10);

    /// <summary>How often the job looks at its frames and places the waiting ones.</summary>
    public static readonly TimeSpan LookEvery = TimeSpan.FromMilliseconds(100);

    /// <summary>How often a status line is sent.</summary>
    public static readonly TimeSpan StatusEvery = TimeSpan.FromSeconds(1);

    /// <summary>A frame's time at the piece's 30 frames a second: how long the last frame lasts at a cancel, and where a lost camera's black frame goes after it.</summary>
    public static readonly TimeSpan FrameTime = TimeSpan.FromSeconds(1.0 / PieceSettings.FramesPerSecond);

    /// <summary>The words of the problem when the camera opened but sent no picture.</summary>
    public const string NoPictureWords = "it opened but sent no picture";

    /// <summary>The words of the problem when the picture has been black for <see cref="DarkAfter"/>.</summary>
    public const string DarkWords = "its picture has been black for 10 seconds";

    /// <summary>Guards the latest clock line, which the control's reader replaces and the loop reads.</summary>
    private readonly Lock _gate = new();

    /// <summary>The latest clock line, by which the waiting frames are placed.</summary>
    private ClockState _clock = ClockState.Empty;

    /// <summary>False while the starter's off holds (a pause): the camera is let go.</summary>
    private volatile bool _wantOpen = true;

    /// <summary>The end position the starter's stop gave, in ticks; -1 before it.</summary>
    private long _stopAtTicks = -1;

    /// <summary>The end position the starter's stop gave; null before it. Read from any thread: the frames', the loop's and the control's.</summary>
    private TimeSpan? StopAt => Volatile.Read(ref _stopAtTicks) is var ticks and >= 0 ? TimeSpan.FromTicks(ticks) : null;

    /// <summary>True once the starter's cancel came, or its end of the lines closed.</summary>
    private volatile bool _cancelled;

    /// <summary>True once a send found the starter gone: nothing more is sent.</summary>
    private volatile bool _starterGone;

    /// <summary>Ends the waits of the job's loop: the starter's stop, cancel, or its end closing.</summary>
    private readonly CancellationTokenSource _ending = new();

    /// <summary>The frames waiting to be placed, made once the picture's size is known.</summary>
    private FrameQueue _queue = null!;

    /// <summary>The camera while it is open; null while it is let go, not open yet, or lost.</summary>
    private CameraOpening? _open;

    /// <summary>The frames that arrived from the camera in this job, over every open.</summary>
    private long _arrived;

    /// <summary>When the last frame arrived, on the steady clock; 0 before the first.</summary>
    private long _lastArrival;

    /// <summary>Since when the picture has been black, on the steady clock; 0 while it is not.</summary>
    private long _darkSince;

    /// <summary>Runs the job and returns the camera process's exit code: 0 at the stop, 130 at a cancel, 1 when it failed.</summary>
    public async Task<int> RunAsync()
    {
        var name = job.CameraName ?? "the camera";
        if (string.IsNullOrWhiteSpace(job.Folder) || string.IsNullOrWhiteSpace(job.BaseName))
            return await FailAsync("The record job names no recording to write the camera's picture into");

        var size = CameraSize.TryParse(job.Resolution, out var asked) ? asked : CameraSize.Default;
        var pieceStart = TimeSpan.FromMilliseconds(Math.Max(0, Math.Round(job.StartSeconds * 1000, MidpointRounding.AwayFromZero)));
        var offset = TimeSpan.FromMilliseconds(job.OffsetMilliseconds);
        var path = new RecordingFiles(job.Folder, job.BaseName).CameraPiece(pieceStart);
        if (File.Exists(path)) return await FailAsync($"The camera's piece {Path.GetFileName(path)} is there already, and ClassScribe never writes over a piece");

        IPieceWriter writer;
        try
        {
            writer = await MakeWriterAsync(path, size);
        }
        catch (Exception ex)
        {
            // Deliberately not rethrown: a piece that cannot be made (an encoder that refuses even its software settings, a
            // folder that cannot be written) costs the picture only; the starter hears why and records the sound on.
            await SendAsync(CameraProtocol.Problem, new CameraProblem(ex is IOException or UnauthorizedAccessException ? CameraProblemKind.Disk : CameraProblemKind.Encoder,
                $"the piece could not be made ({Words(ex)})"), CameraJson.Default.CameraProblem);
            return await FailAsync($"The camera's piece {Path.GetFileName(path)} could not be made: {Words(ex)}");
        }

        var settings = PieceSettings.For(size);
        _queue = new FrameQueue(settings.FrameBytes, FrameQueue.PoolSize);
        var placer = new FramePlacer(pieceStart, offset);
        await LogAsync("Debug", string.Create(CultureInfo.InvariantCulture,
            $"Recording the camera \"{name}\" shared into {Path.GetFileName(path)}, from {Durations.Format(pieceStart)} on the clock: {settings.Words}, {writer.Encoder}"));
        if (offset != TimeSpan.Zero)
            await LogAsync("Information", string.Create(CultureInfo.InvariantCulture,
                $"The picture is placed {Math.Abs(offset.TotalMilliseconds):0} ms {(offset < TimeSpan.Zero ? "earlier" : "later")} for \"{name}\" (Recording:CameraOffsets)"));

        var control = ReadControlAsync();
        try
        {
            return await RecordAsync(name, size, writer, placer);
        }
        finally
        {
            await _ending.CancelAsync();
            await control;
            writer.Dispose();
        }
    }

    /// <summary>The job's loop, from the first open to the stop, a cancel or a failure.</summary>
    private async Task<int> RecordAsync(string name, CameraSize size, IPieceWriter writer, FramePlacer placer)
    {
        var began = time.GetTimestamp();
        var nextTry = began;
        var openedAt = 0L;
        var arrivedAtOpen = 0L;
        var lastStatus = 0L;
        var writtenAtStatus = 0L;
        var darkSaid = false;
        string? lastProblem = null;
        try
        {
            while (true)
            {
                var now = time.GetTimestamp();
                if (StopAt is not null || _cancelled) break;

                var open = _open;
                if (_wantOpen && open is null && now >= nextTry)
                {
                    var log = new CameraCallLog(time);
                    try
                    {
                        open = await thread.RunAsync(() => CameraRoute.Open(captures, job.CameraId!, name, size, log, OnEvent, onFrame: () => { }, onPixels: OnFrame), callLimit);
                        _open = open;
                        openedAt = time.GetTimestamp();
                        arrivedAtOpen = Interlocked.Read(ref _arrived);
                        lastProblem = null;
                        await SendLinesAsync(log);
                        await SendAsync(CameraProtocol.Opened, new CameraOpened(name, size.Width, size.Height, PieceSettings.FramesPerSecond, writer.Encoder), CameraJson.Default.CameraOpened);
                    }
                    catch (CameraCallFailedException failure)
                    {
                        // Deliberately not rethrown: a camera that cannot be opened now (unplugged, refused, held alone by another
                        // app) may be opened later; the cause goes to the starter once, the calls at Debug, and the open is
                        // tried again every RetryEvery while the piece goes on.
                        await SendLinesAsync(log);
                        var problem = CameraProblems.ForFailedOpen(failure);
                        if (problem.Words != lastProblem) await SendAsync(CameraProtocol.Problem, problem, CameraJson.Default.CameraProblem);
                        lastProblem = problem.Words;
                        nextTry = time.GetTimestamp() + Ticks(RetryEvery);
                    }
                }
                else if (!_wantOpen && open is not null)
                {
                    await CloseAsync(open);
                    _open = open = null;
                    await LogAsync("Information", $"Let go of the camera \"{name}\" for the pause");
                    nextTry = time.GetTimestamp();
                }

                // The arrival watch: no first frame after an open, or frames that stopped.
                now = time.GetTimestamp();
                if (open is not null)
                {
                    var thisOpen = Interlocked.Read(ref _arrived) - arrivedAtOpen;
                    var since = time.GetElapsedTime(Math.Max(Interlocked.Read(ref _lastArrival), openedAt), now);
                    if (thisOpen == 0 && since >= FirstFrameLimit || thisOpen > 0 && since >= StoppedAfter)
                    {
                        // The black frame goes right after the last frame written, where the picture stopped: placed by no stamp
                        // of now, it cannot land beyond a sleep the clock has yet to cut, which would hold back the frames after.
                        PlaceDue(writer, placer, all: true);
                        var lost = thisOpen > 0;
                        if (lost && placer.PlaceBlack(placer.Last + FrameTime) is { Written: true } black) writer.WriteBlack(black.At);
                        await CloseAsync(open);
                        _open = open = null;
                        var problem = lost ? new CameraProblem(CameraProblemKind.Lost, CameraProblems.Stopped) : new CameraProblem(CameraProblemKind.NoPicture, NoPictureWords);
                        lastProblem = problem.Words;
                        await SendAsync(CameraProtocol.Problem, problem, CameraJson.Default.CameraProblem);
                        nextTry = now + Ticks(RetryEvery);
                    }
                }

                PlaceDue(writer, placer, all: false);

                // The picture black for DarkAfter: said once a stretch; the status carries it while it lasts.
                var darkSince = Interlocked.Read(ref _darkSince);
                var dark = open is not null && darkSince != 0 && time.GetElapsedTime(darkSince, now) >= DarkAfter;
                if (dark && !darkSaid) await SendAsync(CameraProtocol.Problem, new CameraProblem(CameraProblemKind.Dark, DarkWords), CameraJson.Default.CameraProblem);
                darkSaid = dark;

                if (lastStatus == 0 || time.GetElapsedTime(lastStatus, now) >= StatusEvery)
                {
                    var elapsed = lastStatus == 0 ? time.GetElapsedTime(began, now) : time.GetElapsedTime(lastStatus, now);
                    var rate = elapsed > TimeSpan.Zero ? (placer.Written - writtenAtStatus) / elapsed.TotalSeconds : 0;
                    lastStatus = now;
                    writtenAtStatus = placer.Written;
                    var last = Interlocked.Read(ref _lastArrival);
                    var sinceFrame = time.GetElapsedTime(last == 0 ? began : last, now);
                    var capturing = open is not null && last >= openedAt && sinceFrame < StoppedAfter;
                    await SendAsync(CameraProtocol.Status, new CameraStatus(placer.Written, placer.Dropped + _queue.Dropped, Math.Round(sinceFrame.TotalSeconds, 2), dark,
                        Math.Round(rate, 1), capturing), CameraJson.Default.CameraStatus);
                }

                await DelayAsync(LookEvery);
            }

            // The stop, or a cancel: the camera let go first, so that no frame comes after the end, then the waiting frames.
            if (_open is { } stillOpen)
            {
                await CloseAsync(stillOpen);
                _open = null;
            }

            var end = StopAt;
            PlaceDue(writer, placer, all: true, end);
            var endPosition = end is { } stop && stop > placer.Last ? stop : placer.Last + FrameTime;
            writer.Finish(endPosition - placer.PieceStart);
            await SendAsync(CameraProtocol.Piece, new CameraPiece(writer.Path, placer.PieceStart.TotalSeconds, endPosition.TotalSeconds), CameraJson.Default.CameraPiece);
            await LogAsync("Debug", string.Create(CultureInfo.InvariantCulture,
                $"The camera's piece {Path.GetFileName(writer.Path)} is finished: {Durations.Format(endPosition - placer.PieceStart)}, {placer.Written} frames written, {placer.Dropped + _queue.Dropped} dropped"));
            return end is not null ? CameraProtocol.DoneExitCode : CameraProtocol.CancelledExitCode;
        }
        catch (TimeoutException)
        {
            // Deliberately not rethrown: the camera's thread is stuck inside Windows, so the job cannot go on; what is written is
            // finished as far as it goes, the starter hears why and starts a new piece, and the job ends with 1.
            TryFinish(writer, placer);
            return await FailAsync($"A call on the camera's route did not return within {callLimit.TotalSeconds:0} seconds");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Deliberately not rethrown: the encoder or the disk failed while the picture was written. The camera is let go, the
            // piece finished as far as it goes (a fragmented piece is readable to its last fragment), and the starter hears the
            // problem and why, and starts a new piece; the sound never depended on this process.
            if (_open is { } stillOpen && !thread.Abandoned)
            {
                await CloseQuietlyAsync(stillOpen);
                _open = null;
            }

            TryFinish(writer, placer);
            await SendAsync(CameraProtocol.Problem, new CameraProblem(ex is IOException or UnauthorizedAccessException ? CameraProblemKind.Disk : CameraProblemKind.Encoder,
                $"writing its picture failed ({Words(ex)})"), CameraJson.Default.CameraProblem);
            return await FailAsync($"Writing the camera's piece {Path.GetFileName(writer.Path)} failed: {Words(ex)}");
        }
    }

    /// <summary>
    /// The piece's writer: with the hardware encoder unless the job asks for the software one, then Windows' software encoder
    /// once when the hardware one refuses, with a line saying so. The file a refused writer made is deleted first.
    /// </summary>
    private async Task<IPieceWriter> MakeWriterAsync(string path, CameraSize size)
    {
        if (job.SoftwareEncoder) return writers.Create(path, PieceSettings.For(size, software: true));

        try
        {
            return writers.Create(path, PieceSettings.For(size));
        }
        catch (Exception ex) when (ex is not IOException and not UnauthorizedAccessException)
        {
            // Deliberately not rethrown: the hardware encoder refused its settings or its start (one report has Intel's fail
            // with E_FAIL); Windows' software encoder takes its place once, with the same settings, and a line says so.
            await LogAsync("Warning", $"The hardware encoder could not start ({Words(ex)}); the camera's picture is encoded by Windows' software encoder instead");
            TryDelete(path);
            return writers.Create(path, PieceSettings.For(size, software: true));
        }
    }

    /// <summary>
    /// Places the waiting frames that are due, or all of them, by the latest clock line, and writes those placed; a frame
    /// without a stamp is placed by its arrival. A frame placed after <paramref name="end"/> is dropped.
    /// </summary>
    private void PlaceDue(IPieceWriter writer, FramePlacer placer, bool all, TimeSpan? end = null)
    {
        var now = MediaTime.Now(time);
        var clock = Snapshot();
        while (_queue.TryTakeDue(now, all, out var frame))
        {
            try
            {
                var placement = placer.Place(clock, frame.Stamp ?? frame.Arrival, end);
                if (placement.Written) writer.Write(frame.Pixels, placement.At);
            }
            finally
            {
                _queue.Return(frame.Pixels);
            }
        }
    }

    /// <summary>A frame from the camera, with its pixels: queued with when it arrived, its brightness followed. Runs on Windows' frame thread, so it never throws.</summary>
    private void OnFrame(ICameraFrame frame)
    {
        if (StopAt is not null || _cancelled) return;

        var arrival = MediaTime.Now(time);
        Interlocked.Increment(ref _arrived);
        Interlocked.Exchange(ref _lastArrival, time.GetTimestamp());
        if (_queue.Offer(frame, arrival) is not { } brightness) return;

        if (brightness < CameraPicture.DarkBelow) Interlocked.CompareExchange(ref _darkSince, time.GetTimestamp(), 0);
        else Interlocked.Exchange(ref _darkSince, 0);
    }

    /// <summary>What MediaCapture reported, from Windows' threads, sent on as a log line.</summary>
    private void OnEvent(string text) => _ = LogAsync("Information", text);

    /// <summary>The latest clock line.</summary>
    private ClockState Snapshot()
    {
        lock (_gate) return _clock;
    }

    /// <summary>Lets the camera go on its thread, the calls sent at Debug; the darkness forgotten with it.</summary>
    private async Task CloseAsync(CameraOpening open)
    {
        var log = new CameraCallLog(time);
        try
        {
            await thread.RunAsync(() => CameraRoute.Close(open.Capture, log), callLimit);
        }
        finally
        {
            Interlocked.Exchange(ref _darkSince, 0);
            await SendLinesAsync(log);
        }
    }

    /// <summary>Lets the camera go after a failure, which is said already: a letting go that hangs is not waited for again.</summary>
    private async Task CloseQuietlyAsync(CameraOpening open)
    {
        try
        {
            await CloseAsync(open);
        }
        catch (TimeoutException)
        {
            // Deliberately not rethrown: the job is failing for another reason, which the starter hears; the process ends next,
            // and Windows lets the camera go with it.
        }
    }

    /// <summary>Finishes the piece as far as it goes after a failure: the last frame lasting a frame's time; a finish that fails too leaves the piece readable to its last fragment.</summary>
    private void TryFinish(IPieceWriter writer, FramePlacer placer)
    {
        try
        {
            writer.Finish(placer.Last - placer.PieceStart + FrameTime);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Deliberately not rethrown: the job is failing already, for a reason the starter hears; a fragmented piece stays
            // readable up to its last complete fragment without a finish.
            _ = LogAsync("Debug", $"The camera's piece could not be finished after the failure: {Words(ex)}");
        }
    }

    /// <summary>
    /// Reads the starter's lines until its stop or cancel, or its end closing: a clock line replaces the clock if it is newer,
    /// off and on let the camera go and take it again. A line this build does not know is passed over; a clock line that cannot
    /// be read too, with a log line, since the next one mends it.
    /// </summary>
    private async Task ReadControlAsync()
    {
        try
        {
            while (await channel.ReceiveAsync(_ending.Token) is { } line)
            {
                switch (line.Type)
                {
                    case CameraProtocol.Clock:
                        TakeClock(line);
                        break;
                    case CameraProtocol.Off:
                        _wantOpen = false;
                        break;
                    case CameraProtocol.On:
                        _wantOpen = true;
                        break;
                    case CameraProtocol.Stop:
                        Volatile.Write(ref _stopAtTicks, TimeSpan.FromSeconds(Math.Max(0, ReadStop(line)?.EndSeconds ?? 0)).Ticks);
                        await _ending.CancelAsync();
                        return;
                    case CameraProtocol.Cancel:
                        _cancelled = true;
                        await _ending.CancelAsync();
                        return;
                }
            }

            // The starter's end closed without a stop: it is gone, so the piece is finished as far as it goes.
            _cancelled = true;
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or InvalidDataException or ObjectDisposedException)
        {
            // Deliberately not rethrown: the job is ending, the starter is gone, or a line could not be read; each finishes the
            // piece as far as it goes.
            if (StopAt is null) _cancelled = true;
        }

        await _ending.CancelAsync();
    }

    /// <summary>Takes a clock line's state when it is newer than the one held.</summary>
    private void TakeClock(Envelope line)
    {
        ClockState? state;
        try
        {
            state = line.Body.ValueKind == JsonValueKind.Object ? line.Body.Deserialize(CameraJson.Default.ClockState) : null;
        }
        catch (JsonException ex)
        {
            // Deliberately not rethrown: a clock line that cannot be read is passed over; the starter sends the clock again
            // whenever it changes, and the next line mends it.
            _ = LogAsync("Debug", $"A clock line could not be read: {ex.Message}");
            return;
        }

        if (state is null) return;
        lock (_gate)
        {
            if (state.Version >= _clock.Version) _clock = state;
        }
    }

    /// <summary>The stop's body; null when it has none that can be read, which stops at the last frame.</summary>
    private static CameraStop? ReadStop(Envelope line)
    {
        try
        {
            return line.Body.ValueKind == JsonValueKind.Object ? line.Body.Deserialize(CameraJson.Default.CameraStop) : null;
        }
        catch (JsonException)
        {
            // Deliberately not rethrown: a stop is a stop whatever its body says; without an end, the last frame ends the piece.
            return null;
        }
    }

    /// <summary>Waits <paramref name="delay"/> on the job's clock, or until the job is asked to end.</summary>
    private async Task DelayAsync(TimeSpan delay)
    {
        try
        {
            await Task.Delay(delay, time, _ending.Token);
        }
        catch (OperationCanceledException)
        {
            // Deliberately not rethrown: the end of the wait is the stop or the cancel, which the loop sees.
        }
    }

    /// <summary>Sends each call of <paramref name="log"/> as a log line at Debug.</summary>
    private async Task SendLinesAsync(CameraCallLog log)
    {
        foreach (var call in log.Calls) await LogAsync("Debug", $"Camera call {call.Text}");
    }

    /// <summary>Sends a log line.</summary>
    private Task LogAsync(string level, string text) => SendAsync(CameraProtocol.Log, new CameraLog(level, text), CameraJson.Default.CameraLog);

    /// <summary>Sends failed with <paramref name="why"/>, and returns the failed exit code.</summary>
    private async Task<int> FailAsync(string why)
    {
        await SendAsync(CameraProtocol.Failed, new CameraFailed(why), CameraJson.Default.CameraFailed);
        return CameraProtocol.FailedExitCode;
    }

    /// <summary>Sends a line to the starter; a starter that is gone ends the job as a cancel does, and nothing more is sent.</summary>
    private async Task SendAsync<T>(string type, T body, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info)
    {
        if (_starterGone) return;
        try
        {
            await channel.SendAsync(type, body, info, CancellationToken.None);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // Deliberately not rethrown: the starter is gone, so nobody is left to tell; the piece is finished as far as it
            // goes and the job ends.
            _starterGone = true;
            if (StopAt is null) _cancelled = true;
            await _ending.CancelAsync();
        }
    }

    /// <summary>An exception in a few words: its own words' first line, with Windows' HRESULT when it has one.</summary>
    private static string Words(Exception ex) => CameraCallLog.Describe(ex).Error;

    /// <summary>Deletes <paramref name="path"/> if it is there; one that cannot be deleted is left, and the next writer's start says so.</summary>
    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Deliberately not rethrown: the software encoder's start then fails on the file itself, with the real reason.
        }
    }

    /// <summary><paramref name="span"/> in the clock's ticks.</summary>
    private long Ticks(TimeSpan span) => (long)(span.TotalSeconds * time.TimestampFrequency);
}
