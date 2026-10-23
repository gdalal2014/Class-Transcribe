using ClassScribe.Audio;
using ClassScribe.Camera;
using Microsoft.Extensions.Logging;

namespace ClassScribe.Commands;

/// <summary>What the start of a run with a camera decided (see <see cref="CameraStart"/>).</summary>
public abstract record CameraDecision
{
    /// <summary>Only the kinds declared here derive from it.</summary>
    private CameraDecision()
    {
    }

    /// <summary>Start the run <paramref name="Request"/> describes: with the camera, its size resolved, or the sound only.</summary>
    /// <param name="Request">The request to run.</param>
    public sealed record Go(RecordRequest Request) : CameraDecision;

    /// <summary>Esc at a question: nothing is started, and the menu comes back.</summary>
    public sealed record Back : CameraDecision;

    /// <summary>The camera cannot be used, and nobody could choose the sound only: nothing is started, with <paramref name="ExitCode"/>.</summary>
    /// <param name="ExitCode">The exit code, 1.</param>
    public sealed record Refused(int ExitCode) : CameraDecision;
}

/// <summary>
/// The start of a run whose plan has a camera (teach-video 1.4 and 1.6, amendments 17 and 39), in the window, before the
/// question whether to continue a recent recording: the picture's size is resolved into the request (--resolution, else
/// menu choice 7's pick, else Recording:CameraResolution, else 720p), so that the recorder and the camera process never
/// read the settings; then what keeps the camera from being used on this computer is looked for (the set-up's problems:
/// Windows too old, its privacy switches, no H.264 encoder; then a build that cannot record the picture, which is every build
/// before slice 7). Refused, the menu says why and offers Enter to record the sound only, Esc to go back; any other start
/// says why and starts nothing, exit 1. Otherwise the menu asks <c>Enter records with the camera; S records the sound only</c>,
/// so that a camera never comes on because of an earlier choice alone; the command line is the user's word already.
/// </summary>
/// <param name="cameras">Lists the cameras, to name the one the run would take.</param>
/// <param name="recorder">The camera's side of a run: whether this build can record the picture.</param>
/// <param name="setup">The camera's set-up lines, whose problems refuse the camera.</param>
/// <param name="choice">What is chosen for the session: the picture's size in force.</param>
/// <param name="ui">The window the questions are asked in.</param>
/// <param name="logger">Where what was decided is noted.</param>
public sealed class CameraStart(ICameraProvider cameras, ICameraRecorder recorder, CameraSetupFacts setup, SourceChoice choice, IConsoleUi ui, ILogger<CameraStart> logger)
{
    /// <summary>The prompt's keys after the camera's name, amendment 17's words: letters that do not repeat the continue question's.</summary>
    public const string Prompt = "Enter records with the camera; S records the sound only.";

    /// <summary>What a refusal offers in the menu.</summary>
    public const string SoundOnlyOffer = "Enter records the sound only; Esc goes back.";

    /// <summary>What a refusal says anywhere else.</summary>
    public const string NothingStarted = "Nothing was recorded; leave out --camera to record the sound alone.";

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

        var list = await cameras.GetCamerasAsync(CancellationToken.None);
        var named = Cameras.Choose(list.Cameras, camera.NamePart) is { } found ? $"\"{found.Name}\" ({camera.Resolution})"
            : camera.NamePart is { } part ? $"\"{part}\" (not connected now; {camera.Resolution})"
            : $"(none connected now: a USB camera, else the built-in front camera; {camera.Resolution})";

        if (await WhyNotAsync(list.Cameras) is { } why)
        {
            logger.LogDebug("The camera {Camera} was refused for \"{Name}\": {Why}", named, request.Name, why);
            if (origin != StartOrigin.Menu)
            {
                ui.WriteLine($"{why}. {NothingStarted}");
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
    /// What keeps the camera from being used now, in words without a full stop: the first problem among the set-up's lines
    /// (Windows too old, its privacy switches, no H.264 encoder), else a build that cannot record the picture; null when
    /// nothing does.
    /// </summary>
    private async Task<string?> WhyNotAsync(IReadOnlyList<CameraDevice> listed)
    {
        var lines = await setup.ReadAsync(listed, [], wakeOn: false, CancellationToken.None);
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
