using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using ClassScribe.Audio;
using ClassScribe.Commands;
using ClassScribe.Desktop;
using ClassScribe.Settings;
using ClassScribe.Storage;
using ClassScribe.YouTube;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClassScribe.Transcription;

/// <summary>
/// Takes recorded audio through preparation, upload and saving the transcript, and lays an answer saved earlier out again
/// for free, with the settings of the run's own scope. Every transcript is saved as a new numbered copy, which never
/// overwrites another (see <see cref="ITranscriptCopies"/>), and the upload copy made for a run is removed when the run
/// ends, whether it succeeded, failed or was cancelled, unless Recording:KeepUploadFile keeps it.
/// </summary>
public sealed class TranscriptionWorkflow(
    IAudioPreparer preparer,
    ITranscriptionClient client,
    GlossaryChoice glossaries,
    TranscriptFormatter formatter,
    IClassFolders folders,
    IClassMarks marks,
    ITranscriptCopies copies,
    IRecordingFolder recordings,
    IFileProbe probe,
    IOptionsSnapshot<RecordingOptions> recording,
    IOptionsSnapshot<ElevenLabsOptions> elevenLabs,
    TranscriptionProgress progress,
    UsageReport usage,
    ILogger<TranscriptionWorkflow> logger)
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// The line <see cref="TranscriptFormatter"/> writes under the title of a recording's transcript, such as "Recorded
    /// 15 October 2026, 20:55 UTC.", or for a downloaded YouTube video "Downloaded 18 October 2026, 14:02 UTC.", and never in
    /// the transcript of a file transcribed on its own.
    /// </summary>
    private static readonly Regex RecordedLine = new(@"^(?:Recorded|Downloaded) [0-9]{1,2} \p{L}+ [0-9]{4}, [0-9]{2}:[0-9]{2} UTC\b", RegexOptions.CultureInvariant);

    /// <summary>
    /// Transcribes a recording from every one of its segments on disk, in recording order, and saves the transcript under
    /// the recording's name, next to the audio, with the marks set while recording and the notes made in it, and, for a
    /// recording taken up again after it stopped, the number of parts it was made in. A recording made with the class
    /// and your microphone has a second track on disk, its .mic.wav files, and the two are mixed into the one file that
    /// is sent (see <see cref="IAudioPreparer.Prepare(UploadTracks, string, CancellationToken)"/>); in person your
    /// microphone is the main track, under the recording's plain names, and there is nothing to mix. A recording whose
    /// class track was never written, because the class could not be recorded while your microphone could, has only the
    /// .mic.wav files, and they are sent on their own, as in person. A recording transcribed before keeps its earlier
    /// transcript and gets the next copy. A recording longer than ElevenLabs:LongestFileHours less half an hour, and the minute a
    /// recording may run past it (see <see cref="TranscriptionLimits.Sends"/>), is refused before anything is prepared. The
    /// upload copy is removed when the run ends, after a failure or a cancel as well, unless Recording:KeepUploadFile keeps it.
    /// The glossary is the one decided when the recording started (see <see cref="GlossaryChoice"/>).
    /// </summary>
    /// <exception cref="FileNotFoundException">The recording has no audio on disk, of either track.</exception>
    /// <exception cref="RecordingTooLongException">The recording is longer than one file sends, ElevenLabs:LongestFileHours less half an hour and the minute past it (see <see cref="TranscriptionLimits.Sends"/>).</exception>
    public Task<TranscriptFiles> TranscribeRecordingAsync(RecordingFiles files, CancellationToken cancellationToken) =>
        TranscribeRecordingAsync(files, keyterms: null, cancellationToken);

    /// <summary>
    /// Transcribes a recording as <see cref="TranscribeRecordingAsync(RecordingFiles, CancellationToken)"/> does, with
    /// <paramref name="keyterms"/>, when it is not null, as the glossary given to the command (transcribe --keyterms, a file
    /// or none), over the one decided when the recording started.
    /// </summary>
    /// <exception cref="FileNotFoundException">The recording has no audio on disk, of either track.</exception>
    /// <exception cref="RecordingTooLongException">The recording is longer than one file sends, ElevenLabs:LongestFileHours less half an hour and the minute past it (see <see cref="TranscriptionLimits.Sends"/>).</exception>
    public Task<TranscriptFiles> TranscribeRecordingAsync(RecordingFiles files, string? keyterms, CancellationToken cancellationToken) =>
        TranscribeRecordingAsync(files, keyterms, run: null, cancellationToken);

    /// <summary>
    /// Transcribes a recording as <see cref="TranscribeRecordingAsync(RecordingFiles, string, CancellationToken)"/> does, for
    /// the run that recorded it: <paramref name="run"/> is the glossary its start decided (see <see cref="GlossaryChoice.AtStart"/>),
    /// which is sent when the recording's record gives none, as the start line named it (see <see cref="GlossaryChoice.ForTranscript"/>).
    /// </summary>
    /// <exception cref="FileNotFoundException">The recording has no audio on disk, of either track.</exception>
    /// <exception cref="RecordingTooLongException">The recording is longer than one file sends, ElevenLabs:LongestFileHours less half an hour and the minute past it (see <see cref="TranscriptionLimits.Sends"/>).</exception>
    public async Task<TranscriptFiles> TranscribeRecordingAsync(RecordingFiles files, string? keyterms, GlossaryPick? run, CancellationToken cancellationToken)
    {
        string? upload = null;
        var saved = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var segments = recordings.FindSegments(files);
            var microphone = recordings.FindSegments(files, Track.Microphone);
            if (segments.Count == 0 && microphone.Count == 0)
                throw new FileNotFoundException($"There is no audio of the recording {files.BaseName} in {files.Folder}, so there is nothing to transcribe.", files.Segment(1));

            // The length is the main track's alone: your microphone's track runs on the same clock as the class, so it
            // is as long, and adding the two would refuse a class of five hours. The microphone's files only decide
            // what the refusal advises, unless they are all there is, and are then the track measured.
            var microphoneAlone = segments.Count == 0;
            RefuseIfTooLong(files, microphoneAlone ? microphone : segments, microphoneAlone ? [] : microphone, microphoneAlone);

            // An upload copy left by a run that was ended before it could remove it is made afresh.
            recordings.DeleteUploadCopy(files);
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

            var transcript = await UploadAndSaveAsync(upload, files, files.Name, files.StartedUtc, marks.Read(files), VideoOf(files), keyterms, run, cancellationToken);
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
            if (upload is not null && !recording.Value.KeepUploadFile) RemoveUploadCopy(files, saved);
        }
    }

    /// <summary>
    /// Transcribes one audio or video file into a folder named after it under <paramref name="outputRoot"/>, the recordings
    /// folder given for this run (transcribe --output), or under Recording:OutputFolder when that is null; the file itself
    /// stays where it is. A WAV file is first reduced to 16 kHz mono; anything else is uploaded as it is.
    /// </summary>
    public Task<TranscriptFiles> TranscribeFileAsync(string path, string? outputRoot, CancellationToken cancellationToken) =>
        TranscribeFileAsync(path, outputRoot, keyterms: null, cancellationToken);

    /// <summary>
    /// Transcribes one audio or video file as <see cref="TranscribeFileAsync(string, string, CancellationToken)"/> does, with
    /// <paramref name="keyterms"/>, when it is not null, as the glossary given to the command (a file or none).
    /// </summary>
    public async Task<TranscriptFiles> TranscribeFileAsync(string path, string? outputRoot, string? keyterms, CancellationToken cancellationToken)
    {
        string? upload = null;
        RecordingFiles? files = null;
        var saved = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            files = folders.CreateForFile(path, outputRoot);
            logger.LogInformation("The transcript of {File} goes to {Folder}", Path.GetFileName(path), files.Folder);

            if (TryPrepare(path, files, cancellationToken)) upload = files.Upload;
            var transcript = await UploadAndSaveAsync(upload ?? path, files, files.Name, recordedUtc: null, [], video: null, keyterms, run: null, cancellationToken);
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
            if (upload is not null && !recording.Value.KeepUploadFile) RemoveUploadCopy(files!, saved);
        }
    }

    /// <summary>
    /// Lays an answer of ElevenLabs saved earlier out again as a new transcript, for free: no key is needed and nothing is
    /// sent. The answer is <paramref name="answerPath"/>, or else the recording's newest saved answer, never one kept as
    /// unread (see <see cref="ITranscriptCopies.NewestAnswer"/>). It is laid out with the recording's marks and the
    /// Transcript settings as they are now, and written alone as the first copy that has neither a transcript nor an
    /// answer, so nothing is overwritten. Returns the new transcript's path. A recording keeps its recording time, even
    /// once its audio is deleted, and a file transcribed on its own, which had none, gets none (see <see cref="IsRecording"/>).
    /// A YouTube video's recording keeps its heading, the video's title and link, from its source file.
    /// </summary>
    /// <exception cref="FileNotFoundException">The recording has no saved answer, or the answer named is not there.</exception>
    /// <exception cref="TranscriptionException">The answer cannot be read.</exception>
    public async Task<string> RebuildAsync(RecordingFiles files, string? answerPath, CancellationToken cancellationToken)
    {
        string? answer = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            answer = answerPath ?? copies.NewestAnswer(files)
                ?? throw new FileNotFoundException("This recording has no saved answer from ElevenLabs yet; transcribe it first.", files.Json());

            var result = ElevenLabsAnswer.Parse(await File.ReadAllTextAsync(answer, cancellationToken), HttpStatusCode.OK);
            var recordedUtc = IsRecording(files) ? files.StartedUtc : null;
            var classMarks = marks.Read(files);
            var rebuilt = copies.SaveRebuilt(files, formatter.Format(result, files.Name, recordedUtc, classMarks, ClassMark.PartsIn(classMarks), VideoOf(files)), logger);
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
    /// transcribed on its own, whose name gives the time it was transcribed: the recording's audio, of either track, its
    /// marks or a YouTube video's source file are beside them, or one of its transcripts says when it was recorded or
    /// downloaded, as they still do once the audio is deleted. With none of these left the two cannot be told apart, and
    /// false claims no recording time that may be wrong.
    /// </summary>
    private bool IsRecording(RecordingFiles files) =>
        recordings.FindSegments(files).Count > 0 || recordings.FindSegments(files, Track.Microphone).Count > 0 || probe.FileExists(files.Marks) || probe.FileExists(files.Source)
        || copies.Transcripts(files).Any(SaysWhenRecorded);

    /// <summary>
    /// The YouTube video <paramref name="files"/> is the recording of, from its source file, for the transcript's heading;
    /// null for a class's recording. A source file that cannot be read is said, and the transcript is laid out as a class's.
    /// </summary>
    private VideoSource? VideoOf(RecordingFiles files)
    {
        try
        {
            return VideoSourceFile.Read(files.Source);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            // Deliberately not rethrown: the source only heads the transcript with the video's title and link; a paid answer
            // must never be lost over it, so the transcript is laid out as a class's, and this says why.
            logger.LogWarning("The video's source file of {Recording} could not be read ({Reason}); its transcript is headed by the recording's name instead", files.BaseName, ex.Message);
            return null;
        }
    }

    /// <summary>
    /// True when the transcript at <paramref name="path"/> says when its recording was made, in the line the formatter
    /// writes under the title (see <see cref="RecordedLine"/>), read through the copies' port while another ClassScribe
    /// may write it.
    /// </summary>
    private bool SaysWhenRecorded(string path) => copies.LineUnderTitle(path) is { } line && RecordedLine.IsMatch(line);

    /// <summary>
    /// Refuses a recording whose sent track's segments, <paramref name="segments"/>, hold more audio together than one file
    /// sends, ElevenLabs:LongestFileHours less half an hour and the minute a recording may run past it, as one the safety stop
    /// ended there does (see <see cref="TranscriptionLimits.Sends"/>), before anything is prepared, with the limits in words
    /// from the settings. Each segment is read to its end, as the preparer
    /// reads it (see <see cref="UploadTimeLimit.AudioLength"/>); a file the recorder did not write counts as empty, as it
    /// does in the list of recordings. The microphone's segments beside the class, <paramref name="microphone"/>, are not measured:
    /// they only decide the advice. A folder holding the copied .wav files alone looks like a class-only recording, which
    /// would drop your microphone from every piece without a word, so when the recording has a microphone track the
    /// message says to copy the matching .mic.wav files along with them; a recording that is the microphone's files
    /// alone (<paramref name="microphoneAlone"/>) is transcribed by one of them, since a folder is read by its .wav files.
    /// </summary>
    private void RefuseIfTooLong(RecordingFiles files, IReadOnlyList<string> segments, IReadOnlyList<string> microphone, bool microphoneAlone)
    {
        var limits = TranscriptionLimits.From(elevenLabs.Value);
        var length = TimeSpan.Zero;
        foreach (var segment in segments) length += UploadTimeLimit.AudioLength(segment) ?? TimeSpan.Zero;
        if (limits.Sends(length)) return;

        var advice = microphoneAlone
            ? "copy some of its .mic.wav files into a folder of their own and transcribe one of them there by its path."
            : microphone.Count == 0
                ? "copy some of its .wav files into a folder of their own and transcribe that folder."
                : "copy some of its .wav files into a folder of their own, with the matching .mic.wav files of your microphone beside them under the same names, and transcribe that folder; without the .mic.wav files only the class is sent.";
        throw new RecordingTooLongException(
            $"{files.BaseName} holds {Durations.Format(length)} of audio, and ElevenLabs takes at most {RecordingMonitor.DescribeLength(limits.LongestFile)} per file ({ElevenLabsOptions.SectionName}:{nameof(ElevenLabsOptions.LongestFileHours)}), so ClassScribe sends at most {RecordingMonitor.DescribeLength(limits.LongestRecording)}. Nothing was prepared or sent. To transcribe it in pieces, {advice}");
    }

    /// <summary>Makes the smaller upload copy of a WAV file. Returns false when the file should be uploaded as it is.</summary>
    private bool TryPrepare(string path, RecordingFiles files, CancellationToken cancellationToken)
    {
        if (!string.Equals(Path.GetExtension(path), ".wav", StringComparison.OrdinalIgnoreCase)) return false;

        try
        {
            recordings.DeleteUploadCopy(files);
            logger.LogInformation("Preparing {File} for upload; this takes a moment for a long recording", Path.GetFileName(path));
            progress.Report(TranscriptionStep.Preparing);
            preparer.Prepare([path], files.Upload, cancellationToken);
            return true;
        }
        catch (Exception ex) when (ex is NotSupportedException or FormatException or ArgumentException or InvalidOperationException)
        {
            logger.LogWarning("Could not reduce {Path} to 16 kHz mono ({Message}); uploading the original instead", path, ex.Message);
            recordings.DeleteUploadCopy(files);
            return false;
        }
    }

    /// <summary>
    /// Uploads the audio, with the recording's glossary (see <see cref="GlossaryChoice.ForTranscript"/>: the one given to the
    /// command, <paramref name="keyterms"/>, else the one decided when the recording started, which a recording without a
    /// record has decided now, or the run's own, <paramref name="run"/>, when the record gives none), and saves ElevenLabs'
    /// answer and the Markdown transcript, as the next free copy of the
    /// recording's transcript, then adds the transcript's entry to the recording's glossary record. The account's credits are read as the
    /// upload begins, without holding it up, so that the command can say afterwards what the transcript cost (see
    /// <see cref="UsageReport"/>). An answer that has arrived has been paid for: one that cannot be read is kept for later
    /// rather than lost, and the error is marked as following a charged answer (see <see cref="ChargedAnswer"/>), so that
    /// whoever reports it can say a new try is charged again. No transcription ID can be read from such an answer, so the
    /// mark carries none. A YouTube video's transcript is headed by <paramref name="video"/>, its title and link.
    /// </summary>
    private async Task<TranscriptFiles> UploadAndSaveAsync(
        string audioPath, RecordingFiles files, string title, DateTimeOffset? recordedUtc, IReadOnlyList<ClassMark> classMarks, VideoSource? video,
        string? keyterms, GlossaryPick? run, CancellationToken cancellationToken)
    {
        // A cancel made while the upload file was prepared must not go on to "Uploading", which would look as if it had been ignored.
        cancellationToken.ThrowIfCancellationRequested();
        var glossary = glossaries.ForTranscript(files, keyterms, run);

        TranscriptionResult result;
        try
        {
            // The upload begins; the client says how far it gets. The account is read alongside, never waited for here.
            progress.Report(TranscriptionStep.Uploading);
            usage.Begin(cancellationToken);
            result = await client.TranscribeAsync(audioPath, glossary.Terms, cancellationToken);
        }
        catch (TranscriptionException ex) when (ex.ResponseBody is { } answer && IsSuccess(ex.StatusCode))
        {
            // ElevenLabs answered, and charged for it, but the answer could not be read. It is kept, so that what was paid for is not lost.
            ChargedAnswer.Mark(ex, KeepAnswer(files, answer, transcriptionId: null), transcriptionId: null);
            throw;
        }

        if (string.IsNullOrWhiteSpace(result.Text))
            logger.LogWarning("ElevenLabs heard no speech in {File}: the transcript is empty", Path.GetFileName(audioPath));

        var saved = Save(files, result, title, recordedUtc, classMarks, video);
        glossaries.Sent(files, glossary, saved.TranscriptPath);
        return saved;
    }

    /// <summary>
    /// Saves ElevenLabs' answer and the Markdown transcript made from it as the next free copy (see
    /// <see cref="ITranscriptCopies.Save"/>). The answer has been paid
    /// for, so a Ctrl+C that comes now no longer stops this, and it is saved before the transcript is made. An answer
    /// that cannot be saved as the transcript's is kept under a name of its own (see <see cref="KeepAnswer"/>), and
    /// once it is saved, whatever keeps the transcript from being made leaves it as the place to look. Either way the
    /// error is marked as following a charged answer, with where the answer is, if anywhere, and its transcription ID
    /// (see <see cref="ChargedAnswer"/>).
    /// </summary>
    private TranscriptFiles Save(RecordingFiles files, TranscriptionResult result, string title, DateTimeOffset? recordedUtc, IReadOnlyList<ClassMark> classMarks,
        VideoSource? video)
    {
        try
        {
            return copies.Save(files, result.RawJson, () => formatter.Format(result, title, recordedUtc, classMarks, ClassMark.PartsIn(classMarks), video), logger);
        }
        catch (Exception ex) when (ex.Data[ITranscriptCopies.SavedAnswerKey] is string saved)
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
    /// lost, and returns where it went. It goes under a name of its own (<see cref="IRecordingFolder.NextFreeUnreadAnswer"/>),
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
            var target = recordings.NextFreeUnreadAnswer(place);
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
    private void RemoveUploadCopy(RecordingFiles files, bool transcriptSaved)
    {
        var path = files.Upload;
        try
        {
            if (probe.FileExists(path)) recordings.DeleteUploadCopy(files);
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
