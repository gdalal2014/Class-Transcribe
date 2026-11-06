namespace ClassScribe.YouTube;

/// <summary>
/// The titles of the record path's videos (architecture 4.3; design 2.2), read from YouTube's public oEmbed address, paced
/// by the reader: a video YouTube does not answer for, an answer that cannot be read, a connection that fails and a read
/// that runs out of time each give no title, said at Debug, and the video is tried all the same. The caller's own cancel
/// goes on. One per batch.
/// </summary>
public interface IVideoTitles
{
    /// <summary>The title and channel of video <paramref name="videoId"/>; null when YouTube gave none.</summary>
    Task<VideoTitle?> ReadAsync(string videoId, CancellationToken cancellationToken);
}
