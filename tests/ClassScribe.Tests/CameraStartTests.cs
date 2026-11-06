using ClassScribe.Application.Audio;
using ClassScribe.Application.Camera;
using ClassScribe.Application.Commands;
using ClassScribe.Application.Runs;
using ClassScribe.Application.Settings;
using ClassScribe.Cli.Commands;
using ClassScribe.Core.Audio;
using ClassScribe.Core.Audio.Microphones;
using ClassScribe.Core.Audio.Timeline;
using ClassScribe.Core.Camera;
using ClassScribe.Core.Runs;

namespace ClassScribe.Tests;

/// <summary>
/// The start of a run with a camera (teach-video 1.4 and 1.6, amendments 17 and 39): the picture's size is resolved into the
/// request; in this build the camera's recording is not built, so the menu says so and offers the sound only, and the
/// command line starts nothing, exit 1; once a build can record it, the menu asks Enter or S, and the command line goes on.
/// A set-up problem refuses the camera first. Nothing is opened.
/// </summary>
[TestFixture]
public class CameraStartTests
{
    private FakeConsoleUi _ui = null!;
    private FakeCameraProvider _cameras = null!;
    private FakeCameraSystem _system = null!;
    private FakeCameraUsageRecord _usage = null!;
    private TestOptions<RecordingOptions> _recording = null!;
    private SourceChoice _choice = null!;
    private ManualTimeProvider _time = null!;
    private RecordingLogger<CameraStart> _log = null!;

    /// <summary>This laptop's cameras, set up as the camera needs, over settings with their defaults, on the manual clock.</summary>
    [SetUp]
    public void SetUp()
    {
        _ui = new FakeConsoleUi();
        _cameras = new FakeCameraProvider();
        _system = new FakeCameraSystem();
        _usage = new FakeCameraUsageRecord();
        _recording = TestOptions.Create(new RecordingOptions());
        _choice = new SourceChoice(_recording);
        _time = new ManualTimeProvider(TestRuns.Start);
        _log = new RecordingLogger<CameraStart>();
    }

    /// <summary>The start over the fakes, with <paramref name="recorder"/>, this build's when null.</summary>
    private CameraStart Start(ICameraRecorder? recorder = null) =>
        new(_cameras, recorder ?? new CameraRecorderNotBuilt(), new CameraSetupFacts(_system, _usage, new FakeRegistryValues()), _choice, _ui, _time, _log);

    /// <summary>A request to record "Tajwid 4" in person with the OBSBOT's microphone and <paramref name="camera"/>.</summary>
    private static RecordRequest Lesson(CameraChoice? camera) =>
        TestRuns.Request(TestRuns.Id(1), "Tajwid 4") with { Source = new SourcePlan(RecordingMode.Microphone, null, new MicrophoneChoice("OBSBOT"), camera) };

    /// <summary>The menu, in this build: the camera's name, its size and why it cannot record; Enter records the sound only.</summary>
    [Test]
    public async Task In_this_build_the_menu_says_why_and_Enter_records_the_sound_only()
    {
        _ui.Type('\r');

        var decided = await Start().DecideAsync(Lesson(new CameraChoice("OBSBOT")), StartOrigin.Menu);

        Assert.That(decided, Is.EqualTo(new CameraDecision.Go(Lesson(null))));
        Assert.That(_ui.Screen, Is.EqualTo($"Camera \"OBSBOT Meet Flip StreamCamera\" (720p): {CameraRecorderNotBuilt.Words}. {CameraStart.SoundOnlyOffer}"));
    }

    /// <summary>Esc at that line goes back, and starts nothing; S is not offered there.</summary>
    [Test]
    public async Task In_this_build_Esc_goes_back_and_S_is_not_offered()
    {
        _ui.Type('s', '\u001b');

        Assert.That(await Start().DecideAsync(Lesson(new CameraChoice(null)), StartOrigin.Menu), Is.EqualTo(new CameraDecision.Back()));
    }

    /// <summary>The command line, in this build: why, and that nothing was recorded, exit 1; nothing is asked.</summary>
    [TestCase(StartOrigin.CommandLine)]
    [TestCase(StartOrigin.StartupReport)]
    [TestCase(StartOrigin.AutoContinue)]
    public async Task In_this_build_any_other_start_is_refused_with_1(StartOrigin origin)
    {
        var decided = await Start().DecideAsync(Lesson(new CameraChoice("OBSBOT", "1080p")), origin);

        Assert.That(decided, Is.EqualTo(new CameraDecision.Refused(1)));
        Assert.That(_ui.Screen, Is.EqualTo($"{CameraRecorderNotBuilt.Words}. {ICameraPrompt.NothingStarted}"));
        Assert.That(_ui.KeyReads, Is.Zero, "nothing is asked");
    }

    /// <summary>
    /// Once a build records the picture, the menu asks amendment 17's line: Enter records with the camera, its size resolved
    /// into the request; S records the sound only.
    /// </summary>
    [Test]
    public async Task A_build_that_records_asks_Enter_or_S_in_the_menu()
    {
        _ui.Type('\r');
        var withCamera = await Start(new ReadyRecorder()).DecideAsync(Lesson(new CameraChoice("OBSBOT")), StartOrigin.Menu);
        _ui.Type('s');
        var soundOnly = await Start(new ReadyRecorder()).DecideAsync(Lesson(new CameraChoice("OBSBOT")), StartOrigin.Menu);

        Assert.That(withCamera, Is.EqualTo(new CameraDecision.Go(Lesson(new CameraChoice("OBSBOT", "720p")))));
        Assert.That(soundOnly, Is.EqualTo(new CameraDecision.Go(Lesson(null))));
        Assert.That(_ui.Screen, Does.Contain($"Camera \"OBSBOT Meet Flip StreamCamera\" (720p): {CameraStart.Prompt}"));
    }

    /// <summary>Once a build records the picture, the command line goes on with the camera and its size, asking nothing.</summary>
    [Test]
    public async Task A_build_that_records_goes_on_from_the_command_line()
    {
        var decided = await Start(new ReadyRecorder()).DecideAsync(Lesson(new CameraChoice(null, "1024x576")), StartOrigin.CommandLine);

        Assert.That(decided, Is.EqualTo(new CameraDecision.Go(Lesson(new CameraChoice(null, "1024x576")))));
        Assert.That(_ui.Screen, Is.Empty);
    }

    /// <summary>The size: the request's own (--resolution) wins, then menu choice 7's pick, then the setting, then 720p.</summary>
    [Test]
    public async Task The_size_is_the_requests_else_the_menus_else_the_settings()
    {
        _recording.Set(new RecordingOptions { CameraResolution = "480p" });
        var fromSetting = await Start(new ReadyRecorder()).DecideAsync(Lesson(new CameraChoice(null)), StartOrigin.CommandLine);
        _choice.PickMicrophone(RecordingMode.Microphone, null, null, "OBSBOT", CameraSize.P1080);
        var fromMenu = await Start(new ReadyRecorder()).DecideAsync(Lesson(new CameraChoice(null)), StartOrigin.CommandLine);
        var fromRequest = await Start(new ReadyRecorder()).DecideAsync(Lesson(new CameraChoice(null, "720p")), StartOrigin.CommandLine);

        Assert.That(((CameraDecision.Go)fromSetting).Request.Source.Camera!.Resolution, Is.EqualTo("480p"));
        Assert.That(((CameraDecision.Go)fromMenu).Request.Source.Camera!.Resolution, Is.EqualTo("1080p"));
        Assert.That(((CameraDecision.Go)fromRequest).Request.Source.Camera!.Resolution, Is.EqualTo("720p"));
    }

    /// <summary>A set-up problem, such as Windows denying desktop apps the camera, refuses the camera even in a build that records it.</summary>
    [Test]
    public async Task A_set_up_problem_refuses_the_camera_first()
    {
        _usage.Listing = new UsageRecordListing("Allow", "Deny", [], null);

        var decided = await Start(new ReadyRecorder()).DecideAsync(Lesson(new CameraChoice("OBSBOT")), StartOrigin.CommandLine);

        Assert.That(decided, Is.EqualTo(new CameraDecision.Refused(1)));
        Assert.That(_ui.Screen, Does.StartWith("Windows does not let desktop apps use the camera"));
    }

    /// <summary>
    /// In this build too, a set-up problem is said ahead of the build's own reason, so that what the user can fix (a privacy
    /// switch, a missing encoder) is never hidden behind "not built yet".
    /// </summary>
    [Test]
    public async Task In_this_build_a_set_up_problem_is_said_before_not_built_yet()
    {
        _usage.Listing = new UsageRecordListing("Allow", "Deny", [], null);

        var decided = await Start(new CameraRecorderNotBuilt()).DecideAsync(Lesson(new CameraChoice("OBSBOT")), StartOrigin.CommandLine);

        Assert.That(decided, Is.EqualTo(new CameraDecision.Refused(1)));
        Assert.That(_ui.Screen, Does.StartWith("Windows does not let desktop apps use the camera"));
        Assert.That(_ui.Screen, Does.Not.Contain(CameraRecorderNotBuilt.Words));
    }

    /// <summary>A camera that is not connected is named by what was asked, and that it is not connected; none at all says what would be taken.</summary>
    [Test]
    public async Task A_camera_not_connected_is_named_as_asked()
    {
        _cameras.Connected = [LaptopCameras.Pixel];
        _ui.Type('\u001b', '\u001b');

        await Start(new ReadyRecorder()).DecideAsync(Lesson(new CameraChoice("Logitech")), StartOrigin.Menu);
        await Start(new ReadyRecorder()).DecideAsync(Lesson(new CameraChoice(null)), StartOrigin.Menu);

        Assert.That(_ui.Screen, Does.Contain($"Camera \"Logitech\" (not connected now; 720p): {CameraStart.Prompt}"));
        Assert.That(_ui.Screen, Does.Contain($"Camera (none connected now: a USB camera, else the built-in front camera; 720p): {CameraStart.Prompt}"));
    }

    /// <summary>
    /// A camera list Windows does not answer holds the start 5 seconds at most (release 0.5.0 review, 3.1): the list then
    /// says it could not be read, the start line shows why in place of "not connected now", with or without a name asked
    /// for, and the menu asks as it would; the recorder looks for the camera itself once it records.
    /// </summary>
    [Test]
    public async Task A_camera_list_that_never_answers_holds_the_start_5_seconds_at_most()
    {
        _cameras.Hangs = true;
        _ui.Type('\r', '\r');

        var named = Start(new ReadyRecorder()).DecideAsync(Lesson(new CameraChoice("OBSBOT")), StartOrigin.Menu);
        await WaitAsync(() => _time.UntilNextTimer == CameraListing.Limit);
        var moved = await _time.AdvanceUntilAsync(() => named.IsCompleted, TimeSpan.FromSeconds(1));
        var unnamed = Start(new ReadyRecorder()).DecideAsync(Lesson(new CameraChoice(null)), StartOrigin.Menu);
        await WaitAsync(() => _time.UntilNextTimer == CameraListing.Limit);
        await _time.AdvanceUntilAsync(() => unnamed.IsCompleted, TimeSpan.FromSeconds(1));

        Assert.That(moved, Is.GreaterThanOrEqualTo(CameraListing.Limit).And.LessThanOrEqualTo(CameraListing.Limit + TimeSpan.FromSeconds(1)));
        Assert.That(await named, Is.EqualTo(new CameraDecision.Go(Lesson(new CameraChoice("OBSBOT", "720p")))));
        Assert.That(await unnamed, Is.EqualTo(new CameraDecision.Go(Lesson(new CameraChoice(null, "720p")))));
        Assert.That(_ui.Screen, Does.Contain($"Camera \"OBSBOT\" (the cameras could not be listed: Windows did not list the cameras within 5 seconds; 720p): {CameraStart.Prompt}"));
        Assert.That(_ui.Screen, Does.Contain($"Camera (the cameras could not be listed: Windows did not list the cameras within 5 seconds; 720p): {CameraStart.Prompt}"));
        Assert.That(_ui.Screen, Does.Not.Contain("not connected now"));
    }

    /// <summary>
    /// An encoder query Windows does not answer holds the start 5 seconds at most too: the set-up's verdict is then not had,
    /// which a warning says, and the start goes on, since the camera process says what is wrong once it opens the camera.
    /// </summary>
    [Test]
    public async Task An_encoder_query_that_never_answers_holds_the_start_5_seconds_at_most()
    {
        _system.EncoderQueryHangs = true;

        var deciding = Start(new ReadyRecorder()).DecideAsync(Lesson(new CameraChoice("OBSBOT")), StartOrigin.CommandLine);
        await WaitAsync(() => _system.EncoderQueries == 1 && _time.UntilNextTimer == CameraListing.Limit);
        var moved = await _time.AdvanceUntilAsync(() => deciding.IsCompleted, TimeSpan.FromSeconds(1));

        Assert.That(moved, Is.GreaterThanOrEqualTo(CameraListing.Limit).And.LessThanOrEqualTo(CameraListing.Limit + TimeSpan.FromSeconds(1)));
        Assert.That(await deciding, Is.EqualTo(new CameraDecision.Go(Lesson(new CameraChoice("OBSBOT", "720p")))));
        Assert.That(_log.Problems, Is.EqualTo(new[] { CameraStart.SetupNotRead }));
        Assert.That(_ui.Screen, Is.Empty, "the command line is the user's word already");
    }

    /// <summary>A plan without a camera goes on as it is, and nothing is listed or asked.</summary>
    [Test]
    public async Task A_plan_without_a_camera_goes_on_as_it_is()
    {
        var decided = await Start().DecideAsync(Lesson(null), StartOrigin.Menu);

        Assert.That(decided, Is.EqualTo(new CameraDecision.Go(Lesson(null))));
        Assert.That(_cameras.Listings, Is.Zero);
    }

    /// <summary>The recording's factory refuses a plan with a camera in this build, whatever reached it.</summary>
    [Test]
    public void The_recordings_factory_refuses_a_plan_with_a_camera()
    {
        var refused = Assert.Throws<CameraNotBuiltException>(() => new CameraRecorderNotBuilt().Start(new CameraTrackStart(new CameraChoice(null), "folder", "base", TimeSpan.Zero), new RecordingClock(TimeProvider.System)));

        Assert.That(refused!.Message, Is.EqualTo(CameraRecorderNotBuilt.Words));
        Assert.That(new CameraRecorderNotBuilt().NotReady, Is.EqualTo(CameraRecorderNotBuilt.Words));
    }

    /// <summary>Waits, in real time, up to 10 seconds for <paramref name="condition"/>, without moving the manual clock.</summary>
    private static async Task WaitAsync(Func<bool> condition)
    {
        var deadline = Environment.TickCount64 + 10_000;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline) throw new TimeoutException("The expected condition was not met within 10 seconds.");
            await Task.Delay(5);
        }
    }

    /// <summary>A camera recorder that is ready, though nothing here starts it: the window never starts a camera track.</summary>
    private sealed class ReadyRecorder : ICameraRecorder
    {
        /// <inheritdoc />
        public string? NotReady => null;

        /// <inheritdoc />
        public ICameraTrack Start(CameraTrackStart start, RecordingClock clock) => throw new AssertionException("no camera track is started by the window");
    }
}
