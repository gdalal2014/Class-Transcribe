using ClassScribe.Audio;
using ClassScribe.Camera;
using ClassScribe.Runs;
using Microsoft.Extensions.Logging;

namespace ClassScribe.Commands;

/// <summary>
/// The start of a run whose plan has a camera (teach-video 1.4 and 1.6, amendments 17 and 39), in the window, before the
/// question whether to continue a recent recording: the picture's size is resolved into the request (--resolution, else
/// menu choice 7's pick, else Recording:CameraResolution, else 720p), so that the recorder and the camera process never
/// read the settings; then what keeps the camera from being used on this computer is looked for (the set-up's problems:
/// Windows too old, its privacy switches, no H.264 encoder; then a camera recorder that cannot record the picture, see
/// <see cref="ICameraRecorder.NotReady"/>). Refused, the menu says why and offers Enter to record the sound only, Esc to go back; any other start
/// says why and starts nothing, exit 1. Otherwise the menu asks <c>Enter records with the camera; S records the sound only</c>,
/// so that a camera never comes on because of an earlier choice alone; the command line is the user's word already. The
/// window's answer to the camera prompt (<see cref="ICameraPrompt"/>). Windows' camera list and the set-up's read are each
/// waited for at most <see cref="CameraListing.Limit"/> (release 0.5.0 review, 3.1): a listing that hangs inside Windows
/// would otherwise hold the window, where no key does anything, before any recorder starts.
/// </summary>
/// <param name="cameras">Lists the cameras, to name the one the run would take.</param>
/// <param name="recorder">The camera's side of a run: whether this build can record the picture.</param>
/// <param name="setup">The camera's set-up lines, whose problems refuse the camera.</param>
/// <param name="choice">What is chosen for the session: the picture's size in force.</param>
/// <param name="ui">The window the questions are asked in.</param>
/// <param name="time">The clock the waits for Windows' camera list and the set-up's read are timed on.</param>
/// <param name="logger">Where what was decided is noted.</param>
public sealed class CameraStart(ICameraProvider cameras, ICameraRecorder recorder, CameraSetupFacts setup, SourceChoice choice, IConsoleUi ui, TimeProvider time, ILogger<CameraStart> logger) : ICameraPrompt
{
    /// <summary>The prompt's keys after the camera's name, amendment 17's words: letters that do not repeat the continue question's.</summary>
    public const string Prompt = "Enter records with the camera; S records the sound only.";

    /// <summary>What a refusal offers in the menu.</summary>
    public const string SoundOnlyOffer = "Enter records the sound only; Esc goes back.";

    /// <summary>The warning when the camera's set-up could not be read within <see cref="CameraListing.Limit"/>: the start goes on without its verdict.</summary>
    public const string SetupNotRead = "Windows did not say within 5 seconds whether the camera's set-up allows it (its H.264 encoders were being listed), so the start goes on; the camera process says what is wrong once it opens the camera";

    /// <summary>
    /// Decides the start of <paramref name="request"/>, from <paramref name="origin"/>, when its plan has a camera (see the
    /// class); a plan without one goes on as it is.
    /// </summary>
    public async Task<CameraDecision> DecideAsync(RecordRequest request, StartOrigin origin)
    {
        if (request.Source.Camera is not { } asked) return new CameraDecision.Go(request);

        var camera = asked with { Resolution = asked.Resolution is { } given && CameraSize.TryParse(given, out var size) ? size.Text : choice.CameraResolution.Text };
        var withCamera = request with { Source = request.Source with { Camera = camera } };
        var soundOnly = request with { Source = request.Source with { Camera = null } };

        var list = await cameras.ListWithinAsync(CameraListing.Limit, time, CancellationToken.None);
        var named = Named(list, camera);

        if (await WhyNotAsync(list.Cameras) is { } why)
        {
            logger.LogDebug("The camera {Camera} was refused for \"{Name}\": {Why}", named, request.Name, why);
            if (origin != StartOrigin.Menu)
            {
                ui.WriteLine($"{why}. {ICameraPrompt.NothingStarted}");
                return new CameraDecision.Refused(1);
            }

            ui.WriteLine($"Camera {named}: {why}. {SoundOnlyOffer}");
            return await ReadKeyAsync(enter: soundOnly, s: null);
        }

        if (origin != StartOrigin.Menu) return new CameraDecision.Go(withCamera);

        ui.WriteLine($"Camera {named}: {Prompt}");
        return await ReadKeyAsync(enter: withCamera, s: soundOnly);
    }

    /// <summary>
    /// The camera as the start line names it: the camera <paramref name="camera"/> asks for among <paramref name="list"/>,
    /// with its size; the name asked for and that it is not connected now, or what would be taken; or, for a list that
    /// could not be read (Windows did not answer within <see cref="CameraListing.Limit"/>, or said why), the list's
    /// problem in place of "not connected now", since the camera may well be there (release 0.5.0 review, 3.1).
    /// </summary>
    private static string Named(CameraList list, CameraChoice camera)
    {
        if (list.Problem is { } problem)
            return camera.NamePart is { } asked ? $"\"{asked}\" (the cameras could not be listed: {problem}; {camera.Resolution})" : $"(the cameras could not be listed: {problem}; {camera.Resolution})";

        return Cameras.Choose(list.Cameras, camera.NamePart) is { } found ? $"\"{found.Name}\" ({camera.Resolution})"
            : camera.NamePart is { } part ? $"\"{part}\" (not connected now; {camera.Resolution})"
            : $"(none connected now: a USB camera, else the built-in front camera; {camera.Resolution})";
    }

    /// <summary>
    /// What keeps the camera from being used now, in words without a full stop: the first problem among the set-up's lines
    /// (Windows too old, its privacy switches, no H.264 encoder), else a build that cannot record the picture; null when
    /// nothing does. The set-up is read on a thread of the pool and waited for at most <see cref="CameraListing.Limit"/>
    /// (release 0.5.0 review, 3.1: its encoder query is a WinRT call that could hang inside Windows); a read that does
    /// not come in time is said as a warning (<see cref="SetupNotRead"/>) and the start goes on without its verdict, since
    /// the camera process says what is wrong once it opens the camera.
    /// </summary>
    private async Task<string?> WhyNotAsync(IReadOnlyList<CameraDevice> listed)
    {
        IReadOnlyList<CameraSetupLine> lines;
        try
        {
            lines = await Task.Run(() => setup.ReadAsync(listed, [], wakeOn: false, CancellationToken.None)).WaitAsync(CameraListing.Limit, time);
        }
        catch (TimeoutException)
        {
            // Deliberately not rethrown: the set-up's read hangs inside Windows, and the window must not wait for it, where
            // Esc, Q and Ctrl+C do nothing meanwhile; the start goes on without the set-up's verdict, said as a warning, and
            // the camera process reports a camera that cannot be used once it opens it.
            logger.LogWarning("{Line}", SetupNotRead);
            lines = [];
        }

        return lines.FirstOrDefault(line => line.State == CameraSetupState.Problem)?.Text ?? recorder.NotReady;
    }

    /// <summary>
    /// Waits for Enter, which starts <paramref name="enter"/>, or S, which starts <paramref name="s"/> when it is offered; Esc,
    /// Q, Ctrl+C or nobody able to type go back. Other keys are ignored.
    /// </summary>
    private async Task<CameraDecision> ReadKeyAsync(RecordRequest enter, RecordRequest? s)
    {
        while (true)
        {
            var key = await ui.ReadKeyAsync(CancellationToken.None);
            if (key is not { } pressed || pressed.Key is ConsoleKey.Escape or ConsoleKey.Q || (pressed.Key == ConsoleKey.C && pressed.Modifiers.HasFlag(ConsoleModifiers.Control)))
                return new CameraDecision.Back();
            if (pressed.Key == ConsoleKey.Enter) return new CameraDecision.Go(enter);
            if (pressed.Key == ConsoleKey.S && s is not null) return new CameraDecision.Go(s);
        }
    }
}
