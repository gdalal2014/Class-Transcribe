using System.Globalization;
using ClassScribe.Audio;
using ClassScribe.Settings;
using ClassScribe.Storage;
using ClassScribe.Transcription;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClassScribe.Commands;

/// <summary>
/// Records system audio until asked to stop and then, if asked to, sends the recording for a transcript.
/// While recording, Q pressed twice stops, P pauses or resumes, and M marks the moment.
/// </summary>
public sealed class RecordCommand(
    RecordingSession session,
    TranscriptionWorkflow workflow,
    IUserSignals signals,
    IConsoleUi ui,
    ClassFolders folders,
    ClassMarks marks,
    IDesktop desktop,
    IExitWarnings exitWarnings,
    IOptions<RecordingOptions> recording,
    IOptions<ElevenLabsOptions> elevenLabs,
    TimeProvider time,
    ILogger<RecordCommand> logger)
{
    /// <summary>How soon after a first Q a second one stops the recording. A single Q, typed into this window by mistake, does not.</summary>
    private static readonly TimeSpan SecondQWithin = TimeSpan.FromSeconds(5);

    /// <summary>When Q was last pressed without stopping the recording, on the steady clock (<see cref="TimeProvider.GetTimestamp"/>), or null.</summary>
    private long? _firstQ;

    /// <summary>Runs the whole class under <paramref name="name"/>: record, stop and, when <paramref name="transcribe"/> is true, transcribe. Returns the process exit code.</summary>
    public async Task<int> RunAsync(string name, bool transcribe)
    {
        // The menu points this out as well, but a recording started from the command line never shows the
        // menu. It stays a warning: a mistake in the settings must not cost the class.
        if (folders.DescribeRootProblem() is { } problem)
            logger.LogWarning("{Problem}", problem);

        var files = folders.CreateRecording(name);
        var hasKey = !string.IsNullOrWhiteSpace(elevenLabs.Value.ApiKey);
        if (transcribe && !hasKey)
            logger.LogWarning("No ElevenLabs API key found in {Variable}. The class will still be recorded, and you can transcribe it later", ElevenLabsOptions.ApiKeyVariable);

        RecordingResult result;

        // Closing the window or a crash anywhere in the process ends it within seconds, so the file on
        // disk is brought up to date at once, and the audio captured up to that moment stays usable. A
        // shutdown or a sign-out sends no warning that a console program can catch; see ProcessExitWarnings.
        using (exitWarnings.Register(SaveNow))
        {
            session.Start(files);
            try
            {
                logger.LogInformation("Recording \"{Name}\" to {Folder}", files.Name, files.Folder);
                logger.LogInformation(transcribe
                    ? "Press Q twice (or Ctrl+C) to stop and transcribe, P to pause or resume, M to mark a moment. If the window is closed instead, the audio is kept"
                    : "Press Q twice (or Ctrl+C) to stop, P to pause or resume, M to mark a moment. If the window is closed instead, the audio is kept");
                await WaitForStopAsync(files);
            }
            finally
            {
                result = session.Stop();
            }
        }

        if (result.SegmentPaths.Count == 0)
        {
            logger.LogWarning("No audio was captured. Was anything playing?");
            return 1;
        }

        logger.LogInformation("Recorded {Duration} of audio", Durations.Format(result.Duration));
        if (!transcribe)
        {
            logger.LogInformation("The recording is in {Folder}. To transcribe it later, choose 3 in the menu, or run: {Command}", files.Folder, CommandLine.Transcribe(files.Segment(1)));
            if (recording.Value.OpenFolderWhenDone) desktop.OpenFolder(files.Folder);
            return 0;
        }

        if (!hasKey)
        {
            logger.LogWarning("Set {Variable} and open a new terminal (in VS Code, quit every VS Code window and start it again; a new terminal inside it keeps the old settings). Then start ClassScribe and choose 3 in the menu, or run: {Command}", ElevenLabsOptions.ApiKeyVariable, CommandLine.Transcribe(files.Segment(1)));
            return 1;
        }

        try
        {
            logger.LogInformation("Press Ctrl+C to cancel the upload. The recording is kept either way");
            var saved = await workflow.TranscribeRecordingAsync(files, result.SegmentPaths, signals.Cancellation);
            logger.LogInformation("Transcript saved to {Path}", saved.TranscriptPath);
            desktop.ShowTranscript(recording.Value, saved);
            return 0;
        }
        catch (NoAudioException)
        {
            // No hint to try again: a new try would find the same silence.
            logger.LogWarning(
                "Nothing audible was captured from \"{Source}\", so nothing was sent to ElevenLabs, and trying again would find the same silence. Check that the class plays through that device or app (menu choice 6 picks another) and that Windows is not muted",
                session.GetStatus().DeviceName);
            throw;
        }
        catch (Exception ex)
        {
            // The workflow marks a failure that came after ElevenLabs had charged for its answer, and says where the
            // answer is kept, if anywhere, and its transcription ID when that is known: a new try is charged again, so
            // the hint must not pass for a free retry.
            var command = CommandLine.Transcribe(files.Segment(1));
            switch (ChargedAnswer.Of(ex))
            {
                case { KeptIn: { } answer }:
                    logger.LogWarning(
                        "The audio is safe in {Folder}. ElevenLabs' answer, which has been paid for, is kept in {Answer}: look there for the transcript's text first, because transcribing again is charged again. To transcribe it again all the same, choose 3 in the menu, or run: {Command}",
                        files.Folder, answer, command);
                    break;
                case { TranscriptionId: { } id }:
                    logger.LogWarning(
                        "The audio is safe in {Folder}. ElevenLabs has charged for this answer, but it could not be saved anywhere. Its transcription ID at ElevenLabs is {TranscriptionId}, with which the transcript can be fetched from ElevenLabs. Transcribing again is charged again. To do so all the same, choose 3 in the menu, or run: {Command}",
                        files.Folder, id, command);
                    break;
                case not null:
                    logger.LogWarning(
                        "The audio is safe in {Folder}. ElevenLabs has charged for this answer, but it could not be saved anywhere, and its transcription ID at ElevenLabs is unknown. Transcribing again is charged again. To do so all the same, choose 3 in the menu, or run: {Command}",
                        files.Folder, command);
                    break;
                default:
                    logger.LogWarning("The audio is safe in {Folder}. To try the transcript again, choose 3 in the menu, or run: {Command}", files.Folder, command);
                    break;
            }

            throw;
        }
    }

    /// <summary>Brings the file on disk up to date because the process is about to end.</summary>
    private void SaveNow()
    {
        try
        {
            session.Flush();
        }
        catch (Exception ex)
        {
            // Deliberately not rethrown: the process is already on its way out, and an exception from
            // a shutdown handler has nowhere to go.
            logger.LogError(ex, "Could not bring the recording on disk up to date before exiting");
        }
    }

    /// <summary>
    /// Waits for the stop signal. Meanwhile it acts on the recording keys, logs progress, and warns when
    /// nothing audible is arriving, unless the recording is paused or nothing can be recorded yet. The
    /// intervals are measured on the steady clock (<see cref="TimeProvider.GetTimestamp"/>), so Windows
    /// setting its time of day neither stops nor hurries the status line or the silence warning.
    /// </summary>
    private async Task WaitForStopAsync(RecordingFiles files)
    {
        var statusEvery = TimeSpan.FromSeconds(Math.Max(1, recording.Value.StatusIntervalSeconds));
        var silenceAfter = TimeSpan.FromSeconds(Math.Max(1, recording.Value.SilenceWarningSeconds));
        var lastStatus = time.GetTimestamp();
        long? lastSilenceWarning = null;

        using var stopReading = new CancellationTokenSource();
        Task<ConsoleKeyInfo?>? nextKey = null;
        try
        {
            while (!signals.StopRequested.IsCompleted)
            {
                if (ui.IsInteractive) nextKey ??= ui.ReadKeyAsync(stopReading.Token);
                var tick = Task.Delay(TimeSpan.FromSeconds(1), time);
                if (nextKey is null)
                    await Task.WhenAny(signals.StopRequested, tick);
                else
                    await Task.WhenAny(signals.StopRequested, tick, nextKey);

                if (nextKey is { IsCompleted: true })
                {
                    var key = await nextKey;
                    nextKey = null;
                    if (key is { } pressed) HandleKey(pressed, files);
                }

                if (signals.StopRequested.IsCompleted) break;

                var now = time.GetTimestamp();
                var status = session.GetStatus();
                if (status.WaitingFor is null && !status.Paused && status.SinceLastSound >= silenceAfter
                    && (lastSilenceWarning is not { } warned || time.GetElapsedTime(warned, now) >= silenceAfter))
                {
                    lastSilenceWarning = now;
                    logger.LogWarning(
                        "No sound for {Silence}. Check that the class is playing through \"{Device}\" and that Windows is not muted",
                        DescribeSilence(status.SinceLastSound), status.DeviceName);
                }

                if (time.GetElapsedTime(lastStatus, now) >= statusEvery)
                {
                    lastStatus = now;
                    var captured = Durations.Format(status.Captured);
                    if (status.WaitingFor is { } waitingFor)
                        logger.LogInformation("Recording: {Captured} captured; waiting for {What}", captured, waitingFor);
                    else
                        logger.LogInformation("Recording: {Captured} captured from \"{Device}\"{Paused}", captured, status.DeviceName, status.Paused ? " (paused)" : "");
                }
            }
        }
        finally
        {
            // A cancelled read answers null within moments, so this only waits for it to let go of the keyboard.
            stopReading.Cancel();
            if (nextKey is not null) await nextKey;
        }
    }

    /// <summary>
    /// How long a silence has lasted, in words that read at a glance: in seconds up to two minutes, then in
    /// whole minutes, and from an hour on in hours and minutes, such as "2 hours 30 minutes".
    /// </summary>
    private static string DescribeSilence(TimeSpan silence)
    {
        var seconds = (long)silence.TotalSeconds;
        if (seconds <= 120) return Count(seconds, "second");

        var minutes = seconds / 60;
        if (minutes < 60) return Count(minutes, "minute");

        var hours = Count(minutes / 60, "hour");
        return minutes % 60 == 0 ? hours : $"{hours} {Count(minutes % 60, "minute")}";
    }

    /// <summary>A number with its unit, singular for one and plural otherwise, such as "1 minute" or "30 minutes".</summary>
    private static string Count(long number, string unit) =>
        string.Create(CultureInfo.InvariantCulture, $"{number} {unit}{(number == 1 ? "" : "s")}");

    /// <summary>
    /// Acts on a key pressed while recording: Q stops when it follows another Q within five seconds, timed
    /// on the steady clock, P pauses or resumes, M marks the moment. Other keys do nothing.
    /// </summary>
    private void HandleKey(ConsoleKeyInfo key, RecordingFiles files)
    {
        switch (key.Key)
        {
            case ConsoleKey.Q when _firstQ is { } first && time.GetElapsedTime(first) <= SecondQWithin:
                signals.RequestStop();
                break;
            case ConsoleKey.Q:
                _firstQ = time.GetTimestamp();
                logger.LogInformation("Press Q again within {Seconds} seconds to stop", SecondQWithin.TotalSeconds);
                break;
            case ConsoleKey.P when session.GetStatus().Paused:
                session.Resume();
                logger.LogInformation("Resumed");
                break;
            case ConsoleKey.P:
                session.Pause();
                logger.LogInformation("Paused. Nothing is recorded until you press P again");
                break;
            case ConsoleKey.M:
                Mark(files);
                break;
        }
    }

    /// <summary>Writes the current position in the recording to its marks file, so it shows in the transcript.</summary>
    private void Mark(RecordingFiles files)
    {
        var position = session.GetStatus().Captured;
        var shown = Durations.Format(position);
        try
        {
            var number = marks.Add(files, position);
            logger.LogInformation("Mark {Number} at {Position}", number, shown);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Deliberately not rethrown: a mark that cannot be saved must not end the recording.
            logger.LogError(ex, "Could not save the mark at {Position}", shown);
        }
    }
}
