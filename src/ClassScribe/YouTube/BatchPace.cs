using ClassScribe.Recorder;
using Microsoft.Extensions.Logging;

namespace ClassScribe.YouTube;

/// <summary>
/// The pace of a batch's requests to YouTube (design 3.4), behind an interface so that the batch's tests need not play the
/// waits: reads at most one every 6 seconds, downloads one at a time, 10 to 20 seconds apart, and at most 60 begun in any
/// hour. A read made again before a download counts as a read.
/// </summary>
public interface IBatchPace
{
    /// <summary>Waits until a read of YouTube may begin, and notes that it begins.</summary>
    Task BeforeReadAsync(CancellationToken cancellationToken);

    /// <summary>Waits until a download may begin, and notes that it begins.</summary>
    Task BeforeDownloadAsync(CancellationToken cancellationToken);

    /// <summary>Notes that a download has ended, from which the pause before the next is counted.</summary>
    void DownloadEnded();
}

/// <summary>
/// The pace of a batch's requests, on the steady clock (see <see cref="IBatchPace"/>): well under what the yt-dlp wiki gives
/// for a signed-out session, about 300 videos and 1,000 page and player requests an hour, with 5 to 10 seconds between
/// downloads. One per batch.
/// </summary>
/// <param name="time">The clock the waits are timed on, steady, so that a change of the time of day changes nothing.</param>
/// <param name="random">Where the pause between downloads is drawn from.</param>
public sealed class BatchPace(TimeProvider time, Random random) : IBatchPace
{
    /// <summary>The least time between the starts of two reads.</summary>
    public static readonly TimeSpan ReadEvery = TimeSpan.FromSeconds(6);

    /// <summary>The shortest pause after a download before the next begins.</summary>
    public static readonly TimeSpan ShortestPause = TimeSpan.FromSeconds(10);

    /// <summary>The longest pause after a download before the next begins.</summary>
    public static readonly TimeSpan LongestPause = TimeSpan.FromSeconds(20);

    /// <summary>The most downloads begun in any hour.</summary>
    public const int MostDownloadsAnHour = 60;

    private readonly Queue<long> _downloadsBegun = new();
    private long? _lastRead;
    private long? _lastDownloadEnded;

    /// <summary>A pace on the computer's clock, its pauses drawn at random.</summary>
    public BatchPace(TimeProvider time)
        : this(time, Random.Shared)
    {
    }

    /// <inheritdoc />
    public async Task BeforeReadAsync(CancellationToken cancellationToken)
    {
        if (_lastRead is { } last) await WaitUntilAsync(last, ReadEvery, cancellationToken);
        _lastRead = time.GetTimestamp();
    }

    /// <inheritdoc />
    public async Task BeforeDownloadAsync(CancellationToken cancellationToken)
    {
        if (_lastDownloadEnded is { } ended)
        {
            var pause = ShortestPause + TimeSpan.FromMilliseconds(random.NextDouble() * (LongestPause - ShortestPause).TotalMilliseconds);
            await WaitUntilAsync(ended, pause, cancellationToken);
        }

        while (_downloadsBegun.Count > 0 && time.GetElapsedTime(_downloadsBegun.Peek()) >= TimeSpan.FromHours(1)) _downloadsBegun.Dequeue();
        if (_downloadsBegun.Count >= MostDownloadsAnHour)
        {
            await WaitUntilAsync(_downloadsBegun.Peek(), TimeSpan.FromHours(1), cancellationToken);
            _downloadsBegun.Dequeue();
        }

        _downloadsBegun.Enqueue(time.GetTimestamp());
    }

    /// <inheritdoc />
    public void DownloadEnded() => _lastDownloadEnded = time.GetTimestamp();

    /// <summary>Waits until <paramref name="after"/> has passed since <paramref name="since"/>, on the steady clock.</summary>
    private async Task WaitUntilAsync(long since, TimeSpan after, CancellationToken cancellationToken)
    {
        var left = after - time.GetElapsedTime(since);
        if (left > TimeSpan.Zero) await Task.Delay(left, time, cancellationToken);
    }
}

/// <summary>
/// Keeps a batch out of a class's way (design 2.5): before each video and each transcript, while the run journal lists a run
/// that records and is not a video's, the batch waits, looking every 30 seconds, so that nothing reaches YouTube or
/// ElevenLabs while the class streams. The wait is said once, and so is the going on. It never looks at recording.lock.
/// </summary>
public sealed class ClassWait(IRunJournal journal, TimeProvider time, ILogger<ClassWait> logger)
{
    /// <summary>How often a batch held up by a class looks whether it has stopped.</summary>
    public static readonly TimeSpan Poll = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Waits while a class records, in this window or in the background, as its run file says. True when it had to wait, so
    /// that the record path asks Gulzar again before it opens a video's window; false when no class recorded.
    /// </summary>
    public async Task<bool> WhileAClassRecordsAsync(CancellationToken cancellationToken)
    {
        string? waitedFor = null;
        while (journal.List().FirstOrDefault(run => run.IsRecordingNow && !run.IsVideo) is { } run)
        {
            if (waitedFor is null)
            {
                waitedFor = run.Record.Request.Name;
                logger.LogInformation("YouTube: waiting while \"{Name}\" records; the videos go on after it", waitedFor);
            }

            await Task.Delay(Poll, time, cancellationToken);
        }

        if (waitedFor is not null) logger.LogInformation("YouTube: going on, now that \"{Name}\" has stopped recording", waitedFor);
        return waitedFor is not null;
    }
}
