using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using ClassScribe.Audio.Microphones;
using ClassScribe.Camera;
using ClassScribe.Runs;
using ClassScribe.Settings;
using Microsoft.Extensions.Logging;
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
/// set-up lines (see <see cref="CameraSetupFacts"/>), which setup-check will show too. Then, only after the person types Y
/// and Enter, it opens that camera for a few seconds, shared, as a lesson will (a camera process's check job): every call on
/// the camera's route with its outcome and HRESULT, the frames that came, or why none did. A security program such as
/// Bitdefender meets ClassScribe there, with the person watching. Nothing of the picture is kept. Returns 0, or 1 when a
/// set-up line is a problem that keeps the camera from being used, or the camera opened and sent no picture.
/// </summary>
/// <param name="cameras">Lists the cameras.</param>
/// <param name="microphones">Lists the microphones, for each webcam's own.</param>
/// <param name="usage">Windows' camera record: which apps use a camera now.</param>
/// <param name="setup">The camera's set-up lines.</param>
/// <param name="choice">What is chosen for the session: the picture's size menu choice 7 picked.</param>
/// <param name="recording">The run's recording settings, for the picture's size and the wake.</param>
/// <param name="ui">The window the check is written to, and the Y read from.</param>
/// <param name="starter">Starts the camera process that opens the camera, after the Y.</param>
/// <param name="time">The clock the check's time limit is measured on.</param>
/// <param name="logger">The log, for the check's outcome.</param>
public sealed class CameraCheckCommand(ICameraProvider cameras, IMicrophoneProvider microphones, ICameraUsageRecord usage, CameraSetupFacts setup,
    Audio.SourceChoice choice, IOptionsSnapshot<RecordingOptions> recording, IConsoleUi ui, ICameraProcessStarter starter, TimeProvider time,
    ILogger<CameraCheckCommand> logger)
{
    /// <summary>The line when the person did not type Y.</summary>
    public const string NotOpened = "The camera was not opened.";

    /// <summary>The line when the window takes no typing, so that the Y cannot be asked for.</summary>
    public const string NotAsked = "The camera is opened only after you type Y, and this window takes no typing, so it was not opened.";

    /// <summary>The question before the camera is opened.</summary>
    public const string Question = "Type Y and Enter to open it, or just Enter to leave it closed: ";

    /// <summary>The advice after an open that failed or brought no picture.</summary>
    public const string TryAgain = "If a security program asked about ClassScribe, allow it, then run the check again.";

    /// <summary>How long the camera check's camera process may take in all before it is ended.</summary>
    public static readonly TimeSpan CheckLimit = TimeSpan.FromSeconds(75);

    /// <summary>How long the camera is opened for, as the camera process counts its frames.</summary>
    public static readonly TimeSpan OpenFor = TimeSpan.FromSeconds(5);

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
        var (size, from) = SizeFor(request);
        var camera = Cameras.Choose(list.Cameras, request.NamePart);
        ui.WriteLine(Chosen(camera, request, size, from));

        ui.WriteLine("Set-up:");
        var lines = await setup.ReadAsync(list.Cameras, connected, recording.Value.WakeCameraMicrophone, CancellationToken.None);
        foreach (var line in lines) ui.WriteLine($"  {line}");

        var setupProblem = lines.Any(line => line.State == CameraSetupState.Problem);
        if (camera is null) return setupProblem ? 1 : 0;

        var opened = await OfferToOpenAsync(camera, size);
        return setupProblem || opened == false ? 1 : 0;
    }

    /// <summary>
    /// Offers to open <paramref name="camera"/> at <paramref name="size"/>, and opens it only when the person types Y and
    /// Enter: true when it sent its picture, false when it could not be opened or sent none, null when it was not opened.
    /// </summary>
    private async Task<bool?> OfferToOpenAsync(CameraDevice camera, CameraSize size)
    {
        if (!ui.IsInteractive)
        {
            ui.WriteLine(NotAsked);
            return null;
        }

        ui.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"To see that \"{camera.Name}\" opens, ClassScribe can open it now for {OpenFor.TotalSeconds:0} seconds, shared, at {size.Text}, as a lesson will: its light comes on, nothing of the picture is kept, and a security program such as Bitdefender may ask about ClassScribe; allow it."));
        ui.Write(Question);
        var answer = await ui.ReadLineAsync(CancellationToken.None);
        if (!string.Equals(answer?.Trim(), "Y", StringComparison.OrdinalIgnoreCase))
        {
            ui.WriteLine(NotOpened);
            return null;
        }

        return await OpenAsync(camera, size);
    }

    /// <summary>
    /// Opens <paramref name="camera"/> through a camera process's check job and shows what it says: each call on the route,
    /// the camera's format, the frames counted, and why it failed when it did. True when frames came.
    /// </summary>
    private async Task<bool> OpenAsync(CameraDevice camera, CameraSize size)
    {
        ui.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Opening \"{camera.Name}\" for {OpenFor.TotalSeconds:0} seconds, shared, at {size.Text}:"));
        ICameraProcess process;
        try
        {
            process = starter.Start(RunIds.New(time.GetUtcNow()));
        }
        catch (Win32Exception ex)
        {
            // Deliberately not rethrown: the check's answer is that the camera process did not start, said in words.
            logger.LogWarning(ex, "The camera check could not start its camera process");
            ui.WriteLine($"Windows did not start ClassScribe's camera process: {ex.Message.TrimEnd('.')}.");
            return false;
        }

        long frames = 0;
        string? failure = null;
        await using (process)
        {
            using var limit = new CancellationTokenSource(CheckLimit, time);
            try
            {
                await CameraLines.SendJobAsync(process.Channel, new CameraJob(CameraProtocol.Version, CameraJobKind.Check, camera.Id, camera.Name, size.Text), limit.Token);
                while (await process.Channel.ReceiveAsync(limit.Token) is { } line)
                {
                    switch (CameraLines.Read(line))
                    {
                        case CameraLog log:
                            ui.WriteLine($"  {log.Text}");
                            break;
                        case CameraOpened opened:
                            ui.WriteLine($"  Open, shared; the camera's format: {opened.Encoder.Replace("none: ", "", StringComparison.Ordinal)}");
                            break;
                        case CameraStatus status:
                            frames = status.Frames;
                            ui.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  Frames in {OpenFor.TotalSeconds:0} seconds: {status.Frames}"));
                            break;
                        case CameraFailed failed:
                            failure = failed.Why;
                            break;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Deliberately not rethrown: the check gave its camera process its time; it is ended, which lets the camera go.
                process.Kill();
                failure = string.Create(CultureInfo.InvariantCulture, $"The camera process did not finish within {CheckLimit.TotalSeconds:0} seconds, so it was ended");
            }
            catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException)
            {
                // Deliberately not rethrown: the camera process ended or spoke what this build cannot read; the check says so.
                failure = $"The camera process ended before it had finished ({ex.Message.TrimEnd('.')})";
            }
        }

        if (failure is null && frames > 0)
        {
            var done = string.Create(CultureInfo.InvariantCulture, $"The camera \"{camera.Name}\" opened, shared, and sent its picture: {frames} frames in {OpenFor.TotalSeconds:0} seconds.");
            logger.LogInformation("{Line}", done);
            ui.WriteLine(done);
            return true;
        }

        var why = failure ?? $"The camera \"{camera.Name}\" sent no picture";
        logger.LogWarning("The camera check: {Why}", why);
        ui.WriteLine($"{why.TrimEnd('.')}.");
        ui.WriteLine(TryAgain);
        return false;
    }

    /// <summary>The picture's size and where it comes from: --resolution, else menu choice 7's pick, else Recording:CameraResolution, else 720p.</summary>
    private (CameraSize Size, string From) SizeFor(CameraCheckRequest request) =>
        request.Resolution is { } given && CameraSize.TryParse(given, out var asked) ? (asked, "--resolution")
        : choice.PickedCameraResolution is { } picked ? (picked, "menu choice 7")
        : CameraSize.TryParse(recording.Value.CameraResolution, out var set) ? (set, "Recording:CameraResolution")
        : (CameraSize.Default, "the default");

    /// <summary>Which apps use a camera now, from Windows' record: a start time and no stop time; or that none does, or that the record could not be read.</summary>
    private static string InUse(UsageRecordListing record)
    {
        if (record.Problem is { } problem) return $"Windows' camera record could not be read ({problem}), so it is not known which app uses a camera now.";

        var apps = record.Apps.Where(app => app.InUse).Select(app => app.ProcessName).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();
        return apps.Count == 0 ? "No app is using a camera now, as Windows' camera record shows." : $"Using a camera now, as Windows' camera record shows: {string.Join(", ", apps)}.";
    }

    /// <summary>The camera a lesson recorded with --camera would take now, with the part of its name <paramref name="request"/> gives, and its picture's size.</summary>
    private static string Chosen(CameraDevice? camera, CameraCheckRequest request, CameraSize size, string from)
    {
        var sized = $"{size.Text} ({size.Width}x{size.Height}; {from})";
        var option = request.NamePart is { } part ? $"--camera \"{part}\"" : "--camera";

        return camera is not null
            ? $"A lesson recorded with {option} would take the camera \"{camera.Name}\", at {sized}."
            : request.NamePart is not null
                ? $"No camera's name holds \"{request.NamePart}\" now: a lesson recorded with {option} would wait for it, at {sized}."
                : $"No USB camera or built-in front camera is connected now: a lesson recorded with {option} would wait for one, at {sized}.";
    }
}
