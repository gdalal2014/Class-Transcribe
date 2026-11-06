namespace ClassScribe.YouTube;

/// <summary>
/// A video as its read gives it (design 3.4), with what the guard, the plan, the source file and the transcript use, worked
/// out once by the tool's adapter: yt-dlp's own record never reaches the batch.
/// </summary>
/// <param name="Id">The video's ID.</param>
/// <param name="Title">Its title; null when the read gave none.</param>
/// <param name="Length">Its length; null when it is not known, as for a stream.</param>
/// <param name="Channel">Its channel's name.</param>
/// <param name="ChannelId">Its channel's UC id.</param>
/// <param name="Handle">Its channel's @handle, read as written; null when none is given.</param>
/// <param name="ChannelUrl">Its channel's address.</param>
/// <param name="Uploaded">When it was uploaded, as yyyy-MM-dd; null when not known.</param>
/// <param name="Availability">public, unlisted, private, needs_auth, subscriber_only or premium_only; null when yt-dlp is not sure.</param>
/// <param name="LiveStatus">not_live, is_live, is_upcoming, was_live or post_live.</param>
/// <param name="AgeLimit">The age it is restricted to; 0 for none.</param>
/// <param name="SourceRate">The sample rate its downloaded WAV has, which keeps YouTube's own.</param>
public sealed record VideoInfo(string Id, string? Title, TimeSpan? Length, string? Channel, string? ChannelId, string? Handle, string? ChannelUrl, string? Uploaded,
    string? Availability, string? LiveStatus, int? AgeLimit, int SourceRate)
{
    /// <summary>The rate assumed when no format says its own: 48 kHz, the most YouTube's audio has.</summary>
    public const int HighestRate = 48000;
}

/// <summary>One video of a playlist's listing.</summary>
/// <param name="Id">The video's ID.</param>
/// <param name="Title">Its title, when the listing gave one.</param>
public sealed record ListedVideo(string Id, string? Title);

/// <summary>A playlist as its flat listing gives it.</summary>
/// <param name="Id">The playlist's ID; null when the listing did not say.</param>
/// <param name="Title">Its title; null when the listing did not say.</param>
/// <param name="Entries">Its videos, in order; an entry that names no video is left out.</param>
public sealed record ListedPlaylist(string? Id, string? Title, IReadOnlyList<ListedVideo> Entries);

/// <summary>
/// yt-dlp as the download path uses it (architecture 4.3): the check of the tool and its helpers, whose paths are kept for
/// the calls after it; the calls that read a playlist, read a video and download one, each a <see cref="ToolCall"/> the
/// batch runs itself through <see cref="IToolRunner"/>, its arguments built and checked as the ground rule asks; what the
/// tool wrote, read as Application records; and what a failed run means. One per batch.
/// </summary>
public interface IVideoTool
{
    /// <summary>Finds yt-dlp, Deno, FFmpeg and FFprobe, asks each its version, and says what it found; the paths found serve the calls below.</summary>
    Task<ToolCheck> CheckAsync(CancellationToken cancellationToken);

    /// <summary>The call that reads the playlist <paramref name="playlistId"/>'s flat listing, in the feature's folder. Throws before the check has found yt-dlp.</summary>
    ToolCall ReadPlaylist(string playlistId);

    /// <summary>The call that reads the video <paramref name="videoId"/>'s information into its staging folder, which must be there. Throws before the check has found yt-dlp.</summary>
    ToolCall ReadVideo(string videoId);

    /// <summary>
    /// The call that downloads the video <paramref name="videoId"/>'s audio into its staging folder, from its read's
    /// information file when <paramref name="fromInfoFile"/>, as a mono WAV at YouTube's own rate. Throws before the check has found yt-dlp.
    /// </summary>
    ToolCall Download(string videoId, bool fromInfoFile);

    /// <summary>The playlist <paramref name="playlistId"/>'s listing in <paramref name="output"/>, the lines its read wrote. What is not a listing is refused with an <see cref="InvalidDataException"/>.</summary>
    ListedPlaylist Playlist(IReadOnlyList<string> output, string playlistId);

    /// <summary>True when the video <paramref name="videoId"/>'s information file, which its read writes, is there.</summary>
    bool HasInfo(string videoId);

    /// <summary>The video <paramref name="videoId"/>'s information as its read wrote it; null when there is none. What is not a video's information is refused with an <see cref="InvalidDataException"/> naming the file.</summary>
    Task<VideoInfo?> ReadInfoAsync(string videoId, CancellationToken cancellationToken);

    /// <summary>The WAV yt-dlp finished for the video <paramref name="videoId"/>, as it named it: a .wav in the video's own staging folder; null when it did not say, or the file is not there.</summary>
    string? StagedWav(string videoId);

    /// <summary>What a run that ended with <paramref name="exitCode"/> after writing <paramref name="errorLines"/> means, in words Gulzar reads.</summary>
    DownloadFailure Classify(int exitCode, IReadOnlyList<string> errorLines);
}
