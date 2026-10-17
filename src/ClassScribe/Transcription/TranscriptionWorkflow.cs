using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using ClassScribe.Audio;
using ClassScribe.Settings;
using ClassScribe.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClassScribe.Transcription;

/// <summary>Where a transcript was saved: the Markdown document and ElevenLabs' answer next to it.</summary>
public sealed record TranscriptFiles(string TranscriptPath, string JsonPath);

/// <summary>
/// A recording is longer than ElevenLabs takes in one file (see <see cref="TranscriptionWorkflow.LongestRecording"/>), so
/// nothing of it was prepared or sent. Trying again would find the same length.
/// </summary>
public sealed class RecordingTooLongException(string message) : Exception(message);

/// <summary>
/// Takes recorded audio through preparation, upload and saving the transcript, and lays an answer saved earlier out again
/// for free, with the settings of the run's own scope. Every transcript is saved as a new numbered copy, which never
/// overwrites another (see <see cref="TranscriptCopies"/>), and the upload copy made for a run is removed when the run
/// ends, whether it succeeded, failed or was cancelled, unless Recording:KeepUploadFile keeps it.
/// </summary>
public sealed class TranscriptionWorkflow(
    IAudioPreparer preparer,
    ITranscriptionClient client,
    KeytermLoader keytermLoader,
    TranscriptFormatter formatter,
    ClassFolders folders,
    ClassMarks marks,
    IOptionsSnapshot<ElevenLabsOptions> elevenLabs,
    IOptionsSnapshot<RecordingOptions> recording,
    TranscriptionProgress progress,
    ILogger<TranscriptionWorkflow> logger)
{
    /// <summary>
    /// The longest recording sent in one upload. ElevenLabs takes at most 10 hours of audio per file; half an hour is kept
    /// in hand, so that a recording is refused here, before anything is prepared, rather than by ElevenLabs.
    /// </summary>
    public static readonly TimeSpan LongestRecording = TimeSpan.FromHours(9.5);

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// The line <see cref="TranscriptFormatter"/> writes under the title of a recording's transcript, such as "Recorded
    /// 15 October 2026, 20:55 UTC.", and never in the transcript of a file transcribed on its own.
    /// </summary>
    private static readonly Regex RecordedLine = new(@"^Recorded [0-9]{1,2} \p{L}+ [0-9]{4}, [0-9]{2}:[0-9]{2} UTC\b", RegexOptions.CultureInvariant);

    /// <summary>
    /// Transcribes a recording from every one of its segments on disk, in recording order, and saves the transcript under
    /// the recording's name, next to the audio, with the marks set while recording and the notes made in it, and, for a
    /// recording taken up again after it stopped, the number of parts it was made in. A recording made with the class
    /// and your microphone has a second track on disk, its .mic.wav files, and the two are mixed into the one file that
    /// is sent (see <see cref="IAudioPreparer.Prepare(UploadTracks, string, CancellationToken)"/>); in person your
    /// microphone is the main track, under the recording's plain names, and there is nothing to mix. A recording whose
    /// class track was never written, because the class could not be recorded while your microphone could, has only the
    /// .mic.wav files, and they are sent on their own, as in person. A recording transcribed before keeps its earlier
    /// transcript and gets the next copy. A recording longer than <see cref="LongestRecording"/> is refused before
    /// anything is prepared. The upload copy is removed when the run ends, after a failure or a cancel as well, unless
    /// Recording:KeepUploadFile keeps it.
    /// </summary>
    /// <exception cref="FileNotFoundException">The recording has no audio on disk, of either track.</exception>
    /// <exception cref="RecordingTooLongException">The recording is longer than <see cref="LongestRecording"/>.</exception>
    public async Task<TranscriptFiles> TranscribeRecordingAsync(RecordingFiles files, CancellationToken cancellationToken)
    {
        string? upload = null;
        var saved = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var segments = files.FindSegments();
            var microphone = files.FindSegments(Track.Microphone);
            if (segments.Count == 0 && microphone.Count == 0)
                throw new FileNotFoundException($"There is no audio of the recording {files.BaseName} in {files.Folder}, so there is nothing to transcribe.", files.Segment(1));

            // The length is the main track's alone: your microphone's track runs on the same clock as the class, so it
            // is as long, and adding the two would refuse a class of five hours. The microphone's files only decide
            // what the refusal advises, unless they are all there is, and are then the track measured.
            var microphoneAlone = segments.Count == 0;
            RefuseIfTooLong(files, microphoneAlone ? microphone : segments, microphoneAlone ? [] : microphone, microphoneAlone);

            // An upload copy left by a run that was ended before it could remove it is made afresh.
            File.Delete(files.Upload);
            progress.Report(TranscriptionStep.Preparing);
            if (microphoneAlone)
            {
                logger.LogInformation("Only your microphone was recorded ({Segments} segment(s)); it is sent on its own", microphone.Count);
                preparer.Prepare(microphone, files.Upload, cancellationToken);
            }
            else if (microphone.Count == 0)
            {
                // A single track is prepared as it always was; the mix is only asked for when there is a second track.
                logger.LogInformation("Preparing the recording for upload ({Segments} segment(s)); this takes a moment for a long class", segments.Count);
                preparer.Prepare(segments, files.Upload, cancellationToken);
            }
            else
            {
                logger.LogInformation("Preparing the recording for upload ({Segments} segment(s) of the class and {MicrophoneSegments} of your microphone); this takes a moment for a long class", segments.Count, microphone.Count);
                preparer.Prepare(new UploadTracks(segments, microphone), files.Upload, cancellationToken);
            }

            upload = files.Upload;

            var transcript = await UploadAndSaveAsync(upload, files, files.Name, files.StartedUtc, marks.Read(files), cancellationToken);
            saved = true;
            return transcript;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError("Could not transcribe the recording {Recording}", files.BaseName);
            throw;
        }
        finally
        {
            if (upload is not null && !recording.Value.KeepUploadFile) RemoveUploadCopy(upload, saved);
        }
    }

    /// <summary>
    /// Transcribes one audio or video file into a folder named after it under <paramref name="outputRoot"/>, the recordings
    /// folder given for this run (transcribe --output), or under Recording:OutputFolder when that is null; the file itself
    /// stays where it is. A WAV file is first reduced to 16 kHz mono; anything else is uploaded as it is.
    /// </summary>
    public async Task<TranscriptFiles> TranscribeFileAsync(string path, string? outputRoot, CancellationToken cancellationToken)
    {
        string? upload = null;
        var saved = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var files = folders.CreateForFile(path, outputRoot);
            logger.LogInformation("The transcript of {File} goes to {Folder}", Path.GetFileName(path), files.Folder);

            if (TryPrepare(path, files.Upload, cancellationToken)) upload = files.Upload;
            var transcript = await UploadAndSaveAsync(upload ?? path, files, files.Name, recordedUtc: null, [], cancellationToken);
            saved = true;
            return transcript;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError("Could not transcribe {Path}", path);
            throw;
        }
        finally
        {
            if (upload is not null && !recording.Value.KeepUploadFile) RemoveUploadCopy(upload, saved);
        }
    }

    /// <summary>
    /// Lays an answer of ElevenLabs saved earlier out again as a new transcript, for free: no key is needed and nothing is
    /// sent. The answer is <paramref name="answerPath"/>, or else the recording's newest saved answer, never one kept as
    /// unread (see <see cref="TranscriptCopies.NewestAnswer"/>). It is laid out with the recording's marks and the
    /// Transcript settings as they are now, and written alone as the first copy that has neither a transcript nor an
    /// answer, so nothing is overwritten. Returns the new transcript's path. A recording keeps its recording time, even
    /// once its audio is deleted, and a file transcribed on its own, which had none, gets none (see <see cref="IsRecording"/>).
    /// </summary>
    /// <exception cref="FileNotFoundException">The recording has no saved answer, or the answer named is not there.</exception>
    /// <exception cref="TranscriptionException">The answer cannot be read.</exception>
    public async Task<string> RebuildAsync(RecordingFiles files, string? answerPath, CancellationToken cancellationToken)
    {
        string? answer = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            answer = answerPath ?? TranscriptCopies.NewestAnswer(files)
                ?? throw new FileNotFoundException("This recording has no saved answer from ElevenLabs yet; transcribe it first.", files.Json());

            var result = ElevenLabsAnswer.Parse(await File.ReadAllTextAsync(answer, cancellationToken), HttpStatusCode.OK);
            var recordedUtc = IsRecording(files) ? files.StartedUtc : null;
            var classMarks = marks.Read(files);
            var rebuilt = TranscriptCopies.SaveRebuilt(files, formatter.Format(result, files.Name, recordedUtc, classMarks, ClassMarks.PartsIn(classMarks)), logger);
            logger.LogInformation("Rebuilt from {Answer} into {Transcript}; nothing was uploaded", Path.GetFileName(answer), Path.GetFileName(rebuilt));
            return rebuilt;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (answer is null)
                logger.LogError("Could not rebuild the transcript of {Recording}", files.BaseName);
            else
                logger.LogError("Could not rebuild the transcript of {Recording} from {Answer}", files.BaseName, Path.GetFileName(answer));
            throw;
        }
    }

    /// <summary>
    /// True when the files are a recording's, whose name gives the time it started, rather than those of a file
    /// transcribed on its own, whose name gives the time it was transcribed: the recording's audio, of either track, or
    /// its marks are beside them, or one of its transcripts says when it was recorded, as they still do once the audio
    /// is deleted. With none of these left the two cannot be told apart, and false claims no recording time that may be
    /// wrong.
    /// </summary>
    private static bool IsRecording(RecordingFiles files) =>
        files.FindSegments().Count > 0 || files.FindSegments(Track.Microphone).Count > 0 || File.Exists(files.Marks)
        || TranscriptCopies.Transcripts(files).Any(SaysWhenRecorded);

    /// <summary>
    /// True when the transcript at <paramref name="path"/> says when its recording was made, in the line the formatter
    /// writes under the title (see <see cref="RecordedLine"/>). It can be read while another ClassScribe writes it.
    /// </summary>
    private static bool SaysWhenRecorded(string path)
    {
        using var reader = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
        reader.ReadLine();   // the title
        string? line;
        do line = reader.ReadLine(); while (line is { Length: 0 });
        return line is not null && RecordedLine.IsMatch(line);
    }

    /// <summary>
    /// Refuses a recording whose sent track's segments, <paramref name="segments"/>, hold more than <see cref="LongestRecording"/>
    /// of audio together, before anything is prepared. Each segment is read to its end, as the preparer reads it (see
    /// <see cref="UploadTimeLimit.AudioLength"/>); a file the recorder did not write counts as empty, as it does in the
    /// list of recordings. The microphone's segments beside the class, <paramref name="microphone"/>, are not measured:
    /// they only decide the advice. A folder holding the copied .wav files alone looks like a class-only recording, which
    /// would drop your microphone from every piece without a word, so when the recording has a microphone track the
    /// message says to copy the matching .mic.wav files along with them; a recording that is the microphone's files
    /// alone (<paramref name="microphoneAlone"/>) is transcribed by one of them, since a folder is read by its .wav files.
    /// </summary>
    private static void RefuseIfTooLong(RecordingFiles files, IReadOnlyList<string> segments, IReadOnlyList<string> microphone, bool microphoneAlone)
    {
        var length = TimeSpan.Zero;
        foreach (var segment in segments) length += UploadTimeLimit.AudioLength(segment) ?? TimeSpan.Zero;
        if (length <= LongestRecording) return;

        var advice = microphoneAlone
            ? "copy some of its .mic.wav files into a folder of their own and transcribe one of them there by its path."
            : microphone.Count == 0
                ? "copy some of its .wav files into a folder of their own and transcribe that folder."
                : "copy some of its .wav files into a folder of their own, with the matching .mic.wav files of your microphone beside them under the same names, and transcribe that folder; without the .mic.wav files only the class is sent.";
        throw new RecordingTooLongException(
            $"{files.BaseName} holds {Durations.Format(length)} of audio, and ElevenLabs takes at most 10 hours per file, so ClassScribe sends at most 9 and a half hours. Nothing was prepared or sent. To transcribe it in pieces, {advice}");
    }

    /// <summary>Makes the smaller upload copy of a WAV file. Returns false when the file should be uploaded as it is.</summary>
    private bool TryPrepare(string path, string uploadPath, CancellationToken cancellationToken)
    {
        if (!string.Equals(Path.GetExtension(path), ".wav", StringComparison.OrdinalIgnoreCase)) return false;

        try
        {
            File.Delete(uploadPath);
            logger.LogInformation("Preparing {File} for upload; this takes a moment for a long recording", Path.GetFileName(path));
            progress.Report(TranscriptionStep.Preparing);
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
        var keyterms = keytermLoader.Load(keytermLoader.ResolvePath(elevenLabs.Value.KeytermsFile));

        TranscriptionResult result;
        try
        {
            // The upload begins; the client says how far it gets.
            progress.Report(TranscriptionStep.Uploading);
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

        return Save(files, result, title, recordedUtc, classMarks);
    }

    /// <summary>
    /// Saves ElevenLabs' answer and the Markdown transcript made from it as the next free copy (see
    /// <see cref="TranscriptCopies.Save(RecordingFiles, string, Func{string}, ILogger)"/>). The answer has been paid
    /// for, so a Ctrl+C that comes now no longer stops this, and it is saved before the transcript is made. An answer
    /// that cannot be saved as the transcript's is kept under a name of its own (see <see cref="KeepAnswer"/>), and
    /// once it is saved, whatever keeps the transcript from being made leaves it as the place to look. Either way the
    /// error is marked as following a charged answer, with where the answer is, if anywhere, and its transcription ID
    /// (see <see cref="ChargedAnswer"/>).
    /// </summary>
    private TranscriptFiles Save(RecordingFiles files, TranscriptionResult result, string title, DateTimeOffset? recordedUtc, IReadOnlyList<ClassMark> classMarks)
    {
        try
        {
            return TranscriptCopies.Save(files, result.RawJson, () => formatter.Format(result, title, recordedUtc, classMarks, ClassMarks.PartsIn(classMarks)), logger);
        }
        catch (Exception ex) when (ex.Data[TranscriptCopies.SavedAnswerKey] is string saved)
        {
            // Any failure counts here, not only the disk's: the answer is saved and paid for, so whatever kept the
            // transcript from being made, that answer is where its text is.
            logger.LogError("The transcript could not be made, but ElevenLabs' answer is saved in {Path}. It has been paid for, so look there for the transcript's text before transcribing again, which is charged again", saved);
            ChargedAnswer.Mark(ex, saved, result.TranscriptionId);
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ChargedAnswer.Mark(ex, KeepAnswer(files, result.RawJson, result.TranscriptionId), result.TranscriptionId);
            throw;
        }
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
    /// Removes the upload copy when its run is over, whether the transcript was saved or not; the copy may already have
    /// gone with its folder, during the upload. A copy that cannot be removed, because another program holds it, is only
    /// logged: after a saved transcript, reporting the run as failed would invite a second, charged attempt, and after a
    /// failure or a cancel, that is what the run reports.
    /// </summary>
    private void RemoveUploadCopy(string path, bool transcriptSaved)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Deliberately not rethrown: see the summary. The copy can be deleted by hand.
            if (transcriptSaved)
                logger.LogWarning(ex, "The transcript is saved, but the upload copy {Path} could not be removed; it can be deleted by hand", path);
            else
                logger.LogWarning(ex, "The upload copy {Path} could not be removed; it can be deleted by hand", path);
        }
    }

    /// <summary>True for a 2xx status: ElevenLabs answered, and has charged for the transcript.</summary>
    private static bool IsSuccess(HttpStatusCode? status) => (int?)status is >= 200 and <= 299;
}
