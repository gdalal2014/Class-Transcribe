using ClassScribe.Audio.Timeline;
using ClassScribe.Recorder;

namespace ClassScribe.Tests;

/// <summary>
/// The clock every track of a recording is placed on: positions count from the stamp of the first packet of any track,
/// plus the length the recording already had when continued, less the time paused with P and the time cut out for a
/// sleep or a hold-up. Stamps are in units of 100 nanoseconds on the test's clock, whose count starts at zero.
/// </summary>
[TestFixture]
public sealed class RecordingClockTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 17, 19, 0, 0, TimeSpan.Zero);

    private ManualTimeProvider _time = null!;
    private RecordingClock _clock = null!;

    /// <summary>A clock moved by hand, 10 seconds in so that no stamp of a test falls before its zero, and a recording clock on it.</summary>
    [SetUp]
    public void SetUp()
    {
        _time = new ManualTimeProvider(Start);
        _time.Advance(TimeSpan.FromSeconds(10));
        _clock = new RecordingClock(_time);
    }

    /// <summary>A packet's stamp <paramref name="seconds"/> after the test clock's zero, in its units, rounded to the unit rather than truncated as TimeSpan.FromSeconds truncates.</summary>
    private static long Stamp(double seconds) => (long)Math.Round(seconds * TimeSpan.TicksPerSecond);

    /// <summary>A length of <paramref name="seconds"/>, rounded to the unit.</summary>
    private static TimeSpan Seconds(double seconds) => new(Stamp(seconds));

    /// <summary>Moves the test clock to <paramref name="seconds"/> after its zero.</summary>
    private void MoveTo(double seconds) => _time.Advance(Seconds(seconds) - new TimeSpan(_time.GetTimestamp()));

    /// <summary>Before the first packet of any track, every position is the offset the recording was continued at: nothing is padded before anything has arrived.</summary>
    [Test]
    public void Before_the_first_packet_every_position_is_the_offset()
    {
        _clock.SetOffset(TimeSpan.FromHours(1));
        _time.Advance(TimeSpan.FromMinutes(5));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_clock.Origin, Is.Null);
            Assert.That(_clock.Now(), Is.EqualTo(TimeSpan.FromHours(1)));
            Assert.That(_clock.Position(Stamp(900)), Is.EqualTo(TimeSpan.FromHours(1)));
            Assert.That(_clock.Offset, Is.EqualTo(TimeSpan.FromHours(1)));
        }
    }

    /// <summary>A new recording starts at zero: the first packet's stamp is the origin, and a position is the time since it.</summary>
    [Test]
    public void A_position_is_the_time_since_the_first_packet()
    {
        var first = _clock.Begin(Stamp(10));
        MoveTo(70);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(first, Is.True);
            Assert.That(_clock.Origin, Is.EqualTo(Stamp(10)));
            Assert.That(_clock.Position(Stamp(10)), Is.EqualTo(TimeSpan.Zero));
            Assert.That(_clock.Position(Stamp(25.5)), Is.EqualTo(Seconds(15.5)));
            Assert.That(_clock.Now(), Is.EqualTo(TimeSpan.FromSeconds(60)));
        }
    }

    /// <summary>A continued recording starts at its length on disk: positions are that offset plus the time since the first packet.</summary>
    [Test]
    public void A_continued_recording_counts_from_its_offset()
    {
        _clock.SetOffset(TimeSpan.FromMinutes(30));
        _clock.Begin(Stamp(10));
        MoveTo(110);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_clock.Position(Stamp(70)), Is.EqualTo(TimeSpan.FromMinutes(30) + TimeSpan.FromSeconds(60)));
            Assert.That(_clock.Now(), Is.EqualTo(TimeSpan.FromMinutes(30) + TimeSpan.FromSeconds(100)));
        }
    }

    /// <summary>
    /// The origin is set once, by the first packet of any track: a second track's first packet, stamped a little earlier
    /// but delivered later, does not move it, and is placed a little before zero.
    /// </summary>
    [Test]
    public void The_origin_is_set_once()
    {
        _clock.Begin(Stamp(10));

        var movedIt = _clock.Begin(Stamp(9.98));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(movedIt, Is.False);
            Assert.That(_clock.Origin, Is.EqualTo(Stamp(10)));
            Assert.That(_clock.Position(Stamp(9.98)), Is.EqualTo(Seconds(-0.02)));
        }
    }

    /// <summary>The offset is the recording's length when the run starts, so it cannot change once the first packet has set the origin.</summary>
    [Test]
    public void The_offset_cannot_change_after_the_first_packet()
    {
        _clock.Begin(Stamp(10));

        Assert.That(() => _clock.SetOffset(TimeSpan.FromMinutes(1)), Throws.InvalidOperationException);
    }

    /// <summary>
    /// A pause holds the position where it was: while paused, now and every stamp inside the pause are the pause point, and
    /// after P again positions carry on from it, the time paused left out.
    /// </summary>
    [Test]
    public void A_pause_holds_the_position_and_leaves_its_time_out()
    {
        _clock.Begin(Stamp(10));
        MoveTo(70);
        _clock.Pause();
        _time.Advance(TimeSpan.FromMinutes(5));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_clock.IsPaused, Is.True);
            Assert.That(_clock.Now(), Is.EqualTo(TimeSpan.FromSeconds(60)));
            Assert.That(_clock.Position(Stamp(100)), Is.EqualTo(TimeSpan.FromSeconds(60)));
            Assert.That(_clock.TimePaused, Is.EqualTo(TimeSpan.FromMinutes(5)), "the open pause counts up to now");
        }

        _clock.Resume();
        _time.Advance(TimeSpan.FromSeconds(10));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_clock.IsPaused, Is.False);
            Assert.That(_clock.Now(), Is.EqualTo(TimeSpan.FromSeconds(70)));
            Assert.That(_clock.Position(Stamp(380)), Is.EqualTo(TimeSpan.FromSeconds(70)));
            Assert.That(_clock.TimePaused, Is.EqualTo(TimeSpan.FromMinutes(5)));
            Assert.That(_clock.TimeCut, Is.EqualTo(TimeSpan.Zero));
        }
    }

    /// <summary>
    /// A packet stamped before the pause but delivered after it still belongs before it: its position is unchanged and it is
    /// not dropped, while a packet stamped from the pause point on, until P again, is.
    /// </summary>
    [Test]
    public void A_packet_stamped_before_a_pause_keeps_its_place_and_one_inside_it_is_dropped()
    {
        _clock.Begin(Stamp(10));
        MoveTo(70);
        _clock.Pause();
        _time.Advance(TimeSpan.FromMinutes(5));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_clock.Position(Stamp(69.99)), Is.EqualTo(Seconds(59.99)));
            Assert.That(_clock.IsExcluded(Stamp(69.99)), Is.False);
            Assert.That(_clock.IsExcluded(Stamp(70)), Is.True);
            Assert.That(_clock.IsExcluded(Stamp(1000)), Is.True, "the pause is open");
        }

        _clock.Resume();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_clock.IsExcluded(Stamp(369.99)), Is.True);
            Assert.That(_clock.IsExcluded(Stamp(370)), Is.False);
            Assert.That(_clock.IsExcluded(Stamp(69.99)), Is.False);
        }
    }

    /// <summary>P twice in a row pauses once, and P again twice resumes once: the second of each does nothing.</summary>
    [Test]
    public void Pausing_twice_and_resuming_twice_count_once()
    {
        _clock.Begin(Stamp(10));
        MoveTo(70);
        _clock.Pause();
        _time.Advance(TimeSpan.FromSeconds(30));
        _clock.Pause();
        _time.Advance(TimeSpan.FromSeconds(30));
        _clock.Resume();
        _time.Advance(TimeSpan.FromSeconds(30));
        _clock.Resume();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_clock.TimePaused, Is.EqualTo(TimeSpan.FromSeconds(60)));
            Assert.That(_clock.Now(), Is.EqualTo(TimeSpan.FromSeconds(90)));
        }
    }

    /// <summary>
    /// A cut, as a sleep is cut out, leaves its time out of every later position, holds the positions inside it at its
    /// start, and drops a packet stamped inside it.
    /// </summary>
    [Test]
    public void A_cut_is_left_out_of_the_positions()
    {
        _clock.Begin(Stamp(10));

        _clock.Cut(Stamp(100), Stamp(118));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_clock.Position(Stamp(100)), Is.EqualTo(TimeSpan.FromSeconds(90)));
            Assert.That(_clock.Position(Stamp(110)), Is.EqualTo(TimeSpan.FromSeconds(90)));
            Assert.That(_clock.Position(Stamp(118)), Is.EqualTo(TimeSpan.FromSeconds(90)));
            Assert.That(_clock.Position(Stamp(130)), Is.EqualTo(TimeSpan.FromSeconds(102)));
            Assert.That(_clock.IsExcluded(Stamp(99.99)), Is.False);
            Assert.That(_clock.IsExcluded(Stamp(105)), Is.True);
            Assert.That(_clock.IsExcluded(Stamp(118)), Is.False);
            Assert.That(_clock.TimeCut, Is.EqualTo(TimeSpan.FromSeconds(18)));
        }
    }

    /// <summary>
    /// Either the loop's tick or a capture thread may see a freeze first, and each may cut it with slightly different
    /// edges: cuts that overlap merge, so the time is left out once, and the same cut twice is one cut.
    /// </summary>
    [Test]
    public void Cuts_that_overlap_merge()
    {
        _clock.Begin(Stamp(10));

        _clock.Cut(Stamp(100), Stamp(120));
        _clock.Cut(Stamp(110), Stamp(130));
        _clock.Cut(Stamp(100), Stamp(120));
        _clock.Cut(Stamp(130), Stamp(135));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_clock.TimeCut, Is.EqualTo(TimeSpan.FromSeconds(35)));
            Assert.That(_clock.Position(Stamp(140)), Is.EqualTo(TimeSpan.FromSeconds(95)));
            Assert.That(_clock.IsExcluded(Stamp(125)), Is.True);
            Assert.That(_clock.IsExcluded(Stamp(134.99)), Is.True);
            Assert.That(_clock.IsExcluded(Stamp(135)), Is.False);
        }
    }

    /// <summary>A cut that ends where it starts, or before, is no cut.</summary>
    [Test]
    public void An_empty_or_backwards_cut_is_ignored()
    {
        _clock.Begin(Stamp(10));

        _clock.Cut(Stamp(100), Stamp(100));
        _clock.Cut(Stamp(120), Stamp(100));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_clock.TimeCut, Is.EqualTo(TimeSpan.Zero));
            Assert.That(_clock.Position(Stamp(130)), Is.EqualTo(TimeSpan.FromSeconds(120)));
            Assert.That(_clock.IsExcluded(Stamp(100)), Is.False);
        }
    }

    /// <summary>A sleep while paused is already left out by the pause: the time counts as paused, not as cut, and is left out once.</summary>
    [Test]
    public void A_cut_inside_a_pause_counts_as_paused()
    {
        _clock.Begin(Stamp(10));
        MoveTo(70);
        _clock.Pause();
        MoveTo(95);
        _clock.Cut(Stamp(80), Stamp(90));
        MoveTo(100);
        _clock.Resume();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_clock.TimePaused, Is.EqualTo(TimeSpan.FromSeconds(30)));
            Assert.That(_clock.TimeCut, Is.EqualTo(TimeSpan.Zero));
            Assert.That(_clock.Position(Stamp(110)), Is.EqualTo(TimeSpan.FromSeconds(70)));
        }
    }

    /// <summary>A cut that begins before a pause and reaches into it is cut before the pause and paused from the pause on, each time left out once.</summary>
    [Test]
    public void A_cut_that_reaches_into_a_pause_is_left_out_once()
    {
        _clock.Begin(Stamp(10));
        MoveTo(70);
        _clock.Pause();
        MoveTo(80);
        _clock.Cut(Stamp(65), Stamp(75));
        MoveTo(100);
        _clock.Resume();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_clock.TimeCut, Is.EqualTo(TimeSpan.FromSeconds(5)));
            Assert.That(_clock.TimePaused, Is.EqualTo(TimeSpan.FromSeconds(30)));
            Assert.That(_clock.Position(Stamp(110)), Is.EqualTo(TimeSpan.FromSeconds(65)));
            Assert.That(_clock.IsExcluded(Stamp(66)), Is.True);
        }
    }

    /// <summary>
    /// A freeze is cut by its edges on the steady clock, which the freeze watch gives in that clock's counts: on a clock
    /// that counts in milliseconds, the counts are converted to the recording clock's units.
    /// </summary>
    [Test]
    public void A_freeze_is_cut_by_its_counts_on_the_steady_clock()
    {
        var time = new MillisecondClock();
        var clock = new RecordingClock(time);
        clock.Begin(Stamp(10));

        clock.Cut(new Freeze(Start, Start.AddSeconds(18), FromTimestamp: 100_000, ToTimestamp: 118_000, Slept: true));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(clock.TimeCut, Is.EqualTo(TimeSpan.FromSeconds(18)));
            Assert.That(clock.Position(Stamp(130)), Is.EqualTo(TimeSpan.FromSeconds(102)));
            Assert.That(clock.IsExcluded(Stamp(117.999)), Is.True);
        }
    }

    /// <summary>A pause before the first packet has nothing to leave out: positions count from the first packet as if there had been none.</summary>
    [Test]
    public void A_pause_before_the_first_packet_leaves_nothing_out()
    {
        _clock.Pause();
        MoveTo(20);
        _clock.Resume();

        _clock.Begin(Stamp(30));

        Assert.That(_clock.Position(Stamp(40)), Is.EqualTo(TimeSpan.FromSeconds(10)));
    }

    /// <summary>A pause that is open when the first packet arrives leaves out only its part from that packet on.</summary>
    [Test]
    public void A_pause_open_at_the_first_packet_leaves_out_its_part_from_then_on()
    {
        _clock.Pause();
        MoveTo(20);
        _clock.Begin(Stamp(20));
        MoveTo(30);
        _clock.Resume();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_clock.IsExcluded(Stamp(20)), Is.True, "the first packet itself is dropped");
            Assert.That(_clock.Position(Stamp(40)), Is.EqualTo(TimeSpan.FromSeconds(10)));
        }
    }

    /// <summary>A steady clock that counts in milliseconds, for the conversion of a freeze's counts.</summary>
    private sealed class MillisecondClock : TimeProvider
    {
        /// <inheritdoc />
        public override long TimestampFrequency => 1_000;

        /// <inheritdoc />
        public override long GetTimestamp() => 200_000;
    }
}
