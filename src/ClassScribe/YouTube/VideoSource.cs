using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClassScribe.YouTube;

/// <summary>The playlist a video was taken from, and its place in it.</summary>
/// <param name="Id">The playlist's ID.</param>
/// <param name="Title">The playlist's title, when yt-dlp gave one.</param>
/// <param name="Position">The video's place in the playlist, from 1.</param>
public sealed record VideoPlaylist(string Id, string? Title, int Position);

/// <summary>
/// A video's source file, &lt;base&gt;.source.json beside its recording (design 2.3): what the video is, where it came from,
/// how its audio was obtained and on whose word, and how the recording ended. A recording with one is a video's, which the
/// class machinery never takes for a class (see <see cref="Storage.ClassFolders.IsVideo"/>), and whose transcript is headed
/// by the video's title and link.
/// </summary>
/// <param name="Kind">Always <see cref="YouTubeKind"/>.</param>
/// <param name="Url">The video's link, rebuilt from its ID.</param>
/// <param name="VideoId">The video's ID.</param>
/// <param name="Title">Its title, or its ID when YouTube gave none.</param>
/// <param name="Channel">Its channel's name, when known.</param>
/// <param name="ChannelHandle">Its channel's @handle, when known.</param>
/// <param name="ChannelId">Its channel's UC id, when known.</param>
/// <param name="UploadDate">When it was uploaded, as yyyy-MM-dd, when known.</param>
/// <param name="Duration">Its length as hh:mm:ss, when known.</param>
/// <param name="Availability">public, unlisted and so on, as yt-dlp says it; null when not known.</param>
/// <param name="Playlist">The playlist it was taken from, if any.</param>
/// <param name="Obtained"><see cref="Downloaded"/> or <see cref="Recorded"/>.</param>
/// <param name="Tool">What obtained it: "yt-dlp 2026.08.19", or Chrome's version.</param>
/// <param name="ObtainedUtc">When it was obtained.</param>
/// <param name="Confirmed">On whose word it was downloaded, in words, with the time; null for a recording.</param>
/// <param name="AcceptedBy">What accepted it for downloading: <see cref="ByYes"/>, <see cref="ByWord"/>, <see cref="ByAllowUnlisted"/> or <see cref="ByChannel"/>; null for a recording.</param>
/// <param name="Ended">How the recording ended, in words; "Downloaded" for a download.</param>
/// <param name="EndConfirmed">True when the recording holds the whole video: always for a download.</param>
public sealed record VideoSource(
    string Kind,
    string Url,
    string VideoId,
    string Title,
    string? Channel,
    string? ChannelHandle,
    string? ChannelId,
    string? UploadDate,
    string? Duration,
    string? Availability,
    VideoPlaylist? Playlist,
    string Obtained,
    string? Tool,
    DateTimeOffset ObtainedUtc,
    string? Confirmed,
    string? AcceptedBy,
    string? Ended,
    bool EndConfirmed)
{
    /// <summary>The kind every source file names.</summary>
    public const string YouTubeKind = "youtube";

    /// <summary>The audio was downloaded with yt-dlp.</summary>
    public const string Downloaded = "downloaded";

    /// <summary>The audio was recorded as the video played.</summary>
    public const string Recorded = "recorded";

    /// <summary>A public video, taken under the batch's YES (or --yes on the command line).</summary>
    public const string ByYes = "yes";

    /// <summary>A video Gulzar gave his word for, in the menu: he uploaded it, or its owner allows him to download it.</summary>
    public const string ByWord = "word";

    /// <summary>An unlisted video accepted by --allow-unlisted on the command line, on his word for the whole batch.</summary>
    public const string ByAllowUnlisted = "allow-unlisted";

    /// <summary>A video of a channel in YouTube:MayDownloadChannels.</summary>
    public const string ByChannel = "may-download-channel";

    /// <summary>What <see cref="Ended"/> says of a download, whose whole length is always there.</summary>
    public const string DownloadedEnd = "Downloaded";
}

/// <summary>
/// JSON for a video's source file, generated at compile time: camelCase names, written indented and with every character as
/// it is (an Arabic title stays readable), and a field the reader does not know passed over.
/// </summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(VideoSource))]
[JsonSerializable(typeof(ConversionNote))]
public sealed partial class YouTubeJson : JsonSerializerContext
{
    /// <summary>The context with the options the source file is written with.</summary>
    public static YouTubeJson Files { get; } = new(new JsonSerializerOptions(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    });
}

/// <summary>Reads and writes a video's source file (see <see cref="VideoSource"/>).</summary>
public static class VideoSourceFile
{
    /// <summary>
    /// Writes <paramref name="source"/> whole as the file at <paramref name="path"/>: into a file of its own in the same folder,
    /// flushed to the disk, then moved into its place in one step, so that a reader finds the whole old file or the whole new
    /// one. Nothing of a write that failed is left beside it; the failure is thrown.
    /// </summary>
    public static void Write(string path, VideoSource source)
    {
        var unfinished = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var file = new FileStream(unfinished, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(file, source, YouTubeJson.Files.VideoSource);
                file.Flush(flushToDisk: true);
            }

            File.Move(unfinished, path, overwrite: true);
        }
        catch
        {
            if (File.Exists(unfinished)) File.Delete(unfinished);
            throw;
        }
    }

    /// <summary>
    /// The source file at <paramref name="path"/>; null when there is none. A file that is not a video's source, or lacks what
    /// every source has (its kind, its video's ID, its link, its title and how it was obtained), is refused with an
    /// <see cref="InvalidDataException"/> naming the file. A field this build does not know is passed over.
    /// </summary>
    /// <exception cref="InvalidDataException">The file is not a video's source.</exception>
    /// <exception cref="IOException">The file cannot be read.</exception>
    public static VideoSource? Read(string path)
    {
        if (!File.Exists(path)) return null;

        VideoSource? source;
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            source = JsonSerializer.Deserialize(file, YouTubeJson.Files.VideoSource);
        }
        catch (JsonException ex)
        {
            // Handed on as what it means here, a source file that cannot be read, with the reader's own failure inside.
            throw new InvalidDataException($"The video's source file {path} cannot be read: {ex.Message}", ex);
        }

        return source is { Kind: VideoSource.YouTubeKind, VideoId: not null, Url: not null, Title: not null, Obtained: not null }
            ? source
            : throw new InvalidDataException($"The video's source file {path} cannot be read: it is not the source of a YouTube video.");
    }
}
