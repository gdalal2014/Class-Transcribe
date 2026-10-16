using System.Text;
using ClassScribe.Audio;
using ClassScribe.Settings;
using ClassScribe.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClassScribe.Transcription;

/// <summary>Where a transcript was saved: the Markdown document and ElevenLabs' answer next to it.</summary>
public sealed record TranscriptFiles(string TranscriptPath, string JsonPath);

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
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// Transcribes a recording from its segments and saves the transcript under the recording's name,
    /// next to the audio. The marks set while recording are placed in the transcript. A recording
    /// transcribed before keeps its earlier transcript and gets a second one.
    /// </summary>
    public async Task<TranscriptFiles> TranscribeRecordingAsync(RecordingFiles files, IReadOnlyList<string> segmentPaths, CancellationToken cancellationToken)
    {
        try
        {
            File.Delete(files.Upload);
            logger.LogInformation("Preparing the recording for upload ({Segments} segment(s)); this takes a moment for a long class", segmentPaths.Count);
            preparer.Prepare(segmentPaths, files.Upload);

            var saved = await UploadAndSaveAsync(files.Upload, files, files.Name, files.StartedUtc, marks.Read(files), cancellationToken);
            if (!recording.Value.KeepUploadFile) File.Delete(files.Upload);
            return saved;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError("Could not transcribe the recording {Recording}", files.BaseName);
            throw;
        }
    }

    /// <summary>
    /// Transcribes one audio or video file into a folder named after it in the recordings folder; the
    /// file itself stays where it is. A WAV file is first reduced to 16 kHz mono; anything else is
    /// uploaded as it is.
    /// </summary>
    public async Task<TranscriptFiles> TranscribeFileAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            var files = folders.CreateForFile(path);
            logger.LogInformation("The transcript of {File} goes to {Folder}", Path.GetFileName(path), files.Folder);

            if (!TryPrepare(path, files.Upload))
                return await UploadAndSaveAsync(path, files, files.Name, recordedUtc: null, [], cancellationToken);

            var saved = await UploadAndSaveAsync(files.Upload, files, files.Name, recordedUtc: null, [], cancellationToken);
            if (!recording.Value.KeepUploadFile) File.Delete(files.Upload);
            return saved;
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

    /// <summary>Uploads the audio and writes the raw JSON and the Markdown transcript, as the next free copy of the recording's transcript.</summary>
    private async Task<TranscriptFiles> UploadAndSaveAsync(
        string audioPath, RecordingFiles files, string title, DateTimeOffset? recordedUtc, IReadOnlyList<ClassMark> classMarks, CancellationToken cancellationToken)
    {
        var keyterms = keytermLoader.Load(ResolveKeytermsPath());
        var result = await client.TranscribeAsync(audioPath, keyterms, cancellationToken);

        // An earlier transcript, perhaps corrected by hand, is never overwritten.
        var copy = 1;
        while (File.Exists(files.Transcript(copy)) || File.Exists(files.Json(copy))) copy++;

        var saved = new TranscriptFiles(files.Transcript(copy), files.Json(copy));
        await File.WriteAllTextAsync(saved.JsonPath, result.RawJson, Utf8NoBom, cancellationToken);
        await File.WriteAllTextAsync(saved.TranscriptPath, formatter.Format(result, title, recordedUtc, classMarks), Utf8NoBom, cancellationToken);
        return saved;
    }

    /// <summary>Finds the glossary file, looking next to the executable for a relative path.</summary>
    private string ResolveKeytermsPath()
    {
        var configured = elevenLabs.Value.KeytermsFile;
        return Path.IsPathRooted(configured) ? configured : Path.Combine(AppContext.BaseDirectory, configured);
    }
}
