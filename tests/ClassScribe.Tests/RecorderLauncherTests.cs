using ClassScribe.Commands;
using ClassScribe.Desktop;
using ClassScribe.Recorder;
using ClassScribe.Settings;

namespace ClassScribe.Tests;

/// <summary>
/// Where a run records (table 1.3 of the design: in a separate recorder, or in this window and why), and starting the
/// separate recorder: the request file first, outside the job when the job allows it and again inside it when that is
/// safe, then the wait until the recorder's run file says it records, or the reason when it ends, is refused, or never
/// answers. The recorders are played through a pretend process starter; the run files are real files in a temporary folder.
/// </summary>
[TestFixture]
public class RecorderLauncherTests
{
    /// <summary>The run most tests start.</summary>
    private static readonly string RunId = TestRuns.Id();

    /// <summary>The settings handed to the recorder: what the settings snapshot gives.</summary>
    private static readonly IReadOnlyDictionary<string, string?> Settings = new Dictionary<string, string?>
    {
        ["Recording:OutputFolder"] = @"D:\Classes",
        ["ElevenLabs:BaseUrl"] = "http://localhost:5001/"
    };

    private TestRuntime _runtime = null!;
    private FakeProcessStarter _starter = null!;
    private PlayedRecorder _recorder = null!;
    private FakeJobInspector _jobs = null!;
    private TestOptions<RecordingOptions> _options = null!;
    private RecordingLogger<RecorderLauncher> _log = null!;

    /// <summary>Makes ClassScribe's own folder in a temporary folder, the pretend processes, no job, and the default settings.</summary>
    [SetUp]
    public void SetUp()
    {
        _runtime = new TestRuntime();
        _starter = new FakeProcessStarter(_runtime.Time);
        _recorder = new PlayedRecorder(_runtime, _starter);
        _jobs = new FakeJobInspector();
        _options = TestOptions.Create(new RecordingOptions());
        _log = new RecordingLogger<RecorderLauncher>();
    }

    /// <summary>Deletes the temporary folder.</summary>
    [TearDown]
    public void TearDown() => _runtime.Dispose();

    /// <summary>The launcher under test, over ClassScribe's own folder in the temporary folder, or over <paramref name="folder"/>.</summary>
    private RecorderLauncher Launcher(RuntimeFolder? folder = null) =>
        new(_starter, _jobs, folder ?? _runtime.Folder, _runtime.Journal, _options, _runtime.Time, _log);

    /// <summary>Starts the run <see cref="RunId"/> and waits for the answer, moving the clock a second at a time while the launcher waits for the recorder.</summary>
    private async Task<LaunchResult> StartAsync(RecordRequest? request = null)
    {
        var starting = Launcher().StartAsync(request ?? TestRuns.Request(RunId), Settings, CancellationToken.None);
        await _runtime.Time.AdvanceUntilAsync(() => starting.IsCompleted, TimeSpan.FromSeconds(1), atMost: TimeSpan.FromSeconds(60));
        return await starting;
    }

    /// <summary>
    /// Row 1: with Recording:SeparateProcess false, the run records in this window, which says so and that closing it ends
    /// the recording, as before.
    /// </summary>
    [Test]
    public void Without_a_separate_process_the_run_records_in_this_window()
    {
        _options.Set(new RecordingOptions { SeparateProcess = false });

        var home = Launcher().ChooseHome();

        Assert.That(home, Is.EqualTo(new RunHomeChoice.InWindow(
            "Recording in this window (Recording:SeparateProcess is false): closing it ends the recording; the audio so far is kept.")));
    }

    /// <summary>
    /// Row 2: in a job that ends every process in it when it closes and lets none out, as under dotnet run, a recorder would
    /// end with the window, so the run records in this window and says so plainly, never that closing the window is safe.
    /// </summary>
    [Test]
    public void Under_dotnet_runs_job_the_run_records_in_this_window_and_says_why()
    {
        _jobs.Facts = FakeJobInspector.DotnetRun;

        var home = Launcher().ChooseHome();

        Assert.That(home, Is.TypeOf<RunHomeChoice.InWindow>());
        var reason = ((RunHomeChoice.InWindow)home).Reason;
        Assert.That(reason, Is.EqualTo(
            "ClassScribe was started in a way that ends every program it starts when it ends, as dotnet run does. So this recording runs in this window: closing it ends the recording, and the audio so far is kept. For classes, start ClassScribe.exe itself (README, Set up once)."));
        Assert.That(reason, Does.Not.Contain("does not stop it"));
    }

    /// <summary>A job Windows says nothing about is taken as the worst case, dotnet run's, so the run records in this window.</summary>
    [Test]
    public void A_job_that_cannot_be_read_keeps_the_run_in_this_window()
    {
        _jobs.Facts = JobFacts.Unknown;

        Assert.That(Launcher().ChooseHome(), Is.TypeOf<RunHomeChoice.InWindow>());
    }

    /// <summary>
    /// Row 3: when ClassScribe's own folder cannot be written, the run records in this window, with the reason, what closing
    /// the window does, and what is missing this time.
    /// </summary>
    [Test]
    public void An_unwritable_runtime_folder_keeps_the_run_in_this_window()
    {
        var blocker = _runtime.Temp.File("not-a-folder");
        File.WriteAllText(blocker, "a file where ClassScribe's folder should be");
        var folder = new RuntimeFolder(Path.Combine(blocker, "ClassScribe"));

        var home = Launcher(folder).ChooseHome();

        Assert.That(home, Is.TypeOf<RunHomeChoice.InWindow>());
        var reason = ((RunHomeChoice.InWindow)home).Reason;
        Assert.That(reason, Does.StartWith($"{folder.Root} cannot be written: "));
        Assert.That(reason, Does.Contain("So this recording runs in this window: closing it ends the recording, and the audio so far is kept."));
        Assert.That(reason, Does.EndWith("One recording at a time and the report after a crash are not available this time."));
    }

    /// <summary>Row 5: otherwise the run records in a separate recorder, started outside the job only where the job allows it.</summary>
    [Test]
    public void Otherwise_the_run_records_in_a_separate_recorder_breaking_away_only_where_allowed()
    {
        Assert.That(Launcher().ChooseHome(), Is.EqualTo(new RunHomeChoice.Separate(Breakaway: false)), "not in a job");

        _jobs.Facts = FakeJobInspector.KillOnCloseWithBreakaway;
        Assert.That(Launcher().ChooseHome(), Is.EqualTo(new RunHomeChoice.Separate(Breakaway: true)), "a job that kills on close but lets the recorder out");

        _jobs.Facts = FakeJobInspector.BreakawayWithoutKill;
        Assert.That(Launcher().ChooseHome(), Is.EqualTo(new RunHomeChoice.Separate(Breakaway: true)), "a job that does not kill on close");
    }

    /// <summary>
    /// The recorder gets its request through the request file, written before it starts, with the settings; once its run
    /// file says it records, it has started, and the run is the one its own file describes.
    /// </summary>
    [Test]
    public async Task A_recorder_whose_run_file_says_it_records_has_started()
    {
        var request = TestRuns.Request(RunId) with { Minutes = 90 };

        var result = await StartAsync(request);

        Assert.That(result, Is.TypeOf<LaunchResult.Started>());
        var run = ((LaunchResult.Started)result).Run;
        Assert.That(run.RunId, Is.EqualTo(RunId));
        Assert.That(run.State, Is.EqualTo(RunState.Recording));
        Assert.That(run.ProcessId, Is.EqualTo(_starter.Recorders.Single().ProcessId));
        Assert.That(_starter.Recorders.Single().Breakaway, Is.False, "not in a job, so nothing to break away from");
        Assert.That(_recorder.Runs.Single().Request, Is.EqualTo(request));
        Assert.That(_recorder.Runs.Single().Settings, Is.EquivalentTo(Settings));
    }

    /// <summary>In a job that kills on close but allows breakaway, the recorder is started outside the job.</summary>
    [Test]
    public async Task In_a_job_that_allows_it_the_recorder_breaks_away()
    {
        _jobs.Facts = FakeJobInspector.KillOnCloseWithBreakaway;

        var result = await StartAsync();

        Assert.That(result, Is.TypeOf<LaunchResult.Started>());
        Assert.That(_starter.Launches.Single().Breakaway, Is.True);
    }

    /// <summary>
    /// When Windows refuses the breakaway with access denied, the recorder is started again inside the job, where it
    /// outlives the window all the same because the job does not end its processes when it closes.
    /// </summary>
    [Test]
    public async Task A_refused_breakaway_is_tried_again_without_it_where_the_recorder_outlives_the_window_anyway()
    {
        _jobs.Facts = FakeJobInspector.BreakawayWithoutKill;
        _starter.RefuseBreakaway = true;

        var result = await StartAsync();

        Assert.That(result, Is.TypeOf<LaunchResult.Started>());
        Assert.That(_starter.Launches.Select(launch => (launch.Breakaway, launch.Started)), Is.EqualTo(new[] { (true, false), (false, true) }));
    }

    /// <summary>
    /// In a job that ends its processes when it closes, a recorder started inside it would end with the window, so a refused
    /// breakaway is not tried again without it: the start fails, and says why.
    /// </summary>
    [Test]
    public async Task A_refused_breakaway_is_not_tried_again_where_the_recorder_would_end_with_the_window()
    {
        _jobs.Facts = FakeJobInspector.KillOnCloseWithBreakaway;
        _starter.RefuseBreakaway = true;

        var result = await StartAsync();

        Assert.That(result, Is.TypeOf<LaunchResult.Failed>());
        Assert.That(((LaunchResult.Failed)result).Reason, Does.Contain("would end with this window"));
        Assert.That(_starter.Launches, Has.Count.EqualTo(1));
        Assert.That(_runtime.FilesInRuns(), Is.Empty, "the request nobody will take is removed");
    }

    /// <summary>A start Windows refuses fails with Windows' own words and error, and leaves no request file behind.</summary>
    [Test]
    public async Task A_start_Windows_refuses_fails_with_its_reason()
    {
        _starter.RefuseWith = 225;

        var result = await StartAsync();

        Assert.That(result, Is.TypeOf<LaunchResult.Failed>());
        var reason = ((LaunchResult.Failed)result).Reason;
        Assert.That(reason, Does.StartWith("Windows did not start the recorder: "));
        Assert.That(reason, Does.Contain("(error 225)"));
        Assert.That(_runtime.FilesInRuns(), Is.Empty);
    }

    /// <summary>
    /// A recorder that ends before it answers, with no run file, fails with its exit code and the last lines it wrote to
    /// recorder.log, and its request is removed.
    /// </summary>
    [Test]
    public async Task A_recorder_that_ends_before_it_answers_fails_with_its_exit_code_and_its_last_lines()
    {
        _recorder.EndsBeforeAnswering(1, "19:00:03  Starting the recorder", "19:00:03  ERROR  Could not build the recorder's host", "          IOException: The disk is full.");

        var result = await StartAsync();

        Assert.That(result, Is.TypeOf<LaunchResult.Failed>());
        var reason = ((LaunchResult.Failed)result).Reason;
        Assert.That(reason, Does.StartWith("The recorder ended before it answered, with exit code 1."));
        Assert.That(reason, Does.Contain("ERROR  Could not build the recorder's host"));
        Assert.That(reason, Does.Contain("IOException: The disk is full."));
        Assert.That(_runtime.FilesInRuns(), Is.Empty, "the request nobody took is removed");
    }

    /// <summary>
    /// A recorder that fails on its own, its run file saying why, fails with that reason; the run is marked seen, since this
    /// window says what happened, so the next start does not report it again.
    /// </summary>
    [Test]
    public async Task A_recorder_that_fails_gives_its_own_reason_and_is_not_reported_again()
    {
        _recorder.Fails("The request file cannot be read: it holds no settings.", exitCode: 4);

        var result = await StartAsync();

        Assert.That(result, Is.TypeOf<LaunchResult.Failed>());
        Assert.That(((LaunchResult.Failed)result).Reason, Does.Contain("The request file cannot be read: it holds no settings."));
        Assert.That(((LaunchResult.Failed)result).Reason, Does.Contain("exit code 4"));
        Assert.That(_runtime.Journal.Find(RunId)!.Record.Seen, Is.True);
    }

    /// <summary>A recorder refused because another recording took the lock first hands back the recording that runs, to show or explain.</summary>
    [Test]
    public async Task A_recorder_refused_by_the_lock_hands_back_the_recording_that_runs()
    {
        var winnerId = TestRuns.Id(2);
        var winner = TestRuns.Record(winnerId, RunState.Recording, processId: 9000, name: "Fiqh");
        _recorder.IsRefused(winner);

        var result = await StartAsync();

        Assert.That(result, Is.TypeOf<LaunchResult.Refused>());
        Assert.That(((LaunchResult.Refused)result).Winner.Record.RunId, Is.EqualTo(winnerId));
        Assert.That(((LaunchResult.Refused)result).Winner.IsRecordingNow, Is.True);
    }

    /// <summary>A recorder refused by the lock when the recording that held it has ended meanwhile fails, so that the start can be tried again.</summary>
    [Test]
    public async Task A_refusal_without_a_recording_still_running_fails()
    {
        _recorder.IsRefused();

        var result = await StartAsync();

        Assert.That(result, Is.TypeOf<LaunchResult.Failed>());
        Assert.That(((LaunchResult.Failed)result).Reason, Does.Contain("Another recording was running"));
    }

    /// <summary>
    /// A recorder that ends with the refusal's exit code, 3, while its run file still says Starting, because writing the
    /// refusal there failed, was refused all the same: the recording that holds the lock is handed back, to show or explain,
    /// rather than a failure that would start a recorder again and then record in this window, into a lock that is held.
    /// </summary>
    [Test]
    public async Task A_recorder_that_ends_with_the_refusals_exit_code_hands_back_the_recording_that_runs()
    {
        var winnerId = TestRuns.Id(2);
        _recorder.IsRefusedWhileItsRunFileSaysStarting(TestRuns.Record(winnerId, RunState.Recording, processId: 9000, name: "Fiqh"));

        var result = await StartAsync();

        Assert.That(result, Is.TypeOf<LaunchResult.Refused>());
        Assert.That(((LaunchResult.Refused)result).Winner.Record.RunId, Is.EqualTo(winnerId));
        Assert.That(_runtime.Journal.Find(RunId)!.Record.State, Is.EqualTo(RunState.Starting), "only the exit code said it was refused");
        Assert.That(_runtime.Journal.Find(RunId)!.Record.Seen, Is.True);
    }

    /// <summary>A recorder that ends with the refusal's exit code after the recording that held the lock has ended fails, so that the start can be tried again.</summary>
    [Test]
    public async Task A_refusals_exit_code_without_a_recording_still_running_fails()
    {
        _recorder.IsRefusedWhileItsRunFileSaysStarting();

        var result = await StartAsync();

        Assert.That(result, Is.TypeOf<LaunchResult.Failed>());
        Assert.That(((LaunchResult.Failed)result).Reason, Is.EqualTo("Another recording was running when this one started, and it has ended since."));
    }

    /// <summary>
    /// A recorder that neither writes its run file nor ends within 20 seconds has not answered: the start fails then, and
    /// not before.
    /// </summary>
    [Test]
    public async Task A_recorder_that_does_not_answer_within_20_seconds_fails()
    {
        _recorder.Hangs();

        var starting = Launcher().StartAsync(TestRuns.Request(RunId), Settings, CancellationToken.None);
        var waited = await _runtime.Time.AdvanceUntilAsync(() => starting.IsCompleted, TimeSpan.FromSeconds(1), atMost: TimeSpan.FromSeconds(30));
        var result = await starting;

        Assert.That(waited, Is.GreaterThanOrEqualTo(RecorderLauncher.AnswerPatience));
        Assert.That(result, Is.TypeOf<LaunchResult.Failed>());
        Assert.That(((LaunchResult.Failed)result).Reason, Does.Contain($"(process {_starter.Recorders.Single().ProcessId}) did not answer within 20 seconds"));
        Assert.That(_runtime.FilesInRuns(), Is.Empty, "the request it never took is removed, so that it finds nothing to record if it wakes up");
    }

    /// <summary>
    /// A recorder whose run file still says Starting after 20 seconds has made its pipe, which it does before it writes the
    /// file, so it has started: the view of the run takes over, and says so if it stays silent.
    /// </summary>
    [Test]
    public async Task A_recorder_still_starting_after_20_seconds_is_handed_to_the_view()
    {
        _recorder.StaysStarting();

        var result = await StartAsync();

        Assert.That(result, Is.TypeOf<LaunchResult.Started>());
        Assert.That(((LaunchResult.Started)result).Run.State, Is.EqualTo(RunState.Starting));
    }
}
