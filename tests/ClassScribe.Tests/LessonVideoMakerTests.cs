using ClassScribe.Camera;
using ClassScribe.Storage;

namespace ClassScribe.Tests;

/// <summary>
/// The maker of a lesson's video (teach-video 6.2 and 6.9, slice 8), the starter's side of the finish job, which the
/// recorder, finish-video and the Vs share: over <see cref="FakeCameraProcessStarter"/>, running the real camera host with
/// <see cref="FakeLessonVideoMedia"/>, or a process the test plays, on the manual clock. The video made and said; a failure
/// said with how to make it again; a process silent for two minutes ended; a cancel sent, and a process that does not end
/// at it ended; a process that does not start. Nothing is started for real.
/// </summary>
[TestFixture]
public sealed class LessonVideoMakerTests
{
    private const string BaseName = "Tajwid 4_2026-10-19_14-00-02Z";
    private ManualTimeProvider _time = null!;
    private FakeLessonVideoMedia _media = null!;
    private TempFolder _folder = null!;
    private RecordingFiles _files = null!;
    private TestLoggerFactory _logs = null!;

    /// <summary>A lesson with two seconds of sound and a piece, in a temporary folder, a new clock, fake and log.</summary>
    [SetUp]
    public void SetUp()
    {
        _time = new ManualTimeProvider(TestRuns.Start);
        _media = new FakeLessonVideoMedia();
        _folder = new TempFolder();
        _files = new RecordingFiles(_folder.Path, BaseName);
        _logs = new TestLoggerFactory();
        TestAudio.WriteWav(_files.Segment(1), CameraFinishJob.SoundRate, TestAudio.Tone(CameraFinishJob.SoundRate, 2, 440, 0.25));
        File.WriteAllBytes(_files.CameraPiece(TimeSpan.Zero), [1, 2, 3]);
    }

    /// <summary>Deletes the temporary folder and the log.</summary>
    [TearDown]
    public void TearDown()
    {
        _logs.Dispose();
        _folder.Dispose();
    }

    /// <summary>A starter whose every process runs the real camera host with the fake media, on the test's clock.</summary>
    private FakeCameraProcessStarter RealHost() => new()
    {
        Run = (process, jobId) => new CameraHost { Time = _time, Media = _media }.RunAsync(jobId, process.ProcessStream, process.ProcessStream)
    };

    /// <summary>Makes the lesson's video with <paramref name="starter"/>, moving the clock until it is done.</summary>
    private async Task<LessonVideoResult> MakeAsync(FakeCameraProcessStarter starter, bool keepPieces = false, CancellationToken cancellationToken = default, Action<int>? progress = null)
    {
        var making = new LessonVideoMaker(starter, _time, _logs).MakeAsync(_files, keepPieces, cancellationToken, progress);
        await _time.AdvanceUntilAsync(() => making.IsCompleted, CameraFinishJob.LookEvery, timeoutSeconds: 30);
        return await making;
    }

    /// <summary>The video is made through a camera process's finish job, its progress handed on, and the end said with its length and size and a word to watch it first.</summary>
    [Test]
    public async Task The_video_is_made_and_said()
    {
        var starter = RealHost();
        var percents = new List<int>();

        var result = await MakeAsync(starter, progress: percents.Add);

        Assert.That(result.Outcome, Is.EqualTo(LessonVideoOutcome.Made));
        Assert.That(result.Path, Is.EqualTo(_files.LessonVideo));
        Assert.That(result.Length, Is.EqualTo(TimeSpan.FromSeconds(2)));
        Assert.That(result.Bytes, Is.EqualTo(new FileInfo(_files.LessonVideo).Length));
        Assert.That(percents, Does.Contain(0).And.Contain(99));
        Assert.That(_logs.Messages, Has.Member($"The lesson's video: {BaseName}.mp4 (00:00:02, {result.Bytes} bytes), with its sound. Watch it before you publish it."));
        Assert.That(_logs.Messages, Has.Some.StartsWith("Deleted the camera's 1 piece"), "the camera process's lines in the maker's log");
        Assert.That(starter.Started, Has.Count.EqualTo(1));
    }

    /// <summary>The job carries whether the pieces are kept for Google Drive.</summary>
    [Test]
    public async Task The_job_says_whether_the_pieces_are_kept()
    {
        var starter = new FakeCameraProcessStarter { PlayEach = true };
        using var cancel = new CancellationTokenSource();
        var making = new LessonVideoMaker(starter, _time, _logs).MakeAsync(_files, keepPieces: true, cancel.Token);
        await Wait.UntilAsync(() => starter.Played.Count == 1 && starter.Played[0].Job is not null);

        Assert.That(starter.Played[0].Job, Is.EqualTo(new CameraJob(CameraProtocol.Version, CameraJobKind.Finish, Folder: _folder.Path, BaseName: BaseName, KeepPieces: true)));

        await cancel.CancelAsync();
        await _time.AdvanceUntilAsync(() => making.IsCompleted, TimeSpan.FromSeconds(1), timeoutSeconds: 30);
        Assert.That((await making).Outcome, Is.EqualTo(LessonVideoOutcome.Cancelled));
    }

    /// <summary>A video that fails its check is not made: the camera process's why is said, with how to make it again, once.</summary>
    [Test]
    public async Task A_failure_is_said_with_how_to_make_it_again()
    {
        _media.CheckAnswer = "no frame decodes at 00:00:01.967";

        var result = await MakeAsync(RealHost());

        Assert.That(result.Outcome, Is.EqualTo(LessonVideoOutcome.Failed));
        Assert.That(result.Why, Does.StartWith("The lesson's video did not pass its check (no frame decodes at 00:00:01.967)"));
        var said = _logs.Problems.Single(line => line.StartsWith("The lesson's video was not made", StringComparison.Ordinal));
        Assert.That(said.Split("finish-video").Length, Is.EqualTo(2), "the command once");
    }

    /// <summary>A camera process that says nothing of its progress for two minutes is ended, and the video is not made.</summary>
    [Test]
    public async Task A_process_silent_for_two_minutes_is_ended()
    {
        var starter = new FakeCameraProcessStarter { PlayEach = true };
        var making = new LessonVideoMaker(starter, _time, _logs).MakeAsync(_files, keepPieces: false, CancellationToken.None);
        await Wait.UntilAsync(() => starter.Played.Count == 1 && starter.Played[0].Job is not null);

        await _time.AdvanceUntilAsync(() => making.IsCompleted, TimeSpan.FromSeconds(10), timeoutSeconds: 30);

        var result = await making;
        Assert.That(result.Outcome, Is.EqualTo(LessonVideoOutcome.Failed));
        Assert.That(result.Why, Is.EqualTo("ClassScribe's camera process did not say how far it had got for 2 minutes, so it was ended"));
        Assert.That(starter.Started.Single().Process.Killed, Is.True);
        Assert.That(_time.GetUtcNow() - TestRuns.Start, Is.GreaterThanOrEqualTo(LessonVideoMaker.ProgressLimit));
    }

    /// <summary>A cancel is sent to the camera process, which deletes its unfinished video; the result is cancelled, and the pieces are kept.</summary>
    [Test]
    public async Task A_cancel_reaches_the_camera_process()
    {
        using var gate = new ManualResetEventSlim();
        _media.HoldWrite = gate;
        using var cancel = new CancellationTokenSource();
        var making = new LessonVideoMaker(RealHost(), _time, _logs).MakeAsync(_files, keepPieces: false, cancel.Token);
        await Wait.UntilAsync(() => _media.Writing);

        await cancel.CancelAsync();
        await _time.AdvanceUntilAsync(() => making.IsCompleted, CameraFinishJob.LookEvery, timeoutSeconds: 30);

        Assert.That((await making).Outcome, Is.EqualTo(LessonVideoOutcome.Cancelled));
        Assert.That(File.Exists(_files.LessonVideoPartial), Is.False);
        Assert.That(_files.FindCameraPieces(), Has.Count.EqualTo(1));
        Assert.That(_logs.Problems, Has.Some.StartsWith("The lesson's video was cancelled; the camera's pieces are kept. To make it later, free: "));
    }

    /// <summary>A camera process that does not end at the cancel is ended after fifteen seconds.</summary>
    [Test]
    public async Task A_process_that_does_not_end_at_the_cancel_is_ended()
    {
        var starter = new FakeCameraProcessStarter { PlayEach = true };
        using var cancel = new CancellationTokenSource();
        var making = new LessonVideoMaker(starter, _time, _logs).MakeAsync(_files, keepPieces: false, cancel.Token);
        await Wait.UntilAsync(() => starter.Played.Count == 1 && starter.Played[0].Job is not null);

        await cancel.CancelAsync();
        await _time.AdvanceUntilAsync(() => making.IsCompleted, TimeSpan.FromSeconds(1), timeoutSeconds: 30);

        Assert.That((await making).Outcome, Is.EqualTo(LessonVideoOutcome.Cancelled));
        Assert.That(starter.Played[0].Received, Does.Contain(CameraProtocol.Cancel));
        Assert.That(starter.Started.Single().Process.Killed, Is.True);
    }

    /// <summary>A camera process Windows does not start is a video not made, said with why and how to make it later.</summary>
    [Test]
    public async Task A_process_that_does_not_start_is_a_video_not_made()
    {
        var starter = new FakeCameraProcessStarter { StartFailure = new System.ComponentModel.Win32Exception(5, "Access is denied.") };

        var result = await MakeAsync(starter);

        Assert.That(result.Outcome, Is.EqualTo(LessonVideoOutcome.Failed));
        Assert.That(result.Why, Is.EqualTo("Windows did not start ClassScribe's camera process (Access is denied)"));
        Assert.That(_logs.Problems.Single(), Does.StartWith("The lesson's video was not made: Windows did not start ClassScribe's camera process (Access is denied). To make it again, free: "));
    }
}
