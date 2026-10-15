using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using ClassScribe.Audio;
using ClassScribe.Settings;
using ClassScribe.Transcription;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClassScribe.Commands;

/// <summary>Records system audio until asked to stop, then sends the recording for a transcript.</summary>
public sealed class RecordCommand(
    RecordingSession session,
    TranscriptionWorkflow workflow,
    IUserSignals signals,
    IOptions<RecordingOptions> recording,
    IOptions<ElevenLabsOptions> elevenLabs,
    TimeProvider time,
    ILogger<RecordCommand> logger)
{
    /// <summary>Runs the whole class: record, stop, transcribe. Returns the process exit code.</summary>
    public async Task<int> RunAsync()
    {
        var folder = CreateClassFolder();
        var hasKey = !string.IsNullOrWhiteSpace(elevenLabs.Value.ApiKey);
        if (!hasKey)
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
            logger.LogInformation("Press Q (or Ctrl+C) to stop and transcribe. If the window is closed instead, the audio is kept");
            signals.ListenForStopKey();
            await WaitForStopAsync();
        }
        finally
        {
            result = session.Stop();
            AppDomain.CurrentDomain.UnhandledException -= onCrash;
        }

        if (result.SegmentPaths.Count == 0)
        {
            logger.LogWarning("No audio was captured, so there is nothing to transcribe. Was anything playing?");
            return 1;
        }

        logger.LogInformation("Recorded {Duration} of audio", result.Duration.ToString(@"hh\:mm\:ss"));
        if (!hasKey)
        {
            logger.LogWarning("Set {Variable}, then run: ClassScribe transcribe \"{Folder}\"", ElevenLabsOptions.ApiKeyVariable, folder);
            return 1;
        }

        try
        {
            var files = await workflow.TranscribeSegmentsAsync(result.SegmentPaths, folder, signals.Cancellation);
            logger.LogInformation("Transcript saved to {Path}", files.TextPath);
            OpenFolder(folder);
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

    /// <summary>Waits for the stop signal, logging progress and warning when nothing audible is arriving.</summary>
    private async Task WaitForStopAsync()
    {
        var statusEvery = TimeSpan.FromSeconds(Math.Max(1, recording.Value.StatusIntervalSeconds));
        var silenceAfter = TimeSpan.FromSeconds(Math.Max(1, recording.Value.SilenceWarningSeconds));
        var lastStatus = time.GetUtcNow();
        var lastSilenceWarning = DateTimeOffset.MinValue;

        while (!signals.StopRequested.IsCompleted)
        {
            await Task.WhenAny(signals.StopRequested, Task.Delay(TimeSpan.FromSeconds(1), time));
            if (signals.StopRequested.IsCompleted) break;

            var now = time.GetUtcNow();
            var status = session.GetStatus();

            if (status.SinceLastSound >= silenceAfter && now - lastSilenceWarning >= silenceAfter)
            {
                lastSilenceWarning = now;
                logger.LogWarning(
                    "No sound for {Seconds} seconds. Check that the class is playing through \"{Device}\" and that Windows is not muted",
                    (int)status.SinceLastSound.TotalSeconds, status.DeviceName);
            }

            if (now - lastStatus >= statusEvery)
            {
                lastStatus = now;
                logger.LogInformation("Recording: {Captured} captured from \"{Device}\"", status.Captured.ToString(@"hh\:mm\:ss"), status.DeviceName);
            }
        }
    }

    /// <summary>Creates a folder for this class, named after the local date and time.</summary>
    private string CreateClassFolder()
    {
        var configured = recording.Value.OutputFolder;
        var root = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "ClassScribe")
            : Environment.ExpandEnvironmentVariables(configured);

        // Invariant, so the name is a Gregorian date that sorts by time whatever calendar the machine is set to.
        var name = time.GetLocalNow().ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture);
        var folder = Path.Combine(root, name);
        for (var suffix = 2; Directory.Exists(folder); suffix++)
            folder = Path.Combine(root, $"{name}_{suffix}");

        Directory.CreateDirectory(folder);
        return folder;
    }

    /// <summary>Shows the class folder in Explorer. Failing to do so is not worth failing the run for.</summary>
    private void OpenFolder(string folder)
    {
        if (!recording.Value.OpenFolderWhenDone || !OperatingSystem.IsWindows()) return;

        try
        {
            using var explorer = Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = false });
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            logger.LogWarning(ex, "Could not open {Folder} in Explorer", folder);
        }
    }
}
