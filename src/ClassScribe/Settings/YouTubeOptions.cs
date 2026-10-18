using System.Text.RegularExpressions;
using ClassScribe.Drive;

namespace ClassScribe.Settings;

/// <summary>
/// Settings for YouTube videos (the youtube command and menu choice Y): where yt-dlp is, which channels' videos Gulzar may
/// download and which are his course's, never downloaded, and what of a video's recording goes to Google Drive. None of them
/// holds a secret.
/// </summary>
public sealed partial class YouTubeOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "YouTube";

    /// <summary>
    /// yt-dlp's yt-dlp.exe, as a full path; empty finds it in winget's links folder or on PATH. Deno and FFmpeg are looked for
    /// beside it first.
    /// </summary>
    public string YtDlpPath { get; init; } = "";

    /// <summary>
    /// The channels whose videos Gulzar may download: his own, and those whose owners allow it, each as its UC id or its
    /// @handle. A video of one of them is downloaded under the batch's confirmation, unlisted or not.
    /// </summary>
    public string[] MayDownloadChannels { get; init; } = [];

    /// <summary>
    /// His course's channels, each as its UC id or its @handle: never downloaded, and --allow-unlisted is refused while this
    /// is empty, so that a course's video is never taken for one he may download.
    /// </summary>
    public string[] CourseChannels { get; init; } = [];

    /// <summary>What of a video's recording goes to Google Drive: everything, as for a class, or all but its audio.</summary>
    public DriveUploadScope DriveUpload { get; init; } = DriveUploadScope.Everything;

    /// <summary>
    /// True when <paramref name="entry"/> is a channel as these settings name them: UC and 22 of A-Z, a-z, 0-9, _ and -, or an
    /// @handle, @ and 3 to 30 letters, digits, _, - or dots.
    /// </summary>
    public static bool IsChannel(string? entry) => entry is not null && ChannelShape().IsMatch(entry);

    /// <summary>A path setting as it is used: environment variables such as %LOCALAPPDATA% expanded, and the quotes and spaces around it gone.</summary>
    public static string Expanded(string? path) => Environment.ExpandEnvironmentVariables(path ?? "").Trim().Trim('"').Trim();

    /// <summary>A channel's UC id or @handle, and nothing else; \z ends the match at the very end.</summary>
    [GeneratedRegex(@"^(?:UC[A-Za-z0-9_-]{22}|@[\p{L}\p{N}_.-]{3,30})\z", RegexOptions.CultureInvariant)]
    private static partial Regex ChannelShape();
}
