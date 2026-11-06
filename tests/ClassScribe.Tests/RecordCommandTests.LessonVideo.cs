using ClassScribe.Audio;
using ClassScribe.Audio.Timeline;
using ClassScribe.Camera;
using ClassScribe.Commands;
using ClassScribe.Desktop;
using ClassScribe.Drive;
using ClassScribe.Recorder;
using ClassScribe.Runs;
using ClassScribe.Storage;

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
        var run = CreateCommand(recordings: recordings, lessonVideos: new LessonVideoMaker(starter, new RecordingFolder(), Printed.Commands, TimeProvider.System, logs)).RunAsync(LessonRequest(transcribe: false));
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
        Assert.That(OnDisk.FindCameraPieces(files), Is.Empty, "deleted once the video passed its check, Drive not set up");
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
        var run = CreateCommand(recordings: recordings, lessonVideos: new LessonVideoMaker(starter, new RecordingFolder(), Printed.Commands, TimeProvider.System, logs)).RunAsync(LessonRequest(transcribe: true));
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
        var run = CreateCommand(recordings: recordings, lessonVideos: new LessonVideoMaker(starter, new RecordingFolder(), Printed.Commands, TimeProvider.System, logs)).RunAsync(LessonRequest(transcribe: true));
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

        // Once only the video remains, the run says so, and the title shows how far the video has got (review 3.17).
        await Wait.UntilAsync(() => _status.States.Any(state => state.VideoPercent == 50));
        Assert.That(_log.Messages, Has.Member(RecordCommand.OnlyTheVideoRemains));
        Assert.That(_status.Last, Is.EqualTo(_status.Last! with { Phase = RecorderPhase.Transcribing, VideoPercent = 50 }));
        Assert.That(TitleText.For(_status.Last!), Does.StartWith("MAKING VIDEO 50% "));

        gate.Set();
        Assert.That(await run.WaitAsync(TimeSpan.FromSeconds(30)), Is.EqualTo(0));
        Assert.That(RunFile().Outcome?.Kind, Is.EqualTo(OutcomeKind.Transcribed));
        Assert.That(_power.Holding, Is.False);
        Assert.That(_status.Last, Is.EqualTo(_status.Last! with { Phase = RecorderPhase.Done, VideoPercent = null }), "the end clears the video's percent");
    }

    /// <summary>
    /// A record-only lesson says MAKING VIDEO from its stop, since only the video remains then, and nothing of a transcript
    /// (review 3.17).
    /// </summary>
    [Test]
    public async Task A_record_only_lesson_shows_MAKING_VIDEO_from_its_stop()
    {
        var (media, starter, logs) = LessonVideoParts();
        using var gate = new ManualResetEventSlim();
        media.HoldWrite = gate;
        var recordings = new FakeRecordingFactory();
        var recording = recordings.Enqueue(new FakeRecording(RecordingMode.Microphone) { Written = TimeSpan.FromSeconds(2) });
        var run = CreateCommand(recordings: recordings, lessonVideos: new LessonVideoMaker(starter, new RecordingFolder(), Printed.Commands, TimeProvider.System, logs)).RunAsync(LessonRequest(transcribe: false));
        await Wait.UntilAsync(() => recording.Started);
        WriteTheLesson(recording);

        _signals.RequestStop();
        await Wait.UntilAsync(() => _status.States.Any(state => state.VideoPercent == 50));

        Assert.That(TitleText.For(_status.Last!), Does.StartWith("MAKING VIDEO 50% "));
        Assert.That(_log.Messages, Has.Member(RecordCommand.OnlyTheVideoRemains));
        gate.Set();
        Assert.That(await run.WaitAsync(TimeSpan.FromSeconds(30)), Is.EqualTo(0));
    }

    /// <summary>
    /// The maker never throws by design, but a fault of the camera's channel of a type it does not expect would fault its
    /// task; the run's wait for the video catches it, says the video was not made with how to make it again, and lets the
    /// run end as its transcript did, Transcribed and exit 0, never TranscriptFailed (review 3.4).
    /// </summary>
    [Test]
    public async Task A_faulted_video_task_never_turns_a_saved_transcript_into_a_failed_one()
    {
        var logs = new TestLoggerFactory();
        var recordings = new FakeRecordingFactory();
        var recording = recordings.Enqueue(new FakeRecording(RecordingMode.Microphone) { Written = TimeSpan.FromSeconds(2) });
        var run = CreateCommand(recordings: recordings, lessonVideos: new LessonVideoMaker(new ThrowingCameraProcessStarter(), new RecordingFolder(), Printed.Commands, TimeProvider.System, logs))
            .RunAsync(LessonRequest(transcribe: true));
        await Wait.UntilAsync(() => recording.Started);
        var sound = WriteTheLesson(recording);

        _signals.RequestStop();

        Assert.That(await run.WaitAsync(TimeSpan.FromSeconds(30)), Is.EqualTo(0));
        Assert.That(RunFile().Outcome?.Kind, Is.EqualTo(OutcomeKind.Transcribed));
        Assert.That(_log.Problems, Has.Member($"The lesson's video was not made: an unexpected failure (The camera's lines cannot be read this way); the camera's pieces are kept. To make it again, free: {Printed.Commands.FinishVideo(sound)}"));
        Assert.That(OnDisk.FindCameraPieces(recording.Files!), Has.Count.EqualTo(1));
    }

    /// <summary>A camera process whose lines throw a type the maker does not expect, as a faulted channel would, so that the maker's task faults.</summary>
    private sealed class ThrowingCameraProcessStarter : ICameraProcessStarter
    {
        /// <inheritdoc />
        public ICameraProcess Start(string jobId) => new ThrowingCameraProcess();

        /// <summary>The process: its link takes the job and then throws on every receive.</summary>
        private sealed class ThrowingCameraProcess : ICameraProcess
        {
            /// <inheritdoc />
            public int ProcessId => 7777;

            /// <inheritdoc />
            public ICameraLink Link { get; } = new ThrowingLink();

            /// <inheritdoc />
            public int? ExitCode => null;

            /// <inheritdoc />
            public void Kill()
            {
            }

            /// <inheritdoc />
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }

        /// <summary>The link whose receive throws a <see cref="NotSupportedException"/>, which the maker does not expect.</summary>
        private sealed class ThrowingLink : ICameraLink
        {
            /// <inheritdoc />
            public ValueTask SendJobAsync(CameraJob job, CancellationToken cancellationToken) => ValueTask.CompletedTask;

            /// <inheritdoc />
            public ValueTask SendStopAsync(CameraStop stop, CancellationToken cancellationToken) => ValueTask.CompletedTask;

            /// <inheritdoc />
            public ValueTask SendEndAsync(CameraStop end, CancellationToken cancellationToken) => ValueTask.CompletedTask;

            /// <inheritdoc />
            public ValueTask SendClockAsync(ClockState clock, CancellationToken cancellationToken) => ValueTask.CompletedTask;

            /// <inheritdoc />
            public ValueTask SendSignalAsync(string type, CancellationToken cancellationToken) => ValueTask.CompletedTask;

            /// <inheritdoc />
            public ValueTask<CameraLine?> ReceiveAsync(CancellationToken cancellationToken) => throw new NotSupportedException("The camera's lines cannot be read this way.");
        }
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
        var run = CreateCommand(recordings: recordings, lessonVideos: new LessonVideoMaker(starter, new RecordingFolder(), Printed.Commands, TimeProvider.System, logs)).RunAsync(LessonRequest(transcribe: false));
        await Wait.UntilAsync(() => recording.Started);
        WriteTheLesson(recording);
        _signals.RequestStop();
        await Wait.UntilAsync(() => media.Writing);

        _signals.Cancel();

        Assert.That(await run.WaitAsync(TimeSpan.FromSeconds(30)), Is.EqualTo(0), "record-only: the recording is kept, whatever became of the video");
        var files = recording.Files!;
        Assert.That(File.Exists(files.LessonVideo), Is.False);
        Assert.That(File.Exists(files.LessonVideoPartial), Is.False);
        Assert.That(OnDisk.FindCameraPieces(files), Has.Count.EqualTo(1));
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
        var drive = new LessonVideoDrive(store, TestOptions.Create(new DriveOptions { LessonVideos = lessonVideos }), Printed.Commands);
        var recordings = new FakeRecordingFactory();
        var recording = recordings.Enqueue(new FakeRecording(RecordingMode.Microphone) { Written = TimeSpan.FromSeconds(2) });
        var run = CreateCommand(recordings: recordings, lessonVideos: new LessonVideoMaker(starter, new RecordingFolder(), Printed.Commands, TimeProvider.System, logs), lessonDrive: drive)
            .RunAsync(LessonRequest(transcribe: false));
        await Wait.UntilAsync(() => recording.Started);
        WriteTheLesson(recording);
        _signals.RequestStop();

        Assert.That(await run.WaitAsync(TimeSpan.FromSeconds(30)), Is.EqualTo(0));
        Assert.That(OnDisk.FindCameraPieces(recording.Files!), lessonVideos ? Has.Count.EqualTo(1) : Is.Empty);
        Assert.That(_log.Messages, Has.Member(lessonVideos ? LessonVideoDrive.GoesUpLine : LessonVideoDrive.StaysHereLine(Printed.Commands)), "the start says where the video goes");
    }

    /// <summary>A lesson whose camera wrote no piece makes no video, and says so; the sound is kept as always.</summary>
    [Test]
    public async Task A_lesson_whose_camera_wrote_no_piece_makes_no_video()
    {
        var (_, starter, logs) = LessonVideoParts();
        var recordings = new FakeRecordingFactory();
        var recording = recordings.Enqueue(new FakeRecording(RecordingMode.Microphone) { Written = TimeSpan.FromSeconds(2) });
        var run = CreateCommand(recordings: recordings, lessonVideos: new LessonVideoMaker(starter, new RecordingFolder(), Printed.Commands, TimeProvider.System, logs)).RunAsync(LessonRequest(transcribe: false));
        await Wait.UntilAsync(() => recording.Started);
        TestAudio.WriteWav(recording.Files!.Segment(1), 16000, new short[16000]);
        recording.MainSegments = [recording.Files.Segment(1)];
        _signals.RequestStop();

        Assert.That(await run.WaitAsync(TimeSpan.FromSeconds(30)), Is.EqualTo(0));
        Assert.That(starter.Started, Is.Empty);
        Assert.That(_log.Problems, Has.Member("The camera wrote no picture in this lesson, so it has no video to make; the sound is kept"));
    }
}
