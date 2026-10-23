namespace ClassScribe.Audio.Timeline;

/// <summary>A stretch left out of a recording's positions, from <paramref name="From"/> up to but not including <paramref name="To"/>, in units of 100 nanoseconds on the steady clock.</summary>
/// <param name="From">Where the stretch begins.</param>
/// <param name="To">Where it ends.</param>
public readonly record struct ClockExclusion(long From, long To);

/// <summary>
/// The whole state of a <see cref="RecordingClock"/> at one moment, immutable (teach-video 6.6): the offset, the origin,
/// the stretches left out and the open pause, with the clock's version, which grows with every change. The clock hands one
/// out under its own lock (<see cref="RecordingClock.Snapshot"/>), and the camera process places the camera's frames by it,
/// as the clock places sound: <see cref="Position"/> and <see cref="IsExcluded"/> are the clock's own code, which the clock
/// itself calls under its lock, so that both processes place by one implementation. A newer snapshot replaces an older
/// one whole, so a late line is mended by the next.
/// </summary>
/// <param name="Offset">The length the recording already had on disk when this run continued it, in units; zero for a new one.</param>
/// <param name="Origin">The stamp of the first packet of any track, from which positions count; null until it arrives.</param>
/// <param name="Excluded">The stretches left out of the positions, sorted, apart and merged where they touched.</param>
/// <param name="PauseStart">When the open pause began; null while not paused.</param>
/// <param name="Version">How many times the clock had changed when this was taken: a snapshot of a later version replaces one of an earlier.</param>
public sealed record ClockState(long Offset, long? Origin, IReadOnlyList<ClockExclusion> Excluded, long? PauseStart, long Version)
{
    /// <summary>A clock before anything happened: no offset, no origin, nothing left out, not paused.</summary>
    public static ClockState Empty { get; } = new(0, null, [], null, 0);

    /// <summary>True while paused with P.</summary>
    public bool IsPaused => PauseStart is not null;

    /// <summary>
    /// The position of the moment <paramref name="stamp100ns"/> on the steady clock, as the clock gives it (see
    /// <see cref="RecordingClock.Position"/>): the offset, plus the time from the origin to it, less the time left out in
    /// between; the offset before the origin is known.
    /// </summary>
    public TimeSpan Position(long stamp100ns) => PositionOf(Offset, Origin, Excluded, PauseStart, stamp100ns);

    /// <summary>True when the moment <paramref name="stamp100ns"/> is inside a pause, the open one included, or a cut, as the clock says it (see <see cref="RecordingClock.IsExcluded"/>).</summary>
    public bool IsExcluded(long stamp100ns) => IsExcludedIn(Excluded, PauseStart, stamp100ns);

    /// <summary>
    /// The clock's own placement: the position of <paramref name="stamp"/> from <paramref name="offset"/>,
    /// <paramref name="origin"/>, the stretches <paramref name="excluded"/> and the open pause from <paramref name="pauseStart"/>.
    /// The offset before the origin is known; for a moment inside an exclusion, the position where the exclusion began; and a
    /// little before the offset for a moment stamped before the origin, as a second track's first packet can be.
    /// </summary>
    internal static TimeSpan PositionOf(long offset, long? origin, IReadOnlyList<ClockExclusion> excluded, long? pauseStart, long stamp)
    {
        if (origin is not { } start) return new TimeSpan(offset);

        var left = Covered(excluded, start, stamp);
        if (pauseStart is { } paused && stamp > Math.Max(start, paused))
        {
            // The open pause leaves out everything from its start, except what a cut inside it already left out.
            var from = Math.Max(start, paused);
            left += stamp - from - Covered(excluded, from, stamp);
        }

        return new TimeSpan(offset + stamp - start - left);
    }

    /// <summary>The clock's own test of a moment: inside the open pause from <paramref name="pauseStart"/>, or inside one of <paramref name="excluded"/>.</summary>
    internal static bool IsExcludedIn(IReadOnlyList<ClockExclusion> excluded, long? pauseStart, long stamp)
    {
        if (pauseStart is { } start && stamp >= start) return true;
        for (var index = 0; index < excluded.Count; index++)
        {
            var (from, to) = excluded[index];
            if (stamp < from) return false;
            if (stamp < to) return true;
        }

        return false;
    }

    /// <summary>How much of the stretch from <paramref name="from"/> up to <paramref name="to"/> the stretches <paramref name="excluded"/> cover.</summary>
    internal static long Covered(IReadOnlyList<ClockExclusion> excluded, long from, long to)
    {
        if (to <= from) return 0;

        long covered = 0;
        for (var index = 0; index < excluded.Count; index++)
        {
            var (start, end) = excluded[index];
            if (start >= to) break;
            covered += Math.Max(0, Math.Min(to, end) - Math.Max(from, start));
        }

        return covered;
    }
}
