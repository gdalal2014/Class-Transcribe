using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Text.Json;
using ClassScribe.Audio;
using ClassScribe.Commands;
using ClassScribe.Desktop;
using ClassScribe.Link;
using ClassScribe.Recorder;
using ClassScribe.Settings;
using ClassScribe.Storage;
using ClassScribe.Transcription;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ClassScribe.Tests;

/// <summary>
/// The recorder process, as Program hands it a run: it takes the request a window wrote, builds its host from the request's
/// settings and the key in its own environment, serves its windows on the run's pipe, records at once whether or not a
/// window shows it, stops for the keys a window passes on and for the stop asked for from another window, sends the end to
/// its windows and waits a little for them, opens the results itself when no window will, brings back a window when the
/// run fails unseen, and ends with the recorder's own exit codes. ClassScribe's own folder is a temporary one, the recording
/// is described by hand, ElevenLabs and the desktop are mocked, no process is ever started, and the windows are played over
/// in-memory connections to the recorder's real server; only the run's pipe is real, named after a fresh run id, and nothing
/// connects to it.
/// </summary>
[TestFixture, Platform("Win"), Category("WindowsProcess")]
[SupportedOSPlatform("windows")]
public partial class RecorderHostTests
{
    /// <summary>How long a test waits for the run to end once it has asked for the stop.</summary>
    private static readonly TimeSpan EndPatience = TimeSpan.FromSeconds(30);

    private TempFolder _folder = null!;
    private TestRuntime _runtime = null!;
    private ManualTimeProvider _time = null!;
    private FakeRecordingFactory _recordings = null!;
    private FakeProcessStarter _starter = null!;
    private FakePowerRequests _power = null!;
    private Mock<IDesktop> _desktop = null!;
    private Mock<ITranscriptionClient> _client = null!;
    private FakeElevenLabsAccount _account = null!;
    private RecorderServer? _server;
    private RecorderLog? _log;
    private List<TestWindow> _windows = null!;
    private string? _runId;
    private bool? _runFileExistedWhenThePipeWasMade;

    /// <summary>The recordings folder, ClassScribe's own folder, the clock, the recording, and the stand-ins.</summary>
    [SetUp]
    public void SetUp()
    {
        _folder = new TempFolder();
        _runtime = new TestRuntime();
        _time = new ManualTimeProvider(TestRuns.Start);
        _recordings = new FakeRecordingFactory();
        _starter = new FakeProcessStarter(_time);
        _power = new FakePowerRequests();
        _desktop = new Mock<IDesktop>();
        _client = Mocks.TranscriptionClient();
        _account = new FakeElevenLabsAccount();
        _server = null;
        _log = null;
        _windows = [];
        _runId = null;
        _runFileExistedWhenThePipeWasMade = null;
        _google = null;
        _store = null;
        _videoWindows = new FakeVideoWindows();
    }

    /// <summary>Closes the windows and deletes the folders; a file left open fails the deletion, which is the point.</summary>
    [TearDown]
    public async Task TearDown()
    {
        foreach (var window in _windows) await window.DisposeAsync();
        _runtime.Dispose();
        _folder.Dispose();
    }

    /// <summary>
    /// The recorder's host over the test's folders and clock, with the stand-ins in place of the recording, the processes,
    /// the power, the session, ElevenLabs (the service and the account's reader, which answers that the read is off unless
    /// a test queues readings) and the desktop; its server, made over its log, is kept for the windows the test
    /// attaches, and its log for the lines the test looks at. The server's pipe is the real one, and making its first
    /// instance notes whether the run file existed already: it must not, since a run file that says starting tells the
    /// window that the pipe is up.
    /// </summary>
    private RecorderHost Host() => new()
    {
        Folder = _runtime.Folder,
        Places = new SettingsPlaces(AppContext.BaseDirectory, userFolder: null),
        Time = _time,
        ConfigureServices = services =>
        {
            _log = (RecorderLog)services.Single(descriptor => descriptor.ServiceType == typeof(RecorderLog)).ImplementationInstance!;
            services.Replace(ServiceDescriptor.Singleton(new RoamingFolder(_folder.File("Roaming"))));
            OnGoogleDrive(services);
            services.Replace(ServiceDescriptor.Transient<IRecordingFactory>(_ => _recordings));
            services.Replace(ServiceDescriptor.Singleton<IProcessStarter>(_starter));
            services.Replace(ServiceDescriptor.Singleton<IPowerRequests>(_power));
            services.Replace(ServiceDescriptor.Singleton<IPowerSource>(new FakePowerSource()));
            services.Replace(ServiceDescriptor.Singleton<ISystemTimes>(new FakeSystemTimes(_time)));
            services.Replace(ServiceDescriptor.Scoped<ISessionEvents>(_ => new FakeSessionEvents()));
            services.Replace(ServiceDescriptor.Transient<ITranscriptionClient>(_ => _client.Object));
            services.Replace(ServiceDescriptor.Transient<IElevenLabsAccount>(_ => _account));
            services.Replace(ServiceDescriptor.Singleton(Mocks.AudioPreparer().Object));
            services.Replace(ServiceDescriptor.Singleton(_desktop.Object));
            services.Replace(ServiceDescriptor.Singleton<IVideoWindows>(_videoWindows));
            services.Replace(ServiceDescriptor.Singleton<IProcessFamily>(new ProcessFamily(new FakeProcessTree().Add(Environment.ProcessId, 0, "testhost.exe", ThisProcessStarted))));
            services.Replace(ServiceDescriptor.Singleton(provider => _server = new RecorderServer(
                provider.GetRequiredService<RecorderLog>(), provider.GetRequiredService<TimeProvider>(), provider.GetRequiredService<ILogger<RecorderServer>>())
            {
                MakePipe = (pipeName, first) =>
                {
                    if (first) _runFileExistedWhenThePipeWasMade = _runId is { } runId && _runtime.Journal.Find(runId) is not null;
                    return RecorderServer.NewPipe(pipeName, first);
                }
            }));
        }
    };

    /// <summary>The settings a window hands the recorder: the recordings go to the test's folder, no glossary, and ElevenLabs at a port nothing answers on.</summary>
    private Dictionary<string, string?> Snapshot() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["Recording:OutputFolder"] = _folder.Path,
        ["ElevenLabs:KeytermsFile"] = "",
        ["ElevenLabs:BaseUrl"] = "http://127.0.0.1:9/"
    };

    /// <summary>
    /// A request to record Kalam from the device the class plays on into the test's folder, written as the window writes it
    /// before it starts the recorder, with <paramref name="settings"/> or the usual snapshot, and the glossary the window decided, when given.
    /// </summary>
    private RecordRequest WriteRequest(bool transcribe = false, bool open = true, Dictionary<string, string?>? settings = null, GlossaryPick? glossary = null)
    {
        var request = new RecordRequest(RunIds.New(_time.GetUtcNow()), "Kalam", transcribe, new SourcePlan(RecordingMode.Class, new ClassSource.Automatic(), Microphone: null),
            _folder.Path, ContinueChoice.New, ContinueBaseName: null, Minutes: null, open, Glossary: glossary);
        _runtime.Journal.WriteRequest(request, settings ?? Snapshot());
        _runId = request.RunId;
        return request;
    }

    /// <summary>Writes the run file of a run that was seen long ago, which the recorder's pruning deletes.</summary>
    private void WriteAnOldSeenRun()
    {
        var seen = TestRuns.Record(TestRuns.Id(1), RunState.Finished) with { Seen = true, Outcome = new RunOutcome(OutcomeKind.Recorded, 0, "Recorded.", null, null, null) };
        using (_runtime.Journal.Begin(seen))
        {
        }

        _time.Advance(TimeSpan.FromDays(31));
    }

    /// <summary>
    /// Waits until the recording has started, and gives it a segment so that the run has something to keep; fails, naming
    /// the exit code, should the run end first.
    /// </summary>
    private async Task<FakeRecording> StartedAsync(Task<int> run)
    {
        while (_recordings.Created is not [{ Started: true }])
        {
            if (run.IsCompleted) Assert.Fail($"The run ended with exit code {await run} before it recorded.");
            await Task.Delay(10);
        }

        var recording = _recordings.Created[0];
        recording.MainSegments = [recording.Files!.Segment(1)];
        return recording;
    }

    /// <summary>Waits for the run to end, for at most <see cref="EndPatience"/>, and returns its exit code.</summary>
    private static async Task<int> EndedAsync(Task<int> run) => await run.WaitAsync(EndPatience);

    /// <summary>Asks for the stop from another window, through the run's stop event, as a window of any build can.</summary>
    private static void StopFromAnotherWindow(string runId) =>
        Assert.That(StopSignal.TrySet(runId, NullLogger.Instance), Is.True, "the recorder listens for the stop");

    /// <summary>The run's file as it is now.</summary>
    private RunRecord RunFile(string runId) => _runtime.Journal.Find(runId)!.Record;

    /// <summary>The lines the recorder has logged so far.</summary>
    private IReadOnlyList<string> LoggedLines() => [.. _log!.Since(0).Lines.Select(line => line.Text)];

    /// <summary>Attaches a window to the recorder's server once it serves the run, closed when the test ends unless the test closes it first.</summary>
    private async Task<TestWindow> AttachAsync(bool owner = false)
    {
        await Wait.UntilAsync(() => _server is not null);
        var (window, _) = await TestWindow.AttachAsync(_server!, TestWindow.Hello(owner: owner));
        _windows.Add(window);
        await Wait.UntilAsync(() => _server!.Windows == _windows.Count);
        return window;
    }

    /// <summary>Closes <paramref name="window"/> and waits until the server has let it go.</summary>
    private async Task CloseAsync(TestWindow window)
    {
        _windows.Remove(window);
        await window.DisposeAsync();
        await window.GoneAsync();
    }

    /// <summary>Reads what the recorder sends until a state in <paramref name="phase"/> comes, and returns it.</summary>
    private static async Task<RecorderState> NextStateAsync(TestWindow window, RecorderPhase phase)
    {
        while (true)
        {
            var envelope = await window.NextAsync() ?? throw new AssertionException($"The recorder let the window go before a state in {phase} came.");
            if (envelope.Type == LinkTypes.State && envelope.Body.Deserialize(LinkJson.Default.StateUpdate)!.State is { } state && state.Phase == phase) return state;
        }
    }

    // ------------------------------------------------------------------ the start

    /// <summary>
    /// Program hands a run to the recorder only for exactly "--recorder" and a run id, before the command line is parsed
    /// and before the console is touched; anything else is the command line's, which reports it.
    /// </summary>
    [Test]
    public void Only_the_recorder_switch_with_its_run_id_makes_a_recorder()
    {
        var id = TestRuns.Id();

        Assert.That(RecorderHost.IsRecorderStart([ProcessArguments.Recorder, id], out var runId), Is.True);
        Assert.That(runId, Is.EqualTo(id));
        Assert.That(RecorderHost.IsRecorderStart(["record", "Kalam"], out _), Is.False);
        Assert.That(RecorderHost.IsRecorderStart([ProcessArguments.Recorder], out _), Is.False);
        Assert.That(RecorderHost.IsRecorderStart([ProcessArguments.Recorder, id, "--no-open"], out _), Is.False);
        Assert.That(RecorderHost.IsRecorderStart(["--Recorder", id], out _), Is.False, "the switch is written one way");
        Assert.That(RecorderHost.IsRecorderStart([], out _), Is.False);
    }

    /// <summary>
    /// A run id that is not one ends the recorder with exit code 4 before it names any file, pipe or event with it; the
    /// reason is in recorder.log, the only place a recorder without a run can write.
    /// </summary>
    [Test]
    public async Task A_run_id_that_is_not_one_ends_with_exit_code_4_before_it_names_anything()
    {
        var exitCode = await Host().RunAsync(@"..\not-a-run-id", apiKey: null);

        Assert.That(exitCode, Is.EqualTo(RecorderHost.UnreadableRequestExitCode));
        Assert.That(_runtime.FilesInRuns(), Is.Empty);
        Assert.That(File.ReadAllText(Path.Combine(_runtime.Folder.Root, RecorderLog.StartupFileName)), Does.Contain("is not a ClassScribe run id"));
    }

    /// <summary>Without a request, the window that would have written it gave up: the recorder ends with exit code 4 and records nothing.</summary>
    [Test]
    public async Task Without_a_request_the_recorder_ends_with_exit_code_4()
    {
        var runId = RunIds.New(_time.GetUtcNow());

        var exitCode = await Host().RunAsync(runId, apiKey: null);

        Assert.That(exitCode, Is.EqualTo(RecorderHost.UnreadableRequestExitCode));
        Assert.That(_runtime.Journal.Find(runId), Is.Null);
        Assert.That(_recordings.Created, Is.Empty);
    }

    /// <summary>
    /// A request that cannot be read ends the recorder with exit code 4 and a run file that says it failed, with the reason,
    /// for the window that started it; the request file is deleted, since it can never be used.
    /// </summary>
    [Test]
    public async Task An_unreadable_request_ends_with_exit_code_4_and_a_failed_run_file()
    {
        var runId = RunIds.New(_time.GetUtcNow());
        var requestFile = _runtime.Journal.RequestFile(runId);
        Directory.CreateDirectory(Path.GetDirectoryName(requestFile)!);
        File.WriteAllText(requestFile, "not a request");

        var exitCode = await Host().RunAsync(runId, apiKey: null);

        Assert.That(exitCode, Is.EqualTo(RecorderHost.UnreadableRequestExitCode));
        Assert.That(File.Exists(requestFile), Is.False, "deleted once read");
        var run = RunFile(runId);
        Assert.That((run.State, run.Outcome?.Kind, run.Outcome?.ExitCode), Is.EqualTo((RunState.Failed, OutcomeKind.RecorderFailed, RecorderHost.UnreadableRequestExitCode)));
        Assert.That(run.Outcome!.Message, Does.Contain("cannot be read"));
        Assert.That(_recordings.Created, Is.Empty);
    }

    /// <summary>
    /// The request is taken once, then the recorder records at once, with no window attached: the run file says so, with the
    /// run's home and pipe and this process, for the windows that come later. The stop asked for from another window ends
    /// the run as Q does; the run file keeps the outcome, unseen, for a window to show.
    /// </summary>
    [Test]
    public async Task The_request_is_taken_once_and_the_run_records_with_no_window_attached()
    {
        var request = WriteRequest();
        var run = Host().RunAsync(request.RunId, apiKey: null);
        var recording = await StartedAsync(run);

        Assert.That(File.Exists(_runtime.Journal.RequestFile(request.RunId)), Is.False, "taken once");
        var whileRecording = RunFile(request.RunId);
        Assert.That((whileRecording.State, whileRecording.Home, whileRecording.Pipe, whileRecording.ProcessId, whileRecording.Build, whileRecording.Request),
            Is.EqualTo((RunState.Recording, RunHome.Separate, LinkProtocol.PipeNameFor(request.RunId), Environment.ProcessId, BuildInfo.Build, request)));
        Assert.That(_server!.Windows, Is.Zero, "nobody is attached");
        var dateFolder = ClassFolders.DateFolderName(DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(TestRuns.Start, _time.LocalTimeZone).DateTime));
        Assert.That(recording.Files!.Folder, Is.EqualTo(Path.Combine(_folder.Path, "Kalam", dateFolder)),
            "the request's folder, from the settings handed over, with the course's folder and the date folder of the recorder's clock");

        StopFromAnotherWindow(request.RunId);

        Assert.That(await EndedAsync(run), Is.EqualTo(0));
        var finished = RunFile(request.RunId);
        Assert.That((finished.State, finished.Outcome?.Kind, finished.EndedBy, finished.Seen), Is.EqualTo((RunState.Finished, OutcomeKind.Recorded, RunEndCause.Q, false)));
        Assert.That(recording.Stopped && recording.Disposed, Is.True);
    }

    /// <summary>
    /// The recorder's lines go to recorder.log until the recording's folder is known, and from the start of the process
    /// into the recording's own log beside its audio, which keeps the start-up lines too.
    /// </summary>
    [Test]
    public async Task The_recorder_logs_into_recorder_log_and_then_into_the_recordings_own_log()
    {
        var request = WriteRequest();
        var run = Host().RunAsync(request.RunId, apiKey: null);
        var recording = await StartedAsync(run);
        StopFromAnotherWindow(request.RunId);
        Assert.That(await EndedAsync(run), Is.EqualTo(0));

        var startup = File.ReadAllText(Path.Combine(_runtime.Folder.Root, RecorderLog.StartupFileName));
        var own = File.ReadAllText(recording.Files!.Log);
        Assert.That(startup, Does.Contain($"run {request.RunId}"));
        Assert.That(own, Does.Contain($"run {request.RunId}").And.Contain("Recording \"Kalam\" to").And.Contain("Closing a window that shows this recording does not stop it"));
    }

    /// <summary>The recorder prunes the run files that have nothing left to report when it starts, as the design says, so that they never pile up.</summary>
    [Test]
    public async Task The_recorder_prunes_old_run_files_when_it_starts()
    {
        var lost = TestRuns.Record(TestRuns.Id(2), RunState.Recording);
        using (_runtime.Journal.Begin(lost))
        {
        }

        WriteAnOldSeenRun();
        var request = WriteRequest();
        var run = Host().RunAsync(request.RunId, apiKey: null);
        await StartedAsync(run);
        StopFromAnotherWindow(request.RunId);
        await EndedAsync(run);

        Assert.That(_runtime.Journal.Find(TestRuns.Id(1)), Is.Null, "pruned");
        Assert.That(_runtime.Journal.Find(lost.RunId), Is.Not.Null, "kept for its report");
    }

    /// <summary>
    /// The lines the recorder logs once it has taken its request and before its host exists, such as the pruning of old
    /// run files, follow the Logging section the window handed over, as the host's lines do: recorder.log reads the same
    /// before and after the host is built. Only the lines before the request is read, when no settings are known yet, are
    /// at the usual levels.
    /// </summary>
    [Test]
    public async Task The_start_up_lines_follow_the_Logging_section_handed_over_once_the_request_is_read()
    {
        WriteAnOldSeenRun();
        var snapshot = Snapshot();
        snapshot["Logging:LogLevel:Default"] = "Debug";
        var request = WriteRequest(settings: snapshot);
        var run = Host().RunAsync(request.RunId, apiKey: null);
        await StartedAsync(run);
        StopFromAnotherWindow(request.RunId);
        await EndedAsync(run);

        var startup = File.ReadAllText(Path.Combine(_runtime.Folder.Root, RecorderLog.StartupFileName));
        Assert.That(startup, Does.Contain("Deleted 1 old files from"), "the journal's line about the pruning, logged at Debug, at the level handed over");
    }

    /// <summary>
    /// The recorder's safety stop follows the ElevenLabs:LongestFileHours of the settings the window handed over (architecture
    /// 7.2), never ElevenLabs' own 10 hours: with a file of 5 hours, the default stop of 9 hours comes at 4 hours 30 minutes,
    /// and the line names the 5 hours. The monitor the recorder's host makes is the one that reads it.
    /// </summary>
    [Test]
    public async Task The_safety_stop_follows_the_file_limit_the_window_handed_over()
    {
        var snapshot = Snapshot();
        snapshot["ElevenLabs:LongestFileHours"] = "5";
        var request = WriteRequest(settings: snapshot);
        var run = Host().RunAsync(request.RunId, apiKey: null);
        var recording = await StartedAsync(run);
        recording.Written = new TimeSpan(4, 30, 0);

        await _time.AdvanceUntilAsync(() => run.IsCompleted, TimeSpan.FromSeconds(1), timeoutSeconds: 30);

        Assert.That(await EndedAsync(run), Is.EqualTo(0));
        Assert.That(RunFile(request.RunId).EndedBy, Is.EqualTo(RunEndCause.SafetyStop));
        Assert.That(LoggedLines(), Has.Some.Contains(
            "This recording stops now, after 4 hours 30 minutes (Recording:StopAfterHours is 9, above what one file to ElevenLabs can hold): ElevenLabs takes at most 5 hours of audio in one file"));
    }

    /// <summary>
    /// The run file is written only once the pipe is up: a window that finds the run file starting connects to the pipe at
    /// once, so a run file written first would send it to a pipe that does not exist yet.
    /// </summary>
    [Test]
    public async Task The_run_file_is_written_only_once_the_pipe_is_up()
    {
        var request = WriteRequest();
        var run = Host().RunAsync(request.RunId, apiKey: null);
        await StartedAsync(run);
        StopFromAnotherWindow(request.RunId);
        await EndedAsync(run);

        Assert.That(_runFileExistedWhenThePipeWasMade, Is.False, "no run file yet when the pipe's first instance was made");
    }

    /// <summary>
    /// While another recording holds the lock, the recorder writes that its start was refused and ends with the recorder's own
    /// exit code for it, 3, so that the window that started it shows the recording that runs instead.
    /// </summary>
    [Test]
    public async Task A_start_the_lock_refuses_is_written_as_refused_and_ends_with_exit_code_3()
    {
        using var other = new RecordingLock(_runtime.Folder, NullLogger<RecordingLock>.Instance).TryTake();
        Assert.That(other, Is.Not.Null);
        var request = WriteRequest();

        var exitCode = await EndedAsync(Host().RunAsync(request.RunId, apiKey: null));

        Assert.That(exitCode, Is.EqualTo(RunBookkeeping.RefusedExitCode));
        var run = RunFile(request.RunId);
        Assert.That((run.State, run.Outcome?.Kind), Is.EqualTo((RunState.Refused, OutcomeKind.Refused)));
        Assert.That(_recordings.Created, Is.Empty);
    }

    /// <summary>
    /// When another process holds the run's pipe name, the recorder cannot serve its windows, so it does not record: its run
    /// file says it failed, with the reason, and it ends with exit code 1, and the window records itself instead.
    /// </summary>
    [Test]
    public async Task A_pipe_name_another_process_holds_ends_the_recorder_with_a_failed_run_file()
    {
        var request = WriteRequest();
        using var squatter = new NamedPipeServerStream(LinkProtocol.PipeNameFor(request.RunId), PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

        var exitCode = await EndedAsync(Host().RunAsync(request.RunId, apiKey: null));

        Assert.That(exitCode, Is.EqualTo(1));
        var run = RunFile(request.RunId);
        Assert.That((run.State, run.Outcome?.Kind), Is.EqualTo((RunState.Failed, OutcomeKind.RecorderFailed)));
        Assert.That(run.Outcome!.Message, Does.Contain("pipe"));
        Assert.That(_recordings.Created, Is.Empty);
    }

    // ------------------------------------------------------------------ the windows

    /// <summary>The keys a window passes on reach the recording: P pauses it, and Q twice stops it, as in the window.</summary>
    [Test]
    public async Task Keys_passed_on_from_a_window_reach_the_recording()
    {
        var request = WriteRequest();
        var run = Host().RunAsync(request.RunId, apiKey: null);
        var recording = await StartedAsync(run);
        var window = await AttachAsync();

        await window.SendKeyAsync(LinkKeys.P);
        await Wait.UntilAsync(() => recording.Paused);
        await window.SendKeyAsync(LinkKeys.Q);
        await window.SendKeyAsync(LinkKeys.Q);
        var (_, end) = await window.UntilEndAsync();
        await CloseAsync(window);

        Assert.That(await EndedAsync(run), Is.EqualTo(0));
        Assert.That(end.Outcome.Kind, Is.EqualTo(OutcomeKind.Recorded));
        Assert.That(RunFile(request.RunId).EndedBy, Is.EqualTo(RunEndCause.Q));
        Assert.That(LoggedLines(), Has.Some.Contains("Press Q again within 5 seconds to stop"));
    }

    /// <summary>Ctrl+C passed on from a window reaches the run's signals: the first press asks for a second, and the second stops the recording.</summary>
    [Test]
    public async Task Ctrl_C_passed_on_from_a_window_stops_the_recording_at_the_second_press()
    {
        var request = WriteRequest();
        var run = Host().RunAsync(request.RunId, apiKey: null);
        await StartedAsync(run);
        var window = await AttachAsync();

        await window.SendKeyAsync(LinkKeys.CtrlC);
        await Wait.UntilAsync(() => LoggedLines().Any(line => line.Contains("Press Ctrl+C again within 5 seconds to stop")));
        Assert.That(run.IsCompleted, Is.False, "one press does not stop");
        await window.SendKeyAsync(LinkKeys.CtrlC);
        await window.UntilEndAsync();
        await CloseAsync(window);

        Assert.That(await EndedAsync(run), Is.EqualTo(0));
    }

    /// <summary>
    /// At the end, the window that started the run is told to open the results, and the recorder waits for its windows to
    /// take the end and go: up to 2 seconds, then it ends all the same. The recorder opens nothing itself then.
    /// </summary>
    [Test]
    public async Task The_end_goes_to_the_owner_window_to_open_and_the_recorder_waits_up_to_2_seconds_for_it()
    {
        var request = WriteRequest();
        var run = Host().RunAsync(request.RunId, apiKey: null);
        await StartedAsync(run);
        var window = await AttachAsync(owner: true);

        await window.SendKeyAsync(LinkKeys.Q);
        await window.SendKeyAsync(LinkKeys.Q);
        var (_, end) = await window.UntilEndAsync();
        await Task.Delay(300);
        var endedAtOnce = run.IsCompleted;
        _time.Advance(TimeSpan.FromSeconds(2));

        Assert.That(await EndedAsync(run), Is.EqualTo(0));
        Assert.That(end.Open, Is.True, "the owner opens the results");
        Assert.That(endedAtOnce, Is.False, "the recorder waited for the window");
        _desktop.VerifyNoOtherCalls();
    }

    /// <summary>A window that takes the end and goes lets the recorder end at once, without waiting out the 2 seconds.</summary>
    [Test]
    public async Task A_window_that_takes_the_end_and_goes_lets_the_recorder_end_at_once()
    {
        var request = WriteRequest();
        var run = Host().RunAsync(request.RunId, apiKey: null);
        await StartedAsync(run);
        var window = await AttachAsync(owner: true);

        await window.SendKeyAsync(LinkKeys.Q);
        await window.SendKeyAsync(LinkKeys.Q);
        await window.UntilEndAsync();
        await CloseAsync(window);

        Assert.That(await EndedAsync(run), Is.EqualTo(0));
    }

    /// <summary>
    /// With no window attached at the end, the recorder opens the results itself, as the settings say: the recording's folder
    /// here, and the log says so. With --no-open it opens nothing, and says nothing of opening.
    /// </summary>
    [TestCase(true, 1)]
    [TestCase(false, 0)]
    public async Task With_nobody_attached_the_recorder_opens_the_results_itself_unless_told_not_to(bool open, int opened)
    {
        var request = WriteRequest(open: open);
        var run = Host().RunAsync(request.RunId, apiKey: null);
        var recording = await StartedAsync(run);
        StopFromAnotherWindow(request.RunId);

        Assert.That(await EndedAsync(run), Is.EqualTo(0));
        _desktop.Verify(desktop => desktop.OpenFolder(recording.Files!.Folder), Times.Exactly(opened));
        _desktop.VerifyNoOtherCalls();
        Assert.That(LoggedLines().Count(line => line.Contains(OpensTheResultsItself)), Is.EqualTo(opened));
        Assert.That(_account.Reads, Is.Zero, "without a transcript there is no cost to report");
    }

    /// <summary>What the recorder logs when it opens the results because no window will.</summary>
    private const string OpensTheResultsItself = "the recorder opens the results itself";

    /// <summary>
    /// A run that fails with no window attached opens the alert window, so that the failure is seen: here the transcript
    /// fails for want of a key. Nothing is opened on the desktop for a failure, and the log does not say that the recorder
    /// opens the results: there are none.
    /// </summary>
    [Test]
    public async Task A_failed_end_with_nobody_attached_opens_the_alert_window()
    {
        var request = WriteRequest(transcribe: true);
        var run = Host().RunAsync(request.RunId, apiKey: null);
        await StartedAsync(run);
        StopFromAnotherWindow(request.RunId);

        Assert.That(await EndedAsync(run), Is.EqualTo(1));
        Assert.That(RunFile(request.RunId).Outcome?.Kind, Is.EqualTo(OutcomeKind.TranscriptFailed));
        Assert.That(_starter.AlertWindows.Select(launch => launch.RunId), Is.EqualTo(new[] { request.RunId }));
        _desktop.VerifyNoOtherCalls();
        Assert.That(LoggedLines(), Has.None.Contains(OpensTheResultsItself), "there is nothing to open for a failure");
        Assert.That(_account.Reads, Is.Zero, "a transcript that failed has no cost to report");
    }

    /// <summary>
    /// After the stop the windows keep hearing from the recorder once a second, through the upload too (the mocked service
    /// says nothing of how far it is, so the run stays uploading), so that none takes a long upload for a recorder that
    /// stopped answering; at the end the transcript is saved and the owner is told to open it.
    /// </summary>
    [Test]
    public async Task The_windows_hear_from_the_recorder_once_a_second_through_the_upload()
    {
        var upload = new TaskCompletionSource<TranscriptionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _client.Setup(Calls.AnyTranscription).Returns(upload.Task);
        var request = WriteRequest(transcribe: true);
        var run = Host().RunAsync(request.RunId, "dummy-key-for-a-local-test");
        var recording = await StartedAsync(run);
        TestAudio.WriteWav(recording.Files!.Segment(1), 16000, TestAudio.Tone(16000, seconds: 0.5, frequency: 440, amplitude: 0.5));
        var window = await AttachAsync(owner: true);

        await window.SendKeyAsync(LinkKeys.Q);
        await window.SendKeyAsync(LinkKeys.Q);
        await NextStateAsync(window, RecorderPhase.Uploading);
        for (var second = 0; second < 3; second++)
        {
            _time.Advance(TimeSpan.FromSeconds(1));
            await NextStateAsync(window, RecorderPhase.Uploading);
        }

        upload.SetResult(Mocks.Transcript);
        var (_, end) = await window.UntilEndAsync();
        await CloseAsync(window);

        Assert.That(await EndedAsync(run), Is.EqualTo(0));
        Assert.That((end.Outcome.Kind, end.Open), Is.EqualTo((OutcomeKind.Transcribed, true)));
        Assert.That(File.Exists(end.Outcome.TranscriptPath), Is.True);
    }

    /// <summary>
    /// What the transcript cost the account comes last of all, once the end has gone to the windows, which open the results:
    /// the owner window has the end, and has left, before the account is read again, so that the read never holds the end
    /// up. The line then goes into the recorder's log after the saved transcript, where the recording's own log takes it too.
    /// </summary>
    [Test]
    public async Task The_account_is_read_again_only_once_the_end_has_gone_to_the_windows()
    {
        _account.Answer(FakeElevenLabsAccount.Read(0), FakeElevenLabsAccount.Read(500));
        var endSeen = new TaskCompletionSource();
        bool? endSeenAtTheSecondRead = null;
        _account.OnRead = number =>
        {
            if (number == 2) endSeenAtTheSecondRead = endSeen.Task.IsCompleted;
        };
        var request = WriteRequest(transcribe: true);
        var run = Host().RunAsync(request.RunId, "dummy-key-for-a-local-test");
        var recording = await StartedAsync(run);
        TestAudio.WriteWav(recording.Files!.Segment(1), 16000, TestAudio.Tone(16000, seconds: 0.5, frequency: 440, amplitude: 0.5));
        var window = await AttachAsync(owner: true);

        await window.SendKeyAsync(LinkKeys.Q);
        await window.SendKeyAsync(LinkKeys.Q);
        var (_, end) = await window.UntilEndAsync();
        endSeen.SetResult();
        await CloseAsync(window);

        Assert.That(await EndedAsync(run), Is.EqualTo(0));
        Assert.That((end.Outcome.Kind, end.Open), Is.EqualTo((OutcomeKind.Transcribed, true)));
        Assert.That(endSeenAtTheSecondRead, Is.True, "the window had the end, and had left, before the account was read again");
        var lines = LoggedLines().ToList();
        var saved = lines.FindIndex(line => line.Contains("Transcript saved to", StringComparison.Ordinal));
        var charged = lines.FindIndex(line => line.Contains("ElevenLabs charged 500 credits for this transcript; 99,500 of 100,000 credits are left", StringComparison.Ordinal));
        Assert.That(saved, Is.GreaterThanOrEqualTo(0), "the saved line is in the recorder's log");
        Assert.That(charged, Is.GreaterThan(saved), "the credits charged come last");
        Assert.That(_account.Reads, Is.EqualTo(2));
    }

    /// <summary>
    /// A recorder writes the recording's glossary record from the glossary its request carries, which the window decided where
    /// the saved classes are, says it in its log, and sends it with the transcript, though its own settings name none
    /// (architecture 8.2, item 7).
    /// </summary>
    [Test]
    public async Task The_recorder_writes_the_record_from_the_request_and_sends_its_glossary()
    {
        var glossary = _folder.File("mantiq.txt");
        File.WriteAllLines(glossary, ["kalām", "manṭiq"]);
        var sent = new List<IReadOnlyList<string>>();
        _client.Setup(Calls.AnyTranscription).Callback((string _, IReadOnlyList<string> terms, CancellationToken _) => sent.Add(terms)).ReturnsAsync(Mocks.Transcript);
        var request = WriteRequest(transcribe: true, glossary: new GlossaryPick("mantiq.txt", glossary, "the saved class \"Mantiq\"", "Mantiq"));
        var run = Host().RunAsync(request.RunId, "dummy-key-for-a-local-test");
        var recording = await StartedAsync(run);
        TestAudio.WriteWav(recording.Files!.Segment(1), 16000, TestAudio.Tone(16000, seconds: 0.5, frequency: 440, amplitude: 0.5));

        StopFromAnotherWindow(request.RunId);

        Assert.That(await EndedAsync(run), Is.EqualTo(0));
        Assert.That(sent, Is.EqualTo(new[] { new[] { "kalām", "manṭiq" } }));
        var record = new GlossaryFiles(new SettingsPlaces(_folder.Path, userFolder: null)).ReadRecord(recording.Files!)!;
        Assert.That((record.Decided.Path, record.Decided.From, record.Decided.Terms), Is.EqualTo((glossary, "the saved class \"Mantiq\"", 2)));
        Assert.That(record.Sent.Single().Copy, Is.EqualTo(1));
        Assert.That(LoggedLines(), Has.Some.EndsWith("Glossary: mantiq.txt (the saved class \"Mantiq\"), 2 terms"));
        Assert.That(LoggedLines(), Has.Some.EndsWith("Glossary: mantiq.txt, as decided when this recording started (2 terms then, 2 now)"));
    }
}