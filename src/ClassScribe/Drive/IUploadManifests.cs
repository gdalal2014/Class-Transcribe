using ClassScribe.Storage;

namespace ClassScribe.Drive;

/// <summary>
/// The upload records (architecture 4.3): one manifest per recording (see <see cref="UploadManifest"/>), each written whole
/// or not at all; the queue is the manifests that wait. One for the process.
/// </summary>
public interface IUploadManifests
{
    /// <summary>The folder of the manifests.</summary>
    string Folder { get; }

    /// <summary>The manifest file of <paramref name="files"/>.</summary>
    string PathOf(RecordingFiles files);

    /// <summary>
    /// The manifest of <paramref name="files"/>; null when there is none. One that cannot be read counts as none, with a
    /// warning: Drive's own files are then found again by name and MD5, so nothing goes up twice.
    /// </summary>
    UploadManifest? Read(RecordingFiles files);

    /// <summary>
    /// Puts <paramref name="files"/> in the queue, as waiting, and returns its manifest: a new one, queued now, or the one
    /// there, which keeps what it knows of each file. Each queue is counted (<see cref="UploadManifest.TimesQueued"/>), so
    /// that an upload going on meanwhile in another process takes the recording once more rather than leave it done; and no
    /// upload is taken to be going on from the queue alone (see <see cref="UploadManifest.IsBeingUploaded"/>). Written at
    /// once, so a crash or a restart keeps it in the queue.
    /// </summary>
    UploadManifest Queue(RecordingFiles files);

    /// <summary>
    /// Keeps <paramref name="files"/> in the queue, as waiting, or puts it there, without counting a queue of its own: what an
    /// upload notes of a recording it cannot take now, as one being recorded or one on a drive that is out, with
    /// <paramref name="problem"/>, the reason, and the time of the try, when one is given. Returns the manifest written.
    /// </summary>
    UploadManifest KeepWaiting(RecordingFiles files, string? problem = null);

    /// <summary>Every manifest that waits, the one queued first first.</summary>
    IReadOnlyList<UploadManifest> Waiting();

    /// <summary>Every manifest there is, in no particular order; those that cannot be read are left out, with a warning.</summary>
    IReadOnlyList<UploadManifest> All();

    /// <summary>
    /// Writes <paramref name="manifest"/> whole, through a temporary file in the same folder, made if needed; a file another
    /// program holds for a moment is tried again. A write that still fails is logged and thrown: an upload that cannot keep
    /// track of itself must not go on.
    /// </summary>
    void Write(UploadManifest manifest);

    /// <summary>
    /// How many files of <paramref name="files"/> are on this laptop and how large they are together, the upload copy, the
    /// camera's pieces and a lesson video's .partial left out, as a waiting recording is described: none of them ever goes
    /// to Drive; and the lesson's finished video too unless <paramref name="withLessonVideo"/>, as Drive:LessonVideos says.
    /// None when its folder is not there.
    /// </summary>
    (int Count, long Bytes) OnDisk(RecordingFiles files, bool withLessonVideo = false);
}
