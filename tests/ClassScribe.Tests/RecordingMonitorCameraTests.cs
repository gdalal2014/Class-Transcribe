using ClassScribe.Audio;
using ClassScribe.Camera;
using ClassScribe.Commands;
using ClassScribe.Runs;
using ClassScribe.Settings;

namespace ClassScribe.Tests;

/// <summary>
/// The checks of a lesson with the camera's picture (teach-video 6.10 to 6.14): what is wrong with the camera as the run's
/// attention (an Alert while its picture is not recorded, a Notice while it is dark, the microphone's line when the camera
/// whose microphone is recorded is lost), the status line's camera clause, and the room on the disk counted with the picture,
/// which is let go when the sound and the picture together have room for less than 30 minutes.
/// </summary>
public partial class RecordingMonitorTests
{
    /// <summary>The OBSBOT lost, as the camera track says it.</summary>
    private static readonly CameraConcern CameraLost = new(true, "CAMERA LOST",
        $"The camera \"{LaptopCameras.Obsbot.Name}\" stopped sending its picture; the sound records on. Plug it back in");

    /// <summary>A lesson's request: in person, the OBSBOT's microphone and its camera at 720p.</summary>
    private static RecordRequest Lesson() =>
        TestRuns.Request(TestRuns.Id()) with { Source = new SourcePlan(RecordingMode.Microphone, null, new MicrophoneChoice("OBSBOT"), new CameraChoice("OBSBOT", "720p")) };

    /// <summary>Begins the monitor for a lesson with <paramref name="camera"/>, and the microphone's recording when given.</summary>
    private void BeginLesson(FakeCameraRecording camera, RecordingOptions? options = null, IMicrophoneRecording? microphone = null)
    {
        Begin(options ?? new RecordingOptions { StatusIntervalSeconds = 3600 }, Lesson(), microphone);
        _context = _context with { Camera = camera };
    }

    /// <summary>A lesson's snapshot: the microphone recording in person, paused when asked.</summary>
    private static RecordingSnapshot LessonSnapshot(bool paused = false) =>
        Recording(FakeRecording.Track("OBSBOT Meet Flip Microphone", deliversContinuously: true), paused: paused);

    /// <summary>A camera whose picture is not recorded is an Alert in its words; a dark picture is a Notice; nothing is wrong when it records.</summary>
    [Test]
    public void A_camera_not_recording_is_an_Alert_and_a_dark_picture_a_Notice()
    {
        var camera = new FakeCameraRecording();
        BeginLesson(camera);

        var fine = Look(LessonSnapshot());
        camera.Camera = camera.Camera! with { Capturing = false, Concern = CameraLost };
        var lost = Tick(LessonSnapshot());
        camera.Camera = camera.Camera with { Capturing = true, Dark = true, Concern = new CameraConcern(false, CameraTrack.DarkShort, "The camera's picture has been black for 10 seconds: is its lens covered or folded?") };
        var dark = Tick(LessonSnapshot());

        Assert.That(fine.Attention.Level, Is.EqualTo(AttentionLevel.None));
        Assert.That((lost.Attention.Level, lost.Attention.Short, lost.Attention.Text), Is.EqualTo((AttentionLevel.Alert, CameraLost.Short, CameraLost.Text)));
        Assert.That((dark.Attention.Level, dark.Attention.Short), Is.EqualTo((AttentionLevel.Notice, CameraTrack.DarkShort)));
    }

    /// <summary>A lost camera whose own microphone the run records adds that its microphone may be silent too.</summary>
    [Test]
    public void A_lost_camera_whose_microphone_is_recorded_says_the_microphone_may_be_silent_too()
    {
        var camera = new FakeCameraRecording();
        camera.Camera = camera.Camera! with { Capturing = false, Concern = CameraLost };
        var microphone = new FakeMicrophoneRecording(RecordingMode.Microphone, LaptopMicrophones.Obsbot with { ContainerId = LaptopCameras.ObsbotContainer });
        BeginLesson(camera, microphone: microphone);

        var tick = Look(LessonSnapshot());

        Assert.That(tick.Attention.Text, Is.EqualTo($"{CameraLost.Text}. Its microphone may be silent too while the camera is away or asleep"));
    }

    /// <summary>While paused the camera is let go by design, and nothing is said of it; a picture given up is said all the same.</summary>
    [Test]
    public void While_paused_only_a_picture_given_up_is_said()
    {
        var camera = new FakeCameraRecording();
        camera.Camera = camera.Camera! with { Capturing = false, Concern = CameraLost };
        BeginLesson(camera);

        var paused = Look(LessonSnapshot(paused: true));
        camera.LetGoOfTheCamera(CameraTrack.GivenUpText);
        var givenUp = Tick(LessonSnapshot(paused: true));

        Assert.That(paused.Attention.Short, Is.Not.EqualTo(CameraLost.Short));
        Assert.That(givenUp.Attention.Short, Is.EqualTo(CameraTrack.GivenUpShort));
    }

    /// <summary>The status line says the camera's size, its frames a second and those dropped; or that its picture is not recording.</summary>
    [Test]
    public void The_status_line_says_how_the_camera_records()
    {
        var camera = new FakeCameraRecording();
        BeginLesson(camera, new RecordingOptions { StatusIntervalSeconds = 1 });

        var recording = Tick(LessonSnapshot());
        camera.Camera = camera.Camera! with { Capturing = false, Concern = CameraLost };
        var notRecording = Tick(LessonSnapshot());

        Assert.That(Texts(recording), Has.Some.EndsWith("; camera 1280x720, 29.9 frames a second, 3 dropped"));
        Assert.That(Texts(notRecording), Has.Some.EndsWith("; camera not recording"));
    }

    /// <summary>
    /// The room on the disk counts the picture twice (its pieces and the finished video): with room for less than 30 minutes
    /// of the sound and the picture together, the camera is let go, with words that the camera's track says when it lets go
    /// (so they are not among the check's own lines, and are said once), and the sound alone is measured against the room,
    /// so no disk Alert comes while the sound fits.
    /// </summary>
    [Test]
    public void With_little_room_the_camera_is_let_go_and_the_sound_records_on()
    {
        _space.Bytes = 1_000_000_000;
        var camera = new FakeCameraRecording();
        BeginLesson(camera);

        var tick = Look(LessonSnapshot());

        Assert.That(tick.LetGoOfTheCamera, Is.EqualTo(@"Little room is left on D:\ (1.0 GB free), so the camera is let go: the rest of this lesson is recorded without its picture. The sound records on"));
        Assert.That(Texts(tick), Does.Not.Contain(tick.LetGoOfTheCamera), "the camera's track says it as it lets go");
        Assert.That(tick.Attention.Short, Is.Not.EqualTo("DISK NEARLY FULL"));
        Assert.That(RecordingMonitor.CameraBytesPerHour(_context), Is.EqualTo(2L * 5_000_000 / 8 * 3600));
    }

    /// <summary>The start's warning of room for less than 3 hours counts the picture: 10 GB holds hours of sound but about two of a lesson with the camera.</summary>
    [Test]
    public void The_start_s_room_warning_counts_the_picture()
    {
        _space.Bytes = 10_000_000_000;
        BeginLesson(new FakeCameraRecording());
        var lesson = Look(LessonSnapshot());

        Begin(new RecordingOptions { StatusIntervalSeconds = 3600 }, Lesson() with { Source = Lesson().Source with { Camera = null } });
        var soundOnly = Look(LessonSnapshot());

        Assert.That(Warnings(lesson), Has.Some.StartsWith("Only about 2 hours"));
        Assert.That(Warnings(soundOnly), Has.None.StartsWith("Only about"));
        Assert.That(lesson.LetGoOfTheCamera, Is.Null);
    }

    /// <summary>A picture already given up takes no room: the room is the sound's alone.</summary>
    [Test]
    public void A_picture_given_up_takes_no_room()
    {
        var camera = new FakeCameraRecording();
        camera.LetGoOfTheCamera("given up");
        BeginLesson(camera);

        Assert.That(RecordingMonitor.CameraBytesPerHour(_context), Is.Zero);
    }
}
