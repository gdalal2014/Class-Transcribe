using ClassScribe.Audio;
using ClassScribe.Commands;
using ClassScribe.Desktop;
using ClassScribe.Drive;
using ClassScribe.Recorder;
using ClassScribe.Runs;
using ClassScribe.Settings;
using ClassScribe.Storage;
using ClassScribe.Transcription;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClassScribe.Tests;

/// <summary>
/// Starting a run (sections 1.3, 1.6 and 1.14 of the design): one recording at a time; whether to continue a recent
/// recording, asked in the menu and decided by the flags on the command line; then a separate recorder shown in this
/// window, or the run in this window and why; a recorder that fails to start tried once more; a start refused by a
/// recording of the same name shown instead; and a recorder lost while recording continued by itself, at most twice in 10
/// minutes, the second time in this window. Recorders are played through a pretend process starter, their view through a
/// pretend viewer, and a run in this window through a stand-in for the record command in a scope of its own; the run files
/// and recordings are real files in temporary folders. The clock reads local time in US Eastern time.
/// </summary>
[TestFixture]
public partial class RecordStarterTests
{
    /// <summary>Now: 23:42:15 UTC, 19:42:15 in Eastern time, 42 minutes and 13 seconds after the tests' class started.</summary>
    private static readonly DateTimeOffset Now = TestRuns.Start.AddMinutes(42).AddSeconds(13);

    /// <summary>The run most tests start.</summary>
    private static readonly string RunId = TestRuns.Id(50);

    /// <summary>A transcript saved: how most runs end.</summary>
    private static readonly RunOutcome Transcribed = FakeRunViewer.Transcribed;

    private TestRuntime _runtime = null!;
    private TempFolder _recordings = null!;
    private TempFolder _settingsFolder = null!;
    private TestSettings _settings = null!;
    private TestOptions<RecordingOptions> _options = null!;
    private FakeProcessStarter _starter = null!;
    private PlayedRecorder _recorder = null!;
    private FakeJobInspector _jobs = null!;
    private FakeRunViewer _viewer = null!;
    private FakeConsoleUi _ui = null!;
    private FakeRecordRuns _inWindow = null!;
    private List<FakeDriveAutoUpload> _drives = null!;
    private ServiceProvider _services = null!;
    private RecordingLogger<RecordStarter> _log = null!;
    private RecorderLog _recorderLog = null!;
    private FakeVideoYield _videoYield = null!;

    /// <summary>The camera's start the starter is given; null, as most tests have it, for none.</summary>
    private CameraStart? _camera;

    /// <summary>What decides the run's glossary, the starter is given; null, as most tests have it, for none.</summary>
    private GlossaryChoice? _glossary;

    /// <summary>Makes the folders, the settings, the pretend processes and views, and a run in this window in a scope of its own, with the clock at <see cref="Now"/>.</summary>
    [SetUp]
    public void SetUp()
    {
        _runtime = new TestRuntime();
        _runtime.Time.Advance(Now - TestRuns.Start);
        _recordings = new TempFolder();
        _settingsFolder = new TempFolder();
        _settings = new TestSettings(_settingsFolder.Path, """{ "ElevenLabs": { "BaseUrl": "http://localhost:5001/" } }""");
        _options = TestOptions.Create(new RecordingOptions { OutputFolder = _recordings.Path });
        _starter = new FakeProcessStarter(_runtime.Time);
        _recorder = new PlayedRecorder(_runtime, _starter);
        _jobs = new FakeJobInspector();
        _viewer = new FakeRunViewer();
        _ui = new FakeConsoleUi();
        _inWindow = new FakeRecordRuns();
        _drives = [];
        _services = new ServiceCollection()
            .AddScoped<IRecordRun>(provider => new FakeRecordRun(_inWindow, provider))
            .AddScoped<IDriveAutoUpload>(_ => NewDrive())
            .BuildServiceProvider(validateScopes: true);
        _log = new RecordingLogger<RecordStarter>();
        _recorderLog = new RecorderLog(_runtime.Time);
        _videoYield = new FakeVideoYield();
        _camera = null;
        _glossary = null;
    }

    /// <summary>Deletes the temporary folders.</summary>
    [TearDown]
    public void TearDown()
    {
        _recorderLog.Dispose();
        _services.Dispose();
        _settingsFolder.Dispose();
        _recordings.Dispose();
        _runtime.Dispose();
    }

    /// <summary>The starter under test, with ClassScribe's own folder in the temporary folder, or <paramref name="folder"/>, and <see cref="_camera"/>.</summary>
    private RecordStarter Starter(RuntimeFolder? folder = null)
    {
        var clock = ZonedClock.Eastern(_runtime.Time);
        var launcher = new RecorderLauncher(_starter, _jobs, folder ?? _runtime.Folder, _runtime.Journal, _recorderLog, _options, clock, new RecordingLogger<RecorderLauncher>());
        var question = new ContinueQuestion(new ClassFolders(_options, clock, new RecordingLogger<ClassFolders>()), new RecordingFolder(), _runtime.Journal, _ui, _options, clock,
            new RecordingLogger<ContinueQuestion>());
        return new RecordStarter(_runtime.Journal, launcher, _viewer, question, _settings.Store, new RecordingFolder(), Printed.Commands, _services.GetRequiredService<IServiceScopeFactory>(), _ui, clock, _log,
            _videoYield, _camera, _glossary);
    }

    /// <summary>This build's camera start over the fakes: this laptop's cameras, set up as the camera needs, and a camera's recording not built yet.</summary>
    private CameraStart NotBuiltCamera() =>
        new(new FakeCameraProvider(), new Camera.CameraRecorderNotBuilt(), new Camera.CameraSetupFacts(new FakeCameraSystem(), new FakeCameraUsageRecord(), new FakeRegistryValues()),
            new SourceChoice(_options), _ui, NullLogger<CameraStart>.Instance);

    /// <summary>
    /// The camera's line comes before the continue question (amendment 17): from the menu, with a recent recording of the
    /// same name, this build's refusal of the camera is said first, and Enter there takes the sound only; then C, N or Esc is
    /// asked, and N starts the sound alone as a new recording.
    /// </summary>
    [Test]
    public async Task In_the_menu_the_cameras_line_comes_before_the_continue_question()
    {
        WriteStopped();
        _camera = NotBuiltCamera();
        _ui.Type('\r', 'n', '\u001b');
        _viewer.Then(Ends(_recorder, Transcribed));

        await RunAsync(Request() with { Source = Request().Source with { Camera = new CameraChoice("OBSBOT") } }, StartOrigin.Menu);

        var cameraLine = _ui.Screen.IndexOf(CameraStart.SoundOnlyOffer, StringComparison.Ordinal);
        var continueQuestion = _ui.Screen.IndexOf("C continues it as its next part", StringComparison.Ordinal);
        Assert.That(cameraLine, Is.GreaterThanOrEqualTo(0), "the camera's line was shown");
        Assert.That(continueQuestion, Is.GreaterThan(cameraLine), "the camera's line comes first");
        var request = _recorder.Runs.Single().Request!;
        Assert.That(request.Source.Camera, Is.Null, "Enter at the camera's line records the sound only");
        Assert.That(request.ContinueBaseName, Is.Null, "N at the continue question starts a new recording");
    }

    /// <summary>
    /// A run with a camera is refused before anything records by a starter without a camera start: it says why, starts no
    /// recorder and records nothing in this window, exit 1 (camera-first step 11: no class starts with a half-built camera).
    /// </summary>
    [Test]
    public async Task A_run_with_a_camera_is_refused_before_anything_records()
    {
        var lesson = Request() with { Source = new SourcePlan(RecordingMode.Microphone, null, new MicrophoneChoice("OBSBOT"), new CameraChoice("OBSBOT")) };

        Assert.That(await RunAsync(lesson), Is.EqualTo(1));

        Assert.That(_ui.Screen, Does.Contain(Camera.CameraRecorderNotBuilt.Words));
        Assert.That(_starter.Recorders, Is.Empty, "no recorder was started");
        Assert.That(_inWindow.Runs, Is.Empty, "nothing recorded in this window");
    }

    /// <summary>
    /// The window decides the run's glossary before the run starts, where the saved classes are, and hands it on in the request
    /// (architecture 8.2, item 7): to a recorder, the saved class the run was started as; to a run in this window, the setting,
    /// for a run of no class; and a request that carries one already, as a lost recorder's continued, keeps it.
    /// </summary>
    [Test]
    public async Task The_window_decides_the_glossary_and_hands_it_on_in_the_request()
    {
        var classes = new SavedClasses(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Classes:Mantiq:Course"] = "Mantiq", ["Classes:Mantiq:App"] = "chrome", ["Classes:Mantiq:KeytermsFile"] = "mantiq.txt"
        }).Build());
        _glossary = TestGlossaries.Choice(TestOptions.Create(new ElevenLabsOptions { KeytermsFile = "setting.txt" }), new SettingsPlaces(_settingsFolder.Path, userFolder: null),
            classes: classes);
        _viewer.Then(Ends(_recorder, Transcribed));
        await RunAsync(Request("Mantiq") with { ClassName = "Mantiq" });

        _options.Set(new RecordingOptions { OutputFolder = _recordings.Path, SeparateProcess = false });
        _inWindow.ThenExit(0).ThenExit(0);
        var handedOn = new GlossaryPick("lost.txt", @"C:\lost.txt", "ElevenLabs:KeytermsFile");
        await RunAsync(Request());
        await RunAsync(Request() with { Glossary = handedOn });

        Assert.That(_recorder.Runs.Single().Request!.Glossary,
            Is.EqualTo(new GlossaryPick("mantiq.txt", Path.Combine(_settingsFolder.Path, "mantiq.txt"), "the saved class \"Mantiq\"", "Mantiq")));
        Assert.That(_inWindow.Requests.Select(request => request.Glossary),
            Is.EqualTo(new[] { new GlossaryPick("setting.txt", Path.Combine(_settingsFolder.Path, "setting.txt"), "ElevenLabs:KeytermsFile"), handedOn }));
    }

    /// <summary>
    /// Step 3 comes before step 4: a run started as one of two classes of a course that name different glossaries takes the
    /// glossary of the class it was started as, not ElevenLabs:KeytermsFile's, which the course alone would give.
    /// </summary>
    [Test]
    public async Task A_run_started_as_one_of_two_classes_of_a_course_takes_that_classs_glossary()
    {
        _glossary = TestGlossaries.Choice(TestOptions.Create(new ElevenLabsOptions { KeytermsFile = "setting.txt" }), new SettingsPlaces(_settingsFolder.Path, userFolder: null),
            classes: TestGlossaries.Classes(new Dictionary<string, string?>
            {
                ["Classes:Mantiq (Chrome):Course"] = "Mantiq", ["Classes:Mantiq (Chrome):App"] = "chrome", ["Classes:Mantiq (Chrome):KeytermsFile"] = "chrome.txt",
                ["Classes:Mantiq (Zoom app):Course"] = "Mantiq", ["Classes:Mantiq (Zoom app):App"] = "zoom", ["Classes:Mantiq (Zoom app):KeytermsFile"] = "zoom.txt"
            }));
        _viewer.Then(Ends(_recorder, Transcribed));

        await RunAsync(Request("Mantiq") with { ClassName = "Mantiq (Zoom app)" });

        Assert.That(_recorder.Runs.Single().Request!.Glossary,
            Is.EqualTo(new GlossaryPick("zoom.txt", Path.Combine(_settingsFolder.Path, "zoom.txt"), "the saved class \"Mantiq (Zoom app)\"", "Mantiq (Zoom app)")));
    }

    /// <summary>A request to record "Kalam" into the recordings folder and transcribe it, with neither --continue nor --new.</summary>
    private RecordRequest Request(string name = "Kalam") =>
        TestRuns.Request(RunId, name) with { OutputRoot = _recordings.Path, Continue = ContinueChoice.Ask };

    /// <summary>Starts <paramref name="request"/> from <paramref name="origin"/> and waits for its exit code, moving the clock while a recorder is awaited.</summary>
    private async Task<int> RunAsync(RecordRequest? request = null, StartOrigin origin = StartOrigin.CommandLine, RuntimeFolder? folder = null)
    {
        var running = Starter(folder).RunAsync(request ?? Request(), origin);
        await _runtime.Time.AdvanceUntilAsync(() => running.IsCompleted, TimeSpan.FromSeconds(1), atMost: TimeSpan.FromMinutes(2));
        return await running;
    }

    /// <summary>The view ends the run shown with <paramref name="outcome"/>, its recorder finishing it as the recorder does, before it sends the end.</summary>
    private static Func<RunRecord, ViewMode, ViewResult> Ends(PlayedRecorder recorder, RunOutcome outcome) => (run, _) =>
    {
        recorder.Finish(run.RunId, outcome);
        return new ViewResult(ViewEnd.RunEnded, outcome.ExitCode, outcome);
    };

    /// <summary>The view loses the run shown: its recorder is killed while it records, after the clock has moved by <paramref name="after"/>.</summary>
    private Func<RunRecord, ViewMode, ViewResult> Loses(TimeSpan after = default) => (run, _) =>
    {
        if (after > TimeSpan.Zero) _runtime.Time.Advance(after);
        _recorder.Lose(run.RunId);
        return new ViewResult(ViewEnd.RecorderLost, null, null);
    };

    /// <summary>A recording of <paramref name="name"/> that started with the tests' class and stopped four minutes ago without a transcript.</summary>
    private RecordingFiles WriteStopped(string name = "Kalam")
    {
        var files = RecordingFiles.ForNew(Path.Combine(_recordings.Path, name), name, TestRuns.Start);
        Directory.CreateDirectory(files.Folder);
        using (var writer = new WavSegmentWriter(files.Segment(1), 10)) writer.Write(new byte[2400 * 10 * 2]);
        File.SetLastWriteTimeUtc(files.Segment(1), Now.AddMinutes(-4).UtcDateTime);
        return files;
    }

    /// <summary>A run recording <paramref name="name"/> now, as process 9000, started with the tests' class: in a separate recorder, or in another window.</summary>
    private RunRecord RecordingNow(string name, RunHome home = RunHome.Separate)
    {
        var record = TestRuns.Record(TestRuns.Id(2), RunState.Recording, processId: 9000, name: name) with { Home = home, Pipe = home == RunHome.Separate ? $"ClassScribe-{TestRuns.Id(2)}" : null };
        _runtime.Journal.Write(_runtime.Journal.RunFile(record.RunId), record);
        _runtime.Processes.Start(record.ProcessId, record.ProcessStartedUtc);
        return record;
    }

    /// <summary>
    /// Row 1: with Recording:SeparateProcess false, the run records in this window, through the record command in a scope
    /// of its own, after saying so; no recorder is started.
    /// </summary>
    [Test]
    public async Task Row_1_without_a_separate_process_the_run_records_in_this_window()
    {
        _options.Set(new RecordingOptions { OutputFolder = _recordings.Path, SeparateProcess = false });
        _inWindow.ThenExit(0);

        var exitCode = await RunAsync();

        Assert.That(exitCode, Is.Zero);
        Assert.That(_inWindow.Requests, Is.EqualTo(new[] { Request() with { Continue = ContinueChoice.New } }), "decided: a new recording");
        Assert.That(_inWindow.Runs.Single().Scope, Is.Not.SameAs(_services), "a scope of its own");
        Assert.That(_starter.Launches, Is.Empty);
        Assert.That(_ui.Screen, Does.Contain(RecorderLauncher.SeparateProcessOff));
    }

    /// <summary>Google Drive's part in a scope, noted in <see cref="_drives"/>, one per scope.</summary>
    private FakeDriveAutoUpload NewDrive()
    {
        var drive = new FakeDriveAutoUpload();
        lock (_drives) _drives.Add(drive);
        return drive;
    }

    /// <summary>
    /// A run in this window is followed by the upload to Google Drive, in the run's own scope, once it has ended and shown
    /// its end; the exit code is the run's.
    /// </summary>
    [Test]
    public async Task A_run_in_this_window_is_followed_by_the_upload_to_Google_Drive()
    {
        _options.Set(new RecordingOptions { OutputFolder = _recordings.Path, SeparateProcess = false });
        _inWindow.ThenExit(0);

        Assert.That(await RunAsync(), Is.Zero);

        Assert.That(_drives.Single().Calls, Is.EqualTo(new[] { "Ended Transcribed", "AfterRun" }), "the run's own part, told how it ended, then the upload");
    }

    /// <summary>A run in this window whose transcript failed is followed by the upload all the same; the failure goes on to the caller.</summary>
    [Test]
    public void A_run_in_this_window_that_failed_is_followed_by_the_upload_and_its_failure()
    {
        _options.Set(new RecordingOptions { OutputFolder = _recordings.Path, SeparateProcess = false });
        _inWindow.ThenFail(new InvalidOperationException("The transcript failed."));

        Assert.That(async () => await RunAsync(), Throws.InvalidOperationException.With.Message.EqualTo("The transcript failed."));

        Assert.That(_drives.Single().Calls, Is.EqualTo(new[] { "Ended TranscriptFailed", "AfterRun" }));
    }

    /// <summary>A run in a recorder is uploaded by the recorder itself, so the window that shows it uploads nothing.</summary>
    [Test]
    public async Task A_run_in_a_recorder_is_not_uploaded_by_the_window()
    {
        _viewer.Then(Ends(_recorder, Transcribed));

        Assert.That(await RunAsync(), Is.Zero);

        Assert.That(_drives, Is.Empty);
    }

    /// <summary>A run in this window shows its own outcome, so its run file is marked seen once it is over, and the next start does not report it.</summary>
    [Test]
    public async Task A_run_in_this_window_is_not_reported_again()
    {
        _options.Set(new RecordingOptions { OutputFolder = _recordings.Path, SeparateProcess = false });
        var record = TestRuns.Record(RunId, RunState.Finished, processId: 7000) with { Home = RunHome.InWindow, Pipe = null, Outcome = Transcribed };
        _runtime.Journal.Write(_runtime.Journal.RunFile(RunId), record);

        await RunAsync();

        Assert.That(_runtime.Journal.Find(RunId)!.Record.Seen, Is.True);
    }

    /// <summary>
    /// Row 2: under dotnet run's job the run records in this window, saying why, and nothing ever says that closing the
    /// window does not stop it.
    /// </summary>
    [Test]
    public async Task Row_2_under_dotnet_runs_job_the_run_records_in_this_window()
    {
        _jobs.Facts = FakeJobInspector.DotnetRun;

        await RunAsync();

        Assert.That(_inWindow.Requests, Has.Count.EqualTo(1));
        Assert.That(_starter.Launches, Is.Empty);
        Assert.That(_ui.Screen, Does.Contain(RecorderLauncher.EndsWithItsStarter));
        Assert.That(_ui.Screen, Does.Not.Contain("does not stop it"));
    }

    /// <summary>Row 3: when ClassScribe's own folder cannot be written, the run records in this window, saying why and what is missing.</summary>
    [Test]
    public async Task Row_3_an_unwritable_runtime_folder_leaves_the_run_in_this_window()
    {
        var blocker = _runtime.Temp.File("not-a-folder");
        File.WriteAllText(blocker, "a file where ClassScribe's folder should be");

        await RunAsync(folder: new RuntimeFolder(Path.Combine(blocker, "ClassScribe")));

        Assert.That(_inWindow.Requests, Has.Count.EqualTo(1));
        Assert.That(_starter.Launches, Is.Empty);
        Assert.That(_ui.Screen, Does.Contain(RecorderLauncher.WithoutBookkeeping));
    }

    /// <summary>
    /// Row 4: a recorder that ends before it answers is started once more; when that one fails too, the run records in
    /// this window, with the reason and the advice about Bitdefender, under a run id of its own.
    /// </summary>
    [Test]
    public async Task Row_4_a_recorder_that_fails_twice_leaves_the_run_in_this_window()
    {
        _recorder.EndsBeforeAnswering(1).EndsBeforeAnswering(1);
        _inWindow.ThenExit(0);

        var exitCode = await RunAsync();

        Assert.That(exitCode, Is.Zero);
        Assert.That(_starter.Recorders, Has.Count.EqualTo(2));
        Assert.That(_starter.Recorders[0].RunId, Is.EqualTo(RunId));
        var inWindow = _inWindow.Requests.Single();
        Assert.That(inWindow with { RunId = RunId }, Is.EqualTo(Request() with { Continue = ContinueChoice.New }));
        Assert.That(inWindow.RunId, Is.Not.EqualTo(_starter.Recorders[0].RunId).And.Not.EqualTo(_starter.Recorders[1].RunId));
        Assert.That(_ui.Screen, Does.Contain("The recorder ended before it answered, with exit code 1."));
        Assert.That(_ui.Screen, Does.Contain("Starting the recorder once more."));
        Assert.That(_ui.Screen, Does.Contain("Recording in this window instead."));
        Assert.That(_ui.Screen, Does.Contain("If Bitdefender showed a notice, add the exception described in the README."));
    }

    /// <summary>A recorder that fails once and starts the second time records in the background, as if nothing had happened.</summary>
    [Test]
    public async Task A_recorder_that_fails_once_is_started_again()
    {
        _recorder.EndsBeforeAnswering(1).Records();
        _viewer.Then(Ends(_recorder, Transcribed));

        var exitCode = await RunAsync();

        Assert.That(exitCode, Is.Zero);
        Assert.That(_starter.Recorders, Has.Count.EqualTo(2));
        Assert.That(_viewer.Shown.Single().Run.RunId, Is.EqualTo(_starter.Recorders[1].RunId));
        Assert.That(_inWindow.Requests, Is.Empty);
    }

    /// <summary>
    /// Row 5: otherwise the run records in a separate recorder, given the checked settings in its request, and this window
    /// says that closing it does not stop the recording, then shows the run as its owner; the run's end gives the exit
    /// code, and the run is not reported again.
    /// </summary>
    [TestCase(OutcomeKind.Transcribed, 0)]
    [TestCase(OutcomeKind.TranscriptFailed, 1)]
    [TestCase(OutcomeKind.Cancelled, 130)]
    public async Task Row_5_otherwise_the_run_records_in_the_background_and_this_window_shows_it(OutcomeKind kind, int expected)
    {
        _viewer.Then(Ends(_recorder, new RunOutcome(kind, expected, "Over.", null, null, null)));

        var exitCode = await RunAsync();

        Assert.That(exitCode, Is.EqualTo(expected));
        Assert.That(_ui.Screen, Does.Contain("Recording in the background: closing this window does not stop it. Esc leaves this view; start ClassScribe again to see it."));
        Assert.That(_viewer.Shown.Single().Run.RunId, Is.EqualTo(RunId));
        Assert.That(_viewer.Shown.Single().Mode, Is.EqualTo(ViewMode.Owner));
        Assert.That(_recorder.Runs.Single().Request, Is.EqualTo(Request() with { Continue = ContinueChoice.New }), "decided: a new recording");
        Assert.That(_recorder.Runs.Single().Settings, Is.EquivalentTo(_settings.Reloader.Snapshot()));
        Assert.That(_runtime.Journal.Find(RunId)!.Record.Seen, Is.True);
        Assert.That(_inWindow.Requests, Is.Empty);
    }

    /// <summary>Esc leaves the view: on the command line that ends with exit code 0 and says the recording carries on; from the menu, the menu comes back without a word.</summary>
    [TestCase(StartOrigin.CommandLine, true)]
    [TestCase(StartOrigin.Menu, false)]
    public async Task Esc_leaves_the_view_and_the_recording_carries_on(StartOrigin origin, bool said)
    {
        _viewer.Then((_, _) => new ViewResult(ViewEnd.Left, null, null));

        var exitCode = await RunAsync(origin: origin);

        Assert.That(exitCode, Is.Zero);
        Assert.That(_ui.Screen.Contains("The recording carries on in the background. Start ClassScribe to see it."), Is.EqualTo(said));
        Assert.That(_starter.Recorders.Single().ExitCode, Is.Null, "nothing stopped the recorder");
    }

    /// <summary>On the command line, a recording of the same name running already is shown, in any letter case; nothing new starts.</summary>
    [Test]
    public async Task On_the_command_line_a_recording_of_the_same_name_is_shown()
    {
        var live = RecordingNow("kalam");
        _viewer.Then((_, _) => new ViewResult(ViewEnd.RunEnded, 0, Transcribed));

        var exitCode = await RunAsync();

        Assert.That(exitCode, Is.Zero);
        Assert.That(_viewer.Shown.Single(), Is.EqualTo((live, ViewMode.Viewer)));
        Assert.That(_starter.Launches, Is.Empty);
        Assert.That(_ui.Screen, Does.Contain("\"kalam\" is being recorded already, since 19:00 (00:42:13); showing it."));
    }

    /// <summary>
    /// One recording at a time: another recording running already is explained, with exit code 1 and nothing started. On
    /// the command line it says how to see it; in the menu, and from the report at the start, which leads to the menu, that
    /// A shows it.
    /// </summary>
    [TestCase(StartOrigin.CommandLine, "Only one recording at a time: \"Fiqh\" has been recording since 19:00 (00:42:13). Start ClassScribe to see it, or stop it first.")]
    [TestCase(StartOrigin.Menu, "Only one recording at a time: \"Fiqh\" has been recording since 19:00 (00:42:13). A shows it.")]
    [TestCase(StartOrigin.StartupReport, "Only one recording at a time: \"Fiqh\" has been recording since 19:00 (00:42:13). A shows it.")]
    public async Task One_recording_at_a_time_another_one_running_is_explained(StartOrigin origin, string explained)
    {
        RecordingNow("Fiqh");

        var exitCode = await RunAsync(origin: origin);

        Assert.That(exitCode, Is.EqualTo(1));
        Assert.That(_ui.Screen, Is.EqualTo(explained));
        Assert.That(_starter.Launches, Is.Empty);
        Assert.That(_viewer.Shown, Is.Empty);
        Assert.That(_inWindow.Requests, Is.Empty);
    }

    /// <summary>A recording in another window cannot be shown here, even of the same name: it is explained, with what closing that window does.</summary>
    [Test]
    public async Task A_recording_in_another_window_is_explained()
    {
        RecordingNow("Kalam", RunHome.InWindow);

        var exitCode = await RunAsync();

        Assert.That(exitCode, Is.EqualTo(1));
        Assert.That(_ui.Screen, Is.EqualTo(
            "Only one recording at a time: \"Kalam\" is being recorded in another ClassScribe window (process 9000), which shows it; closing that window ends the recording."));
        Assert.That(_starter.Launches, Is.Empty);
    }

    /// <summary>A start refused because a recording of the same name took the lock at the same moment shows that recording instead.</summary>
    [Test]
    public async Task A_start_refused_by_a_recording_of_the_same_name_shows_it()
    {
        var winner = TestRuns.Record(TestRuns.Id(2), RunState.Recording, processId: 9000);
        _recorder.IsRefused(winner);
        _viewer.Then((_, _) => new ViewResult(ViewEnd.RunEnded, 0, Transcribed));

        var exitCode = await RunAsync();

        Assert.That(exitCode, Is.Zero);
        Assert.That(_viewer.Shown.Single().Run.RunId, Is.EqualTo(winner.RunId));
        Assert.That(_viewer.Shown.Single().Mode, Is.EqualTo(ViewMode.Viewer));
    }

    /// <summary>A start refused because a recording of another name took the lock is explained, with exit code 1.</summary>
    [Test]
    public async Task A_start_refused_by_a_recording_of_another_name_is_explained()
    {
        _recorder.IsRefused(TestRuns.Record(TestRuns.Id(2), RunState.Recording, processId: 9000, name: "Fiqh"));

        var exitCode = await RunAsync();

        Assert.That(exitCode, Is.EqualTo(1));
        Assert.That(_ui.Screen, Does.Contain("Only one recording at a time: \"Fiqh\" has been recording since 19:00 (00:42:13). Start ClassScribe to see it, or stop it first."));
        Assert.That(_viewer.Shown, Is.Empty);
    }

    /// <summary>In the menu, C continues the recording that stopped recently: the recorder is asked to continue it as its next part, and says so.</summary>
    [Test]
    public async Task In_the_menu_C_continues_the_recent_recording()
    {
        var files = WriteStopped();
        _ui.Type('c');
        _viewer.Then(Ends(_recorder, Transcribed));

        await RunAsync(origin: StartOrigin.Menu);

        var request = _recorder.Runs.Single().Request!;
        Assert.That(request.ContinueBaseName, Is.EqualTo(files.BaseName));
        Assert.That(request.Continue, Is.EqualTo(ContinueChoice.Continue));
        Assert.That(_ui.Screen, Does.Contain($"Continuing {files.BaseName} as its next part (00:40:00 recorded so far)."));
    }

    /// <summary>In the menu, N starts a new recording.</summary>
    [Test]
    public async Task In_the_menu_N_starts_a_new_recording()
    {
        WriteStopped();
        _ui.Type('n');
        _viewer.Then(Ends(_recorder, Transcribed));

        await RunAsync(origin: StartOrigin.Menu);

        Assert.That(_recorder.Runs.Single().Request!.ContinueBaseName, Is.Null);
    }

    /// <summary>In the menu, Esc at the question goes back: nothing starts.</summary>
    [Test]
    public async Task In_the_menu_Esc_at_the_question_goes_back()
    {
        WriteStopped();
        _ui.Type('\u001b');

        var exitCode = await RunAsync(origin: StartOrigin.Menu);

        Assert.That(exitCode, Is.Zero);
        Assert.That(_starter.Launches, Is.Empty);
        Assert.That(_inWindow.Requests, Is.Empty);
    }

    /// <summary>
    /// On the command line, neither --continue nor --new starts a new recording at once, never asking or waiting, and says
    /// how to continue the recent one instead.
    /// </summary>
    [Test]
    public async Task On_the_command_line_neither_flag_starts_a_new_recording_with_the_hint()
    {
        var files = WriteStopped();
        _viewer.Then(Ends(_recorder, Transcribed));

        await RunAsync();

        Assert.That(_recorder.Runs.Single().Request!.ContinueBaseName, Is.Null);
        Assert.That(_ui.KeyReads, Is.Zero);
        Assert.That(_ui.Screen, Does.StartWith($"{files.BaseName} stopped 4 minutes ago without a transcript; to add to it instead, run the same command with --continue."));
    }

    /// <summary>On the command line, --continue continues the recent recording.</summary>
    [Test]
    public async Task On_the_command_line_continue_continues_the_recent_recording()
    {
        var files = WriteStopped();
        _viewer.Then(Ends(_recorder, Transcribed));

        await RunAsync(Request() with { Continue = ContinueChoice.Continue });

        Assert.That(_recorder.Runs.Single().Request!.ContinueBaseName, Is.EqualTo(files.BaseName));
    }

    /// <summary>On the command line, --continue with nothing to continue says so and starts a new recording.</summary>
    [Test]
    public async Task On_the_command_line_continue_without_a_recording_starts_a_new_one()
    {
        _viewer.Then(Ends(_recorder, Transcribed));

        var exitCode = await RunAsync(Request() with { Continue = ContinueChoice.Continue });

        Assert.That(exitCode, Is.Zero);
        Assert.That(_recorder.Runs.Single().Request!.ContinueBaseName, Is.Null);
        Assert.That(_ui.Screen, Does.StartWith("There is no recording of \"Kalam\" to continue"));
    }

    /// <summary>A request that names the recording to continue, as from the report at the start, continues it without a question.</summary>
    [Test]
    public async Task A_request_that_names_its_recording_continues_it_without_a_question()
    {
        var files = WriteStopped();
        _viewer.Then(Ends(_recorder, Transcribed));

        await RunAsync(Request() with { ContinueBaseName = files.BaseName, Continue = ContinueChoice.Continue }, StartOrigin.StartupReport);

        Assert.That(_recorder.Runs.Single().Request!.ContinueBaseName, Is.EqualTo(files.BaseName));
        Assert.That(_ui.KeyReads, Is.Zero);
    }

    /// <summary>
    /// A recorder lost while it records is continued by itself: a new recorder continues the same recording as its next
    /// part, with the rest of --minutes, and this window says so and shows it; the lost run is not reported again.
    /// </summary>
    [Test]
    public async Task A_recorder_lost_while_recording_is_continued_in_a_new_recorder()
    {
        _viewer.Then(Loses(after: TimeSpan.FromMinutes(30))).Then(Ends(_recorder, Transcribed));

        var exitCode = await RunAsync(Request() with { Minutes = 90 });

        Assert.That(exitCode, Is.Zero);
        Assert.That(_starter.Recorders, Has.Count.EqualTo(2));
        var lost = _recorder.Runs[0];
        var continued = _recorder.Runs[1].Request!;
        Assert.That(continued.ContinueBaseName, Is.EqualTo(lost.Files!.BaseName));
        Assert.That(continued.Continue, Is.EqualTo(ContinueChoice.Continue));
        Assert.That(continued.Minutes, Is.EqualTo(60).Within(0.001), "the rest of the 90 minutes, 30 of which have passed");
        Assert.That(continued with { RunId = RunId, Continue = ContinueChoice.Ask, ContinueBaseName = null, Minutes = 90 }, Is.EqualTo(Request() with { Minutes = 90 }));
        Assert.That(_viewer.Shown.Select(shown => shown.Mode), Is.EqualTo(new[] { ViewMode.Owner, ViewMode.Owner }));
        Assert.That(_runtime.Journal.Find(RunId)!.Record.Seen, Is.True);
        Assert.That(_ui.Screen, Does.Contain("The recorder ended unexpectedly at 20:12:15; continuing the same recording."));
        Assert.That(_inWindow.Requests, Is.Empty);
    }

    /// <summary>
    /// A second loss within 10 minutes of the first continues the recording in this window instead, since a repeated kill is
    /// most likely Bitdefender, and says so.
    /// </summary>
    [Test]
    public async Task A_second_loss_within_10_minutes_continues_in_this_window()
    {
        _viewer.Then(Loses()).Then(Loses(after: TimeSpan.FromMinutes(3)));
        _inWindow.ThenExit(0);

        var exitCode = await RunAsync();

        Assert.That(exitCode, Is.Zero);
        Assert.That(_starter.Recorders, Has.Count.EqualTo(2));
        Assert.That(_inWindow.Requests.Single().ContinueBaseName, Is.EqualTo(_recorder.Runs[0].Files!.BaseName));
        Assert.That(_ui.Screen, Does.Contain("The recorder ended unexpectedly again at 19:45:15, the second time within 10 minutes"));
        Assert.That(_ui.Screen, Does.Contain("Bitdefender"));
        Assert.That(_ui.Screen, Does.Contain("Continuing the same recording in this window instead: closing it ends the recording, and the audio so far is kept."));
    }

    /// <summary>A second loss more than 10 minutes after the first is continued in a new recorder again.</summary>
    [Test]
    public async Task A_second_loss_after_10_minutes_is_continued_in_a_new_recorder_again()
    {
        _viewer.Then(Loses()).Then(Loses(after: TimeSpan.FromMinutes(11))).Then(Ends(_recorder, Transcribed));

        var exitCode = await RunAsync();

        Assert.That(exitCode, Is.Zero);
        Assert.That(_starter.Recorders, Has.Count.EqualTo(3));
        Assert.That(_inWindow.Requests, Is.Empty);
    }

    /// <summary>
    /// Several windows that showed a recorder now lost may all try to continue its recording; the recorder that gets through
    /// records it, and the other windows attach to it as viewers and start nothing. Here another window got through first.
    /// </summary>
    [Test]
    public async Task A_window_continuing_after_a_loss_shows_the_recording_another_window_continued_first()
    {
        var live = RecordingNow("Kalam");
        _viewer.Then((_, _) => new ViewResult(ViewEnd.RunEnded, 0, Transcribed));

        var exitCode = await RunAsync(Request() with { ContinueBaseName = "Kalam_2026-10-15_23-00-02Z", Continue = ContinueChoice.Continue }, StartOrigin.AutoContinue);

        Assert.That(exitCode, Is.Zero);
        Assert.That(_viewer.Shown.Single(), Is.EqualTo((live, ViewMode.Viewer)));
        Assert.That(_starter.Launches, Is.Empty);
        Assert.That(_inWindow.Requests, Is.Empty);
        Assert.That(_ui.Screen, Is.EqualTo("\"Kalam\" is being recorded already, since 19:00 (00:42:13); showing it."));
    }

    /// <summary>A start from a window whose recorder was lost already counts that loss: the next loss within 10 minutes continues in this window.</summary>
    [Test]
    public async Task A_start_that_continues_after_a_loss_counts_it()
    {
        var files = WriteStopped();
        _viewer.Then(Loses(after: TimeSpan.FromMinutes(2)));

        await RunAsync(Request() with { ContinueBaseName = files.BaseName, Continue = ContinueChoice.Continue }, StartOrigin.AutoContinue);

        Assert.That(_starter.Recorders, Has.Count.EqualTo(1));
        Assert.That(_inWindow.Requests.Single().ContinueBaseName, Is.EqualTo(files.BaseName));
    }

    /// <summary>
    /// A recorder lost while it transcribed is not continued: its audio is complete, and the command to transcribe it ends
    /// the line, after the word about billing, so that it can be copied as it is; exit code 1.
    /// </summary>
    [Test]
    public async Task A_recorder_lost_while_transcribing_gives_the_command_to_transcribe()
    {
        _viewer.Then((run, _) =>
        {
            _recorder.Transcribing(run.RunId);
            _recorder.Lose(run.RunId);
            return new ViewResult(ViewEnd.RecorderLost, null, null);
        });

        var exitCode = await RunAsync();

        var files = _recorder.Runs.Single().Files!;
        Assert.That(exitCode, Is.EqualTo(1));
        Assert.That(_starter.Recorders, Has.Count.EqualTo(1));
        Assert.That(_ui.Screen, Does.EndWith(
            $"{Environment.NewLine}The transcript of \"Kalam\" was not finished; its audio is complete. If the upload had finished, ElevenLabs may bill it again. To transcribe it: {Printed.Transcribe(files.Segment(1))}"));
        Assert.That(_runtime.Journal.Find(RunId)!.Record.Seen, Is.True);
    }

    /// <summary>This build's camera start over the fakes, ready: this laptop's cameras, set up as the camera needs, and a camera recorder that records.</summary>
    private CameraStart ReadyCamera() =>
        new(new FakeCameraProvider(), new FakeCameraRecorder(), new Camera.CameraSetupFacts(new FakeCameraSystem(), new FakeCameraUsageRecord(), new FakeRegistryValues()),
            new SourceChoice(_options), _ui, NullLogger<CameraStart>.Instance);

    /// <summary>
    /// A lesson's recorder lost while it made the lesson's video, record-only or with its transcript saved already, is said to
    /// have had its video cut off, with the command that makes it, free, never its transcript; one lost before its transcript
    /// was saved is said as a transcript cut off. Neither is continued; exit code 1.
    /// </summary>
    [TestCase(false, false, true)]
    [TestCase(true, true, true)]
    [TestCase(true, false, false)]
    public async Task A_lesson_s_recorder_lost_while_its_video_was_made_says_so(bool transcribe, bool transcriptSaved, bool video)
    {
        _camera = ReadyCamera();
        _viewer.Then((run, _) =>
        {
            _recorder.Transcribing(run.RunId);
            if (transcriptSaved)
            {
                var saved = _recorder.Runs.Single().Files!;
                Directory.CreateDirectory(saved.Folder);
                File.WriteAllText(saved.Transcript(), "the transcript");
            }

            _recorder.Lose(run.RunId);
            return new ViewResult(ViewEnd.RecorderLost, null, null);
        });
        var lesson = Request() with { Transcribe = transcribe, Source = new SourcePlan(RecordingMode.Microphone, null, new MicrophoneChoice(null), new CameraChoice("OBSBOT")) };

        var exitCode = await RunAsync(lesson);

        Assert.That(exitCode, Is.EqualTo(1));
        Assert.That(_starter.Recorders, Has.Count.EqualTo(1));
        Assert.That(_ui.Screen.Contains("The lesson video of \"Kalam\" was not finished; its sound and its camera pieces are kept. To make it, free, run: ", StringComparison.Ordinal),
            Is.EqualTo(video));
        Assert.That(_ui.Screen.Contains("The transcript of \"Kalam\" was not finished", StringComparison.Ordinal), Is.EqualTo(!video));
    }

    /// <summary>
    /// A recorder lost while it stopped the recording is not continued, since the stop was asked for: its audio is kept, the
    /// command to transcribe it ends the line, the run is not reported again, and the exit code is 1.
    /// </summary>
    [Test]
    public async Task A_recorder_lost_while_stopping_gives_the_command_to_transcribe()
    {
        _viewer.Then((run, _) =>
        {
            _recorder.Stopping(run.RunId);
            _recorder.Lose(run.RunId);
            return new ViewResult(ViewEnd.RecorderLost, null, null);
        });

        var exitCode = await RunAsync();

        var files = _recorder.Runs.Single().Files!;
        Assert.That(exitCode, Is.EqualTo(1));
        Assert.That(_starter.Recorders, Has.Count.EqualTo(1));
        Assert.That(_inWindow.Requests, Is.Empty);
        Assert.That(_ui.Screen, Does.EndWith(
            $"{Environment.NewLine}The recorder ended unexpectedly while it stopped the recording \"Kalam\"; its audio is kept. To transcribe it: {Printed.Transcribe(files.Segment(1))}"));
        Assert.That(_runtime.Journal.Find(RunId)!.Record.Seen, Is.True);
    }

    /// <summary>
    /// A recorder lost after its run ended, as when the link broke before the end reached this window, leaves the outcome in
    /// its run file: that is shown, with its exit code, and the run is not reported again. A message that ends with a question
    /// keeps its question mark, with no full stop after it.
    /// </summary>
    [Test]
    public async Task A_recorder_lost_after_its_run_ended_gives_the_outcome_in_its_run_file()
    {
        _viewer.Then((run, _) =>
        {
            _recorder.Finish(run.RunId, new RunOutcome(OutcomeKind.NothingRecorded, 1, "No audio was captured. Was anything playing?", null, null, null));
            return new ViewResult(ViewEnd.RecorderLost, null, null);
        });

        var exitCode = await RunAsync();

        Assert.That(exitCode, Is.EqualTo(1));
        Assert.That(_starter.Recorders, Has.Count.EqualTo(1));
        Assert.That(_ui.Screen, Does.EndWith($"{Environment.NewLine}\"Kalam\" ended: No audio was captured. Was anything playing?"));
        Assert.That(_runtime.Journal.Find(RunId)!.Record.Seen, Is.True);
    }

    /// <summary>
    /// A recorder that Windows ended sends the window its end, a failure, and leaves its run file recording, with Windows as
    /// the cause, so that the next start reports the recording and offers C. The window that was shown that end leaves the
    /// run unseen, or that report would be lost.
    /// </summary>
    [Test]
    public async Task A_run_whose_file_still_says_recording_after_its_end_is_left_for_the_next_start()
    {
        _viewer.Then((run, _) =>
        {
            _recorder.Of(run.RunId).Run!.SetEndedBy(RunEndCause.Windows);
            _recorder.Lose(run.RunId);
            return new ViewResult(ViewEnd.RunEnded, 1, new RunOutcome(OutcomeKind.RecorderFailed, 1, "Windows ended the session, so the recording stopped.", null, null, null));
        });

        var exitCode = await RunAsync();

        Assert.That(exitCode, Is.EqualTo(1));
        var record = _runtime.Journal.Find(RunId)!.Record;
        Assert.That((record.State, record.EndedBy, record.Seen), Is.EqualTo((RunState.Recording, RunEndCause.Windows, false)));
    }

    /// <summary>
    /// A start from a window the recorder opened by itself, continuing the recording its lost recorder made, shows the
    /// continued run with its keys waiting for Enter, as that window showed the lost one, and leaves the run unseen at its
    /// end, since nobody may have looked at that window: the next start reports the outcome, with O to open the transcript.
    /// </summary>
    [Test]
    public async Task From_a_window_opened_by_itself_the_continued_run_is_shown_with_its_keys_waiting_for_Enter()
    {
        var files = WriteStopped();
        _viewer.Then(Ends(_recorder, Transcribed));

        var exitCode = await RunAsync(Request() with { ContinueBaseName = files.BaseName, Continue = ContinueChoice.Continue }, StartOrigin.AutoContinueFromAlert);

        Assert.That(exitCode, Is.Zero);
        Assert.That(_recorder.Runs.Single().Request!.ContinueBaseName, Is.EqualTo(files.BaseName));
        Assert.That(_viewer.Shown.Single().Mode, Is.EqualTo(ViewMode.Alert));
        Assert.That(_runtime.Journal.Find(RunId)!.Record.Seen, Is.False, "left for the next start to report");
    }

    /// <summary>
    /// A second loss within 10 minutes from a window the recorder opened by itself is not continued in that window, whose
    /// keys are read with no wait for Enter: it is declined, with the advice about Bitdefender, and the lost run is left for
    /// the next start to report, with C.
    /// </summary>
    [Test]
    public async Task From_a_window_opened_by_itself_a_second_loss_is_declined_rather_than_recorded_in_it()
    {
        var files = WriteStopped();
        _viewer.Then(Loses(after: TimeSpan.FromMinutes(2)));

        var exitCode = await RunAsync(Request() with { ContinueBaseName = files.BaseName, Continue = ContinueChoice.Continue }, StartOrigin.AutoContinueFromAlert);

        Assert.That(exitCode, Is.EqualTo(1));
        Assert.That(_starter.Recorders, Has.Count.EqualTo(1));
        Assert.That(_inWindow.Requests, Is.Empty, "never in a window opened by itself");
        Assert.That(_ui.Screen, Does.Contain("Bitdefender"));
        Assert.That(_ui.Screen, Does.EndWith(RecordStarter.NotInThisWindow));
        Assert.That(_runtime.Journal.Find(RunId)!.Record.Seen, Is.False, "left for the next start to report");
    }

    /// <summary>
    /// From a window the recorder opened by itself, a recorder that fails to start twice is declined too, with the reason
    /// and the advice about Bitdefender, rather than recorded in that window.
    /// </summary>
    [Test]
    public async Task From_a_window_opened_by_itself_a_recorder_that_fails_twice_is_declined()
    {
        _recorder.EndsBeforeAnswering(1).EndsBeforeAnswering(1);

        var exitCode = await RunAsync(Request() with { Continue = ContinueChoice.New }, StartOrigin.AutoContinueFromAlert);

        Assert.That(exitCode, Is.EqualTo(1));
        Assert.That(_starter.Recorders, Has.Count.EqualTo(2));
        Assert.That(_inWindow.Requests, Is.Empty);
        Assert.That(_ui.Screen, Does.Contain("The recorder ended before it answered, with exit code 1."));
        Assert.That(_ui.Screen, Does.Contain("If Bitdefender showed a notice, add the exception described in the README."));
        Assert.That(_ui.Screen, Does.EndWith(RecordStarter.NotInThisWindow));
        Assert.That(_ui.Screen, Does.Not.Contain("Recording in this window instead."));
    }

    /// <summary>From a window the recorder opened by itself, a recording of the same name that another window continued first is shown with the keys waiting for Enter too.</summary>
    [Test]
    public async Task From_a_window_opened_by_itself_a_recording_another_window_continued_is_shown_with_its_keys_waiting_for_Enter()
    {
        var live = RecordingNow("Kalam");
        _viewer.Then((_, _) => new ViewResult(ViewEnd.Left, null, null));

        var exitCode = await RunAsync(Request() with { ContinueBaseName = "Kalam_2026-10-15_23-00-02Z", Continue = ContinueChoice.Continue }, StartOrigin.AutoContinueFromAlert);

        Assert.That(exitCode, Is.Zero);
        Assert.That(_viewer.Shown.Single(), Is.EqualTo((live, ViewMode.Alert)));
        Assert.That(_starter.Launches, Is.Empty);
    }

    /// <summary>A recorder lost after --minutes ran out is not continued: the recording was over.</summary>
    [Test]
    public async Task A_recorder_lost_after_its_minutes_ran_out_is_not_continued()
    {
        _viewer.Then(Loses(after: TimeSpan.FromMinutes(31)));

        var exitCode = await RunAsync(Request() with { Minutes = 30 });

        Assert.That(exitCode, Is.EqualTo(1));
        Assert.That(_starter.Recorders, Has.Count.EqualTo(1));
        Assert.That(_inWindow.Requests, Is.Empty);
        Assert.That(_ui.Screen, Does.Contain("after the time --minutes gave it had run out, so the recording is not continued."));
    }

    /// <summary>A lost link to a recorder that still runs is no loss of the recording: the window says so and leaves it running, with exit code 1.</summary>
    [Test]
    public async Task A_lost_link_to_a_recorder_that_still_runs_leaves_it_running()
    {
        _viewer.Then((_, _) => new ViewResult(ViewEnd.RecorderLost, null, null));

        var exitCode = await RunAsync();

        Assert.That(exitCode, Is.EqualTo(1));
        Assert.That(_starter.Recorders, Has.Count.EqualTo(1));
        Assert.That(_ui.Screen, Does.Contain("Lost the connection to the recording; it carries on. Start ClassScribe again to see it."));
    }

    /// <summary>A recorder lost before it recorded anything counts as a recorder that did not start: it is started once more.</summary>
    [Test]
    public async Task A_recorder_lost_before_it_recorded_is_started_once_more()
    {
        _recorder.StaysStarting().Records();
        _viewer.Then(Loses()).Then(Ends(_recorder, Transcribed));

        var exitCode = await RunAsync();

        Assert.That(exitCode, Is.Zero);
        Assert.That(_starter.Recorders, Has.Count.EqualTo(2));
        Assert.That(_recorder.Runs[1].Request!.ContinueBaseName, Is.Null);
        Assert.That(_ui.Screen, Does.Contain("Starting the recorder once more."));
    }

    /// <summary>
    /// Two recorders in a row that never get past starting and then end are two failed starts, however long each was
    /// waited for: the run then records in this window, rather than starting recorders for ever.
    /// </summary>
    [Test]
    public async Task Two_recorders_lost_before_they_recorded_leave_the_run_in_this_window()
    {
        _recorder.StaysStarting().StaysStarting();
        _viewer.Then(Loses()).Then(Loses());

        await RunAsync();

        Assert.That(_starter.Recorders, Has.Count.EqualTo(2));
        Assert.That(_inWindow.Requests, Has.Count.EqualTo(1));
        Assert.That(_ui.Screen, Does.Contain("Recording in this window instead. The recorder ended before it recorded."));
    }
}
