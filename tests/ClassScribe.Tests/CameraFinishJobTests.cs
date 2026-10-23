using ClassScribe.Audio;
using ClassScribe.Camera;
using ClassScribe.Storage;

namespace ClassScribe.Tests;

/// <summary>
/// The finish job (teach-video 6.9, slice 8), run by the real <see cref="CameraHost"/> over an in-memory pipe, its media work
/// played by <see cref="FakeLessonVideoMedia"/> and its time on the manual clock, over a lesson's real .wav files in a
/// temporary folder: the plan of the pieces, the sound mixed as the upload is at 48 kHz, the video written as .partial and
/// renamed only once it passes its check, an older video replaced only then, the pieces deleted after the check unless kept
/// for Drive, a check failed, a write failed, a cancel, a stale .partial, one being made elsewhere, no sound, no piece, a
/// piece that cannot be read, and progress said at least every ten seconds. Nothing is opened, encoded or decoded.
/// </summary>
[TestFixture]
public sealed class CameraFinishJobTests
{
    private static readonly string JobId = TestRuns.Id(11);
    private const string BaseName = "Tajwid 4_2026-10-19_14-00-02Z";

    private ManualTimeProvider _time = null!;
    private FakeLessonVideoMedia _media = null!;
    private TempFolder _folder = null!;
    private RecordingFiles _files = null!;

    /// <summary>A new clock, a new fake, and a lesson in a temporary folder with two seconds of sound at 48 kHz.</summary>
    [SetUp]
    public void SetUp()
    {
        _time = new ManualTimeProvider(TestRuns.Start);
        _media = new FakeLessonVideoMedia();
        _folder = new TempFolder();
        _files = new RecordingFiles(_folder.Path, BaseName);
        TestAudio.WriteWav(_files.Segment(1), CameraFinishJob.SoundRate, TestAudio.Tone(CameraFinishJob.SoundRate, 2, 440, 0.25));
    }

    /// <summary>Deletes the temporary folder.</summary>
    [TearDown]
    public void TearDown() => _folder.Dispose();

    /// <summary>Writes a piece of the lesson beginning at <paramref name="startSeconds"/>, and returns its path.</summary>
    private string Piece(double startSeconds)
    {
        var path = _files.CameraPiece(TimeSpan.FromSeconds(startSeconds));
        File.WriteAllBytes(path, [0, 0, 0, 24, (byte)'f', (byte)'t', (byte)'y', (byte)'p']);
        return path;
    }

    /// <summary>The finish job for the lesson, keeping its pieces when <paramref name="keepPieces"/>.</summary>
    private CameraJob Finish(bool keepPieces = false) =>
        new(CameraProtocol.Version, CameraJobKind.Finish, Folder: _folder.Path, BaseName: BaseName, KeepPieces: keepPieces);

    /// <summary>Starts the real camera host over <paramref name="process"/>'s own end with the fake media, reads its lines, and sends it <paramref name="job"/>.</summary>
    private async Task<(Task<int> Host, CameraLinesSeen Seen)> StartAsync(FakeCameraProcess process, CameraJob job)
    {
        var camera = new CameraHost { Time = _time, Media = _media };
        var host = Task.Run(() => camera.RunAsync(JobId, process.ProcessStream, process.ProcessStream));
        var seen = new CameraLinesSeen(process.Channel);
        await CameraLines.SendJobAsync(process.Channel, job, CancellationToken.None);
        return (host, seen);
    }

    /// <summary>Runs <paramref name="job"/> to its end, moving the clock while it waits, and returns its exit code and lines.</summary>
    private async Task<(int Exit, CameraLinesSeen Seen)> RunAsync(CameraJob job)
    {
        await using var process = new FakeCameraProcess();
        var (host, seen) = await StartAsync(process, job);
        await _time.AdvanceUntilAsync(() => host.IsCompleted, CameraFinishJob.LookEvery, timeoutSeconds: 30);
        var exit = await host;
        process.Exited(exit);
        await process.ProcessEnd.DisposeAsync();
        await seen.Reading.WaitAsync(TimeSpan.FromSeconds(10));
        return (exit, seen);
    }

    /// <summary>
    /// The video is made from the pieces in their order and the lesson's sound at 48 kHz, written as .partial, checked with the
    /// sound's length and the frames the plan names, renamed, and said with its length and size; the pieces are deleted after
    /// the check, with a line saying why; the job ends with 0.
    /// </summary>
    [Test]
    public async Task The_video_is_made_checked_renamed_and_the_pieces_deleted()
    {
        var first = Piece(0);
        var second = Piece(1.25);

        var (exit, seen) = await RunAsync(Finish());

        var plan = _media.Written.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(exit, Is.EqualTo(CameraProtocol.DoneExitCode));
            Assert.That(plan.Slices, Is.EqualTo(new[]
            {
                new PlannedSlice(first, TimeSpan.Zero, TimeSpan.FromSeconds(1.25)), new PlannedSlice(second, TimeSpan.FromSeconds(1.25), TimeSpan.FromSeconds(0.75))
            }));
            Assert.That(plan.EncodeAgain, Is.False);
            Assert.That(_media.Checks.Single(), Is.EqualTo(new LessonVideoCheck(TimeSpan.FromSeconds(2), plan.FramesToCheck)).Using<LessonVideoCheck>(SameCheck));
            Assert.That((_media.SoundRate, _media.SoundSamples), Is.EqualTo((48_000, 96_000L)));
            Assert.That(File.Exists(_files.LessonVideo), Is.True);
            Assert.That(File.Exists(_files.LessonVideoPartial), Is.False);
            Assert.That(seen.Of<CameraMade>().Single(), Is.EqualTo(new CameraMade(_files.LessonVideo, 2, new FileInfo(_files.LessonVideo).Length)));
            Assert.That(_files.FindCameraPieces(), Is.Empty);
            Assert.That(seen.Logs, Has.Member($"Deleted the camera's 2 pieces of {BaseName}: the lesson's video passed its check, and lesson videos do not go to Google Drive (Drive:LessonVideos), so the video is the one copy of the picture kept"));
            Assert.That(seen.Logs, Has.Member($"Making {BaseName}.mp4 from the camera's 2 pieces and the lesson's sound (00:00:02), 1280x720, the picture passed through as it is"));
            Assert.That(seen.Of<CameraProgress>().Select(progress => progress.Percent), Does.Contain(0).And.Contain(99));
            Assert.That(seen.Of<CameraFailed>(), Is.Empty);
        }
    }

    /// <summary>Two checks are the same when they ask for the same length and the same frames.</summary>
    private static bool SameCheck(LessonVideoCheck first, LessonVideoCheck second) => first.Length == second.Length && first.FramesAt.SequenceEqual(second.FramesAt);

    /// <summary>A video meant for Google Drive keeps its pieces until free-space finds it there, which a line says.</summary>
    [Test]
    public async Task Pieces_kept_for_Drive_stay_with_a_line()
    {
        Piece(0);

        var (exit, seen) = await RunAsync(Finish(keepPieces: true));

        Assert.That(exit, Is.EqualTo(CameraProtocol.DoneExitCode));
        Assert.That(_files.FindCameraPieces(), Has.Count.EqualTo(1));
        Assert.That(seen.Logs, Has.Member($"The camera's 1 piece of {BaseName} stay on this laptop until free-space finds the video safe on Google Drive (Drive:LessonVideos)"));
    }

    /// <summary>A piece from the software encoder, with another sequence header, has the whole picture encoded again, which a line says.</summary>
    [Test]
    public async Task A_piece_of_another_encoder_has_the_picture_encoded_again()
    {
        Piece(0);
        var second = Piece(1);
        _media.Holds(Path.GetFileName(second), _media.Default with { SequenceHeader = FakeLessonVideoMedia.SoftwareHeader });

        var (exit, seen) = await RunAsync(Finish());

        Assert.That(exit, Is.EqualTo(CameraProtocol.DoneExitCode));
        Assert.That(_media.Written.Single().EncodeAgain, Is.True);
        Assert.That(seen.Logs, Has.Some.EndsWith("so the whole picture is encoded again, which takes longer than joining the pieces as they are"));
    }

    /// <summary>
    /// A video that fails its check is deleted, an older one left as it was, the pieces kept, and failed says why and how to
    /// make it again for free; the job ends with 1.
    /// </summary>
    [Test]
    public async Task A_video_that_fails_its_check_is_deleted_and_the_pieces_kept()
    {
        Piece(0);
        File.WriteAllText(_files.LessonVideo, "the older video");
        _media.CheckAnswer = "no frame decodes at 00:00:01.250";

        var (exit, seen) = await RunAsync(Finish());

        var failed = seen.Of<CameraFailed>().Single().Why;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(exit, Is.EqualTo(CameraProtocol.FailedExitCode));
            Assert.That(File.Exists(_files.LessonVideoPartial), Is.False);
            Assert.That(File.ReadAllText(_files.LessonVideo), Is.EqualTo("the older video"), "the older video is replaced only by one that passed");
            Assert.That(_files.FindCameraPieces(), Has.Count.EqualTo(1));
            Assert.That(failed, Does.StartWith("The lesson's video did not pass its check (no frame decodes at 00:00:01.250). The camera's pieces are kept. To make it again, free: "));
            Assert.That(failed, Does.Contain($"finish-video \"{_files.Segment(1)}\""));
            Assert.That(seen.Of<CameraMade>(), Is.Empty);
        }
    }

    /// <summary>An older video is replaced only once the new one has passed its check: while it is written the older stays as it is.</summary>
    [Test]
    public async Task An_older_video_is_replaced_only_once_the_new_one_passed()
    {
        Piece(0);
        File.WriteAllText(_files.LessonVideo, "the older video");
        using var gate = new ManualResetEventSlim();
        _media.HoldWrite = gate;
        await using var process = new FakeCameraProcess();
        var (host, seen) = await StartAsync(process, Finish());

        await Wait.UntilAsync(() => _media.Writing);
        Assert.That(File.ReadAllText(_files.LessonVideo), Is.EqualTo("the older video"));
        Assert.That(ClassFolders.IsLessonVideoBeingMade(_files), Is.True, "the .partial is held open while it is written");

        gate.Set();
        await _time.AdvanceUntilAsync(() => host.IsCompleted, CameraFinishJob.LookEvery, timeoutSeconds: 30);

        Assert.That(await host, Is.EqualTo(CameraProtocol.DoneExitCode));
        Assert.That(File.ReadAllText(_files.LessonVideo), Is.EqualTo("a lesson's video"));
    }

    /// <summary>A continued lesson whose first pieces were deleted takes the older video's picture before its first piece.</summary>
    [Test]
    public async Task A_continued_lesson_takes_the_older_video_before_its_first_piece()
    {
        var piece = Piece(1.5);
        File.WriteAllText(_files.LessonVideo, "the older video");

        var (exit, _) = await RunAsync(Finish());

        Assert.That(exit, Is.EqualTo(CameraProtocol.DoneExitCode));
        Assert.That(_media.Written.Single().Slices, Is.EqualTo(new[]
        {
            new PlannedSlice(_files.LessonVideo, TimeSpan.Zero, TimeSpan.FromSeconds(1.5)), new PlannedSlice(piece, TimeSpan.FromSeconds(1.5), TimeSpan.FromSeconds(0.5))
        }));
    }

    /// <summary>A stale .partial, which nothing holds, is deleted first, with a line; one held open is another making, and the job is refused.</summary>
    [Test]
    public async Task A_stale_partial_is_deleted_and_a_held_one_refuses_the_job()
    {
        Piece(0);
        File.WriteAllText(_files.LessonVideoPartial, "left by a crash");

        var (exit, seen) = await RunAsync(Finish());

        Assert.That(exit, Is.EqualTo(CameraProtocol.DoneExitCode));
        Assert.That(seen.Logs, Has.Member($"Deleted {BaseName}.mp4.partial, an unfinished video that an earlier making left"));

        Piece(0);
        using (new FileStream(_files.LessonVideoPartial, FileMode.Create, FileAccess.Write, FileShare.Read))
        {
            var (refused, again) = await RunAsync(Finish());
            Assert.That(refused, Is.EqualTo(CameraProtocol.FailedExitCode));
            Assert.That(again.Of<CameraFailed>().Single().Why, Does.StartWith($"The video of {BaseName} is being made already, by another ClassScribe"));
        }
    }

    /// <summary>Without the lesson's .wav files, as after free-space, the video cannot be made here: failed says so, exit 1, nothing written.</summary>
    [Test]
    public async Task Without_the_sound_the_video_cannot_be_made_here()
    {
        Piece(0);
        File.Delete(_files.Segment(1));

        var (exit, seen) = await RunAsync(Finish());

        Assert.That(exit, Is.EqualTo(CameraProtocol.FailedExitCode));
        Assert.That(seen.Of<CameraFailed>().Single().Why, Does.StartWith($"The sound of {BaseName} is not on this laptop, so its video cannot be made here. The camera's pieces are kept"));
        Assert.That(_media.Written, Is.Empty);
    }

    /// <summary>Without a piece that can be read there is no picture: failed says so, exit 1; a piece that cannot be read beside others is left out with a line.</summary>
    [Test]
    public async Task A_piece_that_cannot_be_read_is_left_out()
    {
        var bad = Piece(0);
        _media.Unreadable(Path.GetFileName(bad));

        var (alone, aloneSeen) = await RunAsync(Finish());
        Assert.That(alone, Is.EqualTo(CameraProtocol.FailedExitCode));
        Assert.That(aloneSeen.Of<CameraFailed>().Single().Why, Does.StartWith($"{BaseName} has no camera piece that can be read"));

        var good = Piece(0.5);
        var (exit, seen) = await RunAsync(Finish());

        Assert.That(exit, Is.EqualTo(CameraProtocol.DoneExitCode));
        Assert.That(_media.Written.Single().Slices.Select(slice => slice.Path), Is.EqualTo(new[] { good }));
        Assert.That(seen.Logs, Has.Member($"{Path.GetFileName(bad)} cannot be read (it holds no H.264 picture), so it is left out of the lesson's video"));
    }

    /// <summary>A lesson without any piece has no picture to make a video of.</summary>
    [Test]
    public async Task A_lesson_without_pieces_has_no_video_to_make()
    {
        var (exit, seen) = await RunAsync(Finish());

        Assert.That(exit, Is.EqualTo(CameraProtocol.FailedExitCode));
        Assert.That(seen.Of<CameraFailed>().Single().Why, Does.StartWith($"{BaseName} has no camera piece that can be read"));
    }

    /// <summary>The starter's cancel, as Ctrl+C after the stop, deletes the unfinished video and keeps the pieces; the job ends with 130.</summary>
    [Test]
    public async Task A_cancel_deletes_the_partial_and_keeps_the_pieces()
    {
        Piece(0);
        using var gate = new ManualResetEventSlim();
        _media.HoldWrite = gate;
        await using var process = new FakeCameraProcess();
        var (host, seen) = await StartAsync(process, Finish());
        await Wait.UntilAsync(() => _media.Writing);

        await CameraLines.SendSignalAsync(process.Channel, CameraProtocol.Cancel, CancellationToken.None);
        await _time.AdvanceUntilAsync(() => host.IsCompleted, CameraFinishJob.LookEvery, timeoutSeconds: 30);

        Assert.That(await host, Is.EqualTo(CameraProtocol.CancelledExitCode));
        Assert.That(File.Exists(_files.LessonVideoPartial), Is.False);
        Assert.That(File.Exists(_files.LessonVideo), Is.False);
        Assert.That(_files.FindCameraPieces(), Has.Count.EqualTo(1));
        await Wait.UntilAsync(() => seen.Logs.Contains($"The making of {BaseName}.mp4 was cancelled; the camera's pieces are kept"));
    }

    /// <summary>A write that fails, as a full disk, deletes the unfinished video and keeps the pieces; failed says why, exit 1.</summary>
    [Test]
    public async Task A_write_that_fails_keeps_the_pieces()
    {
        Piece(0);
        _media.WriteFailure = new IOException("There is not enough space on the disk.");

        var (exit, seen) = await RunAsync(Finish());

        Assert.That(exit, Is.EqualTo(CameraProtocol.FailedExitCode));
        Assert.That(File.Exists(_files.LessonVideoPartial), Is.False);
        Assert.That(_files.FindCameraPieces(), Has.Count.EqualTo(1));
        Assert.That(seen.Of<CameraFailed>().Single().Why, Does.StartWith("The lesson's video could not be written (There is not enough space on the disk"));
    }

    /// <summary>While the writing takes its time, a progress line goes out at least every ten seconds, so that the starter's limit never ends it.</summary>
    [Test]
    public async Task Progress_is_said_at_least_every_ten_seconds()
    {
        Piece(0);
        using var gate = new ManualResetEventSlim();
        _media.HoldWrite = gate;
        await using var process = new FakeCameraProcess();
        var (host, seen) = await StartAsync(process, Finish());
        await _time.AdvanceUntilAsync(() => _media.Writing && seen.Of<CameraProgress>().Any(progress => progress.Percent == 50), CameraFinishJob.LookEvery, timeoutSeconds: 30);
        var before = seen.Of<CameraProgress>().Count;

        await _time.AdvanceUntilAsync(() => seen.Of<CameraProgress>().Count > before, CameraFinishJob.LookEvery, timeoutSeconds: 30);

        Assert.That(seen.Of<CameraProgress>()[^1].Percent, Is.EqualTo(50), "the same percent, said again");
        gate.Set();
        await _time.AdvanceUntilAsync(() => host.IsCompleted, CameraFinishJob.LookEvery, timeoutSeconds: 30);
        Assert.That(await host, Is.EqualTo(CameraProtocol.DoneExitCode));
    }

    /// <summary>The video's sound is the tracks mixed as the upload is: the microphone's added when it was heard, left out with a line when it stayed silent.</summary>
    [TestCase(true)]
    [TestCase(false)]
    public async Task The_sound_is_the_tracks_mixed_as_the_upload_is(bool microphoneHeard)
    {
        Piece(0);
        TestAudio.WriteWav(_files.Segment(1, Track.Microphone), CameraFinishJob.SoundRate,
            microphoneHeard ? TestAudio.Tone(CameraFinishJob.SoundRate, 2, 440, 0.25) : new short[2 * CameraFinishJob.SoundRate]);

        var (exit, seen) = await RunAsync(Finish());

        Assert.That(exit, Is.EqualTo(CameraProtocol.DoneExitCode));
        Assert.That(_media.SoundPeak, microphoneHeard ? Is.EqualTo(0.5f).Within(0.01f) : Is.EqualTo(0.25f).Within(0.01f));
        Assert.That(seen.Logs.Contains("Your microphone stayed silent; the video's sound is the main track's alone"), Is.EqualTo(!microphoneHeard));
    }
}
