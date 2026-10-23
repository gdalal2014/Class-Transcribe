using System.Globalization;
using System.Text.Json;
using ClassScribe.Audio.Timeline;
using ClassScribe.Commands;
using Microsoft.Extensions.Logging;

namespace ClassScribe.Camera;

/// <summary>
/// A lesson's camera track in the recorder (teach-video 6.2, 6.6 to 6.8, 6.10 to 6.13): it pins the run's camera by its id,
/// starts a camera process with a record job beside the sound, and talks to it. From its own loop, which looks at least every
/// <see cref="LookEvery"/> and at once when nudged, it sends the clock's whole state (<see cref="ClockState"/>) whenever it
/// has changed, and off and on for P after the clock's line, so that the pause reaches the camera process before the frames
/// it leaves out are placed. As the run stops, <see cref="EndAt"/> sends the sound's end at once, before the sound is stopped,
/// so that no frame after it is written; the stop then sends the clock's last line and the end position, and waits for the
/// piece at most <see cref="StopWait"/>, then ends the process. A line the process does not take within
/// <see cref="SendLimit"/> ends it, as stuck, and letting a process go waits at most <see cref="DisposeWait"/>, so that
/// nothing of the camera's waits without end. A camera process that ends by itself while recording gets a new one
/// <see cref="RestartAfter"/> later, writing a new piece from the position then; the software encoder is asked for after an
/// encoder failure; the third such end within <see cref="FailuresWithin"/> gives the picture up for the rest of the run. A
/// camera not connected at the start is looked for every <see cref="FindEvery"/>; there is never a stand-in camera. What the
/// camera process says becomes the track's <see cref="State"/>, with what is wrong in words for the run's title and its
/// checks. The sound never waits for any of it: a camera that crashes or hangs costs the picture only.
/// </summary>
/// <remarks>
/// Lock order (teach-video 6.6): the track takes only the clock's lock, briefly, inside <see cref="RecordingClock.Snapshot"/>,
/// and its own for its state; it never takes a session's gate. <see cref="Nudge"/>, <see cref="Pause"/> and
/// <see cref="Resume"/> take no lock at all: they set a flag and complete a task the loop waits on, whose continuation runs
/// elsewhere.
/// </remarks>
public sealed class CameraTrack : ICameraTrack
{
    /// <summary>How often the loop looks at the clock when nothing nudges it.</summary>
    public static readonly TimeSpan LookEvery = TimeSpan.FromMilliseconds(250);

    /// <summary>How long after a camera process ended by itself a new one is started.</summary>
    public static readonly TimeSpan RestartAfter = TimeSpan.FromSeconds(2);

    /// <summary>How often a camera not connected is looked for.</summary>
    public static readonly TimeSpan FindEvery = TimeSpan.FromSeconds(5);

    /// <summary>The time within which <see cref="MostFailures"/> ends of the camera process give the picture up.</summary>
    public static readonly TimeSpan FailuresWithin = TimeSpan.FromMinutes(10);

    /// <summary>How many times the camera process may stop working within <see cref="FailuresWithin"/>; at that many the picture is given up.</summary>
    public const int MostFailures = 3;

    /// <summary>How long the stop waits for the piece before the camera process is ended.</summary>
    public static readonly TimeSpan StopWait = TimeSpan.FromSeconds(15);

    /// <summary>How long a line to the camera process may take to go out before the process counts as stuck and is ended.</summary>
    public static readonly TimeSpan SendLimit = TimeSpan.FromSeconds(5);

    /// <summary>How long letting a camera process go may take before the track goes on without it.</summary>
    public static readonly TimeSpan DisposeWait = TimeSpan.FromSeconds(5);

    /// <summary>The title's words while the picture is given up.</summary>
    public const string GivenUpShort = "NO CAMERA";

    /// <summary>The words when the camera stopped working three times within ten minutes.</summary>
    public const string GivenUpText = "The camera stopped working three times; the rest of this lesson is recorded without its picture. The sound records on";

    /// <summary>Where the lesson's picture begins, and with which camera.</summary>
    private readonly CameraTrackStart _start;

    /// <summary>The run's clock, whose state goes to the camera process.</summary>
    private readonly RecordingClock _clock;

    /// <summary>Lists the cameras, to pin the run's camera.</summary>
    private readonly ICameraProvider _cameras;

    /// <summary>Starts the camera processes.</summary>
    private readonly ICameraProcessStarter _starter;

    /// <summary>The clock the track is timed on.</summary>
    private readonly TimeProvider _time;

    /// <summary>The log, under the category Camera.</summary>
    private readonly ILogger _logger;

    /// <summary>Guards the track's state, which the loop, the reading of the lines and the run's checks share.</summary>
    private readonly Lock _gate = new();

    /// <summary>Ends the loop's waits at the stop.</summary>
    private readonly CancellationTokenSource _ending = new();

    /// <summary>The ends of camera processes within <see cref="FailuresWithin"/>, on the steady clock.</summary>
    private readonly Queue<long> _failures = new();

    /// <summary>Wakes the loop: completed by a nudge, replaced by the loop once it has woken.</summary>
    private TaskCompletionSource _nudge = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>The track's loop, from <see cref="Begin"/> to the stop.</summary>
    private Task _loop = Task.CompletedTask;

    /// <summary>The camera, pinned by its id once found; null before.</summary>
    private CameraDevice? _camera;

    /// <summary>The camera's offset in milliseconds, resolved when it is found.</summary>
    private int _offset;

    /// <summary>The camera process now; null before the first, between a process's end and the next, and after the stop.</summary>
    private ICameraProcess? _process;

    /// <summary>The reading of the camera process's lines.</summary>
    private Task? _reading;

    /// <summary>The clock's version the camera process was sent last; -1 before any.</summary>
    private long _sentVersion = -1;

    /// <summary>Whether the camera process was told to let the camera go (off) last; null before any word.</summary>
    private bool? _sentOff;

    /// <summary>The sound's end <see cref="EndAt"/> gave, in ticks; -1 before it.</summary>
    private long _endAtTicks = -1;

    /// <summary>The sound's end the camera process now was sent, in ticks; -1 before it was sent one.</summary>
    private long _sentEndTicks = -1;

    /// <summary>True while P holds: the camera is to be let go.</summary>
    private volatile bool _wantOff;

    /// <summary>True once the stop began: nothing more is started.</summary>
    private volatile bool _stopping;

    /// <summary>The stop, once it began; null before. A later stop and the disposal wait for it.</summary>
    private Task? _stopped;

    /// <summary>True once the picture is given up for the rest of the run.</summary>
    private bool _givenUp;

    /// <summary>True until the first camera process starts, whose piece begins at the lesson's start.</summary>
    private bool _first = true;

    /// <summary>True once the encoder failed: the next camera process asks for Windows' software encoder.</summary>
    private bool _software;

    /// <summary>True once the start line was said.</summary>
    private bool _openedSaid;

    /// <summary>When the next camera process may start, on the steady clock.</summary>
    private long _nextStart;

    /// <summary>The kind of the last problem said, so that a problem is said once while it lasts.</summary>
    private CameraProblemKind? _lastProblem;

    /// <summary>True while the camera is open and its frames arrive, as the last status said.</summary>
    private bool _capturing;

    /// <summary>True while the picture is dark, as the last status said.</summary>
    private bool _dark;

    /// <summary>The frames written a second, as the last status said.</summary>
    private double _framesPerSecond;

    /// <summary>The frames written by the camera process now.</summary>
    private long _frames;

    /// <summary>The frames dropped by the camera process now.</summary>
    private long _dropped;

    /// <summary>The frames written by the earlier camera processes of this run.</summary>
    private long _framesBefore;

    /// <summary>The frames dropped by the earlier camera processes of this run.</summary>
    private long _droppedBefore;

    /// <summary>What is wrong now, while it lasts.</summary>
    private CameraConcern? _concern;

    /// <summary>A track for the lesson <paramref name="start"/> describes, placed on <paramref name="clock"/>; nothing runs before <see cref="Begin"/>.</summary>
    public CameraTrack(CameraTrackStart start, RecordingClock clock, ICameraProvider cameras, ICameraProcessStarter starter, TimeProvider time, ILogger logger)
    {
        _start = start;
        _clock = clock;
        _cameras = cameras;
        _starter = starter;
        _time = time;
        _logger = logger;
        _nextStart = time.GetTimestamp();
    }

    /// <inheritdoc />
    public CameraTrackState State
    {
        get
        {
            lock (_gate)
            {
                return new CameraTrackState(_camera?.Name ?? _start.Camera.NamePart ?? "the camera", _start.Camera.Resolution, _capturing, _dark, _framesPerSecond,
                    _framesBefore + _frames, _droppedBefore + _dropped, _concern, _camera?.ContainerId, _givenUp);
            }
        }
    }

    /// <summary>Starts the loop, which finds the camera and starts its first camera process at once.</summary>
    public void Begin() => _loop = Task.Run(LoopAsync);

    /// <inheritdoc />
    public void Pause()
    {
        _wantOff = true;
        Nudge();
    }

    /// <inheritdoc />
    public void Resume()
    {
        _wantOff = false;
        Nudge();
    }

    /// <inheritdoc />
    public void Nudge() => Volatile.Read(ref _nudge).TrySetResult();

    /// <inheritdoc />
    public void EndAt(TimeSpan endPosition)
    {
        Volatile.Write(ref _endAtTicks, endPosition.Ticks);
        Nudge();
    }

    /// <inheritdoc />
    public void GiveUp(string why)
    {
        ICameraProcess? process;
        lock (_gate)
        {
            if (_givenUp || _stopping) return;
            _givenUp = true;
            _capturing = false;
            _concern = new CameraConcern(true, GivenUpShort, why);
            process = _process;
        }

        _logger.LogWarning("{Text}", why);
        if (process is not null) _ = Task.Run(() => LetGoAsync(process, _clock.Now(), CancellationToken.None));
    }

    /// <inheritdoc />
    /// <remarks>The first call stops the track; a later one, the disposal's among them, waits for that same stop.</remarks>
    public Task StopAsync(TimeSpan endPosition, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_stopped is not null) return _stopped;
            _stopping = true;
            return _stopped = Task.Run(() => StopOnceAsync(endPosition, cancellationToken), CancellationToken.None);
        }
    }

    /// <summary>The stop itself, run once: the loop ended, then the process now let go at <paramref name="endPosition"/>.</summary>
    private async Task StopOnceAsync(TimeSpan endPosition, CancellationToken cancellationToken)
    {
        // The loop ends first, a start under way with it, so that the process read next is the last one started.
        await _ending.CancelAsync();
        await _loop;
        ICameraProcess? process;
        lock (_gate) process = _givenUp ? null : _process;
        if (process is null)
        {
            _logger.LogDebug("No camera process is recording the picture at the stop");
            return;
        }

        await LetGoAsync(process, endPosition, cancellationToken);
    }

    /// <summary>Stops the picture at the clock's position now, if the stop has not come, or waits for the stop under way; then lets the loop's signal go.</summary>
    public async ValueTask DisposeAsync()
    {
        await StopAsync(_clock.Now(), CancellationToken.None);
        _ending.Dispose();
    }

    /// <summary>
    /// Ends <paramref name="process"/>'s piece at <paramref name="end"/>: the clock's last line, then the stop, each within
    /// <see cref="SendLimit"/>; waits for the piece, at most <see cref="StopWait"/>, then ends the process if it is still
    /// there, and lets it go, at most <see cref="DisposeWait"/>.
    /// </summary>
    private async Task LetGoAsync(ICameraProcess process, TimeSpan end, CancellationToken cancellationToken)
    {
        Task? reading;
        lock (_gate) reading = _reading;
        try
        {
            await SendWithinAsync(process, () => CameraLines.SendClockAsync(process.Channel, _clock.Snapshot(), CancellationToken.None));
            await SendWithinAsync(process, () => CameraLines.SendStopAsync(process.Channel, new CameraStop(end.TotalSeconds), CancellationToken.None));
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or ArgumentException)
        {
            // Deliberately not rethrown: the camera process is gone already, or its lines are closed; its piece is what it
            // wrote, readable to its last fragment, and the process is let go below.
            _logger.LogDebug(ex, "The camera process had gone before its stop");
        }

        if (reading is not null)
        {
            using var waiting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var waited = await Task.WhenAny(reading, Task.Delay(StopWait, _time, waiting.Token)).ConfigureAwait(false);
            await waiting.CancelAsync();
            if (waited != reading)
            {
                _logger.LogWarning("The camera process did not finish its piece within {Seconds} seconds of the stop, so it was ended; its piece is readable to its last second or so",
                    StopWait.TotalSeconds);
                process.Kill();
            }
        }

        await LetGoOfAsync(process);
        lock (_gate)
        {
            if (ReferenceEquals(_process, process)) _process = null;
            _capturing = false;
        }
    }

    /// <summary>
    /// Sends a line to <paramref name="process"/> through <paramref name="send"/>, waiting at most <see cref="SendLimit"/>. A
    /// line to a process that stopped reading its input never goes out, and the write on its pipe cannot be cancelled: the
    /// process is ended, which breaks the pipe and so ends the write, and an <see cref="IOException"/> says so, as for a
    /// process that is gone.
    /// </summary>
    private async Task SendWithinAsync(ICameraProcess process, Func<ValueTask> send)
    {
        var sending = send().AsTask();
        try
        {
            await sending.WaitAsync(SendLimit, _time);
        }
        catch (TimeoutException)
        {
            // Deliberately not rethrown as is: the camera process is stuck, so it is ended, and the timeout is handed on as what
            // it now means, the IOException of a process that is gone, which every caller already answers (a new process, or
            // the stop going on), so that the stop, the sound's transcript and the next class never wait on a line.
            _logger.LogWarning("The camera process did not take a line within {Seconds} seconds, so it was ended", SendLimit.TotalSeconds);
            process.Kill();
            LogWhenFaulted(sending, "The line the camera process did not take failed once it was ended");
            throw new IOException(string.Create(CultureInfo.InvariantCulture, $"The camera process did not take a line within {SendLimit.TotalSeconds:0} seconds, so it was ended"));
        }
    }

    /// <summary>
    /// Lets <paramref name="process"/> go, waiting at most <see cref="DisposeWait"/>: a process that cannot be ended, whose
    /// error output so never closes, is left to end with ClassScribe, whose job ends it at the latest.
    /// </summary>
    private async Task LetGoOfAsync(ICameraProcess process)
    {
        var disposing = process.DisposeAsync().AsTask();
        try
        {
            await disposing.WaitAsync(DisposeWait, _time);
        }
        catch (TimeoutException)
        {
            // Deliberately not rethrown: the process was stopped or ended already, and its job ends it with ClassScribe at the
            // latest; waiting longer would hold up the stop, and with it the sound's transcript and the next class.
            _logger.LogWarning("The camera process could not be let go within {Seconds} seconds; it ends with ClassScribe at the latest", DisposeWait.TotalSeconds);
            LogWhenFaulted(disposing, "Letting the camera process go failed in the end");
        }
    }

    /// <summary>Logs <paramref name="task"/>'s failure at Debug, with <paramref name="words"/>, should it fail after nobody waits for it any more.</summary>
    private void LogWhenFaulted(Task task, string words) =>
        _ = task.ContinueWith(failed => _logger.LogDebug(failed.Exception, "{Words}", words), CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    /// <summary>
    /// The track's loop until the stop: a look at once and whenever nudged, and every <see cref="LookEvery"/> otherwise. It
    /// never throws: a look that fails is logged, and the next look tries again.
    /// </summary>
    private async Task LoopAsync()
    {
        while (!_stopping)
        {
            try
            {
                await LookAsync();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Deliberately not rethrown: this loop serves the picture alone, and a look that fails must cost neither the
                // sound nor the next look; it is logged, and the next look tries again.
                _logger.LogWarning(ex, "The camera's track could not look at its camera process; it tries again");
            }

            if (_stopping) break;

            var nudge = Volatile.Read(ref _nudge);
            try
            {
                await Task.WhenAny(nudge.Task, Task.Delay(LookEvery, _time, _ending.Token));
            }
            catch (OperationCanceledException)
            {
                // Deliberately not rethrown: the stop ends the wait, which the loop's condition sees.
            }

            if (nudge.Task.IsCompleted) Interlocked.CompareExchange(ref _nudge, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously), nudge);
        }
    }

    /// <summary>
    /// One look: a camera process when none runs and its time has come (the camera found first), else the clock's line when
    /// the clock has changed, off or on when P has changed, and the sound's end once it is given; each line within
    /// <see cref="SendLimit"/>.
    /// </summary>
    private async Task LookAsync()
    {
        ICameraProcess? process;
        lock (_gate)
        {
            if (_givenUp || _stopping) return;
            process = _process;
        }

        if (process is null)
        {
            if (_time.GetTimestamp() >= _nextStart) await StartProcessAsync();
            return;
        }

        var snapshot = _clock.Snapshot();
        var off = _wantOff;
        var end = Volatile.Read(ref _endAtTicks);
        try
        {
            if (snapshot.Version != _sentVersion)
            {
                await SendWithinAsync(process, () => CameraLines.SendClockAsync(process.Channel, snapshot, CancellationToken.None));
                _sentVersion = snapshot.Version;
            }

            if (_sentOff != off)
            {
                await SendWithinAsync(process, () => CameraLines.SendSignalAsync(process.Channel, off ? CameraProtocol.Off : CameraProtocol.On, CancellationToken.None));
                _sentOff = off;
            }

            if (end >= 0 && _sentEndTicks != end)
            {
                await SendWithinAsync(process, () => CameraLines.SendEndAsync(process.Channel, new CameraStop(TimeSpan.FromTicks(end).TotalSeconds), CancellationToken.None));
                _sentEndTicks = end;
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // Deliberately not rethrown: the camera process has ended; its reading sees the end and starts another.
            _logger.LogDebug(ex, "The camera process's lines closed");
        }
    }

    /// <summary>
    /// Finds the camera when it is not pinned yet, then starts a camera process with a record job, its piece beginning at the
    /// lesson's start for the first and at the clock's position now for the others, and sends it the clock and P at once.
    /// A camera not found is said once and looked for again every <see cref="FindEvery"/>; a process that does not start is a
    /// failure, as an end of one is.
    /// </summary>
    private async Task StartProcessAsync()
    {
        if (_camera is null && !await FindAsync()) return;

        var camera = _camera!;
        var position = _first ? _start.StartPosition : _clock.Now();
        var job = new CameraJob(CameraProtocol.Version, CameraJobKind.Record, camera.Id, camera.Name, _start.Camera.Resolution, _offset, _start.Folder, _start.BaseName,
            position.TotalSeconds, SoftwareEncoder: _software);
        ICameraProcess process;
        try
        {
            process = _starter.Start(RunIds.New(_time.GetUtcNow()));
        }
        catch (Exception ex)
        {
            // Deliberately not rethrown: the sound records on whatever kept the camera process from starting (Windows'
            // refusal as a Win32Exception, or anything else); it counts as the camera stopping working, and is tried again.
            Failed($"Windows did not start ClassScribe's camera process ({ex.Message.TrimEnd('.')})");
            return;
        }

        var snapshot = _clock.Snapshot();
        var off = _wantOff;
        try
        {
            await SendWithinAsync(process, () => CameraLines.SendJobAsync(process.Channel, job, CancellationToken.None));
            await SendWithinAsync(process, () => CameraLines.SendClockAsync(process.Channel, snapshot, CancellationToken.None));
            if (off) await SendWithinAsync(process, () => CameraLines.SendSignalAsync(process.Channel, CameraProtocol.Off, CancellationToken.None));
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // Deliberately not rethrown: the process ended before it took its job, or would not take it; it is let go, and
            // counts as the camera stopping working.
            process.Kill();
            await LetGoOfAsync(process);
            Failed($"ClassScribe's camera process ended before it took its job ({ex.Message.TrimEnd('.')})");
            return;
        }

        _sentVersion = snapshot.Version;
        _sentOff = off;
        _sentEndTicks = -1;
        lock (_gate)
        {
            _first = false;
            _process = process;
            _framesBefore += _frames;
            _droppedBefore += _dropped;
            _frames = _dropped = 0;
            _reading = Task.Run(() => ReadAsync(process, camera));
        }

        _logger.LogDebug("Started a camera process for the picture of \"{Camera}\": process {ProcessId}, its piece from {Position}", camera.Name, process.ProcessId,
            Durations.Format(position));
    }

    /// <summary>The run's camera, pinned by its id: found and its offset resolved, or said once as not connected.</summary>
    private async Task<bool> FindAsync()
    {
        CameraList list;
        try
        {
            list = await _cameras.GetCamerasAsync(CancellationToken.None).WaitAsync(FindEvery, _time);
        }
        catch (TimeoutException)
        {
            // Deliberately not rethrown: a listing that hangs inside Windows is a list that could not be read now; the camera is
            // looked for again after FindEvery.
            list = new CameraList([], string.Create(CultureInfo.InvariantCulture, $"Windows did not list them within {FindEvery.TotalSeconds:0} seconds"));
        }

        var found = list.Problem is null ? Cameras.Choose(list.Cameras, _start.Camera.NamePart) : null;
        if (found is null)
        {
            _nextStart = _time.GetTimestamp() + Ticks(FindEvery);
            var name = _start.Camera.NamePart is { } part ? $"\"{part}\"" : "for this lesson (a USB camera, else the built-in front camera)";
            var text = list.Problem is { } problem
                ? $"The cameras could not be listed ({problem}), so the camera is not recording; ClassScribe looks again every {FindEvery.TotalSeconds:0} seconds. The sound records on"
                : $"The camera {name} is not connected, so its picture is not recording; ClassScribe looks for it every {FindEvery.TotalSeconds:0} seconds. The sound records on";
            Concern(true, "CAMERA NOT CONNECTED", text);
            return false;
        }

        _offset = CameraOffsets.For(found.Name, _start.Offsets);
        lock (_gate)
        {
            _camera = found;
            if (_concern?.Short == "CAMERA NOT CONNECTED") _concern = null;
        }

        return true;
    }

    /// <summary>
    /// Reads the camera process's lines until it ends: its opening, status, problems, piece, failure and log lines. A process
    /// that ends by itself before the stop is a failure of the camera, after which another is started, or the picture given up.
    /// </summary>
    private async Task ReadAsync(ICameraProcess process, CameraDevice camera)
    {
        var failedEncoder = false;
        try
        {
            while (await process.Channel.ReceiveAsync(CancellationToken.None) is { } line)
            {
                switch (CameraLines.Read(line))
                {
                    case CameraOpened opened:
                        OnOpened(camera, opened);
                        break;
                    case CameraStatus status:
                        OnStatus(camera, status);
                        break;
                    case CameraProblem problem:
                        failedEncoder |= problem.Kind is CameraProblemKind.Encoder;
                        OnProblem(camera, problem);
                        break;
                    case CameraPiece piece:
                        _logger.LogInformation("The camera's piece {Piece} is finished, from {Start} to {End} on the clock", Path.GetFileName(piece.Path),
                            Durations.Format(TimeSpan.FromSeconds(piece.StartSeconds)), Durations.Format(TimeSpan.FromSeconds(piece.EndSeconds)));
                        break;
                    case CameraFailed failed:
                        _logger.LogWarning("The camera process recording \"{Camera}\" failed: {Why}", camera.Name, failed.Why);
                        break;
                    case CameraLog log:
                        _logger.Log(LevelOf(log.Level), "Camera process: {Text}", log.Text);
                        break;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException or ObjectDisposedException)
        {
            // Deliberately not rethrown: the process's lines ended or could not be read, which ends the reading as the process's
            // end does; before the stop, another process is started.
            _logger.LogDebug(ex, "The camera process's lines for \"{Camera}\" ended", camera.Name);
        }

        bool ended;
        lock (_gate)
        {
            ended = !_stopping && !_givenUp && ReferenceEquals(_process, process);
            if (ended)
            {
                // No look starts another until Failed below says when (RestartAfter from now) or gives the picture up: the
                // process is let go first, which may take DisposeWait, and a look meanwhile must not start one at once.
                _process = null;
                _nextStart = long.MaxValue;
            }

            _capturing = false;
        }

        if (!ended) return;

        if (failedEncoder) _software = true;
        _logger.LogDebug("The camera process recording \"{Camera}\" ended (exit {Code})", camera.Name,
            process.ExitCode?.ToString(CultureInfo.InvariantCulture) ?? "not known");
        await LetGoOfAsync(process);
        Failed($"ClassScribe's camera process for \"{camera.Name}\" stopped working");
    }

    /// <summary>
    /// The camera stopped working (its process ended, or did not start): counted, and after the <see cref="MostFailures"/>th
    /// within <see cref="FailuresWithin"/> the picture is given up for the rest of the run; otherwise a new camera process is
    /// started <see cref="RestartAfter"/> later.
    /// </summary>
    private void Failed(string why)
    {
        var now = _time.GetTimestamp();
        CameraConcern? restarting = null;
        lock (_gate)
        {
            while (_failures.Count > 0 && _time.GetElapsedTime(_failures.Peek(), now) > FailuresWithin) _failures.Dequeue();
            _failures.Enqueue(now);
            if (_failures.Count < MostFailures)
            {
                _nextStart = now + Ticks(RestartAfter);
                restarting = _concern = new CameraConcern(true, "CAMERA RESTARTING", $"{why}; ClassScribe starts it again in {RestartAfter.TotalSeconds:0} seconds. The sound records on");
            }
        }

        if (restarting is not null) _logger.LogWarning("{Text}", restarting.Text);
        else GiveUp(GivenUpText);
    }

    /// <summary>The camera opened: the start line the first time (teach-video 6.10), a Debug line after.</summary>
    private void OnOpened(CameraDevice camera, CameraOpened opened)
    {
        bool first;
        lock (_gate)
        {
            first = !_openedSaid;
            _openedSaid = true;
        }

        if (!first)
        {
            _logger.LogDebug("The camera \"{Camera}\" is open again", camera.Name);
            return;
        }

        var size = CameraSize.TryParse(string.Create(CultureInfo.InvariantCulture, $"{opened.Width}x{opened.Height}"), out var parsed) ? parsed : CameraSize.Default;
        var place = camera.Place switch
        {
            CameraPlace.BuiltInFront => " (built in, front)",
            CameraPlace.BuiltInBack => " (built in, back)",
            CameraPlace.Usb => " (USB)",
            CameraPlace.Virtual => " (virtual)",
            _ => ""
        };

        // The rate is the camera's own as it runs, shared with other apps, up to the 30 the piece keeps; Windows may not say it.
        var rate = opened.FramesPerSecond > 0
            ? string.Create(CultureInfo.InvariantCulture, $"at {opened.FramesPerSecond:0.##} frames a second")
            : string.Create(CultureInfo.InvariantCulture, $"at up to {PieceSettings.FramesPerSecond} frames a second");
        _logger.LogInformation("{Line}", string.Create(CultureInfo.InvariantCulture,
            $"Camera: \"{camera.Name}\"{place}, {opened.Width}x{opened.Height} {rate}, {PieceSettings.BitrateFor(size) / 1e6:0.#} Mbit/s, {opened.Encoder}. Its light is on while it records; P lets it go during a pause"));
    }

    /// <summary>A status line: the frames, the rate, whether the camera records and whether its picture is dark; a picture back mends what was wrong.</summary>
    private void OnStatus(CameraDevice camera, CameraStatus status)
    {
        string? back = null;
        lock (_gate)
        {
            _capturing = status.Capturing;
            _dark = status.Dark;
            _framesPerSecond = status.FramesPerSecond;
            _frames = status.Frames;
            _dropped = status.Dropped;
            if (status.Capturing && _concern is { } concern && !_givenUp && (concern.Short != DarkShort || !status.Dark))
            {
                if (concern.Alert) back = $"The camera \"{camera.Name}\" is recording its picture again";
                _concern = null;
                _lastProblem = null;
            }
        }

        if (back is not null) _logger.LogInformation("{Line}", back);
    }

    /// <summary>The title's words while the picture is dark.</summary>
    public const string DarkShort = "camera dark";

    /// <summary>A problem the camera process said: in the run's words, with the camera's name, said once per kind while it lasts.</summary>
    private void OnProblem(CameraDevice camera, CameraProblem problem)
    {
        lock (_gate)
        {
            if (_lastProblem == problem.Kind && _concern is not null) return;
            _lastProblem = problem.Kind;
        }

        var name = camera.Name;
        var (alert, shortWords, text) = problem.Kind switch
        {
            CameraProblemKind.Dark => (false, DarkShort, "The camera's picture has been black for 10 seconds: is its lens covered or folded?"),
            CameraProblemKind.Lost => (true, "CAMERA LOST", $"The camera \"{name}\" stopped sending its picture; the sound records on. Plug it back in"),
            CameraProblemKind.NoPicture => (true, "CAMERA GIVES NO PICTURE",
                $"The camera \"{name}\" gave no picture. Windows allows it, so a security program may be blocking ClassScribe (Bitdefender: Privacy > Video & Audio Protection > Webcam Protection). The sound records on"),
            CameraProblemKind.InUse => (true, "CAMERA IN USE",
                $"The camera \"{name}\" is held by another app ({problem.Words}). Close it there; ClassScribe takes the camera as soon as it is free. The sound records on"),
            CameraProblemKind.Denied => (true, "CAMERA REFUSED",
                $"The camera \"{name}\" was refused: {problem.Words}. Check Settings > Privacy & security > Camera, and the security program. The sound records on"),
            CameraProblemKind.Encoder or CameraProblemKind.Disk => (true, "CAMERA NOT WRITTEN",
                $"The camera's picture could not be written ({problem.Words}). The sound records on"),
            CameraProblemKind.Unwritable => (true, "CAMERA NOT WRITTEN",
                $"The camera \"{name}\" sends its picture, but it cannot be written: {problem.Words}. ClassScribe opens it again every 2 seconds. The sound records on"),
            _ => (true, "CAMERA LOST", $"The camera \"{name}\" could not be opened: {problem.Words}. The sound records on")
        };
        Concern(alert, shortWords, text);
    }

    /// <summary>Sets what is wrong now, and logs it: an Alert as a warning, a Notice as information.</summary>
    private void Concern(bool alert, string shortWords, string text)
    {
        lock (_gate)
        {
            if (_givenUp) return;
            if (_concern?.Text == text) return;
            _concern = new CameraConcern(alert, shortWords, text);
        }

        if (alert) _logger.LogWarning("{Text}", text);
        else _logger.LogInformation("{Text}", text);
    }

    /// <summary>A camera process's log level by its name.</summary>
    private static LogLevel LevelOf(string level) => level switch
    {
        "Warning" => LogLevel.Warning,
        "Error" => LogLevel.Error,
        "Information" => LogLevel.Information,
        _ => LogLevel.Debug
    };

    /// <summary><paramref name="span"/> in the clock's ticks.</summary>
    private long Ticks(TimeSpan span) => (long)(span.TotalSeconds * _time.TimestampFrequency);
}
