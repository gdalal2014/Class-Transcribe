using System.Text;
using ClassScribe.Audio;
using ClassScribe.Settings;
using ClassScribe.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClassScribe.Transcription;

/// <summary>Where a transcript was saved.</summary>
public sealed record TranscriptFiles(string TextPath, string JsonPath);

/// <summary>Takes recorded audio through preparation, upload and saving the transcript.</summary>
public sealed class TranscriptionWorkflow(
    IAudioPreparer preparer,
    ITranscriptionClient client,
    KeytermLoader keytermLoader,
    TranscriptFormatter formatter,
    ClassFolders folders,
    ClassMarks marks,
    IOptions<ElevenLabsOptions> elevenLabs,
    IOptions<RecordingOptions> recording,
    ILogger<TranscriptionWorkflow> logger)
{
    private const string UploadFileName = "upload.wav";
    private const string TranscriptBaseName = "transcript";

    private static readonly UTF8Encoding Utf8WithBom = new(encoderShouldEmitUTF8Identifier: true);
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// Transcribes the segments of one recording and saves transcript.txt and transcript.json in its
    /// folder. The marks set while recording are placed in the transcript.
    /// </summary>
    public async Task<TranscriptFiles> TranscribeSegmentsAsync(IReadOnlyList<string> segmentPaths, string folder, CancellationToken cancellationToken)
    {
        try
        {
            var uploadPath = Path.Combine(folder, UploadFileName);
            File.Delete(uploadPath);
            logger.LogInformation("Preparing the recording for upload ({Segments} segment(s)); this takes a moment for a long class", segmentPaths.Count);
            preparer.Prepare(segmentPaths, uploadPath);

            var files = await UploadAndSaveAsync(uploadPath, folder, marks.Read(folder), cancellationToken);
            if (!recording.Value.KeepUploadFile) File.Delete(uploadPath);
            return files;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError("Could not transcribe the recording in {Folder}", folder);
            throw;
        }
    }

    /// <summary>
    /// Transcribes one audio or video file into a new folder of its own in the recordings folder; the
    /// file itself stays where it is. A WAV file is first reduced to 16 kHz mono; anything else is
    /// uploaded as it is.
    /// </summary>
    public async Task<TranscriptFiles> TranscribeFileAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            var folder = folders.CreateForFile(path);
            logger.LogInformation("The transcript of {File} goes to {Folder}", Path.GetFileName(path), folder);

            var uploadPath = Path.Combine(folder, UploadFileName);
            if (!TryPrepare(path, uploadPath))
                return await UploadAndSaveAsync(path, folder, [], cancellationToken);

            var files = await UploadAndSaveAsync(uploadPath, folder, [], cancellationToken);
            if (!recording.Value.KeepUploadFile) File.Delete(uploadPath);
            return files;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError("Could not transcribe {Path}", path);
            throw;
        }
    }

    /// <summary>Makes the smaller upload copy of a WAV file. Returns false when the file should be uploaded as it is.</summary>
    private bool TryPrepare(string path, string uploadPath)
    {
        if (!string.Equals(Path.GetExtension(path), ".wav", StringComparison.OrdinalIgnoreCase)) return false;

        try
        {
            File.Delete(uploadPath);
            preparer.Prepare([path], uploadPath);
            return true;
        }
        catch (Exception ex) when (ex is NotSupportedException or FormatException or ArgumentException or InvalidOperationException)
        {
            logger.LogWarning("Could not reduce {Path} to 16 kHz mono ({Message}); uploading the original instead", path, ex.Message);
            File.Delete(uploadPath);
            return false;
        }
    }

    /// <summary>Uploads the audio and writes the raw JSON and the formatted text, with the marks in place.</summary>
    private async Task<TranscriptFiles> UploadAndSaveAsync(string audioPath, string folder, IReadOnlyList<ClassMark> classMarks, CancellationToken cancellationToken)
    {
        var keyterms = keytermLoader.Load(ResolveKeytermsPath());
        var result = await client.TranscribeAsync(audioPath, keyterms, cancellationToken);

        var files = new TranscriptFiles(Path.Combine(folder, $"{TranscriptBaseName}.txt"), Path.Combine(folder, $"{TranscriptBaseName}.json"));
        await File.WriteAllTextAsync(files.JsonPath, result.RawJson, Utf8NoBom, cancellationToken);
        await File.WriteAllTextAsync(files.TextPath, formatter.Format(result, classMarks), Utf8WithBom, cancellationToken);
        return files;
    }

    /// <summary>Finds the glossary file, looking next to the executable for a relative path.</summary>
    private string ResolveKeytermsPath()
    {
        var configured = elevenLabs.Value.KeytermsFile;
        return Path.IsPathRooted(configured) ? configured : Path.Combine(AppContext.BaseDirectory, configured);
    }
}
