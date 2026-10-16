using ClassScribe.Desktop;
using Microsoft.Extensions.Logging;

namespace ClassScribe.Recorder;

/// <summary>
/// A time when ClassScribe did not run: the laptop slept, or it stayed awake while Windows held ClassScribe up, as Modern
/// Standby's Desktop Activity Moderator does with desktop programs (a debugger does the same).
/// </summary>
/// <param name="FromUtc">When ClassScribe was last seen running before it, by the time of day.</param>
/// <param name="ToUtc">When ClassScribe was first seen running again, by the time of day.</param>
/// <param name="FromTimestamp">
/// Where the stretch to leave out of a track that follows the clock starts, on the steady clock: the last tick before the
/// freeze plus <see cref="FreezeWatch.Margin"/>, so that the audio Windows still held at its start is kept.
/// </param>
/// <param name="ToTimestamp">Where that stretch ends: the first tick after the freeze less the margin, so that the audio Windows held on waking is kept.</param>
/// <param name="Slept">True when the laptop slept or hibernated; false when it stayed awake and only ClassScribe was held up.</param>
public sealed record Freeze(DateTimeOffset FromUtc, DateTimeOffset ToUtc, long FromTimestamp, long ToTimestamp, bool Slept);

/// <summary>
/// Finds the times when ClassScribe did not run. Each tick compares the steady clock with the tick before, whoever made it:
/// a jump of <see cref="Threshold"/> or more beyond the one-second tick means the process did not run in between. Windows'
/// interrupt time against its unbiased interrupt time, which leaves sleep out, tells a sleep from a hold-up. Windows
/// setting its time of day moves neither clock, so it is never taken for a freeze, and no power notification is relied on.
/// </summary>
/// <remarks>
/// The recording loop ticks the watch once a second, but between its ticks it does work of its own, and that can hold it
/// up, as when the recording's lock waits for a slow disk to save. So the watch also ticks on a timer of its own (see
/// <see cref="Start"/>), which does nothing else and never waits for the recording: while only the loop is held up, the
/// timer's ticks keep coming, and only a time when the whole of ClassScribe did not run counts. Whichever tick finds a
/// freeze first, the freeze is kept until the loop's next tick hands it on, once.
/// </remarks>
public sealed class FreezeWatch(ISystemTimes times, TimeProvider time, ILogger<FreezeWatch> logger)
{
    /// <summary>How far beyond the tick the steady clock must jump to count as a freeze. It catches the 18-second standby a long recording once lost.</summary>
    public static readonly TimeSpan Threshold = TimeSpan.FromSeconds(3);

    /// <summary>How often the recording loop ticks, and the watch's own timer.</summary>
    public static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(1);

    /// <summary>How much of a freeze the unbiased interrupt time must leave out for it to count as a sleep.</summary>
    public static readonly TimeSpan SleepEvidence = TimeSpan.FromSeconds(2);

    /// <summary>How much of each edge of a freeze is kept out of the stretch to leave out (see <see cref="Freeze.FromTimestamp"/>).</summary>
    public static readonly TimeSpan Margin = TimeSpan.FromSeconds(1.5);

    private readonly Lock _gate = new();

    /// <summary>The freezes found and not handed on yet, oldest first.</summary>
    private readonly List<Freeze> _found = [];

    /// <summary>The clocks as the last tick read them, whoever made it, or null before the first tick.</summary>
    private Reading? _last;

    /// <summary>Whether the timer has failed to read the clocks: that is logged as a warning once, and later failures only for diagnosis.</summary>
    private bool _timerFailed;

    /// <summary>
    /// Starts the watch, if no tick has yet, and has it tick once a second on a timer of its own until the result is
    /// disposed. Freezes the timer finds wait for the next <see cref="Tick"/>.
    /// </summary>
    public IDisposable Start()
    {
        lock (_gate) _last ??= Read();

        return time.CreateTimer(_ => TickOnTimer(), null, TickInterval, TickInterval);
    }

    /// <summary>
    /// One tick, once a second: compares the clocks with the last tick, this caller's or the timer's, and hands on every
    /// freeze found since the last call and not handed on yet, by this tick or by the timer, oldest first. Empty when there
    /// is none, as at the first tick, which only starts the watch.
    /// </summary>
    public IReadOnlyList<Freeze> Tick()
    {
        lock (_gate)
        {
            Look();
            if (_found.Count == 0) return [];

            Freeze[] found = [.. _found];
            _found.Clear();
            return found;
        }
    }

    /// <summary>A tick of the watch's own timer: notes a freeze for the next <see cref="Tick"/>. Runs on a thread of the timer's.</summary>
    private void TickOnTimer()
    {
        try
        {
            lock (_gate) Look();
        }
        catch (Exception ex)
        {
            // Deliberately not rethrown: this runs on a timer, where an exception would end the process, and the recording
            // with it. The loop's own ticks read the same clocks, so a freeze is still found, and a failure there is reported.
            bool first;
            lock (_gate)
            {
                first = !_timerFailed;
                _timerFailed = true;
            }

            if (first)
                logger.LogWarning(ex, "Could not read the computer's clocks to watch for a time ClassScribe did not run");
            else
                logger.LogDebug(ex, "Could not read the computer's clocks again to watch for a time ClassScribe did not run");
        }
    }

    /// <summary>
    /// Reads the clocks and, when the steady clock has jumped <see cref="Threshold"/> or more beyond the tick since the last
    /// reading, keeps the freeze for <see cref="Tick"/> to hand on. Call with the lock held.
    /// </summary>
    private void Look()
    {
        var now = Read();
        var before = _last;
        _last = now;
        if (before is not { } last) return;

        var elapsed = ToTimeSpan(now.Timestamp - last.Timestamp);
        if (elapsed - TickInterval < Threshold) return;

        // The interrupt time counts a sleep and the unbiased interrupt time does not, so what the second left out of
        // this stretch is the time the laptop slept in it.
        var asleep = TimeSpan.FromTicks(now.Interrupt - last.Interrupt - (now.Unbiased - last.Unbiased));
        var margin = ToTimestamp(Margin);
        _found.Add(new Freeze(last.Utc, now.Utc, last.Timestamp + margin, now.Timestamp - margin, Slept: asleep >= SleepEvidence));
    }

    /// <summary>Reads the clocks.</summary>
    private Reading Read()
    {
        // The unbiased count first: read in this order, the interrupt time is never the smaller of the two.
        var unbiased = times.UnbiasedInterruptTime100ns();
        var interrupt = times.InterruptTime100ns();
        return new Reading(times.Timestamp(), times.UtcNow(), interrupt, unbiased);
    }

    /// <summary>A stretch of the steady clock as a time span.</summary>
    private TimeSpan ToTimeSpan(long counts) => new((long)(counts * ((double)TimeSpan.TicksPerSecond / times.TimestampFrequency)));

    /// <summary>A time span in counts of the steady clock.</summary>
    private long ToTimestamp(TimeSpan span) => (long)(span.Ticks * ((double)times.TimestampFrequency / TimeSpan.TicksPerSecond));

    /// <summary>The clocks as one tick read them: the steady clock, the time of day, and the two interrupt times.</summary>
    private readonly record struct Reading(long Timestamp, DateTimeOffset Utc, long Interrupt, long Unbiased);
}
