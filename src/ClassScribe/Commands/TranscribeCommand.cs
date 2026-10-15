using System.Globalization;
using System.Text.RegularExpressions;
using ClassScribe.Settings;
using ClassScribe.Transcription;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClassScribe.Commands;

/// <summary>Transcribes audio that already exists: a class folder, or any audio or video file.</summary>
public sealed class TranscribeCommand(
    TranscriptionWorkflow workflow,
    IUserSignals signals,
    IOptions<ElevenLabsOptions> elevenLabs,
    ILogger<TranscribeCommand> logger)
{
    /// <summary>The recorder's segment files, and nothing else that happens to end in .wav.</summary>
    private static readonly Regex SegmentName = new(@"^recording-(?<number>[0-9]{1,9})\.wav$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

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
            var segments = FindSegments(fullPath);
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
        return 0;
    }

    /// <summary>Lists the segment files of a class folder in the order they were recorded.</summary>
    private static List<string> FindSegments(string folder) =>
        Directory.EnumerateFiles(folder, "recording-*.wav")
            .Select(file => (FullPath: file, Name: SegmentName.Match(Path.GetFileName(file))))
            .Where(file => file.Name.Success)
            .OrderBy(file => int.Parse(file.Name.Groups["number"].Value, CultureInfo.InvariantCulture))
            .Select(file => file.FullPath)
            .ToList();
}
