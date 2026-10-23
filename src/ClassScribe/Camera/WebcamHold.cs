using System.Text.Json;
using ClassScribe.Audio.Microphones;
using ClassScribe.Commands;
using ClassScribe.Link;
using Microsoft.Extensions.Logging;

namespace ClassScribe.Camera;

/// <summary>What the run knows of the webcam whose microphone is picked now, and of its hold (camera-first 3.4).</summary>
/// <param name="CameraName">The webcam's name, as Windows lists it.</param>
/// <param name="Holding">True while ClassScribe holds its picture open (Recording:WakeCameraMicrophone on).</param>
/// <param name="HasFrames">True while the held picture's frames arrive: the camera is awake.</param>
/// <param name="Problem">What is wrong with the hold, in words, while it lasts: the picture stopped, or the camera could not be opened; null otherwise.</param>
/// <param name="ProblemShort">The problem in a few words, for the title; null with no problem.</param>
public sealed record WebcamState(string CameraName, bool Holding, bool HasFrames, string? Problem = null, string? ProblemShort = null);

/// <summary>Makes a recording's <see cref="WebcamHold"/>, with the app's camera list and camera process starter.</summary>
/// <param name="cameras">Lists the cameras, opening none.</param>
/// <param name="starter">Starts camera processes.</param>
/// <param name="time">The clock everything is timed on.</param>
/// <param name="loggers">Makes the log under the category Camera.</param>
public sealed class WebcamHolds(ICameraProvider cameras, ICameraProcessStarter starter, TimeProvider time, ILoggerFactory loggers)
{
    /// <summary>The log's category for the hold's lines.</summary>
    public const string LogCategory = "Camera";

    /// <summary>A hold for one recording: it holds a webcam's picture only when <paramref name="wakeOn"/> (Recording:WakeCameraMicrophone).</summary>
    public WebcamHold Create(bool wakeOn) => new(cameras, starter, wakeOn, time, loggers.CreateLogger(LogCategory));
}

/// <summary>
/// The webcam behind a recording's microphone, and its hold (camera-first 3.4). At each pick of the microphone it finds
/// whether the microphone belongs to a webcam, by the container it shares with a camera that is neither built in nor
/// virtual (listing the cameras opens none, and only a microphone outside the computer's own container needs them). With
/// Recording:WakeCameraMicrophone on, it then holds that webcam's picture open, shared, through a camera process's hold
/// job, from before the microphone's capture opens (<see cref="BeforeOpen"/> waits for the first frame, at most
/// <see cref="FirstFrameWait"/>) until the recording stops, through P and V off; it follows the pick to another webcam, and
/// lets go when the pick is no webcam's. Its lines go into the run's log under Camera: the hold's start, no picture yet, the
/// picture stopped (and back), a camera that cannot be opened (tried again every <see cref="RetryEvery"/>), and the let go.
/// <see cref="State"/> tells the run's checks which webcam it is, whether its frames arrive, and what is wrong. Nothing of
/// the camera's side reaches the microphone's look: a camera list that throws, or that Windows has not answered within
/// <see cref="ListLimit"/>, is a pick not decided yet, said once per stretch and asked again at the next pick, and a camera
/// process that cannot be started, for whatever cause, is a camera that could not be opened.
/// </summary>
public sealed class WebcamHold : IDisposable
{
    /// <summary>The longest the microphone's open waits for the held camera's first frame.</summary>
    public static readonly TimeSpan FirstFrameWait = TimeSpan.FromSeconds(5);

    /// <summary>The longest a pick waits for the cameras to be listed: a list that hangs inside Windows is given up, and asked again at a later pick once it has ended.</summary>
    public static readonly TimeSpan ListLimit = TimeSpan.FromSeconds(5);

    /// <summary>How long without a frame, after frames came, before the picture counts as stopped.</summary>
    public static readonly TimeSpan StoppedAfter = TimeSpan.FromSeconds(3);

    /// <summary>How long after a camera process could not start, or ended by itself, another is started.</summary>
    public static readonly TimeSpan RetryEvery = TimeSpan.FromSeconds(5);

    /// <summary>How long the stop waits for the camera process to let the camera go before it is ended.</summary>
    public static readonly TimeSpan StopWait = TimeSpan.FromSeconds(5);

    /// <summary>How often the hold looks at its frames.</summary>
    public static readonly TimeSpan LookEvery = TimeSpan.FromSeconds(1);

    /// <summary>The words for the title while the picture has stopped.</summary>
    public const string StoppedShort = "camera's picture stopped";

    /// <summary>The words for the title while the camera cannot be opened.</summary>
    public const string NotOpenedShort = "camera not opened";

    private readonly ICameraProvider _cameras;
    private readonly ICameraProcessStarter _starter;
    private readonly bool _wakeOn;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly Lock _gate = new();

    /// <summary>The microphone the webcam was last decided for, by id; null before the first decision.</summary>
    private string? _decidedFor;

    /// <summary>A listing of the cameras an earlier pick began, which a later pick does not begin again while it lasts; null before the first.</summary>
    private Task<CameraList>? _listing;

    /// <summary>True while picks could not be decided, so that the failure is said once per stretch.</summary>
    private bool _undecided;

    /// <summary>The webcam the picked microphone belongs to; null for none.</summary>
    private CameraDevice? _webcam;

    /// <summary>Counts the webcams held, so that a process's lines from an earlier hold are passed over.</summary>
    private int _generation;

    private ICameraProcess? _process;
    private Task? _reading;
    private bool _starting;
    private long _retryAt;
    private ITimer? _timer;

    /// <summary>When the hold of the current webcam began, on the steady clock.</summary>
    private long _holdStarted;

    /// <summary>Done at the hold's first frame.</summary>
    private TaskCompletionSource _firstFrame = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private bool _everFrames;
    private long? _lastFrameAt;
    private bool _noFrameSaid;
    private bool _stoppedSaid;
    private string? _problem;
    private string? _problemShort;
    private string? _lastOpenWords;
    private bool _stopping;

    /// <summary>A hold over <paramref name="cameras"/> and <paramref name="starter"/>, holding a picture only when <paramref name="wakeOn"/>.</summary>
    public WebcamHold(ICameraProvider cameras, ICameraProcessStarter starter, bool wakeOn, TimeProvider time, ILogger logger)
    {
        _cameras = cameras;
        _starter = starter;
        _wakeOn = wakeOn;
        _time = time;
        _logger = logger;
    }

    /// <summary>The webcam the picked microphone belongs to, and its hold; null for a microphone of no webcam, and before the first pick.</summary>
    public WebcamState? State
    {
        get
        {
            lock (_gate)
            {
                if (_webcam is not { } webcam) return null;
                return new WebcamState(webcam.Name, _wakeOn && !_stopping, HasFramesLocked(_time.GetTimestamp()), _problem, _problemShort);
            }
        }
    }

    /// <summary>
    /// The microphone <paramref name="microphone"/> was picked: whether it belongs to a webcam is found once for it, and with
    /// the wake on the hold follows: begun for its webcam, moved from another webcam, or let go for a microphone of none.
    /// A pick of none changes nothing. Runs on the thread that looked, inside the microphone's pick, so it never throws: the
    /// cameras are listed only for a microphone outside the computer's own container, for <see cref="ListLimit"/> at most,
    /// and a list that cannot be read, that throws or that does not come in time leaves the pick undecided, said once per
    /// stretch (a warning with the wake on), and is asked again at the next pick. The microphone is recorded all the same.
    /// </summary>
    public void Follow(Microphone? microphone)
    {
        if (microphone is null) return;
        try
        {
            FollowCore(microphone);
        }
        catch (Exception ex)
        {
            // Deliberately not rethrown: this runs on the microphone's own look, inside its pick, and a failure of the
            // camera's side must never keep the microphone from being picked and opened (in person the recording would
            // then hold nothing). The camera list promises never to throw, but Windows' camera API can raise more than it
            // catches; the pick stays undecided, the failure is said once per stretch, and the next pick asks again.
            OnUndecided(microphone, $"{ex.GetType().Name}: {ex.Message.Split('\n')[0].Trim()}", ex);
        }
    }

    /// <summary>What <see cref="Follow"/> does, which may throw for a failure of the camera's side; Follow catches it.</summary>
    private void FollowCore(Microphone microphone)
    {
        lock (_gate)
        {
            if (_stopping || microphone.Id == _decidedFor) return;
        }

        CameraDevice? webcam = null;
        if (Cameras.MayBeOfACamera(microphone))
        {
            var list = ListCameras();
            if (list.Problem is { } problem)
            {
                OnUndecided(microphone, problem, null);
                return;
            }

            webcam = Cameras.WebcamOf(microphone, list.Cameras);
        }

        bool after;
        lock (_gate)
        {
            after = _undecided;
            _undecided = false;
        }

        if (after) _logger.LogDebug("The cameras are listed again, so ClassScribe can tell whether the microphone \"{Microphone}\" belongs to a webcam", microphone.Name);

        ICameraProcess? previous;
        Task? previousReading;
        CameraDevice? previousWebcam;
        int generation;
        lock (_gate)
        {
            if (_stopping) return;
            _decidedFor = microphone.Id;
            if (_webcam?.Id == webcam?.Id) return;

            previous = _process;
            previousReading = _reading;
            previousWebcam = _webcam;
            _process = null;
            _reading = null;
            _webcam = webcam;
            generation = ++_generation;
            ResetLocked();
        }

        if (previous is not null) _ = Task.Run(() => LetGo(previous, previousReading, previousWebcam!));
        if (webcam is null) return;

        if (!_wakeOn)
        {
            _logger.LogDebug("The microphone \"{Microphone}\" belongs to the camera \"{Camera}\"; Recording:WakeCameraMicrophone is off, so its picture is not held",
                microphone.Name, webcam.Name);
            return;
        }

        _logger.LogInformation("The microphone being recorded belongs to the camera \"{Camera}\", so ClassScribe keeps the camera's picture open, shared, until the recording ends: the camera cannot fall asleep and silence its microphone. Nothing of the picture is kept; the camera's light stays on",
            webcam.Name);
        lock (_gate)
        {
            if (generation != _generation || _stopping) return;
            _timer ??= _time.CreateTimer(_ => Look(), null, LookEvery, LookEvery);
            _starting = true;
        }

        StartProcess(webcam, generation);
    }

    /// <summary>
    /// Before the capture of <paramref name="microphone"/> opens: when its webcam's picture is being held and no frame has
    /// come yet, waits for the first frame, at most until <see cref="FirstFrameWait"/> after the hold began, so that the
    /// microphone opens on a camera that is awake. A camera that is slow or refuses delays the microphone by that much at
    /// most; the microphone's track is on the clock, so the wait is padded and the class's track never waits.
    /// </summary>
    public void BeforeOpen(Microphone microphone)
    {
        Task first;
        long started;
        lock (_gate)
        {
            if (!_wakeOn || _stopping || _webcam is null || _decidedFor != microphone.Id || _everFrames) return;
            first = _firstFrame.Task;
            started = _holdStarted;
        }

        var remaining = FirstFrameWait - _time.GetElapsedTime(started);
        if (remaining <= TimeSpan.Zero) return;

        using var waiting = new CancellationTokenSource();
        var delay = Task.Delay(remaining, _time, waiting.Token);
        Task.WaitAny(first, delay);
        waiting.Cancel();
    }

    /// <summary>
    /// Lets go of the hold at the recording's stop: the camera process is told to stop and given <see cref="StopWait"/> to
    /// let the camera go, then ended. Nothing is held after it. Safe to call twice.
    /// </summary>
    public void Stop()
    {
        ICameraProcess? process;
        Task? reading;
        CameraDevice? webcam;
        ITimer? timer;
        lock (_gate)
        {
            if (_stopping) return;
            _stopping = true;
            process = _process;
            reading = _reading;
            webcam = _webcam;
            timer = _timer;
            _process = null;
            _reading = null;
            _timer = null;
            _firstFrame.TrySetResult();
        }

        timer?.Dispose();
        if (process is not null) LetGo(process, reading, webcam!);
    }

    /// <summary>Lets go of the hold, as <see cref="Stop"/>.</summary>
    public void Dispose() => Stop();

    /// <summary>
    /// The cameras, listed for a pick on a thread of the pool, waited for at most <see cref="ListLimit"/>: a list Windows has
    /// not answered in time is a list with a problem, and while it lasts a later pick begins no other and waits for none, so
    /// that a listing that hangs inside Windows holds the microphone's look once, for <see cref="ListLimit"/> at most. A list
    /// that throws throws here, for <see cref="Follow"/> to catch.
    /// </summary>
    private CameraList ListCameras()
    {
        Task<CameraList> listing;
        lock (_gate)
        {
            if (_listing is { IsCompleted: false }) return new CameraList([], "Windows has not answered an earlier listing of the cameras yet");
            listing = _listing = Task.Run(() => _cameras.GetCamerasAsync(CancellationToken.None));
        }

        try
        {
            return listing.WaitAsync(ListLimit, _time).GetAwaiter().GetResult();
        }
        catch (TimeoutException) when (!listing.IsCompleted)
        {
            // Deliberately not rethrown: the listing hangs inside Windows, and the microphone's look must not wait for it any
            // longer; the pick stays undecided, and a pick after the listing has ended asks again.
            return new CameraList([], string.Create(System.Globalization.CultureInfo.InvariantCulture, $"Windows did not list the cameras within {ListLimit.TotalSeconds:0} seconds"));
        }
    }

    /// <summary>
    /// Whether <paramref name="microphone"/> belongs to a webcam could not be told, as <paramref name="why"/> says: said once
    /// per stretch, as a warning with the wake on (its camera's picture is then not held) and at Debug with it off, and
    /// asked again at the next pick, which comes at the microphone's next look.
    /// </summary>
    private void OnUndecided(Microphone microphone, string why, Exception? failure)
    {
        bool first;
        lock (_gate)
        {
            first = !_undecided;
            _undecided = true;
        }

        if (!first) return;
        if (_wakeOn)
            _logger.LogWarning(failure, "Could not tell whether the microphone \"{Microphone}\" belongs to a webcam, so no camera's picture is held for it yet; the microphone is recorded all the same, and each look asks again: {Why}", microphone.Name, why);
        else
            _logger.LogDebug(failure, "Could not tell whether the microphone \"{Microphone}\" belongs to a webcam, so each look asks again: {Why}", microphone.Name, why);
    }

    /// <summary>Forgets what the last webcam's hold had seen. Call with the lock held.</summary>
    private void ResetLocked()
    {
        _holdStarted = _time.GetTimestamp();
        _firstFrame.TrySetResult();
        _firstFrame = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _everFrames = false;
        _lastFrameAt = null;
        _noFrameSaid = false;
        _stoppedSaid = false;
        _problem = null;
        _problemShort = null;
        _lastOpenWords = null;
        _starting = false;
        _retryAt = 0;
    }

    /// <summary>True while a frame has come within <see cref="StoppedAfter"/>. Call with the lock held.</summary>
    private bool HasFramesLocked(long now) => _lastFrameAt is { } last && _time.GetElapsedTime(last, now) < StoppedAfter;

    /// <summary>
    /// Starts a camera process with a hold job for <paramref name="webcam"/>, and reads its lines. A process that Windows
    /// does not start, or that takes no job, whatever the cause, is a camera that could not be opened, tried again after
    /// <see cref="RetryEvery"/>; it never throws, since it runs on the microphone's pick or the hold's timer. A start for
    /// a webcam no longer held (the pick moved meanwhile) is let go and touches nothing of the current hold, whose own
    /// start may still be under way; a start that finds the hold's process already there is let go too, so that a webcam
    /// never has two camera processes.
    /// </summary>
    private void StartProcess(CameraDevice webcam, int generation)
    {
        ICameraProcess process;
        try
        {
            process = _starter.Start(RunIds.New(_time.GetUtcNow()));
        }
        catch (Exception ex)
        {
            // Deliberately not rethrown: the microphone is recorded all the same, whatever kept the process from starting
            // (Windows' refusal as a Win32Exception, or anything else); the line says the camera could not be opened and
            // why, and another start is tried after RetryEvery.
            OnOpenProblem(generation, $"Windows did not start ClassScribe's camera process ({ex.Message.TrimEnd('.')})", retry: true);
            return;
        }

        try
        {
            CameraLines.SendJobAsync(process.Channel, new CameraJob(CameraProtocol.Version, CameraJobKind.Hold, webcam.Id, webcam.Name), CancellationToken.None)
                .AsTask().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            // Deliberately not rethrown: the process ended, or its lines failed, before it read its job; it is let go, and
            // another is started after RetryEvery.
            Discard(process);
            OnOpenProblem(generation, $"ClassScribe's camera process ended before it took its job ({ex.Message.TrimEnd('.')})", retry: true);
            return;
        }

        bool held;
        lock (_gate)
        {
            // Only the current hold's start clears its flag: a late start for a webcam no longer held must not clear the
            // flag of the current webcam's start, which may be under way now.
            held = generation == _generation && !_stopping && _process is null;
            if (generation == _generation) _starting = false;
            if (held)
            {
                _process = process;
                _reading = Task.Run(() => ReadAsync(process, generation, webcam));
            }
        }

        if (!held) LetGo(process, null, webcam);
    }

    /// <summary>
    /// Ends <paramref name="process"/> and lets it go, after a start that went wrong; one that cannot be ended is left to its
    /// kill-on-close job, which ends it with this process at the latest.
    /// </summary>
    private void Discard(ICameraProcess process)
    {
        try
        {
            process.Kill();
            process.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            // Deliberately not rethrown: this runs on the microphone's pick or the hold's timer, and the process is in a
            // kill-on-close job of this process's own, which ends it at the latest when ClassScribe ends.
            _logger.LogDebug(ex, "Could not end the camera process {ProcessId} after its start went wrong; its job ends it with ClassScribe at the latest", process.ProcessId);
        }
    }

    /// <summary>
    /// Reads the camera process's lines until it ends: its status (the frames), its problems, its log lines (into the run's
    /// log at Debug, a warning as a warning), and its failure. A process that ends by itself during the hold is started again
    /// after <see cref="RetryEvery"/>.
    /// </summary>
    private async Task ReadAsync(ICameraProcess process, int generation, CameraDevice webcam)
    {
        try
        {
            while (await process.Channel.ReceiveAsync(CancellationToken.None) is { } line)
            {
                switch (CameraLines.Read(line))
                {
                    case CameraStatus status:
                        OnStatus(generation, status);
                        break;
                    case CameraProblem problem when problem.Words != CameraProblems.Stopped:
                        OnOpenProblem(generation, problem.Words, retry: false);
                        break;
                    case CameraOpened opened:
                        _logger.LogDebug("The camera \"{Camera}\" is open, shared: {How}", webcam.Name, opened.Encoder);
                        break;
                    case CameraLog log when log.Level is "Warning" or "Error":
                        _logger.LogWarning("Camera process: {Text}", log.Text);
                        break;
                    case CameraLog log:
                        _logger.LogDebug("Camera process: {Text}", log.Text);
                        break;
                    case CameraFailed failed:
                        _logger.LogWarning("The camera process holding \"{Camera}\" failed: {Why}", webcam.Name, failed.Why);
                        break;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException or ObjectDisposedException)
        {
            // Deliberately not rethrown: the process's lines ended or could not be read, which ends the reading as the
            // process's end does; a hold still wanted starts another process.
            _logger.LogDebug(ex, "The camera process's lines for \"{Camera}\" ended", webcam.Name);
        }

        bool ended;
        lock (_gate)
        {
            ended = generation == _generation && !_stopping && ReferenceEquals(_process, process);
            if (ended)
            {
                _process = null;
                _reading = null;
                _retryAt = _time.GetTimestamp() + Ticks(RetryEvery);
            }
        }

        if (!ended) return;

        _logger.LogDebug("The camera process holding \"{Camera}\" ended (exit {Code}); another starts in {Seconds} seconds", webcam.Name,
            process.ExitCode?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "not known", RetryEvery.TotalSeconds);
        await process.DisposeAsync();
    }

    /// <summary>A status line: the frames, and when the last came.</summary>
    private void OnStatus(int generation, CameraStatus status)
    {
        lock (_gate)
        {
            if (generation != _generation || status.Frames <= 0) return;
            _lastFrameAt = _time.GetTimestamp() - Ticks(TimeSpan.FromSeconds(Math.Max(0, status.SinceLastFrameSeconds)));
            _everFrames = true;
            _firstFrame.TrySetResult();
        }

        // The rest of what a status changes (the picture back) is said by the next look, once a second.
    }

    /// <summary>
    /// The camera could not be opened, as <paramref name="words"/> say: said once per cause, and a problem for the title
    /// while it lasts; with <paramref name="retry"/>, another process is started after <see cref="RetryEvery"/>.
    /// </summary>
    private void OnOpenProblem(int generation, string words, bool retry)
    {
        string? line = null;
        lock (_gate)
        {
            if (generation != _generation || _stopping || _webcam is not { } webcam) return;
            if (retry)
            {
                _starting = false;
                _retryAt = _time.GetTimestamp() + Ticks(RetryEvery);
            }

            _problem = CannotOpen(webcam.Name, words);
            _problemShort = NotOpenedShort;
            if (words != _lastOpenWords) line = _problem;
            _lastOpenWords = words;
        }

        if (line is not null) _logger.LogWarning("{Line}", line);
    }

    /// <summary>
    /// Once a second while a picture is held: no picture yet after <see cref="FirstFrameWait"/>, the picture stopped for
    /// <see cref="StoppedAfter"/>, the picture back, and a new camera process when the last could not start or ended.
    /// Runs on a timer's thread, so it never throws: an exception there would end ClassScribe's process, and the class
    /// with it.
    /// </summary>
    private void Look()
    {
        var restarting = 0;
        try
        {
            LookCore(ref restarting);
        }
        catch (Exception ex)
        {
            // Deliberately not rethrown: this runs on the hold's timer thread, where an exception would end the process and
            // the class with it. A start this look began is given up, so that the next look after RetryEvery starts another,
            // and the microphone is recorded all the same.
            lock (_gate)
            {
                if (restarting != 0 && restarting == _generation && _process is null)
                {
                    _starting = false;
                    _retryAt = _time.GetTimestamp() + Ticks(RetryEvery);
                }
            }

            _logger.LogWarning(ex, "The camera's hold could not look at its camera process; it tries again in {Seconds} seconds, and the microphone is recorded all the same", RetryEvery.TotalSeconds);
        }
    }

    /// <summary>What <see cref="Look"/> does, which may throw; <paramref name="restarting"/> is set to the generation of a start it begins.</summary>
    private void LookCore(ref int restarting)
    {
        string? warning = null;
        string? information = null;
        CameraDevice? restart = null;
        var generation = 0;
        lock (_gate)
        {
            if (_stopping || _webcam is not { } webcam) return;

            var now = _time.GetTimestamp();
            var hasFrames = HasFramesLocked(now);
            if (!_everFrames && !_noFrameSaid && _time.GetElapsedTime(_holdStarted, now) >= FirstFrameWait)
            {
                _noFrameSaid = true;
                warning = $"The camera \"{webcam.Name}\" has sent no picture yet; its microphone records meanwhile";
            }
            else if (_everFrames && !hasFrames && !_stoppedSaid)
            {
                _stoppedSaid = true;
                _problem = Stopped(webcam.Name);
                _problemShort = StoppedShort;
                warning = _problem;
            }
            else if (hasFrames && (_stoppedSaid || _problem is not null))
            {
                _stoppedSaid = false;
                _problem = null;
                _problemShort = null;
                _lastOpenWords = null;
                information = $"The camera \"{webcam.Name}\" is open again";
            }

            if (_process is null && !_starting && now >= _retryAt)
            {
                _starting = true;
                restart = webcam;
                generation = _generation;
                restarting = generation;
            }
        }

        if (warning is not null) _logger.LogWarning("{Line}", warning);
        if (information is not null) _logger.LogInformation("{Line}", information);
        if (restart is not null) StartProcess(restart, generation);
    }

    /// <summary>
    /// Lets <paramref name="process"/> go: told to stop, given <see cref="StopWait"/> for its lines to end (it lets the camera
    /// go first), then ended if it has not, and disposed, which closes its job.
    /// </summary>
    private void LetGo(ICameraProcess process, Task? reading, CameraDevice webcam)
    {
        try
        {
            process.Channel.SendAsync(CameraProtocol.Stop, new CameraStop(0), CameraJson.Default.CameraStop, CancellationToken.None).AsTask().GetAwaiter().GetResult();
            if (reading is not null && !reading.IsCompleted)
            {
                using var waiting = new CancellationTokenSource();
                Task.WaitAny(reading, Task.Delay(StopWait, _time, waiting.Token));
                waiting.Cancel();
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // Deliberately not rethrown: the process has already ended or closed its lines, which is what the stop asks for.
            _logger.LogDebug(ex, "The camera process holding \"{Camera}\" had already gone", webcam.Name);
        }

        if (reading is not { IsCompleted: true }) process.Kill();
        process.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _logger.LogInformation("Let go of the picture of the camera \"{Camera}\"", webcam.Name);
    }

    /// <summary>The line when the camera could not be opened, with the cause in <paramref name="words"/>.</summary>
    public static string CannotOpen(string camera, string words) =>
        $"The camera \"{camera}\" could not be opened: {words}. Without its picture open the camera may fall asleep, and its microphone then sends only silence. Turn off Device Auto Sleep in OBSBOT Center, or record another microphone. ClassScribe tries again every {RetryEvery.TotalSeconds:0} seconds";

    /// <summary>The line when the held picture stopped.</summary>
    public static string Stopped(string camera) =>
        $"The camera \"{camera}\" stopped sending its picture. Is its lens folded? Folded, its microphone is off. ClassScribe opens it again every {RetryEvery.TotalSeconds:0} seconds";

    /// <summary><paramref name="span"/> in the clock's ticks.</summary>
    private long Ticks(TimeSpan span) => (long)(span.TotalSeconds * _time.TimestampFrequency);
}
