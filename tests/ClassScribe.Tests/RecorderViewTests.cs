using System.Globalization;
using System.Runtime.Versioning;
using ClassScribe.Commands;
using ClassScribe.Desktop;
using ClassScribe.Link;
using ClassScribe.Recorder;
using ClassScribe.Runs;
using ClassScribe.Settings;
using Microsoft.Extensions.Logging;
using Moq;

namespace ClassScribe.Tests;

/// <summary>
/// The view of a run that records in a separate recorder, as a window shows it: the heading, the log so far and then each
/// new line, none twice across a reconnect; the recording keys passed on, and Ctrl+C even when the input is redirected;
/// Esc, which leaves without stopping anything; a window the recorder opened by itself, which ignores every key until
/// Enter; the title, the taskbar and a flash from the run's states; the end, with its exit code and the results opened only
/// by the window told to; a recorder lost, silent, out of reach or of another build. The recorder is a real server over
/// in-memory connections, on a clock the test moves; the console, the window, the desktop and Ctrl+C are the test's.
/// </summary>
[TestFixture]
public class RecorderViewTests
{
    /// <summary>How far the clock moves at a time while a test waits for the client's retries.</summary>
    private static readonly TimeSpan Step = TimeSpan.FromMilliseconds(50);

    /// <summary>How far the clock moves at a time while a test waits out the client's patience, of ten or twenty seconds.</summary>
    private static readonly TimeSpan LongStep = TimeSpan.FromMilliseconds(500);

    /// <summary>When the tests look at the run: 43 minutes into the class.</summary>
    private static readonly DateTimeOffset Now = TestRuns.Start.AddMinutes(43);

    private ManualTimeProvider _time = null!;
    private FakeProcessLiveness _processes = null!;
    private RunRecord _run = null!;
    private TestRecorder _recorder = null!;
    private FakeConsoleUi _ui = null!;
    private FakeWindowSignals _window = null!;
    private Mock<IDesktop> _desktop = null!;
    private FakeCtrlC _ctrlC = null!;
    private FakeStopRequests _stops = null!;
    private TestOptions<RecordingOptions> _settings = null!;
    private RecordingLogger<RecorderView> _log = null!;
    private List<Task<ViewResult>> _shows = null!;
    private CancellationTokenSource _patience = null!;

    /// <summary>A recorder of the run "Kalam" whose process runs, a console a person types into, and a window, on a clock moved by hand.</summary>
    [SetUp]
    public void SetUp()
    {
        _time = new ManualTimeProvider(Now);
        _processes = new FakeProcessLiveness();
        _run = TestRuns.Record(TestRuns.Id(), RunState.Recording) with { BaseName = "Kalam_2026-10-15_23-00-02Z", Folder = @"D:\Classes\Kalam" };
        _processes.Start(_run.ProcessId, _run.ProcessStartedUtc);
        _recorder = new TestRecorder(_time, _run);
        _ui = new FakeConsoleUi();
        _window = new FakeWindowSignals();
        _desktop = new Mock<IDesktop>();
        _ctrlC = new FakeCtrlC();
        _stops = new FakeStopRequests();
        _settings = TestOptions.Create(new RecordingOptions());
        _log = new RecordingLogger<RecorderView>();
        _shows = [];
        _patience = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    }

    /// <summary>Ends every view still showing, then the recorder.</summary>
    [TearDown]
    public async Task TearDown()
    {
        await _patience.CancelAsync();
        await Task.WhenAny(Task.WhenAll(_shows), Task.Delay(TestWindow.Patience));
        await _recorder.DisposeAsync();
        _patience.Dispose();
    }

    /// <summary>A view on the test's console, window, desktop and Ctrl+C, unless others are given, that reaches the test's recorder.</summary>
    private RecorderView NewView(FakeConsoleUi? ui = null, IDesktop? desktop = null) =>
        new(ui ?? _ui, _window, desktop ?? _desktop.Object, _processes, _settings, _time, _log, new RecordingLogger<RecorderClient>())
        {
            OpenPipe = _recorder.OpenPipeAsync,
            WatchCtrlC = _ctrlC.Watch,
            AskToStop = _stops.Ask
        };

    /// <summary>Starts showing the run in <paramref name="mode"/> with <paramref name="view"/> (a new one unless given); ended when the test ends.</summary>
    private Task<ViewResult> Show(ViewMode mode, RecorderView? view = null)
    {
        var showing = Task.Run(() => (view ?? NewView()).ShowAsync(_run, mode, _patience.Token));
        _shows.Add(showing);
        return showing;
    }

    /// <summary>A state of the run at <paramref name="seconds"/> into the recording, in <paramref name="phase"/>.</summary>
    private static RecorderState State(int seconds, RecorderPhase phase = RecorderPhase.Recording) =>
        new(phase, "Kalam", TimeSpan.FromSeconds(seconds), "Speakers", null, null, null, null, Attention.None, InWindow: false);

    /// <summary>A state that needs attention now: no audio from chrome, the <paramref name="count"/>th time something needed attention.</summary>
    private static RecorderState Alerting(int count) =>
        State(60) with { Attention = new Attention(AttentionLevel.Alert, "NO AUDIO from \"chrome\"", "No audio has arrived from \"chrome\" for 5 seconds", count) };

    /// <summary>How the run ends: transcribed.</summary>
    private static RunOutcome Transcribed() =>
        new(OutcomeKind.Transcribed, 0, @"Transcript saved to D:\Classes\Kalam\Kalam_2026-10-15_23-00-02Z.md", @"D:\Classes\Kalam\Kalam_2026-10-15_23-00-02Z.md", @"D:\Classes\Kalam", null);

    /// <summary><paramref name="moment"/> in this computer's local time, written with <paramref name="format"/>, as the view writes it.</summary>
    private static string Local(DateTimeOffset moment, string format) =>
        TimeZoneInfo.ConvertTime(moment, TimeZoneInfo.Local).ToString(format, CultureInfo.InvariantCulture);

    /// <summary>Waits until the console shows <paramref name="text"/>.</summary>
    private Task UntilShown(string text, FakeConsoleUi? ui = null) => Wait.UntilAsync(() => (ui ?? _ui).Screen.Contains(text, StringComparison.Ordinal));

    /// <summary>The lines the console shows, in order.</summary>
    private List<string> Lines(FakeConsoleUi? ui = null) => [.. (ui ?? _ui).Screen.Split(Environment.NewLine)];

    /// <summary>How many lines the console shows that hold <paramref name="text"/>.</summary>
    private int Count(string text, FakeConsoleUi? ui = null) => Lines(ui).Count(line => line.Contains(text, StringComparison.Ordinal));

    /// <summary>The index of the first line that holds <paramref name="text"/>; -1 when none does.</summary>
    private int IndexOf(string text) => Lines().FindIndex(line => line.Contains(text, StringComparison.Ordinal));

    /// <summary>The next key the recorder's loop gets from the windows.</summary>
    private async Task<ConsoleKeyInfo> NextKeyAsync()
    {
        using var patience = new CancellationTokenSource(TestWindow.Patience);
        return await _recorder.Server.Keys.ReadAsync(patience.Token);
    }

    /// <summary>Waits until <paramref name="count"/> windows show the run.</summary>
    private Task UntilWindows(int count) => Wait.UntilAsync(() => _recorder.Server.Windows == count);

    /// <summary>
    /// The heading names the run, when it started and how far it is, then the log so far comes, then each new line; after a
    /// break of the link while the recorder runs, the window says it reconnected and is shown only the lines it lacks, so
    /// no line is shown twice and none is missing.
    /// </summary>
    [Test]
    public async Task The_log_so_far_then_live_lines_come_each_once_across_a_reconnect()
    {
        _recorder.Server.SetState(State(43 * 60 + 2));
        _recorder.WriteLines(3);
        var showing = Show(ViewMode.Viewer);
        await UntilShown("line 3");
        _recorder.WriteLines(5, from: 4);
        await UntilShown("line 5");

        _recorder.BreakLast();
        _recorder.WriteLines(7, from: 6);
        await _time.AdvanceUntilAsync(() => _recorder.OpenedAt.Count == 2, Step);
        await UntilShown("line 7");
        _ui.Type('\u001b');
        await showing.WaitAsync(TestWindow.Patience);

        var heading = $"ClassScribe is recording \"Kalam\" (started {Local(TestRuns.Start, "HH:mm")}; 00:43:02 so far). What happened so far:";
        Assert.That(Count(heading), Is.EqualTo(1));
        for (var n = 1; n <= 7; n++) Assert.That(Count($"line {n}"), Is.EqualTo(1), $"line {n} is shown once");
        Assert.That(Enumerable.Range(1, 7).Select(n => IndexOf($"line {n}")), Is.Ordered, "the lines in order");
        Assert.That(IndexOf(heading), Is.LessThan(IndexOf("line 1")), "the heading first");
        Assert.That(IndexOf("Reconnected to the recording."), Is.GreaterThan(IndexOf("line 5")).And.LessThan(IndexOf("line 6")));
        Assert.That(IndexOf("Esc leaves this view; the recording carries on in the background."), Is.GreaterThanOrEqualTo(0).And.LessThan(IndexOf(heading)));
    }

    /// <summary>
    /// When the link is made again and the recorder no longer holds lines the window lacks, the window says so, and where the
    /// whole log is, after it says it reconnected and before the lines that follow.
    /// </summary>
    [Test]
    public async Task Lines_lost_while_the_link_was_made_again_are_pointed_to_in_the_log_file()
    {
        var connections = 0;
        _recorder.Answer = async byHand =>
        {
            await byHand.HelloAsync();
            var (oldest, state) = Interlocked.Increment(ref connections) == 1 ? (1L, State(60)) : (40L, State(90));
            await byHand.SendAsync(LinkTypes.Welcome, new Welcome(LinkProtocol.Version, LinkProtocol.Build, _run.ExePath, _run, state, oldest), LinkJson.Default.Welcome);
            await byHand.SendAsync(LinkTypes.Log, new LogLine(oldest, Now, LogLevel.Information, $"line {oldest}"), LinkJson.Default.LogLine);
        };

        var showing = Show(ViewMode.Viewer);
        await UntilShown("line 1");
        _recorder.BreakLast();
        await _time.AdvanceUntilAsync(() => _recorder.OpenedAt.Count == 2, Step);
        await UntilShown("line 40");
        _ui.Type('\u001b');
        await showing.WaitAsync(TestWindow.Patience);

        const string note = @"Some lines of the log are no longer held here; the whole log is in D:\Classes\Kalam\Kalam_2026-10-15_23-00-02Z.log.";
        Assert.That(Count(note), Is.EqualTo(1));
        Assert.That(IndexOf(note), Is.GreaterThan(IndexOf("Reconnected to the recording.")).And.LessThan(IndexOf("line 40")));
    }

    /// <summary>
    /// The line that Esc leaves the view names what carries on in the background: the transcription, for a run being
    /// transcribed, whose heading says so.
    /// </summary>
    [Test]
    public async Task Esc_is_said_to_leave_the_transcription_running_for_a_run_being_transcribed()
    {
        _recorder.Server.SetState(State(3600, RecorderPhase.Uploading) with { UploadPercent = 40 });
        var showing = Show(ViewMode.Viewer);
        await UntilShown("What happened so far:");
        _ui.Type('\u001b');
        await showing.WaitAsync(TestWindow.Patience);

        Assert.That(Count("Esc leaves this view; the transcription carries on in the background."), Is.EqualTo(1));
        Assert.That(Count("ClassScribe is transcribing \"Kalam\" (started"), Is.EqualTo(1));
    }

    /// <summary>
    /// When the recorder no longer holds the first lines of its log, the window says so and where the whole log is, rather
    /// than show a log that seems to start part way.
    /// </summary>
    [Test]
    public async Task Lines_the_recorder_no_longer_holds_are_pointed_to_in_the_log_file()
    {
        _recorder.Answer = async byHand =>
        {
            await byHand.HelloAsync();
            await byHand.SendAsync(LinkTypes.Welcome, new Welcome(LinkProtocol.Version, LinkProtocol.Build, _run.ExePath, _run, State(60), OldestSeq: 120),
                LinkJson.Default.Welcome);
            await byHand.SendAsync(LinkTypes.Log, new LogLine(120, Now, LogLevel.Information, "line 120"), LinkJson.Default.LogLine);
        };

        var showing = Show(ViewMode.Viewer);
        await UntilShown("line 120");
        _ui.Type('\u001b');
        await showing.WaitAsync(TestWindow.Patience);

        Assert.That(IndexOf(@"Some lines of the log are no longer held here; the whole log is in D:\Classes\Kalam\Kalam_2026-10-15_23-00-02Z.log."),
            Is.GreaterThanOrEqualTo(0).And.LessThan(IndexOf("line 120")));
    }

    /// <summary>
    /// Q, P, M and V reach the recorder's loop, in the order they were pressed, with or without Shift; other keys, and
    /// these keys held with Ctrl or Alt, as a shortcut meant for another program, do not.
    /// </summary>
    [Test]
    public async Task The_recording_keys_reach_the_recorder_and_no_others()
    {
        var showing = Show(ViewMode.Viewer);
        await UntilWindows(1);

        _ui.Type('q', 'p', 'x', 'm');
        _ui.TypeHolding(ConsoleModifiers.Control, 'q');
        _ui.TypeHolding(ConsoleModifiers.Alt, 'p');
        _ui.Type('v', 'Q');
        List<ConsoleKey> keys = [];
        for (var n = 0; n < 5; n++) keys.Add((await NextKeyAsync()).Key);
        _ui.Type('\u001b');
        await showing.WaitAsync(TestWindow.Patience);

        Assert.That(keys, Is.EqualTo(new[] { ConsoleKey.Q, ConsoleKey.P, ConsoleKey.M, ConsoleKey.V, ConsoleKey.Q }));
        Assert.That(_recorder.Server.Keys.TryRead(out var extra), Is.False, $"no other key reached the recorder ({extra.Key})");
    }

    /// <summary>
    /// Esc says bye and leaves: the recorder hears that the window left with Esc, no key or Ctrl+C reaches it, nothing is
    /// asked to stop, and the run carries on. The view ends as left, without an exit code of the run's, and the window no
    /// longer shows the run's state.
    /// </summary>
    [Test]
    public async Task Esc_says_bye_and_stops_nothing()
    {
        var ctrlC = 0;
        _recorder.Server.CtrlCPressed += () => Interlocked.Increment(ref ctrlC);
        _recorder.Server.SetState(State(42));
        var showing = Show(ViewMode.Owner);
        await Wait.UntilAsync(() => _window.Title == "REC 00:00:42 Kalam | ClassScribe");

        _ui.Type('\u001b');
        var result = await showing.WaitAsync(TestWindow.Patience);

        // The recorder notes why the window left once it has let it go.
        await Wait.UntilAsync(() => _recorder.ServerLog.Messages.Any(message => message.Contains("left the view of this recording (Esc)", StringComparison.Ordinal)));
        Assert.That(result, Is.EqualTo(new ViewResult(ViewEnd.Left, null, null)));
        Assert.That(_recorder.Server.Windows, Is.Zero);
        Assert.That(_recorder.Server.Keys.TryRead(out _), Is.False, "no key reached the recorder");
        Assert.That(ctrlC, Is.Zero);
        Assert.That(_stops.Asked, Is.Empty);
        Assert.That(_window.Title, Is.EqualTo("ClassScribe"), "the title no longer shows a state that stops changing");
        Assert.That(_window.Taskbar, Is.EqualTo((TaskbarState.None, 0)));
        Assert.That(_ctrlC.Watchers, Is.Zero, "Ctrl+C is no longer watched");
    }

    /// <summary>
    /// A window the recorder opened by itself ignores every key until Enter: Q, P, M, V, S, W, Esc and Ctrl+C reach nothing
    /// and leave nothing, and the hint to press Enter is shown once for a burst of keys, not once per key. After Enter the
    /// keys work as in any window.
    /// </summary>
    [Test]
    public async Task A_window_opened_by_itself_ignores_every_key_until_Enter()
    {
        var ctrlC = 0;
        _recorder.Server.CtrlCPressed += () => Interlocked.Increment(ref ctrlC);
        var showing = Show(ViewMode.Alert);
        await UntilWindows(1);
        await UntilShown("ClassScribe opened this window by itself. Keys do nothing here until you press Enter.");
        await Wait.UntilAsync(() => _ctrlC.Watchers == 1);

        _ctrlC.Press();
        await UntilShown("Press Enter to control the recording from this window");
        _ui.Type('q', 'p', 'm', 'v', 's', 'w', '\u001b', 'q');
        _ui.Type('\r');
        _ui.Type('m');
        var first = await NextKeyAsync();
        var lockedLines = Count("Press Enter to control the recording from this window");
        _ctrlC.Press();
        await Wait.UntilAsync(() => ctrlC == 1);
        _ui.Type('\u001b');
        var result = await showing.WaitAsync(TestWindow.Patience);

        Assert.That(first.Key, Is.EqualTo(ConsoleKey.M), "the first key to reach the recorder came after Enter");
        Assert.That(_recorder.Server.Keys.TryRead(out _), Is.False);
        Assert.That(_stops.Asked, Is.Empty, "S did nothing before Enter");
        Assert.That(lockedLines, Is.EqualTo(1), "one hint for the burst of keys");
        Assert.That(Count("This window controls the recording now"), Is.EqualTo(1));
        Assert.That(result.End, Is.EqualTo(ViewEnd.Left), "Esc left only after Enter");
    }

    /// <summary>
    /// A window the recorder opened by itself flashes once as it shows the run, so that its button, which comes minimized
    /// without the focus, is seen, even while nothing needs attention; for an Alert it flashes once, not twice.
    /// </summary>
    [TestCase(false, 1)]
    [TestCase(true, 1)]
    public async Task A_window_opened_by_itself_flashes_once_as_it_opens(bool alerting, int flashes)
    {
        _recorder.Server.SetState(alerting ? Alerting(1) : State(42));
        var showing = Show(ViewMode.Alert);
        await UntilShown("What happened so far:");
        await Wait.UntilAsync(() => _window.Flashes >= 1);
        _ui.Type('\r', '\u001b');
        await showing.WaitAsync(TestWindow.Patience);

        Assert.That(_window.Flashes, Is.EqualTo(flashes));
    }

    /// <summary>A window someone started does not flash as it shows a run that needs no attention.</summary>
    [Test]
    public async Task A_window_someone_started_does_not_flash_for_nothing()
    {
        _recorder.Server.SetState(State(42));
        var showing = Show(ViewMode.Viewer);
        await UntilShown("What happened so far:");
        _ui.Type('\u001b');
        await showing.WaitAsync(TestWindow.Patience);

        Assert.That(_window.Flashes, Is.Zero);
    }

    /// <summary>
    /// Keys typed before the run was shown, such as while the window connected, are not passed on: P pressed then would
    /// pause the class without anyone seeing why.
    /// </summary>
    [Test]
    public async Task Keys_typed_before_the_run_was_shown_are_dropped()
    {
        _ui.TypeAhead('p', 'm');
        var showing = Show(ViewMode.Viewer);
        await UntilShown("What happened so far:");
        _ui.Type('q');
        var key = await NextKeyAsync();
        _ui.Type('\u001b');
        await showing.WaitAsync(TestWindow.Patience);

        Assert.That(key.Key, Is.EqualTo(ConsoleKey.Q));
        Assert.That(_recorder.Server.Keys.TryRead(out _), Is.False);
    }

    /// <summary>A key typed before the question about a recorder of another build appeared does not answer it: S typed early stops nothing.</summary>
    [Test]
    public async Task A_key_typed_before_a_question_does_not_answer_it()
    {
        _recorder.Answer = async byHand =>
        {
            await byHand.HelloAsync();
            await byHand.SendAsync(LinkTypes.Refused, new Refused("Protocol 2 against 1", @"C:\Old\ClassScribe.exe"), LinkJson.Default.Refused);
        };
        _ui.TypeAhead('s');

        var showing = Show(ViewMode.Viewer);
        await UntilShown("any other key leaves it running");
        _ui.Type('x');
        var result = await showing.WaitAsync(TestWindow.Patience);

        Assert.That(result.End, Is.EqualTo(ViewEnd.Refused));
        Assert.That(_stops.Asked, Is.Empty);
    }

    /// <summary>
    /// In a window the recorder opened by itself, the question about a recorder of another build waits for Enter, as every
    /// key there does, and says so: before Enter, Q, Esc, S, W and Ctrl+C stop nothing and leave nothing, and show the hint
    /// to press Enter once. After Enter, S stops the recording through its stop event.
    /// </summary>
    [Test]
    public async Task A_window_opened_by_itself_answers_the_question_about_another_build_only_after_Enter()
    {
        _recorder.Answer = async byHand =>
        {
            await byHand.HelloAsync();
            await byHand.SendAsync(LinkTypes.Refused, new Refused("Protocol 2 against 1", @"C:\Old\ClassScribe.exe"), LinkJson.Default.Refused);
        };

        var showing = Show(ViewMode.Alert);
        await UntilShown("Press Enter, then S stops the recording (the audio so far is kept); any other key leaves it running.");
        _ctrlC.Press();
        _ui.Type('q', '\u001b', 's', 'w', '\u0003');
        await Wait.UntilAsync(() => _ui.KeyReads >= 6);
        var stopsBeforeEnter = _stops.Asked.Count;
        var endedBeforeEnter = showing.IsCompleted;

        _ui.Type('\r', 's');
        var result = await showing.WaitAsync(TestWindow.Patience);

        Assert.That(stopsBeforeEnter, Is.Zero, "S did nothing before Enter");
        Assert.That(endedBeforeEnter, Is.False, "Esc and the other keys left nothing before Enter");
        Assert.That(Count(RecorderView.EnterHint), Is.EqualTo(1), "one hint for the keys before Enter");
        Assert.That(result, Is.EqualTo(new ViewResult(ViewEnd.Refused, null, null)));
        Assert.That(_stops.Asked, Is.EqualTo(new[] { _run.RunId }), "S after Enter stops the recording");
    }

    /// <summary>
    /// In a window the recorder opened by itself, the question about a recorder that does not answer at the start waits for
    /// Enter too, and says so: before Enter, Q, Esc, S, W and Ctrl+C stop nothing, try nothing again and leave nothing, and
    /// show the hint once. After Enter, W tries again, and S stops the recording when the recorder still does not answer.
    /// </summary>
    [Test]
    public async Task A_window_opened_by_itself_answers_the_question_about_a_silent_recorder_only_after_Enter()
    {
        _recorder.Answering = false;
        var showing = Show(ViewMode.Alert);
        var question = $"The recorder is not answering (process {TestRuns.ProcessId}, started {Local(TestRuns.ProcessStart, "HH:mm")}). Its audio so far is on disk. Press Enter, then W keeps waiting, S stops it, Esc leaves it running.";
        await _time.AdvanceUntilAsync(() => _ui.Screen.Contains(question, StringComparison.Ordinal), LongStep, atMost: TimeSpan.FromSeconds(25));
        var tries = _recorder.OpenedAt.Count;

        _ctrlC.Press();
        _ui.Type('q', '\u001b', 's', 'w', '\u0003');
        await Wait.UntilAsync(() => _ui.KeyReads >= 6);
        var stopsBeforeEnter = _stops.Asked.Count;
        var endedBeforeEnter = showing.IsCompleted;
        var triesBeforeEnter = _recorder.OpenedAt.Count;

        // The second wait of 20 seconds begins only once the view has read Enter and W, while the clock may already move.
        _ui.Type('\r', 'w');
        await _time.AdvanceUntilAsync(() => Count("W keeps waiting, S stops it, Esc leaves it running.") == 2, LongStep, atMost: TimeSpan.FromSeconds(45));
        var triesAfterW = _recorder.OpenedAt.Count;
        _ui.Type('s');
        var result = await showing.WaitAsync(TestWindow.Patience);

        Assert.That(stopsBeforeEnter, Is.Zero, "S did nothing before Enter");
        Assert.That(endedBeforeEnter, Is.False, "Esc and the other keys left nothing before Enter");
        Assert.That(triesBeforeEnter, Is.EqualTo(tries), "W tried nothing again before Enter");
        Assert.That(Count(RecorderView.EnterHint), Is.EqualTo(1), "one hint for the keys before Enter");
        Assert.That(triesAfterW, Is.GreaterThan(tries), "W after Enter tries again");
        Assert.That(Count("Press Enter, then"), Is.EqualTo(1), "the question asked again after Enter does not ask for Enter");
        Assert.That(result, Is.EqualTo(new ViewResult(ViewEnd.NotAnswering, null, null)));
        Assert.That(_stops.Asked, Is.EqualTo(new[] { _run.RunId }), "S after Enter stops the recording");
    }

    /// <summary>
    /// A window the recorder opened by itself whose keys Enter freed at a question before the run was shown does not say
    /// afterwards that keys do nothing until Enter: it says that Esc leaves, as any window does, and Esc does.
    /// </summary>
    [Test]
    public async Task A_window_opened_by_itself_and_freed_before_the_run_was_shown_says_that_Esc_leaves()
    {
        _recorder.Answering = false;
        var showing = Show(ViewMode.Alert);
        await _time.AdvanceUntilAsync(() => _ui.Screen.Contains("Press Enter, then W keeps waiting", StringComparison.Ordinal), LongStep, atMost: TimeSpan.FromSeconds(25));

        _recorder.Answering = true;
        _ui.Type('\r', 'w');
        await UntilShown("What happened so far:");
        _ui.Type('\u001b');
        var result = await showing.WaitAsync(TestWindow.Patience);

        Assert.That(Count("Keys do nothing here until you press Enter"), Is.Zero);
        Assert.That(Count("Esc leaves this view; the recording carries on in the background."), Is.EqualTo(1));
        Assert.That(result.End, Is.EqualTo(ViewEnd.Left));
    }

    /// <summary>The hint to press Enter is shown again for a key pressed once five seconds have passed since it was last shown.</summary>
    [Test]
    public async Task The_hint_to_press_Enter_comes_again_after_five_seconds()
    {
        var showing = Show(ViewMode.Alert);
        await UntilWindows(1);
        _ui.Type('q');
        await UntilShown("Press Enter to control the recording from this window");
        _time.Advance(TimeSpan.FromSeconds(4));
        _ui.Type('p', '\r', 'm');
        await NextKeyAsync();
        var afterFour = Count("Press Enter to control the recording from this window");

        _ui.Type('\u001b');
        await showing.WaitAsync(TestWindow.Patience);

        var again = Show(ViewMode.Alert);
        await UntilWindows(1);
        var before = Count("Press Enter to control the recording from this window");
        _ui.Type('q');
        await Wait.UntilAsync(() => Count("Press Enter to control the recording from this window") == before + 1);
        _time.Advance(TimeSpan.FromSeconds(5));
        _ui.Type('p');
        await Wait.UntilAsync(() => Count("Press Enter to control the recording from this window") == before + 2);
        _ui.Type('\r', '\u001b');
        await again.WaitAsync(TestWindow.Patience);

        Assert.That(afterFour, Is.EqualTo(1), "not again within five seconds");
    }

    /// <summary>
    /// Ctrl+C is passed on to the recorder as it is pressed, even in a window whose input is redirected, as a script's is,
    /// where no key is ever read: the window says hello as one nobody types into.
    /// </summary>
    [Test]
    public async Task Ctrl_C_is_passed_on_even_when_the_input_is_redirected()
    {
        var ctrlC = 0;
        _recorder.Server.CtrlCPressed += () => Interlocked.Increment(ref ctrlC);
        var ui = new FakeConsoleUi { IsInteractive = false };
        var showing = Show(ViewMode.Owner, NewView(ui));
        await UntilWindows(1);
        await Wait.UntilAsync(() => _ctrlC.Watchers == 1);

        _ctrlC.Press();
        await Wait.UntilAsync(() => ctrlC == 1);
        _ctrlC.Press();
        await Wait.UntilAsync(() => ctrlC == 2);
        var interactive = _recorder.Server.AnyInteractive;
        var ending = _recorder.Server.EndAsync(Transcribed() with { Kind = OutcomeKind.Cancelled, ExitCode = 130, Message = "Cancelled; the audio is kept" },
            open: true, TimeSpan.FromSeconds(2));
        var result = await showing.WaitAsync(TestWindow.Patience);
        await ending.WaitAsync(TestWindow.Patience);

        Assert.That(interactive, Is.False, "the window said nobody types into it");
        Assert.That(ui.KeyReads, Is.Zero, "no key was read");
        Assert.That(result.ExitCode, Is.EqualTo(130));
    }

    /// <summary>
    /// The Ctrl+C that the console hands over as a key while one is read is not passed on: the watch passes on each press
    /// already, and a press counted twice would stop the recording at once.
    /// </summary>
    [Test]
    public async Task A_Ctrl_C_read_as_a_key_is_not_passed_on_a_second_time()
    {
        var ctrlC = 0;
        _recorder.Server.CtrlCPressed += () => Interlocked.Increment(ref ctrlC);
        var showing = Show(ViewMode.Viewer);
        await UntilWindows(1);

        _ui.Type('\u0003', 'q');
        await NextKeyAsync();
        var fromTheKey = Volatile.Read(ref ctrlC);
        _ctrlC.Press();
        await Wait.UntilAsync(() => ctrlC == 1);
        _ui.Type('\u001b');
        await showing.WaitAsync(TestWindow.Patience);

        Assert.That(fromTheKey, Is.Zero);
        Assert.That(ctrlC, Is.EqualTo(1));
    }

    /// <summary>
    /// The title and the taskbar follow every state, as the run's own window would show them: green while recording, red with
    /// "!" for an Alert; the window flashes once for each new Alert, not for each state that repeats one; at the end the
    /// title says how the run ended.
    /// </summary>
    [Test]
    public async Task Titles_the_taskbar_and_flashes_follow_the_states()
    {
        _recorder.Server.SetState(State(42));
        var showing = Show(ViewMode.Viewer);
        await Wait.UntilAsync(() => _window.Title == "REC 00:00:42 Kalam | ClassScribe");
        var recording = _window.Taskbar;

        _recorder.Server.SetState(Alerting(1));
        await Wait.UntilAsync(() => _window.Flashes == 1);
        var alertTitle = _window.Title;
        var alertTaskbar = _window.Taskbar;
        var shown = _window.Titles.Count;
        _recorder.Server.SetState(Alerting(1));
        await Wait.UntilAsync(() => _window.Titles.Count > shown);
        var flashesForTheSameAlert = _window.Flashes;
        _recorder.Server.SetState(Alerting(2));
        await Wait.UntilAsync(() => _window.Flashes == 2);

        await _recorder.Server.EndAsync(Transcribed(), open: false, TimeSpan.FromSeconds(2));
        await showing.WaitAsync(TestWindow.Patience);

        Assert.That(recording, Is.EqualTo((TaskbarState.Normal, 100)));
        Assert.That(alertTitle, Is.EqualTo("! NO AUDIO from \"chrome\" Kalam | ClassScribe"));
        Assert.That(alertTaskbar, Is.EqualTo((TaskbarState.Error, 100)));
        Assert.That(flashesForTheSameAlert, Is.EqualTo(1), "no flash for an Alert repeated");
        Assert.That(_window.Title, Is.EqualTo("DONE Kalam | ClassScribe"));
        Assert.That(_window.Taskbar, Is.EqualTo((TaskbarState.None, 0)));
        Assert.That(_window.Flashes, Is.EqualTo(2));
    }

    /// <summary>
    /// The end is shown with its message, and gives the run's exit code; the results open only in the window the recorder
    /// told to open them, as the settings say: the window that started the run, unless the run was started with --no-open.
    /// </summary>
    [TestCase(ViewMode.Owner, true, true)]
    [TestCase(ViewMode.Owner, false, false)]
    public async Task The_end_gives_the_exit_code_and_opens_the_results_only_when_told(ViewMode mode, bool open, bool opened)
    {
        var showing = Show(mode);
        await UntilWindows(1);

        await _recorder.Server.EndAsync(Transcribed(), open, TimeSpan.FromSeconds(2));
        var result = await showing.WaitAsync(TestWindow.Patience);

        Assert.That(result, Is.EqualTo(new ViewResult(ViewEnd.RunEnded, 0, Transcribed())));
        Assert.That(Count(@"Transcript saved to D:\Classes\Kalam\Kalam_2026-10-15_23-00-02Z.md"), Is.EqualTo(1));
        _desktop.Verify(desktop => desktop.OpenFolder(@"D:\Classes\Kalam"), opened ? Times.Once() : Times.Never());
        _desktop.Verify(desktop => desktop.OpenFile(@"D:\Classes\Kalam\Kalam_2026-10-15_23-00-02Z.md"), opened ? Times.Once() : Times.Never());
    }

    /// <summary>
    /// With the window that started the run and another one showing it, only the one that started it opens the results; the
    /// other shows the end and opens nothing.
    /// </summary>
    [Test]
    public async Task Only_the_window_that_started_the_run_opens_the_results()
    {
        var viewerDesktop = new Mock<IDesktop>();
        var viewerUi = new FakeConsoleUi();
        var owner = Show(ViewMode.Owner);
        await UntilWindows(1);
        var viewer = Show(ViewMode.Viewer, NewView(viewerUi, viewerDesktop.Object));
        await UntilWindows(2);

        await _recorder.Server.EndAsync(Transcribed(), open: true, TimeSpan.FromSeconds(2));
        var results = await Task.WhenAll(owner, viewer).WaitAsync(TestWindow.Patience);

        Assert.That(results.Select(result => result.ExitCode), Is.EqualTo(new int?[] { 0, 0 }));
        _desktop.Verify(desktop => desktop.OpenFile(It.IsAny<string>()), Times.Once());
        viewerDesktop.VerifyNoOtherCalls();
        Assert.That(Count("Transcript saved to", viewerUi), Is.EqualTo(1), "the other window shows the end too");
    }

    /// <summary>A window the recorder opened by itself never opens the results, even when a recorder tells it to.</summary>
    [Test]
    public async Task A_window_opened_by_itself_never_opens_the_results()
    {
        _recorder.Answer = async byHand =>
        {
            await byHand.HelloAsync();
            await byHand.SendAsync(LinkTypes.Welcome, new Welcome(LinkProtocol.Version, LinkProtocol.Build, _run.ExePath, _run, State(60), 1), LinkJson.Default.Welcome);
            await byHand.SendAsync(LinkTypes.End, new End(Transcribed(), Open: true), LinkJson.Default.End);
        };

        var result = await Show(ViewMode.Alert).WaitAsync(TestWindow.Patience);

        Assert.That(result.End, Is.EqualTo(ViewEnd.RunEnded));
        _desktop.VerifyNoOtherCalls();
    }

    /// <summary>
    /// The results open as the settings say: a transcript's folder and the transcript each when its setting is on, and a
    /// recording kept without a transcript, its folder alone.
    /// </summary>
    [TestCase(OutcomeKind.Transcribed, true, false, true, false)]
    [TestCase(OutcomeKind.Transcribed, false, true, false, true)]
    [TestCase(OutcomeKind.Recorded, true, true, true, false)]
    [TestCase(OutcomeKind.TranscriptFailed, true, true, false, false)]
    public async Task The_results_open_as_the_settings_say(OutcomeKind kind, bool folderSetting, bool transcriptSetting, bool folderOpened, bool transcriptOpened)
    {
        _settings.Set(new RecordingOptions { OpenFolderWhenDone = folderSetting, OpenTranscriptWhenDone = transcriptSetting });
        var outcome = kind switch
        {
            OutcomeKind.Transcribed => Transcribed(),
            OutcomeKind.Recorded => new RunOutcome(kind, 0, @"The recording is in D:\Classes\Kalam", null, @"D:\Classes\Kalam", null),
            _ => new RunOutcome(kind, 1, "The transcript failed; the audio is kept", null, @"D:\Classes\Kalam", @"ClassScribe transcribe ""D:\Classes\Kalam""")
        };
        var showing = Show(ViewMode.Owner);
        await UntilWindows(1);

        await _recorder.Server.EndAsync(outcome, open: true, TimeSpan.FromSeconds(2));
        await showing.WaitAsync(TestWindow.Patience);

        _desktop.Verify(desktop => desktop.OpenFolder(@"D:\Classes\Kalam"), folderOpened ? Times.Once() : Times.Never());
        _desktop.Verify(desktop => desktop.OpenFile(It.IsAny<string>()), transcriptOpened ? Times.Once() : Times.Never());
    }

    /// <summary>
    /// Each end gives its exit code and its title: a saved transcript 0 and DONE, a failed transcript 1 and FAILED with the
    /// command that tries again, a cancelled one 130 and CANCELLED.
    /// </summary>
    [TestCase(OutcomeKind.Transcribed, 0, "DONE Kalam | ClassScribe")]
    [TestCase(OutcomeKind.TranscriptFailed, 1, "! FAILED Kalam | ClassScribe")]
    [TestCase(OutcomeKind.Cancelled, 130, "CANCELLED Kalam | ClassScribe")]
    public async Task Each_end_gives_its_exit_code_and_title(OutcomeKind kind, int exitCode, string title)
    {
        var outcome = new RunOutcome(kind, exitCode, $"The run ended: {kind}", null, @"D:\Classes\Kalam",
            kind == OutcomeKind.TranscriptFailed ? @"ClassScribe transcribe ""D:\Classes\Kalam""" : null);
        var showing = Show(ViewMode.Viewer);
        await UntilWindows(1);

        await _recorder.Server.EndAsync(outcome, open: false, TimeSpan.FromSeconds(2));
        var result = await showing.WaitAsync(TestWindow.Patience);

        Assert.That(result, Is.EqualTo(new ViewResult(ViewEnd.RunEnded, exitCode, outcome)));
        Assert.That(_window.Title, Is.EqualTo(title));
        Assert.That(Count($"The run ended: {kind}"), Is.EqualTo(1));
        if (outcome.RetryCommand is { } retry) Assert.That(Count($"To try again, run: {retry}"), Is.EqualTo(1));
    }

    /// <summary>
    /// A recorder that ends without the run's end, as when it is killed, makes the view end as a lost recorder, for the
    /// window to act on, and the window says so in red with a flash.
    /// </summary>
    [Test]
    public async Task A_lost_recorder_ends_the_view_as_lost()
    {
        _recorder.Server.SetState(State(42));
        var showing = Show(ViewMode.Owner);
        await Wait.UntilAsync(() => _window.Title == "REC 00:00:42 Kalam | ClassScribe");

        _processes.End(_run.ProcessId);
        _recorder.BreakLast();
        var result = await showing.WaitAsync(TestWindow.Patience);

        Assert.That(result, Is.EqualTo(new ViewResult(ViewEnd.RecorderLost, null, null)));
        Assert.That(_window.Title, Is.EqualTo("! ENDED UNEXPECTEDLY Kalam | ClassScribe"));
        Assert.That(_window.Taskbar?.State, Is.EqualTo(TaskbarState.Error));
        Assert.That(_window.Flashes, Is.EqualTo(1));
    }

    /// <summary>A recorder whose process is gone before the window connects is lost as well.</summary>
    [Test]
    public async Task A_recorder_gone_before_the_window_connects_is_lost()
    {
        _processes.End(_run.ProcessId);

        var result = await Show(ViewMode.Viewer).WaitAsync(TestWindow.Patience);

        Assert.That(result, Is.EqualTo(new ViewResult(ViewEnd.RecorderLost, null, null)));
    }

    /// <summary>
    /// Ten seconds without a state while the recorder's process runs: the window says the recorder is not answering, which
    /// process it is and when it started, and offers W to keep waiting and S to stop it through its stop event. A state
    /// that comes afterwards ends the silence.
    /// </summary>
    [Test]
    public async Task Ten_seconds_of_silence_offer_W_and_S()
    {
        var showing = Show(ViewMode.Owner);
        await UntilShown("What happened so far:");
        await Wait.UntilAsync(() => _time.ActiveTimers == 1);

        _time.Advance(RecorderClient.SilenceLimit);
        var silence = $"The recorder is not answering (process {TestRuns.ProcessId}, started {Local(TestRuns.ProcessStart, "HH:mm")}). Its audio so far is on disk. W keeps waiting, S stops it.";
        await UntilShown(silence);
        _ui.Type('w');
        await UntilShown("Waiting for the recorder to answer.");
        _ui.Type('s');
        await UntilShown("Asked the recording to stop; its audio so far is kept.");
        _recorder.Server.SetState(State(30));
        await UntilShown("The recorder is answering again.");
        _ui.Type('s');
        _ui.Type('\u001b');
        await showing.WaitAsync(TestWindow.Patience);

        Assert.That(_stops.Asked, Is.EqualTo(new[] { _run.RunId }), "S stops only while the recorder does not answer");
        Assert.That(_recorder.Server.Keys.TryRead(out _), Is.False, "W and S are the window's own keys");
    }

    /// <summary>In a window nobody types into, the silence is reported without offering keys that cannot be pressed.</summary>
    [Test]
    public async Task A_silence_in_a_window_nobody_types_into_offers_no_keys()
    {
        var ui = new FakeConsoleUi { IsInteractive = false };
        var showing = Show(ViewMode.Owner, NewView(ui));
        await UntilShown("What happened so far:", ui);
        await Wait.UntilAsync(() => _time.ActiveTimers == 1);

        _time.Advance(RecorderClient.SilenceLimit);
        await UntilShown($"The recorder is not answering (process {TestRuns.ProcessId}, started {Local(TestRuns.ProcessStart, "HH:mm")}). Its audio so far is on disk; waiting for it.", ui);
        await _recorder.Server.EndAsync(Transcribed(), open: false, TimeSpan.FromSeconds(2));
        await showing.WaitAsync(TestWindow.Patience);

        Assert.That(Count("W keeps waiting", ui), Is.Zero);
        Assert.That(Count("Esc leaves", ui), Is.Zero, "nor Esc, at the start either");
    }

    /// <summary>
    /// S, when no recorder listens on the run's stop event, as when it has just ended, says that nothing could be asked to
    /// stop rather than that the recording was asked to.
    /// </summary>
    [Test]
    public async Task S_with_no_recorder_listening_says_that_nothing_was_asked_to_stop()
    {
        _stops.Listening = false;
        _recorder.Answering = false;
        var showing = Show(ViewMode.Viewer);
        await _time.AdvanceUntilAsync(() => _ui.Screen.Contains("Esc leaves it running.", StringComparison.Ordinal), LongStep, atMost: TimeSpan.FromSeconds(25));

        _ui.Type('s');
        var result = await showing.WaitAsync(TestWindow.Patience);

        Assert.That(result.End, Is.EqualTo(ViewEnd.NotAnswering));
        Assert.That(_stops.Asked, Is.EqualTo(new[] { _run.RunId }));
        Assert.That(Count("WARNING  Could not ask the recording to stop: no recorder listens for it, so it may have ended already."), Is.EqualTo(1));
        Assert.That(Count("Asked the recording to stop"), Is.Zero);
    }

    /// <summary>
    /// A recorder started by another build, which refuses this window, is named by its exe, which can show the run, and S
    /// stops the recording through its stop event, which does not need the link.
    /// </summary>
    [Test]
    public async Task A_recorder_of_another_build_is_named_and_S_stops_it()
    {
        _recorder.Answer = async byHand =>
        {
            await byHand.HelloAsync();
            await byHand.SendAsync(LinkTypes.Refused, new Refused("Protocol 2 against 1", @"C:\Old\ClassScribe.exe"), LinkJson.Default.Refused);
        };

        var showing = Show(ViewMode.Viewer);
        await UntilShown(@"This recording was started by another build of ClassScribe (C:\Old\ClassScribe.exe); start that one to see it.");
        await UntilShown("S stops the recording (the audio so far is kept); any other key leaves it running.");
        _ui.Type('s');
        var result = await showing.WaitAsync(TestWindow.Patience);

        Assert.That(result, Is.EqualTo(new ViewResult(ViewEnd.Refused, null, null)));
        Assert.That(_stops.Asked, Is.EqualTo(new[] { _run.RunId }));
        Assert.That(Count("Asked the recording to stop; its audio so far is kept."), Is.EqualTo(1));
    }

    /// <summary>Any other key than S leaves a recorder of another build running.</summary>
    [Test]
    public async Task Another_key_leaves_a_recorder_of_another_build_running()
    {
        _recorder.Answer = async byHand =>
        {
            await byHand.HelloAsync();
            await byHand.SendAsync(LinkTypes.Refused, new Refused("Protocol 2 against 1", @"C:\Old\ClassScribe.exe"), LinkJson.Default.Refused);
        };

        var showing = Show(ViewMode.Viewer);
        await UntilShown("any other key leaves it running");
        _ui.Type('\u001b');
        var result = await showing.WaitAsync(TestWindow.Patience);

        Assert.That(result.End, Is.EqualTo(ViewEnd.Refused));
        Assert.That(_stops.Asked, Is.Empty);
    }

    /// <summary>
    /// A link that cannot be made again while the recorder's process runs ends the view as not answering, with the word that
    /// the recording carries on. The window no longer shows the run's state, which would stop changing there.
    /// </summary>
    [Test]
    public async Task A_link_that_cannot_be_made_again_says_the_recording_carries_on()
    {
        _recorder.Server.SetState(State(42));
        var showing = Show(ViewMode.Viewer);
        await UntilShown("What happened so far:");
        await Wait.UntilAsync(() => _window.Title == "REC 00:00:42 Kalam | ClassScribe");

        _recorder.Answering = false;
        _recorder.BreakLast();
        await _time.AdvanceUntilAsync(() => showing.IsCompleted, LongStep, atMost: TimeSpan.FromSeconds(15));
        var result = await showing;

        Assert.That(result, Is.EqualTo(new ViewResult(ViewEnd.NotAnswering, null, null)));
        Assert.That(Count("Lost the connection to the recording; it carries on. Start ClassScribe again to see it."), Is.EqualTo(1));
        Assert.That(_stops.Asked, Is.Empty);
        Assert.That(_window.Title, Is.EqualTo("ClassScribe"), "the title no longer shows a state that stops changing");
        Assert.That(_window.Taskbar, Is.EqualTo((TaskbarState.None, 0)));
    }

    /// <summary>A key pressed while the link is being made again is said not to have reached the recording, so that it can be pressed again.</summary>
    [Test]
    public async Task A_key_pressed_while_the_link_is_made_again_is_said_not_to_have_reached_it()
    {
        var showing = Show(ViewMode.Viewer);
        await UntilShown("What happened so far:");

        _recorder.BreakLast();
        _ui.Type('p');
        await UntilShown("P did not reach the recording: the connection to it is being made again. Press it again in a moment.");
        await _time.AdvanceUntilAsync(() => _recorder.OpenedAt.Count == 2, Step);
        await UntilShown("Reconnected to the recording.");
        _ui.Type('\u001b');
        await showing.WaitAsync(TestWindow.Patience);

        Assert.That(_recorder.Server.Keys.TryRead(out _), Is.False);
    }

    /// <summary>
    /// A line of the log that holds a control character, such as a BEL in a device's name, is shown without it: a BEL
    /// written to the console can ring, and the class recording would take the sound in.
    /// </summary>
    [Test]
    public async Task A_line_holding_a_bell_is_shown_without_it()
    {
        _recorder.Log.Write(LogLevel.Information, "Capturing \"Speakers\a\"\u001b[2J and more");
        var showing = Show(ViewMode.Viewer);
        await UntilShown("and more");
        _ui.Type('\u001b');
        await showing.WaitAsync(TestWindow.Patience);

        Assert.That(_ui.Screen.Contains('\a'), Is.False, "no BEL is written");
        Assert.That(_ui.Screen.Contains('\u001b'), Is.False, "no escape is written");
        Assert.That(Count("Capturing \"Speakers \" [2J and more"), Is.EqualTo(1));
    }

    /// <summary>A recorder of another build that speaks the same protocol shows the run all the same, with a note that names its exe.</summary>
    [Test]
    public async Task A_recorder_of_another_build_with_the_same_protocol_is_shown_with_a_note()
    {
        _recorder.Answer = async byHand =>
        {
            await byHand.HelloAsync();
            await byHand.SendAsync(LinkTypes.Welcome, new Welcome(LinkProtocol.Version, "0.9.0+old", @"C:\Old\ClassScribe.exe", _run, State(60), 1), LinkJson.Default.Welcome);
            await byHand.SendAsync(LinkTypes.End, new End(Transcribed(), Open: false), LinkJson.Default.End);
        };

        var result = await Show(ViewMode.Viewer).WaitAsync(TestWindow.Patience);

        Assert.That(result.End, Is.EqualTo(ViewEnd.RunEnded));
        Assert.That(Count(@"This recording was started by another build of ClassScribe (C:\Old\ClassScribe.exe); it is shown here all the same."), Is.EqualTo(1));
    }

    /// <summary>
    /// A recorder whose pipe does not answer for 20 seconds while its process runs is said not to answer, with every key
    /// that does something then: W, S and Esc, since the line that Esc leaves the view comes only with the run. W waits and
    /// tries again, and the run is shown once the recorder answers.
    /// </summary>
    [Test]
    public async Task A_recorder_that_does_not_answer_at_the_start_is_waited_for_with_W()
    {
        _recorder.Answering = false;
        var showing = Show(ViewMode.Viewer);
        var silence = $"The recorder is not answering (process {TestRuns.ProcessId}, started {Local(TestRuns.ProcessStart, "HH:mm")}). Its audio so far is on disk. W keeps waiting, S stops it, Esc leaves it running.";
        await _time.AdvanceUntilAsync(() => _ui.Screen.Contains(silence, StringComparison.Ordinal), LongStep, atMost: TimeSpan.FromSeconds(25));

        _recorder.Answering = true;
        _ui.Type('w');
        await UntilShown("What happened so far:");
        _ui.Type('\u001b');
        var result = await showing.WaitAsync(TestWindow.Patience);

        Assert.That(result.End, Is.EqualTo(ViewEnd.Left));
        Assert.That(Count("Waiting for the recorder to answer."), Is.EqualTo(1));
        Assert.That(_stops.Asked, Is.Empty);
    }

    /// <summary>
    /// A key typed before the question about a recorder that does not answer at the start appeared, such as while the window
    /// waited for it, does not answer it: S typed early stops nothing.
    /// </summary>
    [Test]
    public async Task A_key_typed_before_the_question_about_a_silent_recorder_does_not_answer_it()
    {
        _recorder.Answering = false;
        _ui.TypeAhead('s');
        var showing = Show(ViewMode.Viewer);
        await _time.AdvanceUntilAsync(() => _ui.Screen.Contains("Esc leaves it running.", StringComparison.Ordinal), LongStep, atMost: TimeSpan.FromSeconds(25));

        _ui.Type('\u001b');
        var result = await showing.WaitAsync(TestWindow.Patience);

        Assert.That(result.End, Is.EqualTo(ViewEnd.NotAnswering));
        Assert.That(_stops.Asked, Is.Empty);
    }

    /// <summary>S, at a recorder whose pipe does not answer at the start, stops it through its stop event, and the view ends as not answering.</summary>
    [Test]
    public async Task S_stops_a_recorder_that_does_not_answer_at_the_start()
    {
        _recorder.Answering = false;
        var showing = Show(ViewMode.Viewer);
        await _time.AdvanceUntilAsync(() => _ui.Screen.Contains("W keeps waiting, S stops it, Esc leaves it running.", StringComparison.Ordinal), LongStep, atMost: TimeSpan.FromSeconds(25));

        _ui.Type('s');
        var result = await showing.WaitAsync(TestWindow.Patience);

        Assert.That(result, Is.EqualTo(new ViewResult(ViewEnd.NotAnswering, null, null)));
        Assert.That(_stops.Asked, Is.EqualTo(new[] { _run.RunId }));
    }

    /// <summary>
    /// Esc, at a recorder whose pipe does not answer at the start, leaves it running: nothing is asked to stop, no other
    /// try is made, and the view ends as not answering. Other keys do nothing there.
    /// </summary>
    [Test]
    public async Task Esc_leaves_a_recorder_that_does_not_answer_at_the_start_running()
    {
        _recorder.Answering = false;
        var showing = Show(ViewMode.Viewer);
        await _time.AdvanceUntilAsync(() => _ui.Screen.Contains("Esc leaves it running.", StringComparison.Ordinal), LongStep, atMost: TimeSpan.FromSeconds(25));
        var tries = _recorder.OpenedAt.Count;

        _ui.Type('x', 'q', '\u0003', '\u001b');
        var result = await showing.WaitAsync(TestWindow.Patience);

        Assert.That(result, Is.EqualTo(new ViewResult(ViewEnd.NotAnswering, null, null)));
        Assert.That(_stops.Asked, Is.Empty);
        Assert.That(_recorder.OpenedAt, Has.Count.EqualTo(tries), "no other try");
    }

    /// <summary>
    /// A recorder that does not answer before it has begun to record, as its run file says, is not said to have audio on
    /// disk: it may have recorded nothing. This holds at the start, and when the recorder falls silent later without having
    /// said that it records.
    /// </summary>
    [Test]
    public async Task A_recorder_that_has_not_begun_to_record_is_not_said_to_have_audio_on_disk()
    {
        var notAnswering = $"The recorder is not answering (process {TestRuns.ProcessId}, started {Local(TestRuns.ProcessStart, "HH:mm")}).";
        _run = _run with { State = RunState.Starting };
        _recorder.Run = _run;
        _recorder.Answering = false;
        var showing = Show(ViewMode.Viewer);
        await _time.AdvanceUntilAsync(() => _ui.Screen.Contains(notAnswering, StringComparison.Ordinal), LongStep, atMost: TimeSpan.FromSeconds(25));
        _recorder.Answering = true;
        _ui.Type('w');
        await UntilShown("What happened so far:");
        await Wait.UntilAsync(() => _time.ActiveTimers == 1);

        _time.Advance(RecorderClient.SilenceLimit);
        await Wait.UntilAsync(() => Count(notAnswering) == 2);
        _ui.Type('\u001b');
        await showing.WaitAsync(TestWindow.Patience);

        Assert.That(Count($"{notAnswering} W keeps waiting, S stops it, Esc leaves it running."), Is.EqualTo(1), "at the start");
        Assert.That(Count($"{notAnswering} W keeps waiting, S stops it."), Is.EqualTo(1), "later");
        Assert.That(Count("on disk"), Is.Zero);
    }

    /// <summary>In a window nobody types into, a recorder that does not answer at the start is reported without keys, and the view ends as not answering.</summary>
    [Test]
    public async Task A_recorder_that_does_not_answer_at_the_start_ends_a_view_nobody_types_into()
    {
        _recorder.Answering = false;
        var ui = new FakeConsoleUi { IsInteractive = false };
        var showing = Show(ViewMode.Owner, NewView(ui));
        await _time.AdvanceUntilAsync(() => showing.IsCompleted, LongStep, atMost: TimeSpan.FromSeconds(25));
        var result = await showing;

        Assert.That(result, Is.EqualTo(new ViewResult(ViewEnd.NotAnswering, null, null)));
        Assert.That(Count($"The recorder is not answering (process {TestRuns.ProcessId}, started {Local(TestRuns.ProcessStart, "HH:mm")}). Its audio so far is on disk.", ui), Is.EqualTo(1));
        Assert.That(Count("waiting for it", ui), Is.Zero, "it does not say it waits when it does not");
    }

    /// <summary>A recorder that runs with other rights than the window, as one started with Run as administrator, is reported with the way to start ClassScribe to see it.</summary>
    [Test]
    public async Task A_recorder_with_other_rights_is_reported_with_the_way_to_see_it()
    {
        _recorder.Refusal = new UnauthorizedAccessException("Access to the path is denied.");

        var result = await Show(ViewMode.Viewer).WaitAsync(TestWindow.Patience);

        Assert.That(result, Is.EqualTo(new ViewResult(ViewEnd.NotAnswering, null, null)));
        Assert.That(Count("Start ClassScribe the same way as the recording"), Is.EqualTo(1));
    }

    /// <summary>
    /// An outcome without a message, as in a run file of an earlier build, is told by how the run ended, so that the end of
    /// a run never shows an empty line.
    /// </summary>
    [TestCase(OutcomeKind.Transcribed, "The transcript is saved.")]
    [TestCase(OutcomeKind.Recorded, "The recording is saved.")]
    [TestCase(OutcomeKind.NothingRecorded, "No audio was captured. Was anything playing?")]
    [TestCase(OutcomeKind.TranscriptFailed, "The transcript failed; the audio is kept.")]
    [TestCase(OutcomeKind.Cancelled, "The transcription was cancelled; the audio is kept.")]
    [TestCase(OutcomeKind.Refused, "The recording did not start: another recording was running.")]
    [TestCase(OutcomeKind.RecorderFailed, "The recorder failed; the audio so far is kept.")]
    public void An_outcome_without_a_message_is_told_by_how_the_run_ended(OutcomeKind kind, string told)
    {
        foreach (var message in new[] { "", "  ", null! })
            Assert.That(RecorderView.OutcomeLines(new RunOutcome(kind, 1, message, null, null, null)), Is.EqualTo(new[] { told }), $"message \"{message}\"");
    }

    /// <summary>
    /// The command that tries again follows the outcome's message on a line of its own, unless the message holds it already:
    /// it is never shown twice.
    /// </summary>
    [Test]
    public void The_command_that_tries_again_is_shown_once()
    {
        const string retry = @"ClassScribe transcribe ""D:\Classes\Kalam""";
        const string failed = "The transcript failed; the audio is kept.";
        var holding = $"{failed} To try again, run: {retry}";

        Assert.That(RecorderView.OutcomeLines(new RunOutcome(OutcomeKind.TranscriptFailed, 1, failed, null, @"D:\Classes\Kalam", retry)),
            Is.EqualTo(new[] { failed, $"To try again, run: {retry}" }));
        Assert.That(RecorderView.OutcomeLines(new RunOutcome(OutcomeKind.TranscriptFailed, 1, holding, null, @"D:\Classes\Kalam", retry)),
            Is.EqualTo(new[] { holding }));
        Assert.That(RecorderView.OutcomeLines(new RunOutcome(OutcomeKind.TranscriptFailed, 1, "", null, @"D:\Classes\Kalam", retry)),
            Is.EqualTo(new[] { failed, $"To try again, run: {retry}" }), "after the wording for a missing message too");
    }

    /// <summary>A run that records in its own window has no recorder to show it from: asking to show it is a mistake of the caller's.</summary>
    [Test]
    public void A_run_recording_in_its_window_cannot_be_shown()
    {
        var inWindow = _run with { Home = RunHome.InWindow, Pipe = null };

        Assert.ThrowsAsync<ArgumentException>(() => NewView().ShowAsync(inWindow, ViewMode.Viewer, _patience.Token));
    }
}

/// <summary>
/// The view's own stop, through the run's real stop event in Windows, as S asks for it at a recorder of another build: a
/// recorder that listens hears it, and with none listening the window says so. Only a named event of this test's run is
/// made and set; nothing is recorded or started.
/// </summary>
[TestFixture, Platform("Win"), Category("WindowsProcess")]
[SupportedOSPlatform("windows")]
public class RecorderViewStopEventTests
{
    private ManualTimeProvider _time = null!;
    private FakeProcessLiveness _processes = null!;
    private RunRecord _run = null!;
    private TestRecorder _recorder = null!;
    private FakeConsoleUi _ui = null!;
    private RecordingLogger<RecorderView> _log = null!;
    private CancellationTokenSource _patience = null!;

    /// <summary>A run of a fresh id, whose recorder refuses the window as one of another build would.</summary>
    [SetUp]
    public void SetUp()
    {
        _time = new ManualTimeProvider(TestRuns.Start);
        _processes = new FakeProcessLiveness();
        var runId = RunIds.New(DateTimeOffset.UtcNow);
        _run = TestRuns.Record(runId, RunState.Recording);
        _processes.Start(_run.ProcessId, _run.ProcessStartedUtc);
        _recorder = new TestRecorder(_time, _run)
        {
            Answer = async byHand =>
            {
                await byHand.HelloAsync();
                await byHand.SendAsync(LinkTypes.Refused, new Refused("Protocol 2 against 1", @"C:\Old\ClassScribe.exe"), LinkJson.Default.Refused);
            }
        };
        _ui = new FakeConsoleUi();
        _log = new RecordingLogger<RecorderView>();
        _patience = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    }

    /// <summary>Stops the recorder.</summary>
    [TearDown]
    public async Task TearDown()
    {
        await _recorder.DisposeAsync();
        _patience.Dispose();
    }

    /// <summary>A view that stops recorders through Windows, as outside the tests.</summary>
    private RecorderView NewView() =>
        new(_ui, new FakeWindowSignals(), Mock.Of<IDesktop>(), _processes, TestOptions.Create(new RecordingOptions()), _time, _log, new RecordingLogger<RecorderClient>())
        {
            OpenPipe = _recorder.OpenPipeAsync,
            WatchCtrlC = new FakeCtrlC().Watch
        };

    /// <summary>S sets the run's stop event, and the recorder that listens on it hears the stop.</summary>
    [Test]
    public async Task S_reaches_a_recorder_that_listens_on_the_stop_event()
    {
        using var heard = new ManualResetEventSlim();
        using var listening = StopSignal.Listen(_run.RunId, heard.Set, new RecordingLogger<StopSignal>());

        var showing = NewView().ShowAsync(_run, ViewMode.Viewer, _patience.Token);
        await Wait.UntilAsync(() => _ui.Screen.Contains("any other key leaves it running", StringComparison.Ordinal));
        _ui.Type('s');
        await showing.WaitAsync(TestWindow.Patience);

        Assert.That(heard.Wait(TestWindow.Patience), Is.True, "the recorder heard the stop");
        Assert.That(_ui.Screen, Does.Contain("Asked the recording to stop; its audio so far is kept."));
    }

    /// <summary>With no recorder listening on the stop event, S says that nothing could be asked to stop.</summary>
    [Test]
    public async Task S_with_no_recorder_listening_says_so()
    {
        var showing = NewView().ShowAsync(_run, ViewMode.Viewer, _patience.Token);
        await Wait.UntilAsync(() => _ui.Screen.Contains("any other key leaves it running", StringComparison.Ordinal));
        _ui.Type('s');
        await showing.WaitAsync(TestWindow.Patience);

        Assert.That(_ui.Screen, Does.Contain("Could not ask the recording to stop: no recorder listens for it, so it may have ended already."));
    }
}
