using ClassScribe.Audio;

namespace ClassScribe.Storage;

/// <summary>
/// A recording's files on disk (architecture 4.3), as its names give them (see <see cref="RecordingFiles"/>): whether it
/// has a transcript, its segments per track, its camera pieces, its kept answers, the next free name for an unread answer,
/// every file of its own, a file of it opened to read or deleted, whether its upload copy is open elsewhere, and the
/// removal of its upload copy. The naming is the record's; what is there is this port's, so that the use cases reach the
/// recording's folder through it alone.
/// </summary>
public interface IRecordingFolder
{
    /// <summary>
    /// Every file in the recording's folder that <see cref="RecordingFiles.TryParse"/> takes for one of the recording's own,
    /// by its base name, in no particular order: its audio, transcripts, answers, marks, log, source file, pieces and
    /// upload copy alike, never a file beside them. Empty when the folder is not there.
    /// </summary>
    IReadOnlyList<string> FilesOf(RecordingFiles files);

    /// <summary>
    /// Opens the file at <paramref name="path"/> to read it, asynchronously, letting others read and delete it meanwhile, and
    /// write to it too when <paramref name="othersMayWrite"/>: as an upload reads a text file a recorder may still write, or
    /// a segment nobody may write while it is read whole. The file system's own exceptions, so a caller can tell a file
    /// another program holds (a sharing violation) from one that is not there.
    /// </summary>
    Stream OpenToRead(string path, bool othersMayWrite);

    /// <summary>Deletes the file at <paramref name="path"/>; a file another program holds, or one that may not be deleted, is the file system's error.</summary>
    void Delete(string path);

    /// <summary>True when the recording's upload copy (see <see cref="RecordingFiles.Upload"/>) is open in a program now, as while ElevenLabs is sent it; false when it is not, or there is none.</summary>
    bool IsUploadCopyInUse(RecordingFiles files);

    /// <summary>
    /// True when the recording has been transcribed at least once: any copy of its transcript is there, not only the
    /// first, which may have been deleted while a later one was kept.
    /// </summary>
    bool HasTranscript(RecordingFiles files);

    /// <summary>The first <see cref="RecordingFiles.UnreadAnswer"/> name of the recording not taken yet, so that an answer kept earlier is never overwritten.</summary>
    string NextFreeUnreadAnswer(RecordingFiles files);

    /// <summary>
    /// The answers ElevenLabs was paid for that did not become a transcript, kept among the recording's files: every
    /// <see cref="RecordingFiles.UnreadAnswer"/>, and every copy's <see cref="RecordingFiles.Json"/> whose
    /// <see cref="RecordingFiles.Transcript"/> is missing, as when the transcript could not be made. Empty when the folder
    /// holds none, or is not there.
    /// </summary>
    IReadOnlyList<string> FindKeptAnswers(RecordingFiles files);

    /// <summary>
    /// The segment files of <paramref name="track"/> that exist, in recording order: for the main track the .wav files of
    /// the recording's name, and for the microphone's track the .mic.wav files beside them. Neither track takes the
    /// other's files. Empty when there are none, or the folder is not there.
    /// </summary>
    IReadOnlyList<string> FindSegments(RecordingFiles files, Track track = Track.Main);

    /// <summary>
    /// The camera's pieces of the recording that exist, each with where on the clock it begins, in that order: the pieces
    /// of a lesson recorded with the camera, one for each camera process that recorded it. Empty when there are none, or the
    /// folder is not there.
    /// </summary>
    IReadOnlyList<CameraPieceFile> FindCameraPieces(RecordingFiles files);

    /// <summary>
    /// Deletes the recording's upload copy (see <see cref="RecordingFiles.Upload"/>), the 16 kHz copy made for a transcript,
    /// when it is there; nothing happens when it is not. A copy another program holds open, or a folder that is gone, is the
    /// file system's error, which goes to the caller.
    /// </summary>
    void DeleteUploadCopy(RecordingFiles files);
}
