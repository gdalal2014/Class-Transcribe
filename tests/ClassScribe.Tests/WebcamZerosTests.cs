using ClassScribe.Audio;
using ClassScribe.Audio.Microphones;
using ClassScribe.Camera;
using ClassScribe.Commands;
using ClassScribe.Settings;
using Microsoft.Extensions.Logging;

namespace ClassScribe.Tests;

/// <summary>
/// The webcam zeros measure (camera-first 3.4): exact zeros counted at the device's own depth, before the 16-bit conversion,
/// so that Windows' faint residue in a quiet room counts as sound though it rounds to zero in the track; packets Windows
/// flags as silent count as neither; each capture counts afresh.
/// </summary>
public partial class RecordingSessionTests
{
    /// <summary>
    /// Windows' residue in a quiet room (about -120 dBFS, as finding 4 measured below the device's own step) is sound at
    /// the device's depth, while the 16-bit measure, which the track's rounding makes zero, counts it as zeros.
    /// </summary>
    [Test]
    public void The_device_depth_counts_Windows_residue_as_sound()
    {
        var capture = _provider.Enqueue(new FakeAudioCapture(Float48KStereo) { DeliversContinuously = true });
        using var session = CreateSession();
        const float residue = 1e-6f;

        session.Start(_files);
        capture.Deliver(TestAudio.Floats(residue, -residue, 0f, 0f));
        _time.Advance(RecordingSession.OnlyZerosAfter);
        capture.Deliver(TestAudio.Floats(residue, -residue, 0f, 0f));
        var status = session.GetStatus();
        session.Stop();

        Assert.That(status.OnlyZeros, Is.True, "rounded to 16 bits the residue is zero");
        Assert.That(status.RawSoundSinceOpened, Is.True);
        Assert.That(status.RawZerosFor, Is.EqualTo(TimeSpan.Zero));
    }

    /// <summary>
    /// Exact zeros at the device's depth, a negative zero included, are counted from the open; the first sample other than
    /// zero ends the count, and the capture is numbered.
    /// </summary>
    [Test]
    public void Exact_zeros_at_the_device_depth_are_counted_from_the_open()
    {
        var capture = _provider.Enqueue(new FakeAudioCapture(Float48KStereo) { DeliversContinuously = true });
        using var session = CreateSession();

        session.Start(_files);
        var beforeAPacket = session.GetStatus();
        capture.Deliver(TestAudio.Floats(0f, -0f, 0f, 0f));
        _time.Advance(RecordingSession.WebcamZerosAtStart);
        capture.Deliver(TestAudio.Floats(0f, -0f));
        var zeros = session.GetStatus();
        capture.Deliver(TestAudio.Floats(0f, 0.25f));
        _time.Advance(TimeSpan.FromSeconds(3));
        var after = session.GetStatus();
        session.Stop();

        Assert.That(beforeAPacket.RawZerosFor, Is.Null, "nothing has arrived yet");
        Assert.That(zeros.RawZerosFor, Is.EqualTo(RecordingSession.WebcamZerosAtStart));
        Assert.That(zeros.RawSoundSinceOpened, Is.False);
        Assert.That(zeros.CaptureNumber, Is.EqualTo(1));
        Assert.That(after.RawSoundSinceOpened, Is.True);
        Assert.That(after.RawZerosFor, Is.EqualTo(TimeSpan.FromSeconds(3)));
    }

    /// <summary>A packet Windows flags as silent is Windows' silence, not the device's: it is said, and counts as neither sound nor the device's zeros.</summary>
    [Test]
    public void A_packet_Windows_flags_silent_counts_as_neither()
    {
        var capture = _provider.Enqueue(new FakeAudioCapture(Pcm16KMono) { DeliversContinuously = true });
        using var session = CreateSession();

        session.Start(_files);
        capture.Deliver(TestAudio.Shorts(0, 0), flags: PacketFlags.Silent);
        var silent = session.GetStatus();
        capture.Deliver(TestAudio.Shorts(0, 3));
        var sound = session.GetStatus();
        session.Stop();

        Assert.That(silent.WindowsSilent, Is.True);
        Assert.That(silent.RawSoundSinceOpened, Is.False);
        Assert.That(sound.WindowsSilent, Is.False);
        Assert.That(sound.RawSoundSinceOpened, Is.True);
    }
}

/// <summary>
/// The webcam zeros rules in the run's checks (camera-first 3.4), in place of the warning about only silence for a
/// microphone that belongs to a webcam: at the start after 60 seconds of zeros since the open, once a capture, worded by
/// whether the hold has the camera awake; later after 2 minutes of zeros after sound, once a recording, never while the
/// hold's frames arrive; a Notice with the class and an Alert in person; Windows' silence and a muted microphone left to
/// today's warning; every other microphone keeping today's warning; and finding 4's twelve minutes of an awake OBSBOT, with
/// runs of zeros up to 18.7 seconds, giving no line.
/// </summary>
public partial class RecordingMonitorTests
{
    /// <summary>The OBSBOT as the hold knows it, with no hold.</summary>
    private static readonly WebcamState ObsbotWebcam = new(LaptopCameras.Obsbot.Name, Holding: false, HasFrames: false);

    /// <summary>The start rule's words with no hold.</summary>
    private const string StartLine = "The microphone of the camera \"OBSBOT Meet Flip StreamCamera\" has sent only exact zeros since it opened, 1 minute ago. If the camera's light is off, the camera is asleep and its microphone off: unfold its lens or wake it, and turn off Device Auto Sleep in OBSBOT Center or turn on Recording:WakeCameraMicrophone. If the light is on, the room is just quiet: this camera sends exact zeros in silence";

    /// <summary>The later rule's words.</summary>
    private const string LaterLine = "The microphone of the camera \"OBSBOT Meet Flip StreamCamera\" has sent only exact zeros for 2 minutes. If its light is off, the camera has fallen asleep (an open microphone does not keep it awake) and your voice is not being recorded: turn off Device Auto Sleep in OBSBOT Center, or turn on Recording:WakeCameraMicrophone. If the light is on, the room is quiet and nothing is lost";

    /// <summary>
    /// The start rule: 60 seconds of exact zeros since the open, and no other sample, give the line once and a Notice with
    /// the class while they last; sound ends the Notice. Today's warning is not given for a webcam's microphone, though the
    /// 16-bit measure says only zeros.
    /// </summary>
    [Test]
    public void The_start_rule_comes_at_60_seconds_and_is_a_Notice_with_the_class()
    {
        var recording = BeginWithWebcam();

        var before = Tick(WithTracks(microphone: Webcam(TimeSpan.FromSeconds(59))));
        var start = Tick(WithTracks(microphone: Webcam(TimeSpan.FromSeconds(60))));
        var still = Tick(WithTracks(microphone: Webcam(RecordingSession.OnlyZerosAfter) with { OnlyZeros = true }));
        var sound = Tick(WithTracks(microphone: Webcam(TimeSpan.Zero, sound: true)));

        Assert.That(Warnings(before), Is.Empty);
        Assert.That(Warnings(start), Is.EqualTo(new[] { StartLine }));
        Assert.That(start.Attention, Is.EqualTo(new Attention(AttentionLevel.Notice, "only zeros from the webcam's microphone", StartLine, 1)));
        Assert.That(Warnings(still), Is.Empty, "said once, and today's warning not at all");
        Assert.That(still.Attention.Level, Is.EqualTo(AttentionLevel.Notice));
        Assert.That(sound.Attention.Level, Is.EqualTo(AttentionLevel.None));
        Assert.That(recording.Webcam, Is.EqualTo(ObsbotWebcam));
    }

    /// <summary>With the hold's frames arriving the camera is awake, so the start rule's words point at the microphone's own switch and Windows.</summary>
    [Test]
    public void The_start_rule_with_the_camera_awake_points_elsewhere()
    {
        var recording = BeginWithWebcam();
        recording.Webcam = ObsbotWebcam with { Holding = true, HasFrames = true };

        var start = Tick(WithTracks(microphone: Webcam(TimeSpan.FromSeconds(60))));

        Assert.That(Warnings(start), Is.EqualTo(new[]
        {
            "The microphone of the camera \"OBSBOT Meet Flip StreamCamera\" has sent only exact zeros since it opened, 1 minute ago, although its camera is open and awake. Check that its microphone is on in OBSBOT Center, and that Windows lets desktop apps use the microphone"
        }));
    }

    /// <summary>The start rule is said once a capture: a new capture with the same zeros says it again.</summary>
    [Test]
    public void The_start_rule_is_said_once_a_capture()
    {
        BeginWithWebcam();

        var first = Tick(WithTracks(microphone: Webcam(TimeSpan.FromSeconds(60), capture: 1)));
        var same = Tick(WithTracks(microphone: Webcam(TimeSpan.FromSeconds(61), capture: 1)));
        var another = Tick(WithTracks(microphone: Webcam(TimeSpan.FromSeconds(60), capture: 2)));

        Assert.That(Warnings(first), Has.Length.EqualTo(1));
        Assert.That(Warnings(same), Is.Empty);
        Assert.That(Warnings(another), Is.EqualTo(new[] { StartLine }));
    }

    /// <summary>
    /// The later rule: after sound, 2 minutes of exact zeros give the line once and a Notice that ends when sound returns;
    /// a later stretch of 2 minutes says nothing, once a recording. Before 2 minutes nothing is said: a quiet class is not
    /// a sleeping camera.
    /// </summary>
    [Test]
    public void The_later_rule_comes_at_2_minutes_after_sound_once_a_recording()
    {
        BeginWithWebcam();

        var quiet = Tick(WithTracks(microphone: Webcam(TimeSpan.FromSeconds(90), sound: true)));
        var later = Tick(WithTracks(microphone: Webcam(RecordingSession.WebcamZerosAfterSound, sound: true)));
        var still = Tick(WithTracks(microphone: Webcam(RecordingSession.WebcamZerosAfterSound + TimeSpan.FromSeconds(1), sound: true)));
        var back = Tick(WithTracks(microphone: Webcam(TimeSpan.Zero, sound: true)));
        var again = Tick(WithTracks(microphone: Webcam(TimeSpan.FromMinutes(5), sound: true)));

        Assert.That(Warnings(quiet), Is.Empty);
        Assert.That(Warnings(later), Is.EqualTo(new[] { LaterLine }));
        Assert.That(later.Attention, Is.EqualTo(new Attention(AttentionLevel.Notice, "only zeros from the webcam's microphone", LaterLine, 1)));
        Assert.That(Warnings(still), Is.Empty);
        Assert.That(still.Attention.Level, Is.EqualTo(AttentionLevel.Notice));
        Assert.That(back.Attention.Level, Is.EqualTo(AttentionLevel.None));
        Assert.That(Warnings(again), Is.Empty, "once a recording");
        Assert.That(again.Attention.Level, Is.EqualTo(AttentionLevel.None));
    }

    /// <summary>While the hold's frames arrive the camera cannot be asleep, so zeros after sound are the room's quiet: the later rule is off.</summary>
    [Test]
    public void The_later_rule_is_off_while_the_holds_frames_arrive()
    {
        var recording = BeginWithWebcam();
        recording.Webcam = ObsbotWebcam with { Holding = true, HasFrames = true };

        var quiet = Tick(WithTracks(microphone: Webcam(TimeSpan.FromMinutes(5), sound: true)));

        Assert.That(Warnings(quiet), Is.Empty);
        Assert.That(quiet.Attention.Level, Is.EqualTo(AttentionLevel.None));
    }

    /// <summary>In person the whole recording is the webcam's microphone, so the rule is an Alert.</summary>
    [Test]
    public void In_person_the_start_rule_is_an_Alert()
    {
        var recording = new FakeMicrophoneRecording(RecordingMode.Microphone, Obsbot()) { Webcam = ObsbotWebcam };
        Begin(new RecordingOptions { StatusIntervalSeconds = 3600, SilenceWarningSeconds = 3600 }, InPerson(), recording);
        Look(InPersonTracks(Webcam(TimeSpan.FromSeconds(1))));

        var start = Tick(InPersonTracks(Webcam(TimeSpan.FromSeconds(60))));

        Assert.That(start.Attention, Is.EqualTo(new Attention(AttentionLevel.Alert, "ONLY ZEROS FROM THE WEBCAM'S MICROPHONE", StartLine, 1)));
    }

    /// <summary>Packets Windows flags as silent are Windows' silence, not the camera's: today's warning speaks for them, and the webcam rule not at all.</summary>
    [Test]
    public void Windows_silence_is_left_to_todays_warning()
    {
        BeginWithWebcam();

        var start = Tick(WithTracks(microphone: Webcam(TimeSpan.FromSeconds(60)) with { WindowsSilent = true }));
        var zeros = Tick(WithTracks(microphone: Webcam(RecordingSession.OnlyZerosAfter) with { WindowsSilent = true, OnlyZeros = true }));

        Assert.That(Warnings(start), Is.Empty);
        Assert.That(Warnings(zeros).Single(), Does.StartWith("Only silence from the microphone"));
    }

    /// <summary>A webcam's microphone muted in Windows is left to today's warning, which says it is muted.</summary>
    [Test]
    public void A_muted_webcam_microphone_is_left_to_todays_warning()
    {
        var recording = BeginWithWebcam();
        recording.MutedInWindows = true;

        var start = Tick(WithTracks(microphone: Webcam(TimeSpan.FromSeconds(60))));
        var zeros = Tick(WithTracks(microphone: Webcam(RecordingSession.OnlyZerosAfter) with { OnlyZeros = true }));

        Assert.That(Warnings(start), Is.Empty);
        Assert.That(Warnings(zeros).Single(), Does.EndWith("it is muted in Windows (the microphone key, or Sound settings > Input)"));
    }

    /// <summary>Every other microphone keeps today's warning: zeros at the device's depth say nothing, the 2-minute flag says what it always said.</summary>
    [Test]
    public void Every_other_microphone_keeps_todays_warning()
    {
        BeginWithMicrophone(ClassAppMute.Unknown, rule: new FollowRule.None());
        Look(WithTracks());

        var start = Tick(WithTracks(microphone: Webcam(TimeSpan.FromSeconds(60))));
        var zeros = Tick(WithTracks(microphone: Webcam(RecordingSession.OnlyZerosAfter) with { OnlyZeros = true }));

        Assert.That(Warnings(start), Is.Empty);
        Assert.That(Warnings(zeros).Single(), Does.StartWith("Only silence from the microphone"));
    }

    /// <summary>The hold's problem is a Notice with the class while it lasts, in its own short words.</summary>
    [Test]
    public void The_holds_problem_is_a_Notice_while_it_lasts()
    {
        var recording = BeginWithWebcam();
        var stopped = WebcamHold.Stopped(LaptopCameras.Obsbot.Name);
        recording.Webcam = ObsbotWebcam with { Holding = true, Problem = stopped, ProblemShort = WebcamHold.StoppedShort };

        var problem = Tick(WithTracks(microphone: Webcam(TimeSpan.Zero, sound: true)));
        recording.Webcam = ObsbotWebcam with { Holding = true, HasFrames = true };
        var back = Tick(WithTracks(microphone: Webcam(TimeSpan.Zero, sound: true)));

        Assert.That(problem.Attention, Is.EqualTo(new Attention(AttentionLevel.Notice, WebcamHold.StoppedShort, stopped, 1)));
        Assert.That(back.Attention.Level, Is.EqualTo(AttentionLevel.None));
    }

    /// <summary>
    /// Finding 4 replayed: twelve awake minutes of the OBSBOT with speech and quiet, its noise reduction sending exact zeros
    /// in silence (seconds 1 to 4 after the open, and runs up to 18.7 seconds, 77 seconds in all), give no line about zeros
    /// and nothing to attend to, with no hold. Today's warning would have nothing to say either, but the old 3-second
    /// trigger would have fired at every run.
    /// </summary>
    [Test]
    public void Finding_4s_twelve_awake_minutes_give_no_line()
    {
        BeginWithWebcam();
        (double From, double Length)[] runs = [(0, 4), (63, 18.7), (171, 9.5), (260, 12.4), (402, 6.1), (515, 14.8), (640, 11.5)];
        Assert.That(runs.Sum(run => run.Length), Is.EqualTo(77).Within(0.01), "77 seconds of zeros, as finding 4 measured");

        var ticks = Run(720, seconds =>
        {
            var at = seconds;
            var lastSound = runs.Where(run => at >= run.From && at < run.From + run.Length).Select(run => (double?)run.From).FirstOrDefault();
            var heardBefore = at >= 4;
            var zeros = lastSound is { } from ? TimeSpan.FromSeconds(at - from) : TimeSpan.Zero;
            return WithTracks(microphone: Webcam(zeros, sound: heardBefore && !(lastSound == 0)));
        });

        Assert.That(Warnings(ticks).Where(line => line.Contains("zeros", StringComparison.Ordinal) || line.Contains("silence", StringComparison.Ordinal)), Is.Empty);
        Assert.That(ticks.Select(tick => tick.Attention.Short), Has.None.EqualTo("only zeros from the webcam's microphone"));
    }

    /// <summary>Begins with the class and the OBSBOT's microphone, following no app, with no hold, and looks once.</summary>
    private FakeMicrophoneRecording BeginWithWebcam()
    {
        var recording = BeginWithMicrophone(ClassAppMute.Unknown, rule: new FollowRule.None());
        recording.Microphone = Obsbot();
        recording.Webcam = ObsbotWebcam;
        Look(WithTracks(microphone: Webcam(TimeSpan.FromSeconds(1))));
        return recording;
    }

    /// <summary>The OBSBOT's microphone, in its camera's container.</summary>
    private static Microphone Obsbot() => LaptopMicrophones.Obsbot with { ContainerId = LaptopCameras.ObsbotContainer };

    /// <summary>
    /// The webcam's microphone track: <paramref name="zeros"/> of exact zeros at the device's depth so far, after sound since
    /// its open when <paramref name="sound"/>, in capture number <paramref name="capture"/>.
    /// </summary>
    private static TrackSnapshot Webcam(TimeSpan zeros, bool sound = false, int capture = 1) =>
        FakeRecording.Track(LaptopMicrophones.Obsbot.Name, deliversContinuously: true) with
        {
            RawZerosFor = zeros, RawSoundSinceOpened = sound, CaptureNumber = capture
        };
}
