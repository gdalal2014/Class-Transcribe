using ClassScribe.Audio;
using ClassScribe.Audio.Microphones;
using ClassScribe.Camera;
using ClassScribe.Commands;
using ClassScribe.Settings;

namespace ClassScribe.Tests;

/// <summary>
/// Menu choice 7's camera (teach-video 1.4, amendment 39): in person, after the microphone, the colour cameras are listed for
/// one to record the lesson's picture too, or Enter for none; a camera picked asks the picture's size, Enter for the setting,
/// 1 to 3 for the three sizes, T to type one. The pick holds for the session, the header names it with its size, and the
/// menu says the camera is no setting. With the class, no camera is asked about. Nothing is opened: the cameras are listed.
/// </summary>
[TestFixture]
public class CameraMenuTests
{
    private TempFolder _settingsFolder = null!;
    private TestSettings _settings = null!;
    private TestOptions<RecordingOptions> _recording = null!;
    private FakeMicrophoneProvider _microphones = null!;
    private FakeCameraProvider _cameras = null!;
    private SourceChoice _choice = null!;
    private FakeConsoleUi _ui = null!;

    /// <summary>This laptop's array and OBSBOT microphone and its three cameras, over settings with their defaults.</summary>
    [SetUp]
    public void SetUp()
    {
        _settingsFolder = new TempFolder();
        _settings = new TestSettings(_settingsFolder.Path);
        _recording = TestOptions.Create(new RecordingOptions());
        _microphones = new FakeMicrophoneProvider().Connect(LaptopMicrophones.Array, LaptopMicrophones.Obsbot);
        _cameras = new FakeCameraProvider();
        _choice = new SourceChoice(_recording);
        _ui = new FakeConsoleUi();
    }

    /// <summary>Deletes the settings folder.</summary>
    [TearDown]
    public void TearDown() => _settingsFolder.Dispose();

    /// <summary>Runs menu choice 7 until it is left, failing the test rather than hanging if it never is.</summary>
    private Task<int> RunAsync() =>
        new MicrophoneMenu(_microphones, _choice, _settings.File, _recording, _ui, _cameras).RunAsync().WaitAsync(TimeSpan.FromSeconds(15));

    /// <summary>The plan in person with the OBSBOT's microphone and <paramref name="camera"/>.</summary>
    private static SourcePlan InPerson(CameraChoice? camera) => new(RecordingMode.Microphone, null, new MicrophoneChoice(LaptopMicrophones.Obsbot.Name), camera);

    /// <summary>
    /// I, the OBSBOT's microphone, the OBSBOT's camera and 3 for 1080p: the plan carries the camera by its name at 1080p, the
    /// header names both, and the menu says how to keep the size and that the camera is asked for at each start.
    /// </summary>
    [Test]
    public async Task In_person_a_camera_and_a_size_are_picked_for_the_session()
    {
        _ui.Type('i', '2', '2', '3');

        Assert.That(await RunAsync(), Is.EqualTo(0));

        Assert.That(_choice.Current, Is.EqualTo(InPerson(new CameraChoice(LaptopCameras.Obsbot.Name, "1080p"))));
        Assert.That(_ui.Screen, Does.Contain("Camera, for a lesson video:"));
        Assert.That(_ui.Screen, Does.Contain("  1  Integrated Camera  (built in, front)"));
        Assert.That(_ui.Screen, Does.Contain("  2  OBSBOT Meet Flip StreamCamera  (USB)"));
        Assert.That(_ui.Screen, Does.Contain("Picture size: Enter for 720p (your setting), 1 480p, 2 720p, 3 1080p, T to type a size"));
        Assert.That(_choice.Description, Is.EqualTo($"your microphone \"{LaptopMicrophones.Obsbot.Name}\", in person, with the camera \"{LaptopCameras.Obsbot.Name}\" (1080p)"));
        Assert.That(_ui.Screen, Does.Contain("Recording:CameraResolution to \"1080p\""));
        Assert.That(_ui.Screen, Does.Contain(MicrophoneMenu.CameraIsAsked));
    }

    /// <summary>Enter at the size keeps Recording:CameraResolution, read again as it changes; the menu's own pick holds over it.</summary>
    [Test]
    public async Task Enter_at_the_size_keeps_the_setting()
    {
        _recording.Set(new RecordingOptions { CameraResolution = "480p" });
        _ui.Type('i', '\r', '1', '\r');

        await RunAsync();

        Assert.That(_choice.Current.Camera, Is.EqualTo(new CameraChoice(LaptopCameras.Integrated.Name, "480p")));
        Assert.That(_ui.Screen, Does.Contain("Picture size: Enter for 480p (your setting)"));
        Assert.That(_choice.PickedCameraResolution, Is.Null);
        _recording.Set(new RecordingOptions { CameraResolution = "1080p" });
        Assert.That(_choice.Current.Camera, Is.EqualTo(new CameraChoice(LaptopCameras.Integrated.Name, "1080p")), "the setting is read again");
        Assert.That(_ui.Screen, Does.Not.Contain("Recording:CameraResolution to"));
    }

    /// <summary>T types a size: one that is not a size gives the rule and asks again; one that is, is taken.</summary>
    [Test]
    public async Task T_types_a_size_and_a_wrong_one_is_asked_again()
    {
        _ui.Type('i', '\r', '2', 't');
        _ui.TypeLine("1025x576");
        _ui.Type('t');
        _ui.TypeLine(" 1024X576 ");

        await RunAsync();

        Assert.That(_ui.Screen, Does.Contain($"\"1025x576\" is not a size ClassScribe takes: {CameraSize.Rule}."));
        Assert.That(_choice.Current.Camera, Is.EqualTo(new CameraChoice(LaptopCameras.Obsbot.Name, "1024x576")));
        Assert.That(_choice.PickedCameraResolution, Is.EqualTo(new CameraSize(1024, 576)));
    }

    /// <summary>Enter at the camera records no camera, and asks no size.</summary>
    [Test]
    public async Task Enter_at_the_camera_records_none()
    {
        _ui.Type('i', '2', '\r');

        await RunAsync();

        Assert.That(_choice.Current, Is.EqualTo(InPerson(null)));
        Assert.That(_ui.Screen, Does.Not.Contain("Picture size"));
        Assert.That(_ui.Screen, Does.Not.Contain(MicrophoneMenu.CameraIsAsked));
    }

    /// <summary>Esc at the camera or at the size goes back without a change.</summary>
    [TestCase(false)]
    [TestCase(true)]
    public async Task Esc_at_the_camera_or_the_size_changes_nothing(bool atTheSize)
    {
        var before = _choice.Current;
        _ui.Type('i', '2');
        if (atTheSize) _ui.Type('2');
        _ui.Type('\u001b');

        await RunAsync();

        Assert.That(_choice.Current, Is.EqualTo(before));
    }

    /// <summary>With the class, no camera is asked about, and a camera picked before in person is dropped.</summary>
    [Test]
    public async Task With_the_class_no_camera_is_asked_and_one_picked_before_is_dropped()
    {
        _choice.PickMicrophone(RecordingMode.Microphone, null, null, LaptopCameras.Obsbot.Name, CameraSize.P1080);
        _ui.Type('w', '1');
        _ui.TypeLine("");

        await RunAsync();

        Assert.That(_choice.Current.Camera, Is.Null);
        Assert.That(_choice.PickedCameraResolution, Is.Null);
        Assert.That(_ui.Screen, Does.Not.Contain("Camera, for a lesson video:"));
    }

    /// <summary>No camera connected, or a list Windows does not give, is said, and Enter goes on without one.</summary>
    [Test]
    public async Task No_camera_is_said_and_Enter_goes_on_without_one()
    {
        _cameras.Connected = [];
        _ui.Type('i', '\r', '\r');

        await RunAsync();

        Assert.That(_ui.Screen, Does.Contain("  None is connected now."));
        Assert.That(_choice.Current.Camera, Is.Null);
    }
}
