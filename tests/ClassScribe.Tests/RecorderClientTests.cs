using System.Text;
using System.Text.Json;
using ClassScribe.Commands;
using ClassScribe.Link;
using ClassScribe.Recorder;

namespace ClassScribe.Tests;

/// <summary>
/// A window's end of the link, against a real server over in-memory connections or a recorder answered by hand: it says
/// hello as what it is, is shown the log, the states and the end in order, reconnects after a break while the recorder's
/// process lives, every quarter second doubling to two seconds for up to ten, skipping the lines it has seen, gives up at
/// once when the process is gone, says when ten seconds pass without a state, and reports a recorder it cannot reach in
/// plain words.
/// </summary>
[TestFixture]
public class RecorderClientTests
{
    /// <summary>How far the clock moves at a time while a test waits for the client's retries.</summary>
    private static readonly TimeSpan Step = TimeSpan.FromMilliseconds(50);

    private ManualTimeProvider _time = null!;
    private FakeProcessLiveness _processes = null!;
    private RecordingLogger<RecorderClient> _logger = null!;
    private RunRecord _run = null!;
    private TestRecorder _recorder = null!;
    private RecorderClient _client = null!;
    private CancellationTokenSource _patience = null!;
    private List<Reading> _readings = null!;

    /// <summary>A recorder of the run "Kalam" whose process runs, and a window's client for it, on a clock moved by hand.</summary>
    [SetUp]
    public void SetUp()
    {
        _time = new ManualTimeProvider(TestRuns.Start);
        _processes = new FakeProcessLiveness();
        _run = TestRuns.Record(TestRuns.Id(), RunState.Recording);
        _processes.Start(_run.ProcessId, _run.ProcessStartedUtc);
        _recorder = new TestRecorder(_time, _run);
        _logger = new RecordingLogger<RecorderClient>();
        _client = NewClient(_recorder);
        _patience = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        _readings = [];
    }

    /// <summary>Closes the client, the readings and the recorder.</summary>
    [TearDown]
    public async Task TearDown()
    {
        await _client.DisposeAsync();
        foreach (var reading in _readings) await reading.DisposeAsync();
        await _recorder.DisposeAsync();
        _patience.Dispose();
    }

    /// <summary>A token cancelled once the test has run too long, so that a wait that never ends fails the test.</summary>
    private CancellationToken Token => _patience.Token;

    /// <summary>A client that opens the pipe of <paramref name="recorder"/>.</summary>
    private RecorderClient NewClient(TestRecorder recorder) => new(_processes, _time, _logger) { OpenPipe = recorder.OpenPipeAsync };

    /// <summary>Starts reading what the recorder sends to <paramref name="client"/> (the test's client unless given); closed when the test ends.</summary>
    private Reading Read(RecorderClient? client = null)
    {
        var reading = new Reading((client ?? _client).ReadAsync(Token));
        _readings.Add(reading);
        return reading;
    }

    /// <summary>A state of the run at <paramref name="seconds"/> into the recording.</summary>
    private static RecorderState State(int seconds) =>
        new(RecorderPhase.Recording, "Kalam", TimeSpan.FromSeconds(seconds), "Speakers", null, null, null, null, Attention.None, InWindow: false);

    /// <summary>How the tests' run ends: transcribed.</summary>
    private static RunOutcome Outcome() => new(OutcomeKind.Transcribed, 0, "Saved the transcript", @"D:\Classes\Kalam\Kalam.md", @"D:\Classes\Kalam", null);

    /// <summary>A welcome to the run, in this build's protocol unless told otherwise.</summary>
    private Welcome WelcomeTo(int protocol = LinkProtocol.Version) => new(protocol, "1.0.0+test", _run.ExePath, _run, State(0), 1);

    /// <summary>A log line numbered <paramref name="seq"/>.</summary>
    private static LogLine Line(long seq) => new(seq, TestRuns.Start, Microsoft.Extensions.Logging.LogLevel.Information, $"line {seq}");

    /// <summary>Moves the clock a step at a time until the condition holds; fails after 30 seconds on the clock.</summary>
    private Task<TimeSpan> AdvanceUntilAsync(Func<bool> condition) => _time.AdvanceUntilAsync(condition, Step, atMost: TimeSpan.FromSeconds(30));

    /// <summary>A window says hello as what it is: the protocol and build, no lines seen, and whether it can be typed into, opened by the recorder, or started the run.</summary>
    [TestCase(ViewMode.Owner, true, false, true)]
    [TestCase(ViewMode.Viewer, true, false, false)]
    [TestCase(ViewMode.Alert, true, true, false)]
    [TestCase(ViewMode.Viewer, false, false, false)]
    public async Task A_window_says_hello_as_what_it_is(ViewMode mode, bool interactive, bool alert, bool owner)
    {
        List<Hello> hellos = [];
        _recorder.Answer = async recorder =>
        {
            hellos.Add(await recorder.HelloAsync());
            await recorder.SendAsync(LinkTypes.Welcome, WelcomeTo(), LinkJson.Default.Welcome);
        };

        var welcome = await _client.ConnectAsync(_run, mode, interactive, Token);

        Assert.That(hellos.Single(), Is.EqualTo(new Hello(LinkProtocol.Version, LinkProtocol.Build, Since: 0, interactive, alert, owner)));
        Assert.That(welcome, Is.EqualTo(WelcomeTo()));
        Assert.That(_recorder.PipeNames, Is.EqualTo(new[] { _run.Pipe }));
    }

    /// <summary>
    /// After the welcome come the log so far, then new lines, states and the end, in order; after the end the reading is
    /// over and the window lets the recorder go, so the recorder's wait for it ends at once.
    /// </summary>
    [Test]
    public async Task The_log_so_far_then_lines_states_and_the_end_come_in_order()
    {
        _recorder.WriteLines(3);
        var welcome = await _client.ConnectAsync(_run, ViewMode.Owner, interactive: true, Token);
        var reading = Read();

        List<LogLine> replayed = [await reading.NextAsync<LogLine>(), await reading.NextAsync<LogLine>(), await reading.NextAsync<LogLine>()];
        _recorder.WriteLines(4, from: 4);
        var live = await reading.NextAsync<LogLine>();
        _recorder.Server.SetState(State(42));
        var state = await reading.NextAsync<StateUpdate>();
        var ending = _recorder.Server.EndAsync(Outcome(), open: true, TimeSpan.FromSeconds(2));
        var end = await reading.NextAsync<End>();

        Assert.That(welcome.Run, Is.EqualTo(_run));
        Assert.That(replayed.Select(line => line.Text), Is.EqualTo(new[] { "line 1", "line 2", "line 3" }));
        Assert.That(live.Seq, Is.EqualTo(4));
        Assert.That(state.State, Is.EqualTo(State(42)));
        Assert.That(end, Is.EqualTo(new End(Outcome(), Open: true)));
        Assert.That(await reading.NextAsync(), Is.Null, "nothing comes after the end");
        Assert.That(await ending.WaitAsync(TestWindow.Patience), Is.True);
        Assert.That(await _client.SendKeyAsync(LinkKeys.Q, Token), Is.False, "the link is over");
    }

    /// <summary>
    /// A window that stops reading as soon as the end comes, as a view that returns from its loop at the end does, has let
    /// the recorder go: the link is closed as the end is handed over, so the recorder's wait for the window ends at once,
    /// without its patience running out.
    /// </summary>
    [Test]
    public async Task A_window_that_stops_reading_at_the_end_lets_the_recorder_go_at_once()
    {
        await _client.ConnectAsync(_run, ViewMode.Owner, interactive: true, Token);
        var ending = _recorder.Server.EndAsync(Outcome(), open: true, TimeSpan.FromSeconds(2));

        End? end = null;
        await foreach (var item in _client.ReadAsync(Token))
        {
            if (item is not End last) continue;

            end = last;
            break;
        }

        Assert.That(end, Is.EqualTo(new End(Outcome(), Open: true)));
        Assert.That(await ending.WaitAsync(TestWindow.Patience), Is.True, "the recorder's wait ended, the clock unmoved");
        Assert.That(_recorder.Server.Windows, Is.Zero);
    }

    /// <summary>
    /// After a break while the recorder's process lives, the window tries again a quarter of a second later, says hello
    /// with the last line it has, and is shown only the lines after it; a notice says that it reconnected.
    /// </summary>
    [Test]
    public async Task After_a_break_the_window_reconnects_and_is_shown_only_the_lines_it_lacks()
    {
        _recorder.WriteLines(5);
        await _client.ConnectAsync(_run, ViewMode.Viewer, interactive: true, Token);
        var reading = Read();
        for (var n = 1; n <= 5; n++) await reading.NextAsync<LogLine>();

        _recorder.BreakLast();
        _recorder.WriteLines(8, from: 6);
        var pending = reading.Pending;
        await AdvanceUntilAsync(() => _recorder.OpenedAt.Count == 2);
        var reconnected = await reading.NextAsync<Reconnected>();
        List<LogLine> lines = [await reading.NextAsync<LogLine>(), await reading.NextAsync<LogLine>(), await reading.NextAsync<LogLine>()];

        Assert.That(pending.IsCompletedSuccessfully, Is.True);
        Assert.That(_recorder.OpenedAt[1], Is.GreaterThanOrEqualTo(RecorderClient.FirstRetry), "it waits a quarter of a second before it tries again");
        Assert.That(reconnected.Welcome.Run, Is.EqualTo(_run));
        Assert.That(reconnected.Welcome.OldestSeq, Is.EqualTo(1));
        Assert.That(lines.Select(line => line.Seq), Is.EqualTo(new long[] { 6, 7, 8 }));
    }

    /// <summary>Lines the window has already shown are passed over, even when a recorder sends them again after a reconnect.</summary>
    [Test]
    public async Task Lines_already_shown_are_passed_over()
    {
        List<Hello> hellos = [];
        _recorder.Answer = async recorder =>
        {
            hellos.Add(await recorder.HelloAsync());
            await recorder.SendAsync(LinkTypes.Welcome, WelcomeTo(), LinkJson.Default.Welcome);
            for (var seq = 1; seq <= 3 + 2 * (hellos.Count - 1); seq++) await recorder.SendAsync(LinkTypes.Log, Line(seq), LinkJson.Default.LogLine);
        };
        await _client.ConnectAsync(_run, ViewMode.Viewer, interactive: true, Token);
        var reading = Read();
        List<long> seen = [];
        for (var n = 0; n < 3; n++) seen.Add((await reading.NextAsync<LogLine>()).Seq);

        _recorder.BreakLast();
        var pending = reading.NextAsync<Reconnected>();
        await AdvanceUntilAsync(() => pending.IsCompleted);
        await pending;
        for (var n = 0; n < 2; n++) seen.Add((await reading.NextAsync<LogLine>()).Seq);

        Assert.That(seen, Is.EqualTo(new long[] { 1, 2, 3, 4, 5 }));
        Assert.That(hellos.Select(hello => hello.Since), Is.EqualTo(new long[] { 0, 3 }));
    }

    /// <summary>
    /// While the pipe does not answer after a break, the window tries again after a quarter of a second, then half a second,
    /// a second, and every two seconds; ten seconds after the break, with the recorder's process alive, it says the
    /// connection is lost.
    /// </summary>
    [Test]
    public async Task Retries_double_from_a_quarter_second_to_two_seconds_for_ten_seconds()
    {
        await _client.ConnectAsync(_run, ViewMode.Viewer, interactive: true, Token);
        var reading = Read();
        _recorder.Answering = false;

        _recorder.BreakLast();
        var lost = reading.NextAsync<LinkLost>();
        var moved = await AdvanceUntilAsync(() => lost.IsCompleted);
        var tries = _recorder.OpenedAt.Skip(1).ToList();
        var gaps = tries.Zip(tries.Skip(1), (earlier, later) => later - earlier).ToList();

        Assert.That((await lost).Why, Is.EqualTo(LinkLoss.ConnectionLost));
        Assert.That(tries[0], Is.InRange(TimeSpan.FromSeconds(0.25), TimeSpan.FromSeconds(0.25) + 3 * Step));
        Assert.That(gaps.Take(3), Has.Exactly(3).Items);
        Assert.That(gaps[0], Is.InRange(TimeSpan.FromSeconds(0.5), TimeSpan.FromSeconds(0.5) + 3 * Step));
        Assert.That(gaps[1], Is.InRange(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1) + 3 * Step));
        Assert.That(gaps.Skip(2), Is.All.InRange(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2) + 3 * Step));
        Assert.That(tries[^1], Is.LessThan(TimeSpan.FromSeconds(10)), "no try after ten seconds");
        Assert.That(moved, Is.InRange(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(12.5)), "the connection is said to be lost ten seconds after the break");
    }

    /// <summary>When the recorder's process has ended, a break is reported at once as the recorder gone, without trying to reconnect.</summary>
    [Test]
    public async Task When_the_recorders_process_has_ended_the_window_says_so_at_once()
    {
        await _client.ConnectAsync(_run, ViewMode.Owner, interactive: true, Token);
        var reading = Read();

        _processes.End(_run.ProcessId);
        _recorder.BreakLast();
        var lost = await reading.NextAsync<LinkLost>();

        Assert.That(lost.Why, Is.EqualTo(LinkLoss.RecorderGone));
        Assert.That(_recorder.OpenedAt, Has.Count.EqualTo(1));
        Assert.That(await reading.NextAsync(), Is.Null, "nothing comes after the loss");
        Assert.That(await Read().NextAsync(), Is.Null, "a reading begun after the loss reads nothing either: the loss is final");
    }

    /// <summary>A recorder whose process ends while the window tries to reconnect is reported gone, at the next try.</summary>
    [Test]
    public async Task A_recorder_that_ends_while_the_window_retries_is_reported_gone()
    {
        await _client.ConnectAsync(_run, ViewMode.Owner, interactive: true, Token);
        var reading = Read();
        _recorder.Answering = false;

        _recorder.BreakLast();
        var lost = reading.NextAsync<LinkLost>();
        await AdvanceUntilAsync(() => _recorder.OpenedAt.Count == 3);
        _processes.End(_run.ProcessId);
        await AdvanceUntilAsync(() => lost.IsCompleted);

        Assert.That((await lost).Why, Is.EqualTo(LinkLoss.RecorderGone));
        Assert.That(_recorder.OpenedAt, Has.Count.EqualTo(3));
    }

    /// <summary>A process given the recorder's id after it ended does not count as the recorder: the window says the recorder is gone.</summary>
    [Test]
    public async Task A_process_that_took_the_recorders_id_does_not_count_as_the_recorder()
    {
        await _client.ConnectAsync(_run, ViewMode.Owner, interactive: true, Token);
        var reading = Read();

        _processes.Start(_run.ProcessId, _run.ProcessStartedUtc.AddMinutes(30));
        _recorder.BreakLast();

        Assert.That((await reading.NextAsync<LinkLost>()).Why, Is.EqualTo(LinkLoss.RecorderGone));
    }

    /// <summary>
    /// Ten seconds without a state while the recorder's process lives give a notice that the recorder is not answering,
    /// once; the next state ends the silence, and ten seconds more without one give the notice again.
    /// </summary>
    [Test]
    public async Task Ten_seconds_without_a_state_give_a_notice_once()
    {
        await _client.ConnectAsync(_run, ViewMode.Owner, interactive: true, Token);
        var reading = Read();

        var pending = reading.Pending;
        await Wait.UntilAsync(() => _time.ActiveTimers == 1);
        _time.Advance(RecorderClient.SilenceLimit - TimeSpan.FromTicks(1));
        await Task.Delay(50);
        Assert.That(pending.IsCompleted, Is.False, "the notice came too early");
        _time.Advance(TimeSpan.FromTicks(1));
        var silent = await reading.NextAsync<RecorderSilent>();

        var again = reading.Pending;
        _time.Advance(RecorderClient.SilenceLimit * 3);
        await Task.Delay(50);
        Assert.That(again.IsCompleted, Is.False, "a second notice for the same silence");

        _recorder.Server.SetState(State(30));
        await reading.NextAsync<StateUpdate>();
        _ = reading.Pending;
        await Wait.UntilAsync(() => _time.ActiveTimers == 1);
        _time.Advance(RecorderClient.SilenceLimit);
        var silentAgain = await reading.NextAsync<RecorderSilent>();

        Assert.That(silent.For, Is.EqualTo(RecorderClient.SilenceLimit));
        Assert.That(silentAgain.For, Is.EqualTo(RecorderClient.SilenceLimit));
    }

    /// <summary>
    /// A window that stops reading at the notice of silence, to ask whether to keep waiting, and then reads again, goes on
    /// where it stopped: nothing is lost or read twice, whatever the recorder sent meanwhile.
    /// </summary>
    [Test]
    public async Task Reading_again_after_the_silence_notice_goes_on_where_it_stopped()
    {
        await _client.ConnectAsync(_run, ViewMode.Owner, interactive: true, Token);
        var first = Read();
        _ = first.Pending;
        await Wait.UntilAsync(() => _time.ActiveTimers == 1);
        _time.Advance(RecorderClient.SilenceLimit);
        await first.NextAsync<RecorderSilent>();
        await first.DisposeAsync();

        _recorder.WriteLines(3);
        _recorder.Server.SetState(State(5));
        var again = Read();
        List<object?> items = [await again.NextAsync(), await again.NextAsync(), await again.NextAsync(), await again.NextAsync()];

        Assert.That(items.OfType<LogLine>().Select(line => line.Seq), Is.EqualTo(new long[] { 1, 2, 3 }));
        Assert.That(items.OfType<StateUpdate>().Single().State, Is.EqualTo(State(5)));
    }

    /// <summary>A reading cancelled by its token stops with that cancellation; a new reading goes on, and nothing the recorder sent is lost.</summary>
    [Test]
    public async Task A_reading_cancelled_by_its_token_loses_nothing()
    {
        await _client.ConnectAsync(_run, ViewMode.Owner, interactive: true, Token);
        using var cancel = new CancellationTokenSource();
        var cancelled = new Reading(_client.ReadAsync(cancel.Token));
        _readings.Add(cancelled);
        var waiting = cancelled.Pending;
        await Task.Delay(20);

        await cancel.CancelAsync();
        Assert.CatchAsync<OperationCanceledException>(async () => await waiting.WaitAsync(TestWindow.Patience));
        _recorder.WriteLines(2);
        var again = Read();

        Assert.That((await again.NextAsync<LogLine>()).Seq, Is.EqualTo(1));
        Assert.That((await again.NextAsync<LogLine>()).Seq, Is.EqualTo(2));
    }

    /// <summary>
    /// A reading stopped by its token while a broken link is being made again leaves the mending to the next reading, which
    /// takes it up: the window reconnects and is shown the lines it lacks, rather than find the link gone without a word.
    /// </summary>
    [Test]
    public async Task A_reading_stopped_while_the_link_is_made_again_leaves_the_mending_to_the_next()
    {
        _recorder.WriteLines(2);
        await _client.ConnectAsync(_run, ViewMode.Owner, interactive: true, Token);
        using var stop = new CancellationTokenSource();
        var stopped = new Reading(_client.ReadAsync(stop.Token));
        _readings.Add(stopped);
        for (var n = 1; n <= 2; n++) await stopped.NextAsync<LogLine>();

        _recorder.Answering = false;
        _recorder.BreakLast();
        var mending = stopped.Pending;
        await AdvanceUntilAsync(() => _recorder.OpenedAt.Count >= 3);
        await stop.CancelAsync();
        Assert.CatchAsync<OperationCanceledException>(async () => await mending.WaitAsync(TestWindow.Patience));

        _recorder.Answering = true;
        _recorder.WriteLines(3, from: 3);
        var again = Read();
        var pending = again.Pending;
        await AdvanceUntilAsync(() => pending.IsCompleted);
        var reconnected = await again.NextAsync<Reconnected>();
        var line = await again.NextAsync<LogLine>();

        Assert.That(reconnected.Welcome.Run, Is.EqualTo(_run));
        Assert.That(line.Seq, Is.EqualTo(3));
        Assert.That(await _client.SendKeyAsync(LinkKeys.Q, Token), Is.True, "the link is there again");
    }

    /// <summary>Lines of the log do not count as an answer: the capture threads may log while the recorder's own loop hangs.</summary>
    [Test]
    public async Task Lines_of_the_log_do_not_count_as_an_answer()
    {
        await _client.ConnectAsync(_run, ViewMode.Owner, interactive: true, Token);
        var reading = Read();

        for (var n = 1; n <= 3; n++)
        {
            _ = reading.Pending;
            await Wait.UntilAsync(() => _time.ActiveTimers == 1);
            _time.Advance(TimeSpan.FromSeconds(3));
            _recorder.WriteLines(n, from: n);
            Assert.That((await reading.NextAsync<LogLine>()).Seq, Is.EqualTo(n));
        }

        _ = reading.Pending;
        await Wait.UntilAsync(() => _time.ActiveTimers == 1);
        _time.Advance(TimeSpan.FromSeconds(1));

        Assert.That((await reading.NextAsync<RecorderSilent>()).For, Is.EqualTo(RecorderClient.SilenceLimit));
    }

    /// <summary>Messages of a type the window does not know, and fields it does not know, as a later recorder may send, are passed over.</summary>
    [Test]
    public async Task Types_and_fields_the_window_does_not_know_are_passed_over()
    {
        _recorder.Answer = async recorder =>
        {
            await recorder.HelloAsync();
            await recorder.SendAsync(LinkTypes.Welcome, WelcomeTo(), LinkJson.Default.Welcome);
            await recorder.SendRawAsync("{\"type\":\"ping\"}\n{\"type\":\"progress\",\"body\":{\"percent\":40}}\n");
            await recorder.SendRawAsync("{\"type\":\"log\",\"body\":{\"seq\":1,\"utc\":\"2026-10-15T23:00:02+00:00\",\"level\":\"Warning\",\"text\":\"No sound\",\"colour\":\"yellow\"}}\n");
        };
        await _client.ConnectAsync(_run, ViewMode.Owner, interactive: true, Token);
        var reading = Read();

        var line = await reading.NextAsync<LogLine>();

        Assert.That(line, Is.EqualTo(new LogLine(1, TestRuns.Start, Microsoft.Extensions.Logging.LogLevel.Warning, "No sound")));
    }

    /// <summary>A message the window cannot read, such as a state in a phase this build does not know, is passed over, and what follows comes through.</summary>
    [Test]
    public async Task A_message_the_window_cannot_read_is_passed_over()
    {
        _recorder.Answer = async recorder =>
        {
            await recorder.HelloAsync();
            await recorder.SendAsync(LinkTypes.Welcome, WelcomeTo(), LinkJson.Default.Welcome);
            await recorder.SendRawAsync("{\"type\":\"state\",\"body\":{\"state\":{\"phase\":\"Calibrating\",\"name\":\"Kalam\"}}}\n");
            await recorder.SendRawAsync("{\"type\":\"log\",\"body\":{\"seq\":2}}\n{\"type\":\"end\",\"body\":{}}\n");
            await recorder.SendAsync(LinkTypes.Log, Line(3), LinkJson.Default.LogLine);
        };
        await _client.ConnectAsync(_run, ViewMode.Owner, interactive: true, Token);
        var reading = Read();

        Assert.That(await reading.NextAsync<LogLine>(), Is.EqualTo(Line(3)));
    }

    /// <summary>
    /// A welcome the window cannot read, such as one whose source of the class names no kind, is passed over like any
    /// message the window cannot read, rather than end the connecting with an error, and a welcome it can read is taken.
    /// </summary>
    [Test]
    public async Task A_welcome_the_window_cannot_read_is_passed_over()
    {
        var readable = Encoding.UTF8.GetString(LineChannel.Encode(LinkTypes.Welcome, WelcomeTo(), LinkJson.Default.Welcome).Span);
        var withoutKind = readable.Replace("{\"kind\":\"automatic\"}", "{}", StringComparison.Ordinal);
        _recorder.Answer = async recorder =>
        {
            await recorder.HelloAsync();
            await recorder.SendRawAsync(withoutKind);
            await recorder.SendAsync(LinkTypes.Welcome, WelcomeTo(), LinkJson.Default.Welcome);
        };

        var welcome = await _client.ConnectAsync(_run, ViewMode.Owner, interactive: true, Token);

        Assert.That(withoutKind, Is.Not.EqualTo(readable), "the source's kind was taken out of the first welcome");
        Assert.That(welcome, Is.EqualTo(WelcomeTo()));
        Assert.That(_recorder.OpenedAt, Has.Count.EqualTo(1));
    }

    /// <summary>
    /// A recorder of another build that speaks another protocol refuses the window: connecting fails with the refusal and
    /// the ClassScribe.exe that can show the run, in plain words, and is not tried again.
    /// </summary>
    [Test]
    public async Task A_recorder_of_another_protocol_is_reported_with_the_exe_that_can_show_the_run()
    {
        const string olderExe = @"C:\Users\gdala\AppData\Local\Programs\ClassScribe-old\ClassScribe.exe";
        _recorder.Answer = async recorder =>
        {
            await recorder.HelloAsync();
            await recorder.SendAsync(LinkTypes.Refused, new Refused("This recorder speaks protocol 2 of ClassScribe's link, and the window protocol 1.", olderExe), LinkJson.Default.Refused);
        };

        var failure = Assert.ThrowsAsync<RecorderLinkException>(() => _client.ConnectAsync(_run, ViewMode.Viewer, interactive: true, Token));

        Assert.That(failure!.Failure, Is.EqualTo(LinkFailure.Refused));
        Assert.That(failure.Refused?.ExePath, Is.EqualTo(olderExe));
        Assert.That(failure.Message, Does.Contain("another build of ClassScribe").And.Contain(olderExe));
        Assert.That(_recorder.OpenedAt, Has.Count.EqualTo(1));
        await Task.CompletedTask;
    }

    /// <summary>A welcome in another protocol is taken as a refusal, with the recorder's exe.</summary>
    [Test]
    public async Task A_welcome_in_another_protocol_is_taken_as_a_refusal()
    {
        _recorder.Answer = async recorder =>
        {
            await recorder.HelloAsync();
            await recorder.SendAsync(LinkTypes.Welcome, WelcomeTo(protocol: 2), LinkJson.Default.Welcome);
        };

        var failure = Assert.ThrowsAsync<RecorderLinkException>(() => _client.ConnectAsync(_run, ViewMode.Viewer, interactive: true, Token));

        Assert.That(failure!.Failure, Is.EqualTo(LinkFailure.Refused));
        Assert.That(failure.Refused?.ExePath, Is.EqualTo(_run.ExePath));
        await Task.CompletedTask;
    }

    /// <summary>While a recorder that has just started has no pipe yet, connecting tries at once and then again, and connects once it answers.</summary>
    [Test]
    public async Task Connecting_waits_for_a_recorder_that_has_just_started()
    {
        _recorder.Answering = false;
        var connecting = _client.ConnectAsync(_run, ViewMode.Owner, interactive: true, Token);

        await AdvanceUntilAsync(() => _recorder.OpenedAt.Count >= 3);
        _recorder.Answering = true;
        await AdvanceUntilAsync(() => connecting.IsCompleted);

        Assert.That((await connecting).Run, Is.EqualTo(_run));
        Assert.That(_recorder.OpenedAt[0], Is.EqualTo(TimeSpan.Zero), "the first try is at once");
        Assert.That(_recorder.OpenedAt[1], Is.GreaterThanOrEqualTo(RecorderClient.FirstRetry));
    }

    /// <summary>A recorder whose process ends before it answers is reported as ended, without waiting out the 20 seconds.</summary>
    [Test]
    public async Task Connecting_gives_up_when_the_recorder_ends_before_it_answers()
    {
        _recorder.Answering = false;
        var connecting = _client.ConnectAsync(_run, ViewMode.Owner, interactive: true, Token);

        await AdvanceUntilAsync(() => _recorder.OpenedAt.Count >= 2);
        _processes.End(_run.ProcessId);
        var moved = await AdvanceUntilAsync(() => connecting.IsCompleted);

        var failure = Assert.ThrowsAsync<RecorderLinkException>(() => connecting);
        Assert.That(failure!.Failure, Is.EqualTo(LinkFailure.RecorderGone));
        Assert.That(failure.Message, Does.Contain($"process {_run.ProcessId}").And.Contain("ended before it answered"));
        Assert.That(moved, Is.LessThan(TimeSpan.FromSeconds(3)));
    }

    /// <summary>Closing the client while it waits for a recorder to answer stops the waiting at once.</summary>
    [Test]
    public async Task Closing_the_client_while_it_connects_stops_the_tries()
    {
        _recorder.Answering = false;
        var connecting = _client.ConnectAsync(_run, ViewMode.Owner, interactive: true, Token);
        await AdvanceUntilAsync(() => _recorder.OpenedAt.Count >= 2);

        await _client.DisposeAsync();

        Assert.CatchAsync<OperationCanceledException>(async () => await connecting.WaitAsync(TestWindow.Patience));
        Assert.That(_recorder.OpenedAt, Has.Count.EqualTo(2));
    }

    /// <summary>
    /// A window that gives up connecting after its hello and before the recorder's answer, because the client is closed or
    /// the caller stops waiting, closes the connection it opened: the recorder sees the window go, rather than keep it as a
    /// window that never reads, holding one of the four places.
    /// </summary>
    [TestCase(true)]
    [TestCase(false)]
    public async Task Giving_up_before_the_welcome_closes_the_connection(bool byClosing)
    {
        var helloCame = new TaskCompletionSource<RecorderByHand>(TaskCreationOptions.RunContinuationsAsynchronously);
        _recorder.Answer = async recorder =>
        {
            await recorder.HelloAsync();
            helloCame.TrySetResult(recorder);
        };
        using var giveUp = CancellationTokenSource.CreateLinkedTokenSource(Token);

        var connecting = _client.ConnectAsync(_run, ViewMode.Owner, interactive: true, giveUp.Token);
        var recorder = await helloCame.Task.WaitAsync(TestWindow.Patience);
        if (byClosing) await _client.DisposeAsync();
        else await giveUp.CancelAsync();

        Assert.CatchAsync<OperationCanceledException>(async () => await connecting.WaitAsync(TestWindow.Patience));
        Assert.That(await recorder.NextAsync(), Is.Null, "the recorder sees the window go");
    }

    /// <summary>A recorder whose process runs but whose pipe never answers is given up after 20 seconds, and reported in plain words.</summary>
    [Test]
    public async Task Connecting_gives_up_after_20_seconds_without_an_answer()
    {
        _recorder.Answering = false;
        var connecting = _client.ConnectAsync(_run, ViewMode.Owner, interactive: true, Token);

        var moved = await _time.AdvanceUntilAsync(() => connecting.IsCompleted, TimeSpan.FromMilliseconds(250), atMost: TimeSpan.FromSeconds(30));

        var failure = Assert.ThrowsAsync<RecorderLinkException>(() => connecting);
        Assert.That(failure!.Failure, Is.EqualTo(LinkFailure.NoAnswer));
        Assert.That(failure.Message, Does.Contain($"process {_run.ProcessId}").And.Contain("did not answer within 20 seconds"));
        Assert.That(moved, Is.InRange(RecorderClient.ConnectPatience, RecorderClient.ConnectPatience + TimeSpan.FromSeconds(2.5)));
    }

    /// <summary>
    /// A recorder started with other rights than the window, as with Run as administrator on one side only, cannot be
    /// reached; the window is told how to start ClassScribe to see it, which depends on which side runs as administrator:
    /// seen from a window run as administrator, the recording was started without it, and seen from one that is not, with
    /// it. The window does not try again.
    /// </summary>
    [TestCase(true, "Start ClassScribe the same way as the recording (without Run as administrator) to see it.")]
    [TestCase(false, "Start ClassScribe the same way as the recording (with Run as administrator) to see it.")]
    public async Task A_recorder_with_other_rights_is_reported_plainly(bool windowIsAdministrator, string advice)
    {
        await using var client = new RecorderClient(_processes, _time, _logger) { OpenPipe = _recorder.OpenPipeAsync, Elevated = windowIsAdministrator };
        _recorder.Refusal = new UnauthorizedAccessException("Could not connect to the pipe because it was not owned by the current user.");

        var failure = Assert.ThrowsAsync<RecorderLinkException>(() => client.ConnectAsync(_run, ViewMode.Viewer, interactive: true, Token));

        Assert.That(failure!.Failure, Is.EqualTo(LinkFailure.OtherRights));
        Assert.That(failure.Message, Is.EqualTo(advice));
        Assert.That(failure.InnerException, Is.InstanceOf<UnauthorizedAccessException>());
        Assert.That(_recorder.OpenedAt, Has.Count.EqualTo(1));
    }

    /// <summary>A run with no pipe of its own, recording in its window, or naming a pipe that is not a run's, cannot be connected to.</summary>
    [TestCase(RunHome.InWindow, null)]
    [TestCase(RunHome.Separate, null)]
    [TestCase(RunHome.Separate, "Other-pipe")]
    [TestCase(RunHome.Separate, @"ClassScribe-..\..\x")]
    public void A_run_without_a_pipe_of_its_own_cannot_be_connected_to(RunHome home, string? pipe)
    {
        Assert.ThrowsAsync<ArgumentException>(() => _client.ConnectAsync(_run with { Home = home, Pipe = pipe }, ViewMode.Viewer, interactive: true, Token));
        Assert.That(_recorder.OpenedAt, Is.Empty);
    }

    /// <summary>Reading before connecting is a mistake in the caller.</summary>
    [Test]
    public void Reading_before_connecting_is_a_mistake()
    {
        Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in _client.ReadAsync(Token))
            {
            }
        });
    }

    /// <summary>The recording keys reach the recorder; Ctrl+C reaches it as Ctrl+C; a key the link does not know is refused before it goes.</summary>
    [Test]
    public async Task Keys_reach_the_recorder()
    {
        var pressed = 0;
        _recorder.Server.CtrlCPressed += () => Interlocked.Increment(ref pressed);
        await _client.ConnectAsync(_run, ViewMode.Owner, interactive: true, Token);

        var sentQ = await _client.SendKeyAsync(LinkKeys.Q, Token);
        var key = await _recorder.Server.Keys.ReadAsync(Token);
        var sentCtrlC = await _client.SendKeyAsync(LinkKeys.CtrlC, Token);
        await Wait.UntilAsync(() => Volatile.Read(ref pressed) == 1);

        Assert.That((sentQ, sentCtrlC), Is.EqualTo((true, true)));
        Assert.That(key.Key, Is.EqualTo(ConsoleKey.Q));
        Assert.ThrowsAsync<ArgumentException>(() => _client.SendKeyAsync("Enter", Token));
    }

    /// <summary>A key sent while there is no link, before connecting or after the link was lost, is not sent, and the caller is told.</summary>
    [Test]
    public async Task A_key_without_a_link_is_not_sent()
    {
        var beforeConnecting = await _client.SendKeyAsync(LinkKeys.P, Token);
        await _client.ConnectAsync(_run, ViewMode.Owner, interactive: true, Token);
        var reading = Read();
        _processes.End(_run.ProcessId);
        _recorder.BreakLast();
        await reading.NextAsync<LinkLost>();

        var afterTheLoss = await _client.SendKeyAsync(LinkKeys.P, Token);

        Assert.That((beforeConnecting, afterTheLoss), Is.EqualTo((false, false)));
    }

    /// <summary>
    /// A key sent as the link breaks, before any reading has noticed the break, is not sent, and the caller is told so
    /// rather than thrown at: a recorder that died a moment before the key was pressed is an ordinary event for a view.
    /// </summary>
    [Test]
    public async Task A_key_sent_as_the_link_breaks_is_not_sent()
    {
        await _client.ConnectAsync(_run, ViewMode.Owner, interactive: true, Token);

        _recorder.BreakLast();
        var sent = await _client.SendKeyAsync(LinkKeys.Q, Token);

        Assert.That(sent, Is.False);
        Assert.That(_recorder.Server.Keys.TryRead(out _), Is.False);
    }

    /// <summary>Bye tells the recorder why the window leaves, ends the reading quietly, and closes the link; the run carries on.</summary>
    [Test]
    public async Task Bye_tells_the_recorder_and_ends_the_reading_quietly()
    {
        await _client.ConnectAsync(_run, ViewMode.Viewer, interactive: true, Token);
        var reading = Read();
        var pending = reading.Pending;
        await Task.Delay(20);

        await _client.ByeAsync(ByeReasons.Esc);

        Assert.That(await reading.NextAsync(), Is.Null);
        await Wait.UntilAsync(() => _recorder.Server.Windows == 0);
        Assert.That(_recorder.ServerLog.Messages, Has.Some.Contains("(Esc)"));
        Assert.That(await _client.SendKeyAsync(LinkKeys.Q, Token), Is.False);
        Assert.That(pending.IsCompleted, Is.True);
        Assert.ThrowsAsync<ArgumentException>(() => _client.ByeAsync("Crashed"));
    }

    /// <summary>Closing the client ends the reading quietly and lets the recorder know the window has gone.</summary>
    [Test]
    public async Task Closing_the_client_ends_the_reading_quietly()
    {
        await _client.ConnectAsync(_run, ViewMode.Viewer, interactive: true, Token);
        var reading = Read();
        _ = reading.Pending;
        await Task.Delay(20);

        await _client.DisposeAsync();

        Assert.That(await reading.NextAsync(), Is.Null);
        await Wait.UntilAsync(() => _recorder.Server.Windows == 0);
    }

    /// <summary>
    /// A client that connects again to the same run says hello with the last line it has; one that connects to another
    /// run, as a window does after a recording is continued in a new recorder, starts from that run's first line.
    /// </summary>
    [Test]
    public async Task Connecting_to_another_run_starts_from_its_first_line()
    {
        var otherRun = TestRuns.Record(TestRuns.Id(2), RunState.Recording, processId: 5555);
        _processes.Start(5555, otherRun.ProcessStartedUtc);
        await using var other = new TestRecorder(_time, otherRun);
        List<Hello> hellos = [];
        other.Answer = async recorder =>
        {
            hellos.Add(await recorder.HelloAsync());
            await recorder.SendAsync(LinkTypes.Welcome, WelcomeTo() with { Run = otherRun }, LinkJson.Default.Welcome);
        };
        _recorder.Answer = async recorder =>
        {
            hellos.Add(await recorder.HelloAsync());
            await recorder.SendAsync(LinkTypes.Welcome, WelcomeTo(), LinkJson.Default.Welcome);
            for (var seq = 1; seq <= 5; seq++) await recorder.SendAsync(LinkTypes.Log, Line(seq), LinkJson.Default.LogLine);
        };
        await using var client = new RecorderClient(_processes, _time, _logger)
        {
            OpenPipe = (pipe, limit, cancellationToken) => (pipe == otherRun.Pipe ? other : _recorder).OpenPipeAsync(pipe, limit, cancellationToken)
        };

        await client.ConnectAsync(_run, ViewMode.Owner, interactive: true, Token);
        var reading = Read(client);
        for (var n = 1; n <= 5; n++) await reading.NextAsync<LogLine>();
        await client.ConnectAsync(_run, ViewMode.Owner, interactive: true, Token);
        await client.ConnectAsync(otherRun, ViewMode.Owner, interactive: true, Token);

        Assert.That(hellos.Select(hello => hello.Since), Is.EqualTo(new long[] { 0, 5, 0 }));
        Assert.That(_recorder.PipeNames.Concat(other.PipeNames), Is.EqualTo(new[] { _run.Pipe, _run.Pipe, otherRun.Pipe }));
    }

    /// <summary>What a test reads from the client, one item at a time, with a limit on every wait.</summary>
    /// <param name="messages">What the client reads.</param>
    private sealed class Reading(IAsyncEnumerable<object> messages) : IAsyncDisposable
    {
        private readonly IAsyncEnumerator<object> _items = messages.GetAsyncEnumerator();
        private Task<bool>? _pending;

        /// <summary>The wait for the next item, begun now unless it has begun already: true when an item came, false once the reading is over.</summary>
        public Task<bool> Pending => _pending ??= _items.MoveNextAsync().AsTask();

        /// <summary>The next item, or null once the reading is over.</summary>
        public async Task<object?> NextAsync()
        {
            var more = await Pending.WaitAsync(TestWindow.Patience);
            _pending = null;
            return more ? _items.Current : null;
        }

        /// <summary>The next item, which must be a <typeparamref name="T"/>.</summary>
        public async Task<T> NextAsync<T>()
        {
            var item = await NextAsync();
            Assert.That(item, Is.InstanceOf<T>(), "the next item the client read");
            return (T)item!;
        }

        /// <summary>Ends the reading, once the item it waits for, if any, has come.</summary>
        public async ValueTask DisposeAsync()
        {
            if (_pending is { IsCompleted: false } pending && await Task.WhenAny(pending, Task.Delay(TestWindow.Patience)) != pending) return;

            await _items.DisposeAsync();
        }
    }
}
