using ClassScribe.Audio.Timeline;
using ClassScribe.Camera;

namespace ClassScribe.Tests;

/// <summary>
/// The pure parts of a lesson's picture (teach-video 6.5 and 6.6): the encoder's settings by the picture's size, the frame
/// placer that puts each frame on the run's clock as sound is put there, and the queue in which each frame waits half a
/// second for the clock line that may leave it out.
/// </summary>
[TestFixture]
public sealed class CameraPiecesTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 22, 14, 0, 0, TimeSpan.Zero);

    /// <summary>A stamp <paramref name="seconds"/> after the test clock's zero, in its units.</summary>
    private static long Stamp(double seconds) => (long)Math.Round(seconds * TimeSpan.TicksPerSecond);

    /// <summary>A length of <paramref name="seconds"/>, to the unit.</summary>
    private static TimeSpan Seconds(double seconds) => new(Stamp(seconds));

    /// <summary>
    /// The bitrate is YouTube's figure for the band of the height, scaled by the pixels against that band's 16:9 size; the
    /// level is the smallest that holds the size at 30 frames a second: 480p takes 3.1, since 848x480 is over level 3.0's
    /// macroblocks a second, which Windows refused in the probe's self-check.
    /// </summary>
    [TestCase("480p", 2_500_000, 31)]
    [TestCase("720p", 5_000_000, 31)]
    [TestCase("1080p", 8_000_000, 40)]
    [TestCase("1024x576", 3_200_000, 31)]
    [TestCase("640x480", 1_886_792, 30)]
    [TestCase("1440x1080", 6_000_000, 40)]
    public void The_encoder_s_settings_follow_the_picture_s_size(string size, int bitsPerSecond, int level)
    {
        Assert.That(CameraSize.TryParse(size, out var parsed), Is.True);

        var settings = PieceSettings.For(parsed!);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(settings.BitsPerSecond, Is.EqualTo(bitsPerSecond));
            Assert.That(settings.Level, Is.EqualTo(level));
            Assert.That(settings.Software, Is.False);
            Assert.That(PieceSettings.For(parsed!, software: true).Software, Is.True);
        }
    }

    /// <summary>The settings in words, as the start line says them.</summary>
    [Test]
    public void The_settings_are_said_in_words() =>
        Assert.That(PieceSettings.For(CameraSize.P720).Words, Is.EqualTo("1280x720 at 30 frames a second, 5 Mbit/s, H.264 level 3.1"));

    /// <summary>The brightness of a picture is that of its brightness plane; a black picture is NV12's black, and so dark.</summary>
    [Test]
    public void A_picture_s_brightness_is_read_from_a_grid_of_its_brightness_plane()
    {
        var black = CameraPicture.Black(1280, 720);
        var grey = new byte[1280 * 720 * 3 / 2];
        grey.AsSpan(0, 1280 * 720).Fill(120);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(CameraPicture.MeanBrightness(black, 1280, 720), Is.EqualTo(16));
            Assert.That(CameraPicture.MeanBrightness(black, 1280, 720), Is.LessThan(CameraPicture.DarkBelow + 1));
            Assert.That(CameraPicture.MeanBrightness(grey, 1280, 720), Is.EqualTo(120));
            Assert.That(black.Skip(1280 * 720).Distinct(), Is.EqualTo(new byte[] { 128 }));
        }
    }

    /// <summary>A clock that has begun at second 10, for the placer's tests.</summary>
    private static (ManualTimeProvider Time, RecordingClock Clock) ClockFrom10()
    {
        var time = new ManualTimeProvider(Start);
        time.Advance(TimeSpan.FromSeconds(10));
        var clock = new RecordingClock(time);
        clock.Begin(Stamp(10));
        return (time, clock);
    }

    /// <summary>A frame is placed where the clock puts its stamp, and written at that position less the piece's start.</summary>
    [Test]
    public void A_frame_is_written_where_the_clock_puts_its_stamp()
    {
        var (time, clock) = ClockFrom10();
        time.Advance(TimeSpan.FromSeconds(5));
        var placer = new FramePlacer(TimeSpan.Zero, TimeSpan.Zero);

        var first = placer.Place(clock.Snapshot(), Stamp(10.04));
        var second = placer.Place(clock.Snapshot(), Stamp(12.5));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(first, Is.EqualTo(new FramePlacement(FrameFate.Written, Seconds(0.04))));
            Assert.That(second, Is.EqualTo(new FramePlacement(FrameFate.Written, Seconds(2.5))));
            Assert.That(placer.Written, Is.EqualTo(2));
            Assert.That(placer.Last, Is.EqualTo(Seconds(2.5)));
        }
    }

    /// <summary>Before the clock's origin, or with no origin yet, a frame is dropped: the piece's black frame shows.</summary>
    [Test]
    public void A_frame_before_the_origin_is_dropped()
    {
        var time = new ManualTimeProvider(Start);
        time.Advance(TimeSpan.FromSeconds(10));
        var clock = new RecordingClock(time);
        var placer = new FramePlacer(TimeSpan.Zero, TimeSpan.Zero);

        var noOrigin = placer.Place(clock.Snapshot(), Stamp(9));
        clock.Begin(Stamp(10));
        var before = placer.Place(clock.Snapshot(), Stamp(9.9));

        Assert.That(new[] { noOrigin.Fate, before.Fate }, Is.EqualTo(new[] { FrameFate.BeforeOrigin, FrameFate.BeforeOrigin }));
        Assert.That(placer.Dropped, Is.EqualTo(2));
    }

    /// <summary>A frame taken inside a pause or a cut is dropped, as sound stamped there is; after the pause the picture carries on where the clock does.</summary>
    [Test]
    public void A_frame_inside_a_pause_or_a_cut_is_dropped_and_the_next_carries_on()
    {
        var (time, clock) = ClockFrom10();
        var placer = new FramePlacer(TimeSpan.Zero, TimeSpan.Zero);
        time.Advance(TimeSpan.FromSeconds(10));
        clock.Pause();
        time.Advance(TimeSpan.FromSeconds(30));
        clock.Resume();
        clock.Cut(Stamp(55), Stamp(58));
        time.Advance(TimeSpan.FromSeconds(30));
        var snapshot = clock.Snapshot();

        var beforePause = placer.Place(snapshot, Stamp(19));
        var paused = placer.Place(snapshot, Stamp(35));
        var cut = placer.Place(snapshot, Stamp(56));
        var after = placer.Place(snapshot, Stamp(60));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(beforePause, Is.EqualTo(new FramePlacement(FrameFate.Written, Seconds(9))));
            Assert.That(paused.Fate, Is.EqualTo(FrameFate.Excluded));
            Assert.That(cut.Fate, Is.EqualTo(FrameFate.Excluded));
            Assert.That(after, Is.EqualTo(new FramePlacement(FrameFate.Written, Seconds(60 - 10 - 30 - 3))), "the pause and the cut left out of its position");
        }
    }

    /// <summary>The camera's offset moves its picture against the sound: negative is earlier; a frame it moves before the piece's start is dropped.</summary>
    [Test]
    public void The_offset_moves_the_picture_and_a_frame_before_the_piece_is_dropped()
    {
        var (time, clock) = ClockFrom10();
        time.Advance(TimeSpan.FromSeconds(5));
        var placer = new FramePlacer(TimeSpan.Zero, TimeSpan.FromMilliseconds(-60));

        var tooEarly = placer.Place(clock.Snapshot(), Stamp(10.05));
        var moved = placer.Place(clock.Snapshot(), Stamp(11));

        Assert.That(tooEarly.Fate, Is.EqualTo(FrameFate.BeforePiece));
        Assert.That(moved, Is.EqualTo(new FramePlacement(FrameFate.Written, Seconds(0.94))));
    }

    /// <summary>
    /// A continued lesson's piece begins at the run's offset: a frame is written at its position less that start, and one
    /// at the start itself is dropped behind the piece's black frame.
    /// </summary>
    [Test]
    public void A_continued_piece_writes_from_its_own_start()
    {
        var time = new ManualTimeProvider(Start);
        time.Advance(TimeSpan.FromSeconds(10));
        var clock = new RecordingClock(time);
        clock.SetOffset(TimeSpan.FromMinutes(30));
        clock.Begin(Stamp(10));
        time.Advance(TimeSpan.FromSeconds(5));
        var placer = new FramePlacer(TimeSpan.FromMinutes(30), TimeSpan.Zero);

        var atStart = placer.Place(clock.Snapshot(), Stamp(10));
        var next = placer.Place(clock.Snapshot(), Stamp(10.04));

        Assert.That(atStart.Fate, Is.EqualTo(FrameFate.NotAfterLast));
        Assert.That(next, Is.EqualTo(new FramePlacement(FrameFate.Written, Seconds(0.04))));
    }

    /// <summary>A frame at or before the last written is dropped, and one under 25 ms after it too, so a camera at 60 frames a second keeps every other frame.</summary>
    [Test]
    public void A_frame_behind_or_too_close_to_the_last_is_dropped()
    {
        var (time, clock) = ClockFrom10();
        time.Advance(TimeSpan.FromSeconds(5));
        var placer = new FramePlacer(TimeSpan.Zero, TimeSpan.Zero);
        var snapshot = clock.Snapshot();

        var fates = Enumerable.Range(1, 8).Select(frame => placer.Place(snapshot, Stamp(10 + frame / 60.0)).Fate).ToList();
        var behind = placer.Place(snapshot, Stamp(10.05));

        Assert.That(fates, Is.EqualTo(new[] { FrameFate.Written, FrameFate.TooSoon, FrameFate.Written, FrameFate.TooSoon, FrameFate.Written, FrameFate.TooSoon, FrameFate.Written, FrameFate.TooSoon }));
        Assert.That(behind.Fate, Is.EqualTo(FrameFate.NotAfterLast));
    }

    /// <summary>A frame placed after the piece's end is dropped.</summary>
    [Test]
    public void A_frame_after_the_end_is_dropped()
    {
        var (time, clock) = ClockFrom10();
        time.Advance(TimeSpan.FromSeconds(5));
        var placer = new FramePlacer(TimeSpan.Zero, TimeSpan.Zero);

        var after = placer.Place(clock.Snapshot(), Stamp(13), end: Seconds(2));

        Assert.That(after.Fate, Is.EqualTo(FrameFate.AfterEnd));
    }

    /// <summary>A black frame for a lost camera is written only after the last frame, and then is the last.</summary>
    [Test]
    public void A_black_frame_comes_only_after_the_last()
    {
        var (time, clock) = ClockFrom10();
        time.Advance(TimeSpan.FromSeconds(5));
        var placer = new FramePlacer(TimeSpan.Zero, TimeSpan.Zero);
        placer.Place(clock.Snapshot(), Stamp(12));

        var behind = placer.PlaceBlack(Seconds(1));
        var black = placer.PlaceBlack(Seconds(4));

        Assert.That(behind.Fate, Is.EqualTo(FrameFate.NotAfterLast));
        Assert.That(black, Is.EqualTo(new FramePlacement(FrameFate.Written, Seconds(4))));
        Assert.That(placer.Last, Is.EqualTo(Seconds(4)));
        Assert.That(placer.Written, Is.EqualTo(1), "the black frame is no frame of the camera's");
    }

    /// <summary>
    /// A frame waits half a second before it is due, so that a pause's clock line arriving a quarter of a second after the
    /// pause still leaves its frames out; more than 15 waiting let the oldest go early; all are given at the stop.
    /// </summary>
    [Test]
    public void A_frame_waits_half_a_second_in_the_queue()
    {
        var queue = new FrameQueue(64 * 36 * 3 / 2, 24);
        var frame = new FakeCameraFrame(Stamp(1), 64, 36, 100);

        Assert.That(queue.Offer(frame, Stamp(1)), Is.EqualTo(100));
        Assert.That(queue.TryTakeDue(Stamp(1.4), all: false, out _), Is.False);
        Assert.That(queue.TryTakeDue(Stamp(1.5), all: false, out var due), Is.True);
        Assert.That(due, Is.EqualTo(due with { Stamp = Stamp(1), Arrival = Stamp(1), Brightness = 100 }));
        queue.Return(due.Pixels);

        for (var i = 0; i < 16; i++) queue.Offer(new FakeCameraFrame(Stamp(2 + i * 0.01), 64, 36, 100), Stamp(2 + i * 0.01));
        Assert.That(queue.TryTakeDue(Stamp(2.2), all: false, out var early), Is.True, "more than 15 waiting let the oldest go");
        Assert.That(early.Stamp, Is.EqualTo(Stamp(2)));
        Assert.That(queue.TryTakeDue(Stamp(2.2), all: false, out _), Is.False, "15 waiting wait their time");
        Assert.That(queue.TryTakeDue(Stamp(2.2), all: true, out _), Is.True, "all are given at the stop");
    }

    /// <summary>A frame that finds no free buffer is dropped and counted, as one of another size is; a buffer given back is used again.</summary>
    [Test]
    public void A_frame_without_a_free_buffer_is_dropped()
    {
        var queue = new FrameQueue(64 * 36 * 3 / 2, 2);

        Assert.That(queue.Offer(new FakeCameraFrame(Stamp(1), 64, 36, 50), Stamp(1)), Is.Not.Null);
        Assert.That(queue.Offer(new FakeCameraFrame(Stamp(1.03), 64, 36, 50), Stamp(1.03)), Is.Not.Null);
        Assert.That(queue.Offer(new FakeCameraFrame(Stamp(1.06), 64, 36, 50), Stamp(1.06)), Is.Null);
        Assert.That(queue.Offer(new FakeCameraFrame(Stamp(1.09), 32, 18, 50), Stamp(1.09)), Is.Null);
        Assert.That(queue.Dropped, Is.EqualTo(2));

        Assert.That(queue.TryTakeDue(Stamp(2), all: false, out var taken), Is.True);
        queue.Return(taken.Pixels);
        Assert.That(queue.Offer(new FakeCameraFrame(Stamp(2.1), 64, 36, 50), Stamp(2.1)), Is.Not.Null);
    }
}
