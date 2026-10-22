using ClassScribe.Recorder;

namespace ClassScribe.YouTube;

/// <summary>
/// The YouTube feature's folders, every one derived from ClassScribe's own folder (design 2.12), never read from a setting,
/// so that a test, which gives a folder of its own, never reaches the real ones:
/// <code>
/// &lt;RuntimeFolder&gt;\youtube\youtube.lock         one batch at a time
/// &lt;RuntimeFolder&gt;\youtube\converting.json      the conversion in progress, for the clean-up after a crash
/// &lt;RuntimeFolder&gt;\youtube\staging\&lt;video ID&gt;\  a video's read and download until it is a recording
/// &lt;RuntimeFolder&gt;\youtube\yt-dlp-cache\        yt-dlp's cache
/// &lt;RuntimeFolder&gt;\video-browser\               the profile of ClassScribe's own Chrome window (the record path)
/// </code>
/// The video window's profile may hold Google sign-in cookies, once Gulzar signs in there for YouTube Premium: it is a
/// secret, and nothing of ClassScribe reads, lists, copies, uploads or logs anything in it, nor hands it to yt-dlp.
/// </summary>
/// <param name="runtime">ClassScribe's own folder.</param>
public sealed class YouTubeFolders(RuntimeFolder runtime)
{
    /// <summary>The name of the feature's folder in ClassScribe's own folder.</summary>
    public const string FolderName = "youtube";

    /// <summary>The name of the video window's profile folder in ClassScribe's own folder.</summary>
    public const string VideoBrowserName = "video-browser";

    /// <summary>The feature's folder.</summary>
    public string Root => Path.Combine(runtime.Root, FolderName);

    /// <summary>The lock that allows one batch at a time (see <see cref="YouTubeLock"/>).</summary>
    public string LockFile => Path.Combine(Root, "youtube.lock");

    /// <summary>The note of the conversion in progress, which names its unfinished files (see <see cref="DownloadedAudio"/>).</summary>
    public string ConvertingNote => Path.Combine(Root, "converting.json");

    /// <summary>The staging folder, which holds each video's read and download until it is a recording.</summary>
    public string Staging => Path.Combine(Root, "staging");

    /// <summary>yt-dlp's cache folder (--cache-dir): YouTube's player code and the challenges it solved.</summary>
    public string Cache => Path.Combine(Root, "yt-dlp-cache");

    /// <summary>
    /// The profile folder of ClassScribe's own Chrome window. It may hold Google sign-in cookies, and is a secret: it is named
    /// here only so that nothing else of the feature is ever put in it, and so that yt-dlp is never given it.
    /// </summary>
    public string VideoBrowser => Path.Combine(runtime.Root, VideoBrowserName);

    /// <summary>The staging folder of video <paramref name="videoId"/>. Anything but a video's ID is refused before it names a folder.</summary>
    public string StagingOf(string videoId) =>
        YouTubeLinks.IsVideoId(videoId)
            ? Path.Combine(Staging, videoId)
            : throw new ArgumentException($"\"{videoId}\" is not a YouTube video's ID.", nameof(videoId));
}
