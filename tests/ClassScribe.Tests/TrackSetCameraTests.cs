using ClassScribe.Audio;
using ClassScribe.Audio.Microphones;
using ClassScribe.Audio.Timeline;
using ClassScribe.Camera;
using ClassScribe.Commands;
using ClassScribe.Recorder;
using ClassScribe.Settings;
using ClassScribe.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClassScribe.Tests;

/// <summary>
/// A lesson in person with the camera's picture (teach-video 6.6 to 6.8): the track set starts the camera track once the
/// sound records, into the recording's files from the run's offset, with the cameras' offsets and its own clock; P, P
/// again and a cut reach it; at the stop the sound is stopped, padded and flushed first, and the camera then stops at that
/// same end; a camera that fails to start or to stop costs the picture only; and no webcam hold runs beside a picture.
/// </summary>
[TestFixture]
public sealed class TrackSetCameraTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 22, 14, 0, 0, TimeSpan.Zero);
    private static readonly AudioFormat Pcm48KMono = new(48000, 1, 16, false);

    /// <summary>The OBSBOT's microphone, in the container of its camera, as Windows lists them.</summary>
    private static readonly Microphone Obsbot = LaptopMicrophones.Obsbot with { ContainerId = LaptopCameras.ObsbotContainer };

    private TempFolder _folder = null!;
    private RecordingFiles _files = null!;
    private FakeMicrophoneProvider _microphones = null!;
    private ManualTimeProvider _time = null!;
    private FakeSystemTimes _times = null!;
    private FreezeWatch _freezes = null!;
    private RecordingClock _clock = null!;
    private TestLoggerFactory _logs = null!;
    private FakeCameraRecorder _cameras = null!;

    /// <summary>A recording in a temporary folder, the OBSBOT's microphone, the clocks ten seconds in, and a camera recorder that starts fake tracks.</summary>
    [SetUp]
    public void SetUp()
    {
        _folder = new TempFolder();
        _files = new RecordingFiles(_folder.Path, "Tajwid_2026-10-22_14-00-00Z");
        _microphones = new FakeMicrophoneProvider().Connect(Obsbot, LaptopMicrophones.Array);
        _time = new ManualTimeProvider(Start);
        _time.Advance(TimeSpan.FromSeconds(10));
        _times = new FakeSystemTimes(_time);
        _freezes = new FreezeWatch(_times, _time, NullLogger<FreezeWatch>.Instance);
        _clock = new RecordingClock(_time);
        _logs = new TestLoggerFactory();
        _cameras = new FakeCameraRecorder();
    }

    /// <summary>Deletes the temporary folder, which fails if a test left a file open.</summary>
    [TearDown]
    public void TearDown() => _folder.Dispose();

    /// <summary>The factory, over the fakes, with <paramref name="options"/> or the default settings, and the webcam holds when given.</summary>
    private TrackSetFactory Factory(RecordingOptions? options = null, WebcamHolds? webcams = null, ICameraRecorder? recorder = null) =>
        new(new FakeCaptureProvider(), _microphones, new FakeMicrophoneSessions(), new FakeMicrophoneUsageRecord(), new FakeProcessNames(), new FakeOwnProcesses(Laptop.OwnProcess),
            _freezes, TestOptions.Create(options ?? new RecordingOptions()), TestOptions.Create(new TranscriptOptions()), _time, _logs, webcams: webcams,
            cameraRecorder: recorder ?? _cameras);

    /// <summary>A lesson in person: the OBSBOT's microphone and its camera at 720p.</summary>
    private static SourcePlan Lesson => new(RecordingMode.Microphone, null, new MicrophoneChoice("OBSBOT"), new CameraChoice("OBSBOT", "720p"));

    /// <summary>Queues the next capture of the OBSBOT's microphone.</summary>
    private FakeAudioCapture MicrophoneCapture() =>
        _microphones.Prepare(new FakeAudioCapture(Pcm48KMono, Obsbot.Name, Obsbot.Id) { DeliversContinuously = true });

    /// <summary>
    /// The camera's track starts once the sound records, into the recording's files, its piece from the run's offset, with
    /// the cameras' offsets of the run's settings and the recording's own clock.
    /// </summary>
    [Test]
    public void The_camera_starts_once_the_sound_records_from_the_run_s_offset()
    {
        MicrophoneCapture();
        var offsets = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["OBSBOT"] = -60 };
        using var recording = (TrackSet)Factory(new RecordingOptions { CameraOffsets = offsets }).Create(Lesson, _clock);

        recording.Start(_files, TimeSpan.FromMinutes(20));

        var track = _cameras.Tracks.Single();
        Assert.That(track.Start with { Offsets = null }, Is.EqualTo(new CameraTrackStart(new CameraChoice("OBSBOT", "720p"), _files.Folder, _files.BaseName, TimeSpan.FromMinutes(20))));
        Assert.That(track.Start.Offsets, Is.EquivalentTo(offsets));
        Assert.That(track.Clock, Is.SameAs(_clock));
        Assert.That(_microphones.Opened, Has.Count.EqualTo(1), "the sound records first");
        Assert.That(recording.Camera, Is.EqualTo(track.State));
    }

    /// <summary>P lets the camera go, P again takes it again, and a time ClassScribe did not run nudges its clock line out.</summary>
    [Test]
    public void Pause_resume_and_a_cut_reach_the_camera()
    {
        MicrophoneCapture();
        using var recording = (TrackSet)Factory().Create(Lesson, _clock);
        recording.Start(_files, TimeSpan.Zero);

        recording.Pause();
        recording.Resume();
        _freezes.Tick();
        _time.Advance(TimeSpan.FromSeconds(1));
        _freezes.Tick();
        _times.Sleep(TimeSpan.FromSeconds(18));
        _freezes.Tick();

        Assert.That(_clock.TimeCut, Is.GreaterThan(TimeSpan.Zero));
        Assert.That(_cameras.Tracks.Single().Calls.Take(3), Is.EqualTo(new[] { "pause", "resume", "nudge" }));
    }

    /// <summary>
    /// At the stop the sound is stopped, padded to the end and its files finished first; then the camera stops at that same
    /// end, where the sound ends.
    /// </summary>
    [Test]
    public void The_sound_is_finished_before_the_camera_stops_at_the_same_end()
    {
        var microphone = MicrophoneCapture();
        using var recording = (TrackSet)Factory().Create(Lesson, _clock);
        recording.Start(_files, TimeSpan.Zero);
        microphone.Deliver(new byte[960], _time.GetTimestamp());
        _time.Advance(TimeSpan.FromSeconds(30));

        var summary = recording.Stop();

        var track = _cameras.Tracks.Single();
        Assert.That(track.SoundBeingWrittenAtStop, Is.False, "the sound's files were finished before the camera's stop");
        Assert.That(track.Calls, Is.EqualTo(new[] { $"stop at {summary.Length}" }));
        Assert.That(summary.Length, Is.EqualTo(TimeSpan.FromSeconds(30)).Within(TimeSpan.FromMilliseconds(1)));
    }

    /// <summary>A camera track that cannot be started costs the picture only: the sound records, and the line says why.</summary>
    [Test]
    public void A_camera_that_cannot_start_costs_the_picture_only()
    {
        var microphone = MicrophoneCapture();
        _cameras.StartFailure = new InvalidOperationException("the camera's list could not be read");
        using var recording = (TrackSet)Factory().Create(Lesson, _clock);

        recording.Start(_files, TimeSpan.Zero);
        microphone.Deliver(new byte[960], _time.GetTimestamp());
        var summary = recording.Stop();

        Assert.That(summary.MainSegments, Has.Count.EqualTo(1));
        Assert.That(recording.Camera, Is.Null);
        Assert.That(_logs.MessagesOf<TrackSet>(), Has.Some.EqualTo("The camera's picture cannot be recorded: the camera's list could not be read; the sound records on"));
    }

    /// <summary>A camera whose stop fails costs its piece's last moments only: the sound's summary comes all the same.</summary>
    [Test]
    public void A_camera_whose_stop_fails_costs_the_picture_only()
    {
        MicrophoneCapture();
        using var recording = (TrackSet)Factory().Create(Lesson, _clock);
        recording.Start(_files, TimeSpan.Zero);
        _cameras.Tracks.Single().StopFailure = new IOException("The pipe is broken.");

        var summary = recording.Stop();

        Assert.That(summary, Is.Not.Null);
        Assert.That(_logs.MessagesOf<TrackSet>(), Has.Some.StartsWith("The camera's picture could not be stopped cleanly: The pipe is broken."));
    }

    /// <summary>The camera records only in person in this build; a factory without a camera recorder, or with one that cannot record, refuses a plan with a camera before anything is opened.</summary>
    [Test]
    public void A_plan_with_a_camera_is_refused_where_the_picture_cannot_be_recorded()
    {
        var withClass = new SourcePlan(RecordingMode.ClassAndMicrophone, new ClassSource.Automatic(), new MicrophoneChoice(null), new CameraChoice("OBSBOT", "720p"));
        var notReady = new FakeCameraRecorder { NotReady = "The camera needs Windows 10 version 2004 (build 19041) or later" };

        Assert.That(Assert.Throws<CameraNotBuiltException>(() => Factory().Create(withClass, _clock))!.Message, Is.EqualTo("The camera records only in person in this build"));
        Assert.That(Assert.Throws<CameraNotBuiltException>(() => Factory(recorder: notReady).Create(Lesson, _clock))!.Message, Is.EqualTo(notReady.NotReady));
        Assert.That(_microphones.Opened, Is.Empty);
    }

    /// <summary>
    /// A lesson that records the camera's picture starts no webcam hold beside it, whatever Recording:WakeCameraMicrophone
    /// says: the picture keeps the camera streaming by itself (camera-first 3.4). Without the picture, the hold starts.
    /// </summary>
    [Test]
    public async Task No_webcam_hold_runs_beside_the_camera_s_picture()
    {
        var starter = new FakeCameraProcessStarter { PlayEach = true };
        var holds = new WebcamHolds(new FakeCameraProvider(), starter, _time, _logs);
        var wakeOn = new RecordingOptions { WakeCameraMicrophone = true };

        MicrophoneCapture();
        using (var lesson = (TrackSet)Factory(wakeOn, holds).Create(Lesson, _clock))
        {
            lesson.Start(_files, TimeSpan.Zero);
            Assert.That(starter.StartsEntered, Is.Zero);
            Assert.That(lesson.Webcam?.Holding, Is.False);
            lesson.Stop();
        }

        // Without the picture the hold starts, and the microphone's open waits for its first frame, at most 5 seconds.
        MicrophoneCapture();
        using var withoutPicture = (TrackSet)Factory(wakeOn, holds).Create(Lesson with { Camera = null }, new RecordingClock(_time));
        var starting = Task.Run(() => withoutPicture.Start(new RecordingFiles(_folder.Path, "Tajwid_2026-10-22_15-00-00Z"), TimeSpan.Zero));
        await _time.AdvanceUntilAsync(() => starting.IsCompleted, TimeSpan.FromSeconds(1), atMost: TimeSpan.FromSeconds(6));
        await starting;
        Assert.That(starter.StartsEntered, Is.EqualTo(1));
        var stopping = Task.Run(withoutPicture.Stop);
        await _time.AdvanceUntilAsync(() => stopping.IsCompleted, TimeSpan.FromSeconds(1), atMost: TimeSpan.FromSeconds(6));
    }
}
