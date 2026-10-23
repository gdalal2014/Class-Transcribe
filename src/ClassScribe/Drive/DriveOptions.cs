namespace ClassScribe.Drive;

/// <summary>What of a recording goes up to Google Drive (Drive:Upload).</summary>
public enum DriveUploadScope
{
    /// <summary>Everything of the recording: the audio of both tracks, the transcripts, ElevenLabs' answers, the marks and the log.</summary>
    Everything,

    /// <summary>All of it but the audio: the .wav files stay on this laptop alone.</summary>
    WithoutAudio
}

/// <summary>
/// Settings for Google Drive: the folder in My Drive that ClassScribe makes for the classes, what goes up and in pieces of
/// what size, and Google's addresses, which a dry run may point at a local stub. None of them holds a secret: the Google
/// client and the sign-in are kept, encrypted for the Windows account, in %APPDATA%\ClassScribe (see
/// <see cref="IGoogleCredentialStore"/>), never in the settings.
/// </summary>
public sealed class DriveOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Drive";

    /// <summary>The longest name <see cref="FolderName"/> may have.</summary>
    public const int LongestFolderName = 100;

    /// <summary>The smallest piece <see cref="ChunkMegabytes"/> allows, in MiB.</summary>
    public const int SmallestChunkMegabytes = 1;

    /// <summary>The largest piece <see cref="ChunkMegabytes"/> allows, in MiB.</summary>
    public const int LargestChunkMegabytes = 256;

    /// <summary>
    /// True to upload each class by itself once Drive is set up: right after its transcript (record-only: once its files are
    /// closed), and the recordings that wait in the queue with it; and after a transcript made later, its new files. Each
    /// recording goes into the queue as its run starts. False leaves the uploads to menu choice 9 and drive upload alone,
    /// and puts nothing in the queue by itself.
    /// </summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// The name of the folder ClassScribe makes in My Drive, the first time, to hold the classes in the recordings folder's
    /// layout: a folder per course, and in it one per class's date folder. ClassScribe
    /// finds its folder again by a mark of its own, so renaming or moving it in Drive later is fine, and changing this
    /// setting then changes nothing. At most 100 characters, without control characters.
    /// </summary>
    public string FolderName { get; init; } = "ClassScribe";

    /// <summary>
    /// What of a recording goes up: <see cref="DriveUploadScope.Everything"/>, or <see cref="DriveUploadScope.WithoutAudio"/>,
    /// which leaves the .wav files out. The temporary 16 kHz upload copy never goes up.
    /// </summary>
    public DriveUploadScope Upload { get; init; } = DriveUploadScope.Everything;

    /// <summary>
    /// True to send a lesson's finished video (<c>&lt;base&gt;.mp4</c>) to Drive with the rest of its recording, last, while
    /// <see cref="Upload"/> sends audio, since the video holds the same sound; its camera's pieces then stay on this laptop
    /// until free-space finds the video safe on Drive. False, the default (Gulzar's answer N19 C: a teacher's free 15 GB fills
    /// in about six hours of 720p), keeps lesson videos on this laptop, and their pieces are deleted once the video passes its
    /// check. A new key rather than a new value of <see cref="Upload"/>, which an older build would refuse (amendment 5).
    /// </summary>
    public bool LessonVideos { get; init; }

    /// <summary>
    /// The size of each piece sent to Drive, in MiB, from 1 to 256: a piece lost to a dropped connection costs at most this
    /// much, and memory stays flat whatever the size of the file. Always a multiple of the 256 KiB Drive asks for.
    /// </summary>
    public int ChunkMegabytes { get; init; } = 8;

    /// <summary>
    /// The base address of Google's Drive API. An https address, or http to 127.0.0.1 or localhost only, for a local stub in
    /// a dry run, as ElevenLabs:BaseUrl is.
    /// </summary>
    public string ApiBaseUrl { get; init; } = "https://www.googleapis.com/";

    /// <summary>The base address of Google's token and revoke endpoints; the same rule as <see cref="ApiBaseUrl"/>.</summary>
    public string OAuthBaseUrl { get; init; } = "https://oauth2.googleapis.com/";
}
