using System.Globalization;
using System.Runtime.Versioning;
using ClassScribe.Desktop;
using BitmapSize = global::Windows.Graphics.Imaging.BitmapSize;
using MediaCapture = global::Windows.Media.Capture.MediaCapture;
using MediaCaptureInitializationSettings = global::Windows.Media.Capture.MediaCaptureInitializationSettings;
using MediaCaptureMemoryPreference = global::Windows.Media.Capture.MediaCaptureMemoryPreference;
using MediaCaptureSharingMode = global::Windows.Media.Capture.MediaCaptureSharingMode;
using MediaEncodingSubtypes = global::Windows.Media.MediaProperties.MediaEncodingSubtypes;
using MediaFrameArrivedEventArgs = global::Windows.Media.Capture.Frames.MediaFrameArrivedEventArgs;
using MediaFrameFormat = global::Windows.Media.Capture.Frames.MediaFrameFormat;
using MediaFrameReader = global::Windows.Media.Capture.Frames.MediaFrameReader;
using MediaFrameReaderAcquisitionMode = global::Windows.Media.Capture.Frames.MediaFrameReaderAcquisitionMode;
using MediaFrameSource = global::Windows.Media.Capture.Frames.MediaFrameSource;
using MediaFrameSourceGroup = global::Windows.Media.Capture.Frames.MediaFrameSourceGroup;
using MediaFrameSourceKind = global::Windows.Media.Capture.Frames.MediaFrameSourceKind;
using StreamingCaptureMode = global::Windows.Media.Capture.StreamingCaptureMode;

namespace ClassScribe.Camera.Windows;

/// <summary>Makes captures of cameras over WinRT's MediaCapture (see <see cref="WindowsCameraCapture"/>).</summary>
[SupportedOSPlatform("windows10.0.19041.0")]
public sealed class WindowsCameraCaptureFactory : ICameraCaptureFactory
{
    /// <inheritdoc />
    public ICameraCapture Create(string cameraId, string cameraName) => new WindowsCameraCapture(cameraId, cameraName);
}

/// <summary>
/// One camera through WinRT's MediaCapture in frame-reader mode, only ever shared (SharedReadOnly): the members
/// <see cref="CameraRoute"/> calls, in its order, on the camera process's STA thread. Making it touches no device;
/// <see cref="InitializeShared"/> is the open, and in a test run it refuses before anything is asked of Windows
/// (<see cref="DeviceGuards"/>), naming FakeCameraCapture. The source group is found by the camera's id among Windows'
/// lists and never left out, so Windows' default camera is never opened in its place. For the hold and the check no memory
/// preference is given and no pixel is read: each frame is taken and released at once; for a record job the frames come in
/// CPU memory, and each is handed over with its pixels before it is released.
/// </summary>
/// <param name="cameraId">The camera's device interface id, as the camera list gives it.</param>
/// <param name="cameraName">The camera's name, for the words of a refusal.</param>
[SupportedOSPlatform("windows10.0.19041.0")]
public sealed class WindowsCameraCapture(string cameraId, string cameraName) : ICameraCapture
{
    private readonly MediaCapture _capture = new();
    private MediaFrameSourceGroup? _group;
    private MediaFrameSource? _source;
    private MediaFrameReader? _reader;
    private Action? _onFrame;
    private Action<ICameraFrame>? _onPixels;
    private Action<string>? _onEvent;
    private int _handlerFailures;
    private int _disposed;

    /// <inheritdoc />
    public void AddFailed(Action<string> onFailed)
    {
        _onEvent = onFailed;
        _capture.Failed += (_, e) => onFailed($"MediaCapture.Failed: {CameraHresults.Describe(unchecked((int)e.Code), e.Message)}");
    }

    /// <inheritdoc />
    public void FindSourceGroup()
    {
        var groups = MediaFrameSourceGroup.FindAllAsync().AsTask().GetAwaiter().GetResult();
        _group = groups.FirstOrDefault(group => group.SourceInfos.Any(source =>
                     source.SourceKind == MediaFrameSourceKind.Color && string.Equals(source.DeviceInformation?.Id, cameraId, StringComparison.OrdinalIgnoreCase)))
                 ?? throw new InvalidOperationException($"Windows no longer lists the camera \"{cameraName}\"; is it unplugged? Windows' default camera is never opened in its place");
    }

    /// <inheritdoc />
    public void InitializeShared(bool cpuFrames)
    {
        DeviceGuards.RefuseInTests("the real opening of a camera (MediaCapture.InitializeAsync)", "FakeCameraCapture");
        var group = _group ?? throw new InvalidOperationException("The camera's source group was not found, so it is not opened: Windows' default camera is never opened in its place");
        _capture.InitializeAsync(SettingsFor(group, cpuFrames)).AsTask().GetAwaiter().GetResult();
    }

    /// <summary>
    /// What InitializeAsync is asked for: the camera's source group (left unset when <paramref name="group"/> is null, as in a
    /// test, which needs no device), shared and read only, never exclusive control (Gulzar's decision of 21 October: his face
    /// sometimes shows in the class app, which must keep the camera), video alone, and the frames in CPU memory when
    /// <paramref name="cpuFrames"/>, else wherever Windows likes. Touches no device.
    /// </summary>
    internal static MediaCaptureInitializationSettings SettingsFor(MediaFrameSourceGroup? group, bool cpuFrames)
    {
        var settings = new MediaCaptureInitializationSettings
        {
            SharingMode = MediaCaptureSharingMode.SharedReadOnly,
            StreamingCaptureMode = StreamingCaptureMode.Video,
            MemoryPreference = cpuFrames ? MediaCaptureMemoryPreference.Cpu : MediaCaptureMemoryPreference.Auto
        };
        if (group is not null) settings.SourceGroup = group;
        return settings;
    }

    /// <inheritdoc />
    public void AddStreamStateChanged(Action<string> onEvent) =>
        _capture.CameraStreamStateChanged += (sender, _) => onEvent($"The camera's stream: {sender.CameraStreamState}");

    /// <inheritdoc />
    public void AddExclusiveControlChanged(Action<string> onEvent) =>
        _capture.CaptureDeviceExclusiveControlStatusChanged += (_, e) => onEvent($"Another app's exclusive control of the camera: {e.Status}");

    /// <inheritdoc />
    public string FindColourSource()
    {
        var sources = _capture.FrameSources.Values.Where(source => source.Info.SourceKind == MediaFrameSourceKind.Color).ToList();
        _source = sources.FirstOrDefault(source => string.Equals(source.Info.DeviceInformation?.Id, cameraId, StringComparison.OrdinalIgnoreCase))
                  ?? sources.FirstOrDefault()
                  ?? throw new InvalidOperationException($"The camera \"{cameraName}\" offers no colour source");
        return _source.CurrentFormat is { } format ? Describe(format) : "not said by Windows";
    }

    /// <inheritdoc />
    public string CreateReader(CameraSize? convertTo)
    {
        var source = _source ?? throw new InvalidOperationException("The camera's colour source was not found");
        if (convertTo is { } size)
        {
            _reader = _capture.CreateFrameReaderAsync(source, MediaEncodingSubtypes.Nv12, new BitmapSize { Width = (uint)size.Width, Height = (uint)size.Height })
                .AsTask().GetAwaiter().GetResult();
            return string.Create(CultureInfo.InvariantCulture, $"NV12 {size.Width}x{size.Height}");
        }

        _reader = _capture.CreateFrameReaderAsync(source).AsTask().GetAwaiter().GetResult();
        return "the camera's own format, nothing converted";
    }

    /// <inheritdoc />
    public void UseRealtime() => Reader.AcquisitionMode = MediaFrameReaderAcquisitionMode.Realtime;

    /// <inheritdoc />
    public void AddFrameArrived(Action onFrame)
    {
        _onFrame = onFrame;
        Reader.FrameArrived += OnFrameArrived;
    }

    /// <inheritdoc />
    public void AddFrameReader(Action<ICameraFrame> onFrame)
    {
        _onPixels = onFrame;
        Reader.FrameArrived += OnFrameWithPixels;
    }

    /// <inheritdoc />
    public string StartReader() => Reader.StartAsync().AsTask().GetAwaiter().GetResult().ToString();

    /// <inheritdoc />
    public void StopReader()
    {
        if (_reader is not { } reader) return;
        if (!reader.StopAsync().AsTask().Wait(TimeSpan.FromSeconds(5)))
            throw new TimeoutException("MediaFrameReader.StopAsync did not return within 5 seconds");
    }

    /// <summary>Lets the camera go: the frame handler removed, the reader disposed, then MediaCapture. Safe to call twice.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        if (_reader is { } reader)
        {
            reader.FrameArrived -= OnFrameArrived;
            reader.FrameArrived -= OnFrameWithPixels;
            reader.Dispose();
        }

        _capture.Dispose();
    }

    /// <summary>The reader, made by <see cref="CreateReader"/>.</summary>
    private MediaFrameReader Reader => _reader ?? throw new InvalidOperationException("The frame reader was not made");

    /// <summary>
    /// A frame: taken and released at once, its pixels never touched, then counted. Runs on a thread of Windows' own, so
    /// nothing may escape it.
    /// </summary>
    private void OnFrameArrived(MediaFrameReader sender, MediaFrameArrivedEventArgs args)
    {
        try
        {
            using var frame = sender.TryAcquireLatestFrame();
            if (frame is not null) _onFrame?.Invoke();
        }
        catch (Exception ex)
        {
            // Deliberately not rethrown: an exception on Windows' frame thread would end the camera process; the first one
            // is reported through the events, and a frame that cannot be taken is simply not counted.
            if (Interlocked.Increment(ref _handlerFailures) == 1)
                _onEvent?.Invoke($"A frame could not be taken: {CameraHresults.Describe(ex.HResult, ex.Message.Split('\n')[0])}");
        }
    }

    /// <summary>
    /// A frame for a record job: taken, handed over with its stamp and its pixels (in CPU memory, NV12 at the reader's size),
    /// then released. Runs on a thread of Windows' own, so nothing may escape it.
    /// </summary>
    private void OnFrameWithPixels(MediaFrameReader sender, MediaFrameArrivedEventArgs args)
    {
        try
        {
            using var frame = sender.TryAcquireLatestFrame();
            if (frame is null) return;

            using var bitmap = frame.VideoMediaFrame?.SoftwareBitmap;
            _onPixels?.Invoke(new WindowsCameraFrame(frame.SystemRelativeTime?.Ticks, bitmap));
        }
        catch (Exception ex)
        {
            // Deliberately not rethrown: an exception on Windows' frame thread would end the camera process; the first one
            // is reported through the events, and a frame that cannot be taken is simply not written.
            if (Interlocked.Increment(ref _handlerFailures) == 1)
                _onEvent?.Invoke($"A frame could not be taken: {CameraHresults.Describe(ex.HResult, ex.Message.Split('\n')[0])}");
        }
    }

    /// <summary>A frame format in words, such as MJPG 1920x1080 at 30 frames a second (see <see cref="CameraFormats.Words"/>).</summary>
    private static string Describe(MediaFrameFormat format)
    {
        var rate = format.FrameRate is { Denominator: > 0 } frameRate ? (double)frameRate.Numerator / frameRate.Denominator : 0;
        return CameraFormats.Words(format.Subtype, format.VideoFormat?.Width, format.VideoFormat?.Height, rate);
    }
}
