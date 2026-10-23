using System.Globalization;
using System.Runtime.Versioning;
using ClassScribe.Commands;
using ClassScribe.Desktop;
using ClassScribe.Recorder;
using Microsoft.Extensions.Logging;
using Moq;

namespace ClassScribe.Tests;

/// <summary>
/// The hidden attach command, which shows the recording in progress, or the run its id names in a window the recorder
/// opened by itself: which run it finds; a run it cannot show, because it records in another window, never existed or has
/// ended; how the view's end becomes the exit code and marks the run seen; and a recorder lost while the view showed it,
/// which is continued as the next part of the same recording, from a window opened by itself too. The run files are real,
/// in a temporary folder; the processes, the clock, the view and the runner are the test's.
/// </summary>
[TestFixture]
public class AttachCommandTests
{
    /// <summary>The base name of the recording the tests' runs make.</summary>
    private const string BaseName = "Kalam_2026-10-15_23-00-02Z";

    /// <summary>The folder of the recording the tests' runs make.</summary>
    private const string Folder = @"D:\Classes\Kalam";

    /// <summary>A run that ended with its transcript saved.</summary>
    private static readonly RunOutcome Transcribed = new(OutcomeKind.Transcribed, 0, @"Transcript saved to D:\Classes\Kalam\Kalam_2026-10-15_23-00-02Z.md",
        @"D:\Classes\Kalam\Kalam_2026-10-15_23-00-02Z.md", Folder, RetryCommand: null);

    /// <summary>A run whose transcript failed, with the command that tries again.</summary>
    private static readonly RunOutcome Failed = new(OutcomeKind.TranscriptFailed, 1, "The transcript failed: ElevenLabs did not answer. The audio is kept.",
        null, Folder, @"ClassScribe transcribe ""D:\Classes\Kalam\Kalam_2026-10-15_23-00-02Z.wav""");

    private TestRuntime _runtime = null!;
    private FakeConsoleUi _ui = null!;
    private FakeWindowSignals _window = null!;
    private Mock<IRunViewer> _viewer = null!;
    private Mock<ICommandRunner> _commands = null!;
    private RecordingLogger<AttachCommand> _log = null!;

    /// <summary>How many times the command turned Ctrl+C back on in its window.</summary>
    private int _ctrlCTurnedOn;

    /// <summary>What Windows answers when Ctrl+C is turned back on: 0 when it did, else its error.</summary>
    private int _ctrlCError;

    /// <summary>ClassScribe's own folder in a temporary folder, at 43 minutes into the class; a view and a runner that do nothing yet.</summary>
    [SetUp]
    public void SetUp()
    {
        _runtime = new TestRuntime();
        _runtime.Time.Advance(TimeSpan.FromMinutes(43));
        _ui = new FakeConsoleUi();
        _window = new FakeWindowSignals();
        _viewer = new Mock<IRunViewer>(MockBehavior.Strict);
        _commands = new Mock<ICommandRunner>(MockBehavior.Strict);
        _log = new RecordingLogger<AttachCommand>();
        _ctrlCTurnedOn = 0;
        _ctrlCError = 0;
    }

    /// <summary>Deletes the folder.</summary>
    [TearDown]
    public void TearDown() => _runtime.Dispose();

    /// <summary>The command under test, which turns Ctrl+C on through the test rather than through Windows.</summary>
    private AttachCommand Command() => new(_runtime.Journal, _viewer.Object, _commands.Object, _ui, _window, _runtime.Time, _log)
    {
        TurnOnCtrlC = () =>
        {
            _ctrlCTurnedOn++;
            return _ctrlCError;
        }
    };

    /// <summary>
    /// Writes the file of the tests' run numbered <paramref name="number"/>, recording "Kalam" in <paramref name="state"/>,
    /// in a separate recorder unless <paramref name="home"/> says otherwise, ended with <paramref name="outcome"/> when one
    /// is given; its process runs when <paramref name="alive"/> is true. Returns the run as a window reads it.
    /// </summary>
    private RunRecord Run(int number, RunState state, bool alive = true, RunHome home = RunHome.Separate, RunOutcome? outcome = null, double? minutes = null)
    {
        var record = TestRuns.Record(TestRuns.Id(number), state, processId: 1000 + number) with
        {
            Home = home,
            BaseName = BaseName,
            Folder = Folder,
            Position = TimeSpan.FromSeconds(47 * 60 + 10),
            Outcome = outcome
        };
        if (home == RunHome.InWindow) record = record with { Pipe = null };
        if (minutes is not null) record = record with { Request = record.Request with { Minutes = minutes } };

        _runtime.Journal.Begin(record).Dispose();
        if (alive) _runtime.Processes.Start(record.ProcessId, record.ProcessStartedUtc);
        return _runtime.Journal.Find(record.RunId)!.Record;
    }

    /// <summary>The view shows the run <paramref name="runId"/> in <paramref name="mode"/>, and its showing ends with <paramref name="result"/>.</summary>
    private void ViewEnds(string runId, ViewMode mode, ViewResult result, Action? meanwhile = null) =>
        _viewer.Setup(viewer => viewer.ShowAsync(It.Is<RunRecord>(run => run.RunId == runId), mode, It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                meanwhile?.Invoke();
                return Task.FromResult(result);
            });

    /// <summary><paramref name="moment"/> in this computer's local time, written with <paramref name="format"/>.</summary>
    private static string Local(DateTimeOffset moment, string format) =>
        TimeZoneInfo.ConvertTime(moment, TimeZoneInfo.Local).ToString(format, CultureInfo.InvariantCulture);

    /// <summary>True when the console shows <paramref name="text"/>.</summary>
    private bool Shown(string text) => _ui.Screen.Contains(text, StringComparison.Ordinal);

    /// <summary>
    /// Without a run named, the recording in progress is shown, as a window other than the one that started it; runs that
    /// are over are passed by. Leaving it with Esc says that the recording carries on, and exits 0.
    /// </summary>
    [Test]
    public async Task Without_a_run_named_the_recording_in_progress_is_shown()
    {
        Run(1, RunState.Finished, alive: false, outcome: Transcribed);
        var recording = Run(2, RunState.Recording);
        ViewEnds(recording.RunId, ViewMode.Viewer, new ViewResult(ViewEnd.Left, null, null));

        var exitCode = await Command().RunAsync(runId: null, alert: false);

        Assert.That(exitCode, Is.Zero);
        Assert.That(Shown("The recording carries on in the background."), Is.True);
        _viewer.VerifyAll();
    }

    /// <summary>With nothing recording, a run being transcribed in the background is shown; leaving it says where its transcript will appear.</summary>
    [Test]
    public async Task Without_a_recording_a_transcription_in_the_background_is_shown()
    {
        var transcribing = Run(1, RunState.Transcribing);
        ViewEnds(transcribing.RunId, ViewMode.Viewer, new ViewResult(ViewEnd.Left, null, null));

        var exitCode = await Command().RunAsync(runId: null, alert: false);

        Assert.That(exitCode, Is.Zero);
        Assert.That(Shown(@"The transcription carries on in the background; its transcript will appear in D:\Classes\Kalam."), Is.True);
    }

    /// <summary>
    /// With --alert, the run named is shown in a window that ignores every key until Enter, even when another run is newer.
    /// Leaving it with Esc, which closes that window, says how to see the recording again.
    /// </summary>
    [Test]
    public async Task With_alert_the_run_named_is_shown_in_a_window_that_waits_for_Enter()
    {
        var named = Run(1, RunState.Recording);
        Run(2, RunState.Starting);
        ViewEnds(named.RunId, ViewMode.Alert, new ViewResult(ViewEnd.Left, null, null));

        var exitCode = await Command().RunAsync(named.RunId, alert: true);

        Assert.That(exitCode, Is.Zero);
        Assert.That(Shown("The recording carries on in the background. Start ClassScribe to see it."), Is.True);
        _viewer.VerifyAll();
    }

    /// <summary>
    /// A window the recorder opened by itself turns Ctrl+C back on before it shows the run. Windows starts that window with
    /// Ctrl+C turned off, as the recorder has it as the root of a process group of its own; left off, Ctrl+C would do
    /// nothing there even after Enter: two would not stop the class, and one after the stop would not cancel the transcription.
    /// </summary>
    [Test]
    public async Task A_window_opened_by_itself_turns_Ctrl_C_back_on_before_it_shows_the_run()
    {
        var run = Run(1, RunState.Recording);
        var onWhenShown = 0;
        ViewEnds(run.RunId, ViewMode.Alert, new ViewResult(ViewEnd.Left, null, null), () => onWhenShown = _ctrlCTurnedOn);

        await Command().RunAsync(run.RunId, alert: true);

        Assert.That(onWhenShown, Is.EqualTo(1), "turned on before the run was shown");
        Assert.That(_ctrlCTurnedOn, Is.EqualTo(1), "and once");
    }

    /// <summary>A window someone started leaves Ctrl+C as it found it: it has Ctrl+C on, unless whoever started it turned it off on purpose.</summary>
    [Test]
    public async Task A_window_someone_started_leaves_Ctrl_C_as_it_found_it()
    {
        var run = Run(1, RunState.Recording);
        ViewEnds(run.RunId, ViewMode.Viewer, new ViewResult(ViewEnd.Left, null, null));

        await Command().RunAsync(run.RunId, alert: false);

        Assert.That(_ctrlCTurnedOn, Is.Zero);
    }

    /// <summary>
    /// When Windows does not turn Ctrl+C back on, the window shows the run all the same, where Q twice still stops it; the
    /// reason is noted in the log, not in the window.
    /// </summary>
    [Test]
    public async Task A_window_where_Ctrl_C_stays_off_shows_the_run_all_the_same()
    {
        _ctrlCError = 6;
        var run = Run(1, RunState.Recording);
        ViewEnds(run.RunId, ViewMode.Alert, new ViewResult(ViewEnd.Left, null, null));

        var exitCode = await Command().RunAsync(run.RunId, alert: true);

        Assert.That(exitCode, Is.Zero);
        _viewer.VerifyAll();
        Assert.That(_log.Entries.Count(entry => entry.Level == LogLevel.Debug && entry.Message.Contains("Windows error 6", StringComparison.Ordinal)), Is.EqualTo(1));
        Assert.That(Shown("Ctrl+C"), Is.False, "nothing about it in the window");
    }

    /// <summary>Leaving a view the menu opened says only that the recording carries on: the menu itself says that A shows it.</summary>
    [Test]
    public async Task Leaving_a_view_from_the_menu_does_not_say_to_start_ClassScribe()
    {
        var run = Run(1, RunState.Recording);
        ViewEnds(run.RunId, ViewMode.Viewer, new ViewResult(ViewEnd.Left, null, null));

        await Command().RunAsync(runId: null, alert: false);

        Assert.That(Shown("Start ClassScribe to see it"), Is.False);
    }

    /// <summary>With nothing recording or being transcribed, the command says so, shows nothing and exits 1.</summary>
    [Test]
    public async Task With_nothing_in_progress_it_says_so_and_exits_1()
    {
        Run(1, RunState.Finished, alive: false, outcome: Transcribed);
        Run(2, RunState.Recording, alive: false);

        var exitCode = await Command().RunAsync(runId: null, alert: false);

        Assert.That(exitCode, Is.EqualTo(1));
        Assert.That(Shown("Nothing is being recorded or transcribed in the background now."), Is.True);
    }

    /// <summary>A run id without a run file this build can read is said to be unknown, and exits 1.</summary>
    [Test]
    public async Task A_run_id_without_a_run_file_exits_1()
    {
        var exitCode = await Command().RunAsync(TestRuns.Id(9), alert: true);

        Assert.That(exitCode, Is.EqualTo(1));
        Assert.That(Shown($"There is no ClassScribe run {TestRuns.Id(9)}."), Is.True);
    }

    /// <summary>A recording in another window has no recorder to show it from: that window shows it, and closing it ends the recording.</summary>
    [Test]
    public async Task A_recording_in_another_window_is_explained_and_exits_1()
    {
        Run(1, RunState.Recording, home: RunHome.InWindow);

        var exitCode = await Command().RunAsync(runId: null, alert: false);

        Assert.That(exitCode, Is.EqualTo(1));
        Assert.That(Shown($"\"Kalam\" is being recorded in another ClassScribe window (process 1001), which shows it; closing that window ends the recording."), Is.True);
    }

    /// <summary>A transcription in another window is explained as such: closing that window ends the transcription.</summary>
    [Test]
    public async Task A_transcription_in_another_window_is_explained_as_one()
    {
        var run = Run(1, RunState.Transcribing, home: RunHome.InWindow);

        var exitCode = await Command().RunAsync(run.RunId, alert: false);

        Assert.That(exitCode, Is.EqualTo(1));
        Assert.That(Shown("\"Kalam\" is being transcribed in another ClassScribe window (process 1001), which shows it; closing that window ends the transcription."), Is.True);
    }

    /// <summary>
    /// A run that ended in another window, which still waits for Enter to close, is shown from its file: it is over, not
    /// being recorded.
    /// </summary>
    [Test]
    public async Task A_run_that_ended_in_another_window_still_open_is_shown_from_its_file()
    {
        var run = Run(1, RunState.Finished, home: RunHome.InWindow, outcome: Transcribed);

        var exitCode = await Command().RunAsync(run.RunId, alert: false);

        Assert.That(exitCode, Is.Zero);
        Assert.That(Shown(@"Transcript saved to D:\Classes\Kalam\Kalam_2026-10-15_23-00-02Z.md"), Is.True);
        Assert.That(Shown("is being recorded"), Is.False);
    }

    /// <summary>The run's end gives the exit code, and the run is marked seen, so that the next start does not report it again.</summary>
    [TestCase(0)]
    [TestCase(1)]
    [TestCase(130)]
    public async Task The_end_gives_its_exit_code_and_marks_the_run_seen(int exitCode)
    {
        var run = Run(1, RunState.Recording);
        var outcome = Transcribed with { ExitCode = exitCode };
        ViewEnds(run.RunId, ViewMode.Viewer, new ViewResult(ViewEnd.RunEnded, exitCode, outcome), () => Finish(run, outcome));

        var exited = await Command().RunAsync(run.RunId, alert: false);

        Assert.That(exited, Is.EqualTo(exitCode));
        Assert.That(_runtime.Journal.Find(run.RunId)!.Record.Seen, Is.True);
    }

    /// <summary>
    /// A recorder that Windows ended sends the window its end, a failure, and leaves its run file recording, so that the
    /// next start reports the recording and offers C to continue it. The window that was shown that end leaves the run
    /// unseen, or the report would be lost.
    /// </summary>
    [Test]
    public async Task A_run_whose_file_still_says_recording_after_its_end_is_left_for_the_next_start()
    {
        var run = Run(1, RunState.Recording);
        var windowsEnded = new RunOutcome(OutcomeKind.RecorderFailed, 1, "Windows ended the session, so the recording stopped.", null, Folder, null);
        ViewEnds(run.RunId, ViewMode.Viewer, new ViewResult(ViewEnd.RunEnded, 1, windowsEnded), () =>
        {
            _runtime.Journal.Begin(run with { EndedBy = RunEndCause.Windows }).Dispose();
            _runtime.Processes.End(run.ProcessId);
        });

        var exitCode = await Command().RunAsync(run.RunId, alert: false);

        Assert.That(exitCode, Is.EqualTo(1));
        var record = _runtime.Journal.Find(run.RunId)!.Record;
        Assert.That((record.State, record.EndedBy, record.Seen), Is.EqualTo((RunState.Recording, RunEndCause.Windows, false)));
    }

    /// <summary>
    /// A window the recorder opened by itself does not mark the run seen at its end: nobody may have looked at it, and it
    /// opens no results, so the next start reports the outcome, with O to open it.
    /// </summary>
    [Test]
    public async Task A_window_opened_by_itself_leaves_the_run_for_the_next_start_to_report()
    {
        var run = Run(1, RunState.Recording);
        ViewEnds(run.RunId, ViewMode.Alert, new ViewResult(ViewEnd.RunEnded, 0, Transcribed), () => Finish(run, Transcribed));

        var exitCode = await Command().RunAsync(run.RunId, alert: true);

        Assert.That(exitCode, Is.Zero);
        Assert.That(_runtime.Journal.Find(run.RunId)!.Record.Seen, Is.False);
    }

    /// <summary>
    /// A run that ended before the window came, as when the recorder opened a window for a failed end and was gone by the
    /// time it started, is shown from its file: the outcome and the command that tries again, and its exit code.
    /// </summary>
    [Test]
    public async Task A_run_that_ended_before_the_window_came_is_shown_from_its_file()
    {
        var run = Run(1, RunState.Finished, alive: false, outcome: Failed);

        var exitCode = await Command().RunAsync(run.RunId, alert: true);

        Assert.That(exitCode, Is.EqualTo(1));
        Assert.That(Shown($"The run of \"Kalam\" (started {Local(TestRuns.Start, "HH:mm")}) has ended."), Is.True);
        Assert.That(Shown("The transcript failed: ElevenLabs did not answer. The audio is kept."), Is.True);
        Assert.That(Shown($"To try again, run: {Failed.RetryCommand}"), Is.True);
        Assert.That(_runtime.Journal.Find(run.RunId)!.Record.Seen, Is.False, "left for the next start, from a window opened by itself");
        Assert.That(_window.Title, Is.EqualTo("! FAILED Kalam | ClassScribe"), "the window shows the failure");
        Assert.That(_window.Taskbar?.State, Is.EqualTo(TaskbarState.Error));
        Assert.That(_window.Flashes, Is.EqualTo(1), "and flashes, so that a window that came minimized is seen");
    }

    /// <summary>The same in a window someone started marks the run seen once shown, and the title says it is done, without a flash.</summary>
    [Test]
    public async Task An_ended_run_shown_from_its_file_in_a_window_someone_started_is_marked_seen()
    {
        var run = Run(1, RunState.Finished, alive: false, outcome: Transcribed);

        var exitCode = await Command().RunAsync(run.RunId, alert: false);

        Assert.That(exitCode, Is.Zero);
        Assert.That(_runtime.Journal.Find(run.RunId)!.Record.Seen, Is.True);
        Assert.That(_window.Title, Is.EqualTo("DONE Kalam | ClassScribe"));
        Assert.That(_window.Flashes, Is.Zero);
    }

    /// <summary>
    /// A recording named whose process is gone is reported as ended unexpectedly, with its audio kept, and exits 1; the
    /// window says so in red, with a flash.
    /// </summary>
    [Test]
    public async Task A_recording_whose_process_is_gone_is_reported_as_ended_unexpectedly()
    {
        var run = Run(1, RunState.Recording, alive: false);

        var exitCode = await Command().RunAsync(run.RunId, alert: false);

        Assert.That(exitCode, Is.EqualTo(1));
        Assert.That(Shown($"The recording \"Kalam\" ({BaseName}) ended unexpectedly at about {Local(run.HeartbeatUtc, "HH:mm")}, 00:47:10 in. Its audio is kept. Start ClassScribe to continue it or to transcribe it."),
            Is.True);
        Assert.That(_window.Title, Is.EqualTo("! ENDED UNEXPECTEDLY Kalam | ClassScribe"));
        Assert.That(_window.Flashes, Is.EqualTo(1));
    }

    /// <summary>
    /// A run named whose process ended while its file still says it was starting is reported as a recording that did not
    /// start, and exits 1; the window says so in red, with a flash.
    /// </summary>
    [Test]
    public async Task A_run_whose_process_ended_while_it_started_is_reported_as_not_started()
    {
        var run = Run(1, RunState.Starting, alive: false);

        var exitCode = await Command().RunAsync(run.RunId, alert: false);

        Assert.That(exitCode, Is.EqualTo(1));
        Assert.That(Shown("The recording \"Kalam\" did not start: its background process ended first."), Is.True);
        Assert.That(Shown("ended unexpectedly"), Is.False);
        Assert.That(_window.Title, Is.EqualTo("! ENDED UNEXPECTEDLY Kalam | ClassScribe"));
        Assert.That(_window.Flashes, Is.EqualTo(1));
    }

    /// <summary>A transcription whose process is gone is reported as not finished, with the command that transcribes the recording.</summary>
    [Test]
    public async Task A_transcription_whose_process_is_gone_is_reported_with_its_command()
    {
        var run = Run(1, RunState.Transcribing, alive: false);

        var exitCode = await Command().RunAsync(run.RunId, alert: false);

        Assert.That(exitCode, Is.EqualTo(1));
        Assert.That(Shown("The transcript of \"Kalam\" was not finished; its audio is complete. To transcribe it (if the upload had finished, ElevenLabs may bill it again), run:"),
            Is.True);
        Assert.That(Shown($@"transcribe ""{Folder}\{BaseName}.wav"""), Is.True);
    }

    /// <summary>
    /// A record-only lesson found in Transcribing with its process gone was having its video made, not its transcript: it is
    /// reported so, with the command that makes the video, free (teach-video 6.9).
    /// </summary>
    [Test]
    public async Task A_lesson_whose_video_s_making_was_cut_off_is_reported_with_finish_video()
    {
        var record = TestRuns.Record(TestRuns.Id(7), RunState.Transcribing, processId: 1007) with { BaseName = BaseName, Folder = Folder };
        record = record with
        {
            Request = record.Request with
            {
                Transcribe = false,
                Source = new SourcePlan(Audio.RecordingMode.Microphone, Class: null, new MicrophoneChoice(null), new CameraChoice("OBSBOT", "720p"))
            }
        };
        _runtime.Journal.Begin(record).Dispose();

        var exitCode = await Command().RunAsync(record.RunId, alert: false);

        Assert.That(exitCode, Is.EqualTo(1));
        Assert.That(Shown("The lesson video of \"Kalam\" was not finished; its sound and its camera pieces are kept. To make it, free, run:"), Is.True);
        Assert.That(Shown("finish-video"), Is.True);
        Assert.That(Shown("The transcript of"), Is.False);
    }

    /// <summary>
    /// A recorder lost while the view showed it recording is continued at once as the next part of the same recording: the
    /// same name, source, folder and choice to transcribe and to open, a new run id, and the start marked as a window
    /// continuing by itself, which the starter takes over. The command exits as that run does.
    /// </summary>
    [Test]
    public async Task A_lost_recorder_is_continued_as_the_next_part_of_the_recording()
    {
        var run = Run(1, RunState.Recording);
        ViewEnds(run.RunId, ViewMode.Viewer, new ViewResult(ViewEnd.RecorderLost, null, null), () => _runtime.Processes.End(run.ProcessId));
        RecordRequest? continued = null;
        _commands.Setup(commands => commands.RecordAsync(It.IsAny<RecordRequest>(), StartOrigin.AutoContinue))
            .Callback<RecordRequest, StartOrigin>((request, _) => continued = request)
            .ReturnsAsync(0);

        var exitCode = await Command().RunAsync(runId: null, alert: false);

        Assert.That(exitCode, Is.Zero);
        Assert.That(continued, Is.Not.Null);
        Assert.That(continued! with { RunId = run.RunId }, Is.EqualTo(run.Request with { Continue = ContinueChoice.Continue, ContinueBaseName = BaseName }));
        Assert.That(RunIds.IsWellFormed(continued.RunId), Is.True);
        Assert.That(continued.RunId, Is.Not.EqualTo(run.RunId));
        Assert.That(Shown($"The recorder ended unexpectedly at {Local(_runtime.Time.GetUtcNow(), "HH:mm:ss")}; continuing the same recording."), Is.True);
    }

    /// <summary>A continued recording made with --minutes keeps only the minutes it had left, counted from its start.</summary>
    [Test]
    public async Task A_continued_recording_keeps_the_minutes_it_had_left()
    {
        var run = Run(1, RunState.Recording, minutes: 90);
        ViewEnds(run.RunId, ViewMode.Viewer, new ViewResult(ViewEnd.RecorderLost, null, null), () => _runtime.Processes.End(run.ProcessId));
        RecordRequest? continued = null;
        _commands.Setup(commands => commands.RecordAsync(It.IsAny<RecordRequest>(), StartOrigin.AutoContinue))
            .Callback<RecordRequest, StartOrigin>((request, _) => continued = request)
            .ReturnsAsync(0);

        await Command().RunAsync(run.RunId, alert: false);

        Assert.That(continued?.Minutes, Is.EqualTo(47).Within(1e-9), "90 minutes from 23:00:02, lost at 23:43:02");
    }

    /// <summary>A recording lost after the minutes it was to record had passed is not continued: it is reported, and exits 1.</summary>
    [Test]
    public async Task A_recording_lost_after_its_minutes_had_passed_is_not_continued()
    {
        var run = Run(1, RunState.Recording, minutes: 30);
        ViewEnds(run.RunId, ViewMode.Viewer, new ViewResult(ViewEnd.RecorderLost, null, null), () => _runtime.Processes.End(run.ProcessId));

        var exitCode = await Command().RunAsync(run.RunId, alert: false);

        Assert.That(exitCode, Is.EqualTo(1));
        Assert.That(Shown("The recording \"Kalam\" (" + BaseName + ") ended unexpectedly at about"), Is.True);
        _commands.VerifyNoOtherCalls();
    }

    /// <summary>
    /// A window the recorder opened by itself continues a lost recorder too, as any attached window does (design 1.14), since
    /// with the window Gulzar closed it is often the only one left: the start says it comes from such a window, so that the
    /// starter keeps its keys waiting for Enter and never records in it. The command exits as the continued run does.
    /// </summary>
    [Test]
    public async Task A_window_opened_by_itself_continues_a_lost_recorder_with_its_keys_still_waiting_for_Enter()
    {
        var run = Run(1, RunState.Recording);
        ViewEnds(run.RunId, ViewMode.Alert, new ViewResult(ViewEnd.RecorderLost, null, null), () => _runtime.Processes.End(run.ProcessId));
        RecordRequest? continued = null;
        _commands.Setup(commands => commands.RecordAsync(It.IsAny<RecordRequest>(), StartOrigin.AutoContinueFromAlert))
            .Callback<RecordRequest, StartOrigin>((request, _) => continued = request)
            .ReturnsAsync(0);

        var exitCode = await Command().RunAsync(run.RunId, alert: true);

        Assert.That(exitCode, Is.Zero);
        Assert.That(continued! with { RunId = run.RunId }, Is.EqualTo(run.Request with { Continue = ContinueChoice.Continue, ContinueBaseName = BaseName }));
        Assert.That(Shown($"The recorder ended unexpectedly at {Local(_runtime.Time.GetUtcNow(), "HH:mm:ss")}; continuing the same recording."), Is.True);
        _commands.VerifyAll();
    }

    /// <summary>
    /// A recorder lost just after it wrote the run's end, before the end reached the window, is not continued: the outcome
    /// in its file is shown, with its exit code, and the run is marked seen.
    /// </summary>
    [Test]
    public async Task A_recorder_lost_after_writing_its_end_shows_the_outcome_from_its_file()
    {
        var run = Run(1, RunState.Recording);
        ViewEnds(run.RunId, ViewMode.Viewer, new ViewResult(ViewEnd.RecorderLost, null, null), () =>
        {
            Finish(run, Failed);
            _runtime.Processes.End(run.ProcessId);
        });

        var exitCode = await Command().RunAsync(run.RunId, alert: false);

        Assert.That(exitCode, Is.EqualTo(1));
        Assert.That(Shown("The transcript failed: ElevenLabs did not answer. The audio is kept."), Is.True);
        _commands.VerifyNoOtherCalls();
        Assert.That(_runtime.Journal.Find(run.RunId)!.Record.Seen, Is.True);
        Assert.That(_window.Title, Is.EqualTo("! FAILED Kalam | ClassScribe"));
    }

    /// <summary>
    /// The same after a saved transcript: the window, which the view showed as ended unexpectedly, is set right, done; the
    /// exit code is the run's.
    /// </summary>
    [Test]
    public async Task A_recorder_lost_after_saving_the_transcript_shows_it_done()
    {
        var run = Run(1, RunState.Recording);
        ViewEnds(run.RunId, ViewMode.Viewer, new ViewResult(ViewEnd.RecorderLost, null, null), () =>
        {
            _window.SetTitle("! ENDED UNEXPECTEDLY Kalam | ClassScribe");
            Finish(run, Transcribed);
            _runtime.Processes.End(run.ProcessId);
        });

        var exitCode = await Command().RunAsync(run.RunId, alert: false);

        Assert.That(exitCode, Is.Zero);
        Assert.That(Shown(@"Transcript saved to D:\Classes\Kalam\Kalam_2026-10-15_23-00-02Z.md"), Is.True);
        Assert.That(_window.Title, Is.EqualTo("DONE Kalam | ClassScribe"));
        Assert.That(_window.Taskbar, Is.EqualTo((TaskbarState.None, 0)));
    }

    /// <summary>A recorder lost while it transcribed is not continued: the recording is complete, and the transcript is reported as not finished.</summary>
    [Test]
    public async Task A_recorder_lost_while_transcribing_is_reported_not_continued()
    {
        var run = Run(1, RunState.Transcribing);
        ViewEnds(run.RunId, ViewMode.Viewer, new ViewResult(ViewEnd.RecorderLost, null, null), () => _runtime.Processes.End(run.ProcessId));

        var exitCode = await Command().RunAsync(run.RunId, alert: false);

        Assert.That(exitCode, Is.EqualTo(1));
        Assert.That(Shown("The transcript of \"Kalam\" was not finished; its audio is complete."), Is.True);
        _commands.VerifyNoOtherCalls();
    }

    /// <summary>A view that ended because the recorder was out of reach, or of another build, exits 1: the view has said why.</summary>
    [TestCase(ViewEnd.NotAnswering)]
    [TestCase(ViewEnd.Refused)]
    public async Task A_recorder_out_of_reach_exits_1(ViewEnd end)
    {
        var run = Run(1, RunState.Recording);
        ViewEnds(run.RunId, ViewMode.Viewer, new ViewResult(end, null, null));

        var exitCode = await Command().RunAsync(run.RunId, alert: false);

        Assert.That(exitCode, Is.EqualTo(1));
        _commands.VerifyNoOtherCalls();
    }

    /// <summary>Writes the end of <paramref name="run"/>, as its recorder does once it is over.</summary>
    private void Finish(RunRecord run, RunOutcome outcome)
    {
        using var handle = _runtime.Journal.Begin(run);
        handle.End(outcome);
    }
}

/// <summary>
/// Turning Ctrl+C back on through Windows itself, as a window the recorder opened by itself does when it starts. It sets
/// only this process's own handling of Ctrl+C to the usual one; nothing is started, recorded or pressed.
/// </summary>
[TestFixture, Platform("Win"), Category("WindowsProcess")]
[SupportedOSPlatform("windows")]
public class ConsoleCtrlCTests
{
    /// <summary>Windows takes the call, so that Ctrl+C reaches the window's handler from then on.</summary>
    [Test]
    public void Ctrl_C_can_be_turned_back_on() => Assert.That(ConsoleCtrlC.TurnOn(), Is.Zero);
}
