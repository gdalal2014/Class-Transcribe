using System.Diagnostics;
using ClassScribe.Drive;
using ClassScribe.Recorder;
using ClassScribe.Settings;
using ClassScribe.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ClassScribe.Commands;

/// <summary>
/// Starts a run from the menu, the command line or the report at the start (sections 1.3, 1.6 and 1.14 of the design).
/// One recording at a time: a recording of the same name running already is shown from the command line, and any other is
/// explained, with exit code 1. Then whether to continue a recent recording of the same name: the menu asks, the command
/// line follows --continue and --new and never asks. Then where the run records (see <see cref="RecorderLauncher"/>): in
/// a separate recorder, which this window shows as its owner, and which uploads to Google Drive itself after the class; or
/// in this window, through the record command in a scope of its own, after saying why, with the upload to Google Drive
/// after it in the same scope (see <see cref="IDriveAutoUpload"/>). A recorder that fails to start is started once more, and after a second failure the run
/// records in this window. A start refused because a recording of the same name took the lock at the same moment shows
/// that recording instead. A recorder lost while it records is continued by itself, as the same recording's next part,
/// with the rest of --minutes: in a new recorder the first time, and in this window when it is lost again within 10
/// minutes, since a repeated kill is most likely Bitdefender. A start from a window the recorder opened by itself
/// (<see cref="StartOrigin.AutoContinueFromAlert"/>) shows every run with its keys waiting for Enter, marks no run seen,
/// and never records in that window: where the run would record there, the loss is left for the next start to report, with
/// C. Returns the exit code: the run's, 0 after Esc, 1 when nothing was started or the run was lost.
/// </summary>
/// <remarks>
/// <para>A class always comes first (design 2.5). A start of a class, from any origin, that finds a YouTube video's run
/// recording asks that run to give way through its yield (see <see cref="IVideoYield"/>), says so, and waits up to
/// <see cref="YieldPatience"/> on the steady clock, looking at the run files every <see cref="YieldPoll"/>, for it to stop
/// recording; then the class starts as it would have. A video that has not stopped by then is explained as any other
/// recording is, with the keys that stop it. A video's run is never shown to a class's start, never offered to be
/// continued, and never continued once its recorder is lost.</para>
/// <para>A YouTube batch's start of one of its videos (<see cref="StartOrigin.VideoBatch"/>) asks nothing: it is a new
/// recording, shown here as its owner; a recording running already is explained, never shown, and the batch waits.</para>
/// </remarks>
/// <param name="journal">The run files.</param>
/// <param name="launcher">Starts the recorder, or says why the run records in this window.</param>
/// <param name="viewer">Shows a run in the background.</param>
/// <param name="question">Whether to continue a recent recording of the same name.</param>
/// <param name="settings">The settings, handed to the recorder.</param>
/// <param name="scopes">Makes a scope for a run in this window.</param>
/// <param name="ui">The window.</param>
/// <param name="time">The clock.</param>
/// <param name="logger">Where the starts are noted.</param>
/// <param name="yield">Asks a YouTube video's run to give way to a class; null where none is known of.</param>
/// <param name="camera">
/// The start of a run with a camera (see <see cref="CameraStart"/>), before the continue question: its size resolved, its
/// refusals, and the menu's Enter or S; null where none is registered, and a run with a camera is then refused as this build
/// refuses it, so that no class starts with a half-built camera.
/// </param>
public sealed class RecordStarter(RunJournal journal, RecorderLauncher launcher, IRunViewer viewer, ContinueQuestion question, SettingsReloader settings,
    IServiceScopeFactory scopes, IConsoleUi ui, TimeProvider time, ILogger<RecordStarter> logger, IVideoYield? yield = null, CameraStart? camera = null) : IRecordStarter
{
    /// <summary>How long a class waits for a YouTube video's run it asked to give way.</summary>
    public static readonly TimeSpan YieldPatience = TimeSpan.FromSeconds(30);

    /// <summary>How often the run files are looked at while a video's run gives way.</summary>
    public static readonly TimeSpan YieldPoll = TimeSpan.FromMilliseconds(250);

    /// <summary>Within how long a second loss of the recorder continues the recording in this window instead of in a new recorder.</summary>
    public static readonly TimeSpan LossWindow = TimeSpan.FromMinutes(10);

    /// <summary>What a window says once the recorder it started records in the background.</summary>
    public const string InTheBackground = "Recording in the background: closing this window does not stop it. Esc leaves this view; start ClassScribe again to see it.";

    /// <summary>What a command says when Esc leaves the view of a recording.</summary>
    public const string CarriesOn = "The recording carries on in the background. Start ClassScribe to see it.";

    /// <summary>The exit code when nothing was started, or the run was lost.</summary>
    private const int NotDone = 1;

    /// <summary>What a window the recorder opened by itself says where any other window would record the run in itself.</summary>
    public const string NotInThisWindow =
        "ClassScribe opened this window by itself, so the recording is not continued in it. Its audio so far is kept; start ClassScribe to continue it, or to transcribe it.";

    /// <summary>The advice when the recorder would not start twice.</summary>
    private const string BitdefenderAdvice = "If Bitdefender showed a notice, add the exception described in the README.";

    /// <summary>What closing the window does to a run in this window, and the advice when the recorder would not start.</summary>
    private const string InThisWindowAfterFailures = $"Closing this window ends the recording, and the audio so far is kept. {BitdefenderAdvice}";

    /// <summary>
    /// Starts the run <paramref name="request"/> describes, from <paramref name="origin"/>, and returns its exit code. A
    /// request that names the recording to continue (<see cref="RecordRequest.ContinueBaseName"/>), as from the report at
    /// the start or from a window whose recorder was lost, continues it without a question. A start from a window whose
    /// recorder was lost (<see cref="StartOrigin.AutoContinue"/>) counts that loss, so that the next one within 10 minutes
    /// continues in this window. Its request is taken as it is: the window that lost the recorder hands on the lost run's
    /// own request with the rest of its --minutes, counted from that run's start, and starts nothing once they have run
    /// out, as this starter does after a recorder it shows is lost.
    /// </summary>
    public async Task<int> RunAsync(RecordRequest request, StartOrigin origin)
    {
        if (journal.List().FirstOrDefault(run => run.IsRecordingNow) is { } live)
        {
            // A class comes first: a YouTube video's recording gives way to it, and the class then starts as it would have.
            if (request.Video is not null || !live.IsVideo) return await DriveAsync(OneAtATime(live.Record, request, origin), origin);
            if (!await YieldAsync(live.Record, request, origin)) return NotDone;
            if (journal.List().FirstOrDefault(run => run.IsRecordingNow) is { } another) return await DriveAsync(OneAtATime(another.Record, request, origin), origin);
        }

        // The camera's question comes before the continue question (amendment 17): saved class, camera, continue.
        if (request.Source.Camera is not null)
        {
            switch (camera is not null ? await camera.DecideAsync(request, origin) : NoCamera())
            {
                case CameraDecision.Back:
                    logger.LogDebug("Went back at the camera's question for \"{Name}\"; nothing was started", request.Name);
                    return 0;
                case CameraDecision.Refused refused:
                    return refused.ExitCode;
                case CameraDecision.Go go:
                    request = go.Request;
                    break;
            }
        }

        if (await DecideAsync(request, origin) is not { } decided)
        {
            logger.LogDebug("Went back at the question whether to continue \"{Name}\"; nothing was started", request.Name);
            return 0;
        }

        return await DriveAsync(new Step.Launch(decided), origin);
    }

    /// <summary>A run with a camera where no camera start is registered: refused as this build refuses it, before anything records.</summary>
    private CameraDecision NoCamera()
    {
        ui.WriteLine($"{Camera.CameraRecorderNotBuilt.Words}. {CameraStart.NothingStarted}");
        return new CameraDecision.Refused(NotDone);
    }

    /// <summary>
    /// Whether the run continues a recent recording, as <paramref name="request"/> already says, as the menu's question is
    /// answered, or as the command line's flags say (see <see cref="ContinueQuestion"/>); the line it gives is shown. The
    /// request handed on says what was decided, so that neither home decides again: <see cref="ContinueChoice.Continue"/>
    /// with the recording's base name, or <see cref="ContinueChoice.New"/> without one. Null when Esc went back at the question.
    /// </summary>
    private async Task<RecordRequest?> DecideAsync(RecordRequest request, StartOrigin origin)
    {
        // A YouTube video is a new recording, without a question: a video is never continued, nor continues a class.
        if (request.Video is not null || origin == StartOrigin.VideoBatch) return request with { Continue = ContinueChoice.New, ContinueBaseName = null };
        if (request.ContinueBaseName is not null) return request with { Continue = ContinueChoice.Continue };

        var mode = request.Source.Mode;
        var camera = request.Source.Camera is not null;
        var answer = origin == StartOrigin.Menu && request.Continue == ContinueChoice.Ask
            ? await question.AskAsync(request.Name, mode, request.OutputRoot, camera)
            : question.Resolve(request.Name, mode, request.OutputRoot, request.Continue, camera);
        if (answer.GoBack) return null;

        if (answer.Hint is { } hint) ui.WriteLine(hint);
        return answer.Continue is { } files
            ? request with { Continue = ContinueChoice.Continue, ContinueBaseName = files.BaseName }
            : request with { Continue = ContinueChoice.New };
    }

    /// <summary>
    /// What to do about <paramref name="live"/>, a recording running already: from the command line, or a window continuing
    /// after a loss, a recording of the same name in the background is shown; anything else is explained.
    /// </summary>
    private Step OneAtATime(RunRecord live, RecordRequest request, StartOrigin origin) =>
        origin is StartOrigin.CommandLine or StartOrigin.AutoContinue or StartOrigin.AutoContinueFromAlert ? Winner(live, request, origin) : Explain(live, origin);

    /// <summary>
    /// Shows <paramref name="live"/>, a recording in the background of the same name as <paramref name="request"/>'s, or
    /// explains it. A YouTube video's run is never shown to another start, nor another run to a video's start: a video shares
    /// its course's name, but is never the class of that name.
    /// </summary>
    private Step Winner(RunRecord live, RecordRequest request, StartOrigin origin)
    {
        if (live.Home != RunHome.Separate || live.Request.Video is not null || request.Video is not null
            || !string.Equals(live.Request.Name, request.Name, StringComparison.OrdinalIgnoreCase)) return Explain(live, origin);

        ui.WriteLine($"\"{live.Request.Name}\" is being recorded already, since {Since(live)}; showing it.");
        return new Step.Show(live, origin == StartOrigin.AutoContinueFromAlert ? ViewMode.Alert : ViewMode.Viewer);
    }

    /// <summary>True for a start from a window the recorder opened by itself, which shows every run with its keys waiting for Enter and never records in itself.</summary>
    private static bool FromAlertWindow(StartOrigin origin) => origin == StartOrigin.AutoContinueFromAlert;

    /// <summary>Marks <paramref name="runId"/> seen once this window has shown its end; not from a window the recorder opened by itself, which nobody may have looked at.</summary>
    private void MarkSeen(string runId, StartOrigin origin)
    {
        if (FromAlertWindow(origin))
        {
            logger.LogDebug("Run {RunId} is left for the next start to report: this window opened by itself", runId);
            return;
        }

        journal.MarkSeen(runId);
    }

    /// <summary>
    /// Explains that only one recording runs at a time, with how to see <paramref name="live"/>: A in the menu, a new start
    /// otherwise; to a YouTube batch, that its videos wait for it. A video's run is named as the video it records.
    /// </summary>
    private Step.Done Explain(RunRecord live, StartOrigin origin)
    {
        var name = Named(live);
        ui.WriteLine(live.Home == RunHome.InWindow
            ? $"Only one recording at a time: {name} is being recorded in another ClassScribe window (process {live.ProcessId}), which shows it; closing that window ends the recording."
            : origin switch
            {
                StartOrigin.Menu or StartOrigin.StartupReport => $"Only one recording at a time: {name} has been recording since {Since(live)}. A shows it.",
                StartOrigin.VideoBatch => $"Only one recording at a time: {name} has been recording since {Since(live)}; the YouTube videos wait for it.",
                _ => $"Only one recording at a time: {name} has been recording since {Since(live)}. Start ClassScribe to see it, or stop it first."
            });
        logger.LogDebug("Nothing was started: run {RunId} records \"{Name}\"", live.RunId, live.Request.Name);
        return new Step.Done(NotDone);
    }

    /// <summary>A run as the words name it: "Kalam" in quotes, or the YouTube video "The creed, part 1" for a video's.</summary>
    internal static string Named(RunRecord run) =>
        run.Request.Video is { } video ? $"the YouTube video \"{video.Title}\"" : $"\"{run.Request.Name}\"";

    /// <summary>A run as the words name it at the start of a sentence: "Kalam" in quotes, or The YouTube video "The creed, part 1".</summary>
    internal static string NamedFirst(RunRecord run) => Named(run) is var named && named.StartsWith("the ", StringComparison.Ordinal) ? "T" + named[1..] : named;

    /// <summary>
    /// Asks <paramref name="video"/>, a YouTube video's run that records, to give way to <paramref name="request"/>'s class
    /// (design 2.5), and waits up to <see cref="YieldPatience"/> on the steady clock, looking at its run file every
    /// <see cref="YieldPoll"/>, for it to stop recording. A run still starting listens for its yield only once its run has
    /// begun, so while it says it is starting, it is asked again at each look. That the video is stopping is said once it has
    /// been asked. True once it has stopped; otherwise, as when nothing listens for its yield, it is explained as any recording
    /// is, with whether it could be asked and the keys that stop it, and false.
    /// </summary>
    private async Task<bool> YieldAsync(RunRecord video, RecordRequest request, StartOrigin origin)
    {
        var since = time.GetTimestamp();
        var asked = false;
        while (time.GetElapsedTime(since) < YieldPatience)
        {
            var now = journal.Find(video.RunId);
            if (now is not { IsRecordingNow: true })
            {
                logger.LogDebug("Run {RunId} of the YouTube video gave way to \"{Name}\"", video.RunId, request.Name);
                return true;
            }

            if (!asked && yield?.Ask(video.RunId) == true)
            {
                asked = true;
                ui.WriteLine($"Stopping the YouTube video \"{video.Request.Video!.Title}\" so that \"{request.Name}\" can record; what it recorded is kept.");
            }
            else if (!asked && now.Record.State != RunState.Starting)
            {
                // A run that records and does not listen, as one of an older build, will not hear a later ask either.
                break;
            }

            await Task.Delay(YieldPoll, time);
        }

        if (!asked) logger.LogDebug("Run {RunId} of the YouTube video does not listen for a class, so it could not be asked to give way", video.RunId);
        Explain(video, origin);
        ui.WriteLine(asked
            ? "Press Q twice in the window that shows the video to stop it, then start the class again."
            : "It could not be asked to stop: press Q twice in the window that shows the video to stop it, then start the class again.");
        return false;
    }

    /// <summary>Takes the run from <paramref name="first"/> through launches, views and continues until it is done, and returns its exit code.</summary>
    private async Task<int> DriveAsync(Step first, StartOrigin origin)
    {
        var attempts = new Attempts();
        if (origin is StartOrigin.AutoContinue or StartOrigin.AutoContinueFromAlert) attempts.Losses.Add(time.GetTimestamp());

        for (var step = first; ;)
        {
            switch (step)
            {
                case Step.Done done:
                    return done.ExitCode;

                case Step.InThisWindow window:
                    return await RecordInThisWindowAsync(window.Request);

                case Step.Launch launch:
                    step = await LaunchAsync(launch.Request, origin, attempts);
                    break;

                case Step.Show show:
                    step = await ShowAsync(show.Run, show.Mode, origin, attempts);
                    break;
            }
        }
    }

    /// <summary>
    /// Starts <paramref name="request"/>'s run where it records: in this window when the launcher says so, after its
    /// reason; otherwise in a recorder, shown here once it records, as its owner, or with the keys waiting for Enter from a
    /// window the recorder opened by itself. A recorder that fails is started once more under a new run id, and after the
    /// second failure the run records in this window.
    /// </summary>
    private async Task<Step> LaunchAsync(RecordRequest request, StartOrigin origin, Attempts attempts)
    {
        if (launcher.ChooseHome() is RunHomeChoice.InWindow inWindow)
        {
            if (FromAlertWindow(origin)) return Declined(request);

            ui.WriteLine(inWindow.Reason);
            return new Step.InThisWindow(request);
        }

        switch (await launcher.StartAsync(request, settings.Snapshot(), CancellationToken.None))
        {
            case LaunchResult.Started started:
                // Only a recorder that records has started for good: one still starting may yet end before it records,
                // which counts as a failed start, so that recorders that never get further are not started for ever.
                if (started.Run.State != RunState.Starting) attempts.FailedStarts = 0;
                ui.WriteLine(InTheBackground);
                return new Step.Show(started.Run, FromAlertWindow(origin) ? ViewMode.Alert : ViewMode.Owner);

            case LaunchResult.Refused refused:
                return Winner(refused.Winner.Record, request, origin);

            case LaunchResult.Failed failed:
                return FailedStart(failed.Reason, request, origin, attempts);

            default:
                throw new UnreachableException("A launch is started, refused or failed; LaunchResult has no other kind.");
        }
    }

    /// <summary>
    /// A recorder did not start, for <paramref name="reason"/>: the first time it is started once more, under a new run id;
    /// the second time in a row the run records in this window, with the reason and the advice about Bitdefender; from a
    /// window the recorder opened by itself, it is declined instead, with the same advice.
    /// </summary>
    private Step FailedStart(string reason, RecordRequest request, StartOrigin origin, Attempts attempts)
    {
        if (++attempts.FailedStarts < 2)
        {
            ui.WriteLine(reason);
            ui.WriteLine("Starting the recorder once more.");
            return new Step.Launch(request with { RunId = NewRunId() });
        }

        if (FromAlertWindow(origin))
        {
            ui.WriteLine(reason);
            ui.WriteLine(BitdefenderAdvice);
            return Declined(request);
        }

        ui.WriteLine($"Recording in this window instead. {reason}");
        ui.WriteLine(InThisWindowAfterFailures);
        return new Step.InThisWindow(request with { RunId = NewRunId() });
    }

    /// <summary>
    /// Declines to record <paramref name="request"/>'s run in this window, which the recorder opened by itself: its keys are
    /// read with no wait for Enter there, and a run must never act on keys typed into such a window by mistake. The
    /// recording's audio is kept, and the next start reports the loss, with C to continue it.
    /// </summary>
    private Step.Done Declined(RecordRequest request)
    {
        ui.WriteLine(NotInThisWindow);
        logger.LogDebug("Run {RunId} is not recorded in this window, which the recorder opened by itself; the next start reports the recording", request.RunId);
        return new Step.Done(NotDone);
    }

    /// <summary>Shows <paramref name="run"/> as <paramref name="mode"/> says, and decides what follows from how the view ended.</summary>
    private async Task<Step> ShowAsync(RunRecord run, ViewMode mode, StartOrigin origin, Attempts attempts)
    {
        var shown = await viewer.ShowAsync(run, mode, CancellationToken.None);
        switch (shown.End)
        {
            case ViewEnd.RunEnded:
                // Not while the run's file still says it records, as a recorder that Windows ended leaves it: the next
                // start reports the recording, with C to continue it.
                if (!FromAlertWindow(origin)) journal.MarkSeenIfOver(run.RunId);
                else logger.LogDebug("Run {RunId} is left for the next start to report: this window opened by itself", run.RunId);
                return new Step.Done(shown.ExitCode ?? shown.Outcome?.ExitCode ?? NotDone);

            case ViewEnd.Left:
                if (origin == StartOrigin.CommandLine) ui.WriteLine(CarriesOn);
                return new Step.Done(0);

            case ViewEnd.RecorderLost:
                return AfterLoss(run, origin, attempts);

            default:
                logger.LogDebug("The view of run {RunId} ended: {End}", run.RunId, shown.End);
                return new Step.Done(NotDone);
        }
    }

    /// <summary>
    /// The view lost the recorder of <paramref name="shown"/>; its run file says what happened. A recorder that still runs
    /// lost only its link, and carries on. One that never recorded counts as a recorder that did not start. One lost while
    /// recording is continued as the same recording's next part, but a YouTube video's, which is never continued: its audio
    /// so far is kept, and the same command records the video again; one lost while stopping or transcribing leaves complete
    /// audio to transcribe; one whose run ended gives its outcome.
    /// </summary>
    private Step AfterLoss(RunRecord shown, StartOrigin origin, Attempts attempts)
    {
        var view = journal.Find(shown.RunId);
        if (view is { IsAlive: true })
        {
            ui.WriteLine("Lost the connection to the recording; it carries on. Start ClassScribe again to see it.");
            return new Step.Done(NotDone);
        }

        var lost = view?.Record ?? shown;
        var name = lost.Request.Name;
        switch (lost.State)
        {
            case RunState.Recording when lost.BaseName is not null && lost.Request.Video is { } video:
                MarkSeen(lost.RunId, origin);
                ui.WriteLine($"The recorder of the YouTube video \"{video.Title}\" ended unexpectedly at {LocalTime.Of(time.GetUtcNow(), time, seconds: true)}. Its audio so far is kept; a video is never continued, so the same command records it again.");
                return new Step.Done(NotDone);

            case RunState.Recording when lost.BaseName is not null:
                return Continue(lost, origin, attempts);

            case RunState.Starting or RunState.Recording:
                return FailedStart("The recorder ended before it recorded.", lost.Request, origin, attempts);

            case RunState.Stopping:
                MarkSeen(lost.RunId, origin);
                ui.WriteLine($"The recorder ended unexpectedly while it stopped the recording \"{name}\"; its audio is kept.{TranscribeCommand(lost)}");
                return new Step.Done(NotDone);

            case RunState.Transcribing:
                MarkSeen(lost.RunId, origin);
                ui.WriteLine($"The transcript of \"{name}\" was not finished; its audio is complete. If the upload had finished, ElevenLabs may bill it again.{TranscribeCommand(lost)}");
                return new Step.Done(NotDone);

            default:
                MarkSeen(lost.RunId, origin);
                var outcome = lost.Outcome;
                ui.WriteLine($"\"{name}\" ended: {Sentence.Of(outcome?.Message is { Length: > 0 } message ? message : "the recorder gave no outcome")}");
                return new Step.Done(outcome?.ExitCode ?? NotDone);
        }
    }

    /// <summary>
    /// Continues <paramref name="lost"/>'s recording, lost while it recorded, as its next part with the rest of its
    /// --minutes: in a new recorder, or in this window when the recorder was lost before within <see cref="LossWindow"/>;
    /// from a window the recorder opened by itself, the second loss is declined instead, and the lost run is left for the
    /// next start to report. Not when its --minutes had run out: the recording was over.
    /// </summary>
    private Step Continue(RunRecord lost, StartOrigin origin, Attempts attempts)
    {
        MarkSeen(lost.RunId, origin);
        var at = LocalTime.Of(time.GetUtcNow(), time, seconds: true);
        double? minutes = lost.Request.Minutes is { } given ? given - (time.GetUtcNow() - lost.StartedUtc).TotalMinutes : null;
        if (minutes <= 0)
        {
            ui.WriteLine($"The recorder ended unexpectedly at {at}, after the time --minutes gave it had run out, so the recording is not continued.{TranscribeCommand(lost)}");
            return new Step.Done(NotDone);
        }

        var now = time.GetTimestamp();
        attempts.Losses.RemoveAll(loss => time.GetElapsedTime(loss, now) > LossWindow);
        attempts.Losses.Add(now);

        var continued = lost.Request with { RunId = NewRunId(), Continue = ContinueChoice.Continue, ContinueBaseName = lost.BaseName, Minutes = minutes };
        logger.LogDebug("The recorder of run {RunId} was lost while it recorded; continuing {Recording} as run {Continued}", lost.RunId, lost.BaseName, continued.RunId);
        if (attempts.Losses.Count < 2)
        {
            ui.WriteLine($"The recorder ended unexpectedly at {at}; continuing the same recording.");
            return new Step.Launch(continued);
        }

        ui.WriteLine($"The recorder ended unexpectedly again at {at}, the second time within {LossWindow.TotalMinutes:0} minutes, which most likely means Bitdefender is ending it: check its Notifications (Protection > Advanced Threat Defense).");
        if (FromAlertWindow(origin)) return Declined(continued);

        ui.WriteLine("Continuing the same recording in this window instead: closing it ends the recording, and the audio so far is kept.");
        return new Step.InThisWindow(continued);
    }

    /// <summary>
    /// Runs <paramref name="request"/>'s run in this window: the record command, resolved in a scope of its own, and its
    /// exit code. Once the run has ended and shown its end, however it ended, the upload to Google Drive follows in the same
    /// scope, so its lines go into the recording's log too; it never changes the exit code (see
    /// <see cref="IDriveAutoUpload"/>). This window shows the run's outcome, so its run file is marked seen once the run is
    /// over, however it ended here; a window closed meanwhile leaves the file as it was, for the next start to report.
    /// </summary>
    private async Task<int> RecordInThisWindowAsync(RecordRequest request)
    {
        logger.LogDebug("Recording run {RunId} in this window", request.RunId);
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            try
            {
                return await scope.ServiceProvider.GetRequiredService<IRecordRun>().RunAsync(request);
            }
            finally
            {
                if (scope.ServiceProvider.GetService<IDriveAutoUpload>() is { } drive) await drive.AfterRunAsync();
            }
        }
        finally
        {
            journal.MarkSeen(request.RunId);
        }
    }

    /// <summary>
    /// " To transcribe it: " and the command that transcribes <paramref name="run"/>'s recording, which ends the line it is
    /// added to, so that the command can be copied as it is; empty when it made none.
    /// </summary>
    private static string TranscribeCommand(RunRecord run) =>
        run is { Folder: { } folder, BaseName: { } baseName }
            ? $" To transcribe it: {CommandLine.Transcribe(new RecordingFiles(folder, baseName).Segment(1))}"
            : "";

    /// <summary>Since when <paramref name="run"/> records, in local time, and for how long: "19:00 (00:42:13)".</summary>
    private string Since(RunRecord run) => $"{LocalTime.Of(run.StartedUtc, time)} ({Durations.Format(time.GetUtcNow() - run.StartedUtc)})";

    /// <summary>A new run id, for a run started again or continued.</summary>
    private string NewRunId() => RunIds.New(time.GetUtcNow());

    /// <summary>What has happened to the starts of one run so far: recorders that did not start in a row, and when recorders were lost.</summary>
    private sealed class Attempts
    {
        /// <summary>How many recorders in a row did not start.</summary>
        public int FailedStarts { get; set; }

        /// <summary>When recorders were lost while recording, on the steady clock, within the last <see cref="LossWindow"/>.</summary>
        public List<long> Losses { get; } = [];
    }

    /// <summary>The next step of a run.</summary>
    private abstract record Step
    {
        /// <summary>The run is done, with its exit code.</summary>
        /// <param name="ExitCode">The exit code.</param>
        public sealed record Done(int ExitCode) : Step;

        /// <summary>Start a recorder for the request, or record in this window when the launcher says so.</summary>
        /// <param name="Request">The run to start.</param>
        public sealed record Launch(RecordRequest Request) : Step;

        /// <summary>Show a run in the background in this window.</summary>
        /// <param name="Run">The run.</param>
        /// <param name="Mode">How: as its owner, or as another window.</param>
        public sealed record Show(RunRecord Run, ViewMode Mode) : Step;

        /// <summary>Record in this window.</summary>
        /// <param name="Request">The run to record.</param>
        public sealed record InThisWindow(RecordRequest Request) : Step;
    }
}
