using ClassScribe.Camera;

namespace ClassScribe.Tests;

/// <summary>
/// The plan of a lesson's video (teach-video 6.9, slice 8), pure: which source's picture goes where, the pieces in position
/// order each at its start, an older video before the first piece, a piece cut where the next begins and at the sound's end,
/// a gap holding the last frame, the frames the check decodes, and the whole picture encoded again when any source's H.264
/// sequence header or size differs from the others'.
/// </summary>
[TestFixture]
public sealed class LessonVideoPlanTests
{
    /// <summary>The sequence header of the hardware encoder's pieces, as the tests make it up.</summary>
    private static readonly byte[] Hardware = [0, 0, 0, 1, 0x67, 0x4D, 0x40, 0x1F];

    /// <summary>The sequence header of Windows' software encoder's pieces, as the tests make it up: another header.</summary>
    private static readonly byte[] Software = [0, 0, 0, 1, 0x67, 0x4D, 0x00, 0x1F];

    /// <summary>A source at <paramref name="start"/>, <paramref name="seconds"/> long, 720p with <paramref name="header"/> unless given another size.</summary>
    private static LessonVideoSource Source(string name, double start, double seconds, byte[]? header = null, int width = 1280, int height = 720) =>
        new(name, TimeSpan.FromSeconds(start), new VideoSourceFacts(width, height, header ?? Hardware, TimeSpan.FromSeconds(seconds)));

    /// <summary>One piece from the lesson's start covers the whole sound: one slice, no join, passed through.</summary>
    [Test]
    public void One_piece_is_one_slice_passed_through()
    {
        var plan = LessonVideoPlan.Make([Source("a.cam.mp4", 0, 600)], older: null, TimeSpan.FromSeconds(600));

        Assert.That(plan.Slices, Is.EqualTo(new[] { new PlannedSlice("a.cam.mp4", TimeSpan.Zero, TimeSpan.FromSeconds(600)) }));
        Assert.That(plan.EncodeAgain, Is.False);
        Assert.That(plan.End, Is.EqualTo(TimeSpan.FromSeconds(600)));
        Assert.That(plan.Joins, Is.Empty);
        Assert.That(plan.Size, Is.EqualTo((1280, 720)));
    }

    /// <summary>
    /// Pieces go in position order, each at its start; one is cut where the next begins; a gap between them holds the last
    /// frame (the slices leave it, and the writer lets each frame last until the next); the last piece is cut at the sound's end.
    /// </summary>
    [Test]
    public void Pieces_go_in_position_order_each_cut_where_the_next_begins()
    {
        var plan = LessonVideoPlan.Make(
            [Source("c.cam@00-30-00.000.mp4", 1800, 1300), Source("a.cam.mp4", 0, 700), Source("b.cam@00-11-00.000.mp4", 660, 1200)],
            older: null, TimeSpan.FromSeconds(3000));

        Assert.That(plan.Slices, Is.EqualTo(new[]
        {
            new PlannedSlice("a.cam.mp4", TimeSpan.Zero, TimeSpan.FromSeconds(660)),
            new PlannedSlice("b.cam@00-11-00.000.mp4", TimeSpan.FromSeconds(660), TimeSpan.FromSeconds(1140)),
            new PlannedSlice("c.cam@00-30-00.000.mp4", TimeSpan.FromSeconds(1800), TimeSpan.FromSeconds(1200))
        }));
        Assert.That(plan.Joins, Is.EqualTo(new[] { TimeSpan.FromSeconds(660), TimeSpan.FromSeconds(1800) }));
    }

    /// <summary>A continued lesson whose first pieces were deleted takes an older video's picture before its first piece, cut where the piece begins.</summary>
    [Test]
    public void An_older_video_comes_before_the_first_piece()
    {
        var plan = LessonVideoPlan.Make([Source("b.cam@00-40-00.000.mp4", 2400, 1200)], Source("old.mp4", 0, 2410), TimeSpan.FromSeconds(3600));

        Assert.That(plan.Slices, Is.EqualTo(new[]
        {
            new PlannedSlice("old.mp4", TimeSpan.Zero, TimeSpan.FromSeconds(2400)),
            new PlannedSlice("b.cam@00-40-00.000.mp4", TimeSpan.FromSeconds(2400), TimeSpan.FromSeconds(1200))
        }));
        Assert.That(plan.EncodeAgain, Is.False, "the same encoder made both");
    }

    /// <summary>An older video is not needed when a piece begins at the lesson's start: the pieces are the picture, and the old video is replaced.</summary>
    [Test]
    public void An_older_video_is_not_used_when_the_pieces_cover_the_start()
    {
        var plan = LessonVideoPlan.Make([Source("a.cam.mp4", 0, 100), Source("b.cam@00-01-40.000.mp4", 100, 50)], Source("old.mp4", 0, 100), TimeSpan.FromSeconds(150));

        Assert.That(plan.Slices.Select(slice => slice.Path), Is.EqualTo(new[] { "a.cam.mp4", "b.cam@00-01-40.000.mp4" }));
    }

    /// <summary>A piece that begins at or after the sound's end adds nothing, and is left out of the plan.</summary>
    [Test]
    public void A_piece_after_the_sound_s_end_is_left_out()
    {
        var plan = LessonVideoPlan.Make([Source("a.cam.mp4", 0, 100), Source("b.cam@00-01-40.000.mp4", 100, 50)], older: null, TimeSpan.FromSeconds(100));

        Assert.That(plan.Slices.Select(slice => slice.Path), Is.EqualTo(new[] { "a.cam.mp4" }));
        Assert.That(plan.End, Is.EqualTo(TimeSpan.FromSeconds(100)));
    }

    /// <summary>
    /// A source whose sequence header differs, as after the software encoder took over, or whose size differs, means the
    /// whole picture is encoded again, at the newest piece's size, and the plan says why.
    /// </summary>
    [TestCase(true, false)]
    [TestCase(false, true)]
    public void A_header_or_a_size_that_differs_means_encoding_again(bool otherHeader, bool otherSize)
    {
        var plan = LessonVideoPlan.Make(
            [Source("a.cam.mp4", 0, 600), Source("b.cam@00-10-00.000.mp4", 600, 600, otherHeader ? Software : Hardware, otherSize ? 1920 : 1280, otherSize ? 1080 : 720)],
            older: null, TimeSpan.FromSeconds(1200));

        Assert.That(plan.EncodeAgain, Is.True);
        Assert.That(plan.WhyEncodeAgain, Does.Contain("b.cam@00-10-00.000.mp4"));
        Assert.That(plan.Size, Is.EqualTo(otherSize ? (1920, 1080) : (1280, 720)));
    }

    /// <summary>An older video made by another encoder means encoding again too.</summary>
    [Test]
    public void An_older_video_of_another_encoder_means_encoding_again()
    {
        var plan = LessonVideoPlan.Make([Source("b.cam@00-40-00.000.mp4", 2400, 1200)], Source("old.mp4", 0, 2400, Software), TimeSpan.FromSeconds(3600));

        Assert.That(plan.EncodeAgain, Is.True);
    }

    /// <summary>The check decodes a frame at the start, on each side of every join, and at the end.</summary>
    [Test]
    public void The_check_decodes_a_frame_at_the_start_each_side_of_every_join_and_the_end()
    {
        var plan = LessonVideoPlan.Make([Source("a.cam.mp4", 0, 700), Source("b.cam@00-11-00.000.mp4", 660, 1200)], older: null, TimeSpan.FromSeconds(1500));

        Assert.That(plan.FramesToCheck, Is.EqualTo(new[]
        {
            TimeSpan.Zero, TimeSpan.FromSeconds(660) - LessonVideoPlan.BeforeAJoin, TimeSpan.FromSeconds(660), TimeSpan.FromSeconds(1500) - LessonVideoPlan.BeforeAJoin
        }));
    }

    /// <summary>A plan needs a piece that adds something to the sound: none, or only pieces past its end, is refused in words.</summary>
    [Test]
    public void Without_a_piece_there_is_no_plan()
    {
        Assert.That(() => LessonVideoPlan.Make([], older: null, TimeSpan.FromSeconds(10)), Throws.InstanceOf<InvalidDataException>().With.Message.Contains("no camera piece"));
        Assert.That(() => LessonVideoPlan.Make([Source("a.cam@00-01-00.000.mp4", 60, 10)], older: null, TimeSpan.FromSeconds(30)),
            Throws.InstanceOf<InvalidDataException>());
        Assert.That(() => LessonVideoPlan.Make([Source("a.cam.mp4", 0, 10)], older: null, TimeSpan.Zero), Throws.InstanceOf<InvalidDataException>().With.Message.Contains("no sound"));
    }
}
