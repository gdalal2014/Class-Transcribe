using ClassScribe.Recorder;

namespace ClassScribe.Audio.Timeline;

/// <summary>What a session does with a packet, as <see cref="TrackPlacer.Place"/> decides it.</summary>
public enum PlacementKind
{
    /// <summary>The packet lands where the track has reached, within the tolerance: write it as it is.</summary>
    Append,

    /// <summary>The packet is a little ahead of the track: write <see cref="Placement.Gap"/> of silence first, then the packet.</summary>
    Pad,

    /// <summary>The packet is behind the track: drop <see cref="Placement.Trimmed"/> from its start, then write the rest, if any.</summary>
    Trim,

    /// <summary>The packet is stamped inside a pause or a cut: write nothing, though its sound is still measured.</summary>
    Drop,

    /// <summary>
    /// The packet is far ahead of the track, which only happens when the process was frozen: ask <c>FreezeWatch.CheckNow</c>
    /// for a look, which cuts a freeze from the clock, and place the packet again with the look made. The cut keeps
    /// <c>FreezeWatch.Margin</c> at each edge, so after a sleep the packet placed again still lands about three to
    /// five seconds ahead (the margins, plus the time from the last tick before the sleep) and is a <see cref="LongGap"/>.
    /// </summary>
    CheckFreeze,

    /// <summary>The look found no freeze and the gap is at most a minute: write <see cref="Placement.Gap"/> of silence in chunks of <see cref="TrackPlacer.PaddingChunk"/>, then the packet.</summary>
    LongGap,

    /// <summary>The look found no freeze and the gap is longer than a minute, which is not expected: write the packet as it is, with a warning, and leave the padding timer to catch the track up.</summary>
    TooFarAhead
}

/// <summary>Where a packet goes on its track.</summary>
/// <param name="Kind">What to do with it.</param>
/// <param name="Gap">
/// How much silence to write before it, for <see cref="PlacementKind.Pad"/> and <see cref="PlacementKind.LongGap"/>; how
/// far ahead it is, for the log, for <see cref="PlacementKind.CheckFreeze"/> and <see cref="PlacementKind.TooFarAhead"/>;
/// zero for <see cref="PlacementKind.Append"/>, <see cref="PlacementKind.Trim"/> and <see cref="PlacementKind.Drop"/>.
/// </param>
/// <param name="Trimmed">How much to drop from its start, for <see cref="PlacementKind.Trim"/>: the whole packet when it is shorter than the time it is behind.</param>
public readonly record struct Placement(PlacementKind Kind, TimeSpan Gap = default, TimeSpan Trimmed = default);

/// <summary>
/// Keeps one track on the recording clock. It knows how far the track has been written (its earlier parts, the open one
/// and the length it had before this run) and decides where each packet goes by the packet's stamp: appended when it
/// lands where the track has reached, after a little silence when it is a little ahead, cut at the front when it is
/// behind, so that a device whose clock drifts from the steady clock is corrected in small steps; and it says how much
/// silence the padding timer owes the track when nothing arrives. The session writes, then reports what it wrote.
/// </summary>
public sealed class TrackPlacer(RecordingClock clock, TimeProvider time, TimeSpan written = default)
{
    /// <summary>How far a stamped packet may be from where the track has reached and still be appended as it is.</summary>
    public static readonly TimeSpan Tolerance = TimeSpan.FromMilliseconds(50);

    /// <summary>The tolerance for a packet whose stamp Windows flagged as wrong, which is placed by the time it arrived instead.</summary>
    public static readonly TimeSpan ArrivalTolerance = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// The furthest a packet may be from where the track has reached, either way, for its padding or trimming to count as
    /// a correction for drift. A device's clock drifts by milliseconds, so a wider gap is something else: a stretch with
    /// nothing playing on an output device, whose loopback hands over nothing meanwhile, or a capture that stalled. Such
    /// a gap is padded or trimmed all the same, and not counted.
    /// </summary>
    public static readonly TimeSpan DriftLimit = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// The longest gap written before a packet without a look for a freeze. The padding timer keeps a track within about a
    /// second of the clock, so a longer gap means the process did not run, unless a look says otherwise.
    /// </summary>
    public static readonly TimeSpan InlineGapLimit = TimeSpan.FromSeconds(2.5);

    /// <summary>The longest gap written on a capture thread after a look found no freeze; beyond it the packet is appended with a warning.</summary>
    public static readonly TimeSpan LongGapLimit = TimeSpan.FromSeconds(60);

    /// <summary>
    /// How far behind now the padding timer pads a track to. A packet arrives up to about 80 ms after its stamp, and
    /// Windows holds up to a second of audio, so audio never lands on padding.
    /// </summary>
    public static readonly TimeSpan PaddingMargin = TimeSpan.FromSeconds(1.5);

    /// <summary>The most silence written in one go, so that a capture thread never waits for more than a second's write.</summary>
    public static readonly TimeSpan PaddingChunk = TimeSpan.FromSeconds(1);

    private readonly Lock _gate = new();

    /// <summary>The length written before the open part, in units: the parts closed in this run, and what the track had before it.</summary>
    private long _closed = written.Ticks;

    /// <summary>The frames written to the open part, counted as frames so that the length stays exact.</summary>
    private long _frames;

    /// <summary>The open part's sample rate; zero before anything is written in this run.</summary>
    private int _rate;

    private int _corrections;

    /// <summary>How far the track has been written: its earlier parts, the open one and the length it had before this run.</summary>
    public TimeSpan Written
    {
        get { lock (_gate) return new TimeSpan(WrittenLocked()); }
    }

    /// <summary>
    /// How many packets were padded or trimmed by at most <see cref="DriftLimit"/> to keep the track on the clock: the
    /// drift corrections of the end-of-class summary. A wider gap is padded or trimmed too, but is no drift.
    /// </summary>
    public int Corrections
    {
        get { lock (_gate) return _corrections; }
    }

    /// <summary>
    /// Decides where a packet goes. The packet's first frame was recorded at <paramref name="stamp100ns"/> on the steady
    /// clock, or, when Windows flagged the stamp as wrong (null), it is taken to have ended when it arrived, which is now
    /// unless <paramref name="arrival100ns"/> says when (a packet the gate held), with a wider tolerance; it lasts
    /// <paramref name="length"/>. The first stamp of any track sets the clock's origin. A packet far ahead asks for a look
    /// for a freeze first; <paramref name="freezeChecked"/> says the look has been made and the packet is placed again.
    /// </summary>
    public Placement Place(long? stamp100ns, TimeSpan length, long? arrival100ns = null, bool freezeChecked = false)
    {
        var tolerance = Tolerance;
        if (stamp100ns is not { } stamp)
        {
            stamp = (arrival100ns ?? MediaTime.Now(time)) - length.Ticks;
            tolerance = ArrivalTolerance;
        }

        clock.Begin(stamp);
        if (clock.IsExcluded(stamp)) return new Placement(PlacementKind.Drop);

        var position = clock.Position(stamp);
        lock (_gate)
        {
            var ahead = position - new TimeSpan(WrittenLocked());
            if (ahead > InlineGapLimit)
            {
                if (!freezeChecked) return new Placement(PlacementKind.CheckFreeze, ahead);
                return ahead > LongGapLimit ? new Placement(PlacementKind.TooFarAhead, ahead) : new Placement(PlacementKind.LongGap, ahead);
            }

            if (ahead > tolerance)
            {
                if (ahead <= DriftLimit) _corrections++;
                return new Placement(PlacementKind.Pad, ahead);
            }

            if (ahead < -tolerance)
            {
                var behind = -ahead;
                if (behind <= DriftLimit) _corrections++;
                return new Placement(PlacementKind.Trim, Trimmed: behind < length ? behind : length);
            }

            return new Placement(PlacementKind.Append);
        }
    }

    /// <summary>
    /// Notes that <paramref name="frames"/> frames at <paramref name="sampleRate"/> were written to the track: a packet,
    /// what was left of one, or silence. A part at a new rate closes the count of the one before, so the length written
    /// is exact however the rate's period divides.
    /// </summary>
    public void Wrote(long frames, int sampleRate)
    {
        lock (_gate)
        {
            if (sampleRate != _rate)
            {
                _closed += OpenPartLocked();
                _frames = 0;
                _rate = sampleRate;
            }

            _frames += frames;
        }
    }

    /// <summary>Where the padding timer pads the track to: the position of <see cref="PaddingMargin"/> ago, which stops at a pause and at the offset before the first packet.</summary>
    public TimeSpan PaddingTarget() => clock.Position(MediaTime.Now(time) - PaddingMargin.Ticks);

    /// <summary>How much silence the track is short of <paramref name="target"/>; zero when it has reached it.</summary>
    public TimeSpan Shortfall(TimeSpan target)
    {
        var shortfall = target - Written;
        return shortfall > TimeSpan.Zero ? shortfall : TimeSpan.Zero;
    }

    /// <summary>The length written, in units. Call with the lock held.</summary>
    private long WrittenLocked() => _closed + OpenPartLocked();

    /// <summary>How long the open part's frames last, in units. Call with the lock held.</summary>
    private long OpenPartLocked() => _rate == 0 ? 0 : MediaTime.FromFrames(_frames, _rate).Ticks;
}
