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
