using System.Globalization;
using ClassScribe.Audio;
using ClassScribe.Audio.Microphones;
using ClassScribe.Camera;
using ClassScribe.Desktop;
using ClassScribe.Drive;
using ClassScribe.Recorder;
using ClassScribe.Runs;
using ClassScribe.Settings;
using ClassScribe.Storage;
using ClassScribe.Transcription;
using ClassScribe.YouTube;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClassScribe.Commands;

/// <summary>
/// One run: records a class until it is stopped and then, when asked to, sends the recording for a transcript, in a
/// separate recorder or in this window alike. While it records, the recording loop acts on the keys and checks on the
/// recording once a second (see <see cref="RecordingLoop"/>); what every run holds meanwhile, from the lock to the run file
/// and the laptop kept awake, is the bookkeeping's (see <see cref="RunBookkeeping"/>). The settings are those of the run's
/// own scope, taken when it starts, so an edit made during the class counts from the next run. Each run ends with an
/// outcome, which the run file keeps and the window or the recorder shows (see <see cref="IResultShower"/>). The recording
/// goes into Google Drive's queue as the run starts, and its outcome is noted at the end; the upload after the run is left
/// to whoever ran it, once the end is shown (see <see cref="IDriveAutoUpload"/>).
/// </summary>
/// <remarks>
/// A YouTube video's run (design 4.5), whose request carries the video, records the window its batch opened, in the batch's
/// date folder once the batch has one, with the video's source file beside the recording, written at the start and again at
/// the stop with how it ended; it fails closed when the window is gone before it records; it gives way to a class that
/// starts, through its yield (see <see cref="IVideoYield"/>); and once its capture has stopped it asks the window to close.
/// </remarks>
public sealed class RecordCommand(
    IRecordingFactory recordings,
    RecordingLoop loop,
    TranscriptionWorkflow workflow,
    GlossaryChoice glossaries,
    TranscriptionProgress progress,
    UsageReport usage,
    IUserSignals signals,
    IClassFolders folders,
    IClassMarks marks,
    IRecordingFolder recordingFolder,
    IFileProbe probe,
    IVideoSources sources,
    CommandLine commandLine,
    RunBookkeeping bookkeeping,
    IResultShower results,
    IRecorderStatusSink status,
    SourceChoice choice,
    IDriveAutoUpload drive,
    IOptionsSnapshot<RecordingOptions> recording,
    IOptionsSnapshot<ElevenLabsOptions> elevenLabs,
    TimeProvider time,
    ILogger<RecordCommand> logger,
    VideoPlayback? video = null,
    IVideoYield? yield = null,
    LessonVideoMaker? lessonVideos = null,
    LessonVideoDrive? lessonDrive = null) : IRecordRun
{
    /// <summary>True once a class asked this video's run to give way (see <see cref="IVideoYield"/>).</summary>
    private volatile bool _yielded;

    /// <summary>A lesson's video being made beside its transcript, once its sound has stopped; null for a run without the camera, or before.</summary>
    private Task<LessonVideoResult>? _lessonVideo;

    /// <summary>How a video's recording ended, when the run itself knows better than its cause, as when its window was gone before it began.</summary>
    private string? _videoEnded;

    /// <summary>
    /// The glossary the run's start decided (see <see cref="GlossaryChoice.AtStart"/>), handed to its transcript for when the
    /// recording's record gives none then; null before the start, or when the start could not decide one.
    /// </summary>
    private GlossaryPick? _glossary;

    /// <summary>
    /// Runs the class under <paramref name="name"/> as a new recording, transcribed when <paramref name="transcribe"/> is
    /// true: from what is chosen now (menu choice 6, or the settings) into the recordings folder of the settings, opening
    /// the results as the settings say. It stays for the callers that pass a name alone, until they pass a whole request to
    /// <see cref="RunAsync(RecordRequest)"/>. Returns the exit code.
    /// </summary>
    public Task<int> RunAsync(string name, bool transcribe) =>
        RunAsync(new RecordRequest(RunIds.New(time.GetUtcNow()), name, transcribe, choice.Current, folders.Root, ContinueChoice.New,
            ContinueBaseName: null, Minutes: null, Open: true));

    /// <summary>
    /// Runs the class as <paramref name="request"/> says:
    /// <list type="number">
    /// <item>takes the lock, or says which recording runs and returns at once;</item>
    /// <item>makes the recording's files under the request's recordings folder, or opens the recording to continue, from
    /// its length on disk;</item>
    /// <item>begins the bookkeeping: the run file, the recording's log, Google Drive's queue, the laptop kept awake, and
    /// the handlers for an end without the usual stop;</item>
    /// <item>starts the recording at that length, with a note where a continued recording resumes;</item>
    /// <item>says where it records, what, the keys, and when it stops by itself with --minutes; then which glossary its
    /// transcripts take, decided now and written beside it, or kept from its start for a continued recording (see
    /// <see cref="GlossaryChoice.AtStart"/>);</item>
    /// <item>runs the loop until the stop;</item>
    /// <item>stops the recording, lets go of the lock, so that the next class can start while this one uploads, and moves
    /// the run file on;</item>
    /// <item>with nothing captured, or for record-only nothing audible, says so;</item>
    /// <item>for record-only, says where the recording is and how to transcribe it;</item>
    /// <item>otherwise transcribes the whole recording from disk, still keeping the laptop awake.</item>
    /// </list>
    /// Each end writes its outcome into the run file, shows it, and publishes the run's last state. Returns the exit code:
    /// 0 when the recording (record-only) or its transcript was saved; 1 when nothing was captured, when record-only heard
    /// nothing audible, when there is no key, or when the start was refused in this window, which in a recorder is 3; 1 too
    /// when Windows ended the session, which leaves the run for the next start of ClassScribe to report. A recording that
    /// fails, a transcript that fails (digital silence, which the upload refuses, included), and a cancel (Ctrl+C) are
    /// thrown on after their outcome is written, for the caller's exit code (130 for a cancel).
    /// </summary>
    public async Task<int> RunAsync(RecordRequest request)
    {
        // A YouTube video's run gives way to a class that starts, heard from the very start of the run (design 2.5).
        using var yielding = ListenForTheYield(request);

        // 1. One recording at a time: the lock comes before any file is made, and goes the moment capture stops.
        if (bookkeeping.TryTakeLock() is not { } held) return Refuse(request);

        IDisposable? awake = null;
        IRecording? audio = null;
        var windows = new SessionEnd();
        var sofar = new WhatWasRecorded();
        try
        {
            RecordingFiles files;
            TimeSpan offset;
            RecordingSummary summary;
            GateSummary? microphone = null;
            RecorderState stopping;
            try
            {
                // 2. The recording's files: a new recording, or the one this run continues as its next part; a video's in
                // its batch's date folder once the batch has one.
                WarnAboutTheRecordingsFolder(request);
                (files, offset, var notRecorded) = OpenFiles(request);
                (sofar.Files, sofar.Offset) = (files, offset);

                // 3. The run file, the recording's log, Google Drive's queue (once Drive is set up), the laptop kept awake, and the
                // handlers for an end without the usual stop.
                var run = bookkeeping.Begin(request, files);
                if (_yielded) run?.SetEndedBy(RunEndCause.ClassStarted);
                drive.Queue(files);
                awake = bookkeeping.KeepAwake();
                if (request.Transcribe && !HasKey())
                    logger.LogWarning("No ElevenLabs API key found in {Variable}. The class will still be recorded, and you can transcribe it later", ElevenLabsOptions.ApiKeyVariable);

                // 3b. A YouTube video's source file, and its window, which must still be open: a video's run records nothing else.
                if (request.Video is { } videoRequest) BeginTheVideo(videoRequest, files);

                // 4. The recording, from where the recording on disk ends.
                var started = audio = recordings.Create(request.Source);
                using (bookkeeping.OnEnding(started.Flush, () => windows.EndNow(started, signals), () => started.Position))
                {
                    try
                    {
                        started.Start(files, offset);
                        sofar.Started = true;
                        if (notRecorded is { } gap) NoteTheResume(files, offset, gap);

                        // 5. The start lines, the glossary's among them, with the recording's glossary record written now, before
                        // any transcript, then 6. the loop, until the stop.
                        LogTheStart(request, files, offset);
                        _glossary = StartTheGlossary(files, request);
                        stopping = await loop.RunAsync(started, files, request, run, offset);
                    }
                    finally
                    {
                        // 7. The stop: the capture is released and the files finished. The microphone's numbers are taken
                        // now, at the stop, for the summary.
                        if (!windows.Ended) run?.SetState(RunState.Stopping);
                        summary = started.Stop();
                        microphone = (started as IMicrophoneRecording)?.Gate?.Summary();
                        sofar.Summary = summary;
                    }
                }
            }
            catch (Exception ex) when (!windows.Ended)
            {
                RecordingFailed(request, sofar, ex);
                throw;
            }

            // The next class may start while this one uploads.
            held.Dispose();

            // 7b. A YouTube video's source file says how it ended, and its window is asked to close.
            EndTheVideo(request, files);
            if (windows.Ended) return EndedByWindows(files);

            // A lesson with the camera is complete once its sound has stopped: its run file says Transcribing from now, record-only
            // included, while its video is made (and its transcript), so that Drive and transcribe wait, the next class may
            // start, and a crash meanwhile is not taken for a recording that ended unexpectedly (teach-video 6.9).
            if (request.Source.Camera is not null) bookkeeping.Run?.SetState(RunState.Transcribing);

            return await FinishAsync(request, files, offset, summary, stopping, audio.GetSnapshot().Main.Source,
                microphone, (audio as IMicrophoneRecording)?.Follows);
        }
        finally
        {
            audio?.Dispose();
            awake?.Dispose();
            held.Dispose();
        }
    }

    /// <summary>
    /// Steps 8 to 10, once the recording has stopped: nothing captured ends the run; record-only with nothing audible ends
    /// it as well, and otherwise says where the recording is; with a transcript to make and a key, the whole recording is
    /// transcribed from disk. What is sent follows the upload's own rule alone, as transcribe does: only exact zeros count as
    /// silence there (see <c>AudioPreparer</c>), so a class that is merely quiet still goes up, and digital silence is
    /// refused before anything is sent (<see cref="NoAudioException"/>). A continued recording whose new part heard nothing
    /// still has its earlier parts, so it goes on to them. With the class and your microphone, the main track is the
    /// class's: the microphone's numbers (<paramref name="microphone"/>, from the gate that follows <paramref name="follows"/>)
    /// are summed up after the length, a microphone track that was not written is said so, and the class's audio missing
    /// ends the run with the microphone's kept.
    /// </summary>
    private async Task<int> FinishAsync(RecordRequest request, RecordingFiles files, TimeSpan offset, RecordingSummary summary, RecorderState stopping, string source,
        GateSummary? microphone, FollowRule? follows)
    {
        var continued = offset > TimeSpan.Zero;
        var withMicrophone = request.Source.Mode == RecordingMode.ClassAndMicrophone;
        if (summary.MainSegments.Count == 0 && !continued)
        {
            // Your microphone's files alone are a recording the transcribe command takes by one of them (a folder is read by its .wav files).
            return NothingRecorded(request, files, stopping, withMicrophone && summary.MicrophoneSegments.Count > 0
                ? $"No audio was captured from the class. Was anything playing? Your microphone's audio is kept in {files.Folder}. To transcribe it on its own, run: {commandLine.Transcribe(files.Segment(1, Track.Microphone))}"
                : "No audio was captured. Was anything playing?");
        }

        if (continued)
            logger.LogInformation("Recorded {Part} in this part, {Total} in all", Durations.Format(summary.Length), Durations.Format(offset + summary.Length));
        else
            logger.LogInformation("Recorded {Duration} of audio", Durations.Format(summary.Length));

        if (microphone is { } numbers) logger.LogInformation("{Summary}", MicrophoneSummary(numbers, follows));
        if (withMicrophone && summary.MicrophoneSegments.Count == 0)
        {
            logger.LogWarning("Your microphone was not recorded in this run: no microphone file was written. The class was recorded and {Fate}",
                request.Transcribe && HasKey() ? "goes on to the transcript" : "is kept");
        }

        // A lesson's video is made beside the transcript, in a camera process of its own; the run's outcome waits for it.
        StartTheLessonVideo(request, files);

        var command = commandLine.Transcribe(files.Segment(1));
        if (!request.Transcribe)
        {
            // 8. Record only, with nothing loud enough to hear, as the warnings during the class count it: the file is kept,
            // and the words say what to check, since there is nothing in it to transcribe.
            if (!summary.HeardAnything)
            {
                if (!continued)
                {
                    await WaitForTheLessonVideoAsync();
                    return NothingRecorded(request, files, stopping, request.Video is { } nothing
                        ? $"Nothing played in ClassScribe's video window, so nothing of \"{TitleOf(nothing)}\" was kept to transcribe"
                        : $"Nothing audible was captured from \"{source}\". {WhatToCheck}");
                }

                logger.LogWarning("Nothing audible was captured in this part; the earlier parts of {Recording} are kept", files.BaseName);
            }

            // 9. Record only.
            await WaitForTheLessonVideoAsync();
            var kept = request.Video is { } recorded
                ? $"The recording of \"{TitleOf(recorded)}\" is in {files.Folder}."
                : $"The recording is in {files.Folder}. To transcribe it later, choose 3 in the menu, or run: {command}";
            logger.LogInformation("{Message}", kept);
            End(request, stopping, RecorderPhase.Done, new RunOutcome(OutcomeKind.Recorded, 0, kept, TranscriptPath: null, files.Folder, command));
            return 0;
        }

        if (!HasKey())
        {
            await WaitForTheLessonVideoAsync();
            var noKey =
                $"Set {ElevenLabsOptions.ApiKeyVariable} and open a new terminal (in VS Code, quit every VS Code window and start it again; a new terminal inside it keeps the old settings). Then start ClassScribe and choose 3 in the menu, or run: {command}";
            logger.LogWarning("{Message}", noKey);
            End(request, stopping, RecorderPhase.Failed, new RunOutcome(OutcomeKind.TranscriptFailed, 1, noKey, TranscriptPath: null, files.Folder, command));
            return 1;
        }

        // 10. The transcript, from every part of the recording on disk.
        return await TranscribeAsync(request, files, stopping, source, command);
    }

    /// <summary>
    /// Starts making a lesson's video once its sound has stopped (teach-video 6.9), beside the transcript: for a run with the
    /// camera whose camera wrote a piece, through a camera process of its own (see <see cref="LessonVideoMaker"/>), its
    /// pieces kept for Google Drive only when the video goes there (see <see cref="LessonVideoDrive.KeepsPieces"/>). A first
    /// Ctrl+C after the stop cancels it with the transcript. A lesson whose camera wrote no piece says so; the menu and
    /// finish-video have nothing to make then either.
    /// </summary>
    private void StartTheLessonVideo(RecordRequest request, RecordingFiles files)
    {
        if (request.Source.Camera is null || lessonVideos is null) return;

        if (recordingFolder.FindCameraPieces(files).Count == 0)
        {
            logger.LogWarning("The camera wrote no picture in this lesson, so it has no video to make; the sound is kept");
            return;
        }

        _lessonVideo = lessonVideos.MakeAsync(files, lessonDrive?.KeepsPieces ?? false, signals.Cancellation);
    }

    /// <summary>
    /// Waits for the lesson's video, when one is being made, before the run's outcome is written, so that the end can say what
    /// became of it (the maker says it); the laptop is kept awake meanwhile, as the run holds it until it returns.
    /// </summary>
    private async Task WaitForTheLessonVideoAsync()
    {
        if (_lessonVideo is not { } making) return;

        _lessonVideo = null;
        await making;
    }

    /// <summary>What to check when nothing audible reached a recording.</summary>
    private const string WhatToCheck =
        "Check that the class plays through that device or app (menu choice 6 picks another) and that Windows is not muted";

    /// <summary>
    /// The end-of-class summary of your microphone, from the gate's numbers: with an app followed, how long your voice was
    /// heard and in how many stretches, how long the microphone was muted by the app, off with V, and muted in Windows;
    /// then how often the app let go of the microphone (what an app whose mute Windows can see does on mute, so it is left
    /// out when the settings say Visible, and told against the settings when they say Invisible), and how often the app's
    /// session changed for under a second. With no app followed, how long the microphone was on and off.
    /// </summary>
    private string MicrophoneSummary(GateSummary numbers, FollowRule? follows)
    {
        if (follows is null or FollowRule.None)
            return $"Microphone: on for {Durations.Format(numbers.Heard)}, off (V) for {Durations.Format(numbers.Off)}";

        var app = MicrophoneWords.App(follows);
        var summary = $"Microphone: heard for {Durations.Format(numbers.Heard)} in {RecordingMonitor.Count(numbers.Stretches, "stretch", "stretches")} while {app} listened, " +
            $"muted for {Durations.Format(numbers.Muted)}, off (V) for {Durations.Format(numbers.Off)}, muted in Windows for {Durations.Format(numbers.MutedInWindows)}";
        var appMute = recording.Value.ClassAppMute;
        if (numbers.Cycles > 0 && appMute != ClassAppMute.Visible)
        {
            summary += $"; {app} let go of the microphone {RecordingMonitor.Count(numbers.Cycles, "time")}";
            if (appMute == ClassAppMute.Invisible) summary += " although Recording:ClassAppMute says Invisible";
        }

        if (numbers.Flickers > 0) summary += $"; {app}'s microphone session changed {RecordingMonitor.Count(numbers.Flickers, "time")} for under a second";
        return summary;
    }

    /// <summary>
    /// Transcribes the whole recording from disk, every part of it, while the laptop is still kept awake, showing how far it
    /// gets (see <see cref="Follow"/>). Every end writes its outcome, with the command that tries again where trying again
    /// can help, before a failure or a cancel goes on to the caller. Once the transcript is saved and the run has ended,
    /// with its results opened, the last line says what the transcript cost the account (see <see cref="UsageReport"/>),
    /// so that nothing anyone waits for is held up by it: in this window the run says it, since the end has opened the
    /// results already; in a recorder the end has still to go to the windows once the run returns, so the recorder's host
    /// says it after that (see <c>RecorderHost</c>).
    /// </summary>
    private async Task<int> TranscribeAsync(RecordRequest request, RecordingFiles files, RecorderState stopping, string source, string command)
    {
        bookkeeping.Run?.SetState(RunState.Transcribing);
        Publish(stopping with { Phase = RecorderPhase.Preparing });
        var follow = Follow(stopping);
        progress.Changed += follow;
        try
        {
            logger.LogInformation("Press Ctrl+C to cancel the upload. The recording is kept either way");
            var saved = await workflow.TranscribeRecordingAsync(files, keyterms: null, _glossary, signals.Cancellation);
            logger.LogInformation("Transcript saved to {Path}", saved.TranscriptPath);
            await WaitForTheLessonVideoAsync();
            End(request, stopping, RecorderPhase.Done,
                new RunOutcome(OutcomeKind.Transcribed, 0, $"Transcript saved to {saved.TranscriptPath}", saved.TranscriptPath, files.Folder, RetryCommand: null));

            // In a recorder the end goes to the windows, which open the results, only once this run has returned: the
            // host reports then, so that no read of the account holds the end up.
            if (bookkeeping.Home == RunHome.InWindow) await usage.ReportAsync(signals.Cancellation);
            return 0;
        }
        catch (OperationCanceledException)
        {
            await WaitForTheLessonVideoAsync();
            var cancelled = $"The transcription was cancelled. The recording is in {files.Folder}. To transcribe it later, choose 3 in the menu, or run: {command}";
            logger.LogWarning("{Message}", cancelled);
            End(request, stopping, RecorderPhase.Cancelled, new RunOutcome(OutcomeKind.Cancelled, 130, cancelled, TranscriptPath: null, files.Folder, command));
            throw;
        }
        catch (NoAudioException)
        {
            // No hint to try again: a new try would find the same silence.
            await WaitForTheLessonVideoAsync();
            var silent = $"Nothing audible was captured from \"{source}\", so nothing was sent to ElevenLabs, and trying again would find the same silence. {WhatToCheck}";
            logger.LogWarning("{Message}", silent);
            End(request, stopping, RecorderPhase.Failed, new RunOutcome(OutcomeKind.NothingRecorded, 1, silent, TranscriptPath: null, files.Folder, RetryCommand: null));
            throw;
        }
        catch (RecordingTooLongException)
        {
            // No hint to try again: a new try would be refused for the same length. The error itself says how to send the
            // recording in pieces.
            await WaitForTheLessonVideoAsync();
            var tooLong = $"The audio is safe in {files.Folder}. It is longer than ElevenLabs takes in one file, so trying again as it is would be refused the same way";
            logger.LogWarning("{Message}", tooLong);
            End(request, stopping, RecorderPhase.Failed, new RunOutcome(OutcomeKind.TranscriptFailed, 1, tooLong, TranscriptPath: null, files.Folder, RetryCommand: null));
            throw;
        }
        catch (Exception ex)
        {
            await WaitForTheLessonVideoAsync();
            var (failed, retry) = DescribeFailure(ex, files, command);
            End(request, stopping, RecorderPhase.Failed, new RunOutcome(OutcomeKind.TranscriptFailed, 1, failed, TranscriptPath: null, files.Folder, retry));
            throw;
        }
        finally
        {
            progress.Changed -= follow;
        }
    }

    /// <summary>
    /// What shows the transcription's progress after <paramref name="stopping"/>, the state the recording stopped in: each
    /// step and percent goes to the window's title and taskbar as the run's state (PREPARING, UPLOADING 40%, TRANSCRIBING),
    /// and into the run file's heartbeat, so that the menu and the list can say how far a transcription in the background
    /// is; the heartbeat at each step, at the first percent, and then at most every <see cref="RunTimings.HeartbeatInterval"/>
    /// on the steady clock, since the percent changes many times a second. Runs on the upload's thread, and never throws.
    /// </summary>
    private Action<TranscriptionStep, int?> Follow(RecorderState stopping)
    {
        var gate = new Lock();
        TranscriptionStep? lastStep = null;
        int? lastPercent = null;
        long lastHeartbeat = 0;
        return (step, percent) =>
        {
            var (phase, shown) = step switch
            {
                TranscriptionStep.Preparing => (RecorderPhase.Preparing, (int?)null),
                TranscriptionStep.Uploading => (RecorderPhase.Uploading, percent),
                _ => (RecorderPhase.Transcribing, 100)
            };
            Publish(stopping with { Phase = phase, UploadPercent = phase == RecorderPhase.Uploading ? percent : null });

            bool due;
            lock (gate)
            {
                var now = time.GetTimestamp();
                due = step != lastStep || (shown is not null) != (lastPercent is not null) || time.GetElapsedTime(lastHeartbeat, now) >= RunTimings.HeartbeatInterval;
                if (due) (lastStep, lastPercent, lastHeartbeat) = (step, shown, now);
            }

            if (due) bookkeeping.Run?.Heartbeat(stopping.Position, shown);
        };
    }

    /// <summary>
    /// Says why the transcript failed and how to try again, and returns the words and the command that tries again. The
    /// workflow marks a failure that came after ElevenLabs had charged for its answer, and says where the answer is kept,
    /// if anywhere, and its transcription ID when that is known: a new try is charged again, so the hint must not pass for
    /// a free retry. When the answer is kept, the free rebuild from it is the command to try first.
    /// </summary>
    private (string Message, string? RetryCommand) DescribeFailure(Exception failure, RecordingFiles files, string command)
    {
        switch (ChargedAnswer.Of(failure))
        {
            case { KeptIn: { } answer }:
            {
                // On the command line, transcribe refuses a recording whose paid answer is kept unless --again is given, and
                // offers the free rebuild instead (see TranscribeCommand). The commands given here are those two, so that
                // each runs as printed.
                var kept = $"The audio is safe in {files.Folder}. ElevenLabs' answer, which has been paid for, is kept in {answer}: look there for the transcript's text first, because transcribing again is charged again";
                var rebuild = commandLine.Rebuild(answer);
                logger.LogWarning("{Message}", kept);
                logger.LogInformation("To lay that answer out as a transcript for free, run: {Command}", rebuild);
                logger.LogInformation("To transcribe it again all the same, choose 3 in the menu, or run: {Command}", commandLine.Transcribe(files.Segment(1), again: true));
                return ($"{kept}. To lay that answer out as a transcript for free, run: {rebuild}", rebuild);
            }

            case { TranscriptionId: { } id }:
            {
                var lost = $"The audio is safe in {files.Folder}. ElevenLabs has charged for this answer, but it could not be saved anywhere. Its transcription ID at ElevenLabs is {id}, with which the transcript can be fetched from ElevenLabs. Transcribing again is charged again. To do so all the same, choose 3 in the menu, or run: {command}";
                logger.LogWarning("{Message}", lost);
                return (lost, command);
            }

            case not null:
            {
                var lost = $"The audio is safe in {files.Folder}. ElevenLabs has charged for this answer, but it could not be saved anywhere, and its transcription ID at ElevenLabs is unknown. Transcribing again is charged again. To do so all the same, choose 3 in the menu, or run: {command}";
                logger.LogWarning("{Message}", lost);
                return (lost, command);
            }

            default:
            {
                var again = $"The audio is safe in {files.Folder}. To try the transcript again, choose 3 in the menu, or run: {command}";
                logger.LogWarning("{Message}", again);
                return (again, command);
            }
        }
    }

    /// <summary>
    /// Step 5's glossary: which glossary the recording's transcripts take, said and written beside it, or kept from its start
    /// for a continued recording (see <see cref="GlossaryChoice.AtStart"/>); returns the run's glossary, which its transcript
    /// sends when the recording's record gives none then. The class is recording already, so nothing about the glossary may
    /// end it: whatever fails here is logged, the class records on, and its transcript decides the glossary as a later
    /// transcription would; null then.
    /// </summary>
    private GlossaryPick? StartTheGlossary(RecordingFiles files, RecordRequest request)
    {
        try
        {
            return glossaries.AtStart(files, request);
        }
        catch (Exception ex)
        {
            // Deliberately not rethrown: the capture has started, and the glossary only decides which terms the transcript is
            // sent with; an exception here would stop the capture and end the class.
            logger.LogError(ex, "Could not decide the recording's glossary at its start ({Reason}); the class records on, and its transcript decides the glossary when it is made", ex.Message);
            return null;
        }
    }

    /// <summary>A start the lock refused: says which recording runs, writes the refusal where the run has a run file already, and returns the exit code.</summary>
    private int Refuse(RecordRequest request)
    {
        var refused = bookkeeping.Refuse(request);
        logger.LogWarning("{Message}", refused.Message);
        results.Show(refused, request.Open);
        return bookkeeping.Home == RunHome.Separate ? RunBookkeeping.RefusedExitCode : refused.ExitCode;
    }

    /// <summary>Nothing to transcribe: says so in <paramref name="message"/>, ends the run with nothing recorded, and returns 1.</summary>
    private int NothingRecorded(RecordRequest request, RecordingFiles files, RecorderState stopping, string message)
    {
        logger.LogWarning("{Message}", message);
        End(request, stopping, RecorderPhase.Failed, new RunOutcome(OutcomeKind.NothingRecorded, 1, message, TranscriptPath: null, files.Folder, RetryCommand: null));
        return 1;
    }

    /// <summary>
    /// The recording failed, before or while it recorded, as a device in a format ClassScribe cannot read makes it fail at
    /// the start: says so, and where the audio recorded so far is kept, when there is any (this run's, or the earlier parts
    /// of the recording it continues), or that nothing was recorded; and ends the run as failed, with the error as its
    /// cause. The error itself goes on to the caller.
    /// </summary>
    private void RecordingFailed(RecordRequest request, WhatWasRecorded sofar, Exception failure)
    {
        var folder = sofar.HasAudio ? sofar.Files!.Folder : null;
        var kept = folder is null ? "Nothing was recorded." : $"The audio recorded so far is kept in {folder}";
        var message = $"The recording failed: {failure.Message.TrimEnd('.')}. {kept}";
        logger.LogError("{Message}", message);
        bookkeeping.Run?.SetEndedBy(RunEndCause.Error);
        if (sofar.Files is { } files) EndTheVideo(request, files);
        End(request, stopping: null, RecorderPhase.Failed, new RunOutcome(OutcomeKind.RecorderFailed, 1, message, TranscriptPath: null, folder, RetryCommand: null));
    }

    /// <summary>
    /// Listens, for a YouTube video's run, for a class that starts and asks it to give way (design 2.5): the run notes that a
    /// class stopped it, so that its end is not confirmed, and stops as Q pressed twice stops it. Nothing for a class's run,
    /// which never gives way. A yield that comes once the run is stopping changes nothing.
    /// </summary>
    private IDisposable? ListenForTheYield(RecordRequest request)
    {
        if (request.Video is null || yield is null) return null;

        return yield.Listen(request.RunId, () =>
        {
            if (signals.StopRequested.IsCompleted) return;

            _yielded = true;
            bookkeeping.Run?.SetEndedBy(RunEndCause.ClassStarted);
            signals.RequestStop();
        });
    }

    /// <summary>
    /// Step 3b for a YouTube video's run: its source file beside the recording, saying it is being recorded, so that a recorder
    /// lost mid-video leaves a recording that may be cut short; then its window, which must still be open, or the run fails
    /// closed with a <see cref="VideoWindowClosedException"/> before anything is recorded.
    /// </summary>
    private void BeginTheVideo(VideoRequest request, RecordingFiles files)
    {
        WriteSource(files, request, request.Title, VideoEnds.NotFinished, confirmed: false);
        if (video?.Begin(request) == true) return;

        _videoEnded = VideoEnds.ClosedBeforeItBegan;
        throw new VideoWindowClosedException();
    }

    /// <summary>
    /// Step 7b for a YouTube video's run, once its capture has stopped: its source file says how it ended, from what ended the
    /// run (see <see cref="VideoEnds"/>), with the title its window showed while it played; and the window is asked to close,
    /// never waited for, which its batch sees to. Nothing for a class's run.
    /// </summary>
    private void EndTheVideo(RecordRequest request, RecordingFiles files)
    {
        if (request.Video is not { } videoRequest) return;

        var endedBy = bookkeeping.Run?.Current.EndedBy ?? (_yielded ? RunEndCause.ClassStarted : null);
        var (ended, confirmed) = _videoEnded is { } own ? (own, false) : VideoEnds.Of(endedBy);
        WriteSource(files, videoRequest, TitleOf(videoRequest), ended, confirmed);
        logger.LogInformation("\"{Title}\": {Ended}{Confirmed}", TitleOf(videoRequest), ended,
            confirmed ? "" : "; it may be cut short, so the same command records it again");
        video?.Close();
    }

    /// <summary>
    /// Writes the source file of <paramref name="request"/>'s video beside <paramref name="files"/>: recorded as it played, its
    /// title, channel and Chrome's version, and how it ended. One that cannot be written is said, and the run goes on.
    /// </summary>
    private void WriteSource(RecordingFiles files, VideoRequest request, string title, string ended, bool confirmed)
    {
        var source = new VideoSource(VideoSource.YouTubeKind, request.Url, request.VideoId, title, request.Channel, request.ChannelHandle, ChannelId: null,
            UploadDate: null, Duration: null, Availability: null, Playlist: null, VideoSource.Recorded, request.Tool, files.StartedUtc ?? time.GetUtcNow(),
            Confirmed: null, AcceptedBy: null, ended, confirmed);
        try
        {
            sources.Write(files.Source, source);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Deliberately not rethrown: the source file names the video and how its recording ended; one that cannot be
            // written must not cost the recording, which the same command then records again, as it finds no video there.
            logger.LogWarning("Could not write the video's source file {Path} ({Reason}); the recording goes on", files.Source, ex.Message);
        }
    }

    /// <summary>
    /// Windows ended the session during the run: the files are finished, and the run file is left as it is, recording,
    /// with Windows as the cause, so that the next start of ClassScribe reports it, and offers to continue it while it can
    /// still be continued (see design 1.14). Returns 1.
    /// </summary>
    private int EndedByWindows(RecordingFiles files)
    {
        logger.LogWarning(
            "Windows is ending the session, so the recording stopped. Its audio is kept in {Folder}. The next start of ClassScribe reports it, and offers to continue it if that is still possible",
            files.Folder);
        return 1;
    }

    /// <summary>
    /// The run is over: its outcome goes into the run file and is noted for the upload to Google Drive after the run, the run's
    /// last state is published, and the outcome is shown.
    /// </summary>
    private void End(RecordRequest request, RecorderState? stopping, RecorderPhase phase, RunOutcome outcome)
    {
        bookkeeping.End(outcome);
        drive.Ended(outcome);
        Publish((stopping ?? new RecorderState(RecorderPhase.Stopping, request.Name, TimeSpan.Zero, Source: null, WaitingFor: null, Microphone: null,
            UploadPercent: null, StopsAtUtc: null, Attention.None, bookkeeping.Home == RunHome.InWindow)) with { Phase = phase, UploadPercent = null });
        results.Show(outcome, request.Open);
    }

    /// <summary>Publishes the run's state, for the window's title and taskbar, or for the windows that show the run.</summary>
    private void Publish(RecorderState state)
    {
        try
        {
            status.Publish(state);
        }
        catch (Exception ex)
        {
            // Deliberately not rethrown: the state only shows the run on a title and a taskbar button, and the log says the
            // same in words; a run whose transcript is saved must not fail over it, nor a failure be hidden behind it.
            logger.LogError(ex, "Could not show that the run is {Phase}", state.Phase);
        }
    }

    /// <summary>
    /// The files this run records into: those of the recording it continues, in its own folder in whichever layout it was
    /// made (see <see cref="IClassFolders.OpenToContinue"/>), when the request names one and it can still be opened, with
    /// the recording's length on disk, where this run starts, and how long ago its audio was last written;
    /// otherwise a new recording's. A recording that cannot be continued does not cost the class: the run says why and
    /// records a new one. A YouTube video's, once its batch has a date folder, is a new recording there (see
    /// <see cref="IClassFolders.CreateRecordingIn"/>).
    /// </summary>
    private (RecordingFiles Files, TimeSpan Offset, TimeSpan? NotRecorded) OpenFiles(RecordRequest request)
    {
        // A YouTube video of a batch goes into the batch's date folder, once its first video has made it; a video is never continued.
        if (request.Video is { Folder: { } batchFolder }) return (folders.CreateRecordingIn(batchFolder, request.Name), TimeSpan.Zero, null);

        if (request.ContinueBaseName is { } baseName)
        {
            try
            {
                var continued = folders.OpenToContinue(request.OutputRoot, request.Name, baseName);
                var length = folders.LengthOnDisk(continued);
                var notRecorded = time.GetUtcNow() - LastWritten(continued);
                return (continued, length, notRecorded < TimeSpan.Zero ? TimeSpan.Zero : notRecorded);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // Deliberately not rethrown: a recording that can no longer be continued, as one whose audio was moved, must
                // not cost the class; a new recording is made instead, and the warning says why.
                logger.LogWarning("Could not continue {BaseName} ({Reason}); recording \"{Name}\" as a new recording instead", baseName, ex.Message, request.Name);
            }
        }

        return (folders.CreateRecording(request.Name, request.OutputRoot), TimeSpan.Zero, null);
    }

    /// <summary>When audio was last written to the recording, on either track, in UTC.</summary>
    private DateTimeOffset LastWritten(RecordingFiles files) =>
        recordingFolder.FindSegments(files).Concat(recordingFolder.FindSegments(files, Track.Microphone))
            .Select(segment => new DateTimeOffset(probe.LastWriteUtc(segment), TimeSpan.Zero))
            .DefaultIfEmpty(DateTimeOffset.MinValue)
            .Max();

    /// <summary>
    /// Notes in the marks file where this part of a continued recording begins, at <paramref name="offset"/>, and how long
    /// was not recorded before it, so that the transcript shows it as a heading.
    /// </summary>
    private void NoteTheResume(RecordingFiles files, TimeSpan offset, TimeSpan notRecorded)
    {
        try
        {
            marks.AddResume(files, offset, time.GetUtcNow(), notRecorded);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Deliberately not rethrown: a note that cannot be saved must not cost the class; the transcript then lacks the
            // heading where this part begins, and the log says so.
            logger.LogError(ex, "Could not note in the marks file where this part of the recording begins, at {Position}", Durations.Format(offset));
        }
    }

    /// <summary>
    /// Warns about a problem with Recording:OutputFolder before the recording starts, when the run records there: a
    /// recording started from the command line never shows the menu that points it out. It stays a warning: a mistake in
    /// the settings must not cost the class. A run given a recordings folder of its own (--output) does not use the setting.
    /// </summary>
    private void WarnAboutTheRecordingsFolder(RecordRequest request)
    {
        if (!SameFolder(request.OutputRoot, folders.Root)) return;
        if (folders.DescribeRootProblem() is { } problem) logger.LogWarning("{Problem}", problem);
    }

    /// <summary>
    /// The start lines: where the recording goes, or which recording it continues; what it records; the keys (V among them
    /// with your microphone), and what closing the window does; the lid; and, with --minutes, when it stops by itself, by
    /// the local time of day. The lines about your microphone itself, from the privacy line to the warnings, come from the
    /// checks' first look, right after these (see <see cref="RecordingMonitor"/>). A YouTube video's run says which video it
    /// records, where, and that Gulzar plays it in its window, with its keys.
    /// </summary>
    private void LogTheStart(RecordRequest request, RecordingFiles files, TimeSpan offset)
    {
        if (request.Video is { } videoRequest)
        {
            logger.LogInformation("Recording the YouTube video \"{Title}\" ({Position} of {Count}) as it plays in ClassScribe's video window, into {Folder}",
                videoRequest.Title, videoRequest.Position, videoRequest.Count, files.Folder);
            logger.LogInformation(
                "Press play in the video window. The recording stops when the video ends or its window is closed; Q twice stops it sooner, P pauses or resumes, M marks a moment. {Closing}",
                bookkeeping.Home == RunHome.InWindow
                    ? "If this window is closed instead, the recording ends and the audio so far is kept"
                    : "Closing a window that shows this recording does not stop it");
            return;
        }

        // A saved class says so after its name: "Mantiq" (saved class), or the class's own name when it differs from the course.
        var saved = request.ClassName is not { } className ? ""
            : className.Equals(files.Name, StringComparison.OrdinalIgnoreCase) ? " (saved class)"
            : $" (the saved class \"{className}\")";
        if (offset > TimeSpan.Zero)
            logger.LogInformation("Continuing \"{Name}\"{Saved} as its next part ({BaseName}, {Length} recorded so far), in {Folder}", files.Name, saved, files.BaseName, Durations.Format(offset), files.Folder);
        else
            logger.LogInformation("Recording \"{Name}\"{Saved} to {Folder}", files.Name, saved, files.Folder);

        logger.LogInformation("Recording from {Source}", Describe(request.Source));

        // A lesson's video and Google Drive (N19 C): where it goes, and how to send lesson videos there while they stay here.
        if (request.Source.Camera is not null && lessonDrive?.StartLine() is { } whereItGoes) logger.LogInformation("{Line}", whereItGoes);
        logger.LogInformation("{Keys}{Microphone}. {Closing}",
            request.Transcribe
                ? "Press Q twice (or Ctrl+C twice) to stop and transcribe, P to pause or resume, M to mark a moment"
                : "Press Q twice (or Ctrl+C twice) to stop, P to pause or resume, M to mark a moment",
            request.Source.Mode == RecordingMode.Class ? "" : ", V to switch your microphone off or on",
            bookkeeping.Home == RunHome.InWindow
                ? "If the window is closed instead, the recording ends and the audio so far is kept"
                : "Closing a window that shows this recording does not stop it");
        logger.LogInformation("Keep the lid open and the laptop plugged in; closing the lid still puts it to sleep");
        if (request.Minutes is { } minutes)
        {
            logger.LogInformation("This recording stops by itself at {Time} (--minutes {Minutes}); Q twice stops it sooner",
                TimeZoneInfo.ConvertTime(time.GetUtcNow() + TimeSpan.FromMinutes(minutes), time.LocalTimeZone).ToString("HH:mm", CultureInfo.InvariantCulture),
                minutes.ToString("0.###", CultureInfo.InvariantCulture));
        }
    }

    /// <summary>
    /// What a run records, in words for the start line: the class's source, and your microphone with the class or alone in
    /// person, named by its name part when one was given; and a lesson's camera with its picture's size (teach-video 6.13).
    /// </summary>
    private static string Describe(SourcePlan source)
    {
        var microphone = source.Microphone?.NamePart is { Length: > 0 } namePart ? $"your microphone \"{namePart}\"" : "your microphone";
        return source.Mode switch
        {
            RecordingMode.Microphone when source.Camera is { } camera =>
                $"{microphone}, in person, and the camera {(camera.NamePart is { Length: > 0 } part ? $"\"{part}\"" : "of the lesson (a USB camera, else the built-in front camera)")} at {camera.Resolution ?? Camera.CameraSize.DefaultText}",
            RecordingMode.Microphone => microphone,
            RecordingMode.ClassAndMicrophone => $"{DescribeClass(source.Class)}, and {microphone}",
            _ => DescribeClass(source.Class)
        };
    }

    /// <summary>Where the class's sound comes from, in words for the start line.</summary>
    private static string DescribeClass(ClassSource? source) => source switch
    {
        ClassSource.App app => $"the app \"{app.ProcessName}\"",
        ClassSource.Device device => $"the output device \"{device.NamePart}\"",
        _ => "the device the class plays on"
    };

    /// <summary>A YouTube video's title: as its window showed it while it played, or as its request names it.</summary>
    private string TitleOf(VideoRequest request) => video?.PlayedTitle ?? request.Title;

    /// <summary>True when the run's settings hold an ElevenLabs API key, which only ELEVENLABS_API_KEY sets.</summary>
    private bool HasKey() => !string.IsNullOrWhiteSpace(elevenLabs.Value.ApiKey);

    /// <summary>True when two paths name the same folder, whatever their letter case or a separator at the end.</summary>
    private bool SameFolder(string first, string second) =>
        string.Equals(Path.TrimEndingDirectorySeparator(probe.FullPath(first)), Path.TrimEndingDirectorySeparator(probe.FullPath(second)), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// What the run has recorded so far, for the words of a failure: the files, once known; where it started in them; whether
    /// the recording started; and what it held when it stopped, once it did.
    /// </summary>
    private sealed class WhatWasRecorded
    {
        /// <summary>The recording's files, or null before they are known.</summary>
        public RecordingFiles? Files { get; set; }

        /// <summary>Where the run started in them: the length of the recording it continues, zero for a new one.</summary>
        public TimeSpan Offset { get; set; }

        /// <summary>True once the recording has started.</summary>
        public bool Started { get; set; }

        /// <summary>What the recording held when it stopped, or null before it did.</summary>
        public RecordingSummary? Summary { get; set; }

        /// <summary>
        /// True when the recording's files hold audio: the earlier parts of a recording the run continues, or what this run
        /// wrote. A recording that started but whose stop did not answer may have written some, so it counts as holding it.
        /// </summary>
        public bool HasAudio =>
            Files is not null
            && (Offset > TimeSpan.Zero
                || (Summary is { } summary ? summary.MainSegments.Count > 0 || summary.MicrophoneSegments.Count > 0 : Started));
    }

    /// <summary>Whether Windows ended the session during the run, and what ending it does to the recording. Safe to use from any thread.</summary>
    private sealed class SessionEnd
    {
        private int _ended;

        /// <summary>True once Windows ended the session during the run.</summary>
        public bool Ended => Volatile.Read(ref _ended) == 1;

        /// <summary>Windows ends the session now: notes it, finishes the recording's files, and asks the loop to stop.</summary>
        public void EndNow(IRecording audio, IUserSignals signals)
        {
            Volatile.Write(ref _ended, 1);
            audio.Stop();
            signals.RequestStop();
        }
    }
}
