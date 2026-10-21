using ClassScribe.Audio.Microphones;
using ClassScribe.Camera;
using ClassScribe.Settings;
using Microsoft.Extensions.Options;

namespace ClassScribe.Commands;

/// <summary>What a camera check is asked: camera-check on the command line, or menu choice 8's C, which asks for neither.</summary>
/// <param name="NamePart">Part of a camera's name, as --camera would give it; null for the camera a lesson takes without one.</param>
/// <param name="Resolution">The picture's size, as --resolution gives it, already checked; null for the menu's pick or Recording:CameraResolution.</param>
public sealed record CameraCheckRequest(string? NamePart = null, string? Resolution = null);

/// <summary>
/// The camera check (teach-video 6.16, camera-first step 11), camera-check and menu choice 8's C: the colour cameras from
/// Windows' own lists, numbered, each with where it is and a webcam's own microphone; which apps use a camera now, from
/// Windows' camera record; the camera a lesson recorded with --camera would take, and at which size; and the camera's
/// set-up lines (see <see cref="CameraSetupFacts"/>), which setup-check will show too. In this build it opens no camera:
/// opening one for three seconds, and saving a still to check the framing, come once the camera probe has chosen the
/// route. Returns 0, or 1 when a set-up line is a problem that keeps the camera from being used.
/// </summary>
/// <param name="cameras">Lists the cameras.</param>
/// <param name="microphones">Lists the microphones, for each webcam's own.</param>
/// <param name="usage">Windows' camera record: which apps use a camera now.</param>
/// <param name="setup">The camera's set-up lines.</param>
/// <param name="recording">The run's recording settings, for the picture's size and the wake.</param>
/// <param name="ui">The window the check is written to.</param>
public sealed class CameraCheckCommand(ICameraProvider cameras, IMicrophoneProvider microphones, ICameraUsageRecord usage, CameraSetupFacts setup,
    IOptionsSnapshot<RecordingOptions> recording, IConsoleUi ui)
{
    /// <summary>The last line, which says what this build does and does not do.</summary>
    public const string OpensNothing = "This build lists and checks the cameras and opens none; recording a camera's picture comes in a later build.";

    /// <summary>Runs the check <paramref name="request"/> describes and returns the exit code.</summary>
    public async Task<int> RunAsync(CameraCheckRequest request)
    {
        var list = await cameras.GetCamerasAsync(CancellationToken.None);
        var connected = microphones.GetMicrophones();

        ui.WriteLine("Cameras (from Windows' own lists; none was opened):");
        if (list.Problem is { } problem) ui.WriteLine($"  The cameras could not be listed: {problem}.");
        else if (list.Cameras.Count == 0) ui.WriteLine("  None is connected now.");
        for (var i = 0; i < list.Cameras.Count; i++)
            ui.WriteLine($"  {i + 1}  {DevicesCommand.Describe(list.Cameras[i], Cameras.MicrophoneOf(list.Cameras[i], connected))}");

        ui.WriteLine(InUse(usage.Read()));
        ui.WriteLine(Chosen(list.Cameras, request));

        ui.WriteLine("Set-up:");
        var lines = await setup.ReadAsync(list.Cameras, connected, recording.Value.WakeCameraMicrophone, CancellationToken.None);
        foreach (var line in lines) ui.WriteLine($"  {line}");

        ui.WriteLine(OpensNothing);
        return lines.Any(line => line.State == CameraSetupState.Problem) ? 1 : 0;
    }

    /// <summary>Which apps use a camera now, from Windows' record: a start time and no stop time; or that none does, or that the record could not be read.</summary>
    private static string InUse(UsageRecordListing record)
    {
        if (record.Problem is { } problem) return $"Windows' camera record could not be read ({problem}), so it is not known which app uses a camera now.";

        var apps = record.Apps.Where(app => app.InUse).Select(app => app.ProcessName).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();
        return apps.Count == 0 ? "No app is using a camera now, as Windows' camera record shows." : $"Using a camera now, as Windows' camera record shows: {string.Join(", ", apps)}.";
    }

    /// <summary>
    /// The camera a lesson recorded with --camera would take now, with the part of its name <paramref name="request"/> gives,
    /// and its picture's size: --resolution, else Recording:CameraResolution, else 720p.
    /// </summary>
    private string Chosen(IReadOnlyList<CameraDevice> listed, CameraCheckRequest request)
    {
        var (size, from) = request.Resolution is { } given && CameraSize.TryParse(given, out var asked) ? (asked, "--resolution")
            : CameraSize.TryParse(recording.Value.CameraResolution, out var set) ? (set, "Recording:CameraResolution")
            : (CameraSize.Default, "the default");
        var sized = $"{size.Text} ({size.Width}x{size.Height}; {from})";
        var option = request.NamePart is { } part ? $"--camera \"{part}\"" : "--camera";

        return Cameras.Choose(listed, request.NamePart) is { } camera
            ? $"A lesson recorded with {option} would take the camera \"{camera.Name}\", at {sized}."
            : request.NamePart is not null
                ? $"No camera's name holds \"{request.NamePart}\" now: a lesson recorded with {option} would wait for it, at {sized}."
                : $"No USB camera or built-in front camera is connected now: a lesson recorded with {option} would wait for one, at {sized}.";
    }
}
