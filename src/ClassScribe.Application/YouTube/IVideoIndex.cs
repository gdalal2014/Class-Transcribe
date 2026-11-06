using Microsoft.Extensions.Logging;

namespace ClassScribe.YouTube;

/// <summary>
/// The videos fetched before (architecture 4.3; design 2.6): every video's source file under the recordings folder, read
/// once per batch and found by the video's ID, so that the same command run again picks up where it stopped and nothing is
/// fetched or billed twice unless asked. One per batch.
/// </summary>
public interface IVideoIndex
{
    /// <summary>
    /// Reads the source file of every video's recording under <paramref name="root"/>, in every course folder and its date
    /// folders. A folder that cannot be looked into, and a source file that cannot be read, are passed over, the second
    /// said at Debug through <paramref name="logger"/>, the batch's: that video is then fetched again, which costs time,
    /// never a recording.
    /// </summary>
    void Read(string root, ILogger logger);

    /// <summary>The recordings of video <paramref name="videoId"/> made before, newest first.</summary>
    IReadOnlyList<PastVideo> Of(string videoId);

    /// <summary>
    /// What was done before with video <paramref name="videoId"/>, the dates as the local day of <paramref name="zone"/>; with
    /// <paramref name="again"/> (--again), it is fetched again in every case.
    /// </summary>
    PastVerdict Judge(string videoId, bool again, TimeZoneInfo zone);
}
