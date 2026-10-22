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
/// The webcam hold (camera-first 3.4) on the manual clock, over <see cref="FakeCameraProvider"/> (this laptop's cameras),
/// <see cref="FakeCameraProcessStarter"/> playing each camera process, and the microphones of <see cref="LaptopMicrophones"/>:
/// off by default with no camera process; on, the hold job asked for the webcam alone (shared, nothing else asked), before
/// the microphone opens and with a 5-second limit; held through P and V, let go at the stop; following the microphone to
/// another; the picture stopping and back; a refusal and a process that ends. No camera and no microphone is opened.
/// </summary>
[TestFixture]
public class CameraHoldTests
{
    /// <summary>The OBSBOT's microphone, in the container it shares with its camera.</summary>
    private static readonly Microphone Obsbot = LaptopMicrophones.Obsbot with { ContainerId = LaptopCameras.ObsbotContainer };

    /// <summary>A microphone of the Pixel, through Phone Link, in the container of its virtual camera: no webcam's.</summary>
    private static readonly Microphone Pixel = LaptopMicrophones.Obsbot with
    {
        Id = "{0.0.1.00000000}.{b1c2d3e4-0000-4000-8000-000000000009}", Name = "Pixel 10 Pro XL Microphone", ContainerId = LaptopCameras.PixelContainer
    };

    /// <summary>The steps the clock moves by.</summary>
    private static readonly TimeSpan Step = TimeSpan.FromMilliseconds(250);

    private ManualTimeProvider _time = null!;
    private FakeCameraProvider _cameras = null!;
    private FakeCameraProcessStarter _starter = null!;
    private TestLoggerFactory _logs = null!;

    /// <summary>A new clock, camera list, starter and log for each test.</summary>
    [SetUp]
    public void SetUp()
    {
        _time = new ManualTimeProvider(TestRuns.Start);
        _cameras = new FakeCameraProvider();
        _starter = new FakeCameraProcessStarter { PlayEach = true };
        _logs = new TestLoggerFactory();
    }

    /// <summary>With the setting off, as it is by default, a webcam's microphone is known to be one, and no camera process starts.</summary>
    [Test]
    public void Off_by_default_no_camera_process_starts()
    {
        Assert.That(new RecordingOptions().WakeCameraMicrophone, Is.False);
        using var hold = Hold(wakeOn: false);

        hold.Follow(Obsbot);
        hold.BeforeOpen(Obsbot);

        Assert.That(_starter.Started, Is.Empty);
        Assert.That(hold.State, Is.EqualTo(new WebcamState(LaptopCameras.Obsbot.Name, Holding: false, HasFrames: false)));
    }

    /// <summary>
    /// On, the webcam's microphone starts a camera process whose hold job carries the camera's id and name and nothing
    /// else (no size: the hold asks the camera for nothing); the start is said once, in the words of the design.
    /// </summary>
    [Test]
    public async Task On_the_hold_job_names_the_webcam_alone()
    {
        using var hold = Hold();

        hold.Follow(Obsbot);
        var played = _starter.Played.Single();
        await WaitAsync(() => played.Job is not null);

        Assert.That(played.Job, Is.EqualTo(new CameraJob(CameraProtocol.Version, CameraJobKind.Hold, LaptopCameras.Obsbot.Id, LaptopCameras.Obsbot.Name)));
        Assert.That(_logs.Messages, Does.Contain("The microphone being recorded belongs to the camera \"OBSBOT Meet Flip StreamCamera\", so ClassScribe keeps the camera's picture open, shared, until the recording ends: the camera cannot fall asleep and silence its microphone. Nothing of the picture is kept; the camera's light stays on"));
        Assert.That(hold.State!.Holding, Is.True);
    }

    /// <summary>The microphone's open waits for the held camera's first frame, and goes on as soon as it comes.</summary>
    [Test]
    public async Task The_microphone_opens_after_the_first_frame()
    {
        using var hold = Hold();
        hold.Follow(Obsbot);
        var played = _starter.Played.Single();

        var opening = Task.Run(() => hold.BeforeOpen(Obsbot));
        await Task.Delay(100);
        Assert.That(opening.IsCompleted, Is.False, "waiting for the first frame");

        await played.StatusAsync(frames: 1);
        await opening.WaitAsync(TimeSpan.FromSeconds(10));

        await _time.AdvanceUntilAsync(() => hold.State!.HasFrames, Step, atMost: TimeSpan.FromSeconds(1));
    }

    /// <summary>
    /// A camera that sends no frame delays the microphone's open by 5 seconds at most, and the hold says the camera has
    /// sent no picture yet while its microphone records.
    /// </summary>
    [Test]
    public async Task No_frame_delays_the_microphone_by_5_seconds_at_most()
    {
        using var hold = Hold();
        hold.Follow(Obsbot);

        var opening = Task.Run(() => hold.BeforeOpen(Obsbot));
        var waited = await _time.AdvanceUntilAsync(() => opening.IsCompleted, Step, atMost: TimeSpan.FromSeconds(6));
        await _time.AdvanceUntilAsync(() => _logs.Problems.Count > 0, Step, atMost: TimeSpan.FromSeconds(2));

        Assert.That(waited, Is.GreaterThanOrEqualTo(WebcamHold.FirstFrameWait - Step).And.LessThanOrEqualTo(WebcamHold.FirstFrameWait + Step));
        Assert.That(_logs.Problems, Is.EqualTo(new[] { "The camera \"OBSBOT Meet Flip StreamCamera\" has sent no picture yet; its microphone records meanwhile" }));
    }

    /// <summary>
    /// Only a webcam's microphone is held: the laptop's array (the computer's own container) needs no camera list, and a
    /// phone's microphone through Phone Link belongs to a virtual camera, never a webcam. Each microphone is decided once.
    /// </summary>
    [Test]
    public void Only_a_webcams_microphone_is_held()
    {
        using var hold = Hold();

        hold.Follow(LaptopMicrophones.Array);
        var listedForTheArray = _cameras.Listings;
        hold.Follow(Pixel);
        hold.Follow(Pixel);

        Assert.That(listedForTheArray, Is.Zero);
        Assert.That(_cameras.Listings, Is.EqualTo(1), "decided once for the Pixel");
        Assert.That(_starter.Started, Is.Empty);
        Assert.That(hold.State, Is.Null);
    }

    /// <summary>When the pick moves to a microphone of no webcam, the hold is let go: its process told to stop, and the line said.</summary>
    [Test]
    public async Task Moving_to_another_microphone_lets_the_hold_go()
    {
        using var hold = Hold();
        hold.Follow(Obsbot);
        var played = _starter.Played.Single();
        await WaitAsync(() => played.Job is not null);

        hold.Follow(LaptopMicrophones.Array);
        await WaitAsync(() => _logs.Messages.Contains("Let go of the picture of the camera \"OBSBOT Meet Flip StreamCamera\""));

        Assert.That(played.StopReceived, Is.True);
        Assert.That(hold.State, Is.Null);
        Assert.That(_starter.Started, Has.Count.EqualTo(1));
    }

    /// <summary>
    /// A picture that stops for 3 seconds is said once, with the folded lens as the likely cause, and a problem for the
    /// title while it lasts; when frames come again the camera is said to be open again and the problem ends.
    /// </summary>
    [Test]
    public async Task A_picture_that_stops_is_said_and_its_return_too()
    {
        using var hold = Hold();
        hold.Follow(Obsbot);
        var played = _starter.Played.Single();
        await played.StatusAsync(frames: 30);
        await _time.AdvanceUntilAsync(() => hold.State!.HasFrames, Step, atMost: TimeSpan.FromSeconds(1));

        await _time.AdvanceUntilAsync(() => hold.State!.Problem is not null, Step, atMost: TimeSpan.FromSeconds(5));
        var stopped = hold.State!;
        await played.StatusAsync(frames: 60);
        await _time.AdvanceUntilAsync(() => hold.State!.Problem is null, Step, atMost: TimeSpan.FromSeconds(2));

        Assert.That(stopped.HasFrames, Is.False);
        Assert.That(stopped.Problem, Is.EqualTo(WebcamHold.Stopped(LaptopCameras.Obsbot.Name)));
        Assert.That(stopped.Problem, Is.EqualTo("The camera \"OBSBOT Meet Flip StreamCamera\" stopped sending its picture. Is its lens folded? Folded, its microphone is off. ClassScribe opens it again every 5 seconds"));
        Assert.That(stopped.ProblemShort, Is.EqualTo(WebcamHold.StoppedShort));
        Assert.That(_logs.Problems, Is.EqualTo(new[] { stopped.Problem }), "said once");
        Assert.That(_logs.Messages, Does.Contain("The camera \"OBSBOT Meet Flip StreamCamera\" is open again"));
    }

    /// <summary>
    /// A camera that cannot be opened is said with its cause, once per cause however often the camera process tries again,
    /// with what to do; the process's own word that the picture stopped is left to the hold's own rule.
    /// </summary>
    [Test]
    public async Task A_camera_that_cannot_be_opened_is_said_once_with_its_cause()
    {
        using var hold = Hold();
        hold.Follow(Obsbot);
        var played = _starter.Played.Single();
        const string words = "Windows' camera privacy settings or a security program (such as Bitdefender's Webcam Protection) refused it (MediaCapture.InitializeAsync (SharedReadOnly, video only) failed: 0x80070005 E_ACCESSDENIED (Windows: \"Access is denied.\"))";

        await played.ProblemAsync(CameraProblemKind.Denied, words);
        await played.ProblemAsync(CameraProblemKind.Lost, CameraProblems.Stopped);
        await played.ProblemAsync(CameraProblemKind.Denied, words);
        await WaitAsync(() => hold.State!.Problem is not null);
        await Task.Delay(100);

        var line = WebcamHold.CannotOpen(LaptopCameras.Obsbot.Name, words);
        Assert.That(line, Does.EndWith("Without its picture open the camera may fall asleep, and its microphone then sends only silence. Turn off Device Auto Sleep in OBSBOT Center, or record another microphone. ClassScribe tries again every 5 seconds"));
        Assert.That(_logs.Problems, Is.EqualTo(new[] { line }));
        Assert.That(hold.State, Is.EqualTo(new WebcamState(LaptopCameras.Obsbot.Name, true, false, line, WebcamHold.NotOpenedShort)));
    }

    /// <summary>A camera process that ends by itself during the hold is started again 5 seconds later.</summary>
    [Test]
    public async Task A_camera_process_that_ends_is_started_again()
    {
        using var hold = Hold();
        hold.Follow(Obsbot);
        var first = _starter.Played.Single();
        await WaitAsync(() => first.Job is not null);

        first.Process.Exit(CameraProtocol.FailedExitCode);
        var moved = await _time.AdvanceUntilAsync(() => _starter.Started.Count == 2, Step, atMost: TimeSpan.FromSeconds(7));

        Assert.That(moved, Is.GreaterThanOrEqualTo(WebcamHold.RetryEvery));
        await WaitAsync(() => _starter.Played[1].Job is not null);
        Assert.That(_starter.Played[1].Job!.Kind, Is.EqualTo(CameraJobKind.Hold));
    }

    /// <summary>A camera process Windows does not start is a camera that could not be opened, and another start is tried 5 seconds later.</summary>
    [Test]
    public async Task A_camera_process_that_does_not_start_is_tried_again()
    {
        _starter.StartFailure = new System.ComponentModel.Win32Exception(5, "Access is denied");
        using var hold = Hold();

        hold.Follow(Obsbot);
        var said = _logs.Problems.ToList();
        _starter.StartFailure = null;
        await _time.AdvanceUntilAsync(() => _starter.Started.Count == 1, Step, atMost: TimeSpan.FromSeconds(7));

        Assert.That(said, Is.EqualTo(new[] { WebcamHold.CannotOpen(LaptopCameras.Obsbot.Name, "Windows did not start ClassScribe's camera process (Access is denied)") }));
    }

    /// <summary>The stop tells the camera process to stop, which lets the camera go; nothing is held after it, and a later pick starts nothing.</summary>
    [Test]
    public async Task The_stop_lets_the_camera_go()
    {
        using var hold = Hold();
        hold.Follow(Obsbot);
        var played = _starter.Played.Single();
        await WaitAsync(() => played.Job is not null);

        hold.Stop();
        hold.Follow(LaptopMicrophones.Array);
        hold.Follow(Obsbot with { Id = "another" });

        Assert.That(played.StopReceived, Is.True);
        Assert.That(played.Process.ExitCode, Is.EqualTo(CameraProtocol.DoneExitCode));
        Assert.That(_logs.Messages, Does.Contain("Let go of the picture of the camera \"OBSBOT Meet Flip StreamCamera\""));
        Assert.That(_starter.Started, Has.Count.EqualTo(1));
        Assert.That(hold.State!.Holding, Is.False);
    }

    /// <summary>
    /// Through the real track set, in person on the OBSBOT: the hold job is asked for before the microphone's capture opens,
    /// which waits for the first frame; the hold lasts through P and V off, and is let go at the stop.
    /// </summary>
    [Test]
    public async Task In_a_recording_the_hold_comes_before_the_microphone_and_lasts_to_the_stop()
    {
        using var folder = new TempFolder();
        var microphones = new FakeMicrophoneProvider().Connect(LaptopMicrophones.Array, Obsbot);
        var freezes = new FreezeWatch(new FakeSystemTimes(_time), _time, NullLogger<FreezeWatch>.Instance);
        var factory = new TrackSetFactory(new FakeCaptureProvider(), microphones, new FakeMicrophoneSessions(), new FakeMicrophoneUsageRecord(), new FakeProcessNames(),
            new FakeOwnProcesses(Laptop.OwnProcess), freezes, TestOptions.Create(new RecordingOptions { WakeCameraMicrophone = true }),
            TestOptions.Create(new TranscriptOptions()), _time, _logs, webcams: new WebcamHolds(_cameras, _starter, _time, _logs));
        using var recording = (TrackSet)factory.Create(new SourcePlan(RecordingMode.Microphone, Class: null, new MicrophoneChoice("OBSBOT")), new RecordingClock(_time));

        var starting = Task.Run(() => recording.Start(new RecordingFiles(folder.Path, "Lesson_2026-10-24_14-00-00Z"), TimeSpan.Zero));
        await WaitAsync(() => _starter.Played.Count == 1 && _starter.Played[0].Job is not null);
        var openedBeforeTheFrame = microphones.Opened.Count;
        await _starter.Played[0].StatusAsync(frames: 1);
        await starting.WaitAsync(TimeSpan.FromSeconds(10));

        recording.Pause();
        recording.SetMicrophone(false);
        recording.SetMicrophone(true);
        recording.Resume();
        var heldThroughPandV = !_starter.Played[0].StopReceived;
        recording.Stop();

        Assert.That(openedBeforeTheFrame, Is.Zero, "the microphone waited for the camera");
        Assert.That(microphones.Opened.Select(opened => opened.Id), Does.Contain(Obsbot.Id));
        Assert.That(heldThroughPandV, Is.True);
        Assert.That(_starter.Played[0].StopReceived, Is.True);
        Assert.That(_starter.Started, Has.Count.EqualTo(1));
        Assert.That(recording.Webcam!.Holding, Is.False);
    }

    /// <summary>A hold over the test's cameras and starter, on the manual clock.</summary>
    private WebcamHold Hold(bool wakeOn = true) => new(_cameras, _starter, wakeOn, _time, _logs.CreateLogger(WebcamHolds.LogCategory));

    /// <summary>Waits, in real time, up to 10 seconds for <paramref name="condition"/>.</summary>
    private static async Task WaitAsync(Func<bool> condition)
    {
        var deadline = Environment.TickCount64 + 10_000;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline) throw new TimeoutException("The expected condition was not met within 10 seconds.");
            await Task.Delay(5);
        }
    }
}
