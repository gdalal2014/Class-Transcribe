using ClassScribe.Transcription;
using Microsoft.Extensions.Logging;

namespace ClassScribe.Storage;

/// <summary>
/// The numbered copies of a recording's transcript, the .md of each copy, and of ElevenLabs' answer, the .json beside it
/// (architecture 4.3; see <see cref="RecordingFiles.Transcript"/> and <see cref="RecordingFiles.Json"/>). A new copy takes
/// the first number that has neither file, and claims each file by creating it only when no file of that name is there, so
/// nothing is ever overwritten, not even by another ClassScribe saving a transcript of the same recording at the same
/// moment: whichever comes second takes the next number. Both files are written in UTF-8 without a byte order mark. Each
/// save takes the caller's logger, so that what it says of a copy taken meanwhile keeps the caller's category.
/// </summary>
public interface ITranscriptCopies
{
    /// <summary>
    /// The <see cref="Exception.Data"/> key under which a failure of <see cref="Save"/> gives the path of ElevenLabs' answer,
    /// when the answer was saved before the failure, so that whoever reports it can point there.
    /// </summary>
    const string SavedAnswerKey = "ClassScribe.SavedAnswer";

    /// <summary>
    /// Saves ElevenLabs' answer <paramref name="json"/>, and the transcript <paramref name="transcript"/> makes from it,
    /// as the next copy of the recording's transcript, and returns where they went. The answer is saved first, since it
    /// has been paid for, and the transcript is made only then, once, so that an answer whose transcript cannot be made is
    /// on disk all the same. When the transcript's name is taken at that moment, by another ClassScribe, the answer moves
    /// on with the transcript to the next free copy. A failure that comes after the answer was saved carries the answer's
    /// path in its <see cref="Exception.Data"/> under <see cref="SavedAnswerKey"/>.
    /// </summary>
    TranscriptFiles Save(RecordingFiles files, string json, Func<string> transcript, ILogger logger);

    /// <summary>
    /// Saves a transcript made again from a saved answer, without an answer of its own, as the first copy that has neither
    /// a transcript nor an answer, and returns its path. An answer kept without its transcript, or a transcript made by
    /// hand, keeps its number to itself.
    /// </summary>
    string SaveRebuilt(RecordingFiles files, string markdown, ILogger logger);

    /// <summary>
    /// ElevenLabs' answer of the recording's newest transcript: the .json of the highest copy that has one, counted as a
    /// number, or null when there is none. An answer kept as unread (<see cref="RecordingFiles.UnreadAnswer"/>) is never
    /// it: such an answer was set aside because it could not be read, or could not be saved in its place.
    /// </summary>
    string? NewestAnswer(RecordingFiles files);

    /// <summary>
    /// The recording's transcripts on disk: the .md of every copy that has one, in the order of their numbers, made when
    /// the recording was transcribed or rebuilt later. Empty when its folder holds none, or is not there.
    /// </summary>
    IReadOnlyList<string> Transcripts(RecordingFiles files);

    /// <summary>
    /// The line under the title of the transcript at <paramref name="transcriptPath"/>, blank lines passed over, where the
    /// formatter says when its recording was made; null when the transcript ends before one. It is read while another
    /// ClassScribe may write it.
    /// </summary>
    string? LineUnderTitle(string transcriptPath);
}
