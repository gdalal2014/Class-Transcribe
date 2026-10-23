using ClassScribe.Commands;
using ClassScribe.Drive;
using ClassScribe.Recorder;
using ClassScribe.Storage;

namespace ClassScribe.Tests;

/// <summary>
/// A lesson's video and Google Drive (teach-video 8, build plan amendments 5 and 47, Gulzar's answers N13, N18 and N19): the
/// video goes up only with Drive:LessonVideos on, which is off by default, and Drive:Upload sending audio; streamed, typed
/// video/mp4, last; its .partial and the camera's pieces never. The pieces are kept for Drive only when the video goes there,
/// and free-space deletes them once the video's copy on Drive is checked, never the video. Drive waits while a class is
/// transcribed, as it does while one records. Nothing leaves the process.
/// </summary>
[TestFixture]
public sealed class LessonVideoDriveTests
{
    private static readonly DateTimeOffset LessonStart = new(2026, 10, 19, 14, 0, 2, TimeSpan.Zero);
    private GoogleWorld _world = null!;
    private RecordingFiles _lesson = null!;

    /// <summary>A signed-in world and a lesson with its sound, transcript, log, two camera pieces and its finished video.</summary>
    [SetUp]
    public void SetUp()
    {
        _world = new GoogleWorld();
        _world.SignedIn();
        _lesson = _world.WriteRecording(@"Tajwid\2026-10-19", "Tajwid", LessonStart, extras: [".md", ".log"]);
        File.WriteAllBytes(_lesson.CameraPiece(TimeSpan.Zero), Bytes(300_000, 1));
        File.WriteAllBytes(_lesson.CameraPiece(TimeSpan.FromMinutes(20)), Bytes(200_000, 2));
        File.WriteAllBytes(_lesson.LessonVideo, Bytes(3 * GoogleWorld.PieceSize + 5000, 3));
    }

    /// <summary>Deletes the world's folders.</summary>
    [TearDown]
    public void TearDown() => _world.Dispose();

    /// <summary><paramref name="count"/> bytes, a pattern seeded by <paramref name="seed"/>, so that no two files are alike.</summary>
    private static byte[] Bytes(int count, int seed) => [.. Enumerable.Range(0, count).Select(i => (byte)((i * 7 + seed) % 251))];

    /// <summary>Sets Drive:LessonVideos, and Drive:Upload as <paramref name="upload"/> says.</summary>
    private void LessonVideos(bool on, DriveUploadScope upload = DriveUploadScope.Everything) =>
        _world.Options.Set(new DriveOptions { LessonVideos = on, Upload = upload });

    /// <summary>Uploads the lesson, failing the test rather than waiting for ever.</summary>
    private Task<RecordingUploadResult> UploadAsync() =>
        _world.RecordingUpload().UploadAsync(_lesson, _ => Task.CompletedTask, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));

    /// <summary>The names of the files whose upload was started, in order.</summary>
    private List<string> Started() =>
        [.. _world.Google.Requests.Where(request => request.IsUploadStart).Select(request => JsonText.Object(request.Body)["name"]!.GetValue<string>())];

    /// <summary>Lesson videos stay off Drive by default: everything else of the lesson goes up, and neither the video, its .partial nor a piece.</summary>
    [Test]
    public async Task By_default_the_lesson_video_stays_on_this_laptop()
    {
        File.WriteAllText(_lesson.LessonVideoPartial, "half made");

        var result = await UploadAsync();

        var b = _lesson.BaseName;
        Assert.That(result.Outcome, Is.EqualTo(RecordingOutcome.Done));
        Assert.That(Started(), Is.EqualTo(new[] { $"{b}.md", $"{b}.wav", $"{b}.log" }));
        Assert.That(new DriveOptions().LessonVideos, Is.False, "off by default (N19 C)");
    }

    /// <summary>With Drive:LessonVideos on, the video goes up last, after the log, as the very bytes, typed video/mp4; its pieces and .partial still never.</summary>
    [Test]
    public async Task With_lesson_videos_on_the_video_goes_up_last_as_video_mp4()
    {
        LessonVideos(on: true);
        File.WriteAllText(_lesson.LessonVideoPartial, "half made");

        var result = await UploadAsync();

        var b = _lesson.BaseName;
        Assert.That(result.Outcome, Is.EqualTo(RecordingOutcome.Done));
        Assert.That(Started(), Is.EqualTo(new[] { $"{b}.md", $"{b}.wav", $"{b}.log", $"{b}.mp4" }));
        var video = _world.Google.FilesIn(_world.Google.FolderAt("Tajwid/2026-10-19")!.Id).Single(file => file.Name == $"{b}.mp4");
        Assert.That(video.Content, Is.EqualTo(File.ReadAllBytes(_lesson.LessonVideo)));
        Assert.That(video.MimeType, Is.EqualTo("video/mp4"));
        Assert.That(_world.Manifests().Read(_lesson)!.File($"{b}.mp4")!.Seconds, Is.Null, "a video is not measured as a .wav is");
    }

    /// <summary>Drive:Upload WithoutAudio keeps the video off Drive too, since it holds the same sound.</summary>
    [Test]
    public async Task Without_audio_the_video_stays_too()
    {
        LessonVideos(on: true, DriveUploadScope.WithoutAudio);

        await UploadAsync();

        Assert.That(Started(), Is.EqualTo(new[] { $"{_lesson.BaseName}.md", $"{_lesson.BaseName}.log" }));
    }

    /// <summary>As the video begins to go up, a line says its size; with no speed known yet, no time is guessed.</summary>
    [Test]
    public async Task A_line_says_when_the_video_begins_to_go_up()
    {
        LessonVideos(on: true);

        await UploadAsync();

        var size = DriveCommand.Size(new FileInfo(_lesson.LessonVideo).Length);
        Assert.That(_world.Lines, Has.Member($"Sending the lesson's video ({size}) to Google Drive."));
    }

    /// <summary>The time the video's line gives is worked out from the speed reached on the recording's earlier files, in whole minutes, at least one.</summary>
    [TestCase(2_400L * 1024 * 1024, 1_310_720.0, "Sending the lesson's video (2.3 GB) to Google Drive; at this connection's speed so far, about 32 minutes.")]
    [TestCase(10L * 1024 * 1024, 1_310_720.0, "Sending the lesson's video (10 MB) to Google Drive; at this connection's speed so far, about 1 minute.")]
    [TestCase(10L * 1024 * 1024, null, "Sending the lesson's video (10 MB) to Google Drive.")]
    public void The_video_line_gives_a_time_from_the_speed_so_far(long bytes, double? bytesPerSecond, string line) =>
        Assert.That(RecordingUpload.SendingTheVideo(bytes, bytesPerSecond), Is.EqualTo(line));

    /// <summary>
    /// A lesson's video made again since its copy went up (a continued lesson's, by finish-video) goes up whole as a new
    /// revision of that copy, as teach-video 8 asks, smaller or larger: the same file on Drive, never a second one, its bytes
    /// now the new video's and checked, which is said; free-space then finds the copy the same and frees the pieces.
    /// </summary>
    [TestCase(2)]
    [TestCase(5)]
    public async Task A_video_made_again_goes_up_as_a_new_revision_of_its_copy(int pieces)
    {
        LessonVideos(on: true);
        await UploadAsync();
        var b = _lesson.BaseName;
        var folder = _world.Google.FolderAt("Tajwid/2026-10-19")!.Id;
        var copy = _world.Google.FilesIn(folder).Single(file => file.Name == $"{b}.mp4");
        File.WriteAllBytes(_lesson.LessonVideo, Bytes(pieces * GoogleWorld.PieceSize + 7000, 5));

        var result = await UploadAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Outcome, Is.EqualTo(RecordingOutcome.Done));
            Assert.That(_world.Google.FilesIn(folder).Where(file => file.Name == $"{b}.mp4").Select(file => file.Id), Is.EqualTo(new[] { copy.Id }), "a new revision, not a second file");
            Assert.That(copy.Content, Is.EqualTo(File.ReadAllBytes(_lesson.LessonVideo)));
            Assert.That(copy.Revisions, Is.EqualTo(2));
            Assert.That(_world.Lines, Has.Member($"{b}.mp4 was made again since its copy went up to Google Drive, so it goes up whole, as a new revision of that copy"));
            Assert.That(_world.Google.Destructive, Is.Empty);
        }

        var freed = await _world.Freer().FreeAsync(_lesson, CancellationToken.None);

        Assert.That(freed.Problem, Is.Null);
        Assert.That(_lesson.FindCameraPieces(), Is.Empty);
    }

    /// <summary>A lesson whose video is being made, its .partial held open, waits to go up.</summary>
    [Test]
    public async Task A_lesson_whose_video_is_being_made_waits()
    {
        LessonVideos(on: true);
        using var making = new FileStream(_lesson.LessonVideoPartial, FileMode.Create, FileAccess.Write, FileShare.Read);

        var result = await UploadAsync();

        Assert.That(result.Outcome, Is.EqualTo(RecordingOutcome.InProgress));
        Assert.That(result.Problem, Is.EqualTo($"{_lesson.BaseName} is having its lesson video made now"));
        Assert.That(Started(), Is.Empty);
    }

    /// <summary>A waiting recording is described without its video's .partial, which never goes up.</summary>
    [Test]
    public void A_waiting_lesson_is_described_without_its_partial()
    {
        var (count, _) = UploadManifests.OnDisk(_lesson);
        File.WriteAllText(_lesson.LessonVideoPartial, "half made");

        Assert.That(UploadManifests.OnDisk(_lesson).Count, Is.EqualTo(count));
    }

    /// <summary>
    /// While a class is transcribed, or a lesson's video made, in a recorder or a window, the pass waits before each piece as
    /// it does while one records (N13), says so once, and goes on once it is done.
    /// </summary>
    [Test]
    public async Task The_pass_waits_while_a_class_is_transcribed()
    {
        _world.Runtime.Processes.Start(TestRuns.ProcessId, TestRuns.ProcessStart);
        _world.Runtime.Journal.Begin(TestRuns.Record(TestRuns.Id(), RunState.Transcribing, name: "Kalam"));

        var pass = _world.Pass().RunAsync([_lesson], everythingWaiting: false, CancellationToken.None);
        await Wait.UntilAsync(() => _world.Lines.Contains("Google Drive: paused while \"Kalam\" is transcribed"));
        var made = _world.Time.TimersMade;
        _world.Time.Advance(DriveUploadPass.PausePoll);
        await Wait.UntilAsync(() => _world.Time.TimersMade > made && _world.Time.UntilNextTimer == DriveUploadPass.PausePoll);

        Assert.That(_world.Google.Requests.Where(request => request.IsPiece), Is.Empty);

        _world.Runtime.Processes.End(TestRuns.ProcessId);
        await _world.Time.AdvanceUntilAsync(() => pass.IsCompleted, TimeSpan.FromSeconds(1));

        Assert.That((await pass).AllDone, Is.True);
        Assert.That(_world.Lines, Has.Member("Google Drive: going on, now that \"Kalam\" is done"));
    }

    /// <summary>
    /// With the video on Drive, free-space deletes the camera's pieces with the .wav files once the video's copy there is
    /// checked again, and never the video itself; the line names what went.
    /// </summary>
    [Test]
    public async Task Free_space_deletes_the_pieces_once_the_video_is_safe_on_Drive()
    {
        LessonVideos(on: true);
        await UploadAsync();
        var pieces = new FileInfo(_lesson.CameraPiece(TimeSpan.Zero)).Length + new FileInfo(_lesson.CameraPiece(TimeSpan.FromMinutes(20))).Length;
        var wav = new FileInfo(_lesson.Segment(1)).Length;

        var freed = await _world.Freer().FreeAsync(_lesson, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(freed.Problem, Is.Null);
            Assert.That(_lesson.FindCameraPieces(), Is.Empty);
            Assert.That(File.Exists(_lesson.LessonVideo), Is.True, "the finished video is never freed");
            Assert.That(File.Exists(_lesson.Segment(1)), Is.False);
            Assert.That(freed.Deleted, Is.EquivalentTo(new[]
            {
                Path.GetFileName(_lesson.Segment(1)), Path.GetFileName(_lesson.CameraPiece(TimeSpan.Zero)), Path.GetFileName(_lesson.CameraPiece(TimeSpan.FromMinutes(20)))
            }));
            Assert.That(freed.Bytes, Is.EqualTo(wav + pieces));
            Assert.That(_world.Lines, Has.Some.StartsWith($"Freed the camera's 2 pieces of {_lesson.BaseName} ({DriveCommand.Size(pieces)}): its lesson video is on Google Drive, checked"));
        }
    }

    /// <summary>Without the video on Drive, free-space keeps the pieces: they are the only other copy of the picture.</summary>
    [Test]
    public async Task Free_space_keeps_the_pieces_while_the_video_is_not_on_Drive()
    {
        await UploadAsync();

        var freed = await _world.Freer().FreeAsync(_lesson, CancellationToken.None);

        Assert.That(freed.Problem, Is.Null);
        Assert.That(_lesson.FindCameraPieces(), Has.Count.EqualTo(2));
        Assert.That(File.Exists(_lesson.Segment(1)), Is.False, "the audio is freed as before");
    }

    /// <summary>
    /// A continued lesson whose video went to Drive before its next part was recorded: the newest piece is newer than the
    /// video, so its picture is in no video yet. Free-space deletes nothing of the lesson, neither a piece nor its sound,
    /// which making the video needs, and says so with the command that makes it.
    /// </summary>
    [Test]
    public async Task Free_space_keeps_a_lesson_whose_video_is_still_to_be_made()
    {
        LessonVideos(on: true);
        await UploadAsync();
        var part2 = _lesson.CameraPiece(TimeSpan.FromMinutes(40));
        File.WriteAllBytes(part2, Bytes(200_000, 4));
        File.SetLastWriteTimeUtc(part2, File.GetLastWriteTimeUtc(_lesson.LessonVideo).AddMinutes(5));
        Assert.That(ClassFolders.LessonVideoStateOf(_lesson), Is.EqualTo(LessonVideoState.NotMade), "part 2's picture is in no video");

        var freed = await _world.Freer().FreeAsync(_lesson, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(freed.Deleted, Is.Empty);
            Assert.That(freed.Bytes, Is.Zero);
            Assert.That(_lesson.FindCameraPieces(), Has.Count.EqualTo(3));
            Assert.That(File.Exists(_lesson.Segment(1)), Is.True, "the sound stays to make the video with");
            Assert.That(freed.Problem, Is.EqualTo(LocalAudioFreer.VideoStillToMake(_lesson)));
            Assert.That(freed.Problem, Does.Contain(CommandLine.FinishVideo(_lesson.Segment(1))));
        }
    }

    /// <summary>
    /// Only the pieces the video on Drive holds are freed, those not newer than it; a newer one stays, said by name, as when the
    /// sound has gone already and the video cannot be made again here.
    /// </summary>
    [Test]
    public async Task Free_space_deletes_only_the_pieces_its_video_holds()
    {
        LessonVideos(on: true);
        await UploadAsync();
        File.Delete(_lesson.Segment(1));
        var part2 = _lesson.CameraPiece(TimeSpan.FromMinutes(40));
        File.WriteAllBytes(part2, Bytes(200_000, 4));
        File.SetLastWriteTimeUtc(part2, File.GetLastWriteTimeUtc(_lesson.LessonVideo).AddMinutes(5));

        var freed = await _world.Freer().FreeAsync(_lesson, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(File.Exists(part2), Is.True, "its picture is in no video");
            Assert.That(freed.Deleted, Is.EquivalentTo(new[] { Path.GetFileName(_lesson.CameraPiece(TimeSpan.Zero)), Path.GetFileName(_lesson.CameraPiece(TimeSpan.FromMinutes(20))) }));
            Assert.That(freed.Problem, Is.EqualTo($"{Path.GetFileName(part2)} is newer than the lesson video of {_lesson.BaseName}, which does not hold it, so it stays on this laptop"));
        }
    }

    /// <summary>A video whose copy on Drive no longer matches keeps the pieces, and says why; the audio is freed as before.</summary>
    [Test]
    public async Task A_video_that_differs_from_its_copy_keeps_the_pieces()
    {
        LessonVideos(on: true);
        await UploadAsync();
        File.WriteAllBytes(_lesson.LessonVideo, Bytes(4000, 9));

        var freed = await _world.Freer().FreeAsync(_lesson, CancellationToken.None);

        Assert.That(_lesson.FindCameraPieces(), Has.Count.EqualTo(2));
        Assert.That(freed.Problem, Does.StartWith($"The camera's pieces of {_lesson.BaseName} stay: {_lesson.BaseName}.mp4 differs from its copy on Google Drive"));
    }

    /// <summary>
    /// The pieces are kept for Drive only when the video goes there: Drive set up, Drive:LessonVideos on and Drive:Upload
    /// sending audio (amendment 47, N18 B); in every other case, the default included, they are deleted once the video passes
    /// its check.
    /// </summary>
    [TestCase(true, true, DriveUploadScope.Everything, true)]
    [TestCase(true, false, DriveUploadScope.Everything, false)]
    [TestCase(true, true, DriveUploadScope.WithoutAudio, false)]
    [TestCase(false, true, DriveUploadScope.Everything, false)]
    public void The_pieces_are_kept_only_for_a_video_that_goes_to_Drive(bool setUp, bool lessonVideos, DriveUploadScope upload, bool kept)
    {
        if (!setUp) _world.Store.Stored = null;
        LessonVideos(lessonVideos, upload);

        Assert.That(new LessonVideoDrive(_world.Store, _world.Options).KeepsPieces, Is.EqualTo(kept));
    }

    /// <summary>
    /// At a lesson's start with Drive set up, a line says where its video goes, and with Drive:LessonVideos off how to turn it
    /// on (N19 C); without Drive nothing is said.
    /// </summary>
    [Test]
    public void A_lesson_s_start_says_where_its_video_goes()
    {
        Assert.That(new LessonVideoDrive(_world.Store, _world.Options).StartLine(), Is.EqualTo(LessonVideoDrive.StaysHereLine));

        LessonVideos(on: true);
        Assert.That(new LessonVideoDrive(_world.Store, _world.Options).StartLine(), Is.EqualTo(LessonVideoDrive.GoesUpLine));

        LessonVideos(on: true, DriveUploadScope.WithoutAudio);
        Assert.That(new LessonVideoDrive(_world.Store, _world.Options).StartLine(), Is.EqualTo(LessonVideoDrive.WithoutAudioLine));

        _world.Store.Stored = null;
        Assert.That(new LessonVideoDrive(_world.Store, _world.Options).StartLine(), Is.Null);
        Assert.That(LessonVideoDrive.StaysHereLine, Does.Contain("settings set Drive:LessonVideos \"true\""));
    }
}
