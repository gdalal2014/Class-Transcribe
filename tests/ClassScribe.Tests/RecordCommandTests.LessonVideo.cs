using ClassScribe.Audio;
using ClassScribe.Camera;
using ClassScribe.Commands;
using ClassScribe.Drive;
using ClassScribe.Recorder;

namespace ClassScribe.Tests;

/// <summary>
/// A lesson with the camera at its end (teach-video 6.9, slice 8): once its sound has stopped its run file says Transcribing,
/// record-only included, while its video is made beside the transcript by a camera process's finish job (here the real
/// camera host with <see cref="FakeLessonVideoMedia"/>); the outcome is written only once the video is made or has failed;
/// the laptop stays awake until then; Ctrl+C after the stop cancels the video with the transcript; the pieces are kept for
/// Drive only when the video goes there; the start line says where the video goes; a camera that wrote no piece makes no
/// video.
/// </summary>
public partial class RecordCommandTests
{
    /// <summary>A lesson's request: in person, the camera "OBSBOT" at 720p.</summary>
    private RecordRequest LessonRequest(bool transcribe) =>
        Request(transcribe) with { Source = new SourcePlan(RecordingMode.Microphone, Class: null, new MicrophoneChoice(null), new CameraChoice("OBSBOT", "720p")) };

    /// <summary>The fake media the lesson's finish jobs use, and the log the maker writes.</summary>
    private (FakeLessonVideoMedia Media, FakeCameraProcessStarter Starter, TestLoggerFactory Logs) LessonVideoParts()
    {
        var media = new FakeLessonVideoMedia();
        var starter = new FakeCameraProcessStarter
        {
            Run = (process, jobId) => new CameraHost { Media = media }.RunAsync(jobId, process.ProcessStream, process.ProcessStream)
        };
        return (media, starter, new TestLoggerFactory());
    }

    /// <summary>Writes the lesson's sound and a camera piece into the files the recording was started into, as a lesson leaves them; returns the sound's file.</summary>
    private static string WriteTheLesson(FakeRecording recording)
    {
        var files = recording.Files!;
        TestAudio.WriteWav(files.Segment(1), CameraFinishJob.SoundRate, TestAudio.Tone(CameraFinishJob.SoundRate, 2, 440, 0.25));
        File.WriteAllBytes(files.CameraPiece(TimeSpan.Zero), [1, 2, 3]);
        recording.MainSegments = [files.Segment(1)];
        return files.Segment(1);
    }

    /// <summary>
    /// A record-only lesson: at the stop its run file says Transcribing while its video is made, the outcome waits for the
    /// video, the laptop is kept awake until then, and the end says the video with its length and size; the pieces go.
    /// </summary>
    [Test]
    public async Task A_lesson_s_video_is_made_at_the_stop_before_its_outcome()
    {
        var (media, starter, logs) = LessonVideoParts();
        using var gate = new ManualResetEventSlim();
        media.HoldWrite = gate;
        var recordings = new FakeRecordingFactory();
        var recording = recordings.Enqueue(new FakeRecording(RecordingMode.Microphone) { Written = TimeSpan.FromSeconds(2) });
        var run = CreateCommand(recordings: recordings, lessonVideos: new LessonVideoMaker(starter, TimeProvider.System, logs)).RunAsync(LessonRequest(transcribe: false));
        await Wait.UntilAsync(() => recording.Started);
        WriteTheLesson(recording);

        _signals.RequestStop();
        await Wait.UntilAsync(() => media.Writing);

        Assert.That(RunFile().State, Is.EqualTo(RunState.Transcribing), "the recording is complete; its video is being made");
        Assert.That(RunFile().Outcome, Is.Null, "the outcome waits for the video");
        Assert.That(_power.Holding, Is.True, "the laptop is kept awake while the video is made");

        gate.Set();
        Assert.That(await run.WaitAsync(TimeSpan.FromSeconds(30)), Is.EqualTo(0));

        var files = recording.Files!;
        Assert.That((RunFile().State, RunFile().Outcome?.Kind), Is.EqualTo((RunState.Finished, OutcomeKind.Recorded)));
        Assert.That(File.Exists(files.LessonVideo), Is.True);
        Assert.That(files.FindCameraPieces(), Is.Empty, "deleted once the video passed its check, Drive not set up");
        Assert.That(logs.Messages, Has.Some.StartsWith($"The lesson's video: {files.BaseName}.mp4 (00:00:02, "));
        Assert.That(starter.Started, Has.Count.EqualTo(1));
        Assert.That(_power.Holding, Is.False);
    }

    /// <summary>With a transcript to make, the video is made beside it, and the outcome, Transcribed, is written once both are done.</summary>
    [Test]
    public async Task A_lesson_s_video_is_made_beside_its_transcript()
    {
        var (media, starter, logs) = LessonVideoParts();
        var recordings = new FakeRecordingFactory();
        var recording = recordings.Enqueue(new FakeRecording(RecordingMode.Microphone) { Written = TimeSpan.FromSeconds(2) });
        var run = CreateCommand(recordings: recordings, lessonVideos: new LessonVideoMaker(starter, TimeProvider.System, logs)).RunAsync(LessonRequest(transcribe: true));
        await Wait.UntilAsync(() => recording.Started);
        WriteTheLesson(recording);

        _signals.RequestStop();

        Assert.That(await run.WaitAsync(TimeSpan.FromSeconds(30)), Is.EqualTo(0));
        Assert.That(RunFile().Outcome?.Kind, Is.EqualTo(OutcomeKind.Transcribed));
        Assert.That(File.Exists(recording.Files!.LessonVideo), Is.True);
        Assert.That(media.Written, Has.Count.EqualTo(1));
    }

    /// <summary>
    /// With a transcript to make, the transcript saved first and the video still being made, the outcome waits for the video:
    /// no outcome is written and the laptop stays awake until the video is done, so that the recorder never ends, ending the
    /// camera process, while the video is made.
    /// </summary>
    [Test]
    public async Task A_lesson_s_outcome_waits_for_its_video_after_the_transcript_is_saved()
    {
        var (media, starter, logs) = LessonVideoParts();
        using var gate = new ManualResetEventSlim();
        media.HoldWrite = gate;
        var recordings = new FakeRecordingFactory();
        var recording = recordings.Enqueue(new FakeRecording(RecordingMode.Microphone) { Written = TimeSpan.FromSeconds(2) });
        var run = CreateCommand(recordings: recordings, lessonVideos: new LessonVideoMaker(starter, TimeProvider.System, logs)).RunAsync(LessonRequest(transcribe: true));
        await Wait.UntilAsync(() => recording.Started);
        WriteTheLesson(recording);

        _signals.RequestStop();
        await Wait.UntilAsync(() => _log.Messages.Any(message => message.StartsWith("Transcript saved to", StringComparison.Ordinal)));
        await Task.Delay(200);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(media.Writing, Is.True, "the video is still being made");
            Assert.That(run.IsCompleted, Is.False);
            Assert.That(RunFile().Outcome, Is.Null, "the outcome waits for the video");
            Assert.That(_power.Holding, Is.True, "the laptop is kept awake while the video is made");
        }

        gate.Set();
        Assert.That(await run.WaitAsync(TimeSpan.FromSeconds(30)), Is.EqualTo(0));
        Assert.That(RunFile().Outcome?.Kind, Is.EqualTo(OutcomeKind.Transcribed));
        Assert.That(_power.Holding, Is.False);
    }

    /// <summary>Ctrl+C after the stop cancels the video with the transcript: the run ends cancelled, and the pieces are kept for finish-video.</summary>
    [Test]
    public async Task Ctrl_C_after_the_stop_cancels_the_video_with_the_transcript()
    {
        var (media, starter, logs) = LessonVideoParts();
        using var gate = new ManualResetEventSlim();
        media.HoldWrite = gate;
        var recordings = new FakeRecordingFactory();
        var recording = recordings.Enqueue(new FakeRecording(RecordingMode.Microphone) { Written = TimeSpan.FromSeconds(2) });
        var run = CreateCommand(recordings: recordings, lessonVideos: new LessonVideoMaker(starter, TimeProvider.System, logs)).RunAsync(LessonRequest(transcribe: false));
        await Wait.UntilAsync(() => recording.Started);
        WriteTheLesson(recording);
        _signals.RequestStop();
        await Wait.UntilAsync(() => media.Writing);

        _signals.Cancel();

        Assert.That(await run.WaitAsync(TimeSpan.FromSeconds(30)), Is.EqualTo(0), "record-only: the recording is kept, whatever became of the video");
        var files = recording.Files!;
        Assert.That(File.Exists(files.LessonVideo), Is.False);
        Assert.That(File.Exists(files.LessonVideoPartial), Is.False);
        Assert.That(files.FindCameraPieces(), Has.Count.EqualTo(1));
        Assert.That(logs.Problems, Has.Some.StartsWith("The lesson's video was cancelled; the camera's pieces are kept"));
        Assert.That(RunFile().Outcome?.Kind, Is.EqualTo(OutcomeKind.Recorded));
    }

    /// <summary>The finish job keeps the pieces only when the video goes to Drive: set up, Drive:LessonVideos on, audio sent.</summary>
    [TestCase(true)]
    [TestCase(false)]
    public async Task The_pieces_are_kept_only_for_a_video_that_goes_to_Drive(bool lessonVideos)
    {
        var (_, starter, logs) = LessonVideoParts();
        var store = FakeCredentialStore.With("refresh");
        var drive = new LessonVideoDrive(store, TestOptions.Create(new DriveOptions { LessonVideos = lessonVideos }));
        var recordings = new FakeRecordingFactory();
        var recording = recordings.Enqueue(new FakeRecording(RecordingMode.Microphone) { Written = TimeSpan.FromSeconds(2) });
        var run = CreateCommand(recordings: recordings, lessonVideos: new LessonVideoMaker(starter, TimeProvider.System, logs), lessonDrive: drive)
            .RunAsync(LessonRequest(transcribe: false));
        await Wait.UntilAsync(() => recording.Started);
        WriteTheLesson(recording);
        _signals.RequestStop();

        Assert.That(await run.WaitAsync(TimeSpan.FromSeconds(30)), Is.EqualTo(0));
        Assert.That(recording.Files!.FindCameraPieces(), lessonVideos ? Has.Count.EqualTo(1) : Is.Empty);
        Assert.That(_log.Messages, Has.Member(lessonVideos ? LessonVideoDrive.GoesUpLine : LessonVideoDrive.StaysHereLine), "the start says where the video goes");
    }

    /// <summary>A lesson whose camera wrote no piece makes no video, and says so; the sound is kept as always.</summary>
    [Test]
    public async Task A_lesson_whose_camera_wrote_no_piece_makes_no_video()
    {
        var (_, starter, logs) = LessonVideoParts();
        var recordings = new FakeRecordingFactory();
        var recording = recordings.Enqueue(new FakeRecording(RecordingMode.Microphone) { Written = TimeSpan.FromSeconds(2) });
        var run = CreateCommand(recordings: recordings, lessonVideos: new LessonVideoMaker(starter, TimeProvider.System, logs)).RunAsync(LessonRequest(transcribe: false));
        await Wait.UntilAsync(() => recording.Started);
        TestAudio.WriteWav(recording.Files!.Segment(1), 16000, new short[16000]);
        recording.MainSegments = [recording.Files.Segment(1)];
        _signals.RequestStop();

        Assert.That(await run.WaitAsync(TimeSpan.FromSeconds(30)), Is.EqualTo(0));
        Assert.That(starter.Started, Is.Empty);
        Assert.That(_log.Problems, Has.Member("The camera wrote no picture in this lesson, so it has no video to make; the sound is kept"));
    }
}
