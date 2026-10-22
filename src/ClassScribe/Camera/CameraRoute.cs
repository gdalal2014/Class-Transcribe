using System.Globalization;

namespace ClassScribe.Camera;

/// <summary>
/// One named call on the camera's route (MediaCapture and its frame reader), timed: OK, failed with its HRESULT and
/// Windows' words, or a note of a call not made and why. A required call that fails ends the open; an optional one (an
/// event that only reports) is recorded and passed over.
/// </summary>
/// <param name="Order">Its place on the route, from 1.</param>
/// <param name="Name">The call, such as MediaCapture.InitializeAsync (SharedReadOnly, video only).</param>
/// <param name="Outcome">OK, failed, or the note's words.</param>
/// <param name="Took">How long it took; zero for a note.</param>
/// <param name="Error">Why it failed, in words: its HRESULT with its name and Windows' own words, or the exception's words; null when it did not fail.</param>
/// <param name="HResult">The HRESULT it failed with; null when it did not fail, or failed without one.</param>
/// <param name="Required">False for a call the open goes on without.</param>
public sealed record CameraCall(int Order, string Name, string Outcome, TimeSpan Took, string? Error = null, int? HResult = null, bool Required = true)
{
    /// <summary>The outcome of a call that worked.</summary>
    public const string Ok = "OK";

    /// <summary>The outcome of a call that failed.</summary>
    public const string FailedWord = "failed";

    /// <summary>True when the call failed.</summary>
    public bool Failed => Outcome == FailedWord;

    /// <summary>
    /// The call as one line: <c>4. MediaCapture.InitializeAsync (SharedReadOnly, video only): OK (412 ms)</c>, or with its
    /// failure: <c>4. ...: failed (35 ms): 0x80070005 E_ACCESSDENIED (Windows: "Access is denied.")</c>.
    /// </summary>
    public string Text =>
        string.Create(CultureInfo.InvariantCulture, $"{Order}. {Name}: {Outcome}{(Took > TimeSpan.Zero ? $" ({Took.TotalMilliseconds:0} ms)" : "")}")
        + (Failed && Error is not null ? $": {Error}" : "")
        + (Failed && !Required ? " (optional: passed over)" : "");
}

/// <summary>A required call on the camera's route failed: which call, and why in words, its HRESULT kept.</summary>
public sealed class CameraCallFailedException : Exception
{
    /// <summary>The call <paramref name="call"/> failed as <paramref name="error"/> says, with <paramref name="hresult"/> when it had one.</summary>
    public CameraCallFailedException(string call, string error, int? hresult, Exception? inner = null)
        : base($"{call} failed: {error}", inner)
    {
        Call = call;
        Error = error;
        if (hresult is { } code) HResult = code;
        Code = hresult;
    }

    /// <summary>The call that failed.</summary>
    public string Call { get; }

    /// <summary>Why, in words.</summary>
    public string Error { get; }

    /// <summary>The HRESULT it failed with; null when it failed without one.</summary>
    public int? Code { get; }
}

/// <summary>
/// The calls of one opening of a camera, in the order they were made (the probe fix's rule 4): every call on the route goes
/// through it, so a failure in a class names its call, with its HRESULT, and what came before it. Safe to read from any
/// thread.
/// </summary>
/// <param name="time">The clock each call is timed on.</param>
public sealed class CameraCallLog(TimeProvider time)
{
    private readonly Lock _gate = new();
    private readonly List<CameraCall> _calls = [];
    private string? _inProgress;

    /// <summary>The call under way now, named in the report when it never returns; null between calls.</summary>
    public string? InProgress
    {
        get { lock (_gate) return _inProgress; }
    }

    /// <summary>The calls so far, in order.</summary>
    public IReadOnlyList<CameraCall> Calls
    {
        get { lock (_gate) return [.. _calls]; }
    }

    /// <summary>The first required call that failed; null while none has.</summary>
    public CameraCall? FirstFailure
    {
        get { lock (_gate) return _calls.FirstOrDefault(call => call.Failed && call.Required); }
    }

    /// <summary>Runs a call the open cannot go on without; a failure is recorded and thrown as <see cref="CameraCallFailedException"/>, naming the call.</summary>
    public T Required<T>(string name, Func<T> call)
    {
        var began = Begin(name);
        try
        {
            var result = call();
            Add(name, CameraCall.Ok, began, null, null, required: true);
            return result;
        }
        catch (Exception ex) when (ex is not CameraCallFailedException)
        {
            var (error, hresult) = Describe(ex);
            Add(name, CameraCall.FailedWord, began, error, hresult, required: true);
            throw new CameraCallFailedException(name, error, hresult, ex);
        }
    }

    /// <summary>A required call with no result.</summary>
    public void Required(string name, Action call) => Required(name, () =>
    {
        call();
        return 0;
    });

    /// <summary>
    /// Runs a call the open can do without, such as an event that only reports: a failure is recorded with its HRESULT and
    /// passed over, and false returned.
    /// </summary>
    public bool Optional(string name, Action call)
    {
        var began = Begin(name);
        try
        {
            call();
            Add(name, CameraCall.Ok, began, null, null, required: false);
            return true;
        }
        catch (Exception ex)
        {
            // Deliberately not rethrown: an optional call only reports (an event) or lets go; the open goes on without it,
            // or the camera is let go all the same, and the failure is kept on the route with its HRESULT for the report.
            var (error, hresult) = Describe(ex);
            Add(name, CameraCall.FailedWord, began, error, hresult, required: false);
            return false;
        }
    }

    /// <summary>
    /// Records an outcome that is not an exception: a call not made and why, or a status a call returned; a failed note
    /// counts as a required call that failed.
    /// </summary>
    public void Note(string name, string words, bool failed = false)
    {
        lock (_gate)
        {
            _calls.Add(failed
                ? new CameraCall(_calls.Count + 1, name, CameraCall.FailedWord, TimeSpan.Zero, words)
                : new CameraCall(_calls.Count + 1, name, words, TimeSpan.Zero));
        }
    }

    /// <summary>
    /// Why <paramref name="ex"/> failed, in words: for a failure Windows reported, its HRESULT with its name and Windows'
    /// words (WinRT hands an HRESULT over as an exception of a matching type, E_ACCESSDENIED as an
    /// UnauthorizedAccessException, keeping the code); for one of .NET's own (facility 0x13), such as ClassScribe's own
    /// refusal of a camera that is not listed, its type and words alone.
    /// </summary>
    public static (string Error, int? HResult) Describe(Exception ex)
    {
        var words = FirstLine(ex.Message);
        return (ex.HResult & unchecked((int)0xFFFF0000)) == unchecked((int)0x80130000)
            ? ($"{ex.GetType().Name}: {words}", null)
            : (CameraHresults.Describe(ex.HResult, words), ex.HResult);
    }

    /// <summary>The first line of <paramref name="text"/>, trimmed.</summary>
    private static string FirstLine(string text) => text.Split('\n')[0].Trim();

    /// <summary>Marks <paramref name="name"/> as under way and returns when it began.</summary>
    private long Begin(string name)
    {
        lock (_gate) _inProgress = name;
        return time.GetTimestamp();
    }

    /// <summary>Records a call that has returned, and that none is under way now.</summary>
    private void Add(string name, string outcome, long began, string? error, int? hresult, bool required)
    {
        var took = time.GetElapsedTime(began);
        lock (_gate)
        {
            _inProgress = null;
            _calls.Add(new CameraCall(_calls.Count + 1, name, outcome, took, error, hresult, required));
        }
    }
}

/// <summary>
/// An HRESULT in words: its hex value, its name where it is known for certain (the probe's table, checked against
/// winerror.h and mferror.h), and Windows' own words for it, as the call that failed gave them.
/// </summary>
public static class CameraHresults
{
    /// <summary>MF_E_NOT_INITIALIZED: what a MediaCapture refuses with when a member that needs the device is touched before InitializeAsync (finding 7).</summary>
    public const int NotInitialized = unchecked((int)0xC00D36B6);

    /// <summary>E_ACCESSDENIED: Windows' camera privacy switch, or a security program, refused the camera.</summary>
    public const int AccessDenied = unchecked((int)0x80070005);

    /// <summary>HRESULT_FROM_WIN32(ERROR_DEVICE_NOT_CONNECTED): the camera is not connected.</summary>
    public const int DeviceNotConnected = unchecked((int)0x8007048F);

    /// <summary>HRESULT_FROM_WIN32(ERROR_SHARING_VIOLATION): another program holds the camera.</summary>
    public const int SharingViolation = unchecked((int)0x80070020);

    /// <summary>HRESULT_FROM_WIN32(ERROR_BUSY): the camera is busy.</summary>
    public const int Busy = unchecked((int)0x800700AA);

    /// <summary>The name of <paramref name="hresult"/> in winerror.h or mferror.h, where it is known for certain; null otherwise.</summary>
    public static string? Name(int hresult) => unchecked((uint)hresult) switch
    {
        0x80004001 => "E_NOTIMPL",
        0x80004002 => "E_NOINTERFACE",
        0x80004005 => "E_FAIL",
        0x8000000E => "E_ILLEGAL_METHOD_CALL",
        0x80070005 => "E_ACCESSDENIED",
        0x80070057 => "E_INVALIDARG",
        0x8007000E => "E_OUTOFMEMORY",
        0x80070015 => "HRESULT_FROM_WIN32(ERROR_NOT_READY)",
        0x80070020 => "HRESULT_FROM_WIN32(ERROR_SHARING_VIOLATION)",
        0x800700AA => "HRESULT_FROM_WIN32(ERROR_BUSY)",
        0x8007048F => "HRESULT_FROM_WIN32(ERROR_DEVICE_NOT_CONNECTED)",
        0x80040154 => "REGDB_E_CLASSNOTREG",
        0x800401F0 => "CO_E_NOTINITIALIZED",
        0x8001010E => "RPC_E_WRONG_THREAD",
        0xC00D36B0 => "MF_E_PLATFORM_NOT_INITIALIZED",
        0xC00D36B2 => "MF_E_INVALIDREQUEST",
        0xC00D36B4 => "MF_E_INVALIDMEDIATYPE",
        0xC00D36B5 => "MF_E_NOTACCEPTING",
        0xC00D36B6 => "MF_E_NOT_INITIALIZED",
        _ => null
    };

    /// <summary>
    /// <c>0xC00D36B6 MF_E_NOT_INITIALIZED (Windows: "This object needs to be initialized...")</c>: the code, its name when
    /// known, and <paramref name="windowsWords"/> when there are any.
    /// </summary>
    public static string Describe(int hresult, string? windowsWords) =>
        string.Create(CultureInfo.InvariantCulture, $"0x{hresult:X8}")
        + (Name(hresult) is { } name ? $" {name}" : "")
        + (string.IsNullOrWhiteSpace(windowsWords) ? "" : $" (Windows: \"{windowsWords.Trim()}\")");
}

/// <summary>
/// The members of WinRT's MediaCapture and its frame reader that a shared open uses, behind a seam, one member a step, so
/// that the order of the calls is ClassScribe's (<see cref="CameraRoute"/>) and is tested against a fake that refuses what
/// Windows refuses before InitializeAsync. There is no way to ask for exclusive control: the camera is only ever shared
/// (Gulzar's decision of 21 October; his face sometimes shows in the class app). Each member is called on the camera
/// process's own STA thread; disposing lets the camera go.
/// </summary>
public interface ICameraCapture : IDisposable
{
    /// <summary>MediaCapture.Failed += (allowed before InitializeAsync).</summary>
    void AddFailed(Action<string> onFailed);

    /// <summary>
    /// Finds the camera's frame source group among Windows' lists; throws when Windows no longer lists it (unplugged), so
    /// that Windows' default camera is never opened in its place (the probe fix's rule 3).
    /// </summary>
    void FindSourceGroup();

    /// <summary>
    /// MediaCapture.InitializeAsync with the camera's source group, SharedReadOnly, video only: with no memory preference,
    /// or with CPU memory when <paramref name="cpuFrames"/>, so that a record job's frames come as pixels it can copy.
    /// </summary>
    void InitializeShared(bool cpuFrames);

    /// <summary>MediaCapture.CameraStreamStateChanged += (only reports).</summary>
    void AddStreamStateChanged(Action<string> onEvent);

    /// <summary>MediaCapture.CaptureDeviceExclusiveControlStatusChanged += (only reports; refused before InitializeAsync).</summary>
    void AddExclusiveControlChanged(Action<string> onEvent);

    /// <summary>MediaCapture.FrameSources: the camera's colour source (refused before InitializeAsync); returns its format as it runs, in words.</summary>
    string FindColourSource();

    /// <summary>
    /// MediaCapture.CreateFrameReaderAsync on the colour source: with no subtype and no size when <paramref name="convertTo"/>
    /// is null, so that Windows converts nothing, else NV12 at that size. Returns what the reader hands over, in words.
    /// </summary>
    string CreateReader(CameraSize? convertTo);

    /// <summary>MediaFrameReader.AcquisitionMode = Realtime: frames are dropped rather than queued.</summary>
    void UseRealtime();

    /// <summary>MediaFrameReader.FrameArrived +=: each frame is taken and released at once, its pixels never touched, then <paramref name="onFrame"/> is called.</summary>
    void AddFrameArrived(Action onFrame);

    /// <summary>
    /// MediaFrameReader.FrameArrived +=, for a record job: each frame is taken, handed to <paramref name="onFrame"/> with its
    /// stamp and its pixels while Windows holds it, then released.
    /// </summary>
    void AddFrameReader(Action<ICameraFrame> onFrame);

    /// <summary>MediaFrameReader.StartAsync; returns its status by name, Success when frames will come.</summary>
    string StartReader();

    /// <summary>MediaFrameReader.StopAsync.</summary>
    void StopReader();
}

/// <summary>Makes the capture of one camera, a new MediaCapture that has not touched the camera yet.</summary>
public interface ICameraCaptureFactory
{
    /// <summary>A capture of the camera Windows knows as <paramref name="cameraId"/>, named <paramref name="cameraName"/> in its words.</summary>
    ICameraCapture Create(string cameraId, string cameraName);
}

/// <summary>A camera opened shared and streaming: the capture, the calls that opened it, and what it streams.</summary>
/// <param name="Capture">The capture, to let go with <see cref="CameraRoute.Close"/>.</param>
/// <param name="Format">The colour source's format as it runs, in words: a shared open cannot change it.</param>
/// <param name="Reader">What the frame reader hands over, in words.</param>
public sealed record CameraOpening(ICameraCapture Capture, string Format, string Reader);

/// <summary>
/// The route of a shared open, in the order Microsoft documents and Windows enforces (ROOT-CAUSE.md beside the camera
/// probe, finding 7 of 21 October): create the MediaCapture; register Failed; find the camera's source group, never
/// letting a missing one through; call InitializeAsync, SharedReadOnly; and only after it has completed register the
/// events that only report (stream state, exclusive control), each optional, and read FrameSources. Before
/// InitializeAsync those are refused with 0xC00D36B6 MF_E_NOT_INITIALIZED, which is what every open of the probe's first
/// evening hit: it registered the exclusive-control event first. Then the frame reader: no format is set (a shared open
/// cannot set one), Realtime, each frame released unread, and StartAsync, whose status must be Success. Every call goes
/// through the <see cref="CameraCallLog"/>, so a failure names its call and HRESULT.
/// </summary>
public static class CameraRoute
{
    /// <summary>The first call: a new MediaCapture, which touches no device.</summary>
    public const string Create = "new MediaCapture()";

    /// <summary>The one event registered before InitializeAsync.</summary>
    public const string Failed = "MediaCapture.Failed +=";

    /// <summary>The camera's frame source group, found by its id among Windows' lists.</summary>
    public const string Source = "MediaFrameSourceGroup (the camera's source group)";

    /// <summary>The open itself.</summary>
    public const string Initialize = "MediaCapture.InitializeAsync (SharedReadOnly, video only)";

    /// <summary>The open itself, for a record job, whose frames come in CPU memory to be copied.</summary>
    public const string InitializeForPixels = "MediaCapture.InitializeAsync (SharedReadOnly, video only, CPU memory)";

    /// <summary>An event that only reports, registered after InitializeAsync.</summary>
    public const string StreamState = "MediaCapture.CameraStreamStateChanged +=";

    /// <summary>An event that only reports, refused before InitializeAsync (finding 7's call), registered after it.</summary>
    public const string Exclusive = "MediaCapture.CaptureDeviceExclusiveControlStatusChanged +=";

    /// <summary>The colour source, refused before InitializeAsync.</summary>
    public const string FrameSources = "MediaCapture.FrameSources (the colour source)";

    /// <summary>The format call a shared open never makes.</summary>
    public const string SetFormat = "MediaFrameSource.SetFormatAsync";

    /// <summary>The note in place of <see cref="SetFormat"/>.</summary>
    public const string SetFormatNotCalled = "not called: SharedReadOnly cannot change the camera's format, so the camera is taken as it runs";

    /// <summary>The frame reader.</summary>
    public const string Reader = "MediaCapture.CreateFrameReaderAsync";

    /// <summary>Frames dropped rather than queued.</summary>
    public const string Realtime = "MediaFrameReader.AcquisitionMode = Realtime";

    /// <summary>The frame handler.</summary>
    public const string FrameArrived = "MediaFrameReader.FrameArrived +=";

    /// <summary>The frame handler of a record job, which copies each frame's pixels before it is released.</summary>
    public const string FrameArrivedForPixels = "MediaFrameReader.FrameArrived += (each frame's pixels copied, then released)";

    /// <summary>The start of the frames.</summary>
    public const string Start = "MediaFrameReader.StartAsync";

    /// <summary>The status StartAsync returns when frames will come.</summary>
    public const string Success = "Success";

    /// <summary>Letting the reader go.</summary>
    public const string Stop = "MediaFrameReader.StopAsync (letting go)";

    /// <summary>Letting the camera go.</summary>
    public const string Dispose = "MediaCapture.Dispose (letting go)";

    /// <summary>
    /// The members measured on this laptop on 21 October (an uninitialised MediaCapture, no device touched) as refused with
    /// MF_E_NOT_INITIALIZED before InitializeAsync. The fake refuses the same ones, and a test measures the real object.
    /// </summary>
    public static IReadOnlyList<string> NeedInitialization { get; } =
        ["CaptureDeviceExclusiveControlStatusChanged +=", "ThermalStatusChanged +=", "FocusChanged +=", "PhotoConfirmationCaptured +=",
         "FrameSources", "MediaCaptureSettings", "VideoDeviceController", "AudioDeviceController"];

    /// <summary>
    /// Opens the camera <paramref name="cameraId"/> shared, through <paramref name="factory"/>, in the documented order, each
    /// call recorded in <paramref name="log"/>: with the reader converting nothing when <paramref name="convertTo"/> is null
    /// (the hold), else to NV12 at that size (the camera check and a lesson). <paramref name="onEvent"/> hears what
    /// MediaCapture reports (Failed, the stream's state, exclusive control); <paramref name="onFrame"/> is called for each
    /// frame, released unread, unless <paramref name="onPixels"/> is given: then the frames come in CPU memory and each is
    /// handed to it with its stamp and pixels before it is released (a record job). Throws
    /// <see cref="CameraCallFailedException"/> naming the call that failed, after letting the camera go.
    /// </summary>
    public static CameraOpening Open(ICameraCaptureFactory factory, string cameraId, string cameraName, CameraSize? convertTo, CameraCallLog log,
        Action<string> onEvent, Action onFrame, Action<ICameraFrame>? onPixels = null)
    {
        var capture = log.Required(Create, () => factory.Create(cameraId, cameraName));
        try
        {
            log.Required(Failed, () => capture.AddFailed(onEvent));
            log.Required(Source, capture.FindSourceGroup);
            log.Required(onPixels is null ? Initialize : InitializeForPixels, () => capture.InitializeShared(cpuFrames: onPixels is not null));

            // Only after InitializeAsync has completed: these only report, so a refusal is recorded and passed over.
            log.Optional(StreamState, () => capture.AddStreamStateChanged(onEvent));
            log.Optional(Exclusive, () => capture.AddExclusiveControlChanged(onEvent));

            var format = log.Required(FrameSources, capture.FindColourSource);
            log.Note(SetFormat, SetFormatNotCalled);
            var readerName = convertTo is { } size
                ? string.Create(CultureInfo.InvariantCulture, $"{Reader} (NV12 {size.Width}x{size.Height})")
                : $"{Reader} (as the camera sends it, nothing converted)";
            var reader = log.Required(readerName, () => capture.CreateReader(convertTo));
            log.Required(Realtime, capture.UseRealtime);
            if (onPixels is { } pixels) log.Required(FrameArrivedForPixels, () => capture.AddFrameReader(pixels));
            else log.Required(FrameArrived, () => capture.AddFrameArrived(onFrame));
            var status = log.Required(Start, capture.StartReader);
            if (status != Success)
            {
                var words = $"{status}, not {Success}: no frames will come";
                log.Note($"{Start}'s status", words, failed: true);
                throw new CameraCallFailedException(Start, words, null);
            }

            return new CameraOpening(capture, format, reader);
        }
        catch
        {
            Close(capture, log, readerStarted: false);
            throw;
        }
    }

    /// <summary>Lets the camera go: the reader stopped when it was started, then the capture disposed, each call recorded and none fatal.</summary>
    public static void Close(ICameraCapture capture, CameraCallLog log, bool readerStarted = true)
    {
        if (readerStarted) log.Optional(Stop, capture.StopReader);
        log.Optional(Dispose, capture.Dispose);
    }
}
