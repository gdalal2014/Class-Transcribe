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

/// <summary>
/// The cameras a test connects, listed as a provider lists them; listing never opens anything, here as in the real provider.
/// A test can also make the listing throw what gets past a real provider's promise, or never answer, as a listing that
/// hangs inside Windows would.
/// </summary>
public sealed class FakeCameraProvider : ICameraProvider
{
    private int _listings;

    /// <summary>The cameras connected now; the laptop's three unless a test says otherwise.</summary>
    public IReadOnlyList<CameraDevice> Connected { get; set; } = LaptopCameras.All;

    /// <summary>Why the list cannot be read, as a real provider says it; null while it can.</summary>
    public string? Problem { get; set; }

    /// <summary>When set, each listing throws this, as a failure of Windows' camera API the provider did not catch would.</summary>
    public Exception? Failure { get; set; }

    /// <summary>True for listings that never answer, as one that hangs inside Windows.</summary>
    public bool Hangs { get; set; }

    /// <summary>How many times the cameras were listed.</summary>
    public int Listings => Volatile.Read(ref _listings);

    /// <inheritdoc />
    public Task<CameraList> GetCamerasAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _listings);
        if (Failure is { } failure) throw failure;
        if (Hangs) return new TaskCompletionSource<CameraList>(TaskCreationOptions.RunContinuationsAsynchronously).Task;
        return Task.FromResult(Problem is null ? new CameraList(Connected) : new CameraList([], Problem));
    }
}

/// <summary>
/// A camera process as a test plays it (teach-video 11): its lines go over an in-memory pipe (<see cref="DuplexStreamPair"/>),
/// the starter's end as <see cref="Channel"/> and the process's end read and written by the test, as the process would: its
/// job, then whatever lines the test scripts. Its exit and a kill are the test's to say. A test can make it stop reading its
/// input, so that the starter's writes hang as on a full pipe until the process ends, and make letting it go never finish,
/// as for a process stuck inside Windows. Nothing is started and no camera is opened.
/// </summary>
public sealed class FakeCameraProcess : ICameraProcess
{
    /// <summary>The in-memory pipe between the starter and the process.</summary>
    private readonly DuplexStreamPair _pipe = new();

    /// <summary>Completed when the process ends, by itself or by a kill: what a write that hangs waits for.</summary>
    private readonly TaskCompletionSource _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>The process's end of the lines, made when the test first speaks as the process.</summary>
    private Link.LineChannel? _processEnd;

    /// <summary>A process with the id <paramref name="processId"/>.</summary>
    public FakeCameraProcess(int processId = 7001)
    {
        ProcessId = processId;
        Channel = new Link.LineChannel(new StickingInput(_pipe.WindowEnd, this));
    }

    /// <summary>
    /// True once the process stopped reading its input: each write of the starter's from then on hangs, whatever its
    /// cancellation says, as a write to a synchronous pipe that is full does, until the process ends, and then fails as a
    /// broken pipe does.
    /// </summary>
    public bool InputStuck { get; set; }

    /// <summary>True for a letting go that never finishes, as when the process cannot be ended and its error output stays open.</summary>
    public bool DisposeHangs { get; set; }

    /// <summary>True once the starter began to let the process go.</summary>
    public bool DisposeEntered { get; private set; }

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
        _ended.TrySetResult();
    }

    /// <summary>Notes that the process ended with <paramref name="exitCode"/>, as when the real camera host it ran has returned and closed its lines itself.</summary>
    public void Exited(int exitCode)
    {
        ExitCode = exitCode;
        _ended.TrySetResult();
    }

    /// <summary>
    /// Ends the process with <paramref name="exitCode"/> once its starter has its last lines: its own end of the lines closes,
    /// so the starter reads what was written, then the end, as from a process that wrote its last line and exited.
    /// </summary>
    public async Task EndAsync(int exitCode)
    {
        ExitCode = exitCode;
        await ProcessEnd.DisposeAsync();
        _ended.TrySetResult();
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
        DisposeEntered = true;
        if (DisposeHangs) await new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously).Task;

        await Channel.DisposeAsync();
        if (_processEnd is not null) await _processEnd.DisposeAsync();
        _pipe.Dispose();
    }

    /// <summary>
    /// The starter's end of the pipe, which writes as the in-memory pipe does until the process stops reading its input
    /// (<see cref="InputStuck"/>); from then on a write waits for the process to end, its cancellation passed over as a
    /// synchronous pipe's is, and then fails as a broken pipe does.
    /// </summary>
    /// <param name="inner">The in-memory pipe's end.</param>
    /// <param name="process">The process whose input it is.</param>
    private sealed class StickingInput(Stream inner, FakeCameraProcess process) : Stream
    {
        /// <inheritdoc />
        public override bool CanRead => true;

        /// <inheritdoc />
        public override bool CanWrite => true;

        /// <inheritdoc />
        public override bool CanSeek => false;

        /// <summary>Not supported: a pipe has no length.</summary>
        public override long Length => throw new NotSupportedException();

        /// <summary>Not supported: a pipe has no position.</summary>
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        /// <inheritdoc />
        public override void Flush() => inner.Flush();

        /// <inheritdoc />
        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

        /// <inheritdoc />
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

        /// <inheritdoc />
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            inner.ReadAsync(buffer, offset, count, cancellationToken);

        /// <inheritdoc />
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => inner.ReadAsync(buffer, cancellationToken);

        /// <inheritdoc />
        public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        /// <inheritdoc />
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        /// <inheritdoc />
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (process.InputStuck)
            {
                await process._ended.Task;
                throw new IOException("Pipe is broken.");
            }

            await inner.WriteAsync(buffer, cancellationToken);
        }

        /// <summary>Not supported: a pipe cannot seek.</summary>
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        /// <summary>Not supported: a pipe has no length.</summary>
        public override void SetLength(long value) => throw new NotSupportedException();

        /// <inheritdoc />
        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
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
    private Action<ICameraFrame>? _onPixels;
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

    /// <summary>
    /// The steps that get stuck, by the member's name, each until its gate is set: a call that does not return, as one
    /// waiting on a security program's prompt inside Windows would. Set them before the capture is handed out.
    /// </summary>
    public Dictionary<string, ManualResetEventSlim> Stuck { get; } = [];

    /// <summary>How many frames the reader hands over as soon as it has started, as a camera that streams at once; none unless a test says.</summary>
    public int FramesOnStart { get; set; }

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

    /// <summary>True when the camera was opened with its frames in CPU memory, as a record job opens it.</summary>
    public bool CpuFrames { get; private set; }

    /// <inheritdoc />
    public void InitializeShared(bool cpuFrames)
    {
        Step(nameof(InitializeShared));
        CpuFrames = cpuFrames;
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
    public void AddFrameReader(Action<ICameraFrame> onFrame)
    {
        Step(nameof(AddFrameReader));
        _onPixels = onFrame;
    }

    /// <inheritdoc />
    public string StartReader()
    {
        Step(nameof(StartReader));
        Streaming = StartStatus == CameraRoute.Success;
        Frames(FramesOnStart);
        return StartStatus;
    }

    /// <summary>
    /// Hands over a frame with its pixels, as the reader's thread would for a record job, while streaming: taken at
    /// <paramref name="stamp100ns"/> (null for none), at the size the reader converts to, its picture of
    /// <paramref name="brightness"/> throughout. False when the capture does not stream, or has no record job's reader.
    /// </summary>
    public bool Deliver(long? stamp100ns, byte brightness = 128)
    {
        if (!Streaming || _onPixels is null || ConvertedTo is not { } size) return false;

        _onPixels(new FakeCameraFrame(stamp100ns, size.Width, size.Height, brightness));
        return true;
    }

    /// <summary>
    /// Hands over <paramref name="frame"/> as it is, as the reader's thread would for a record job, while streaming: such as a
    /// frame of another size than the reader was asked for, or one whose pixels cannot be read. False when the capture does
    /// not stream, or has no record job's reader.
    /// </summary>
    public bool DeliverFrame(ICameraFrame frame)
    {
        if (!Streaming || _onPixels is null) return false;

        _onPixels(frame);
        return true;
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

    /// <summary>
    /// Notes the call; refuses it as Windows does when it needs InitializeAsync first; gets stuck until its gate is set when a
    /// test made it stuck; throws the failure a test set for it.
    /// </summary>
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

        if (Stuck.TryGetValue(name, out var stuck)) stuck.Wait();
        if (Failures.TryGetValue(name, out var failure)) throw failure;
    }
}

/// <summary>
/// A frame as a test hands it over: its stamp, its size, and a picture of one brightness, in NV12; or, unless
/// <paramref name="readable"/>, a frame whose pixels cannot be read, as one Windows hands over in another format or outside
/// CPU memory.
/// </summary>
/// <param name="stamp">When it was taken, in units of 100 nanoseconds; null for none.</param>
/// <param name="width">Its width.</param>
/// <param name="height">Its height.</param>
/// <param name="brightness">The brightness of every pixel.</param>
/// <param name="readable">False for a frame whose NV12 pixels cannot be copied.</param>
public sealed class FakeCameraFrame(long? stamp, int width, int height, byte brightness, bool readable = true) : ICameraFrame
{
    /// <inheritdoc />
    public long? Stamp100ns => stamp;

    /// <inheritdoc />
    public int Width => width;

    /// <inheritdoc />
    public int Height => height;

    /// <inheritdoc />
    public bool CopyNv12To(byte[] target)
    {
        if (!readable) return false;

        target.AsSpan(0, width * height).Fill(brightness);
        target.AsSpan(width * height, width * height / 2).Fill(128);
        return true;
    }
}

/// <summary>One sample a <see cref="FakePieceWriter"/> was handed: when, and whether it was black or the brightness of the frame.</summary>
/// <param name="At">Its time from the piece's start.</param>
/// <param name="Black">True for a black frame the writer made (the first, and one for a camera lost).</param>
/// <param name="Brightness">The brightness of the frame's first pixel.</param>
public sealed record WrittenSample(TimeSpan At, bool Black, byte Brightness);

/// <summary>
/// A piece writer as a test plays it (teach-video 11): it writes nothing to disk but an empty file at its path, so that the
/// piece is there as a real one would be, and notes each sample, the black frame at 0 first, and the end it was finished
/// at. A test can make the Nth write fail as an encoder or a disk would.
/// </summary>
/// <param name="path">The piece's file.</param>
/// <param name="settings">How it is encoded.</param>
public sealed class FakePieceWriter(string path, PieceSettings settings) : IPieceWriter
{
    private readonly Lock _gate = new();
    private readonly List<WrittenSample> _samples = [new(TimeSpan.Zero, true, 16)];

    /// <inheritdoc />
    public string Path => path;

    /// <summary>How the piece is encoded.</summary>
    public PieceSettings Settings => settings;

    /// <inheritdoc />
    public string Encoder => settings.Software ? "software encoder (Fake H.264 Encoder)" : "hardware encoder (Fake H.264 Encoder)";

    /// <summary>The samples so far, the black frame at 0 first.</summary>
    public IReadOnlyList<WrittenSample> Samples
    {
        get { lock (_gate) return [.. _samples]; }
    }

    /// <summary>The end the piece was finished at; null before.</summary>
    public TimeSpan? FinishedAt { get; private set; }

    /// <summary>True once disposed.</summary>
    public bool Disposed { get; private set; }

    /// <summary>When set, the write numbered so (1 for the first frame after the black one) throws this.</summary>
    public (int Number, Exception Failure)? FailWrite { get; set; }

    /// <inheritdoc />
    public void Write(byte[] nv12, TimeSpan at) => Add(new WrittenSample(at, false, nv12[0]));

    /// <inheritdoc />
    public void WriteBlack(TimeSpan at) => Add(new WrittenSample(at, true, 16));

    /// <inheritdoc />
    public void Finish(TimeSpan end)
    {
        if (FinishedAt is not null) throw new InvalidOperationException("Finished twice.");
        FinishedAt = end;
    }

    /// <inheritdoc />
    public void Dispose() => Disposed = true;

    /// <summary>Notes a sample, later than the last, or fails as the test asked.</summary>
    private void Add(WrittenSample sample)
    {
        lock (_gate)
        {
            if (FinishedAt is not null) throw new InvalidOperationException("Written after the finish.");
            if (FailWrite is { } fail && _samples.Count == fail.Number) throw fail.Failure;
            if (sample.At <= _samples[^1].At) throw new InvalidOperationException($"A sample at {sample.At} is not later than the last, at {_samples[^1].At}.");
            _samples.Add(sample);
        }
    }
}

/// <summary>Makes <see cref="FakePieceWriter"/>s, keeping each; a start with the hardware encoder can be refused, as Intel's may be.</summary>
public sealed class FakePieceWriterFactory : IPieceWriterFactory
{
    private readonly Lock _gate = new();
    private readonly List<FakePieceWriter> _made = [];

    /// <summary>When set, a start with the hardware encoder throws this.</summary>
    public Exception? HardwareRefuses { get; set; }

    /// <summary>When set, every start throws this.</summary>
    public Exception? Refuses { get; set; }

    /// <summary>Sets up each writer as it is made, such as to fail a write.</summary>
    public Action<FakePieceWriter>? Prepare { get; set; }

    /// <summary>The writers made, in order.</summary>
    public IReadOnlyList<FakePieceWriter> Made
    {
        get { lock (_gate) return [.. _made]; }
    }

    /// <inheritdoc />
    public IPieceWriter Create(string path, PieceSettings settings)
    {
        if (Refuses is { } refused) throw refused;
        if (!settings.Software && HardwareRefuses is { } refusal)
        {
            File.WriteAllBytes(path, []);
            throw refusal;
        }

        using (new FileStream(path, FileMode.CreateNew, FileAccess.Write)) { }
        var writer = new FakePieceWriter(path, settings);
        Prepare?.Invoke(writer);
        lock (_gate) _made.Add(writer);
        return writer;
    }
}

/// <summary>
/// A lesson's camera track as a test plays it, in place of <see cref="CameraTrack"/>: it starts no process, and notes every
/// call in order (pause, resume, nudge, give up, the end, stop with its end, dispose), with whether the recording's sound was
/// still being written when the end and the stop came. A test can make its stop throw, or never return.
/// </summary>
/// <param name="start">Where the track begins.</param>
/// <param name="clock">The clock it was given.</param>
public sealed class FakeCameraTrack(CameraTrackStart start, Audio.Timeline.RecordingClock clock) : ICameraTrack
{
    private readonly Lock _gate = new();
    private readonly List<string> _calls = [];

    /// <summary>Whether the recording's sound was being written when the end came; null before it.</summary>
    public bool? SoundBeingWrittenAtEnd { get; private set; }

    /// <summary>True for a stop that never returns, as one waiting on a camera process stuck inside Windows.</summary>
    public bool StopHangs { get; set; }

    /// <inheritdoc />
    public void EndAt(TimeSpan endPosition)
    {
        SoundBeingWrittenAtEnd = Storage.ClassFolders.IsBeingWritten(new Storage.RecordingFiles(start.Folder, start.BaseName));
        Note($"end at {endPosition}");
    }

    /// <summary>Where the track begins.</summary>
    public CameraTrackStart Start => start;

    /// <summary>The clock it was given.</summary>
    public Audio.Timeline.RecordingClock Clock => clock;

    /// <summary>The calls so far, in order.</summary>
    public IReadOnlyList<string> Calls
    {
        get { lock (_gate) return [.. _calls]; }
    }

    /// <summary>Whether the recording's sound was being written when the stop came; null before the stop.</summary>
    public bool? SoundBeingWrittenAtStop { get; private set; }

    /// <summary>When set, the stop throws this.</summary>
    public Exception? StopFailure { get; set; }

    /// <inheritdoc />
    public CameraTrackState State { get; set; } = new("OBSBOT Meet Flip StreamCamera", "720p", true, false, 30, 0, 0, null, LaptopCameras.ObsbotContainer);

    /// <inheritdoc />
    public void Pause() => Note("pause");

    /// <inheritdoc />
    public void Resume() => Note("resume");

    /// <inheritdoc />
    public void Nudge() => Note("nudge");

    /// <inheritdoc />
    public void GiveUp(string why) => Note($"give up: {why}");

    /// <inheritdoc />
    public Task StopAsync(TimeSpan endPosition, CancellationToken cancellationToken)
    {
        SoundBeingWrittenAtStop = Storage.ClassFolders.IsBeingWritten(new Storage.RecordingFiles(start.Folder, start.BaseName));
        Note($"stop at {endPosition}");
        if (StopHangs) return new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously).Task;
        return StopFailure is { } failure ? Task.FromException(failure) : Task.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        Note("dispose");
        return ValueTask.CompletedTask;
    }

    /// <summary>Notes a call.</summary>
    private void Note(string call)
    {
        lock (_gate) _calls.Add(call);
    }
}

/// <summary>What a recording with the camera says about it, as a test sets it: the OBSBOT recording at 720p unless the test says otherwise; letting it go is noted and given up.</summary>
public sealed class FakeCameraRecording : Commands.ICameraRecording
{
    private readonly Lock _gate = new();
    private readonly List<string> _letGo = [];

    /// <inheritdoc />
    public CameraTrackState? Camera { get; set; } = new("OBSBOT Meet Flip StreamCamera", "720p", true, false, 29.9, 900, 3, null, LaptopCameras.ObsbotContainer);

    /// <summary>Why the camera was let go, each time it was.</summary>
    public IReadOnlyList<string> LetGo
    {
        get { lock (_gate) return [.. _letGo]; }
    }

    /// <inheritdoc />
    public void LetGoOfTheCamera(string why)
    {
        lock (_gate) _letGo.Add(why);
        Camera = Camera! with { GivenUp = true, Capturing = false, Concern = new CameraConcern(true, CameraTrack.GivenUpShort, why) };
    }
}

/// <summary>A camera recorder as a test plays it: ready, each start a <see cref="FakeCameraTrack"/>, or a failure the test sets.</summary>
public sealed class FakeCameraRecorder : ICameraRecorder
{
    private readonly Lock _gate = new();
    private readonly List<FakeCameraTrack> _tracks = [];

    /// <summary>When set, a start throws this.</summary>
    public Exception? StartFailure { get; set; }

    /// <summary>The tracks started, in order.</summary>
    public IReadOnlyList<FakeCameraTrack> Tracks
    {
        get { lock (_gate) return [.. _tracks]; }
    }

    /// <inheritdoc />
    public string? NotReady { get; set; }

    /// <inheritdoc />
    public ICameraTrack Start(CameraTrackStart start, Audio.Timeline.RecordingClock clock)
    {
        if (StartFailure is { } failure) throw failure;

        var track = new FakeCameraTrack(start, clock);
        lock (_gate) _tracks.Add(track);
        return track;
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

/// <summary>
/// Starts <see cref="FakeCameraProcess"/>es in place of real camera processes, and keeps each with its job id; with
/// <see cref="PlayEach"/>, each is played by a <see cref="PlayedCameraProcess"/> as a camera process would answer its
/// starter. A start can be made to fail, as Windows' would, or be held, as one Windows is slow to make. Safe to use from any
/// thread.
/// </summary>
public sealed class FakeCameraProcessStarter : ICameraProcessStarter
{
    private readonly Lock _gate = new();
    private readonly List<(string JobId, FakeCameraProcess Process)> _started = [];
    private readonly List<PlayedCameraProcess> _played = [];
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, ManualResetEventSlim> _held = new();
    private int _entered;

    /// <summary>How many starts have been asked for so far, held and failed ones included.</summary>
    public int StartsEntered => Volatile.Read(ref _entered);

    /// <summary>Holds the start numbered <paramref name="number"/> (0 for the first asked for) until <paramref name="gate"/> is set, as a start Windows is slow to make.</summary>
    public void HoldStart(int number, ManualResetEventSlim gate) => _held[number] = gate;

    /// <summary>True to play each process started: it reads its job, and ends at its starter's stop.</summary>
    public bool PlayEach { get; init; }

    /// <summary>When set, runs as each process starts, on the thread pool, with the process and its job id: such as the real camera host over the process's own end.</summary>
    public Func<FakeCameraProcess, string, Task>? Run { get; set; }

    /// <summary>When set, each start throws this, as Windows not starting the process would.</summary>
    public Exception? StartFailure { get; set; }

    /// <summary>The processes started, with their job ids, in order.</summary>
    public IReadOnlyList<(string JobId, FakeCameraProcess Process)> Started
    {
        get { lock (_gate) return [.. _started]; }
    }

    /// <summary>The processes played, in order, with <see cref="PlayEach"/>.</summary>
    public IReadOnlyList<PlayedCameraProcess> Played
    {
        get { lock (_gate) return [.. _played]; }
    }

    /// <inheritdoc />
    public ICameraProcess Start(string jobId)
    {
        if (!Commands.RunIds.IsWellFormed(jobId)) throw new ArgumentException($"\"{jobId}\" is not a job id.", nameof(jobId));
        var number = Interlocked.Increment(ref _entered) - 1;
        if (_held.TryRemove(number, out var held)) held.Wait();
        if (StartFailure is { } failure) throw failure;

        lock (_gate)
        {
            var process = new FakeCameraProcess(7001 + _started.Count);
            _started.Add((jobId, process));
            if (PlayEach) _played.Add(new PlayedCameraProcess(process));
            if (Run is { } run) _ = Task.Run(() => run(process, jobId));
            return process;
        }
    }
}

/// <summary>
/// A camera process played by the test, as its starter sees it: it reads its job, sends the lines the test asks for
/// (opened, status, problems), notes every line its starter sends (the clock's states among them), and ends at its
/// starter's stop, as a hold job does once it has let the camera go; a record job says its piece first, unless the test
/// makes it hang at the stop, as one stuck inside Windows would, until it is killed.
/// </summary>
public sealed class PlayedCameraProcess
{
    private readonly Lock _gate = new();
    private readonly List<string> _received = [];
    private readonly List<Audio.Timeline.ClockState> _clocks = [];
    private volatile CameraJob? _job;
    private volatile bool _stopReceived;
    private volatile CameraStop? _stop;
    private volatile CameraStop? _end;

    /// <summary>The types of the lines the starter sent after the job, in order.</summary>
    public IReadOnlyList<string> Received
    {
        get { lock (_gate) return [.. _received]; }
    }

    /// <summary>The clock's states the starter sent, in order.</summary>
    public IReadOnlyList<Audio.Timeline.ClockState> Clocks
    {
        get { lock (_gate) return [.. _clocks]; }
    }

    /// <summary>The stop the starter sent; null before it.</summary>
    public CameraStop? Stop => _stop;

    /// <summary>The sound's end the starter sent before its stop; null before it.</summary>
    public CameraStop? End => _end;

    /// <summary>True to answer the stop with nothing at all, as a process stuck inside Windows, until the starter kills it.</summary>
    public bool HangOnStop { get; set; }

    /// <summary>Sends the opened line of a record job: the size, the frames a second the piece keeps (0 when Windows did not say the camera's) and the encoder.</summary>
    public ValueTask OpenedAsync(int width = 1280, int height = 720, string encoder = "hardware encoder (Fake H.264 Encoder)", double framesPerSecond = 30) =>
        Process.ProcessEnd.SendAsync(CameraProtocol.Opened, new CameraOpened(Job?.CameraName ?? "camera", width, height, framesPerSecond, encoder), CameraJson.Default.CameraOpened,
            CancellationToken.None);

    /// <summary>Sends a record job's status line: <paramref name="frames"/> so far, whether the camera records and whether its picture is dark.</summary>
    public ValueTask RecordStatusAsync(long frames, bool capturing = true, bool dark = false, double framesPerSecond = 30, long dropped = 0) =>
        Process.ProcessEnd.SendAsync(CameraProtocol.Status, new CameraStatus(frames, dropped, capturing ? 0 : 4, dark, framesPerSecond, capturing), CameraJson.Default.CameraStatus,
            CancellationToken.None);

    /// <summary>Sends a finish job's progress line: <paramref name="percent"/> of the video made.</summary>
    public ValueTask ProgressAsync(int percent) =>
        Process.ProcessEnd.SendAsync(CameraProtocol.Progress, new CameraProgress(percent), CameraJson.Default.CameraProgress, CancellationToken.None);

    /// <summary>Sends a finish job's made line: the video at <paramref name="path"/>, <paramref name="lengthSeconds"/> long and <paramref name="bytes"/> in size.</summary>
    public ValueTask MadeAsync(string path, double lengthSeconds, long bytes) =>
        Process.ProcessEnd.SendAsync(CameraProtocol.Made, new CameraMade(path, lengthSeconds, bytes), CameraJson.Default.CameraMade, CancellationToken.None);

    /// <summary>Sends failed with <paramref name="why"/>.</summary>
    public ValueTask FailedAsync(string why) =>
        Process.ProcessEnd.SendAsync(CameraProtocol.Failed, new CameraFailed(why), CameraJson.Default.CameraFailed, CancellationToken.None);

    /// <summary>Starts playing <paramref name="process"/> on the thread pool.</summary>
    public PlayedCameraProcess(FakeCameraProcess process)
    {
        Process = process;
        Playing = Task.Run(PlayAsync);
    }

    /// <summary>The process played.</summary>
    public FakeCameraProcess Process { get; }

    /// <summary>The playing, which ends with the process.</summary>
    public Task Playing { get; }

    /// <summary>The job the starter sent; null until it came.</summary>
    public CameraJob? Job => _job;

    /// <summary>True once the starter's stop came.</summary>
    public bool StopReceived => _stopReceived;

    /// <summary>Sends a status line: <paramref name="frames"/> so far, the last <paramref name="sinceLastFrame"/> seconds ago.</summary>
    public ValueTask StatusAsync(long frames, double sinceLastFrame = 0) =>
        Process.ProcessEnd.SendAsync(CameraProtocol.Status, new CameraStatus(frames, 0, sinceLastFrame, false), CameraJson.Default.CameraStatus, CancellationToken.None);

    /// <summary>Sends a problem line.</summary>
    public ValueTask ProblemAsync(CameraProblemKind kind, string words) =>
        Process.ProcessEnd.SendAsync(CameraProtocol.Problem, new CameraProblem(kind, words), CameraJson.Default.CameraProblem, CancellationToken.None);

    /// <summary>
    /// Reads the job, then the starter's lines until its stop, which ends the process with 0, a record job's piece said
    /// first; with <see cref="HangOnStop"/>, the stop is noted and nothing answers it.
    /// </summary>
    private async Task PlayAsync()
    {
        try
        {
            _job = await Process.ReadJobAsync();
            while (await Process.ProcessEnd.ReceiveAsync(CancellationToken.None) is { } line)
            {
                lock (_gate)
                {
                    _received.Add(line.Type);
                    if (line.Type == CameraProtocol.Clock) _clocks.Add(System.Text.Json.JsonSerializer.Deserialize(line.Body, CameraJson.Default.ClockState)!);
                }

                if (line.Type == CameraProtocol.End) _end = System.Text.Json.JsonSerializer.Deserialize(line.Body, CameraJson.Default.CameraStop);
                if (line.Type != CameraProtocol.Stop) continue;

                _stop = System.Text.Json.JsonSerializer.Deserialize(line.Body, CameraJson.Default.CameraStop);
                _stopReceived = true;
                if (HangOnStop) continue;

                if (_job is { Kind: CameraJobKind.Record } job)
                {
                    var path = new Storage.RecordingFiles(job.Folder!, job.BaseName!).CameraPiece(TimeSpan.FromSeconds(job.StartSeconds));
                    await Process.ProcessEnd.SendAsync(CameraProtocol.Piece, new CameraPiece(path, job.StartSeconds, _stop?.EndSeconds ?? 0), CameraJson.Default.CameraPiece,
                        CancellationToken.None);
                    await Process.EndAsync(CameraProtocol.DoneExitCode);
                    return;
                }

                Process.Exit(CameraProtocol.DoneExitCode);
                return;
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // The process was ended or let go: nothing more to play.
        }
    }
}
