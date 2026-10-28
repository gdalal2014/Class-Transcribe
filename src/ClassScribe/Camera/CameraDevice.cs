using ClassScribe.Audio.Microphones;

namespace ClassScribe.Camera;

/// <summary>Where a camera is, as Windows' device lists tell it (teach-video 6.4).</summary>
public enum CameraPlace
{
    /// <summary>Built into the laptop's screen, facing the user (enclosure panel Front).</summary>
    BuiltInFront,

    /// <summary>Built into the back of the device, facing away (enclosure panel Back), as on a tablet.</summary>
    BuiltInBack,

    /// <summary>Plugged in through USB, such as the OBSBOT Meet Flip.</summary>
    Usb,

    /// <summary>A camera made by software, such as a phone's through Phone Link: recorded only when named.</summary>
    Virtual,

    /// <summary>Any other camera, whose place Windows does not say.</summary>
    Other
}

/// <summary>A colour camera Windows can record from right now, as its device lists describe it. Nothing here opens it.</summary>
/// <param name="Id">Windows' identifier for the camera's device interface, by which a run's camera is pinned.</param>
/// <param name="Name">The name Windows shows for it, such as OBSBOT Meet Flip StreamCamera.</param>
/// <param name="Place">Built in at the front or the back, on USB, virtual, or other.</param>
/// <param name="ContainerId">
/// The physical device the camera belongs to (System.Devices.ContainerId), which a webcam's microphone shares; null when
/// Windows does not say.
/// </param>
public sealed record CameraDevice(string Id, string Name, CameraPlace Place, Guid? ContainerId)
{
    /// <summary>True for a camera built into the laptop, at its front or its back.</summary>
    public bool IsBuiltIn => Place is CameraPlace.BuiltInFront or CameraPlace.BuiltInBack;

    /// <summary>What the lists say about the camera after its name: built in and where, USB, virtual, or nothing.</summary>
    public string Note => Place switch
    {
        CameraPlace.BuiltInFront => "built in, front",
        CameraPlace.BuiltInBack => "built in, back",
        CameraPlace.Usb => "USB",
        CameraPlace.Virtual => "virtual, such as a phone's camera through Phone Link: recorded only when named",
        _ => ""
    };

    /// <summary>The camera as a list shows it: its name, then its note in brackets when it has one.</summary>
    public string Described => Note.Length > 0 ? $"{Name}  ({Note})" : Name;
}

/// <summary>Where a camera's device interface sits, as Windows' enclosure location says it.</summary>
public enum CameraPanel
{
    /// <summary>Windows does not say.</summary>
    Unknown,

    /// <summary>On the front of the device: the laptop's screen.</summary>
    Front,

    /// <summary>On the back of the device.</summary>
    Back,

    /// <summary>On another side (top, bottom, left, right).</summary>
    Elsewhere
}

/// <summary>
/// What Windows' device list says about one video capture device, before it is known to be a colour camera: plain data, so
/// that the listing's rules are tested without Windows.
/// </summary>
/// <param name="Id">The device interface's identifier, such as \\?\USB#VID_3564&amp;PID_FF06...</param>
/// <param name="Name">The name Windows shows.</param>
/// <param name="Panel">Where its enclosure location puts it.</param>
/// <param name="ContainerId">Its container, the physical device it belongs to; null when Windows does not say.</param>
public sealed record VideoDeviceFacts(string Id, string Name, CameraPanel Panel, Guid? ContainerId);

/// <summary>The cameras listed, or why they could not be.</summary>
/// <param name="Cameras">The colour cameras, in Windows' order; empty when none is connected or the list could not be read.</param>
/// <param name="Problem">Why the cameras could not be listed, in words; null when they were.</param>
public sealed record CameraList(IReadOnlyList<CameraDevice> Cameras, string? Problem = null);

/// <summary>
/// The rules of the camera list and the camera choice (teach-video 6.4), kept apart from Windows so that they are tested
/// alone: which devices are colour cameras, where each is, which one a run records, and which microphone is a webcam's own.
/// </summary>
public static class Cameras
{
    /// <summary>
    /// The container Windows gives every device built into the computer itself; a webcam's microphone is never matched to a
    /// camera by it, since the laptop's array and its camera both carry it.
    /// </summary>
    public static readonly Guid ThisComputer = new("00000000-0000-0000-FFFF-FFFFFFFFFFFF");

    /// <summary>
    /// The colour cameras among <paramref name="devices"/>: those whose id is the device of a colour source in Windows' frame
    /// source groups (<paramref name="colourSourceIds"/>), so that an infrared camera (Windows Hello) never shows; each with
    /// its place: built in at the front or the back by its enclosure panel, else USB by its id, virtual (a software device,
    /// such as Phone Link's) by its id, or other.
    /// </summary>
    public static IReadOnlyList<CameraDevice> Listed(IEnumerable<VideoDeviceFacts> devices, IReadOnlySet<string> colourSourceIds) =>
    [
        .. devices
            .Where(device => colourSourceIds.Contains(device.Id))
            .Select(device => new CameraDevice(device.Id, device.Name, PlaceOf(device), device.ContainerId))
    ];

    /// <summary>
    /// The camera a run records: the first whose name holds <paramref name="namePart"/>, in any case, virtual ones included;
    /// without a name part, a USB camera when one is plugged in (someone who plugs in a webcam means to use it), else the
    /// built-in front camera, never a virtual one. Null when none fits: a run then waits for it.
    /// </summary>
    public static CameraDevice? Choose(IReadOnlyList<CameraDevice> cameras, string? namePart)
    {
        if (!string.IsNullOrWhiteSpace(namePart))
            return cameras.FirstOrDefault(camera => camera.Name.Contains(namePart.Trim(), StringComparison.OrdinalIgnoreCase));

        return cameras.FirstOrDefault(camera => camera.Place == CameraPlace.Usb)
            ?? cameras.FirstOrDefault(camera => camera.Place == CameraPlace.BuiltInFront);
    }

    /// <summary>
    /// The camera <paramref name="microphone"/> belongs to: a camera that is not built in whose container is the
    /// microphone's (a webcam's microphone). Never a built-in camera, and never by the container of the computer itself,
    /// which the laptop's own array and camera share. Null for a microphone of no camera.
    /// </summary>
    public static CameraDevice? CameraOf(Microphone microphone, IReadOnlyList<CameraDevice> cameras) =>
        MayBeOfACamera(microphone) ? cameras.FirstOrDefault(camera => !camera.IsBuiltIn && camera.ContainerId == microphone.ContainerId) : null;

    /// <summary>
    /// The webcam <paramref name="microphone"/> belongs to (camera-first 3.4): as <see cref="CameraOf"/>, but only a camera
    /// that is neither built in nor virtual, such as the OBSBOT on USB; a phone's camera through Phone Link is not a webcam.
    /// Null for a microphone of no webcam: the hold and the webcam zeros rules are for a webcam's microphone alone.
    /// </summary>
    public static CameraDevice? WebcamOf(Microphone microphone, IReadOnlyList<CameraDevice> cameras) =>
        CameraOf(microphone, [.. cameras.Where(camera => camera.Place != CameraPlace.Virtual)]);

    /// <summary>
    /// True when <paramref name="microphone"/> could belong to a camera at all: it names a container that is not the
    /// computer's own. Only such a microphone needs the cameras listed.
    /// </summary>
    public static bool MayBeOfACamera(Microphone microphone) =>
        microphone.ContainerId is { } container && container != ThisComputer && container != Guid.Empty;

    /// <summary>The microphone that belongs to <paramref name="camera"/>, by the same rule as <see cref="CameraOf"/>; null when it has none.</summary>
    public static Microphone? MicrophoneOf(CameraDevice camera, IReadOnlyList<Microphone> microphones) =>
        microphones.FirstOrDefault(microphone => CameraOf(microphone, [camera]) is not null);

    /// <summary>
    /// Where <paramref name="device"/> is: built in by its panel first, since a laptop's own camera usually sits on an internal
    /// USB bus too; then USB or virtual by its id; other when nothing says.
    /// </summary>
    private static CameraPlace PlaceOf(VideoDeviceFacts device) => device.Panel switch
    {
        CameraPanel.Front => CameraPlace.BuiltInFront,
        CameraPanel.Back => CameraPlace.BuiltInBack,
        _ when device.Id.StartsWith(@"\\?\USB#", StringComparison.OrdinalIgnoreCase) => CameraPlace.Usb,
        _ when device.Id.StartsWith(@"\\?\SWD#", StringComparison.OrdinalIgnoreCase) => CameraPlace.Virtual,
        _ => CameraPlace.Other
    };
}
