namespace ClassScribe.Audio.Windows;

/// <summary>Hold-ups of a capture loop long enough to lose audio, since the last report of them.</summary>
/// <param name="Count">How many there were.</param>
/// <param name="Longest">The longest, from the end of one drain of Windows' buffer to the end of the next.</param>
/// <param name="Storing">How much of the longest went on handing the audio on, to the disk usually; the loop did not run for the rest.</param>
public sealed record CaptureGap(int Count, TimeSpan Longest, TimeSpan Storing);

/// <summary>
/// Watches a capture loop for hold-ups long enough to lose audio. Windows keeps captured audio waiting
/// for as long as its buffer lasts; when the loop has not emptied the buffer for that long, the oldest
/// audio was overwritten. A hold-up is timed from the end of one drain to the end of the next, so every
/// cause counts: storing the audio (a slow disk), the wait between drains, and a computer too busy to
/// run the loop at all. Hold-ups are reported at most once per <paramref name="reportEvery"/>, and
/// those in between are counted into the next report.
/// </summary>
/// <param name="buffer">How long Windows keeps captured audio waiting.</param>
/// <param name="reportEvery">The least time between two reports.</param>
/// <param name="time">The clock; its steady count is used.</param>
public sealed class CaptureGapWatch(TimeSpan buffer, TimeSpan reportEvery, TimeProvider time)
{
    private long? _lastDrained;
    private long? _lastReport;
    private int _unreported;
    private TimeSpan _longest;
    private TimeSpan _longestStoring;

    /// <summary>
    /// Notes that Windows' buffer has just been emptied, <paramref name="storing"/> of it spent handing the
    /// audio on. Returns the hold-ups to report now, or null when there are none or it is too soon to report again.
    /// </summary>
    public CaptureGap? Drained(TimeSpan storing)
    {
        var now = time.GetTimestamp();
        if (_lastDrained is { } last)
        {
            var heldUp = time.GetElapsedTime(last, now);
            if (heldUp >= buffer)
            {
                _unreported++;
                if (heldUp > _longest)
                {
                    _longest = heldUp;
                    _longestStoring = storing;
                }
            }
        }

        _lastDrained = now;
        if (_unreported == 0) return null;
        if (_lastReport is { } reported && time.GetElapsedTime(reported, now) < reportEvery) return null;

        var gap = new CaptureGap(_unreported, _longest, _longestStoring);
        _unreported = 0;
        _longest = TimeSpan.Zero;
        _longestStoring = TimeSpan.Zero;
        _lastReport = now;
        return gap;
    }
}
