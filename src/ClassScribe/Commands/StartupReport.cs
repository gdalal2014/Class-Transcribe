using System.Globalization;
using ClassScribe.Audio;
using ClassScribe.Drive;
using ClassScribe.Recorder;
using ClassScribe.Runs;
using ClassScribe.Settings;
using ClassScribe.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClassScribe.Commands;

/// <summary>
/// The report at the start (sections 1.7 and 1.14 of the design), from the run files. A recording whose recorder ended
/// while it recorded is reported with when it ended, how far in, and its cause when it is known (Windows restarted, shut
/// down or signed out; its window was closed), or else where to look (Bitdefender's Notifications), at every start until
/// it is dealt with: C continues it as its next part, with the run's own request, when it may still be continued and no
/// other recording runs; T transcribes what was recorded; F forgets it; Enter goes on. A transcription that was
/// interrupted is reported the same way, with T and F. An outcome nobody saw, such as a transcript made while no window
/// showed the run, is shown once, with O to open the transcript. On the command line the report prints the commands that
/// would do the same, asks nothing, and the command carries on. A recording continued by a later run, transcribed since,
/// or whose audio is gone has been dealt with: it is marked seen without a word. One on a drive that is not there now, such
/// as a USB drive unplugged, waits for a start that finds the drive. After the runs, once Google Drive is set up, what
/// waits to go up there is said, with its size and its last try (see <see cref="DriveQueueReport"/>), and U uploads it now;
/// on the command line, the command that does. The menu's header names what runs in the background, and ends with Google
/// Drive's line (see <see cref="HeaderLines"/>), and the menu shows the recording in progress (see <see cref="RecordingNow"/>).
/// </summary>
/// <remarks>
/// The report carries out the choice made itself, since the run it is about is known here only: C through
/// <see cref="IRunStarter.RecordAsync"/> as a run started from the report, T through
/// <see cref="ICommandRunner.TranscribeAsync"/>, U through <see cref="ICommandRunner.DriveUploadAsync"/>, each once the
/// settings have passed the check, as a menu choice does; O and F itself. The choice returned only says what was done; the
/// caller does not carry it out again.
/// </remarks>
public sealed class StartupReport(IRunJournal journal, IClassFolders folders, IRecordingFolder recordings, CommandLine commandLine, ICommandRunner commands, RunGate gate, IConsoleUi ui,
    IDesktop desktop, IProcessLiveness liveness, DriveQueueReport drive, IOptionsMonitor<RecordingOptions> recording, TimeProvider time, ILogger<StartupReport> logger) : IStartupReport
{
    /// <summary>Where the second offer on a line of offers begins, as in "  C  continue it now as its next part     T  transcribe what was recorded".</summary>
    private const int SecondColumn = 42;

    /// <summary>The line that names where to look when nobody stopped a recording and the cause is not known.</summary>
    private const string LookAtBitdefender = "If you did not stop it, check Bitdefender's Notifications (Protection > Advanced Threat Defense).";

    /// <summary>The last line of a report on the command line, which cannot ask: how to forget the run, which only the menu's report offers.</summary>
    private const string ForgetInTheMenu = "To forget it: start ClassScribe without a command, then press F.";

    /// <summary>
    /// Shows the report. Interactive, as at the menu's start, each run is shown in turn with what can be done, and the key
    /// pressed is carried out: C and T run their work and end the report, the other runs waiting for the next start; O, F
    /// and Enter go on to the next run; then what waits to go up to Google Drive, with U to upload it now and Enter to go on.
    /// Returns the last choice carried out, or null for none. Not interactive, as before a command, the runs going on now get
    /// a line each, the others are printed with the commands that deal with them, and so is what waits for Google Drive;
    /// nothing is asked, and null is returned. Outcomes nobody saw are shown once in either case.
    /// </summary>
    public async Task<StartupChoice?> ShowAsync(bool interactive)
    {
        var runs = journal.List();
        if (!interactive)
        {
            foreach (var line in LinesAboutRunsNow(runs, header: false)) ui.WriteLine(line);
        }

        StartupChoice? made = null;
        foreach (var run in runs.Where(run => run.HasUnseenOutcome))
        {
            if (await ShowOutcomeAsync(run.Record, interactive) is { } opened) made = opened;
            if (await OfferTheVideoAfterTheOutcomeAsync(run, interactive) is { } video) return video;
        }

        foreach (var run in runs.Where(run => run.EndedUnexpectedly || run.TranscriptionInterrupted))
        {
            if (Report(run, runs) is not { } report) continue;

            if (!interactive)
            {
                PrintWithCommands(report);
                continue;
            }

            var (choice, ended) = await OfferAsync(report);
            if (choice is not null) made = choice;
            if (ended) return choice;
        }

        return await ReportWhatWaitsForDriveAsync(interactive) ?? made;
    }

    /// <summary>
    /// The lines for the menu's header about the runs going on now: the recording in the background, since when and for
    /// how long, with A to show it; a recording in another window, which has no recorder to show and ends when that window
    /// is closed; and each transcription in the background, with how far its upload is. Last, once Google Drive is set up,
    /// its line: what waits to go up, or what goes up now (see <see cref="DriveQueueReport.HeaderLine"/>).
    /// </summary>
    public IReadOnlyList<string> HeaderLines() => [.. LinesAboutRunsNow(journal.List(), header: true), .. drive.HeaderLine() is { } line ? [line] : Array.Empty<string>()];

    /// <summary>
    /// What waits to go up to Google Drive, once Drive is set up and anything waits. Interactive, signed in, U uploads it now
    /// (once the settings pass, as a menu choice), which ends the report, and Enter goes on; not signed in, how to sign in is
    /// said and nothing is asked. Not interactive, the command that uploads it is printed. Returns the upload chosen, or null.
    /// </summary>
    private async Task<StartupChoice?> ReportWhatWaitsForDriveAsync(bool interactive)
    {
        if (drive.Waiting() is not { } waiting) return null;

        ui.WriteLine(waiting.Line);
        var it = waiting.Count == 1 ? "it" : "them";
        if (!interactive)
        {
            ui.WriteLine(waiting.SignedIn
                ? $"To upload {it}: {commandLine.DriveUpload(null)}"
                : $"{IGoogleAccount.NotSignedIn} Then, to upload {it}: {commandLine.DriveUpload(null)}");
            return null;
        }

        if (!waiting.SignedIn)
        {
            ui.WriteLine($"ClassScribe is not signed in to Google Drive: 9 > S in the menu signs in, then 9 > U uploads {it}.");
            return null;
        }

        WriteOffers([("U", $"upload {it} now"), ("Enter", "go on to the menu")]);
        while (true)
        {
            if (await ui.ReadKeyAsync(CancellationToken.None) is not { } key || key.Key is ConsoleKey.Enter or ConsoleKey.Escape || IsCtrlC(key)) return null;
            if (key.Key == ConsoleKey.U) return (await CarryOutAsync(run: null, StartupAction.Upload, () => commands.DriveUploadAsync(null))).Choice;
        }
    }

    /// <summary>The recording in the background now, which the menu shows at once; null when there is none. A recording in another window has no recorder to show.</summary>
    public RunRecord? RecordingNow() => journal.List().FirstOrDefault(run => run.IsRecordingNow && run.Record.Home == RunHome.Separate)?.Record;

    /// <summary>
    /// The lines about the runs going on now, for the menu's header (<paramref name="header"/>) or before a command. A YouTube
    /// video's run is named as the video, never as its course, which a class of that name would be taken for; a transcript a
    /// YouTube batch makes in another window is said to be made there.
    /// </summary>
    private List<string> LinesAboutRunsNow(IReadOnlyList<RunView> runs, bool header)
    {
        var lines = new List<string>();
        foreach (var run in runs.Where(run => run.IsRecordingNow))
        {
            var record = run.Record;
            var since = $"{RecordStarter.Named(record)} since {When(record.StartedUtc)} ({Durations.Format(time.GetUtcNow() - record.StartedUtc)})";
            lines.Add(record.Home == RunHome.InWindow
                ? $"{RecordStarter.NamedFirst(record)} is being recorded in another ClassScribe window (process {record.ProcessId}), which shows it; closing that window ends the recording."
                : header
                    ? $"Recording now: {since}. A shows it."
                    : $"Recording now in the background: {since}. Start ClassScribe to see it.");
        }

        foreach (var run in runs.Where(run => run.IsTranscribingNow))
        {
            var progress = run.Record.UploadPercent switch
            {
                null => "",
                >= 100 => " (uploaded; waiting for the transcript)",
                var percent => string.Create(CultureInfo.InvariantCulture, $" (uploading {percent}%)")
            };
            lines.Add(run.Record.Home == RunHome.InWindow
                ? $"Being transcribed in another ClassScribe window (process {run.Record.ProcessId}): {RecordStarter.Named(run.Record)}{progress}."
                : $"Being transcribed in the background: {RecordStarter.Named(run.Record)}{progress}.");
        }

        return lines;
    }

    /// <summary>
    /// Shows the outcome of a run nobody saw end, and marks it seen: a transcript, with O to open it when someone can type
    /// and it is there; a recording made without a transcript, with the command to transcribe it; or what went wrong, with
    /// the command to try again, unless the message already holds it, as the run's own messages do, so that the command is
    /// shown once. Returns the choice to open the transcript when O was pressed, which is carried out.
    /// </summary>
    private async Task<StartupChoice?> ShowOutcomeAsync(RunRecord run, bool interactive)
    {
        journal.MarkSeen(run.RunId);
        var outcome = run.Outcome!;
        var name = RecordStarter.NamedFirst(run);
        switch (outcome.Kind)
        {
            case OutcomeKind.Transcribed when outcome.TranscriptPath is { } transcript:
                var canOpen = interactive && File.Exists(transcript);
                ui.WriteLine($"{name} was transcribed in the background: {transcript}.{(canOpen ? " O opens it; any other key goes on." : "")}");
                if (!canOpen || (await ui.ReadKeyAsync(CancellationToken.None))?.Key != ConsoleKey.O) return null;

                desktop.OpenFile(transcript);
                return new StartupChoice(run.RunId, StartupAction.Open);

            case OutcomeKind.Recorded:
                ui.WriteLine($"{name} was recorded in the background: {outcome.Folder ?? run.Folder}.");
                if (outcome.RetryCommand is { } transcribe) ui.WriteLine($"To transcribe it: {transcribe}");
                return null;

            default:
                ui.WriteLine($"{name} ended in the background: {Sentence.Of(outcome.Message)}");
                if (outcome.RetryCommand is { } retry && !outcome.Message.Contains(retry, StringComparison.Ordinal)) ui.WriteLine($"To try again: {retry}");
                return null;
        }
    }

    /// <summary>
    /// After the outcome of a lesson's run nobody saw, when its video is still to be made by its files and its process is
    /// gone (a cancel, a failure, a time limit, a record-only lesson whose video failed): says so and, interactive, offers V
    /// to make it now, free, and Enter to go on; not interactive, prints the command. Asked once, as the outcome is shown
    /// once. Returns the choice when V was pressed, which ends the report.
    /// </summary>
    private async Task<StartupChoice?> OfferTheVideoAfterTheOutcomeAsync(RunView run, bool interactive)
    {
        if (run.IsAlive || !LessonRuns.VideoNotMade(run.Record, folders)) return null;

        ui.WriteLine($"The lesson video of {RecordStarter.Named(run.Record)} was not made; its sound and its camera pieces are kept.");
        if (!interactive)
        {
            ui.WriteLine($"To make it, free: {LessonRuns.FinishVideoCommand(run.Record, recordings, commandLine)}");
            return null;
        }

        WriteOffers([("V", MakeTheVideoNow), ("Enter", "go on to the menu")]);
        while (true)
        {
            if (await ui.ReadKeyAsync(CancellationToken.None) is not { } key || key.Key is ConsoleKey.Enter or ConsoleKey.Escape || IsCtrlC(key)) return null;
            if (key.Key == ConsoleKey.V) return (await CarryOutAsync(run.Record, StartupAction.MakeVideo, () => MakeTheVideoAsync(run.Record))).Choice;
        }
    }

    /// <summary>The words of V in the report: the lesson's video made now, from its pieces and its sound, for free.</summary>
    public const string MakeTheVideoNow = "make the lesson's video now, free";

    /// <summary>Makes <paramref name="run"/>'s lesson video through the runner, as finish-video does, by one of the lesson's files.</summary>
    private Task<int> MakeTheVideoAsync(RunRecord run) =>
        commands.FinishVideoAsync(new FinishVideoRequest(LessonRuns.FilesOf(run) is { } files ? recordings.FindSegments(files).FirstOrDefault() ?? files.Folder : run.Folder!));

    /// <summary>
    /// What to report about <paramref name="run"/>, which ended unexpectedly or whose transcription was interrupted; null
    /// when it has been dealt with since, which marks it seen, or when its recording cannot be looked at now, which leaves
    /// it for the next start. A lesson's run is dealt with only once its video is made too, and offers T only when it was to
    /// be transcribed and has no transcript, and V while its video is still to be made (teach-video 6.9).
    /// </summary>
    private RunReport? Report(RunView run, IReadOnlyList<RunView> runs)
    {
        var record = run.Record;
        try
        {
            if (record.BaseName is null || record.Folder is null) return DealtWith(record, "nothing was recorded");

            var files = new RecordingFiles(record.Folder, record.BaseName);
            if (!Directory.Exists(files.Folder) && Path.GetPathRoot(files.Folder) is { Length: > 0 } drive && !Directory.Exists(drive))
            {
                // Not marked seen: the drive, such as a USB drive or a network share, may be back at the next start.
                logger.LogDebug("Run {RunId} is not reported now: the drive {Drive} of its recording is not there", record.RunId, drive);
                return null;
            }

            var lesson = LessonRuns.IsLesson(record);
            var videoNotMade = LessonRuns.VideoNotMade(record, folders);
            var dealtWith =
                runs.Any(other => other.Record.RunId != record.RunId && other.Record.StartedUtc >= record.StartedUtc && ContinueQuestion.Records(other.Record, files))
                    ? "a later run continued it"
                    : recordings.HasTranscript(files) && !videoNotMade
                        ? lesson ? "it has a transcript and its lesson video" : "it has a transcript"
                        : lesson && run.TranscriptionInterrupted && !record.Request.Transcribe && !videoNotMade
                            ? "its lesson video is made"
                            : recordings.FindSegments(files).Count == 0 && recordings.FindSegments(files, Track.Microphone).Count == 0
                                ? "its audio is gone"
                                : null;
            if (dealtWith is not null) return DealtWith(record, dealtWith);

            // A lesson's T only while it was to be transcribed and has none; its V while its video is still to be made.
            var canTranscribe = !lesson || (record.Request.Transcribe && !recordings.HasTranscript(files));
            if (run.TranscriptionInterrupted)
            {
                return new RunReport(record, files,
                [
                    LessonRuns.OnlyItsVideoLeft(record, recordings)
                        ? $"The lesson video of {RecordStarter.Named(record)} ({record.BaseName}) was not finished; its sound and its camera pieces are kept."
                        : $"The transcript of {RecordStarter.Named(record)} ({record.BaseName}) was not finished; its audio is complete. If the upload had finished, ElevenLabs may bill it again."
                ], CanContinue: false, canTranscribe, videoNotMade);
            }

            var endedAt = EndedAt(record, files);
            var recording = record.Request.Video is null ? $"The recording \"{record.Request.Name}\"" : $"The recording of {RecordStarter.Named(record)}";
            return new RunReport(record, files,
            [
                $"{recording} ({record.BaseName}) ended unexpectedly at about {When(endedAt)}, {Durations.Format(LengthOf(record, files))} in. Its audio is kept.",
                Cause(record, files, endedAt)
            ], CanContinue(record, files, runs), canTranscribe, videoNotMade);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Deliberately not rethrown: the report must never keep ClassScribe from its menu or a command from running;
            // a recording that cannot be looked at now is reported at the next start, and this says why.
            logger.LogWarning(ex, "Could not look at the recording of run {RunId} ({Recording}); it is reported at the next start", record.RunId, record.BaseName);
            return null;
        }
    }

    /// <summary>Marks <paramref name="run"/> seen without a word, as dealt with for the reason given, and reports nothing.</summary>
    private RunReport? DealtWith(RunRecord run, string reason)
    {
        logger.LogDebug("Run {RunId} ({Recording}) is not reported: {Reason}", run.RunId, run.BaseName, reason);
        journal.MarkSeen(run.RunId);
        return null;
    }

    /// <summary>
    /// What ended the recording, when it is known, in place of where to look: its window closed, an error nobody caught,
    /// which the recording's log names, Windows started again after its last heartbeat, or Windows ended the session;
    /// otherwise, Bitdefender's Notifications.
    /// </summary>
    private string Cause(RunRecord run, RecordingFiles files, DateTimeOffset endedAt)
    {
        if (run.EndedBy == RunEndCause.WindowClosed) return $"It ended when its window was closed at about {When(endedAt)}.";
        if (run.EndedBy == RunEndCause.Error) return $"It stopped because of an error at about {When(endedAt)}; {files.Log} says which.";
        if (liveness.LastBootUtc() > run.HeartbeatUtc) return $"Windows restarted at about {When(endedAt)}.";
        if (run.EndedBy == RunEndCause.Windows) return $"Windows shut down or signed you out at about {When(endedAt)}.";

        return LookAtBitdefender;
    }

    /// <summary>
    /// True when C may continue the recording: it is a class's, never a YouTube video's (whose run is never continued), no
    /// other recording runs, and the recording is the one a new run of its name may continue (see
    /// <see cref="ContinueQuestion.FindContinuable"/>), within Recording:ContinueWithinMinutes as it is now.
    /// </summary>
    private bool CanContinue(RunRecord run, RecordingFiles files, IReadOnlyList<RunView> runs)
    {
        if (run.Request.Video is not null || runs.Any(other => other.IsRecordingNow)) return false;

        var within = TimeSpan.FromMinutes(Math.Max(0, recording.CurrentValue.ContinueWithinMinutes));
        return ContinueQuestion.FindContinuable(folders, recordings, journal, run.Request.Name, run.Request.Source.Mode, within, run.Request.OutputRoot, logger,
                   ifUnreadable: "it is not offered to be continued this time", camera: run.Request.Source.Camera is not null) is { } candidate
               && ContinueQuestion.Records(run, candidate.Files);
    }

    /// <summary>
    /// When the recording ended, at about: the later of the run's last heartbeat and the last audio written to any segment
    /// of either track.
    /// </summary>
    private DateTimeOffset EndedAt(RunRecord run, RecordingFiles files)
    {
        var lastAudio = recordings.FindSegments(files).Concat(recordings.FindSegments(files, Track.Microphone))
            .Select(segment => new DateTimeOffset(File.GetLastWriteTimeUtc(segment), TimeSpan.Zero))
            .DefaultIfEmpty(DateTimeOffset.MinValue)
            .Max();
        return lastAudio > run.HeartbeatUtc ? lastAudio : run.HeartbeatUtc;
    }

    /// <summary>How far in the recording ended: its length on disk, exact even after a crash; the last heartbeat's position when a segment cannot be read now.</summary>
    private TimeSpan LengthOf(RunRecord run, RecordingFiles files)
    {
        try
        {
            return folders.LengthOnDisk(files);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Deliberately not rethrown: how far in is a detail of the report, and the last heartbeat says it to within 10
            // seconds when another program holds a segment.
            logger.LogDebug(ex, "Could not measure {Recording}; reporting the position of its last heartbeat", files.BaseName);
            return run.Position;
        }
    }

    /// <summary>
    /// Shows <paramref name="report"/> with what can be done, reads the key, and carries it out. Returns the choice carried
    /// out, or null, and whether the report ends here: after C or T, which ran their work, and when the settings stopped them.
    /// </summary>
    private async Task<(StartupChoice? Choice, bool Ended)> OfferAsync(RunReport report)
    {
        var run = report.Run;
        foreach (var line in report.Lines) ui.WriteLine(line);

        List<(string Key, string Text)> offers = [];
        if (report.CanContinue) offers.Add(("C", "continue it now as its next part"));
        if (report.CanTranscribe) offers.Add(("T", run.State == RunState.Transcribing ? "transcribe it" : "transcribe what was recorded"));
        if (report.CanMakeVideo) offers.Add(("V", MakeTheVideoNow));
        offers.Add(("F", "forget this"));
        offers.Add(("Enter", "go on to the menu"));
        WriteOffers(offers);

        while (true)
        {
            if (await ui.ReadKeyAsync(CancellationToken.None) is not { } key || key.Key is ConsoleKey.Enter or ConsoleKey.Escape || IsCtrlC(key)) return (null, false);

            switch (key.Key)
            {
                case ConsoleKey.C when report.CanContinue:
                    return await CarryOutAsync(run, StartupAction.Continue, () => commands.RecordAsync(ContinueRequest(run), StartOrigin.StartupReport));

                case ConsoleKey.T when report.CanTranscribe:
                    return await CarryOutAsync(run, StartupAction.Transcribe, () => commands.TranscribeAsync(new TranscribeRequest(report.Files.Segment(1))));

                case ConsoleKey.V when report.CanMakeVideo:
                    return await CarryOutAsync(run, StartupAction.MakeVideo, () => MakeTheVideoAsync(run));

                case ConsoleKey.F:
                    journal.MarkSeen(run.RunId);
                    return (new StartupChoice(run.RunId, StartupAction.Forget), false);
            }
        }
    }

    /// <summary>Writes the offers two to a line, as "  C  continue it now as its next part     T  transcribe what was recorded".</summary>
    private void WriteOffers(IReadOnlyList<(string Key, string Text)> offers)
    {
        for (var i = 0; i < offers.Count; i += 2)
        {
            var left = $"  {offers[i].Key}  {offers[i].Text}";
            ui.WriteLine(i + 1 < offers.Count ? $"{left.PadRight(SecondColumn)}{offers[i + 1].Key}  {offers[i + 1].Text}" : left);
        }
    }

    /// <summary>
    /// Carries out C or T for <paramref name="run"/>, or U for what waits to go up to Google Drive, with no run: once the
    /// settings, read again, pass the check, as for a menu choice, the run is marked seen and <paramref name="work"/> runs. A
    /// failure is reported and the menu comes next, as after a menu choice; settings that cannot be used run nothing and
    /// leave the run, or what waits, to report again.
    /// </summary>
    private async Task<(StartupChoice? Choice, bool Ended)> CarryOutAsync(RunRecord? run, StartupAction action, Func<Task<int>> work)
    {
        // Continuing a class is a recording, which a refused settings file never stops (amendment 4); T and U wait for a fixed file.
        if (!gate.TryOpen(action == StartupAction.Continue ? GateUse.Recording : GateUse.MenuChoice)) return (null, true);

        if (run is not null) journal.MarkSeen(run.RunId);
        try
        {
            await work();
        }
        catch (OperationCanceledException)
        {
            // Deliberately not rethrown: Ctrl+C during the upload is a normal way back to the menu, as from a menu choice.
            logger.LogWarning("Cancelled");
        }
        catch (Exception ex)
        {
            // Deliberately not rethrown: as after a menu choice, ClassScribe outlives a continue or a transcription that
            // failed, and this log line is the report; the menu comes next.
            logger.LogError(ex, "Stopped because of an error");
        }

        return (new StartupChoice(run?.RunId, action), true);
    }

    /// <summary>Prints <paramref name="report"/> with the commands that deal with it, for the command line, which never asks.</summary>
    private void PrintWithCommands(RunReport report)
    {
        foreach (var line in report.Lines) ui.WriteLine(line);

        if (report.CanContinue) ui.WriteLine($"To continue it as its next part: {commandLine.Record(ContinueRequest(report.Run))}");
        if (report.CanTranscribe)
        {
            ui.WriteLine(report.Run.State == RunState.Transcribing
                ? $"To transcribe it: {commandLine.Transcribe(report.Files.Segment(1))}"
                : $"To transcribe what was recorded: {commandLine.Transcribe(report.Files.Segment(1))}");
        }

        if (report.CanMakeVideo) ui.WriteLine($"To make its lesson video, free: {LessonRuns.FinishVideoCommand(report.Run, recordings, commandLine)}");
        ui.WriteLine(ForgetInTheMenu);
    }

    /// <summary>The request that continues <paramref name="run"/>'s recording as its next part: the run's own, as a new run, without --minutes, which counted from the run's own start.</summary>
    private RecordRequest ContinueRequest(RunRecord run) =>
        run.Request with { RunId = RunIds.New(time.GetUtcNow()), Continue = ContinueChoice.Continue, ContinueBaseName = run.BaseName, Minutes = null };

    /// <summary>A moment in local time, as "19:47", with the day when it is not today, as "19:47 on 15 October".</summary>
    private string When(DateTimeOffset utc) => LocalTime.Of(utc, time);

    /// <summary>True for Ctrl+C, which goes on as Enter does.</summary>
    private static bool IsCtrlC(ConsoleKeyInfo key) => key.Key == ConsoleKey.C && key.Modifiers.HasFlag(ConsoleModifiers.Control);

    /// <summary>A run to report: the run, its recording's files, the lines that describe it, and which of C, T and V are offered.</summary>
    /// <param name="Run">The run as its file says.</param>
    /// <param name="Files">The files of the recording it made.</param>
    /// <param name="Lines">What happened, as shown above the offers or the commands.</param>
    /// <param name="CanContinue">True when C is offered.</param>
    /// <param name="CanTranscribe">True when T is offered: always for a class, and for a lesson only when it was to be transcribed and has no transcript.</param>
    /// <param name="CanMakeVideo">True when V is offered: a lesson whose video is still to be made, by its files.</param>
    private sealed record RunReport(RunRecord Run, RecordingFiles Files, IReadOnlyList<string> Lines, bool CanContinue, bool CanTranscribe = true, bool CanMakeVideo = false);
}
