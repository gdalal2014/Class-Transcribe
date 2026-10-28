using ClassScribe.YouTube;

namespace ClassScribe.Storage;

/// <summary>A recording, as the menu and the list command list it.</summary>
/// <param name="Files">The recording's files.</param>
/// <param name="LastRecordedUtc">When audio was last written to it, on either track.</param>
/// <param name="Length">Length of the recording: that of its longest track, every segment read to its end (see <c>IClassFolders.LengthOnDisk</c>).</param>
/// <param name="HasTranscript">True when it has been transcribed at least once.</param>
/// <param name="KeptAnswer">
/// The newest answer ElevenLabs was paid for that did not become a transcript (see
/// <see cref="RecordingFiles.FindKeptAnswers"/>), kept beside the recording or in the recordings folder; null when there is none.
/// </param>
/// <param name="BeingWritten">
/// True while it is being recorded, in this window or another: a recorder still writes the newest segment of one of its
/// tracks (see <c>IClassFolders.IsBeingWritten</c>). It is not transcribed until it has stopped.
/// </param>
/// <param name="Parts">
/// How many parts it was made in: one, and one more for each time it was taken up again after it stopped, as its resume
/// notes say (see <see cref="ClassMarks.PartsOf"/>).
/// </param>
/// <param name="HasMicrophone">True when your microphone was recorded with the class, on a track of its own: the .mic.wav files.</param>
/// <param name="OnDrive">
/// The recording's audio when it is on Google Drive alone, checked, and none of it is left on this laptop (see
/// <see cref="IAudioOnDrive"/>): its transcripts can be read and rebuilt, and transcribing it again needs the audio back.
/// Null while its audio is on this laptop.
/// </param>
/// <param name="Video">
/// The YouTube video it is the recording of, from its source file (see <see cref="RecordingFiles.Source"/>); null for a
/// class's recording, and for a video's whose source file cannot be read.
/// </param>
/// <param name="LessonVideo">What its files say of a lesson's finished video (see <c>IClassFolders.LessonVideoStateOf</c>): none for a recording without the camera.</param>
public sealed record RecordedClass(RecordingFiles Files, DateTime LastRecordedUtc, TimeSpan Length, bool HasTranscript, string? KeptAnswer, bool BeingWritten,
    int Parts = 1, bool HasMicrophone = false, AudioOnDrive? OnDrive = null, VideoSource? Video = null, LessonVideoState LessonVideo = LessonVideoState.None);

/// <summary>
/// What a recording's files say of a lesson's finished video (teach-video 6.9 and 6.15): decided from the files alone, so that
/// one rule covers a crash, a session Windows ended, a cancel, a time limit, a failure and a record-only lesson. A camera's
/// piece or a video's <c>.partial</c> held open for writing is never counted.
/// </summary>
public enum LessonVideoState
{
    /// <summary>No camera's piece and no finished video: a recording without the camera.</summary>
    None,

    /// <summary>The finished video is there, and no piece is newer than it.</summary>
    Made,

    /// <summary>Pieces and no finished video, or a piece newer than the video, as a continued lesson's: its video is still to be made.</summary>
    NotMade
}

/// <summary>
/// A recording that a new run of the same name may continue as its next part, because it stopped recently with neither a
/// transcript nor an answer from ElevenLabs paid for, as after a crash, a closed window or a restart (see
/// <c>IClassFolders.FindContinuable</c>).
/// </summary>
/// <param name="Files">The recording's files, which the run that continues it writes into.</param>
/// <param name="Length">Its length on disk (see <c>IClassFolders.LengthOnDisk</c>): where the run that continues it starts.</param>
/// <param name="StoppedUtc">When its audio was last written, on either track.</param>
/// <param name="Parts">How many parts it was made in so far (see <see cref="ClassMarks.PartsOf"/>).</param>
public sealed record ContinueCandidate(RecordingFiles Files, TimeSpan Length, DateTimeOffset StoppedUtc, int Parts);
