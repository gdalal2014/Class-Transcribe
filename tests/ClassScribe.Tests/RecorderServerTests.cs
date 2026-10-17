using System.Text.Json;
using ClassScribe.Commands;
using ClassScribe.Link;
using ClassScribe.Recorder;
using Microsoft.Extensions.Logging;

namespace ClassScribe.Tests;

/// <summary>
/// The recorder's end of the link, driven over in-memory connections: each window is welcomed with the run, its state and
/// the log after the last line it has, with nothing lost or shown twice while lines keep coming; four windows at once; a
/// window that falls behind is let go and catches up when it comes back; states wait coalesced; keys reach the recording
/// loop and Ctrl+C its event; a window of another protocol is refused; the end reaches every window, and only one of them
/// opens the results.
/// </summary>
[TestFixture]
public class RecorderServerTests
{
    private ManualTimeProvider _time = null!;
    private RecorderLog _log = null!;
    private RecordingLogger<RecorderServer> _logger = null!;
    private RecorderServer _server = null!;
    private RunRecord _run = null!;
    private List<TestWindow> _windows = null!;

    /// <summary>A server for a run recording "Kalam", on a clock moved by hand, with an empty log and no window yet.</summary>
    [SetUp]
    public void SetUp()
    {
        _time = new ManualTimeProvider(TestRuns.Start);
        _log = new RecorderLog(_time);
        _logger = new RecordingLogger<RecorderServer>();
        _server = new RecorderServer(_log, _time, _logger);
        _run = TestRuns.Record(TestRuns.Id(), RunState.Recording);
        _server.Begin(() => _run);
        _windows = [];
    }

    /// <summary>Closes every window, stops the server and disposes the log.</summary>
    [TearDown]
    public async Task TearDown()
    {
        foreach (var window in _windows) await window.DisposeAsync();
        await _server.DisposeAsync();
        _log.Dispose();
    }

    /// <summary>Connects a window that says <paramref name="hello"/> (none when null), closed when the test ends.</summary>
    private async Task<TestWindow> ConnectAsync(Hello? hello, int capacity = DuplexStreamPair.PipeBuffer)
    {
        var window = await TestWindow.ConnectAsync(_server, hello, capacity);
        _windows.Add(window);
        return window;
    }

    /// <summary>Attaches a window that says <paramref name="hello"/> (this build's, from the start, unless given) and reads its welcome; closed when the test ends.</summary>
    private async Task<(TestWindow Window, Welcome Welcome)> AttachAsync(Hello? hello = null, int capacity = DuplexStreamPair.PipeBuffer)
    {
        var attached = await TestWindow.AttachAsync(_server, hello, capacity);
        _windows.Add(attached.Window);
        return attached;
    }

    /// <summary>Writes the lines "line &lt;from&gt;" to "line &lt;to&gt;" into the log; the first line of the log is numbered 1.</summary>
    private void WriteLines(int to, int from = 1)
    {
        for (var n = from; n <= to; n++) _log.Write(LogLevel.Information, $"line {n}");
    }

    /// <summary>A state of the run recording "Kalam" at <paramref name="seconds"/> into the recording.</summary>
    private static RecorderState State(int seconds = 60, RecorderPhase phase = RecorderPhase.Recording) =>
        new(phase, "Kalam", TimeSpan.FromSeconds(seconds), "Speakers", null, null, null, null, Attention.None, InWindow: false);

    /// <summary>How the tests' run ends: transcribed.</summary>
    private static RunOutcome Outcome() =>
        new(OutcomeKind.Transcribed, 0, "Saved the transcript", @"D:\Classes\Kalam\Kalam_2026-10-15_23-00-02Z.md", @"D:\Classes\Kalam", null);

    /// <summary>The next key the server passes to the recording loop.</summary>
    private async Task<ConsoleKeyInfo> NextKeyAsync() => await _server.Keys.ReadAsync().AsTask().WaitAsync(TestWindow.Patience);

    /// <summary>The numbers of the lines.</summary>
    private static long[] Numbers(IEnumerable<LogLine> lines) => [.. lines.Select(line => line.Seq)];

    /// <summary>The numbers from <paramref name="from"/> to <paramref name="to"/>.</summary>
    private static long[] Range(long from, long to) => [.. Enumerable.Range((int)from, (int)(to - from + 1)).Select(n => (long)n)];

    /// <summary>A window is welcomed with the run, its state now, this build and the oldest line held, then shown the log so far.</summary>
    [Test]
    public async Task A_window_is_welcomed_with_the_run_its_state_and_the_log_so_far()
    {
        WriteLines(3);
        var state = State();
        _server.SetState(state);

        var (window, welcome) = await AttachAsync();
        var lines = await window.LinesUpToAsync(3);

        Assert.That(welcome, Is.EqualTo(new Welcome(LinkProtocol.Version, LinkProtocol.Build, _run.ExePath, _run, state, OldestSeq: 1)));
        Assert.That(lines.Select(line => line.Text), Is.EqualTo(new[] { "line 1", "line 2", "line 3" }));
        Assert.That(Numbers(lines), Is.EqualTo(Range(1, 3)));
        Assert.That(lines[0].Level, Is.EqualTo(LogLevel.Information));
        Assert.That(lines[0].Utc, Is.EqualTo(TestRuns.Start));
    }

    /// <summary>Before the run has published a state, a window is welcomed with the run starting.</summary>
    [Test]
    public async Task Before_any_state_a_window_is_welcomed_with_the_run_starting()
    {
        var (_, welcome) = await AttachAsync();

        Assert.That(welcome.State, Is.EqualTo(new RecorderState(RecorderPhase.Starting, "Kalam", TimeSpan.Zero, null, null, null, null, null, Attention.None, InWindow: false)));
    }

    /// <summary>The welcome holds the run as it is when the window comes, not as it was when the server started.</summary>
    [Test]
    public async Task The_welcome_holds_the_run_as_it_is_when_the_window_comes()
    {
        _run = _run with { State = RunState.Transcribing, BaseName = "Kalam_2026-10-15_23-00-02Z", Folder = @"D:\Classes\Kalam" };

        var (_, welcome) = await AttachAsync();

        Assert.That(welcome.Run, Is.EqualTo(_run));
    }

    /// <summary>A window is shown only the lines after the last one it has, then the lines that come after it.</summary>
    [Test]
    public async Task A_window_is_shown_only_the_lines_after_the_last_one_it_has()
    {
        WriteLines(5);

        var (window, _) = await AttachAsync(TestWindow.Hello(since: 3));
        var replayed = await window.LinesUpToAsync(5);
        WriteLines(6, from: 6);
        var live = await window.LinesUpToAsync(6);

        Assert.That(Numbers(replayed), Is.EqualTo(Range(4, 5)));
        Assert.That(Numbers(live), Is.EqualTo(Range(6, 6)));
    }

    /// <summary>The welcome names the oldest line still held, once the log has let earlier lines go, and the window is shown the lines it lacks.</summary>
    [Test]
    public async Task The_welcome_names_the_oldest_line_still_held()
    {
        WriteLines(RecorderLog.Capacity + 5);

        var (window, welcome) = await AttachAsync(TestWindow.Hello(since: RecorderLog.Capacity + 3));
        var lines = await window.LinesUpToAsync(RecorderLog.Capacity + 5);

        Assert.That(welcome.OldestSeq, Is.EqualTo(6));
        Assert.That(Numbers(lines), Is.EqualTo(Range(RecorderLog.Capacity + 4, RecorderLog.Capacity + 5)));
    }

    /// <summary>
    /// A window that speaks another protocol is refused, told which ClassScribe.exe records the run, and let go without
    /// being shown anything; it never counts as attached.
    /// </summary>
    [Test]
    public async Task A_window_of_another_protocol_is_refused_and_told_which_exe_records_the_run()
    {
        var changes = 0;
        _server.WindowsChanged += (_, _) => Interlocked.Increment(ref changes);

        var window = await ConnectAsync(TestWindow.Hello() with { Protocol = LinkProtocol.Version + 1 });
        var refused = await window.NextAsync(LinkTypes.Refused, LinkJson.Default.Refused);

        Assert.That(refused.ExePath, Is.EqualTo(_run.ExePath));
        Assert.That(refused.Reason, Does.Contain($"protocol {LinkProtocol.Version}").And.Contain($"{LinkProtocol.Version + 1}"));
        Assert.That(await window.NextAsync(), Is.Null);
        await window.GoneAsync();
        Assert.That(_server.Windows, Is.Zero);
        Assert.That(Volatile.Read(ref changes), Is.Zero);
    }

    /// <summary>A window of another protocol is refused even when the rest of its hello is not one this build can read: only the protocol's number counts.</summary>
    [Test]
    public async Task A_window_of_another_protocol_is_refused_whatever_else_its_hello_holds()
    {
        var window = await ConnectAsync(null);
        await window.SendRawAsync("{\"type\":\"hello\",\"body\":{\"protocol\":7,\"greeting\":{\"words\":[\"salaam\"]}}}\n");

        var refused = await window.NextAsync(LinkTypes.Refused, LinkJson.Default.Refused);

        Assert.That(refused.ExePath, Is.EqualTo(_run.ExePath));
        Assert.That(await window.NextAsync(), Is.Null);
    }

    /// <summary>A hello in this protocol that cannot be read, or a first message that is not a hello, gets nothing: the window is let go, and a key it sent goes nowhere.</summary>
    [TestCase("{\"type\":\"hello\",\"body\":{\"protocol\":1}}\n")]
    [TestCase("{\"type\":\"hello\"}\n")]
    [TestCase("{\"type\":\"key\",\"body\":{\"key\":\"Q\"}}\n")]
    [TestCase("{\"type\":\"bye\",\"body\":{\"reason\":\"Esc\"}}\n")]
    public async Task A_window_that_does_not_begin_with_a_readable_hello_is_let_go(string first)
    {
        var window = await ConnectAsync(null);
        await window.SendRawAsync(first);

        Assert.That(await window.NextAsync(), Is.Null);
        await window.GoneAsync();
        Assert.That(_server.Keys.TryRead(out _), Is.False);
        Assert.That(_server.Windows, Is.Zero);
    }

    /// <summary>A window that connects and says nothing is let go after 10 seconds, so it cannot hold a place for good.</summary>
    [Test]
    public async Task A_window_that_says_nothing_is_let_go_after_10_seconds()
    {
        var window = await ConnectAsync(null);
        await Wait.UntilAsync(() => _time.ActiveTimers == 1);

        _time.Advance(RecorderServer.HelloPatience - TimeSpan.FromMilliseconds(1));
        await Task.Delay(50);
        Assert.That(window.Served.IsCompleted, Is.False, "let go too early");

        _time.Advance(TimeSpan.FromMilliseconds(1));
        await window.GoneAsync();
        Assert.That(await window.NextAsync(), Is.Null);
    }

    /// <summary>
    /// Windows that come while lines keep coming are each shown every line after the one they had, once and in order:
    /// the replay and the live lines meet without a gap or a line shown twice.
    /// </summary>
    [Test]
    public async Task Replay_loses_and_doubles_nothing_while_lines_keep_coming()
    {
        const int total = 3_000;
        WriteLines(100);
        var writing = Task.Run(() =>
        {
            for (var n = 101; n <= total; n++)
            {
                _log.Write(LogLevel.Information, $"line {n}");
                if (n % 25 == 0) Thread.Sleep(1);
            }
        });

        List<(long Since, Task<List<LogLine>> Lines)> windows = [];
        foreach (var since in new long[] { 0, 40, 90, 0, 100 })
        {
            var (window, _) = await AttachAsync(TestWindow.Hello(since: since));
            windows.Add((since, window.LinesUpToAsync(total)));
            await Task.Delay(2);
        }

        await writing;
        foreach (var (since, lines) in windows)
            Assert.That(Numbers(await lines), Is.EqualTo(Range(since + 1, total)), $"the window that had line {since}");
    }

    /// <summary>
    /// A line the log hands on only after a window was given it in its replay, as when the log's handing on is held up, is
    /// not sent to that window a second time; the lines after the replay still come.
    /// </summary>
    [Test]
    public async Task A_line_handed_on_after_the_replay_is_not_sent_twice()
    {
        using var release = new ManualResetEventSlim();
        using var holding = new ManualResetEventSlim();
        _log.Written += line =>
        {
            if (line.Seq != 1) return;

            holding.Set();
            release.Wait(TestWindow.Patience);
        };

        try
        {
            WriteLines(1);
            Assert.That(holding.Wait(TestWindow.Patience), Is.True, "the log began to hand line 1 on");
            WriteLines(5, from: 2);

            var (window, _) = await AttachAsync();
            var replayed = await window.LinesUpToAsync(5);
            release.Set();
            WriteLines(6, from: 6);
            var live = await window.LinesUpToAsync(6);

            Assert.That(Numbers(replayed.Concat(live)), Is.EqualTo(Range(1, 6)));
        }
        finally
        {
            release.Set();
        }
    }

    /// <summary>
    /// A line written just after a window's replay was taken, before the window is among those shown the run, still
    /// reaches it, once: the replay is taken and the window added under the lock the following of the log takes, so the
    /// line waits for the window to be added instead of passing it by. The test writes the line from inside the server at
    /// that very moment, and gives the log a quarter of a second to hand it on, which it cannot do until the window is in.
    /// </summary>
    [Test]
    public async Task A_line_written_as_the_replay_is_taken_reaches_the_window_once()
    {
        WriteLines(3);
        bool? handedOnAtOnce = null;
        await using var server = new RecorderServer(_log, _time, _logger)
        {
            ReplayTaken = () =>
            {
                _log.Write(LogLevel.Information, "line 4");
                handedOnAtOnce = _log.WaitForFollowers(TimeSpan.FromMilliseconds(250));
            }
        };
        server.Begin(() => _run);

        var (window, _) = await TestWindow.AttachAsync(server);
        _windows.Add(window);
        WriteLines(5, from: 5);
        var lines = await window.LinesUpToAsync(5);

        Assert.That(Numbers(lines), Is.EqualTo(Range(1, 5)));
        Assert.That(lines[3].Text, Is.EqualTo("line 4"));
        Assert.That(handedOnAtOnce, Is.False, "the log could not hand the line on before the window was added");
    }

    /// <summary>Four windows are shown the run at once: each is shown every line, the state and the end.</summary>
    [Test]
    public async Task Four_windows_are_shown_the_run_at_once()
    {
        List<TestWindow> windows = [];
        for (var n = 0; n < LinkProtocol.MaxWindows; n++) windows.Add((await AttachAsync()).Window);
        Assert.That(_server.Windows, Is.EqualTo(4));

        WriteLines(5);
        foreach (var window in windows) Assert.That(Numbers(await window.LinesUpToAsync(5)), Is.EqualTo(Range(1, 5)));
        _server.SetState(State(90));
        foreach (var window in windows) Assert.That((await window.NextAsync(LinkTypes.State, LinkJson.Default.StateUpdate)).State, Is.EqualTo(State(90)));

        var ending = _server.EndAsync(Outcome(), open: false, TimeSpan.FromSeconds(2));
        foreach (var window in windows)
        {
            var (_, end) = await window.UntilEndAsync();
            Assert.That(end.Outcome, Is.EqualTo(Outcome()));
            await window.DisposeAsync();
        }

        await ending.WaitAsync(TestWindow.Patience);
    }

    /// <summary>
    /// A window that stops reading, as a classic console frozen by a text selection does, is let go once 2,000 messages
    /// wait for it, while a window that reads carries on; when it comes back with the last line it had, it is shown every
    /// line after it, so nothing is lost. The lines come a hundred at a time, as a run logs them, never faster than the
    /// window that reads can take them.
    /// </summary>
    [Test]
    public async Task A_window_that_stops_reading_is_let_go_and_catches_up_when_it_comes_back()
    {
        const int total = RecorderServer.QueueCapacity + 500;
        var (slow, _) = await AttachAsync(capacity: 1024);
        var (steady, _) = await AttachAsync();
        var steadyLines = steady.LinesUpToAsync(total);

        for (var to = 100; to <= total; to += 100)
        {
            WriteLines(to, from: to - 99);
            await Wait.UntilAsync(() => steady.LastSeq >= to || steadyLines.IsCompleted);
        }

        await slow.GoneAsync();
        var held = (await slow.RestAsync()).Where(message => message.Type == LinkTypes.Log).Select(message => message.Body.Deserialize(LinkJson.Default.LogLine)!).ToList();
        var lastHeld = held.Count == 0 ? 0 : held[^1].Seq;
        var (back, _) = await AttachAsync(TestWindow.Hello(since: lastHeld));
        var caughtUp = await back.LinesUpToAsync(total);

        Assert.That(lastHeld, Is.LessThan(total), "the slow window was let go before it had every line");
        Assert.That(Numbers(held.Concat(caughtUp)), Is.EqualTo(Range(1, total)));
        Assert.That(Numbers(await steadyLines), Is.EqualTo(Range(1, total)));
        Assert.That(_logger.Messages.Count(message => message.Contains("fell", StringComparison.Ordinal)), Is.EqualTo(1));
    }

    /// <summary>States wait coalesced: a window that cannot take them as fast as they come is sent only the latest.</summary>
    [Test]
    public async Task Only_the_latest_state_waits_for_a_window_that_is_behind()
    {
        var window = await ConnectAsync(TestWindow.Hello(), capacity: 16);
        await Wait.UntilAsync(() => _server.Windows == 1);

        for (var seconds = 1; seconds <= 100; seconds++) _server.SetState(State(seconds));
        await window.NextAsync(LinkTypes.Welcome, LinkJson.Default.Welcome);
        var state = await window.NextAsync(LinkTypes.State, LinkJson.Default.StateUpdate);
        WriteLines(1);

        Assert.That(state.State, Is.EqualTo(State(100)));
        Assert.That((await window.NextAsync())?.Type, Is.EqualTo(LinkTypes.Log), "no earlier state was still waiting");
    }

    /// <summary>A state equal to the last one is passed on all the same: a window takes states as the sign that the recorder is answering.</summary>
    [Test]
    public async Task A_state_equal_to_the_last_is_passed_on_all_the_same()
    {
        var (window, _) = await AttachAsync();

        _server.SetState(State(30, RecorderPhase.Paused));
        var first = await window.NextAsync(LinkTypes.State, LinkJson.Default.StateUpdate);
        _server.SetState(State(30, RecorderPhase.Paused));
        var second = await window.NextAsync(LinkTypes.State, LinkJson.Default.StateUpdate);

        Assert.That(second, Is.EqualTo(first));
    }

    /// <summary>Q, P, M and V from a window reach the recording loop as those keys, in order, without modifiers; keys from a second window reach it too.</summary>
    [Test]
    public async Task Keys_from_the_windows_reach_the_recording_loop()
    {
        var (window, _) = await AttachAsync();
        var (other, _) = await AttachAsync();

        foreach (var key in new[] { LinkKeys.Q, LinkKeys.P, LinkKeys.M, LinkKeys.V }) await window.SendKeyAsync(key);
        List<ConsoleKeyInfo> keys = [];
        for (var n = 0; n < 4; n++) keys.Add(await NextKeyAsync());
        await other.SendKeyAsync(LinkKeys.Q);
        var fromOther = await NextKeyAsync();

        Assert.That(keys.Select(key => key.Key), Is.EqualTo(new[] { ConsoleKey.Q, ConsoleKey.P, ConsoleKey.M, ConsoleKey.V }));
        Assert.That(keys.Select(key => key.KeyChar), Is.EqualTo(new[] { 'q', 'p', 'm', 'v' }));
        Assert.That(keys.Select(key => key.Modifiers), Is.All.EqualTo((ConsoleModifiers)0));
        Assert.That(fromOther.Key, Is.EqualTo(ConsoleKey.Q));
    }

    /// <summary>Ctrl+C from a window raises the server's event, once per press, and is not passed on as a key.</summary>
    [Test]
    public async Task Ctrl_C_from_a_window_raises_its_event_and_is_not_a_key()
    {
        var pressed = 0;
        _server.CtrlCPressed += () => Interlocked.Increment(ref pressed);
        var (window, _) = await AttachAsync();

        await window.SendKeyAsync(LinkKeys.CtrlC);
        await window.SendKeyAsync(LinkKeys.CtrlC);
        await window.SendKeyAsync(LinkKeys.P);
        var next = await NextKeyAsync();

        Assert.That(Volatile.Read(ref pressed), Is.EqualTo(2));
        Assert.That(next.Key, Is.EqualTo(ConsoleKey.P));
    }

    /// <summary>A key the link does not know, or a key message without a key, is passed over, and the window stays.</summary>
    [Test]
    public async Task A_key_the_link_does_not_know_is_passed_over()
    {
        var (window, _) = await AttachAsync();

        await window.SendKeyAsync("Enter");
        await window.SendKeyAsync("q");
        await window.SendRawAsync("{\"type\":\"key\",\"body\":{}}\n{\"type\":\"key\",\"body\":{\"key\":null}}\n{\"type\":\"key\"}\n");
        await window.SendKeyAsync(LinkKeys.M);

        Assert.That((await NextKeyAsync()).Key, Is.EqualTo(ConsoleKey.M));
        Assert.That(_server.Windows, Is.EqualTo(1));
    }

    /// <summary>A message of a type the recorder does not know, as a later build may send, is passed over, and the window stays.</summary>
    [Test]
    public async Task A_message_type_the_recorder_does_not_know_is_passed_over()
    {
        var (window, _) = await AttachAsync();

        await window.SendRawAsync("{\"type\":\"ping\"}\n{\"type\":\"resize\",\"body\":{\"columns\":120}}\n{\"type\":\"hello\",\"body\":{\"protocol\":1}}\n");
        await window.SendKeyAsync(LinkKeys.V);

        Assert.That((await NextKeyAsync()).Key, Is.EqualTo(ConsoleKey.V));
        Assert.That(_server.Windows, Is.EqualTo(1));
    }

    /// <summary>A window that sends a line over 64 KB is let go, and that is logged.</summary>
    [Test]
    public async Task A_window_that_sends_a_line_over_64_KB_is_let_go()
    {
        var (window, _) = await AttachAsync();

        await window.SendRawAsync(new string('x', LinkProtocol.MaxLineBytes + 1));

        await window.GoneAsync();
        Assert.That(_server.Windows, Is.Zero);
        Assert.That(_logger.Problems, Has.Some.Contains("let go"));
    }

    /// <summary>A window that sends a line that is not a message of the link is let go, and that is logged.</summary>
    [Test]
    public async Task A_window_that_sends_what_is_not_a_message_is_let_go()
    {
        var (window, _) = await AttachAsync();

        await window.SendRawAsync("Q\n");

        await window.GoneAsync();
        Assert.That(_server.Windows, Is.Zero);
        Assert.That(_logger.Problems, Has.Some.Contains("let go"));
    }

    /// <summary>Bye ends that window's connection, and the change is announced; the run and other windows carry on.</summary>
    [Test]
    public async Task Bye_lets_the_window_go_and_the_run_carries_on()
    {
        var changes = 0;
        _server.WindowsChanged += (_, _) => Interlocked.Increment(ref changes);
        var (leaving, _) = await AttachAsync();
        var (staying, _) = await AttachAsync();

        await leaving.SendAsync(LinkTypes.Bye, new Bye(ByeReasons.Esc), LinkJson.Default.Bye);
        await leaving.GoneAsync();
        WriteLines(1);

        Assert.That(await leaving.NextAsync(), Is.Null);
        Assert.That(Numbers(await staying.LinesUpToAsync(1)), Is.EqualTo(Range(1, 1)));
        Assert.That(_server.Windows, Is.EqualTo(1));
        Assert.That(Volatile.Read(ref changes), Is.EqualTo(3), "two came, one went");
    }

    /// <summary>A window that closes without a word is let go all the same.</summary>
    [Test]
    public async Task A_window_that_closes_without_a_word_is_let_go()
    {
        var (window, _) = await AttachAsync();

        await window.DisposeAsync();

        await window.GoneAsync();
        Assert.That(_server.Windows, Is.Zero);
    }

    /// <summary>The count of windows and whether any of them can be typed into follow the windows attached; a window the recorder opened can be typed into.</summary>
    [Test]
    public async Task The_windows_attached_are_counted_and_whether_a_person_can_type_into_one()
    {
        Assert.That((_server.Windows, _server.AnyInteractive), Is.EqualTo((0, false)));

        var (script, _) = await AttachAsync(TestWindow.Hello(interactive: false));
        Assert.That((_server.Windows, _server.AnyInteractive), Is.EqualTo((1, false)));

        var (alert, _) = await AttachAsync(TestWindow.Hello(alert: true));
        Assert.That((_server.Windows, _server.AnyInteractive), Is.EqualTo((2, true)));

        await alert.DisposeAsync();
        await alert.GoneAsync();
        Assert.That((_server.Windows, _server.AnyInteractive), Is.EqualTo((1, false)));
        await script.DisposeAsync();
    }

    /// <summary>The end reaches every window after the run's last lines, and the end waits for the windows to take it and go.</summary>
    [Test]
    public async Task The_end_reaches_every_window_after_the_runs_last_lines()
    {
        var (first, _) = await AttachAsync(TestWindow.Hello(owner: true));
        var (second, _) = await AttachAsync();
        WriteLines(3);

        var ending = _server.EndAsync(Outcome(), open: true, TimeSpan.FromSeconds(2));
        foreach (var window in new[] { first, second })
        {
            var (before, end) = await window.UntilEndAsync();
            Assert.That(before.Where(message => message.Type == LinkTypes.Log).Select(message => message.Body.Deserialize(LinkJson.Default.LogLine)!.Seq), Is.EqualTo(Range(1, 3)));
            Assert.That(end.Outcome, Is.EqualTo(Outcome()));
            await window.DisposeAsync();
        }

        Assert.That(await ending.WaitAsync(TestWindow.Patience), Is.True);
    }

    /// <summary>The window that started the run opens the results, wherever it came in the order, and no other window does.</summary>
    [Test]
    public async Task The_window_that_started_the_run_opens_the_results()
    {
        var (viewer, _) = await AttachAsync();
        var (owner, _) = await AttachAsync(TestWindow.Hello(owner: true));
        var (later, _) = await AttachAsync();

        var ending = _server.EndAsync(Outcome(), open: true, TimeSpan.FromSeconds(2));
        var opens = new Dictionary<string, bool>();
        foreach (var (name, window) in new[] { ("viewer", viewer), ("owner", owner), ("later", later) })
        {
            opens[name] = (await window.UntilEndAsync()).End.Open;
            await window.DisposeAsync();
        }

        Assert.That(opens, Is.EqualTo(new Dictionary<string, bool> { ["viewer"] = false, ["owner"] = true, ["later"] = false }));
        Assert.That(await ending.WaitAsync(TestWindow.Patience), Is.True);
    }

    /// <summary>
    /// With the window that started the run gone, the last window a person can type into to attach opens the results; a
    /// window whose input is redirected and a window the recorder opened by itself never do.
    /// </summary>
    [Test]
    public async Task Without_the_owner_the_last_window_a_person_can_type_into_opens_the_results()
    {
        var (first, _) = await AttachAsync();
        var (second, _) = await AttachAsync();
        var (script, _) = await AttachAsync(TestWindow.Hello(interactive: false));
        var (alert, _) = await AttachAsync(TestWindow.Hello(alert: true));

        var ending = _server.EndAsync(Outcome(), open: true, TimeSpan.FromSeconds(2));
        var opens = new Dictionary<string, bool>();
        foreach (var (name, window) in new[] { ("first", first), ("second", second), ("script", script), ("alert", alert) })
        {
            opens[name] = (await window.UntilEndAsync()).End.Open;
            await window.DisposeAsync();
        }

        Assert.That(opens, Is.EqualTo(new Dictionary<string, bool> { ["first"] = false, ["second"] = true, ["script"] = false, ["alert"] = false }));
        Assert.That(await ending.WaitAsync(TestWindow.Patience), Is.True);
    }

    /// <summary>With only a window the recorder opened by itself attached, no window opens the results, and the end says so, for the recorder to open them itself.</summary>
    [Test]
    public async Task A_window_the_recorder_opened_never_opens_the_results()
    {
        var (alert, _) = await AttachAsync(TestWindow.Hello(alert: true, owner: true));

        var ending = _server.EndAsync(Outcome(), open: true, TimeSpan.FromSeconds(2));
        var (_, end) = await alert.UntilEndAsync();
        await alert.DisposeAsync();

        Assert.That(end.Open, Is.False);
        Assert.That(await ending.WaitAsync(TestWindow.Patience), Is.False);
    }

    /// <summary>With --no-open, no window opens the results, and the end says no window took them.</summary>
    [Test]
    public async Task With_no_open_no_window_opens_the_results()
    {
        var (owner, _) = await AttachAsync(TestWindow.Hello(owner: true));

        var ending = _server.EndAsync(Outcome(), open: false, TimeSpan.FromSeconds(2));
        var (_, end) = await owner.UntilEndAsync();
        await owner.DisposeAsync();

        Assert.That(end.Open, Is.False);
        Assert.That(await ending.WaitAsync(TestWindow.Patience), Is.False);
    }

    /// <summary>With no window attached, the end returns at once, saying no window took the results.</summary>
    [Test]
    public async Task With_no_window_attached_the_end_returns_at_once()
    {
        WriteLines(2);

        Assert.That(await _server.EndAsync(Outcome(), open: true, TimeSpan.FromSeconds(2)).WaitAsync(TestWindow.Patience), Is.False);
    }

    /// <summary>The end waits for a window that took it and stays only as long as its patience, on the steady clock.</summary>
    [Test]
    public async Task The_end_waits_for_a_window_that_stays_only_as_long_as_its_patience()
    {
        var (owner, _) = await AttachAsync(TestWindow.Hello(owner: true));

        var ending = _server.EndAsync(Outcome(), open: true, TimeSpan.FromSeconds(2));
        await owner.UntilEndAsync();
        await Wait.UntilAsync(() => _time.ActiveTimers == 1);
        _time.Advance(TimeSpan.FromMilliseconds(1999));
        await Task.Delay(50);
        Assert.That(ending.IsCompleted, Is.False, "the end stopped waiting too early");

        _time.Advance(TimeSpan.FromMilliseconds(1));

        Assert.That(await ending.WaitAsync(TestWindow.Patience), Is.True);
    }

    /// <summary>A window that comes after the end is shown the log and the end, and does not open the results: that was settled at the end.</summary>
    [Test]
    public async Task A_window_that_comes_after_the_end_is_shown_the_log_and_the_end()
    {
        WriteLines(2);
        await _server.EndAsync(Outcome(), open: true, TimeSpan.FromSeconds(2));

        var (late, _) = await AttachAsync(TestWindow.Hello(owner: true));
        var (before, end) = await late.UntilEndAsync();

        Assert.That(before.Where(message => message.Type == LinkTypes.Log).Select(message => message.Body.Deserialize(LinkJson.Default.LogLine)!.Seq), Is.EqualTo(Range(1, 2)));
        Assert.That(end, Is.EqualTo(new End(Outcome(), Open: false)));
    }

    /// <summary>The end is sent once: a second end is a mistake in the caller.</summary>
    [Test]
    public async Task The_end_is_sent_once()
    {
        await _server.EndAsync(Outcome(), open: true, TimeSpan.FromSeconds(2));

        Assert.ThrowsAsync<InvalidOperationException>(() => _server.EndAsync(Outcome(), open: true, TimeSpan.FromSeconds(2)));
    }

    /// <summary>A log line too long for a line of the link is cut, with a note that the whole of it is in the log file, so that the window still gets it.</summary>
    [Test]
    public async Task A_log_line_too_long_for_the_link_is_cut_with_a_note()
    {
        var (window, _) = await AttachAsync();

        _log.Write(LogLevel.Error, new string('x', 100_000));
        var line = (await window.LinesUpToAsync(1)).Single();

        Assert.That(line.Text, Does.StartWith(new string('x', RecorderServer.LongestLogText)));
        Assert.That(line.Text, Has.Length.LessThan(RecorderServer.LongestLogText + 200));
        Assert.That(line.Text, Does.Contain("log file"));
        Assert.That(line.Level, Is.EqualTo(LogLevel.Error));
    }

    /// <summary>Stopping the server lets every window go, and ends the keys for the recording loop.</summary>
    [Test]
    public async Task Stopping_the_server_lets_every_window_go_and_ends_the_keys()
    {
        var (window, _) = await AttachAsync();

        await _server.DisposeAsync();

        await window.GoneAsync();
        Assert.That(await window.RestAsync(), Is.Empty);
        await _server.Keys.Completion.WaitAsync(TestWindow.Patience);
        Assert.That(_server.Windows, Is.Zero);
    }

    /// <summary>The server takes only a pipe name made for a run, before it makes any pipe.</summary>
    [Test]
    public async Task The_server_takes_only_a_pipe_name_made_for_a_run()
    {
        await using var server = new RecorderServer(_log, _time, _logger);

        Assert.Throws<ArgumentException>(() => server.Start("Kalam", _run));
        Assert.Throws<ArgumentException>(() => server.Start(@"ClassScribe-..\x", _run));
    }

    /// <summary>A server serves one run: starting it a second time is a mistake in the caller, and so is serving a window before it starts.</summary>
    [Test]
    public async Task A_server_serves_one_run()
    {
        await using var unstarted = new RecorderServer(_log, _time, _logger);
        using var pair = new DuplexStreamPair();

        Assert.Throws<InvalidOperationException>(() => _server.Begin(() => _run));
        Assert.ThrowsAsync<InvalidOperationException>(() => unstarted.ServeAsync(pair.RecorderEnd, CancellationToken.None));
    }

    /// <summary>
    /// The server's own lines, written through a logger into the very log it follows, as in the recorder, reach the windows
    /// like any other line, without the server ever logging from inside its following of the log.
    /// </summary>
    [Test]
    public async Task The_servers_own_lines_reach_the_windows_through_the_log()
    {
        using var factory = LoggerFactory.Create(logging => logging.SetMinimumLevel(LogLevel.Debug).AddProvider(new RecorderLogProvider(_log)));
        await using var server = new RecorderServer(_log, _time, factory.CreateLogger<RecorderServer>());
        server.Begin(() => _run);
        var (first, _) = await TestWindow.AttachAsync(server);
        var (second, _) = await TestWindow.AttachAsync(server);
        _windows.AddRange([first, second]);

        await second.SendAsync(LinkTypes.Bye, new Bye(ByeReasons.Esc), LinkJson.Default.Bye);
        await second.GoneAsync();
        List<string> lines = [];
        while (!lines.Any(line => line.Contains("left", StringComparison.Ordinal)))
        {
            var envelope = await first.NextAsync() ?? throw new AssertionException("The first window was let go.");
            if (envelope.Type == LinkTypes.Log) lines.Add(envelope.Body.Deserialize(LinkJson.Default.LogLine)!.Text);
        }

        Assert.That(lines, Has.Some.Contains("window"));
    }
}
