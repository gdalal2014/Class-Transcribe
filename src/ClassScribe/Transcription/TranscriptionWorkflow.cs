using System.Net;
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
            cancellationToken.ThrowIfCancellationRequested();
            File.Delete(files.Upload);
            logger.LogInformation("Preparing the recording for upload ({Segments} segment(s)); this takes a moment for a long class", segmentPaths.Count);
            preparer.Prepare(segmentPaths, files.Upload, cancellationToken);

            var saved = await UploadAndSaveAsync(files.Upload, files, files.Name, files.StartedUtc, marks.Read(files), cancellationToken);
            if (!recording.Value.KeepUploadFile) RemoveUploadCopy(files.Upload);
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
            cancellationToken.ThrowIfCancellationRequested();
            var files = folders.CreateForFile(path);
            logger.LogInformation("The transcript of {File} goes to {Folder}", Path.GetFileName(path), files.Folder);

            if (!TryPrepare(path, files.Upload, cancellationToken))
                return await UploadAndSaveAsync(path, files, files.Name, recordedUtc: null, [], cancellationToken);

            var saved = await UploadAndSaveAsync(files.Upload, files, files.Name, recordedUtc: null, [], cancellationToken);
            if (!recording.Value.KeepUploadFile) RemoveUploadCopy(files.Upload);
            return saved;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError("Could not transcribe {Path}", path);
            throw;
        }
    }

    /// <summary>Makes the smaller upload copy of a WAV file. Returns false when the file should be uploaded as it is.</summary>
    private bool TryPrepare(string path, string uploadPath, CancellationToken cancellationToken)
    {
        if (!string.Equals(Path.GetExtension(path), ".wav", StringComparison.OrdinalIgnoreCase)) return false;

        try
        {
            File.Delete(uploadPath);
            logger.LogInformation("Preparing {File} for upload; this takes a moment for a long recording", Path.GetFileName(path));
            preparer.Prepare([path], uploadPath, cancellationToken);
            return true;
        }
        catch (Exception ex) when (ex is NotSupportedException or FormatException or ArgumentException or InvalidOperationException)
        {
            logger.LogWarning("Could not reduce {Path} to 16 kHz mono ({Message}); uploading the original instead", path, ex.Message);
            File.Delete(uploadPath);
            return false;
        }
    }

    /// <summary>
    /// Uploads the audio, with the glossary that ElevenLabs:KeytermsFile names, and saves ElevenLabs' answer and the
    /// Markdown transcript, as the next free copy of the recording's transcript. An answer that has arrived has been
    /// paid for: one that cannot be read is kept for later rather than lost, and the error is marked as following a
    /// charged answer (see <see cref="ChargedAnswer"/>), so that whoever reports it can say a new try is charged again.
    /// No transcription ID can be read from such an answer, so the mark carries none.
    /// </summary>
    private async Task<TranscriptFiles> UploadAndSaveAsync(
        string audioPath, RecordingFiles files, string title, DateTimeOffset? recordedUtc, IReadOnlyList<ClassMark> classMarks, CancellationToken cancellationToken)
    {
        // A cancel made while the upload file was prepared must not go on to "Uploading", which would look as if it had been ignored.
        cancellationToken.ThrowIfCancellationRequested();
        var keyterms = keytermLoader.Load(KeytermLoader.ResolvePath(elevenLabs.Value.KeytermsFile));

        TranscriptionResult result;
        try
        {
            result = await client.TranscribeAsync(audioPath, keyterms, cancellationToken);
        }
        catch (TranscriptionException ex) when (ex.ResponseBody is { } answer && IsSuccess(ex.StatusCode))
        {
            // ElevenLabs answered, and charged for it, but the answer could not be read. It is kept, so that what was paid for is not lost.
            ChargedAnswer.Mark(ex, KeepAnswer(files, answer, transcriptionId: null), transcriptionId: null);
            throw;
        }

        if (string.IsNullOrWhiteSpace(result.Text))
            logger.LogWarning("ElevenLabs heard no speech in {File}: the transcript is empty", Path.GetFileName(audioPath));

        return await SaveAsync(files, result, title, recordedUtc, classMarks);
    }

    /// <summary>
    /// Saves ElevenLabs' answer and the Markdown transcript made from it, as the next free copy. The answer has
    /// been paid for, so a Ctrl+C that comes now no longer stops this. An answer that cannot be saved as the
    /// transcript's is kept under a name of its own (see <see cref="KeepAnswer"/>), and once it is saved, whatever
    /// keeps the transcript from being made leaves it as the place to look. Either way the error is marked as
    /// following a charged answer, with where the answer is, if anywhere, and its transcription ID (see
    /// <see cref="ChargedAnswer"/>).
    /// </summary>
    private async Task<TranscriptFiles> SaveAsync(RecordingFiles files, TranscriptionResult result, string title, DateTimeOffset? recordedUtc, IReadOnlyList<ClassMark> classMarks)
    {
        var saved = NextFreeCopy(files);
        try
        {
            await File.WriteAllTextAsync(saved.JsonPath, result.RawJson, Utf8NoBom, CancellationToken.None);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ChargedAnswer.Mark(ex, KeepAnswer(files, result.RawJson, result.TranscriptionId), result.TranscriptionId);
            throw;
        }

        try
        {
            await File.WriteAllTextAsync(saved.TranscriptPath, formatter.Format(result, title, recordedUtc, classMarks), Utf8NoBom, CancellationToken.None);
        }
        catch (Exception ex)
        {
            // Any failure counts here, not only the disk's: the answer is saved and paid for, so whatever kept the
            // transcript from being made, that answer is where its text is.
            logger.LogError("The transcript could not be made, but ElevenLabs' answer is saved in {Path}. It has been paid for, so look there for the transcript's text before transcribing again, which is charged again", saved.JsonPath);
            ChargedAnswer.Mark(ex, saved.JsonPath, result.TranscriptionId);
            throw;
        }

        return saved;
    }

    /// <summary>The next copy of the recording's transcript that is free: an earlier transcript, perhaps corrected by hand, is never overwritten.</summary>
    private static TranscriptFiles NextFreeCopy(RecordingFiles files)
    {
        var copy = 1;
        while (File.Exists(files.Transcript(copy)) || File.Exists(files.Json(copy))) copy++;
        return new TranscriptFiles(files.Transcript(copy), files.Json(copy));
    }

    /// <summary>
    /// Keeps an answer ElevenLabs has charged for that did not become a transcript, so that what was paid for is not
    /// lost, and returns where it went. It goes under a name of its own (<see cref="RecordingFiles.NextFreeUnreadAnswer"/>),
    /// which counts as no transcript and takes no transcript's place: beside the recording or, when its folder will
    /// not take it, under the same name in the recordings folder, which a problem with one recording's folder may have
    /// spared. Nothing is ever overwritten. When neither place works, it returns null and logs the answer's
    /// <paramref name="transcriptionId"/>, with which the transcript can still be fetched from ElevenLabs. When that is
    /// null, as it is for an answer that could not be read, the log says the ID is unknown.
    /// </summary>
    private string? KeepAnswer(RecordingFiles files, string answer, string? transcriptionId)
    {
        // The recording's own names, first in its folder, then in the recordings folder.
        foreach (var place in new[] { files, files with { Folder = folders.Root } })
        {
            var target = place.NextFreeUnreadAnswer();
            try
            {
                using (var writer = new StreamWriter(new FileStream(target, FileMode.CreateNew, FileAccess.Write), Utf8NoBom))
                    writer.Write(answer);

                logger.LogWarning("ElevenLabs' answer is kept in {Path}. It has been paid for, so look there for the transcript's text before transcribing again, which is charged again", target);
                return target;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Deliberately not rethrown: the error that led here is the one to report, and the next place is tried.
                logger.LogWarning(ex, "Could not keep ElevenLabs' answer in {Path}", target);
            }
        }

        logger.LogError("ElevenLabs' answer could not be kept anywhere. Its transcription ID at ElevenLabs is {Id}. It has been paid for, so transcribing again is charged again", transcriptionId ?? "unknown");
        return null;
    }

    /// <summary>
    /// Removes the upload copy once the transcript is saved. A copy that cannot be removed, because another
    /// program holds it, is only logged: the transcript is saved and paid for, and reporting the run as failed
    /// would invite a second, charged attempt.
    /// </summary>
    private void RemoveUploadCopy(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Deliberately not rethrown: see the summary. The copy can be deleted by hand.
            logger.LogWarning(ex, "The transcript is saved, but the upload copy {Path} could not be removed; it can be deleted by hand", path);
        }
    }

    /// <summary>True for a 2xx status: ElevenLabs answered, and has charged for the transcript.</summary>
    private static bool IsSuccess(HttpStatusCode? status) => (int?)status is >= 200 and <= 299;
}
