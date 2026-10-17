using ClassScribe.Commands;
using ClassScribe.Link;
using ClassScribe.Recorder;
using ClassScribe.Settings;
using Microsoft.Extensions.Logging;

namespace ClassScribe.Tests;

/// <summary>
/// How the recorder makes itself seen while no window shows its run, without a sound: the minimized window it brings back
/// after the last window is closed, at once when something needs attention or the run fails, and after two minutes of
/// something worth a look; each as Recording:ReopenWindow says, one window at a time, at most one launch every two minutes,
/// and never again for a mere close once that window was closed within two minutes. The windows that come and go are
/// played over in-memory connections to a real server; the alert windows are pretend processes the test ends.
/// </summary>
[TestFixture]
public class UnattendedAlertsTests
{
    /// <summary>The run the alerts are for.</summary>
    private static readonly string RunId = TestRuns.Id();

    private ManualTimeProvider _time = null!;
    private RecorderLog _log = null!;
    private RecorderServer _server = null!;
    private FakeProcessStarter _starter = null!;
    private TestOptions<RecordingOptions> _options = null!;
    private RecordingLogger<UnattendedAlerts> _logger = null!;
    private UnattendedAlerts _alerts = null!;
    private List<TestWindow> _windows = null!;

    /// <summary>A server for the run, with no window, and the alerts started for it, on a clock moved by hand, with the default setting.</summary>
    [SetUp]
    public void SetUp()
    {
        _time = new ManualTimeProvider(TestRuns.Start);
        _log = new RecorderLog(_time);
        _server = new RecorderServer(_log, _time, new RecordingLogger<RecorderServer>());
        _server.Begin(() => TestRuns.Record(RunId, RunState.Recording));
        _starter = new FakeProcessStarter(_time);
        _options = TestOptions.Create(new RecordingOptions());
        _logger = new RecordingLogger<UnattendedAlerts>();
        _alerts = new UnattendedAlerts(_server, _starter, _options, _time, _logger);
        _alerts.Start(RunId);
        _windows = [];
    }

    /// <summary>Closes every window, the alerts, the server and the log.</summary>
    [TearDown]
    public async Task TearDown()
    {
        foreach (var window in _windows) await window.DisposeAsync();
        _alerts.Dispose();
        await _server.DisposeAsync();
        _log.Dispose();
    }

    /// <summary>A state of the run recording, with <paramref name="attention"/> or none, in <paramref name="phase"/>.</summary>
    private static RecorderState State(RecorderPhase phase = RecorderPhase.Recording, Attention? attention = null) =>
        new(phase, "Kalam", TimeSpan.FromMinutes(1), "Speakers", null, null, null, null, attention ?? Attention.None, InWindow: false);

    /// <summary>What needs attention now, numbered <paramref name="count"/>, such as nothing being recorded.</summary>
    private static Attention Alert(int count) => new(AttentionLevel.Alert, "NOT RECORDING (waiting for \"chrome\")", "Not recording: waiting for \"chrome\"", count);

    /// <summary>Something worth a look, numbered <paramref name="count"/>, such as no sound for a while.</summary>
    private static Attention Notice(int count) => new(AttentionLevel.Notice, "no sound 2 min", "Nothing audible for 2 minutes", count);

    /// <summary>Moves the clock on by <paramref name="by"/> and publishes <paramref name="state"/>, as the run does once a second.</summary>
    private void Tick(TimeSpan by, RecorderState? state = null)
    {
        _time.Advance(by);
        _alerts.Publish(state ?? State());
    }

    /// <summary>Publishes <paramref name="state"/> once a second for <paramref name="seconds"/> seconds.</summary>
    private void TickFor(int seconds, RecorderState? state = null)
    {
        for (var second = 0; second < seconds; second++) Tick(TimeSpan.FromSeconds(1), state);
    }

    /// <summary>Attaches a window to the server, closed when the test ends unless the test closes it first.</summary>
    private async Task<TestWindow> AttachAsync(bool alert = false)
    {
        var (window, _) = await TestWindow.AttachAsync(_server, TestWindow.Hello(alert: alert));
        _windows.Add(window);
        await Wait.UntilAsync(() => _server.Windows == _windows.Count);
        return window;
    }

    /// <summary>Closes <paramref name="window"/>, as closing the ClassScribe window does, and waits until the server has let it go.</summary>
    private async Task CloseAsync(TestWindow window)
    {
        _windows.Remove(window);
        await window.DisposeAsync();
        await window.GoneAsync();
        await Wait.UntilAsync(() => _server.Windows == _windows.Count);
    }

    /// <summary>Attaches a window and closes it again at once: the last window going away.</summary>
    private async Task AttachAndCloseAsync()
    {
        var window = await AttachAsync();
        await CloseAsync(window);
    }

    /// <summary>Leaves the view in <paramref name="window"/> with Esc, as the view does, and waits until the server has let the window go.</summary>
    private async Task LeaveWithEscAsync(TestWindow window)
    {
        _windows.Remove(window);
        await window.SendAsync(LinkTypes.Bye, new Bye(ByeReasons.Esc), LinkJson.Default.Bye);
        await window.GoneAsync();
        await window.DisposeAsync();
        await Wait.UntilAsync(() => _server.Windows == _windows.Count);
    }

    /// <summary>
    /// A window that leaves the view with Esc was not closed: whoever pressed it is at the keyboard and chose to leave, from
    /// the menu, say, which stays open, so no window comes back for it, however long nobody shows the run. A window that
    /// goes away without a word, as a closed one does, still brings one back.
    /// </summary>
    [Test]
    public async Task Leaving_with_Esc_brings_no_window_back_but_a_window_closed_does()
    {
        await LeaveWithEscAsync(await AttachAsync());
        TickFor(10);
        Assert.That(_starter.Launches, Is.Empty, "Esc is a choice, not a closed window");

        await AttachAndCloseAsync();
        TickFor(3);

        Assert.That(_starter.AlertWindows, Has.Count.EqualTo(1));
    }

    // ------------------------------------------------------------------ the last window closed

    /// <summary>
    /// When the last window showing the recording goes away while it records, the alert window opens 3 seconds later, not
    /// before, and the log says why before it opens.
    /// </summary>
    [Test]
    public async Task When_the_last_window_goes_away_while_recording_the_alert_window_opens_after_3_seconds()
    {
        await AttachAndCloseAsync();

        TickFor(2);
        Assert.That(_starter.Launches, Is.Empty, "not yet");
        Tick(TimeSpan.FromSeconds(1));

        Assert.That(_starter.AlertWindows.Select(launch => launch.RunId), Is.EqualTo(new[] { RunId }));
        Assert.That(_starter.Launches, Has.Count.EqualTo(1));
        Assert.That(_logger.Messages, Has.Some.StartsWith("The window was closed; the recording goes on"));
    }

    /// <summary>A window that comes back within the 3 seconds keeps the alert window away: nobody needs reminding.</summary>
    [Test]
    public async Task A_window_that_comes_back_within_3_seconds_keeps_the_alert_window_away()
    {
        await AttachAndCloseAsync();
        TickFor(2);
        await AttachAsync();

        TickFor(10);

        Assert.That(_starter.Launches, Is.Empty);
    }

    /// <summary>The last window going away after the stop, during the upload, opens nothing: the transcript appearing is the sign that the work is done.</summary>
    [Test]
    public async Task A_window_that_goes_away_after_the_stop_opens_nothing()
    {
        _alerts.Publish(State(RecorderPhase.Transcribing));
        await AttachAndCloseAsync();

        TickFor(10, State(RecorderPhase.Transcribing));

        Assert.That(_starter.Launches, Is.Empty);
    }

    /// <summary>With Recording:ReopenWindow = OnProblems, the last window going away opens nothing; an Alert still does.</summary>
    [Test]
    public async Task With_OnProblems_a_closed_window_opens_nothing_but_an_alert_does()
    {
        _options.Set(new RecordingOptions { ReopenWindow = ReopenWindow.OnProblems });
        await AttachAndCloseAsync();
        TickFor(10);
        Assert.That(_starter.Launches, Is.Empty, "a mere close");

        Tick(TimeSpan.FromSeconds(1), State(attention: Alert(1)));

        Assert.That(_starter.AlertWindows, Has.Count.EqualTo(1));
    }

    /// <summary>With Recording:ReopenWindow = Never, nothing opens a window: not a close, not an Alert, not a failure.</summary>
    [Test]
    public async Task With_Never_nothing_opens_a_window()
    {
        _options.Set(new RecordingOptions { ReopenWindow = ReopenWindow.Never });
        await AttachAndCloseAsync();

        TickFor(10);
        Tick(TimeSpan.FromSeconds(1), State(attention: Alert(1)));
        TickFor(130, State(attention: Notice(2)));
        Tick(TimeSpan.FromSeconds(1), State(RecorderPhase.Failed));

        Assert.That(_starter.Launches, Is.Empty);
    }

    // ------------------------------------------------------------------ alerts, notices and failures

    /// <summary>Something that needs attention, with nobody attached, opens the alert window at once, with the reason in the log first.</summary>
    [Test]
    public void An_alert_with_nobody_attached_opens_the_window_at_once()
    {
        _alerts.Publish(State(attention: Alert(1)));

        Assert.That(_starter.AlertWindows, Has.Count.EqualTo(1));
        Assert.That(_logger.Messages, Has.Some.Contains("Not recording: waiting for \"chrome\""));
    }

    /// <summary>The same Alert, published again every second, opens one window, not one a second.</summary>
    [Test]
    public void The_same_alert_published_again_opens_no_second_window()
    {
        TickFor(5, State(attention: Alert(1)));
        _starter.AlertWindows[0].Exit(0);
        TickFor(5, State(attention: Alert(1)));

        Assert.That(_starter.Launches, Has.Count.EqualTo(1));
    }

    /// <summary>While a window shows the run, an Alert opens no window: the window flashes for it itself.</summary>
    [Test]
    public async Task An_alert_opens_nothing_while_a_window_shows_the_run()
    {
        await AttachAsync();

        _alerts.Publish(State(attention: Alert(1)));

        Assert.That(_starter.Launches, Is.Empty);
    }

    /// <summary>The window the recorder opened itself counts as a window showing the run: an Alert while it is attached opens no second one.</summary>
    [Test]
    public async Task The_alert_window_itself_counts_as_a_window_showing_the_run()
    {
        _alerts.Publish(State(attention: Alert(1)));
        Assert.That(_starter.AlertWindows, Has.Count.EqualTo(1));
        await AttachAsync(alert: true);
        TickFor(130, State(attention: Alert(1)));

        Tick(TimeSpan.FromSeconds(1), State(attention: Alert(2)));

        Assert.That(_starter.Launches, Has.Count.EqualTo(1));
    }

    /// <summary>
    /// Something worth a look opens the window only once it has lasted two minutes: a Notice that ends sooner opens
    /// nothing, and one that lasts opens the window at the two-minute mark.
    /// </summary>
    [Test]
    public void A_notice_opens_the_window_once_it_has_lasted_two_minutes()
    {
        _alerts.Publish(State(attention: Notice(1)));
        TickFor(119, State(attention: Notice(1)));
        Assert.That(_starter.Launches, Is.Empty, "not yet");
        Tick(TimeSpan.FromSeconds(1), State(attention: Notice(1)));

        Assert.That(_starter.AlertWindows, Has.Count.EqualTo(1));
        Assert.That(_logger.Messages, Has.Some.Contains("Nothing audible for 2 minutes"));
    }

    /// <summary>A Notice that ends before two minutes opens nothing, and the count starts again with the next one.</summary>
    [Test]
    public void A_notice_that_ends_before_two_minutes_opens_nothing()
    {
        TickFor(100, State(attention: Notice(1)));
        TickFor(10);
        TickFor(100, State(attention: Notice(2)));

        Assert.That(_starter.Launches, Is.Empty);
    }

    /// <summary>A run that fails with nobody attached opens the window, so that the failure is seen; a run that is done opens nothing.</summary>
    [TestCase(RecorderPhase.Failed, 1)]
    [TestCase(RecorderPhase.Done, 0)]
    [TestCase(RecorderPhase.Cancelled, 0)]
    public void The_end_of_the_run_opens_the_window_only_for_a_failure(RecorderPhase end, int launches)
    {
        _alerts.Publish(State(end));

        Assert.That(_starter.Launches, Has.Count.EqualTo(launches));
    }

    /// <summary>
    /// A failure a window saw opens no window later: the run's last state may be published again after that window has
    /// taken the end and gone, in the moment before the run's scope ends, and that repeat must not open a window for a
    /// failure that was shown already.
    /// </summary>
    [Test]
    public async Task A_failed_end_a_window_saw_opens_no_window_once_that_window_has_gone()
    {
        var window = await AttachAsync();
        _alerts.Publish(State(RecorderPhase.Failed));
        await CloseAsync(window);

        TickFor(5, State(RecorderPhase.Failed));

        Assert.That(_starter.Launches, Is.Empty);
    }

    /// <summary>
    /// An Alert a window saw, and flashed for, opens no window once that window is closed while the Alert lasts: whoever
    /// closed it had seen it. A new Alert still opens one.
    /// </summary>
    [Test]
    public async Task An_alert_a_window_saw_opens_no_window_once_that_window_is_closed_but_a_new_alert_does()
    {
        _options.Set(new RecordingOptions { ReopenWindow = ReopenWindow.OnProblems });
        var window = await AttachAsync();
        _alerts.Publish(State(attention: Alert(1)));
        await CloseAsync(window);
        TickFor(5, State(attention: Alert(1)));
        Assert.That(_starter.Launches, Is.Empty, "the window showed it");

        Tick(TimeSpan.FromSeconds(1), State(attention: Alert(2)));

        Assert.That(_starter.Launches, Has.Count.EqualTo(1), "a new Alert");
    }

    // ------------------------------------------------------------------ the limits

    /// <summary>One alert window at a time: while the window opened lives, nothing opens another, however much needs attention.</summary>
    [Test]
    public void One_alert_window_at_a_time()
    {
        _alerts.Publish(State(attention: Alert(1)));
        TickFor(130, State(attention: Alert(1)));

        Tick(TimeSpan.FromSeconds(1), State(attention: Alert(2)));
        Assert.That(_starter.Launches, Has.Count.EqualTo(1), "the first window still lives");

        _starter.AlertWindows[0].Exit(0);
        Tick(TimeSpan.FromSeconds(1), State(attention: Alert(3)));

        Assert.That(_starter.Launches, Has.Count.EqualTo(2));
    }

    /// <summary>At most one launch every two minutes: a new Alert within two minutes of the last launch opens no window, even after that window was closed.</summary>
    [Test]
    public void At_most_one_launch_every_two_minutes()
    {
        _alerts.Publish(State(attention: Alert(1)));
        _starter.AlertWindows[0].Exit(0);
        TickFor(119, State(attention: Alert(2)));
        Assert.That(_starter.Launches, Has.Count.EqualTo(1), "within two minutes of the last launch");

        Tick(TimeSpan.FromSeconds(1), State(attention: Alert(2)));

        Assert.That(_starter.Launches, Has.Count.EqualTo(2));
    }

    /// <summary>
    /// A window opened for a mere close that is closed again within two minutes means "leave me alone": the last window
    /// going away opens no window again in this run, while a problem still does.
    /// </summary>
    [Test]
    public async Task A_window_opened_for_a_close_and_closed_within_two_minutes_snoozes_closes_but_not_problems()
    {
        await AttachAndCloseAsync();
        TickFor(3);
        Assert.That(_starter.AlertWindows, Has.Count.EqualTo(1));
        TickFor(60);
        _starter.AlertWindows[0].Exit(0);
        Tick(TimeSpan.FromSeconds(1));

        TickFor(120);
        await AttachAndCloseAsync();
        TickFor(10);
        Assert.That(_starter.Launches, Has.Count.EqualTo(1), "a mere close opens nothing now");
        Assert.That(_logger.Messages, Has.Some.Contains("closed within 2 minutes"));

        Tick(TimeSpan.FromSeconds(1), State(attention: Alert(1)));

        Assert.That(_starter.Launches, Has.Count.EqualTo(2), "a problem still opens it");
    }

    /// <summary>A window opened for a close that stays open longer than two minutes does not snooze anything: once it is closed, the next close brings a window back again.</summary>
    [Test]
    public async Task A_window_opened_for_a_close_that_stays_longer_does_not_snooze()
    {
        await AttachAndCloseAsync();
        TickFor(3);
        TickFor(130);
        _starter.AlertWindows[0].Exit(0);
        Tick(TimeSpan.FromSeconds(1));

        await AttachAndCloseAsync();
        TickFor(3);

        Assert.That(_starter.Launches, Has.Count.EqualTo(2));
    }

    /// <summary>A start Windows refuses is logged as a warning, with Windows' words, and the recording goes on; it is tried again at the next occasion after the rate limit.</summary>
    [Test]
    public void A_start_Windows_refuses_is_logged_and_tried_again_later()
    {
        _starter.RefuseWith = 2;

        Assert.DoesNotThrow(() => _alerts.Publish(State(attention: Alert(1))));
        Assert.That(_logger.Problems, Has.Some.StartsWith("Could not open a ClassScribe window").And.Some.Contains("(error 2)"));

        _starter.RefuseWith = null;
        TickFor(119, State(attention: Alert(2)));
        Assert.That(_starter.AlertWindows, Is.Empty, "within two minutes of the refused try");
        Tick(TimeSpan.FromSeconds(1), State(attention: Alert(2)));

        Assert.That(_starter.AlertWindows, Has.Count.EqualTo(1));
    }

    /// <summary>Once the alerts are disposed, as the run ends, nothing opens a window any more, and a window going away is no longer followed.</summary>
    [Test]
    public async Task Once_disposed_nothing_opens_a_window()
    {
        _alerts.Dispose();
        await AttachAndCloseAsync();

        TickFor(5);
        Tick(TimeSpan.FromSeconds(1), State(attention: Alert(1)));

        Assert.That(_starter.Launches, Is.Empty);
    }

    /// <summary>The alerts are started once for one run; a second start is a mistake in the code and says so.</summary>
    [Test]
    public void The_alerts_start_once()
    {
        Assert.That(() => _alerts.Start(RunId), Throws.InvalidOperationException);
    }

    /// <summary>The alerts refuse anything but a run id before it could name a process to start.</summary>
    [Test]
    public void Anything_but_a_run_id_is_refused()
    {
        using var alerts = new UnattendedAlerts(_server, _starter, _options, _time, _logger);

        Assert.That(() => alerts.Start("not a run id"), Throws.ArgumentException);
    }
}
