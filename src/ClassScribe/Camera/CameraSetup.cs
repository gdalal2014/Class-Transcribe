using ClassScribe.Audio.Microphones;
using ClassScribe.Desktop;

namespace ClassScribe.Camera;

/// <summary>Windows' record of which apps used a camera and when (ConsentStore\webcam), with its privacy switches. Reading it opens nothing.</summary>
public interface ICameraUsageRecord
{
    /// <summary>The record as it is now. A record that cannot be read gives a listing with its <see cref="UsageRecordListing.Problem"/>; nothing is thrown for it.</summary>
    UsageRecordListing Read();
}

/// <summary>What Windows says that the camera needs, read without opening any camera.</summary>
public interface ICameraSystem
{
    /// <summary>The version of Windows that runs, such as 10.0.26300.</summary>
    Version WindowsVersion { get; }

    /// <summary>Whether an H.264 video encoder is installed (a Windows N edition without the Media Feature Pack has none); null when that cannot be read. Lists the encoders and opens none.</summary>
    Task<bool?> HasH264EncoderAsync(CancellationToken cancellationToken);

    /// <summary>The security programs Windows' Security Center knows of, by name, such as Bitdefender Antivirus; null when they cannot be read.</summary>
    IReadOnlyList<string>? SecurityPrograms();
}

/// <summary>How a set-up line stands.</summary>
public enum CameraSetupState
{
    /// <summary>As the camera needs it.</summary>
    Ok,

    /// <summary>The camera cannot be used until this is mended.</summary>
    Problem,

    /// <summary>Worth knowing; nothing to mend.</summary>
    Note,

    /// <summary>A part that is switched off.</summary>
    Off
}

/// <summary>One line of the camera's set-up, as camera-check shows it and setup-check will.</summary>
/// <param name="State">How it stands.</param>
/// <param name="Text">What it says, in plain words, without a full stop.</param>
public sealed record CameraSetupLine(CameraSetupState State, string Text)
{
    /// <summary>The line as a window shows it: its state in capitals, padded, then the words.</summary>
    public override string ToString() => $"{State switch { CameraSetupState.Ok => "OK", CameraSetupState.Problem => "PROBLEM", CameraSetupState.Note => "NOTE", _ => "OFF" },-8} {Text}";
}

/// <summary>
/// The camera's set-up lines (camera-first step 11, point 3), which camera-check shows and setup-check will reuse: Windows'
/// version against 10.0.19041, Windows' camera privacy switches (the device's, the user's, and the one for desktop apps),
/// an H.264 encoder present, the security program and, for Bitdefender, its Webcam Protection step, each webcam's
/// microphone (by the container it shares with its camera), and whether a webcam is kept awake (Recording:WakeCameraMicrophone). Nothing is
/// opened to read them: Windows' lists, its registry and its Security Center only.
/// </summary>
/// <param name="system">What Windows says: its version, its encoders, its security programs.</param>
/// <param name="usage">Windows' camera record, whose values are the user's privacy switches.</param>
/// <param name="registry">The registry, for the switch of the whole device under HKEY_LOCAL_MACHINE.</param>
public sealed class CameraSetupFacts(ICameraSystem system, ICameraUsageRecord usage, IRegistryValues registry)
{
    /// <summary>The oldest Windows the camera API works on: Windows 10 version 2004.</summary>
    public static readonly Version OldestWindows = new(10, 0, 19041);

    /// <summary>The camera's ConsentStore key, under HKEY_LOCAL_MACHINE for the whole device.</summary>
    public const string WebcamKey = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\webcam";

    /// <summary>Where Windows' camera privacy switches are, in words, with the page that opens them.</summary>
    public const string PrivacyPage = "Settings > Privacy & security > Camera (ms-settings:privacy-webcam)";

    /// <summary>Where Bitdefender's switch for ClassScribe is.</summary>
    public const string BitdefenderStep = "Bitdefender: Privacy > Video & Audio Protection > Webcam Protection";

    /// <summary>
    /// The set-up lines, in order: Windows' version, the privacy switches, the encoder, each security program, each
    /// webcam's microphone among <paramref name="cameras"/> and <paramref name="microphones"/>, and the wake, which
    /// <paramref name="wakeOn"/> says is on or off.
    /// </summary>
    public async Task<IReadOnlyList<CameraSetupLine>> ReadAsync(IReadOnlyList<CameraDevice> cameras, IReadOnlyList<Microphone> microphones, bool wakeOn,
        CancellationToken cancellationToken)
    {
        List<CameraSetupLine> lines = [VersionLine(system.WindowsVersion), Privacy()];

        lines.Add(await system.HasH264EncoderAsync(cancellationToken) switch
        {
            true => new(CameraSetupState.Ok, "An H.264 video encoder is installed, which a lesson's video needs"),
            false => new(CameraSetupState.Problem, "No H.264 video encoder is installed, so the picture cannot be recorded: a Windows N edition needs the Media Feature Pack (Settings > System > Optional features)"),
            null => new(CameraSetupState.Note, "Windows did not say whether an H.264 video encoder is installed")
        });

        lines.AddRange(Security(system.SecurityPrograms()));
        foreach (var camera in cameras)
        {
            if (Cameras.MicrophoneOf(camera, microphones) is { } microphone)
                lines.Add(new(CameraSetupState.Note, $"The camera \"{camera.Name}\" has its own microphone: \"{microphone.Name}\""));
        }

        lines.Add(wakeOn
            ? new(CameraSetupState.Note, "Recording:WakeCameraMicrophone is on: while a webcam's microphone is recorded, ClassScribe keeps the webcam's picture open, shared, so that it cannot fall asleep; its light stays on")
            : new(CameraSetupState.Off, "Recording:WakeCameraMicrophone is off: a webcam that sleeps by itself, such as the OBSBOT with Device Auto Sleep on, is not kept awake; turn off its Device Auto Sleep in OBSBOT Center, or turn the setting on"));
        return lines;
    }

    /// <summary>Windows' version against the oldest the camera API works on.</summary>
    private static CameraSetupLine VersionLine(Version version) =>
        version >= OldestWindows
            ? new(CameraSetupState.Ok, $"Windows {version.ToString(3)}, which the camera needs at 10.0.19041 (version 2004) or later")
            : new(CameraSetupState.Problem, $"Windows {version.ToString(3)} is older than 10.0.19041 (version 2004), which the camera needs");

    /// <summary>
    /// Windows' camera privacy switches: the whole device's (HKEY_LOCAL_MACHINE), the user's, and the user's for desktop
    /// apps, which ClassScribe is. Deny in any of them keeps the camera from ClassScribe; one not set allows it.
    /// </summary>
    private CameraSetupLine Privacy()
    {
        var device = registry.ReadLocalMachineString(WebcamKey, "Value");
        var record = usage.Read();
        if (IsDeny(device)) return new(CameraSetupState.Problem, $"Windows lets no app on this device use the camera: turn on Camera access in {PrivacyPage}");
        if (record.Problem is null && IsDeny(record.Access)) return new(CameraSetupState.Problem, $"Windows lets no app use the camera: turn on Camera access in {PrivacyPage}");
        if (record.Problem is null && IsDeny(record.DesktopAccess)) return new(CameraSetupState.Problem, $"Windows does not let desktop apps use the camera: turn on \"Let desktop apps access your camera\" in {PrivacyPage}");

        return record.Problem is null
            ? new(CameraSetupState.Ok, "Windows lets desktop apps use the camera")
            : new(CameraSetupState.Note, $"Windows' camera privacy switches could not be read ({record.Problem}); see {PrivacyPage}");
    }

    /// <summary>
    /// A line per security program: Bitdefender's names its Webcam Protection step, since it blocks programs it does not
    /// know and asks about ClassScribe the first time it opens a camera; any other says to allow ClassScribe if it asks.
    /// </summary>
    private static IEnumerable<CameraSetupLine> Security(IReadOnlyList<string>? programs)
    {
        if (programs is null)
        {
            yield return new(CameraSetupState.Note, "Windows' Security Center did not say which security program runs; if one asks about ClassScribe and the camera, allow it");
            yield break;
        }

        foreach (var program in programs)
        {
            yield return program.Contains("Bitdefender", StringComparison.OrdinalIgnoreCase)
                ? new(CameraSetupState.Note, $"{program} watches the camera: when its Webcam Protection asks about ClassScribe, allow it ({BitdefenderStep})")
                : new(CameraSetupState.Note, $"{program} is a security program here: if it asks about ClassScribe and the camera, allow it");
        }
    }

    /// <summary>True for a switch set to Deny, in any case.</summary>
    private static bool IsDeny(string? value) => string.Equals(value?.Trim(), "Deny", StringComparison.OrdinalIgnoreCase);
}
