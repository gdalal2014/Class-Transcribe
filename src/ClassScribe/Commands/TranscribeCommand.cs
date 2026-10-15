using ClassScribe.Settings;
using ClassScribe.Storage;
using ClassScribe.Transcription;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClassScribe.Commands;

/// <summary>Transcribes audio that already exists: a class folder, or any audio or video file.</summary>
public sealed class TranscribeCommand(
    TranscriptionWorkflow workflow,
    IUserSignals signals,
    IDesktop desktop,
    IOptions<RecordingOptions> recording,
    IOptions<ElevenLabsOptions> elevenLabs,
    ILogger<TranscribeCommand> logger)
{
    /// <summary>Transcribes the file or folder at <paramref name="path"/>. Returns the process exit code.</summary>
    public async Task<int> RunAsync(string path)
    {
        // Nothing is recording here, so the first Ctrl+C should already cancel the upload.
        signals.RequestStop();

        if (string.IsNullOrWhiteSpace(elevenLabs.Value.ApiKey))
        {
            logger.LogError("No ElevenLabs API key found. Set the {Variable} environment variable and open a new terminal", ElevenLabsOptions.ApiKeyVariable);
            return 1;
        }

        var fullPath = Path.GetFullPath(path);
        TranscriptFiles files;
        if (Directory.Exists(fullPath))
        {
            var segments = ClassFolders.FindSegments(fullPath);
            if (segments.Count == 0)
            {
                logger.LogError("There are no recording-001.wav style files in {Folder}", fullPath);
                return 1;
            }

            files = await workflow.TranscribeSegmentsAsync(segments, fullPath, signals.Cancellation);
        }
        else if (File.Exists(fullPath))
        {
            files = await workflow.TranscribeFileAsync(fullPath, signals.Cancellation);
        }
        else
        {
            logger.LogError("Nothing found at {Path}", fullPath);
            return 1;
        }

        logger.LogInformation("Transcript saved to {Path}", files.TextPath);
        desktop.ShowTranscript(recording.Value, files);
        return 0;
    }
}
