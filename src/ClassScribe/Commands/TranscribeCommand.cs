using ClassScribe.Settings;
using ClassScribe.Storage;
using ClassScribe.Transcription;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClassScribe.Commands;

/// <summary>
/// Transcribes audio that already exists: a recording, named by its folder or by one of its segment
/// files, or any other audio or video file.
/// </summary>
public sealed class TranscribeCommand(
    TranscriptionWorkflow workflow,
    IUserSignals signals,
    IDesktop desktop,
    IOptions<RecordingOptions> recording,
    IOptions<ElevenLabsOptions> elevenLabs,
    ILogger<TranscribeCommand> logger)
{
    /// <summary>
    /// Transcribes what <paramref name="path"/> names: a folder means the recording started last in it, a
    /// segment file means its whole recording, and any other file is sent on its own. Returns the process exit code.
    /// </summary>
    public async Task<int> RunAsync(string path)
    {
        // Nothing is recording here, so the first Ctrl+C should already cancel the upload.
        signals.RequestStop();

        if (string.IsNullOrWhiteSpace(elevenLabs.Value.ApiKey))
        {
            logger.LogError("No ElevenLabs API key found. Set the {Variable} environment variable and open a new terminal (in VS Code, quit every VS Code window and start it again; a new terminal inside it keeps the old settings)", ElevenLabsOptions.ApiKeyVariable);
            return 1;
        }

        var fullPath = Path.GetFullPath(path);
        TranscriptFiles saved;
        if (Directory.Exists(fullPath))
        {
            if (ClassFolders.FindNewest(fullPath) is not { } newest)
            {
                logger.LogError("There is no ClassScribe recording in {Folder}", fullPath);
                return 1;
            }

            saved = await TranscribeRecordingAsync(newest);
        }
        else if (File.Exists(fullPath))
        {
            saved = RecordingFiles.TryParse(fullPath) is { } recorded && recorded.FindSegments().Count > 0
                ? await TranscribeRecordingAsync(recorded)
                : await workflow.TranscribeFileAsync(fullPath, signals.Cancellation);
        }
        else
        {
            logger.LogError("Nothing found at {Path}", fullPath);
            return 1;
        }

        logger.LogInformation("Transcript saved to {Path}", saved.TranscriptPath);
        desktop.ShowTranscript(recording.Value, saved);
        return 0;
    }

    /// <summary>Transcribes a recording from all its segments, with its marks.</summary>
    private Task<TranscriptFiles> TranscribeRecordingAsync(RecordingFiles files)
    {
        logger.LogInformation("Transcribing the recording {Recording}", files.BaseName);
        return workflow.TranscribeRecordingAsync(files, files.FindSegments(), signals.Cancellation);
    }
}
