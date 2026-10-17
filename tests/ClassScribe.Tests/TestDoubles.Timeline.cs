using ClassScribe.Audio.Microphones;

namespace ClassScribe.Tests;

/// <summary>
/// A gate over the microphone's packets that answers as the test says: one verdict for every packet, or a verdict per
/// stamp. <see cref="RaiseChanged"/> tells the session to judge its held packets again, as a look arriving does.
/// </summary>
public sealed class FakePacketGate : IPacketGate
{
    private Func<long, GateVerdict> _verdict = _ => GateVerdict.Write;
    private int _judgements;

    /// <summary>How many packets have been judged, held ones judged again included.</summary>
    public int Judgements => Volatile.Read(ref _judgements);

    /// <summary>True while anyone listens for <see cref="Changed"/>: a session that has set this gate, until it lets go.</summary>
    public bool HasListeners => Changed is not null;

    /// <inheritdoc />
    public event EventHandler? Changed;

    /// <summary>Answers <paramref name="verdict"/> for every packet from now on.</summary>
    public void Answer(GateVerdict verdict) => Answer(_ => verdict);

    /// <summary>Answers each packet by its stamp, in units of 100 nanoseconds on the test clock, from now on.</summary>
    public void Answer(Func<long, GateVerdict> verdict) => Volatile.Write(ref _verdict, verdict);

    /// <summary>Tells the listeners that the verdicts may have changed, as a look arriving does.</summary>
    public void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

    /// <inheritdoc />
    public GateVerdict Judge(long stamp100ns)
    {
        Interlocked.Increment(ref _judgements);
        return Volatile.Read(ref _verdict)(stamp100ns);
    }
}

/// <summary>
/// The test's clock with a jump the timers do not see: <see cref="Jump"/> moves the steady count and the time of day on
/// without firing any timer, which is how a process that did not run sees the clocks on waking, before its timers catch
/// up. Timers made from it belong to the manual clock underneath and fire when that clock is moved.
/// </summary>
public sealed class JumpingClock(ManualTimeProvider clock) : TimeProvider
{
    private long _jump;

    /// <inheritdoc />
    public override long TimestampFrequency => clock.TimestampFrequency;

    /// <inheritdoc />
    public override TimeZoneInfo LocalTimeZone => clock.LocalTimeZone;

    /// <summary>The manual clock's steady count plus the jumps so far.</summary>
    public override long GetTimestamp() => clock.GetTimestamp() + Volatile.Read(ref _jump);

    /// <summary>The manual clock's time of day plus the jumps so far.</summary>
    public override DateTimeOffset GetUtcNow() => clock.GetUtcNow() + TimeSpan.FromSeconds((double)Volatile.Read(ref _jump) / clock.TimestampFrequency);

    /// <inheritdoc />
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
        clock.CreateTimer(callback, state, dueTime, period);

    /// <summary>Moves both faces of the clock on by <paramref name="by"/> without firing any timer.</summary>
    public void Jump(TimeSpan by) => Interlocked.Add(ref _jump, (long)(by.Ticks * (double)clock.TimestampFrequency / TimeSpan.TicksPerSecond));
}
