using ClassScribe.Audio;

namespace ClassScribe.Storage;

/// <summary>
/// The recordings' library (architecture 4.3): where recordings and transcripts go, under Recording:OutputFolder or the
/// recordings folder given for one run, a folder per name, the course, and in it a date folder per class; the recordings in
/// a folder, in that layout or flat in the folder of their name as recordings were kept before; what a new run may
/// continue; a recording's length by its longest track, and a track's or a segment's; and what a recording's files say of
/// it. It is the one way the use cases reach the recordings' folders and files; the class folders of Storage implement it.
/// </summary>
public interface IClassFolders
{
    /// <summary>
    /// The folder that holds every recording folder: Recording:OutputFolder with environment variables such as %USERPROFILE%
    /// expanded and any surrounding quotes removed. A relative setting is taken from the user profile, so it means the same
    /// folder however ClassScribe was started; an empty one means a ClassScribe folder there.
    /// </summary>
    string Root { get; }

    /// <summary>
    /// The folder that holds every recording folder when Recording:OutputFolder is <paramref name="outputFolder"/>:
    /// environment variables such as %USERPROFILE% expanded and any surrounding quotes removed, a relative path taken from
    /// the user profile, and an empty one meaning a ClassScribe folder there.
    /// </summary>
    string RootFor(string? outputFolder);

    /// <summary>
    /// The files for a recording named <paramref name="name"/> starting now: the folder of that name, the course, under
    /// <paramref name="root"/>, the recordings folder given for the run (--output), or under the root when that is null; in
    /// it the date folder of the local date now, or the first of its " (2)", " (3)" and so on that holds no recording yet;
    /// and files stamped with the UTC time. Both folders are created if needed. A recording started within the same second
    /// as another of the course takes the next second, so that no two share a base name.
    /// </summary>
    RecordingFiles CreateRecording(string name, string? root = null);

    /// <summary>
    /// The files for a recording named <paramref name="name"/> starting now in <paramref name="folder"/>, which a batch of
    /// YouTube videos shares: the batch's first video takes its date folder as a class does (see <see cref="CreateRecording"/>),
    /// and every later one goes into that same folder, made if it is gone. A recording started within the same second as
    /// another of the course takes the next second; the course is the date folder's parent, or the folder itself when it is
    /// no date folder.
    /// </summary>
    RecordingFiles CreateRecordingIn(string folder, string name);

    /// <summary>
    /// The files for the transcript of <paramref name="sourcePath"/>, laid out as a recording's (see <see cref="CreateRecording"/>):
    /// a folder named after the file, under <paramref name="root"/>, the recordings folder given for the run, or under the
    /// root when that is null, the date folder of the transcription in it, and files stamped with the time now. A file with
    /// nothing usable in its name, such as ".mp3", gets an "Untitled" folder.
    /// </summary>
    RecordingFiles CreateForFile(string sourcePath, string? root = null);

    /// <summary>
    /// The files of the recording <paramref name="baseName"/> in <paramref name="folder"/>, for a run that continues it as
    /// its next part: the run's segments take the part numbers after the recording's own, and its marks and notes go into
    /// the same file.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="baseName"/> is no recording's base name: one with a folder in it, or without a time stamp.</exception>
    /// <exception cref="System.IO.FileNotFoundException">None of the recording's audio is in <paramref name="folder"/> any more, so there is nothing to continue.</exception>
    RecordingFiles Open(string folder, string baseName);

    /// <summary>
    /// The files of the recording <paramref name="baseName"/> of <paramref name="name"/>, under the recordings folder
    /// <paramref name="root"/>, for a run that continues it as its next part (see <see cref="Open"/>), in whichever layout
    /// it was made: flat in the folder of its name, or in one of that folder's date folders, " (2)" included, whatever the
    /// date is now. The folder of the name is found in any letter case, as Windows takes such names for one.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="baseName"/> is no recording's base name: one with a folder in it, or without a time stamp.</exception>
    /// <exception cref="System.IO.FileNotFoundException">None of the recording's audio is under the folder of its name any more, so there is nothing to continue.</exception>
    RecordingFiles OpenToContinue(string root, string name, string baseName);

    /// <summary>
    /// The recording a new run of <paramref name="name"/> may continue as its next part, or null when there is none: only
    /// the newest recording of the name under <paramref name="root"/> (the recordings folder given for the run, or the root
    /// when that is null), in either layout, and never a YouTube video's (see <see cref="IsVideo"/>). It is offered only
    /// when it has no transcript and no answer ElevenLabs was paid for is kept for it; it has the same tracks as a run of
    /// <paramref name="mode"/>; a lesson with the camera's pieces is offered only to a run with the camera
    /// (<paramref name="camera"/>); it is not being written and has no upload copy; its audio was last written no more
    /// than <paramref name="within"/> ago (zero never offers one); and it is shorter than the longest recording sent in one
    /// file. The run files can tell what the files cannot, for the caller to check.
    /// </summary>
    ContinueCandidate? FindContinuable(string name, RecordingMode mode, TimeSpan within, string? root = null, bool camera = false);

    /// <summary>
    /// The length of the recording on disk: that of its longest track, each track's segments read to the end of their
    /// files as the preparer reads them, so that it is exact even after a crash left a header behind. A run that continues
    /// the recording starts there.
    /// </summary>
    TimeSpan LengthOnDisk(RecordingFiles files);

    /// <summary>
    /// The length of one track of the recording on disk: its segments, in recording order, each read to the end of its
    /// file as the preparer reads it (see <see cref="MeasureSegment"/>); zero when the track has none.
    /// </summary>
    TimeSpan TrackLength(RecordingFiles files, Track track);

    /// <summary>
    /// Every recording under <paramref name="root"/> (the recordings folder given for the listing, or the root when that
    /// is null), newest first by the time each started, for the list command. One bad item does not hide the rest (see
    /// <see cref="ListRecent"/>); a root that is not there holds none.
    /// </summary>
    IReadOnlyList<RecordedClass> ListAll(string? root = null);

    /// <summary>
    /// Says what keeps recordings from going to the root, in words fit to show before a recording starts, or returns null
    /// when nothing does: an environment variable in Recording:OutputFolder that is not set, a drive that is not there, or a
    /// file where the folder should be. A root that does not exist yet is no problem; the first recording makes it.
    /// </summary>
    string? DescribeRootProblem();

    /// <summary>
    /// Lists the recordings under the root, the most recently recorded first. One bad item does not hide the rest: a folder
    /// ClassScribe may not open is passed over, and a folder or recording that cannot be read for another reason is left out
    /// with a warning. A root that cannot be read is still an error, because the setting needs fixing.
    /// </summary>
    IReadOnlyList<RecordedClass> ListRecent(int count);

    /// <summary>
    /// The recording started last in a folder, or null when the folder holds none. The recordings of a folder are those in
    /// it and in its date folders (see <see cref="RecordingFoldersIn"/>): a course's folder stands for its recordings in
    /// both layouts, a date folder for its own. The folders are read strictly, so one that cannot be read is an error.
    /// </summary>
    RecordingFiles? FindNewest(string folder);

    /// <summary>
    /// The recordings in <paramref name="folder"/> and its date folders, such as a course's or one class's, newest first by
    /// the time each started, so that the first is the one <see cref="FindNewest"/> finds, each described as the menu's
    /// list describes it. The folders are read strictly, so one that cannot be read is an error; a recording in them that
    /// cannot be read now is left out with a warning, as in the list.
    /// </summary>
    IReadOnlyList<RecordedClass> RecordingsIn(string folder);

    /// <summary>
    /// The folders whose recordings <paramref name="folder"/> stands for: itself, which holds the recordings of the old
    /// layout flat in the folder of their name, and then its date folders, by name (see <see cref="FolderNames.IsDateFolder"/>).
    /// Its other folders, such as one of slides, are passed over. The folder is read strictly, so one that cannot be read
    /// is an error.
    /// </summary>
    IReadOnlyList<string> RecordingFoldersIn(string folder);

    /// <summary>The files in <paramref name="folder"/>, itself alone, read strictly: a folder that cannot be read is an error.</summary>
    IReadOnlyList<string> FilesIn(string folder);

    /// <summary>The folders in <paramref name="folder"/>, itself alone, read strictly: a folder that cannot be read is an error.</summary>
    IReadOnlyList<string> FoldersIn(string folder);

    /// <summary>
    /// The folders that hold the copy of a recording kept in <paramref name="recordingFolder"/> under ClassScribe's own
    /// folder in Google Drive, from the top down, so that Drive mirrors the laptop: for a date folder its course's name and
    /// its own, " (2)" included, as Mantiq and 2026-10-17; for any other folder, as one of the old layout, its own name
    /// alone, as Kalam. Only the folder's own name and its parent's count, whatever the recordings folder is.
    /// </summary>
    IReadOnlyList<string> DriveLevelsOf(string recordingFolder);

    /// <summary>
    /// <paramref name="folder"/>'s full path with each folder's name written as it is on disk, as Mantiq when it was typed
    /// mantiq, so that Drive's folders are named as the laptop's are, whatever was typed. A folder that is not there, or
    /// that cannot be looked into, keeps the name as given.
    /// </summary>
    string AsOnDisk(string folder);

    /// <summary>
    /// Every recording in <paramref name="folder"/> and its date folders (see <see cref="RecordingFoldersIn"/>), known by any
    /// of its files (see <see cref="RecordingFiles.TryParse"/>), so that one whose audio is on Google Drive alone, or a file
    /// transcribed on its own, counts too; newest first by the time each started. Files that are not a recording's, such as a
    /// class's PDFs, are passed over. The folders are read strictly, so one that cannot be read is an error.
    /// </summary>
    IReadOnlyList<RecordingFiles> RecordingsByAnyFileIn(string folder);

    /// <summary>The audio of <paramref name="files"/> that is on Google Drive alone, checked, and gone from this laptop; null when none of it is.</summary>
    AudioOnDrive? AudioOnDriveOf(RecordingFiles files);

    /// <summary>
    /// The recording started last in <paramref name="folder"/> and its date folders whose audio is all on Google Drive alone,
    /// with that audio; null when there is none. Transcribing the folder would otherwise pass it over for an older one.
    /// </summary>
    (RecordingFiles Files, AudioOnDrive Audio)? NewestWithAudioOnDriveIn(string folder);

    /// <summary>
    /// Describes one recording as the list describes it (see <see cref="ListRecent"/>), such as one given by a path: its
    /// length, whether it has a transcript, an answer paid for and kept without one, and whether it is being recorded now.
    /// Its files are read strictly, so one that cannot be read now is an error, rather than left out as in the list.
    /// </summary>
    RecordedClass Describe(RecordingFiles files);

    /// <summary>
    /// The newest answer ElevenLabs was paid for that did not become a transcript of the recording, kept beside it or, when
    /// its own folder would not take it, in the recordings folder; null when there is none.
    /// </summary>
    string? KeptAnswerOf(RecordingFiles files);

    /// <summary>
    /// True when <paramref name="files"/> is a YouTube video's recording: it has a source file (see
    /// <see cref="RecordingFiles.Source"/>). The class machinery passes such a recording over: it is never offered to be
    /// continued, and never shown as a class.
    /// </summary>
    bool IsVideo(RecordingFiles files);

    /// <summary>
    /// True while the recording is being recorded, in this window or another: a recorder still writes the newest segment
    /// of one of its tracks, or a camera process its newest piece or the lesson's video (see
    /// <see cref="IsLessonVideoBeingMade"/>). Each holds that file open for writing and lets others read it. A file that
    /// does not open for reading at all is held by another program, such as a scanner, not by a recorder: that does not
    /// count, and transcribing the recording reports the real reason.
    /// </summary>
    bool IsBeingWritten(RecordingFiles files);

    /// <summary>
    /// True while a camera process makes the lesson's video: it holds the video's <see cref="RecordingFiles.LessonVideoPartial"/>
    /// open for writing, others allowed to read it. A <c>.partial</c> a crash left, which nothing holds, does not count.
    /// </summary>
    bool IsLessonVideoBeingMade(RecordingFiles files);

    /// <summary>
    /// What the files of <paramref name="files"/> say of the lesson's finished video (see <see cref="LessonVideoState"/>): its
    /// camera's pieces, the newest left out while a camera process still writes it, against its finished video. Made when
    /// the video is there and no piece is newer; not made when pieces are there without it, or one is newer; none without
    /// either. A video's <c>.partial</c> is no video.
    /// </summary>
    LessonVideoState LessonVideoStateOf(RecordingFiles files);

    /// <summary>
    /// The length of a segment, worked out from its size the way the preparer reads it: to the end of the file, whatever
    /// the header says. A file the recorder did not write counts as empty.
    /// </summary>
    TimeSpan MeasureSegment(string path);
}
