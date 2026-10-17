using ClassScribe.Audio.Timeline;
using ClassScribe.Recorder;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClassScribe.Tests;

/// <summary>
/// Where a packet goes on a track that follows the recording clock: appended when it lands where the track has reached,
/// after a little silence when it is a little ahead, cut at the front when it is behind, dropped inside a pause or a cut,
/// and, when it is far ahead, placed again after a look for a freeze. Packets are 10 ms at 48 kHz unless a test says
/// otherwise, and stamps are in units of 100 nanoseconds on the test's clock.
/// </summary>
[TestFixture]
public sealed class TrackPlacerTests
{
    private const int Rate = 48_000;
    private const int FramesPerPacket = 480;
    private static readonly TimeSpan Packet = TimeSpan.FromMilliseconds(10);
    private static readonly DateTimeOffset Start = new(2026, 10, 17, 19, 0, 0, TimeSpan.Zero);

    private ManualTimeProvider _time = null!;
    private RecordingClock _clock = null!;
    private TrackPlacer _placer = null!;

    /// <summary>A clock moved by hand, 10 seconds in, a recording clock on it, and a placer for a new track.</summary>
    [SetUp]
    public void SetUp()
    {
        _time = new ManualTimeProvider(Start);
        _time.Advance(TimeSpan.FromSeconds(10));
        _clock = new RecordingClock(_time);
        _placer = new TrackPlacer(_clock, _time);
    }

    /// <summary>A packet's stamp <paramref name="seconds"/> after the test clock's zero, in its units, rounded to the unit rather than truncated as TimeSpan.FromSeconds truncates.</summary>
    private static long Stamp(double seconds) => (long)Math.Round(seconds * TimeSpan.TicksPerSecond);

    /// <summary>A length of <paramref name="seconds"/>, rounded to the unit.</summary>
    private static TimeSpan Seconds(double seconds) => new(Stamp(seconds));

    /// <summary>Moves the test clock to <paramref name="seconds"/> after its zero.</summary>
    private void MoveTo(double seconds) => _time.Advance(Seconds(seconds) - new TimeSpan(_time.GetTimestamp()));

    /// <summary>Places the first packet, stamped at 10 s, and writes it, so that the track has reached 10.01 s on the clock.</summary>
    private void FirstPacket()
    {
        Assert.That(_placer.Place(Stamp(10), Packet).Kind, Is.EqualTo(PlacementKind.Append));
        _placer.Wrote(FramesPerPacket, Rate);
    }

    /// <summary>Writes silence until the track has reached <paramref name="position"/>.</summary>
    private void FillTo(TimeSpan position) => _placer.Wrote(MediaTime.ToFrames(position - _placer.Written, Rate), Rate);

    /// <summary>The first packet of a new recording sets the origin and is appended: the track starts at zero with it.</summary>
    [Test]
    public void The_first_packet_sets_the_origin_and_is_appended()
    {
        var placement = _placer.Place(Stamp(10), Packet);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(placement, Is.EqualTo(new Placement(PlacementKind.Append)));
            Assert.That(_clock.Origin, Is.EqualTo(Stamp(10)));
            Assert.That(_placer.Written, Is.EqualTo(TimeSpan.Zero));
        }

        _placer.Wrote(FramesPerPacket, Rate);

        Assert.That(_placer.Written, Is.EqualTo(Packet));
    }

    /// <summary>A packet stamped within 50 ms of where the track has reached, either way, is appended as it is.</summary>
    [TestCase(-50)]
    [TestCase(-7)]
    [TestCase(0)]
    [TestCase(12)]
    [TestCase(50)]
    public void A_packet_within_50_ms_of_the_track_is_appended(int milliseconds)
    {
        FirstPacket();

        var placement = _placer.Place(Stamp(10.01 + milliseconds / 1000.0), Packet);

        Assert.That(placement, Is.EqualTo(new Placement(PlacementKind.Append)));
        Assert.That(_placer.Corrections, Is.Zero);
    }

    /// <summary>
    /// A packet stamped more than 50 ms ahead of the track, up to 2.5 s, is written after that much silence. Up to 200 ms
    /// ahead that counts as a correction for drift; further ahead it is a stretch with nothing playing, which an output
    /// device's loopback hands over nothing for, or a capture that stalled, and is padded all the same but not counted.
    /// </summary>
    [TestCase(51, 1)]
    [TestCase(200, 1)]
    [TestCase(400, 0)]
    [TestCase(2500, 0)]
    public void A_gap_of_up_to_two_and_a_half_seconds_is_padded_first(int milliseconds, int corrections)
    {
        FirstPacket();

        var placement = _placer.Place(Stamp(10.01 + milliseconds / 1000.0), Packet);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(placement.Kind, Is.EqualTo(PlacementKind.Pad));
            Assert.That(placement.Gap, Is.EqualTo(TimeSpan.FromMilliseconds(milliseconds)));
            Assert.That(_placer.Corrections, Is.EqualTo(corrections));
        }
    }

    /// <summary>
    /// A packet stamped more than 50 ms behind the track, as a device whose clock runs fast delivers, loses that much from
    /// its start: all of it when it is shorter than the time it is behind. Up to 200 ms behind counts as a correction for
    /// drift; further behind is a glitch of the stamps, cut the same way but not counted.
    /// </summary>
    [TestCase(60, 100, 60, 1)]
    [TestCase(60, 10, 10, 1)]
    [TestCase(200, 100, 100, 1)]
    [TestCase(300, 100, 100, 0)]
    public void A_packet_behind_the_track_is_cut_at_the_front(int behindMilliseconds, int packetMilliseconds, int trimmedMilliseconds, int corrections)
    {
        FirstPacket();
        FillTo(TimeSpan.FromSeconds(1));

        var placement = _placer.Place(Stamp(11 - behindMilliseconds / 1000.0), TimeSpan.FromMilliseconds(packetMilliseconds));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(placement.Kind, Is.EqualTo(PlacementKind.Trim));
            Assert.That(placement.Trimmed, Is.EqualTo(TimeSpan.FromMilliseconds(trimmedMilliseconds)));
            Assert.That(_placer.Corrections, Is.EqualTo(corrections));
        }
    }

    /// <summary>
    /// A packet the gate held, whose stamp Windows flagged as wrong, is placed by the time it arrived, not by the time the
    /// gate let it through: the arrival given stands in for now, with the wider tolerance of a packet without a stamp.
    /// </summary>
    [Test]
    public void A_packet_without_a_stamp_held_by_the_gate_is_placed_by_its_arrival()
    {
        FirstPacket();
        FillTo(TimeSpan.FromSeconds(1));
        var arrival = Stamp(11.16);                       // 150 ms past the track's end, within the wider tolerance
        MoveTo(12);                                       // the gate let it through 840 ms later

        var placement = _placer.Place(null, Packet, arrival);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(placement, Is.EqualTo(new Placement(PlacementKind.Append)), "by its arrival; by now it would be padded");
            Assert.That(_placer.Corrections, Is.Zero);
        }
    }

    /// <summary>A packet stamped inside a pause is dropped; its stamp still sets the origin when it is the first.</summary>
    [Test]
    public void A_packet_stamped_inside_a_pause_is_dropped()
    {
        FirstPacket();
        FillTo(TimeSpan.FromSeconds(10));
        MoveTo(20);
        _clock.Pause();
        MoveTo(25);

        Assert.That(_placer.Place(Stamp(25), Packet), Is.EqualTo(new Placement(PlacementKind.Drop)));
    }

    /// <summary>The first packet of a recording that is paused when it arrives sets the origin, and is dropped like any other inside the pause.</summary>
    [Test]
    public void The_first_packet_inside_a_pause_sets_the_origin_and_is_dropped()
    {
        _clock.Pause();
        MoveTo(20);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_placer.Place(Stamp(20), Packet).Kind, Is.EqualTo(PlacementKind.Drop));
            Assert.That(_clock.Origin, Is.EqualTo(Stamp(20)));
        }
    }

    /// <summary>A packet stamped just before a pause, delivered after P was pressed, still belongs before it and is appended.</summary>
    [Test]
    public void A_packet_stamped_before_a_pause_and_placed_after_it_is_appended()
    {
        FirstPacket();
        FillTo(TimeSpan.FromSeconds(10));
        MoveTo(20.005);
        _clock.Pause();
        MoveTo(20.03);

        Assert.That(_placer.Place(Stamp(19.995), Packet), Is.EqualTo(new Placement(PlacementKind.Append)));
    }

    /// <summary>
    /// A packet more than 2.5 s ahead of the track can only mean that the process was frozen, since the padding timer would
    /// otherwise have kept the track current: the placer asks for a look for a freeze before anything is written.
    /// </summary>
    [Test]
    public void A_gap_above_two_and_a_half_seconds_asks_for_a_look_for_a_freeze_first()
    {
        FirstPacket();
        MoveTo(13.05);

        var placement = _placer.Place(Stamp(10.01 + 3), Packet);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(placement.Kind, Is.EqualTo(PlacementKind.CheckFreeze));
            Assert.That(placement.Gap, Is.EqualTo(TimeSpan.FromSeconds(3)));
            Assert.That(_placer.Corrections, Is.Zero);
        }
    }

    /// <summary>
    /// A cut that reaches from where the track has reached up to the packet's stamp puts the packet placed again where the
    /// track has reached, so it is appended. The freeze watch never makes such a cut, since it keeps the margins of
    /// <see cref="FreezeWatch.Margin"/> at each edge: that case is the test after this one.
    /// </summary>
    [Test]
    public void A_packet_placed_again_after_a_cut_up_to_its_stamp_is_appended()
    {
        FirstPacket();
        MoveTo(28.05);
        Assert.That(_placer.Place(Stamp(28.01), Packet).Kind, Is.EqualTo(PlacementKind.CheckFreeze));

        _clock.Cut(Stamp(10.01), Stamp(28.01));

        Assert.That(_placer.Place(Stamp(28.01), Packet, freezeChecked: true), Is.EqualTo(new Placement(PlacementKind.Append)));
    }

    /// <summary>
    /// After a sleep, the look the placer asked for finds the freeze, the clock cuts it with a second and a half kept at
    /// each edge, and the packet placed again lands a few seconds ahead: the three seconds of margins, plus the time from
    /// the last tick before the sleep. That is above the inline limit, so the session writes the gap in chunks: the real
    /// path after a sleep is the long gap, not an append.
    /// </summary>
    [Test]
    public void After_a_sleep_the_packet_placed_again_lands_a_few_seconds_ahead_and_is_a_long_gap()
    {
        var times = new FakeSystemTimes(_time);
        var watch = new FreezeWatch(times, _time, NullLogger<FreezeWatch>.Instance);
        watch.Frozen += (_, freeze) => _clock.Cut(freeze);
        watch.Tick();
        FirstPacket();
        times.Sleep(TimeSpan.FromSeconds(18));
        var first = _placer.Place(Stamp(28), Packet);
        Assert.That(first.Kind, Is.EqualTo(PlacementKind.CheckFreeze));

        Assert.That(watch.CheckNow(), Is.Not.Null, "the look found the sleep");
        var again = _placer.Place(Stamp(28), Packet, freezeChecked: true);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_clock.TimeCut, Is.EqualTo(TimeSpan.FromSeconds(15)), "18 s less the margins");
            Assert.That(again, Is.EqualTo(new Placement(PlacementKind.LongGap, Seconds(3) - Packet)));
            Assert.That(_placer.Corrections, Is.Zero);
        }
    }

    /// <summary>
    /// After the look found no freeze, as when the 30-second pause after a disk failure has just ended, a gap of up to 60 s
    /// is written in chunks before the packet; a longer gap, which is not expected, is not written on the capture thread:
    /// the packet is appended with a warning.
    /// </summary>
    [TestCase(3.0, PlacementKind.LongGap)]
    [TestCase(30.0, PlacementKind.LongGap)]
    [TestCase(60.0, PlacementKind.LongGap)]
    [TestCase(60.001, PlacementKind.TooFarAhead)]
    [TestCase(7200.0, PlacementKind.TooFarAhead)]
    public void After_a_look_that_found_no_freeze_a_gap_is_written_up_to_a_minute(double seconds, PlacementKind expected)
    {
        FirstPacket();
        MoveTo(10.05 + seconds);

        var placement = _placer.Place(Stamp(10.01 + seconds), Packet, freezeChecked: true);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(placement.Kind, Is.EqualTo(expected));
            Assert.That(placement.Gap, Is.EqualTo(Seconds(seconds)));
            Assert.That(_placer.Corrections, Is.Zero);
        }
    }

    /// <summary>
    /// A packet whose stamp Windows flagged as wrong is placed by its arrival, as if it had just been recorded, with a
    /// tolerance of 200 ms instead of 50: a late arrival within that is appended, and only beyond it is padded or cut.
    /// </summary>
    [TestCase(150, PlacementKind.Append, 0, 0)]
    [TestCase(-150, PlacementKind.Append, 0, 0)]
    [TestCase(250, PlacementKind.Pad, 250, 0)]
    [TestCase(-250, PlacementKind.Trim, 0, 10)]
    public void A_packet_without_a_stamp_is_placed_by_its_arrival_with_a_wider_tolerance(int milliseconds, PlacementKind expected, int gapMilliseconds, int trimmedMilliseconds)
    {
        FirstPacket();
        FillTo(TimeSpan.FromSeconds(1));
        MoveTo(11 + milliseconds / 1000.0 + 0.01);

        var placement = _placer.Place(null, Packet);

        Assert.That(placement, Is.EqualTo(new Placement(expected, TimeSpan.FromMilliseconds(gapMilliseconds), TimeSpan.FromMilliseconds(trimmedMilliseconds))));
    }

    /// <summary>The first packet without a stamp sets the origin at its arrival less its length.</summary>
    [Test]
    public void The_first_packet_without_a_stamp_sets_the_origin_at_its_arrival_less_its_length()
    {
        MoveTo(12);

        var placement = _placer.Place(null, Packet);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(placement.Kind, Is.EqualTo(PlacementKind.Append));
            Assert.That(_clock.Origin, Is.EqualTo(Stamp(11.99)));
        }
    }

    /// <summary>
    /// What the track has written is counted exactly across parts of different rates: 441 frames at 44.1 kHz and 480 at
    /// 48 kHz are 10 ms each, and single frames at 48 kHz are summed as frames, not as rounded times.
    /// </summary>
    [Test]
    public void What_is_written_is_counted_exactly_across_parts_of_different_rates()
    {
        _placer.Wrote(441, 44_100);
        _placer.Wrote(480, Rate);
        _placer.Wrote(1, Rate);
        _placer.Wrote(1, Rate);
        _placer.Wrote(1, Rate);

        Assert.That(_placer.Written, Is.EqualTo(TimeSpan.FromMilliseconds(20) + new TimeSpan(625)));
    }

    /// <summary>A track continued from its length on disk counts from there.</summary>
    [Test]
    public void A_continued_track_counts_from_its_length_on_disk()
    {
        var placer = new TrackPlacer(_clock, _time, TimeSpan.FromHours(1));
        placer.Wrote(FramesPerPacket, Rate);

        Assert.That(placer.Written, Is.EqualTo(TimeSpan.FromHours(1) + Packet));
    }

    /// <summary>
    /// The padding timer brings a track up to the position of a second and a half ago, never to now, so that a packet that
    /// arrives late, as every packet does a little, never lands on padding; the shortfall is never below zero.
    /// </summary>
    [Test]
    public void The_padding_target_is_the_position_a_second_and_a_half_ago()
    {
        FirstPacket();
        MoveTo(30);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_placer.PaddingTarget(), Is.EqualTo(TimeSpan.FromSeconds(18.5)));
            Assert.That(_placer.Shortfall(TimeSpan.FromSeconds(18.5)), Is.EqualTo(TimeSpan.FromSeconds(18.5) - Packet));
            Assert.That(_placer.Shortfall(TimeSpan.FromMilliseconds(5)), Is.EqualTo(TimeSpan.Zero));
        }
    }

    /// <summary>The padding target stops at the pause point while paused, so padding stops there too.</summary>
    [Test]
    public void The_padding_target_stops_at_the_pause_point()
    {
        FirstPacket();
        MoveTo(30);
        _clock.Pause();
        MoveTo(60);

        Assert.That(_placer.PaddingTarget(), Is.EqualTo(TimeSpan.FromSeconds(20)));
    }

    /// <summary>
    /// When a recording is continued, each track starts from its own length, and one that is shorter than the recording,
    /// as after a crash, is padded up to the recording's length before anything arrives: by the timer, or by its first
    /// packet, whichever comes first.
    /// </summary>
    [Test]
    public void A_track_shorter_than_the_continued_recording_is_padded_up_to_it()
    {
        _clock.SetOffset(TimeSpan.FromHours(1));
        var placer = new TrackPlacer(_clock, _time, TimeSpan.FromHours(1) - TimeSpan.FromSeconds(2));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(placer.PaddingTarget(), Is.EqualTo(TimeSpan.FromHours(1)));
            Assert.That(placer.Shortfall(placer.PaddingTarget()), Is.EqualTo(TimeSpan.FromSeconds(2)));
            Assert.That(placer.Place(Stamp(10), Packet), Is.EqualTo(new Placement(PlacementKind.Pad, TimeSpan.FromSeconds(2))));
        }
    }

    /// <summary>
    /// A device whose clock runs 100 parts in a million fast or slow, as a microphone's typically does, stays within 50 ms
    /// of the recording clock over two hours of 10 ms packets, a little audio dropped or a little silence written each time
    /// it drifts that far, and the corrections are counted.
    /// </summary>
    [TestCase(100)]
    [TestCase(-100)]
    public void A_device_a_hundred_parts_in_a_million_fast_or_slow_stays_within_50_ms_over_two_hours(int partsPerMillion)
    {
        const int packets = 2 * 3600 * 100;
        var origin = Stamp(10);
        var worst = TimeSpan.Zero;

        for (var n = 0; n < packets; n++)
        {
            // A device that runs fast delivers its 480 frames in less than 10 ms of the steady clock, so its stamps come closer together.
            var stamp = origin + (long)Math.Round(n * (double)Packet.Ticks / (1 + partsPerMillion / 1_000_000.0));
            _time.Advance(new TimeSpan(stamp + TimeSpan.FromMilliseconds(20).Ticks - _time.GetTimestamp()));

            var placement = _placer.Place(stamp, Packet);
            switch (placement.Kind)
            {
                case PlacementKind.Append:
                    _placer.Wrote(FramesPerPacket, Rate);
                    break;
                case PlacementKind.Pad:
                    _placer.Wrote(MediaTime.ToFrames(placement.Gap, Rate) + FramesPerPacket, Rate);
                    break;
                case PlacementKind.Trim:
                    _placer.Wrote(Math.Max(0, FramesPerPacket - MediaTime.ToFrames(placement.Trimmed, Rate)), Rate);
                    break;
                default:
                    Assert.Fail($"Packet {n} was placed as {placement.Kind}");
                    break;
            }

            var offBy = _clock.Position(stamp) + Packet - _placer.Written;
            if (offBy.Duration() > worst) worst = offBy.Duration();
            if (offBy.Duration() > TrackPlacer.Tolerance) Assert.Fail($"Packet {n} ended {offBy.TotalMilliseconds} ms off the clock");
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(worst, Is.LessThanOrEqualTo(TrackPlacer.Tolerance));
            Assert.That(_placer.Corrections, Is.InRange(10, 80), "about 720 ms of drift, corrected 10 to 50 ms at a time");
        }
    }
}
