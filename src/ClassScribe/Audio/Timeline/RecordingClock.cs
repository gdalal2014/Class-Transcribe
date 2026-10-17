using ClassScribe.Recorder;

namespace ClassScribe.Audio.Timeline;

/// <summary>
/// The one clock every track of a recording is placed on, so that the class and the microphone always agree. A position
/// is the length the recording already had when this run continued it (the offset), plus the time since the first packet
/// of any track (the origin), less the time paused with P and the time cut out for a sleep or a hold-up (the exclusions).
/// Stamps and moments are in units of 100 nanoseconds on the steady clock, as <see cref="MediaTime"/> gives them. One
/// lock guards it all; it is called from the capture threads, the padding timers and the loop.
/// </summary>
public sealed class RecordingClock(TimeProvider time)
{
    private readonly Lock _gate = new();

    /// <summary>The stretches left out of the positions, each from its start up to but not including its end, kept sorted, apart and merged when they touch.</summary>
    private readonly List<(long From, long To)> _excluded = [];

    /// <summary>The offset, in units.</summary>
    private long _offset;

    /// <summary>The origin, or null before the first packet of any track.</summary>
    private long? _origin;

    /// <summary>When the open pause began, or null while not paused.</summary>
    private long? _pauseStart;

    /// <summary>The time left out by pauses that have ended, in units.</summary>
    private long _paused;

    /// <summary>The time left out by cuts, in units, less what a pause had already left out.</summary>
    private long _cut;

    /// <summary>The length the recording already had on disk when this run continued it; zero for a new recording.</summary>
    public TimeSpan Offset
    {
        get { lock (_gate) return new TimeSpan(_offset); }
    }

    /// <summary>The stamp of the first packet of any track in this run, from which positions count; null until it arrives.</summary>
    public long? Origin
    {
        get { lock (_gate) return _origin; }
    }

    /// <summary>True while paused with P.</summary>
    public bool IsPaused
    {
        get { lock (_gate) return _pauseStart is not null; }
    }

    /// <summary>How long the recording has been paused with P in this run, the open pause counted up to now.</summary>
    public TimeSpan TimePaused
    {
        get
        {
            lock (_gate)
            {
                var total = _paused;
                if (_pauseStart is { } start)
                {
                    var now = MediaTime.Now(time);
                    total += now - start - CoveredLocked(start, now);
                }

                return new TimeSpan(total);
            }
        }
    }

    /// <summary>How much time was cut out of this run for sleeps and hold-ups, other than time that was paused anyway.</summary>
    public TimeSpan TimeCut
    {
        get { lock (_gate) return new TimeSpan(_cut); }
    }

    /// <summary>
    /// Sets the offset: the recording's length on disk when this run continues it. It is set before the first packet of any
    /// track; afterwards the positions are already counted from it, so a change is refused.
    /// </summary>
    public void SetOffset(TimeSpan offset)
    {
        lock (_gate)
        {
            if (_origin is not null) throw new InvalidOperationException("The offset of a recording is set before its first packet arrives.");
            _offset = offset.Ticks;
        }
    }

    /// <summary>
    /// Notes that a packet stamped <paramref name="stamp100ns"/> has arrived on some track. The first such stamp is the
    /// origin, from which every position counts; later ones change nothing. True when this call set the origin.
    /// </summary>
    public bool Begin(long stamp100ns)
    {
        lock (_gate)
        {
            if (_origin is not null) return false;
            _origin = stamp100ns;
            return true;
        }
    }

    /// <summary>
    /// The position of the moment <paramref name="stamp100ns"/> on the steady clock: the offset, plus the time from the
    /// origin to it, less the excluded time in between. The offset before the origin is known; for a moment inside an
    /// exclusion, the position where the exclusion began; and a little before the offset for a moment stamped before the
    /// origin, as a second track's first packet can be.
    /// </summary>
    public TimeSpan Position(long stamp100ns)
    {
        lock (_gate) return PositionLocked(stamp100ns);
    }

    /// <summary>The position now: what a mark set now gets, and where the status line says the recording is.</summary>
    public TimeSpan Now() => Position(MediaTime.Now(time));

    /// <summary>True when the moment <paramref name="stamp100ns"/> is inside a pause, the open one included, or a cut: a packet stamped there is dropped.</summary>
    public bool IsExcluded(long stamp100ns)
    {
        lock (_gate)
        {
            if (_pauseStart is { } start && stamp100ns >= start) return true;
            foreach (var (from, to) in _excluded)
            {
                if (stamp100ns < from) return false;
                if (stamp100ns < to) return true;
            }

            return false;
        }
    }

    /// <summary>Pauses (P): from now until <see cref="Resume"/>, time is left out of the positions, which stay where they are. Nothing more while already paused.</summary>
    public void Pause()
    {
        lock (_gate) _pauseStart ??= MediaTime.Now(time);
    }

    /// <summary>Resumes (P again): the pause ends now and becomes an exclusion, and positions carry on from where they stopped. Nothing while not paused.</summary>
    public void Resume()
    {
        lock (_gate)
        {
            if (_pauseStart is not { } start) return;
            _pauseStart = null;
            var now = MediaTime.Now(time);
            _paused += now - start - CoveredLocked(start, now);
            AddLocked(start, now);
        }
    }

    /// <summary>
    /// Cuts the stretch from <paramref name="from100ns"/> up to <paramref name="to100ns"/> out of the positions, as a time
    /// the laptop slept or ClassScribe was held up is cut (see <see cref="FreezeWatch"/>). A stretch that overlaps an
    /// earlier cut merges with it, so whichever caller sees a freeze first, its time is left out once; a stretch inside the
    /// open pause is left out by the pause already; and an empty stretch is no cut.
    /// </summary>
    public void Cut(long from100ns, long to100ns)
    {
        lock (_gate)
        {
            var to = to100ns;
            if (_pauseStart is { } start && to > start) to = start;
            if (to <= from100ns) return;

            _cut += to - from100ns - CoveredLocked(from100ns, to);
            AddLocked(from100ns, to);
        }
    }

    /// <summary>Cuts <paramref name="freeze"/> out of the positions, by its edges on the steady clock, which the freeze watch gives in that clock's counts.</summary>
    public void Cut(Freeze freeze) =>
        Cut(MediaTime.From(freeze.FromTimestamp, time.TimestampFrequency), MediaTime.From(freeze.ToTimestamp, time.TimestampFrequency));

    /// <summary>The position of <paramref name="stamp"/>. Call with the lock held.</summary>
    private TimeSpan PositionLocked(long stamp)
    {
        if (_origin is not { } origin) return new TimeSpan(_offset);

        var excluded = CoveredLocked(origin, stamp);
        if (_pauseStart is { } start && stamp > Math.Max(origin, start))
        {
            // The open pause leaves out everything from its start, except what a cut inside it already left out.
            var from = Math.Max(origin, start);
            excluded += stamp - from - CoveredLocked(from, stamp);
        }

        return new TimeSpan(_offset + stamp - origin - excluded);
    }

    /// <summary>How much of the stretch from <paramref name="from"/> up to <paramref name="to"/> the exclusions that have ended cover. Call with the lock held.</summary>
    private long CoveredLocked(long from, long to)
    {
        if (to <= from) return 0;

        long covered = 0;
        foreach (var (start, end) in _excluded)
        {
            if (start >= to) break;
            covered += Math.Max(0, Math.Min(to, end) - Math.Max(from, start));
        }

        return covered;
    }

    /// <summary>Adds the stretch from <paramref name="from"/> up to <paramref name="to"/> to the exclusions, merged with those it overlaps or touches. Call with the lock held.</summary>
    private void AddLocked(long from, long to)
    {
        var index = 0;
        while (index < _excluded.Count && _excluded[index].To < from) index++;

        var last = index;
        while (last < _excluded.Count && _excluded[last].From <= to)
        {
            from = Math.Min(from, _excluded[last].From);
            to = Math.Max(to, _excluded[last].To);
            last++;
        }

        _excluded.RemoveRange(index, last - index);
        _excluded.Insert(index, (from, to));
    }
}
