using ClassScribe.Desktop;
using ClassScribe.Recorder;
using ClassScribe.Runs;
using ClassScribe.Storage;
using Microsoft.Extensions.Logging;

namespace ClassScribe.Commands;

/// <summary>
/// The hidden attach command: ClassScribe attach shows the recording in progress, and ClassScribe attach --alert &lt;run
/// id&gt; shows the run that id names in a window the recorder opened by itself, which ignores every key until Enter (see
/// <see cref="RecorderView"/>). The menu shows a recording in progress through it too. It finds the run in the run files: the
/// newest one recording now, else the newest one being transcribed in the background; or the one named. A run whose process
/// is gone is reported from its file without connecting to anything: its outcome, as when the recorder opened a window for
/// a failed end and was gone by the time the window started, or that it ended unexpectedly; the window's title and taskbar
/// say the same, and a failure flashes it. At the end of a run shown, the run's exit code is the command's, and the run is
/// marked seen. A recorder lost while the view showed it recording is continued at once as the next part of the same
/// recording, through the starter (<see cref="StartOrigin.AutoContinue"/>, or <see cref="StartOrigin.AutoContinueFromAlert"/>
/// from a window the recorder opened by itself, whose keys keep waiting for Enter).
/// </summary>
/// <remarks>
/// <para>
/// A window the recorder opened by itself first turns Ctrl+C back on, which Windows starts it with turned off, as the
/// recorder has it, so that Ctrl+C counts there after Enter as in any window.
/// </para>
/// <para>
/// Such a window does not mark the run seen: nobody may have looked at it, and it opens no results, so the next start of
/// ClassScribe reports the run, with what can be done about it. Nor does any window mark seen a run whose file still says
/// it records once its end was shown: a recorder that Windows ended leaves its file so, for the next start to report.
/// </para>
/// </remarks>
/// <param name="journal">The run files: which run to show, and how it ended.</param>
/// <param name="viewer">Shows the run.</param>
/// <param name="commands">Starts the run that continues a lost recorder's recording.</param>
/// <param name="ui">The window's screen.</param>
/// <param name="window">The window's title, taskbar button and flash, for a run reported from its file.</param>
/// <param name="time">The clock for the times written, and for the minutes a continued recording has left.</param>
/// <param name="logger">Where the command notes what it found and did; what Gulzar needs to read, it writes to the window itself.</param>
public sealed class AttachCommand(RunJournal journal, IRunViewer viewer, ICommandRunner commands, IConsoleUi ui, IWindowSignals window, TimeProvider time, ILogger<AttachCommand> logger)
{
    /// <summary>The exit code when there is nothing to show, the run ended without a saved result, or the recorder could not be reached.</summary>
    private const int Failed = 1;

    /// <summary>
    /// Turns Ctrl+C back on in this window and returns 0 when Windows did, else its error (see <see cref="ConsoleCtrlC.TurnOn"/>).
    /// Tests count the calls through it, so that only the test of the call itself reaches Windows.
    /// </summary>
    internal Func<int> TurnOnCtrlC { get; init; } = ConsoleCtrlC.TurnOn;

    /// <summary>
    /// The request that continues the recording of <paramref name="lost"/>, whose recorder was lost while it recorded, as its
    /// next part, from <paramref name="now"/>: the same name, source, folder, choice to transcribe and to open, a new run id,
    /// and the minutes it had left of --minutes, counted from its start. Null when those minutes had run out: the recording
    /// was due to stop; and null for a YouTube video's run, which is never continued (design 2.5): its audio so far is kept,
    /// and the same command records the video again.
    /// </summary>
    public static RecordRequest? ContinuationOf(RunRecord lost, DateTimeOffset now)
    {
        if (lost.Request.Video is not null) return null;

        double? minutes = null;
        if (lost.Request.Minutes is { } total)
        {
            var left = total - (now - lost.StartedUtc).TotalMinutes;
            if (left <= 0) return null;

            minutes = left;
        }

        return lost.Request with
        {
            RunId = RunIds.New(now),
            Continue = ContinueChoice.Continue,
            ContinueBaseName = lost.BaseName,
            Minutes = minutes
        };
    }

    /// <summary>
    /// Shows the run <paramref name="runId"/> names, or, when it is null, the recording in progress; with
    /// <paramref name="alert"/>, in a window the recorder opened by itself. Returns the exit code: the run's own at its end,
    /// 0 after Esc, 1 when there is nothing to show or the recorder could not be reached, and the continued run's after a
    /// recorder lost while recording.
    /// </summary>
    public async Task<int> RunAsync(string? runId, bool alert)
    {
        var mode = alert ? ViewMode.Alert : ViewMode.Viewer;
        if (alert) TurnCtrlCOn();

        if ((runId is null ? InProgress() : Named(runId)) is not { } found) return Failed;

        var run = found.Record;

        // A run in another window that has ended, while that window waits for Enter to close, is over: it has no recorder to
        // show it from, but its file says how it ended.
        if (!found.IsAlive || (run.Home != RunHome.Separate && OutcomeOf(run) is not null)) return ShowEnded(run, mode);

        if (run.Home != RunHome.Separate)
        {
            var (doing, what) = run.State != RunState.Transcribing ? ("recorded", "recording")
                : LessonRuns.OnlyItsVideoLeft(run) ? ("made into its lesson video", "making of its video")
                : ("transcribed", "transcription");
            Say($"\"{run.Request.Name}\" is being {doing} in another ClassScribe window (process {run.ProcessId}), which shows it; closing that window ends the {what}.",
                LogLevel.Warning);
            return Failed;
        }

        logger.LogDebug("Showing run {RunId} ({Mode})", run.RunId, mode);
        var shown = await viewer.ShowAsync(run, mode, CancellationToken.None);
        logger.LogDebug("The view of run {RunId} ended: {End}", run.RunId, shown.End);

        switch (shown.End)
        {
            case ViewEnd.RunEnded:
                MarkSeen(run, mode);
                return shown.ExitCode ?? shown.Outcome?.ExitCode ?? Failed;

            case ViewEnd.Left:
                // A window opened by itself closes once it is left; the menu, which shows a recording through this command
                // too, says itself that A shows it again.
                var carriesOn = CarriesOn(journal.Find(run.RunId)?.Record ?? run);
                Say(mode == ViewMode.Alert ? $"{carriesOn} Start ClassScribe to see it." : carriesOn);
                return 0;

            case ViewEnd.RecorderLost:
                return await LostAsync(run, mode);

            default:
                // Not answering, or of another build: the view has said why, and what can be done.
                return Failed;
        }
    }

    /// <summary>
    /// Turns Ctrl+C back on in a window the recorder opened by itself. Windows starts that window with Ctrl+C turned off,
    /// as the recorder has it as the root of a process group of its own; turned back on, Ctrl+C counts here after Enter as
    /// in any window: twice stops the class, and once after the stop cancels the transcription. When Windows does not
    /// turn it on, Q twice still stops the class, so the window carries on and the log notes why.
    /// </summary>
    private void TurnCtrlCOn()
    {
        if (TurnOnCtrlC() is var error and not 0)
            logger.LogDebug("Could not turn Ctrl+C back on in this window, which the recorder opened by itself (Windows error {Error}); Ctrl+C does nothing here", error);
    }

    /// <summary>The newest run recording now, else the newest being transcribed in the background; null, said, when there is none.</summary>
    private RunView? InProgress()
    {
        var runs = journal.List();
        if ((runs.FirstOrDefault(run => run.IsRecordingNow) ?? runs.FirstOrDefault(run => run.IsTranscribingNow)) is { } found) return found;

        Say("Nothing is being recorded or transcribed in the background now.");
        return null;
    }

    /// <summary>The run <paramref name="runId"/> names; null, said, when it has no run file this build can read.</summary>
    private RunView? Named(string runId)
    {
        if (journal.Find(runId) is { } found) return found;

        Say($"There is no ClassScribe run {runId}.", LogLevel.Warning);
        return null;
    }

    /// <summary>
    /// A run whose process was gone before the window came, reported from its file: its outcome, with its exit code, when it
    /// ended; otherwise that it ended unexpectedly, that its transcript was not finished, or that it never started.
    /// </summary>
    private int ShowEnded(RunRecord run, ViewMode mode)
    {
        if (OutcomeOf(run) is { } outcome)
        {
            Say($"The run of \"{run.Request.Name}\" (started {WindowText.Local(time, run.StartedUtc, "HH:mm")}) has ended.");
            return ShowOutcome(run, outcome, mode);
        }

        return ShowUnexpectedEnd(run, run.HeartbeatUtc);
    }

    /// <summary>
    /// The recorder was lost while the view showed the run. When its file says the run had ended meanwhile, its outcome is
    /// shown; a recording is continued as its next part, from any window, the one the recorder opened by itself included,
    /// whose keys then keep waiting for Enter; a recording whose minutes have run out, and anything else, is reported.
    /// </summary>
    private async Task<int> LostAsync(RunRecord shown, ViewMode mode)
    {
        var run = journal.Find(shown.RunId)?.Record ?? shown;
        if (OutcomeOf(run) is { } outcome) return ShowOutcome(run, outcome, mode);

        var now = time.GetUtcNow();
        if (run.State == RunState.Recording && ContinuationOf(run, now) is { } next)
        {
            Say($"The recorder ended unexpectedly at {WindowText.Local(time, now, "HH:mm:ss")}; continuing the same recording.", LogLevel.Warning);
            logger.LogDebug("Continuing the recording of run {RunId} in run {Next} ({Mode})", run.RunId, next.RunId, mode);
            return await commands.RecordAsync(next, mode == ViewMode.Alert ? StartOrigin.AutoContinueFromAlert : StartOrigin.AutoContinue);
        }

        return ShowUnexpectedEnd(run, now);
    }

    /// <summary>
    /// Reports a run whose process ended without its end, at about <paramref name="at"/>: a recording that ended
    /// unexpectedly, a transcript not finished, or a run that never started; the window says so in red, with a flash.
    /// </summary>
    private int ShowUnexpectedEnd(RunRecord run, DateTimeOffset at)
    {
        Say(run.State switch
        {
            RunState.Recording or RunState.Stopping => EndedUnexpectedly(run, at),
            RunState.Transcribing => TranscriptNotFinished(run),
            _ => $"The recording \"{run.Request.Name}\" did not start: its background process ended first."
        }, LogLevel.Warning);
        ShowOnWindow(RecorderView.LostState(RecorderView.StateOf(run)));
        return Failed;
    }

    /// <summary>How <paramref name="run"/> ended, when its file says it has; null while it is under way or when it ended unexpectedly.</summary>
    private static RunOutcome? OutcomeOf(RunRecord run) =>
        run.State is RunState.Finished or RunState.Failed or RunState.Refused ? run.Outcome : null;

    /// <summary>Shows <paramref name="outcome"/>, in words and on the window's title and taskbar, marks the run seen, and returns its exit code.</summary>
    private int ShowOutcome(RunRecord run, RunOutcome outcome, ViewMode mode)
    {
        var level = outcome.ExitCode == 0 ? LogLevel.Information : LogLevel.Warning;
        foreach (var line in RecorderView.OutcomeLines(outcome)) Say(line, level);
        ShowOnWindow(RecorderView.EndedState(RecorderView.StateOf(run), outcome));

        MarkSeen(run, mode);
        return outcome.ExitCode;
    }

    /// <summary>
    /// Shows <paramref name="state"/> on the window's title and taskbar, as the view of a run does, flashing it for a
    /// failure or an Alert. A run reported from its file is shown on a window state of its own.
    /// </summary>
    private void ShowOnWindow(RecorderState state) => new WindowStatusSink(window).Publish(state);

    /// <summary>
    /// Marks the run seen once its end has been shown, so that the next start does not report it; not in a window opened by
    /// itself, and not while the run's file still says it records, as a recorder that Windows ended leaves it.
    /// </summary>
    private void MarkSeen(RunRecord run, ViewMode mode)
    {
        if (mode == ViewMode.Alert)
        {
            logger.LogDebug("Run {RunId} is left for the next start to report: this window opened by itself", run.RunId);
            return;
        }

        journal.MarkSeenIfOver(run.RunId);
    }

    /// <summary>What carries on in the background once the window has left <paramref name="run"/>: its transcription, a lesson's video when only that is left (see <see cref="LessonRuns.OnlyItsVideoLeft"/>), or its recording.</summary>
    private static string CarriesOn(RunRecord run) => run.State switch
    {
        RunState.Transcribing when LessonRuns.OnlyItsVideoLeft(run) && run.Folder is { Length: > 0 } lesson =>
            $"The lesson's video is being made in the background; it will appear in {lesson}.",
        RunState.Transcribing when run.Folder is { Length: > 0 } folder => $"The transcription carries on in the background; its transcript will appear in {folder}.",
        RunState.Transcribing => "The transcription carries on in the background.",
        _ => "The recording carries on in the background."
    };

    /// <summary>That the recording of <paramref name="run"/> ended unexpectedly at about <paramref name="at"/>, how far it got, and that its audio is kept.</summary>
    private string EndedUnexpectedly(RunRecord run, DateTimeOffset at)
    {
        var when = WindowText.Local(time, at, "HH:mm");
        return run.BaseName is { Length: > 0 } baseName
            ? $"The recording \"{run.Request.Name}\" ({baseName}) ended unexpectedly at about {when}, {Durations.Format(run.Position)} in. Its audio is kept. Start ClassScribe to continue it or to transcribe it."
            : $"The recording \"{run.Request.Name}\" ended unexpectedly at about {when}, before anything was recorded.";
    }

    /// <summary>That the transcript of <paramref name="run"/> was not finished, with the command that transcribes its recording; for a lesson with only its video left, its video.</summary>
    private static string TranscriptNotFinished(RunRecord run)
    {
        if (LessonRuns.OnlyItsVideoLeft(run)) return LessonRuns.VideoNotFinished(run);

        var said = $"The transcript of \"{run.Request.Name}\" was not finished; its audio is complete.";
        var recording = run is { Folder: { Length: > 0 } folder, BaseName: { Length: > 0 } baseName } ? new RecordingFiles(folder, baseName).Segment(1) : run.Folder;
        return recording is null
            ? said
            : $"{said} To transcribe it (if the upload had finished, ElevenLabs may bill it again), run: {CommandLine.Transcribe(recording)}";
    }

    /// <summary>Writes a line as the log shows its lines, the time first, in the window.</summary>
    private void Say(string text, LogLevel level = LogLevel.Information) => ui.WriteLine(WindowText.Line(time, level, text));
}
