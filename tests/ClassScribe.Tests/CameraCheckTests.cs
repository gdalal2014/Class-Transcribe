using ClassScribe.Audio.Microphones;
using ClassScribe.Camera;
using ClassScribe.Commands;
using ClassScribe.Settings;

namespace ClassScribe.Tests;

/// <summary>
/// The camera check (teach-video 6.16, camera-first step 11) and its set-up lines (<see cref="CameraSetupFacts"/>): the
/// cameras, which apps use one, the camera and size a lesson would take, and what the camera needs, all read without
/// opening a camera.
/// </summary>
[TestFixture]
public class CameraCheckTests
{
    private FakeConsoleUi _ui = null!;
    private FakeCameraProvider _cameras = null!;
    private FakeMicrophoneProvider _microphones = null!;
    private FakeCameraUsageRecord _usage = null!;
    private FakeCameraSystem _system = null!;
    private FakeRegistryValues _registry = null!;
    private TestOptions<RecordingOptions> _recording = null!;

    /// <summary>The laptop's cameras and microphones, the OBSBOT's microphone in its camera's container, and the settings as shipped.</summary>
    [SetUp]
    public void SetUp()
    {
        _ui = new FakeConsoleUi();
        _cameras = new FakeCameraProvider();
        _microphones = new FakeMicrophoneProvider();
        _microphones.Connect(LaptopMicrophones.Array with { ContainerId = Cameras.ThisComputer }, ObsbotMicrophone);
        _usage = new FakeCameraUsageRecord();
        _system = new FakeCameraSystem();
        _registry = new FakeRegistryValues();
        _recording = new TestOptions<RecordingOptions>(new RecordingOptions());
    }

    /// <summary>The OBSBOT's microphone, in its camera's container.</summary>
    private static Microphone ObsbotMicrophone => LaptopMicrophones.Obsbot with { ContainerId = LaptopCameras.ObsbotContainer };

    /// <summary>The check over the fakes.</summary>
    private CameraCheckCommand Command() =>
        new(_cameras, _microphones, _usage, new CameraSetupFacts(_system, _usage, _registry), _recording, _ui);

    /// <summary>The set-up lines over the fakes, with the wake as <paramref name="wakeOn"/> says.</summary>
    private Task<IReadOnlyList<CameraSetupLine>> SetupAsync(bool wakeOn = false) =>
        new CameraSetupFacts(_system, _usage, _registry).ReadAsync(LaptopCameras.All, [LaptopMicrophones.Array, ObsbotMicrophone], wakeOn, CancellationToken.None);

    /// <summary>
    /// The check lists the cameras by number, with a webcam's own microphone, says no app uses one, which camera and size a
    /// lesson would take, the set-up lines, and that it opened nothing; on this laptop as it is, with 0.
    /// </summary>
    [Test]
    public async Task The_check_lists_the_cameras_the_choice_and_the_set_up_and_opens_nothing()
    {
        var exitCode = await Command().RunAsync(new CameraCheckRequest());

        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(_ui.Screen, Does.Contain("Cameras (from Windows' own lists; none was opened):"));
        Assert.That(_ui.Screen, Does.Contain("  1  Integrated Camera  (built in, front)"));
        Assert.That(_ui.Screen, Does.Contain($"  2  OBSBOT Meet Flip StreamCamera  (USB; its microphone: {ObsbotMicrophone.Name})"));
        Assert.That(_ui.Screen, Does.Contain("  3  Pixel 10 Pro XL  (virtual"));
        Assert.That(_ui.Screen, Does.Contain("No app is using a camera now, as Windows' camera record shows."));
        Assert.That(_ui.Screen, Does.Contain("A lesson recorded with --camera would take the camera \"OBSBOT Meet Flip StreamCamera\", at 720p (1280x720; Recording:CameraResolution)."));
        Assert.That(_ui.Screen, Does.Contain("Set-up:"));
        Assert.That(_ui.Screen, Does.EndWith(CameraCheckCommand.OpensNothing));
        Assert.That(_microphones.Opened, Is.Empty, "no microphone was opened");
    }

    /// <summary>A camera named, and a size given, show what a lesson with them would take; a name that fits no camera says the lesson would wait.</summary>
    [Test]
    public async Task A_name_and_a_size_show_what_a_lesson_with_them_would_take()
    {
        await Command().RunAsync(new CameraCheckRequest("integrated", "1080p"));
        await Command().RunAsync(new CameraCheckRequest("Logitech", null));

        Assert.That(_ui.Screen, Does.Contain("A lesson recorded with --camera \"integrated\" would take the camera \"Integrated Camera\", at 1080p (1920x1080; --resolution)."));
        Assert.That(_ui.Screen, Does.Contain("No camera's name holds \"Logitech\" now: a lesson recorded with --camera \"Logitech\" would wait for it, at 720p (1280x720; Recording:CameraResolution)."));
    }

    /// <summary>Recording:CameraResolution gives the size when --resolution does not.</summary>
    [Test]
    public async Task The_setting_gives_the_size_without_resolution()
    {
        _recording.Set(new RecordingOptions { CameraResolution = "1024x576" });

        await Command().RunAsync(new CameraCheckRequest());

        Assert.That(_ui.Screen, Does.Contain("at 1024x576 (1024x576; Recording:CameraResolution)."));
    }

    /// <summary>The apps Windows' record shows using a camera now are named once each; a record that cannot be read says so.</summary>
    [Test]
    public async Task The_apps_using_a_camera_now_are_named()
    {
        _usage.Listing = new UsageRecordListing("Allow", "Allow",
        [
            new UsageEntry(@"NonPackaged\C:#Program Files#Zoom#Zoom.exe", "Zoom.exe", @"C:\Program Files\Zoom\Zoom.exe", 133000000000000000, 0),
            new UsageEntry("Microsoft.WindowsCamera_8wekyb3d8bbwe", "Microsoft.WindowsCamera_8wekyb3d8bbwe", null, 133000000000000000, 133000000000000100)
        ], null);

        await Command().RunAsync(new CameraCheckRequest());
        _usage.Listing = new UsageRecordListing(null, null, [], "IOException: busy");
        await Command().RunAsync(new CameraCheckRequest());

        Assert.That(_ui.Screen, Does.Contain("Using a camera now, as Windows' camera record shows: Zoom."));
        Assert.That(_ui.Screen, Does.Contain("Windows' camera record could not be read (IOException: busy), so it is not known which app uses a camera now."));
    }

    /// <summary>Cameras that cannot be listed, or none connected, are said; the lesson would wait for one.</summary>
    [Test]
    public async Task No_cameras_or_a_list_that_cannot_be_read_are_said()
    {
        _cameras.Connected = [];
        await Command().RunAsync(new CameraCheckRequest());
        _cameras.Problem = "The camera needs Windows 10 version 2004 (build 19041) or later; this is Windows 10.0.18363";
        await Command().RunAsync(new CameraCheckRequest());

        Assert.That(_ui.Screen, Does.Contain("  None is connected now."));
        Assert.That(_ui.Screen, Does.Contain("No USB camera or built-in front camera is connected now: a lesson recorded with --camera would wait for one"));
        Assert.That(_ui.Screen, Does.Contain("  The cameras could not be listed: The camera needs Windows 10 version 2004 (build 19041) or later; this is Windows 10.0.18363."));
    }

    /// <summary>A set-up problem, such as Windows denying desktop apps the camera, makes the check end with 1.</summary>
    [Test]
    public async Task A_set_up_problem_ends_the_check_with_1()
    {
        _usage.Listing = new UsageRecordListing("Allow", "Deny", [], null);

        Assert.That(await Command().RunAsync(new CameraCheckRequest()), Is.EqualTo(1));
        Assert.That(_ui.Screen, Does.Contain("PROBLEM  Windows does not let desktop apps use the camera: turn on \"Let desktop apps access your camera\" in Settings > Privacy & security > Camera (ms-settings:privacy-webcam)"));
    }

    /// <summary>
    /// The set-up lines in order, on this laptop as it is: Windows' version, the privacy switches, the encoder, Bitdefender
    /// with its Webcam Protection step, the OBSBOT's own microphone, and the wake, off by default.
    /// </summary>
    [Test]
    public async Task The_set_up_lines_on_this_laptop()
    {
        var lines = await SetupAsync();

        Assert.That(lines.Select(line => line.ToString()), Is.EqualTo(new[]
        {
            "OK       Windows 10.0.26300, which the camera needs at 10.0.19041 (version 2004) or later",
            "OK       Windows lets desktop apps use the camera",
            "OK       An H.264 video encoder is installed, which a lesson's video needs",
            "NOTE     Bitdefender Antivirus watches the camera: when its Webcam Protection asks about ClassScribe, allow it (Bitdefender: Privacy > Video & Audio Protection > Webcam Protection)",
            $"NOTE     The camera \"OBSBOT Meet Flip StreamCamera\" has its own microphone: \"{ObsbotMicrophone.Name}\"",
            "OFF      Recording:WakeCameraMicrophone is off: a webcam that sleeps by itself, such as the OBSBOT with Device Auto Sleep on, is not woken; turn off its Device Auto Sleep in OBSBOT Center"
        }));
    }

    /// <summary>Windows older than 10.0.19041 is a problem; the wake turned on is said.</summary>
    [Test]
    public async Task Old_Windows_is_a_problem_and_the_wake_turned_on_is_said()
    {
        _system.WindowsVersion = new Version(10, 0, 18363);

        var lines = await SetupAsync(wakeOn: true);

        Assert.That(lines[0], Is.EqualTo(new CameraSetupLine(CameraSetupState.Problem, "Windows 10.0.18363 is older than 10.0.19041 (version 2004), which the camera needs")));
        Assert.That(lines[^1].State, Is.EqualTo(CameraSetupState.Note));
        Assert.That(lines[^1].Text, Does.StartWith("Recording:WakeCameraMicrophone is on"));
    }

    /// <summary>Each privacy switch set to Deny is a problem naming the switch: the whole device's, all apps', desktop apps'.</summary>
    [Test]
    public async Task Each_privacy_switch_set_to_deny_is_a_problem()
    {
        _registry.SetLocalMachine(CameraSetupFacts.WebcamKey, "Value", "Deny");
        var device = (await SetupAsync())[1];
        _registry.SetLocalMachine(CameraSetupFacts.WebcamKey, "Value", "Allow");
        _usage.Listing = new UsageRecordListing("deny", null, [], null);
        var everyApp = (await SetupAsync())[1];
        _usage.Listing = new UsageRecordListing(null, null, [], "SecurityException: denied");
        var unread = (await SetupAsync())[1];

        Assert.That(device, Is.EqualTo(new CameraSetupLine(CameraSetupState.Problem, "Windows lets no app on this device use the camera: turn on Camera access in Settings > Privacy & security > Camera (ms-settings:privacy-webcam)")));
        Assert.That(everyApp, Is.EqualTo(new CameraSetupLine(CameraSetupState.Problem, "Windows lets no app use the camera: turn on Camera access in Settings > Privacy & security > Camera (ms-settings:privacy-webcam)")));
        Assert.That(unread.State, Is.EqualTo(CameraSetupState.Note));
        Assert.That(unread.Text, Does.Contain("could not be read (SecurityException: denied)"));
    }

    /// <summary>No H.264 encoder is a problem with the Media Feature Pack named; an encoder Windows does not say about is a note.</summary>
    [Test]
    public async Task No_encoder_is_a_problem_and_an_unknown_one_a_note()
    {
        _system.HasH264Encoder = false;
        var none = (await SetupAsync())[2];
        _system.HasH264Encoder = null;
        var unknown = (await SetupAsync())[2];

        Assert.That(none.State, Is.EqualTo(CameraSetupState.Problem));
        Assert.That(none.Text, Does.Contain("Media Feature Pack"));
        Assert.That(unknown, Is.EqualTo(new CameraSetupLine(CameraSetupState.Note, "Windows did not say whether an H.264 video encoder is installed")));
    }

    /// <summary>Another security program gets the general advice; an unknown one, the advice for any program; none, no line.</summary>
    [Test]
    public async Task Other_security_programs_get_the_general_advice()
    {
        _system.Programs = ["Windows Defender Antivirus"];
        var defender = await SetupAsync();
        _system.Programs = null;
        var unknown = await SetupAsync();
        _system.Programs = [];
        var none = await SetupAsync();

        Assert.That(defender[3], Is.EqualTo(new CameraSetupLine(CameraSetupState.Note, "Windows Defender Antivirus is a security program here: if it asks about ClassScribe and the camera, allow it")));
        Assert.That(unknown[3].Text, Does.StartWith("Windows' Security Center did not say which security program runs"));
        Assert.That(none.Select(line => line.Text), Has.None.Contains("security program"));
    }
}
