using System.Globalization;
using System.Runtime.InteropServices;
using ClassScribe.Audio;
using ClassScribe.Settings;
using ClassScribe.Storage;
using ClassScribe.Transcription;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClassScribe.Commands;

/// <summary>
/// Records system audio until asked to stop and then, if asked to, sends the recording for a transcript.
/// While recording, Q stops, P pauses or resumes, and M marks the moment.
/// </summary>
public sealed class RecordCommand(
    RecordingSession session,
    TranscriptionWorkflow workflow,
    IUserSignals signals,
    IConsoleUi ui,
    ClassFolders folders,
    ClassMarks marks,
    IDesktop desktop,
    IOptions<RecordingOptions> recording,
    IOptions<ElevenLabsOptions> elevenLabs,
    TimeProvider time,
    ILogger<RecordCommand> logger)
{
    /// <summary>Runs the whole class: record, stop and, when <paramref name="transcribe"/> is true, transcribe. Returns the process exit code.</summary>
    public async Task<int> RunAsync(bool transcribe)
    {
        var folder = folders.CreateForClass();
        var hasKey = !string.IsNullOrWhiteSpace(elevenLabs.Value.ApiKey);
        if (transcribe && !hasKey)
            logger.LogWarning("No ElevenLabs API key found in {Variable}. The class will still be recorded, and you can transcribe it later", ElevenLabsOptions.ApiKeyVariable);

        session.Start(folder);

        // Closing the window (SIGHUP), shutting Windows down (SIGTERM) or a crash anywhere in the process
        // ends it within seconds, so the header is written right away. The audio captured up to that
        // moment stays usable.
        using var onWindowClosed = PosixSignalRegistration.Create(PosixSignal.SIGHUP, _ => SaveNow());
        using var onShutdown = PosixSignalRegistration.Create(PosixSignal.SIGTERM, _ => SaveNow());
        UnhandledExceptionEventHandler onCrash = (_, _) => SaveNow();
        AppDomain.CurrentDomain.UnhandledException += onCrash;

        RecordingResult result;
        try
        {
            logger.LogInformation("Recording to {Folder}", folder);
            logger.LogInformation(transcribe
                ? "Press Q (or Ctrl+C) to stop and transcribe, P to pause or resume, M to mark a moment. If the window is closed instead, the audio is kept"
                : "Press Q (or Ctrl+C) to stop, P to pause or resume, M to mark a moment. If the window is closed instead, the audio is kept");
            await WaitForStopAsync(folder);
        }
        finally
        {
            result = session.Stop();
            AppDomain.CurrentDomain.UnhandledException -= onCrash;
        }

        if (result.SegmentPaths.Count == 0)
        {
            logger.LogWarning("No audio was captured. Was anything playing?");
            return 1;
        }

        logger.LogInformation("Recorded {Duration} of audio", result.Duration.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture));
        if (!transcribe)
        {
            logger.LogInformation("The recording is in {Folder}. To transcribe it later, choose 3 in the menu, or run: ClassScribe transcribe \"{SameFolder}\"", folder, folder);
            if (recording.Value.OpenFolderWhenDone) desktop.OpenFolder(folder);
            return 0;
        }

        if (!hasKey)
        {
            logger.LogWarning("Set {Variable}, then run: ClassScribe transcribe \"{Folder}\"", ElevenLabsOptions.ApiKeyVariable, folder);
            return 1;
        }

        try
        {
            var files = await workflow.TranscribeSegmentsAsync(result.SegmentPaths, folder, signals.Cancellation);
            logger.LogInformation("Transcript saved to {Path}", files.TextPath);
            desktop.ShowTranscript(recording.Value, files);
            return 0;
        }
        catch (Exception)
        {
            logger.LogWarning("The audio is safe in {Folder}. To try the transcript again, run: ClassScribe transcribe \"{SameFolder}\"", folder, folder);
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
    /// nothing audible is arriving, unless the recording is paused.
    /// </summary>
    private async Task WaitForStopAsync(string folder)
    {
        var statusEvery = TimeSpan.FromSeconds(Math.Max(1, recording.Value.StatusIntervalSeconds));
        var silenceAfter = TimeSpan.FromSeconds(Math.Max(1, recording.Value.SilenceWarningSeconds));
        var lastStatus = time.GetUtcNow();
        var lastSilenceWarning = DateTimeOffset.MinValue;

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
                    if (key is { } pressed) HandleKey(pressed, folder);
                }

                if (signals.StopRequested.IsCompleted) break;

                var now = time.GetUtcNow();
                var status = session.GetStatus();
                if (status.WaitingFor is null && !status.Paused && status.SinceLastSound >= silenceAfter && now - lastSilenceWarning >= silenceAfter)
                {
                    lastSilenceWarning = now;
                    logger.LogWarning(
                        "No sound for {Seconds} seconds. Check that the class is playing through \"{Device}\" and that Windows is not muted",
                        (int)status.SinceLastSound.TotalSeconds, status.DeviceName);
                }

                if (now - lastStatus >= statusEvery)
                {
                    lastStatus = now;
                    var captured = status.Captured.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);
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

    /// <summary>Acts on a key pressed while recording: Q stops, P pauses or resumes, M marks the moment. Other keys do nothing.</summary>
    private void HandleKey(ConsoleKeyInfo key, string folder)
    {
        switch (key.Key)
        {
            case ConsoleKey.Q:
                signals.RequestStop();
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
                Mark(folder);
                break;
        }
    }

    /// <summary>Writes the current position in the recording to marks.txt, so it shows in the transcript.</summary>
    private void Mark(string folder)
    {
        var position = session.GetStatus().Captured;
        var shown = position.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);
        try
        {
            var number = marks.Add(folder, position);
            logger.LogInformation("Mark {Number} at {Position}", number, shown);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Deliberately not rethrown: a mark that cannot be saved must not end the recording.
            logger.LogError(ex, "Could not save the mark at {Position}", shown);
        }
    }
}
