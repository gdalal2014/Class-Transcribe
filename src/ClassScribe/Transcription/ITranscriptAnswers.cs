namespace ClassScribe.Transcription;

/// <summary>
/// ElevenLabs' answers kept beside a recording (architecture 4.3): one saved earlier read back for a rebuild, which lays
/// it out again without sending anything, and one paid for kept under a new name when it could not become a transcript.
/// The reader of the service's JSON lives in Infrastructure and is the one the client uses too, so a rebuilt transcript
/// starts from just what the upload gave.
/// </summary>
public interface ITranscriptAnswers
{
    /// <summary>
    /// Reads the answer saved at <paramref name="path"/>: the fields this app uses, with the JSON exactly as it is. A
    /// saved answer came with a success status.
    /// </summary>
    /// <exception cref="System.IO.FileNotFoundException">There is no file at <paramref name="path"/>.</exception>
    /// <exception cref="TranscriptionException">The answer is not JSON of the form ElevenLabs sends, or holds no transcript.</exception>
    Task<TranscriptionResult> ReadAsync(string path, CancellationToken cancellationToken);

    /// <summary>
    /// Writes <paramref name="json"/>, an answer as it arrived, to a new file at <paramref name="path"/>, in UTF-8 without a
    /// byte order mark, never over a file that is there. The file system's failure goes to the caller, which tries the next
    /// place.
    /// </summary>
    void WriteNew(string path, string json);
}
