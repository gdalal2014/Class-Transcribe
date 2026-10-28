using ClassScribe.Desktop;
using Microsoft.Extensions.Logging;

namespace ClassScribe.Recorder;

/// <summary>
/// Finds the times when ClassScribe did not run. Each tick compares the steady clock with the tick before, whoever made it:
/// a jump of <see cref="Threshold"/> or more beyond the one-second tick means the process did not run in between. Windows'
/// interrupt time against its unbiased interrupt time, which leaves sleep out, tells a sleep from a hold-up. Windows
/// setting its time of day moves neither clock, so it is never taken for a freeze, and no power notification is relied on.
/// </summary>
/// <remarks>
/// <para>The recording loop ticks the watch once a second, but between its ticks it does work of its own, and that can hold it
/// up, as when the recording's lock waits for a slow disk to save. So the watch also ticks on a timer of its own (see
/// <see cref="Start"/>), which does nothing else and never waits for the recording: while only the loop is held up, the
/// timer's ticks keep coming, and only a time when the whole of ClassScribe did not run counts. Whichever tick finds a
/// freeze first, the freeze is kept until the loop's next tick hands it on, once.</para>
/// <para>A capture thread that sees a packet far ahead of its track, as the first packet after a sleep is, asks for a look
/// at once (<see cref="CheckNow"/>) rather than wait for the tick. Each freeze found, by a tick, by the timer or by a look,
/// is also raised through <see cref="Frozen"/> once, by the next tick or look and never on the timer's thread, so that the
/// recording clock cuts it out before the packet is placed. It is raised while that tick or look still holds the watch,
/// so that every other tick or look, as the other track's capture thread makes one when both wake from one sleep, answers
/// only after the cut has landed and never pads the sleep.</para>
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

    /// <summary>The freezes found and not handed on by <see cref="Tick"/> yet, oldest first.</summary>
    private readonly List<Freeze> _found = [];

    /// <summary>The freezes found and not raised through <see cref="Frozen"/> yet, oldest first.</summary>
    private readonly List<Freeze> _unraised = [];

    /// <summary>The clocks as the last tick read them, whoever made it, or null before the first tick.</summary>
    private Reading? _last;

    /// <summary>Whether the timer has failed to read the clocks: that is logged as a warning once, and later failures only for diagnosis.</summary>
    private bool _timerFailed;

    /// <summary>
    /// Raised once for each freeze found, on the thread of the <see cref="Tick"/> or <see cref="CheckNow"/> that finds it or
    /// that follows the timer's finding it, never on the timer's thread, and while that call holds the watch: a tick or a
    /// look from another thread answers only after the handlers have run. The recording clock cuts the freeze out of the
    /// positions of every track that follows it, by the freeze's edges on the steady clock. A handler is quick and never
    /// waits for another caller of the watch, which would be waiting for it.
    /// </summary>
    public event EventHandler<Freeze>? Frozen;

    /// <summary>
    /// Starts the watch, if no tick has yet, and has it tick once a second on a timer of its own until the result is
    /// disposed. Freezes the timer finds wait for the next <see cref="Tick"/> or <see cref="CheckNow"/>.
    /// </summary>
    public IDisposable Start()
    {
        lock (_gate) _last ??= Read();

        return time.CreateTimer(_ => TickOnTimer(), null, TickInterval, TickInterval);
    }

    /// <summary>
    /// One tick, once a second: compares the clocks with the last tick, this caller's or the timer's, raises
    /// <see cref="Frozen"/> for every freeze not raised yet, and hands on every freeze found since the last call and not
    /// handed on yet, by this tick, by the timer or by a look, oldest first. Empty when there is none, as at the first
    /// tick, which only starts the watch.
    /// </summary>
    public IReadOnlyList<Freeze> Tick()
    {
        lock (_gate)
        {
            Look();
            Freeze[] found = [.. _found];
            _found.Clear();
            RaiseLocked();
            return found;
        }
    }

    /// <summary>
    /// A look at once, from a capture thread that sees a packet far ahead of its track: compares the clocks with the last
    /// tick or look, whoever made it, and raises <see cref="Frozen"/> for every freeze not raised yet, this look's or the
    /// timer's. Answers with the last freeze raised, so that the caller knows the clock has just been cut, or null when
    /// there was none to raise; either way, by the time it answers, every freeze found so far has been cut, a look on
    /// another thread included. The freeze still waits for the next <see cref="Tick"/> to hand it on, so a look and a tick
    /// on one freeze give one event and one freeze handed on.
    /// </summary>
    public Freeze? CheckNow()
    {
        lock (_gate)
        {
            Look();
            return RaiseLocked();
        }
    }

    /// <summary>
    /// Raises <see cref="Frozen"/> for every freeze not raised yet, oldest first, and answers with the last of them, or null
    /// when there was none. Call with the lock held: the handlers run before any other tick or look can answer, so the clock
    /// is always cut before a packet after the freeze is placed. The list is cleared first, so a handler that looks or ticks
    /// again on this thread, which the lock lets in, finds nothing to raise twice.
    /// </summary>
    private Freeze? RaiseLocked()
    {
        if (_unraised.Count == 0) return null;

        Freeze[] unraised = [.. _unraised];
        _unraised.Clear();
        foreach (var freeze in unraised) Frozen?.Invoke(this, freeze);
        return unraised[^1];
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
    /// reading, keeps the freeze for <see cref="Tick"/> to hand on and for the next tick or look to raise. Call with the
    /// lock held.
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
        var freeze = new Freeze(last.Utc, now.Utc, last.Timestamp + margin, now.Timestamp - margin, Slept: asleep >= SleepEvidence);
        _found.Add(freeze);
        _unraised.Add(freeze);
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
