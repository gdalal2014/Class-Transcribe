using ClassScribe.Camera;

namespace ClassScribe.Tests;

/// <summary>
/// This laptop's video capture devices, as the camera design's probe found them (teach-video 6.4): the Integrated Camera at
/// the front of the screen, the OBSBOT Meet Flip on USB, Phone Link's virtual camera of the Pixel 10 Pro XL, and the
/// infrared camera Windows Hello uses, which has no colour source and so is never listed. The OBSBOT's container is its
/// microphone's (<see cref="LaptopMicrophones.Obsbot"/>); the built-in camera's is the computer's own, which the laptop's
/// array shares.
/// </summary>
public static class LaptopCameras
{
    /// <summary>The container of the OBSBOT, its camera and its microphone alike.</summary>
    public static readonly Guid ObsbotContainer = new("6a1d3c5e-0b0b-4f1a-9d1e-0b5b07000001");

    /// <summary>The container of the Pixel's virtual camera.</summary>
    public static readonly Guid PixelContainer = new("6a1d3c5e-0b0b-4f1a-9d1e-0b5b07000002");

    /// <summary>The Integrated Camera's device facts: at the front, in the computer's own container.</summary>
    public static readonly VideoDeviceFacts IntegratedFacts = new(@"\\?\USB#VID_30C9&PID_00F4&MI_00#7&1f3a&0&0000#{e5323777-f976-4f5b-9b55-b94699c46e44}\GLOBAL",
        "Integrated Camera", CameraPanel.Front, Cameras.ThisComputer);

    /// <summary>The OBSBOT's device facts: USB, no panel, its own container.</summary>
    public static readonly VideoDeviceFacts ObsbotFacts = new(@"\\?\USB#VID_3564&PID_FF06&MI_00#8&2c4d&0&0000#{e5323777-f976-4f5b-9b55-b94699c46e44}\GLOBAL",
        "OBSBOT Meet Flip StreamCamera", CameraPanel.Unknown, ObsbotContainer);

    /// <summary>Phone Link's virtual camera of the Pixel: a software device.</summary>
    public static readonly VideoDeviceFacts PixelFacts = new(@"\\?\SWD#VCAMDEVAPI#6a1d3c5e0b0b#{e5323777-f976-4f5b-9b55-b94699c46e44}\GLOBAL",
        "Pixel 10 Pro XL", CameraPanel.Unknown, PixelContainer);

    /// <summary>The infrared camera Windows Hello uses: at the front, but with an infrared source only.</summary>
    public static readonly VideoDeviceFacts InfraredFacts = new(@"\\?\USB#VID_30C9&PID_00F4&MI_02#7&1f3a&0&0002#{e5323777-f976-4f5b-9b55-b94699c46e44}\GLOBAL",
        "Integrated IR Camera", CameraPanel.Front, Cameras.ThisComputer);

    /// <summary>All four devices, in Windows' order.</summary>
    public static IReadOnlyList<VideoDeviceFacts> AllFacts { get; } = [IntegratedFacts, InfraredFacts, ObsbotFacts, PixelFacts];

    /// <summary>The devices with a colour source: all but the infrared camera.</summary>
    public static IReadOnlySet<string> ColourSources { get; } = new HashSet<string>([IntegratedFacts.Id, ObsbotFacts.Id, PixelFacts.Id], StringComparer.OrdinalIgnoreCase);

    /// <summary>The Integrated Camera as listed: built in, at the front.</summary>
    public static CameraDevice Integrated { get; } = new(IntegratedFacts.Id, IntegratedFacts.Name, CameraPlace.BuiltInFront, Cameras.ThisComputer);

    /// <summary>The OBSBOT as listed: USB.</summary>
    public static CameraDevice Obsbot { get; } = new(ObsbotFacts.Id, ObsbotFacts.Name, CameraPlace.Usb, ObsbotContainer);

    /// <summary>The Pixel's virtual camera as listed.</summary>
    public static CameraDevice Pixel { get; } = new(PixelFacts.Id, PixelFacts.Name, CameraPlace.Virtual, PixelContainer);

    /// <summary>The three colour cameras, as the provider lists them.</summary>
    public static IReadOnlyList<CameraDevice> All { get; } = [Integrated, Obsbot, Pixel];
}

/// <summary>What Windows says the camera needs, as a test sets it: this laptop's Windows 11, an H.264 encoder, and Bitdefender.</summary>
public sealed class FakeCameraSystem : ICameraSystem
{
    /// <inheritdoc />
    public Version WindowsVersion { get; set; } = new(10, 0, 26300);

    /// <summary>Whether an H.264 encoder is installed; null for "Windows did not say".</summary>
    public bool? HasH264Encoder { get; set; } = true;

    /// <summary>The security programs; null for "Security Center did not say".</summary>
    public IReadOnlyList<string>? Programs { get; set; } = ["Bitdefender Antivirus"];

    /// <inheritdoc />
    public Task<bool?> HasH264EncoderAsync(CancellationToken cancellationToken) => Task.FromResult(HasH264Encoder);

    /// <inheritdoc />
    public IReadOnlyList<string>? SecurityPrograms() => Programs;
}

/// <summary>Windows' camera record as a test sets it: the privacy switches and the apps in it; both switches unset, and no app, unless a test says otherwise.</summary>
public sealed class FakeCameraUsageRecord : ICameraUsageRecord
{
    /// <summary>The record a read gives.</summary>
    public Audio.Microphones.UsageRecordListing Listing { get; set; } = new(null, null, [], null);

    /// <inheritdoc />
    public Audio.Microphones.UsageRecordListing Read() => Listing;
}

/// <summary>The cameras a test connects, listed as a provider lists them; listing never opens anything, here as in the real provider.</summary>
public sealed class FakeCameraProvider : ICameraProvider
{
    /// <summary>The cameras connected now; the laptop's three unless a test says otherwise.</summary>
    public IReadOnlyList<CameraDevice> Connected { get; set; } = LaptopCameras.All;

    /// <summary>Why the list cannot be read, as a real provider says it; null while it can.</summary>
    public string? Problem { get; set; }

    /// <summary>How many times the cameras were listed.</summary>
    public int Listings { get; private set; }

    /// <inheritdoc />
    public Task<CameraList> GetCamerasAsync(CancellationToken cancellationToken)
    {
        Listings++;
        return Task.FromResult(Problem is null ? new CameraList(Connected) : new CameraList([], Problem));
    }
}

/// <summary>
/// A camera process as a test plays it (teach-video 11): its lines go over an in-memory pipe (<see cref="DuplexStreamPair"/>),
/// the starter's end as <see cref="Channel"/> and the process's end read and written by the test, as the process would: its
/// job, then whatever lines the test scripts. Its exit and a kill are the test's to say. Nothing is started and no camera
/// is opened.
/// </summary>
public sealed class FakeCameraProcess : ICameraProcess
{
    /// <summary>The in-memory pipe between the starter and the process.</summary>
    private readonly DuplexStreamPair _pipe = new();

    /// <summary>The process's end of the lines, made when the test first speaks as the process.</summary>
    private Link.LineChannel? _processEnd;

    /// <summary>A process with the id <paramref name="processId"/>.</summary>
    public FakeCameraProcess(int processId = 7001)
    {
        ProcessId = processId;
        Channel = new Link.LineChannel(_pipe.WindowEnd);
    }

    /// <inheritdoc />
    public int ProcessId { get; }

    /// <inheritdoc />
    public Link.LineChannel Channel { get; }

    /// <summary>The process's own end of the lines, which the test reads the job from and writes the process's lines to.</summary>
    public Link.LineChannel ProcessEnd => _processEnd ??= new Link.LineChannel(_pipe.RecorderEnd);

    /// <summary>The process's own end of the pipe, as a stream, for the real <see cref="CameraHost"/> to run over in place of the test.</summary>
    public Stream ProcessStream => _pipe.RecorderEnd;

    /// <inheritdoc />
    public int? ExitCode { get; private set; }

    /// <summary>True once the starter killed it.</summary>
    public bool Killed { get; private set; }

    /// <summary>Reads the job, the first line, as the camera process would; null when the starter closed its end first.</summary>
    public async Task<CameraJob?> ReadJobAsync()
    {
        var first = await ProcessEnd.ReceiveAsync(CancellationToken.None);
        return first is { Type: CameraProtocol.Job } ? System.Text.Json.JsonSerializer.Deserialize(first.Body, CameraJson.Default.CameraJob) : null;
    }

    /// <summary>Ends the process with <paramref name="exitCode"/>: its end of the lines closes, as a process's pipes do.</summary>
    public void Exit(int exitCode)
    {
        ExitCode = exitCode;
        _pipe.Break();
    }

    /// <inheritdoc />
    public void Kill()
    {
        Killed = true;
        Exit(ExitCode ?? 1);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await Channel.DisposeAsync();
        if (_processEnd is not null) await _processEnd.DisposeAsync();
        _pipe.Dispose();
    }
}

/// <summary>Starts <see cref="FakeCameraProcess"/>es in place of real camera processes, and keeps each with its job id.</summary>
public sealed class FakeCameraProcessStarter : ICameraProcessStarter
{
    /// <summary>The processes started, with their job ids, in order.</summary>
    public List<(string JobId, FakeCameraProcess Process)> Started { get; } = [];

    /// <inheritdoc />
    public ICameraProcess Start(string jobId)
    {
        if (!Commands.RunIds.IsWellFormed(jobId)) throw new ArgumentException($"\"{jobId}\" is not a job id.", nameof(jobId));

        var process = new FakeCameraProcess(7001 + Started.Count);
        Started.Add((jobId, process));
        return process;
    }
}

/// <summary>
/// A camera capture as a test plays it, in place of WinRT's MediaCapture (see <see cref="ICameraCapture"/>): it opens
/// nothing, notes each call in order, and refuses what Windows refuses before InitializeAsync has completed (the
/// exclusive-control event, FrameSources and the frame reader), with the same exception type and code Windows gave on 21
/// October: a COMException of 0xC00D36B6 MF_E_NOT_INITIALIZED (ROOT-CAUSE.md beside the camera probe). A test can make any
/// step fail, set the reader's start status, and hand over frames.
/// </summary>
public sealed class FakeCameraCapture : ICameraCapture
{
    /// <summary>Windows' words for MF_E_NOT_INITIALIZED, as finding 7 gave them.</summary>
    public const string NotInitializedWords = "This object needs to be initialized before the requested operation can be carried out.";

    private readonly Lock _gate = new();
    private readonly List<string> _calls = [];
    private readonly List<string> _tooEarly = [];
    private Action? _onFrame;
    private Action<string>? _onEvent;
    private bool _initialized;

    /// <summary>The calls, by the member's name, in the order they came.</summary>
    public IReadOnlyList<string> Calls
    {
        get { lock (_gate) return [.. _calls]; }
    }

    /// <summary>The calls that needed an initialised MediaCapture and came before InitializeAsync.</summary>
    public IReadOnlyList<string> TooEarly
    {
        get { lock (_gate) return [.. _tooEarly]; }
    }

    /// <summary>The steps that fail, by the member's name (such as nameof(InitializeShared)), with what they throw.</summary>
    public Dictionary<string, Exception> Failures { get; } = [];

    /// <summary>What StartReader answers: Success unless a test says otherwise.</summary>
    public string StartStatus { get; set; } = CameraRoute.Success;

    /// <summary>The colour source's format as the camera runs it.</summary>
    public string Format { get; set; } = "MJPG 1920x1080 at 30 frames a second";

    /// <summary>The size the reader was asked to convert to; null for none, or before it was made.</summary>
    public CameraSize? ConvertedTo { get; private set; }

    /// <summary>True once the camera was let go.</summary>
    public bool Disposed { get; private set; }

    /// <summary>True once frames have been started and not stopped.</summary>
    public bool Streaming { get; private set; }

    /// <inheritdoc />
    public void AddFailed(Action<string> onFailed)
    {
        Step(nameof(AddFailed));
        _onEvent = onFailed;
    }

    /// <inheritdoc />
    public void FindSourceGroup() => Step(nameof(FindSourceGroup));

    /// <inheritdoc />
    public void InitializeShared()
    {
        Step(nameof(InitializeShared));
        lock (_gate) _initialized = true;
    }

    /// <inheritdoc />
    public void AddStreamStateChanged(Action<string> onEvent) => Step(nameof(AddStreamStateChanged));

    /// <inheritdoc />
    public void AddExclusiveControlChanged(Action<string> onEvent) => Step(nameof(AddExclusiveControlChanged), needsInitialization: true);

    /// <inheritdoc />
    public string FindColourSource()
    {
        Step(nameof(FindColourSource), needsInitialization: true);
        return Format;
    }

    /// <inheritdoc />
    public string CreateReader(CameraSize? convertTo)
    {
        Step(nameof(CreateReader), needsInitialization: true);
        ConvertedTo = convertTo;
        return convertTo is { } size ? $"NV12 {size.Width}x{size.Height}" : "the camera's own format, nothing converted";
    }

    /// <inheritdoc />
    public void UseRealtime() => Step(nameof(UseRealtime));

    /// <inheritdoc />
    public void AddFrameArrived(Action onFrame)
    {
        Step(nameof(AddFrameArrived));
        _onFrame = onFrame;
    }

    /// <inheritdoc />
    public string StartReader()
    {
        Step(nameof(StartReader));
        Streaming = StartStatus == CameraRoute.Success;
        return StartStatus;
    }

    /// <inheritdoc />
    public void StopReader()
    {
        Step(nameof(StopReader));
        Streaming = false;
    }

    /// <summary>Notes the camera let go; a failure set for it is thrown after it is noted.</summary>
    public void Dispose()
    {
        Disposed = true;
        Streaming = false;
        Step(nameof(Dispose));
    }

    /// <summary>Hands over <paramref name="count"/> frames, as the reader's thread would, while streaming.</summary>
    public void Frames(int count = 1)
    {
        for (var i = 0; i < count && Streaming; i++) _onFrame?.Invoke();
    }

    /// <summary>Reports <paramref name="words"/> as MediaCapture.Failed would.</summary>
    public void RaiseFailed(string words) => _onEvent?.Invoke(words);

    /// <summary>Notes the call; refuses it as Windows does when it needs InitializeAsync first; throws the failure a test set for it.</summary>
    private void Step(string name, bool needsInitialization = false)
    {
        lock (_gate)
        {
            _calls.Add(name);
            if (needsInitialization && !_initialized)
            {
                _tooEarly.Add(name);
                throw new System.Runtime.InteropServices.COMException(NotInitializedWords, CameraHresults.NotInitialized);
            }
        }

        if (Failures.TryGetValue(name, out var failure)) throw failure;
    }
}

/// <summary>Makes <see cref="FakeCameraCapture"/>s, one a call, keeping each with the camera it was made for; or fails to make one.</summary>
public sealed class FakeCameraCaptureFactory : ICameraCaptureFactory
{
    private readonly Lock _gate = new();
    private readonly List<(string CameraId, string CameraName, FakeCameraCapture Capture)> _made = [];

    /// <summary>Sets up each capture before it is handed out, as a test wants it: failing a step, or answering another start status.</summary>
    public Action<FakeCameraCapture, int>? Prepare { get; set; }

    /// <summary>The captures made, in order, with their cameras.</summary>
    public IReadOnlyList<(string CameraId, string CameraName, FakeCameraCapture Capture)> Made
    {
        get { lock (_gate) return [.. _made]; }
    }

    /// <summary>The last capture made; null before the first.</summary>
    public FakeCameraCapture? Last
    {
        get { lock (_gate) return _made.Count == 0 ? null : _made[^1].Capture; }
    }

    /// <inheritdoc />
    public ICameraCapture Create(string cameraId, string cameraName)
    {
        var capture = new FakeCameraCapture();
        int index;
        lock (_gate)
        {
            index = _made.Count;
            _made.Add((cameraId, cameraName, capture));
        }

        Prepare?.Invoke(capture, index);
        return capture;
    }
}

/// <summary>
/// Reads every line a camera process sends, as its starter would, typed by <see cref="CameraLines.Read"/>, into a list a
/// test can look at while the process runs; until the process's end of the lines closes.
/// </summary>
public sealed class CameraLinesSeen
{
    private readonly Lock _gate = new();
    private readonly List<object> _lines = [];

    /// <summary>Starts reading <paramref name="channel"/>, the starter's end, on the thread pool.</summary>
    public CameraLinesSeen(Link.LineChannel channel) => Reading = Task.Run(async () =>
    {
        try
        {
            while (await channel.ReceiveAsync(CancellationToken.None) is { } line)
            {
                if (CameraLines.Read(line) is { } read)
                {
                    lock (_gate) _lines.Add(read);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // The process's end closed: nothing more to read.
        }
    });

    /// <summary>The reading, which ends when the process's end of the lines closes.</summary>
    public Task Reading { get; }

    /// <summary>Every line so far, in order.</summary>
    public IReadOnlyList<object> All
    {
        get { lock (_gate) return [.. _lines]; }
    }

    /// <summary>The lines of type <typeparamref name="T"/> so far, in order.</summary>
    public IReadOnlyList<T> Of<T>() => [.. All.OfType<T>()];

    /// <summary>The words of the log lines so far, in order.</summary>
    public IReadOnlyList<string> Logs => [.. Of<CameraLog>().Select(log => log.Text)];
}
