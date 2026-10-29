using ClassScribe.Audio.Microphones;
using ClassScribe.Commands;
using ClassScribe.Desktop;
using ClassScribe.Settings;
using ClassScribe.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClassScribe.Tests;

/// <summary>
/// The microphone check: a passive look every 250 ms at what the apps do with the microphones, with the readers played by
/// fakes, the clock and the keyboard the test's, a log file in a folder of the test's own, and the window's title. Q,
/// Ctrl+C, --seconds and a cancellation stop it; M marks; a reader that fails gives lines and the check goes on; a gap
/// between checks is noted; the log's path is printed first and last; and a log that cannot be created is exit 1.
/// </summary>
[TestFixture]
public class MicCheckCommandTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 17, 23, 0, 0, TimeSpan.Zero);

    private ManualTimeProvider _time = null!;
    private FakeMicrophoneSessions _sessions = null!;
    private FakeMicrophoneUsageRecord _record = null!;
    private FakeProcessNames _names = null!;
    private FakeConsoleUi _ui = null!;
    private FakeWindowSignals _window = null!;
    private FakeUserSignals _signals = null!;
    private RecordingLogger<MicCheckCommand> _log = null!;
    private TempFolder _root = null!;
    private CancellationTokenSource _cancellation = null!;
    private Task<int>? _run;

    /// <summary>A clock moved by hand, this laptop's two microphones with nothing on them, an empty usage record, the keyboard, the window and a recordings folder of the test's own.</summary>
    [SetUp]
    public void SetUp()
    {
        _time = new ManualTimeProvider(Start);
        _sessions = new FakeMicrophoneSessions();
        _sessions.Set(Laptop.Listing(Laptop.ArrayId));
        _sessions.Set(Laptop.Listing(Laptop.ObsbotId));
        _record = new FakeMicrophoneUsageRecord();
        _names = new FakeProcessNames();
        _ui = new FakeConsoleUi();
        _window = new FakeWindowSignals();
        _signals = new FakeUserSignals();
        _log = new RecordingLogger<MicCheckCommand>();
        _root = new TempFolder();
        _cancellation = new CancellationTokenSource();
        _run = null;
    }

    /// <summary>Ends a check a test left running, so that its log is closed, and deletes the folder.</summary>
    [TearDown]
    public async Task TearDown()
    {
        _cancellation.Cancel();
        if (_run is not null) await _run.WaitAsync(TimeSpan.FromSeconds(15));
        _cancellation.Dispose();
        _root.Dispose();
    }

    /// <summary>The command under test over the fakes, with the recordings folder at the test's root, and <paramref name="window"/> for the window (the fake unless given).</summary>
    private MicCheckCommand CreateCommand(IWindowSignals? window = null) =>
        new(_sessions, _record, _names, new FakeOwnProcesses(Environment.ProcessId), Printed.Program,
            new ClassFolders(TestOptions.Create(new RecordingOptions { OutputFolder = _root.Path }), _time, NullLogger<ClassFolders>.Instance), new MicCheckLog(),
            _ui, window ?? _window, _signals, _time, _log);

    /// <summary>
    /// Starts the check with <paramref name="request"/> (none given: no limit, the default folder) and waits for its first
    /// look to finish: the title is the last thing the first look writes, so the look's lines are on the screen by then.
    /// </summary>
    private async Task StartAsync(MicCheckRequest? request = null)
    {
        _run = CreateCommand().RunAsync(request ?? new MicCheckRequest(null, null), _cancellation.Token);
        await Wait.UntilAsync(() => _window.Titles.Count >= 1 || _run.IsCompleted);
    }

    /// <summary>
    /// Moves the clock on by <paramref name="step"/> (one interval unless given) and waits for the look that follows to read the
    /// usage record, the last of its reads, or for the check's end. What the look writes after that read, the record's lines
    /// and the title, may still be on its way: a test that asserts on those waits for them.
    /// </summary>
    private async Task TickAsync(TimeSpan? step = null)
    {
        var reads = _record.Reads;
        _time.Advance(step ?? MicCheckCommand.Interval);
        await Wait.UntilAsync(() => _record.Reads > reads || _run!.IsCompleted);
    }

    /// <summary>Presses Q and waits for the check to end; returns its exit code.</summary>
    private async Task<int> StopAsync()
    {
        _ui.Type('q');
        return await _run!.WaitAsync(TimeSpan.FromSeconds(15));
    }

    /// <summary>What the check showed, line by line.</summary>
    private string[] Lines => _ui.Screen.Split(Environment.NewLine);

    /// <summary>The log's path, as the header printed it.</summary>
    private string LogPath => Lines.Single(line => line.StartsWith("Log file: ", StringComparison.Ordinal))["Log file: ".Length..];

    /// <summary>A look is made at the start and then every 250 ms: the microphones and the usage record are read at each.</summary>
    [Test]
    public async Task A_look_is_made_at_the_start_and_every_250_ms()
    {
        await StartAsync();
        for (var tick = 0; tick < 4; tick++) await TickAsync();

        Assert.That(_sessions.ReadAlls, Is.EqualTo(5));
        Assert.That(_record.Reads, Is.EqualTo(5));
        Assert.That(_sessions.Reads, Is.Empty, "every look covers every microphone");
    }

    /// <summary>
    /// The header comes first, with the log's path; then the lines of the first look, how things are at the start; Q stops
    /// the check, with the footer that says why it stopped, how many looks it made and what it cost, and the log's path again
    /// last; the exit code is 0 and the window's title is ClassScribe's own again.
    /// </summary>
    [Test]
    public async Task Q_stops_the_check_with_the_footer_and_exit_0()
    {
        await StartAsync();

        var exitCode = await StopAsync();

        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(Lines[0], Does.StartWith("ClassScribe microphone check"));
        Assert.That(LogPath, Does.StartWith(Path.Combine(_root.Path, FolderNames.MicCheck, "mic-check_")));
        Assert.That(Lines, Has.Some.Matches<string>(line => line.Contains($"MIC      {Laptop.ArrayName}: at start:", StringComparison.Ordinal)));
        Assert.That(Lines, Has.Some.Matches<string>(line => line.Contains($"MIC      {Laptop.ObsbotName}: at start:", StringComparison.Ordinal)));
        Assert.That(Lines, Has.Some.Matches<string>(line => line.Contains("PRIVACY  Windows settings: microphone access: Allow; desktop apps: Allow", StringComparison.Ordinal)));
        Assert.That(Lines[^3], Does.Match(@"^\d\d:\d\d:\d\d\.\d{3}  The microphone check stopped: Q pressed\. 1 look in 00:00:00; 3 timestamped lines\.$"));
        Assert.That(Lines[^2], Does.StartWith("The check's own cost: "));
        Assert.That(Lines[^1], Is.EqualTo($"The log is saved as: {LogPath}"));
        Assert.That(_window.Title, Is.EqualTo(TitleText.AppName));
        Assert.That(File.ReadAllText(LogPath).TrimEnd(), Does.EndWith($"The log is saved as: {LogPath}"), "the footer is in the file too");
    }

    /// <summary>M writes a numbered MARK line, to the window and the file, so that Gulzar's clicks in the class page can be placed in the log.</summary>
    [Test]
    public async Task M_writes_numbered_marks()
    {
        await StartAsync();

        _ui.Type('m');
        await Wait.UntilAsync(() => _ui.Screen.Contains("MARK     1 (M pressed)", StringComparison.Ordinal));
        _ui.Type('m');
        await Wait.UntilAsync(() => _ui.Screen.Contains("MARK     2 (M pressed)", StringComparison.Ordinal));
        await StopAsync();

        var file = File.ReadAllText(LogPath);
        Assert.That(file, Does.Contain("MARK     1 (M pressed)"));
        Assert.That(file, Does.Contain("MARK     2 (M pressed)"));
        Assert.That(Lines[^3], Does.Contain("5 timestamped lines."));
    }

    /// <summary>Keys other than Q and M, and Q or M held with Ctrl or Alt as a shortcut meant for another program, do nothing.</summary>
    [Test]
    public async Task Other_keys_do_nothing()
    {
        await StartAsync();

        _ui.Type('p', 'v', 'x');
        _ui.TypeHolding(ConsoleModifiers.Control, 'q');
        _ui.TypeHolding(ConsoleModifiers.Alt, 'm');
        await TickAsync();
        await TickAsync();

        Assert.That(_run!.IsCompleted, Is.False);
        Assert.That(_ui.Screen, Does.Not.Contain("MARK"));
    }

    /// <summary>--seconds stops the check by itself once that long has passed, and the footer says so.</summary>
    [Test]
    public async Task Seconds_stops_the_check_by_itself()
    {
        await StartAsync(new MicCheckRequest(TimeSpan.FromSeconds(1), null));

        for (var tick = 0; tick < 4; tick++) await TickAsync();
        var exitCode = await _run!.WaitAsync(TimeSpan.FromSeconds(15));

        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(Lines[^3], Does.Contain("The microphone check stopped: the 1 second asked for with --seconds has passed. 5 looks in 00:00:01;"));
        Assert.That(Lines, Has.Some.Matches<string>(line => line == "Stops by itself after 1 second; Q or Ctrl+C stops it sooner. M writes a numbered mark."));
    }

    /// <summary>Ctrl+C, which the console hands over as a key while one is read, stops the check at once: there is no class at risk, so one press is enough.</summary>
    [Test]
    public async Task Ctrl_C_stops_the_check()
    {
        await StartAsync();

        _ui.Type('\u0003');
        var exitCode = await _run!.WaitAsync(TimeSpan.FromSeconds(15));

        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(Lines[^3], Does.Contain("The microphone check stopped: Ctrl+C pressed."));
    }

    /// <summary>A cancellation from outside, as when ClassScribe is closing, stops the check with its footer written.</summary>
    [Test]
    public async Task A_cancellation_stops_the_check()
    {
        await StartAsync();

        _cancellation.Cancel();
        var exitCode = await _run!.WaitAsync(TimeSpan.FromSeconds(15));

        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(Lines[^3], Does.Contain("The microphone check stopped: cancelled."));
    }

    /// <summary>
    /// Run as the runner runs it (<see cref="IMicCheckRun"/>), the check takes Ctrl+C from the signals of its scope, so one
    /// press stops it even without a keyboard, when no key is ever read: nothing is recorded, so the first press counts.
    /// </summary>
    [Test]
    public async Task Run_by_the_runner_one_Ctrl_C_through_the_signals_stops_the_check()
    {
        _ui = new FakeConsoleUi { IsInteractive = false };
        IMicCheckRun run = CreateCommand();
        _run = run.RunAsync(new MicCheckRequest(null, null));
        await Wait.UntilAsync(() => _window.Titles.Count >= 1 || _run.IsCompleted);

        _signals.CtrlCPressed();
        var exitCode = await _run.WaitAsync(TimeSpan.FromSeconds(15));

        Assert.Multiple(() =>
        {
            Assert.That(exitCode, Is.EqualTo(0));
            Assert.That(_signals.StopRequested.IsCompleted, Is.True, "nothing is recorded, so the first press must count");
            Assert.That(_signals.CtrlCPresses, Is.EqualTo(1));
            Assert.That(_ui.KeyReads, Is.EqualTo(0));
            Assert.That(Lines[^3], Does.Contain("The microphone check stopped: cancelled."));
        });
    }

    /// <summary>
    /// A reader that throws, which the readers never should, gives an ERROR line once, not at every look, and the check goes
    /// on: the other reader is still read, and a NOTE says when the failed one reads again. The failure is noted in the log
    /// of the app at Debug, four looks a second being too many for more.
    /// </summary>
    [Test]
    public async Task A_reader_that_throws_gives_an_ERROR_line_once_and_the_check_goes_on()
    {
        await StartAsync();

        _sessions.Failure = new InvalidOperationException("the audio service is not running");
        await TickAsync();
        await TickAsync();
        _record.Problem = "SecurityException: Requested registry access is not allowed.";
        await TickAsync();
        _sessions.Failure = null;
        _record.Problem = null;
        await TickAsync();
        await Wait.UntilAsync(() => _ui.Screen.Contains("NOTE     Windows' microphone usage record can be read again.", StringComparison.Ordinal));

        var error = "ERROR    Windows' list of microphones could not be read: InvalidOperationException: the audio service is not running. The check keeps trying.";
        Assert.That(Lines.Count(line => line.EndsWith(error, StringComparison.Ordinal)), Is.EqualTo(1));
        Assert.That(Lines, Has.Some.Matches<string>(line => line.EndsWith("ERROR    Windows' microphone usage record could not be read: SecurityException: Requested registry access is not allowed. The check keeps trying.", StringComparison.Ordinal)));
        Assert.That(Lines, Has.Some.Matches<string>(line => line.EndsWith("NOTE     Windows' list of microphones can be read again.", StringComparison.Ordinal)));
        Assert.That(Lines, Has.Some.Matches<string>(line => line.EndsWith("NOTE     Windows' microphone usage record can be read again.", StringComparison.Ordinal)));
        Assert.That(_record.Reads, Is.EqualTo(5), "the record was read at every look, the failed ones included");
        Assert.That(_log.Problems, Is.Empty, "noted at Debug, not as a problem of the app");
        Assert.That(_log.Messages, Has.Some.Contains("the audio service is not running"));
        Assert.That(await StopAsync(), Is.EqualTo(0));
    }

    /// <summary>
    /// A list of microphones that could not be read as a whole, which the reader says rather than throws (Windows' audio
    /// service restarting, a stale enumerator), is one ERROR line, not every microphone gone and every session gone: the
    /// log decides Recording:ClassAppMute, and a restart must not read as the app letting go of the microphone. The next
    /// look that reads the list adds the NOTE, and nothing is said to have come back.
    /// </summary>
    [Test]
    public async Task A_list_that_could_not_be_read_is_one_ERROR_line_and_not_every_microphone_gone()
    {
        await StartAsync();
        _sessions.SetSessions(Laptop.ArrayId, Laptop.Session(Laptop.ArrayId, Laptop.ChromeUtility));
        await TickAsync();
        await Wait.UntilAsync(() => _ui.Screen.Contains("appeared", StringComparison.Ordinal));

        _sessions.ListingProblem = "The audio service is not running (0x88890010)";
        await TickAsync();
        await TickAsync();
        _sessions.ListingProblem = null;
        await TickAsync();
        await Wait.UntilAsync(() => _ui.Screen.Contains("NOTE     Windows' list of microphones can be read again.", StringComparison.Ordinal));

        var error = "ERROR    Windows' list of microphones could not be read: The audio service is not running (0x88890010). The check keeps trying.";
        Assert.That(Lines.Count(line => line.EndsWith(error, StringComparison.Ordinal)), Is.EqualTo(1));
        Assert.That(Lines, Has.None.Contains("no longer active"));
        Assert.That(Lines, Has.None.Contains("gone (Windows no longer lists it)"));
        Assert.That(Lines, Has.None.Contains("became active"));
        Assert.That(Lines.Count(line => line.Contains("appeared", StringComparison.Ordinal)), Is.EqualTo(1), "the session is the one seen before, not a new one");
        Assert.That(await StopAsync(), Is.EqualTo(0));
    }

    /// <summary>A gap of two seconds or more between two looks, as after a sleep, is a NOTE, so that a change that came and went meanwhile is known to be missing.</summary>
    [Test]
    public async Task A_gap_between_looks_is_noted()
    {
        await StartAsync();

        await TickAsync();
        await TickAsync(TimeSpan.FromSeconds(3));

        Assert.That(Lines.Count(line => line.Contains("NOTE     no check for", StringComparison.Ordinal)), Is.EqualTo(1));
        Assert.That(Lines, Has.Some.Matches<string>(line => line.EndsWith("NOTE     no check for 3.0 s: the laptop slept or the check was held up, so a change that came and went in that time is not in the log.", StringComparison.Ordinal)));
    }

    /// <summary>A change seen at a look is written to the window and to the file alike.</summary>
    [Test]
    public async Task A_change_is_written_to_the_window_and_the_file()
    {
        await StartAsync();

        _sessions.SetSessions(Laptop.ArrayId, Laptop.Session(Laptop.ArrayId, Laptop.ChromeUtility));
        await TickAsync();
        await StopAsync();

        var line = $"SESSION  #1 chrome (pid {Laptop.ChromeUtility}) on {Laptop.ArrayName}: appeared: state: Active; session muted: no; program {Laptop.Names[Laptop.ChromeUtility].Path}";
        Assert.That(_ui.Screen, Does.Contain(line));
        Assert.That(File.ReadAllText(LogPath), Does.Contain(line));
    }

    /// <summary>The window's title shows the check, how long it has run, how many lines it has written and that Q stops it, kept up every second.</summary>
    [Test]
    public async Task The_title_shows_the_check_and_is_kept_up()
    {
        await StartAsync();
        Assert.That(_window.Title, Is.EqualTo($"MIC CHECK 00:00:00, 3 lines  Q stops | {TitleText.AppName}"));

        for (var tick = 0; tick < 4; tick++) await TickAsync();
        _ui.Type('m');
        await Wait.UntilAsync(() => _ui.Screen.Contains("MARK", StringComparison.Ordinal));
        for (var tick = 0; tick < 4; tick++) await TickAsync();
        await Wait.UntilAsync(() => _window.Titles.Count >= 3);

        Assert.That(_window.Titles, Is.EqualTo(new[]
        {
            $"MIC CHECK 00:00:00, 3 lines  Q stops | {TitleText.AppName}",
            $"MIC CHECK 00:00:01, 3 lines  Q stops | {TitleText.AppName}",
            $"MIC CHECK 00:00:02, 4 lines  Q stops | {TitleText.AppName}"
        }));
        Assert.That(_window.Flashes, Is.EqualTo(0), "a check never flashes the window");
    }

    /// <summary>
    /// Ticks that piled up while a look was held, as after a sleep or a reader slow to answer, are one look, not a burst:
    /// the one look notes the gap, and the next tick is the next look.
    /// </summary>
    [Test]
    public async Task Ticks_that_piled_up_while_a_look_was_held_are_one_look()
    {
        await StartAsync();
        using var released = new ManualResetEventSlim(false);
        _sessions.Failure = new InvalidOperationException("held for the test");
        _log.HoldNext("Windows' list of microphones could not be read", released);
        _time.Advance(MicCheckCommand.Interval);
        await Wait.UntilAsync(() => _log.Held);

        for (var tick = 0; tick < 3; tick++) _time.Advance(TimeSpan.FromSeconds(1));
        _sessions.Failure = null;
        released.Set();
        await Wait.UntilAsync(() => _record.Reads >= 3);
        await TickAsync();

        Assert.That(_record.Reads, Is.EqualTo(4), "the held look, one for the three ticks that piled up, and one for the tick after");
        Assert.That(Lines.Count(line => line.Contains("NOTE     no check for", StringComparison.Ordinal)), Is.EqualTo(1));
        Assert.That(Lines, Has.Some.Matches<string>(line => line.Contains("NOTE     no check for 3.0 s:", StringComparison.Ordinal)));
    }

    /// <summary>A key typed before the check started, while nothing read the keyboard, is dropped: a Q from before does not stop the check.</summary>
    [Test]
    public async Task A_key_typed_before_the_check_started_is_dropped()
    {
        _ui.TypeAhead('q');

        await StartAsync();
        await TickAsync();

        Assert.That(_run!.IsCompleted, Is.False, "a Q from before the check is no Q to stop it");
        Assert.That(_record.Reads, Is.EqualTo(2));
        Assert.That(await StopAsync(), Is.EqualTo(0));
    }

    /// <summary>
    /// A failure of the loop itself, which the readers' lines never are, is logged as an error in the app's log and written
    /// into the check's file, so that it shows in what Gulzar sends, and rethrown; ClassScribe's own title is restored.
    /// </summary>
    [Test]
    public async Task A_failure_of_the_loop_is_logged_in_both_logs_and_rethrown()
    {
        var run = CreateCommand(new WindowWhoseTitleFailsOnce(_window, new InvalidOperationException("the window is gone")))
            .RunAsync(new MicCheckRequest(null, null), _cancellation.Token);

        var failure = Assert.ThrowsAsync<InvalidOperationException>(() => run);

        Assert.That(failure!.Message, Is.EqualTo("the window is gone"));
        Assert.That(_log.Problems, Is.EqualTo(new[] { "The microphone check failed after 1 look" }));
        Assert.That(File.ReadAllText(LogPath), Does.Match(@"\d\d:\d\d:\d\d\.\d{3}  The microphone check failed after 1 look: System\.InvalidOperationException: the window is gone"));
        Assert.That(_window.Title, Is.EqualTo(TitleText.AppName), "ClassScribe's own title is restored");
        Assert.That(_ui.Screen, Does.Not.Contain("The microphone check stopped:"), "no footer: the check did not stop, it failed");
    }

    /// <summary>A window whose first title cannot be set, as a loop failing on its own would see it; every title after that reaches <paramref name="window"/>.</summary>
    /// <param name="window">The window the titles after the first reach.</param>
    /// <param name="failure">What setting the first title throws.</param>
    private sealed class WindowWhoseTitleFailsOnce(FakeWindowSignals window, Exception failure) : IWindowSignals
    {
        private int _failed;

        /// <inheritdoc />
        public void SetTitle(string title)
        {
            if (Interlocked.Exchange(ref _failed, 1) == 0) throw failure;
            window.SetTitle(title);
        }

        /// <inheritdoc />
        public void SetTaskbar(TaskbarState state, int percent = 0) => window.SetTaskbar(state, percent);

        /// <inheritdoc />
        public void Flash() => window.Flash();
    }

    /// <summary>The log goes to the folder given with --log-folder, read as --output is read, instead of the mic-check folder.</summary>
    [Test]
    public async Task A_folder_given_takes_the_log()
    {
        await StartAsync(new MicCheckRequest(null, _root.File("elsewhere")));
        await StopAsync();

        Assert.That(LogPath, Does.StartWith(Path.Combine(_root.File("elsewhere"), "mic-check_")));
        Assert.That(File.Exists(LogPath), Is.True);
    }

    /// <summary>A log that cannot be created, as when a file stands where its folder should be, is reported and exit 1, and nothing is looked at.</summary>
    [Test]
    public async Task A_log_that_cannot_be_created_is_exit_1()
    {
        File.WriteAllText(_root.File("blocked"), "");

        _run = CreateCommand().RunAsync(new MicCheckRequest(null, _root.File("blocked")), _cancellation.Token);
        var exitCode = await _run.WaitAsync(TimeSpan.FromSeconds(15));

        Assert.That(exitCode, Is.EqualTo(1));
        Assert.That(_log.Problems, Has.Exactly(1).Matches<string>(message => message.StartsWith($"The microphone check cannot create its log in {_root.File("blocked")}:", StringComparison.Ordinal)));
        Assert.That(_sessions.ReadAlls, Is.EqualTo(0));
        Assert.That(_window.Titles, Is.Empty);
    }

    /// <summary>Without a keyboard, as with the input redirected, no key is read and --seconds or a cancellation ends the check.</summary>
    [Test]
    public async Task Without_a_keyboard_the_check_runs_until_seconds()
    {
        _ui = new FakeConsoleUi { IsInteractive = false };
        await StartAsync(new MicCheckRequest(TimeSpan.FromMilliseconds(500), null));

        await TickAsync();
        await TickAsync();
        var exitCode = await _run!.WaitAsync(TimeSpan.FromSeconds(15));

        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(_ui.KeyReads, Is.EqualTo(0));
    }
}
