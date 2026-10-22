using Microsoft.Extensions.Logging;
using DeviceClass = global::Windows.Devices.Enumeration.DeviceClass;
using DeviceInformation = global::Windows.Devices.Enumeration.DeviceInformation;
using FrameSourceGroup = global::Windows.Media.Capture.Frames.MediaFrameSourceGroup;
using FrameSourceKind = global::Windows.Media.Capture.Frames.MediaFrameSourceKind;
using Panel = global::Windows.Devices.Enumeration.Panel;

namespace ClassScribe.Camera.Windows;

/// <summary>
/// Lists the colour cameras through Windows' own device lists (teach-video 6.4): the video capture devices
/// (DeviceInformation.FindAllAsync, with each one's container and enclosure location) and the frame source groups
/// (MediaFrameSourceGroup.FindAllAsync), whose colour sources tell a colour camera from an infrared one. Neither opens a
/// camera, so no light comes on and no security program is asked. Windows older than 10 version 2004 is answered with a
/// list that says so, before any of Windows' camera API is touched. A read that fails, whatever Windows raises, is answered
/// with a list that says why, as the microphone readers answer a failed read; only the caller's own cancel is thrown.
/// </summary>
public sealed class WindowsCameraProvider : ICameraProvider
{
    /// <summary>The property that names the physical device a device interface belongs to.</summary>
    private const string ContainerIdProperty = "System.Devices.ContainerId";

    /// <summary>What the list says on Windows older than 10 version 2004, where the camera API ClassScribe uses is missing.</summary>
    public const string TooOld = "The camera needs Windows 10 version 2004 (build 19041) or later";

    private readonly ILogger<WindowsCameraProvider> _logger;

    /// <summary>Reads the cameras: Windows' own lists, or a test's stand-in.</summary>
    private readonly Func<CancellationToken, Task<IReadOnlyList<CameraDevice>>> _read;

    /// <summary>A provider over Windows' own lists.</summary>
    public WindowsCameraProvider(ILogger<WindowsCameraProvider> logger) : this(logger, read: null)
    {
    }

    /// <summary>A provider over <paramref name="read"/> in place of Windows' own lists, for the tests of a read that fails; Windows' lists when it is null.</summary>
    internal WindowsCameraProvider(ILogger<WindowsCameraProvider> logger, Func<CancellationToken, Task<IReadOnlyList<CameraDevice>>>? read)
    {
        _logger = logger;
        _read = read ?? ReadFromWindowsAsync;
    }

    /// <inheritdoc />
    public async Task<CameraList> GetCamerasAsync(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041)) return new CameraList([], $"{TooOld}; this is Windows {Environment.OSVersion.Version}");

        try
        {
            return new CameraList(await _read(cancellationToken));
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // Deliberately not rethrown: a list that cannot be read is a list that says why, which the caller shows, as the
            // microphone readers answer a failed read. WinRT hands an HRESULT over as whatever type it maps to (a
            // FileNotFoundException, an OperationCanceledException for ERROR_CANCELLED, an ObjectDisposedException, ...), and
            // the webcam hold lists the cameras inside the microphone's pick, so every one of them is answered here; only
            // the caller's own cancel goes on. Nothing was opened, and nothing else depends on it.
            _logger.LogDebug(ex, "Could not list the cameras (0x{Code:X8})", ex.HResult);
            return new CameraList([], $"Windows did not list the cameras: {ex.Message.Trim()} (0x{ex.HResult:X8})");
        }
    }

    /// <summary>The colour cameras from Windows' own lists: the video capture devices and the frame source groups' colour sources.</summary>
    private async Task<IReadOnlyList<CameraDevice>> ReadFromWindowsAsync(CancellationToken cancellationToken)
    {
        var selector = DeviceInformation.GetAqsFilterFromDeviceClass(DeviceClass.VideoCapture);
        var devices = await DeviceInformation.FindAllAsync(selector, [ContainerIdProperty]).AsTask(cancellationToken);
        var groups = await FrameSourceGroup.FindAllAsync().AsTask(cancellationToken);

        var colour = groups
            .SelectMany(group => group.SourceInfos)
            .Where(source => source.SourceKind == FrameSourceKind.Color)
            .Select(source => source.DeviceInformation?.Id)
            .OfType<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var facts = devices.Select(device => new VideoDeviceFacts(device.Id, device.Name, PanelOf(device), ContainerOf(device))).ToList();

        var cameras = Cameras.Listed(facts, colour);
        _logger.LogDebug("Listed {Cameras} colour cameras among {Devices} video capture devices: {Names}", cameras.Count, facts.Count, string.Join(", ", cameras.Select(camera => camera.Name)));
        return cameras;
    }

    /// <summary>Where <paramref name="device"/> sits by its enclosure location; Unknown when Windows does not say.</summary>
    private static CameraPanel PanelOf(DeviceInformation device) => device.EnclosureLocation?.Panel switch
    {
        Panel.Front => CameraPanel.Front,
        Panel.Back => CameraPanel.Back,
        null or Panel.Unknown => CameraPanel.Unknown,
        _ => CameraPanel.Elsewhere
    };

    /// <summary>The container <paramref name="device"/> belongs to, as its properties give it; null when they do not.</summary>
    private static Guid? ContainerOf(DeviceInformation device) =>
        device.Properties.TryGetValue(ContainerIdProperty, out var value) ? value switch
        {
            Guid guid => guid,
            string text when Guid.TryParse(text, out var parsed) => parsed,
            _ => null
        } : null;
}
