using System.Globalization;
using System.Runtime.Versioning;
using ClassScribe.Audio;
using ClassScribe.Audio.Microphones;
using ClassScribe.Commands;
using ClassScribe.Recorder;
using ClassScribe.Runs;
using ClassScribe.Settings;
using ClassScribe.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClassScribe.Tests;

/// <summary>
/// The loop that runs while a class records: the keys (Q twice, P, M, V and Esc), Ctrl+C twice, a state for the window
/// each second, the run file's heartbeat every 10 seconds, and what the checks made once a second find, acted on: lines,
/// notes, a capture reopened, a stop by itself. The copies of the record command's loop tests drive a real recording of
/// the class over simulated devices, as those did; the other tests drive a recording described by hand. The clock and the
/// keyboard are the test's, and the run file is a real one, in a folder of the test's own.
/// </summary>
[TestFixture]
public partial class RecordingLoopTests
{
    /// <summary>What the log says when a second's work fails.</summary>
    private const string TickFailure = "Could not check on the recording; it goes on, and the next second checks again";

    /// <summary>What the log says when acting on a key fails.</summary>
    private const string KeyFailure = "Something failed while acting on a key; the recording goes on";

    /// <summary>What the log says when the state the run stops in cannot be shown.</summary>
    private const string StopFailure = "Could not show that the recording is stopping; it stops all the same";

    private static readonly AudioFormat Pcm16KMono = new(16000, 1, 16, false);

    private TestRuntime _runtime = null!;
    private TempFolder _folder = null!;
    private ManualTimeProvider _time = null!;
    private FakeSystemTimes _times = null!;
    private FakeCaptureProvider _provider = null!;
    private FakeAudioCapture _capture = null!;
    private FakeUserSignals _signals = null!;
    private FakeConsoleUi _ui = null!;
    private FakeStatusSink _status = null!;
    private FakeFreeSpace _space = null!;
    private FakeRegistryValues _registry = null!;
    private FakeMicrophoneProvider _microphones = null!;
    private RecordingLogger<RecordingLoop> _log = null!;
    private RecordingFiles _files = null!;
    private RecordRequest _request = null!;
    private RunHandle _run = null!;
    private List<IRecording> _recordings = null!;
    private Task<RecorderState>? _loop;
    private IUserSignals _loopSignals = null!;

    /// <summary>
    /// Makes ClassScribe's own folder with a run file recording "Kalam" in the background, the recording's folder, a
    /// simulated device with a capture ready, the keyboard, the signals, the window's states, room for any class on the
    /// disk, and a log to inspect. The clock stands at the tests' class until a test moves it.
    /// </summary>
    [SetUp]
    public void SetUp()
    {
        _runtime = new TestRuntime();
        _folder = new TempFolder();
        _time = _runtime.Time;
        _times = new FakeSystemTimes(_time);
        _provider = new FakeCaptureProvider();
        _capture = _provider.Enqueue(new FakeAudioCapture(Pcm16KMono));
        _signals = new FakeUserSignals();
        _loopSignals = _signals;
        _ui = new FakeConsoleUi();
        _status = new FakeStatusSink();
        _space = new FakeFreeSpace();
        _registry = new FakeRegistryValues()
            .Set(MicrophoneAdvice.VantageKey, MicrophoneAdvice.VantageValue, "0")
            .Set(MicrophoneAdvice.DuckingKey, MicrophoneAdvice.DuckingValue, 3);
        _microphones = new FakeMicrophoneProvider();
        _log = new RecordingLogger<RecordingLoop>();
        _files = new RecordingFiles(_folder.Path, "Kalam_2026-10-15_23-00-02Z");
        var runId = TestRuns.Id();
        _request = TestRuns.Request(runId);
        _run = _runtime.Journal.Begin(TestRuns.Record(runId, RunState.Recording));
        _recordings = [];
        _loop = null;
    }

    /// <summary>Ends a loop a test left running, stops the recordings so that no file stays open, and deletes the folders.</summary>
    [TearDown]
    public async Task TearDown()
    {
        _loopSignals.RequestStop();
        if (_loop is not null) await _loop.WaitAsync(TimeSpan.FromSeconds(15));
        foreach (var recording in _recordings)
        {
            recording.Stop();
            recording.Dispose();
        }

        _run.Dispose();
        _folder.Dispose();
        _runtime.Dispose();
    }

    /// <summary>The settings the loop's tests use: a status line every second, which the copies of the record command's tests wait for.</summary>
    private static RecordingOptions Settings(int silenceWarningSeconds = 60, double stopAfterHours = 9) =>
        new() { StatusIntervalSeconds = 1, SilenceWarningSeconds = silenceWarningSeconds, StopAfterHours = stopAfterHours };

    /// <summary>
    /// The loop under test, with the checks made once a second on the test's clocks, free space and <paramref name="settings"/>,
    /// a registry with Vantage off and ducking set to Do nothing, the simulated output devices, and the simulated
    /// microphones for the Bluetooth check.
    /// </summary>
    private RecordingLoop CreateLoop(TestOptions<RecordingOptions> settings, IUserSignals signals) =>
        new(_ui, signals, new ClassMarks(NullLogger<ClassMarks>.Instance),
            new RecordingMonitor(new FreezeWatch(_times, _time, NullLogger<FreezeWatch>.Instance), _space, settings,
                new MicrophoneAdvice(_registry, NullLogger<MicrophoneAdvice>.Instance), _provider,
                new BluetoothMicrophoneWatch(new FakeMicrophoneSessions(), _microphones, new FakeOwnProcesses(), _time, NullLogger<BluetoothMicrophoneWatch>.Instance), _time),
            _status, _time, _log);

    /// <summary>
    /// Records the class from <paramref name="source"/> (the device it plays on, unless given) with a real session over the
    /// simulated devices, starts the loop on it, and waits until the loop runs.
    /// </summary>
    private async Task<IRecording> StartAsync(RecordingOptions? settings = null, ClassSource? source = null)
    {
        var options = TestOptions.Create(settings ?? Settings());
        var recording = new ClassRecordingFactory(_provider, new WavTrackWriterFactory(), options, _time, NullLogger<RecordingSession>.Instance, NullLogger<CaptureChooser>.Instance,
            NullLogger<ClassRecording>.Instance).Create(new SourcePlan(RecordingMode.Class, source ?? new ClassSource.Automatic(), Microphone: null));
        _recordings.Add(recording);
        recording.Start(_files, TimeSpan.Zero);
        await RunAsync(recording, options);
        return recording;
    }

    /// <summary>
    /// Starts the loop on <paramref name="recording"/>, with <paramref name="signals"/> (the test's stand-in unless given)
    /// and <paramref name="request"/> (the tests' request unless given), and waits until it has published its first state.
    /// The loop gets the tests' run file, unless <paramref name="withRunFile"/> is false: then it gets none, as a run whose
    /// file cannot be written.
    /// </summary>
    private async Task RunAsync(IRecording recording, TestOptions<RecordingOptions>? settings = null, IUserSignals? signals = null, RecordRequest? request = null,
        bool withRunFile = true)
    {
        _loopSignals = signals ?? _signals;
        var published = _status.Count;
        _loop = CreateLoop(settings ?? TestOptions.Create(Settings()), _loopSignals).RunAsync(recording, _files, request ?? _request, withRunFile ? _run : null, TimeSpan.Zero);
        await Wait.UntilAsync(() => _status.Count > published || _loop.IsCompleted);
    }

    /// <summary>Asks for the stop, as a stop from elsewhere does, and waits for the loop to end; returns the state it ended with.</summary>
    private async Task<RecorderState> StopAsync()
    {
        _loopSignals.RequestStop();
        return await _loop!.WaitAsync(TimeSpan.FromSeconds(15));
    }

    /// <summary>Stops the loop and the recordings, so that their files are finished and can be read.</summary>
    private async Task FinishAsync()
    {
        await StopAsync();
        foreach (var recording in _recordings) recording.Stop();
    }

    /// <summary>Moves the clock on one second at a time, each time waiting until the loop has published that second's state.</summary>
    private async Task TickAsync(int seconds = 1)
    {
        for (var i = 0; i < seconds; i++)
            await JumpAsync(TimeSpan.FromSeconds(1));
    }

    /// <summary>Moves the clock on by <paramref name="step"/> at once, then waits until the loop has published its state for that moment.</summary>
    private async Task JumpAsync(TimeSpan step)
    {
        var published = _status.Count;
        _time.Advance(step);
        await Wait.UntilAsync(() => _status.Count > published);
    }

    /// <summary>The laptop sleeps for <paramref name="duration"/>; waits until the loop has published its state on waking.</summary>
    private async Task SleepAsync(TimeSpan duration)
    {
        var published = _status.Count;
        _times.Sleep(duration);
        await Wait.UntilAsync(() => _status.Count > published);
    }

    /// <summary>
    /// Sets what Windows' meter shows for <paramref name="deviceId"/>, has the session look at the devices at once, as a
    /// device notification would, and waits until that look has read the level, so that the loop's next tick sees it.
    /// </summary>
    private async Task SetLevelAsync(IRecording recording, string deviceId, float level)
    {
        _provider.SetLevel(deviceId, level);
        _provider.RaiseDevicesChanged();
        await Wait.UntilAsync(() => recording.GetSnapshot().Main.RecordedLevel == level);
    }

    /// <summary>A capture of an app's sound, which like Windows' process loopback hands over audio all the time, silence included.</summary>
    private static FakeAudioCapture AppCapture(int processId) => new(Pcm16KMono, "chrome", $"process:{processId}") { DeliversContinuously = true };

    /// <summary>How many times the loop has asked for a second Q.</summary>
    private int SecondQRequests() => _log.Messages.Count(message => message == "Press Q again within 5 seconds to stop");

    /// <summary>How many status lines the loop has logged.</summary>
    private int StatusLines() => _log.Messages.Count(message => message.StartsWith("Recording: ", StringComparison.Ordinal));

    /// <summary>The silence warnings given so far, each cut to its first sentence, such as "No sound for 60 seconds".</summary>
    private string[] SilenceWarnings() => [.. _log.Problems.Where(problem => problem.StartsWith("No sound for ", StringComparison.Ordinal)).Select(problem => problem.Split('.')[0])];

    /// <summary>The lines about a time ClassScribe did not run, given so far.</summary>
    private string[] FreezeLines() => [.. _log.Problems.Where(problem => problem.EndsWith("; nothing was recorded then", StringComparison.Ordinal))];

    /// <summary>The warnings that audio has stopped arriving, given so far.</summary>
    private string[] ArrivalWarnings() => [.. _log.Problems.Where(problem => problem.StartsWith("No audio has arrived from ", StringComparison.Ordinal))];

    /// <summary>The levels at which <paramref name="message"/> was logged, in order.</summary>
    private LogLevel[] LevelsOf(string message) => [.. _log.Entries.Where(entry => entry.Message == message).Select(entry => entry.Level)];

    /// <summary>A moment as the freeze lines give it: the local time of day, to the second.</summary>
    private static string Local(DateTimeOffset moment) =>
        TimeZoneInfo.ConvertTime(moment, TimeZoneInfo.Local).ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    // ------------------------------------------------------------------ copied from the record command's tests: Q, P, M

    /// <summary>Q pressed twice stops the recording, which the run file puts down to Gulzar; the loop ends with the recording stopping.</summary>
    [Test]
    public async Task Q_pressed_twice_stops_the_recording()
    {
        await StartAsync();
        _capture.Deliver(TestAudio.Shorts(1000, -1000));
        _ui.Type('q', 'q');

        var last = await _loop!.WaitAsync(TimeSpan.FromSeconds(15));

        Assert.That(last.Phase, Is.EqualTo(RecorderPhase.Stopping));
        Assert.That(_signals.StopRequested.IsCompleted, Is.True, "the stop is asked for, so that what waits for it sees it");
        Assert.That(_run.Current.EndedBy, Is.EqualTo(RunEndCause.Q));
    }

    /// <summary>A single Q, typed into the window by mistake, only asks for a second one: the recording goes on.</summary>
    [Test]
    public async Task A_single_Q_does_not_stop_the_recording()
    {
        await StartAsync();
        _capture.Deliver(TestAudio.Shorts(1000, -1000));
        _ui.Type('q');
        await Wait.UntilAsync(() => SecondQRequests() == 1);
        _capture.Deliver(TestAudio.Shorts(-7, 7));
        Assert.That(_loop!.IsCompleted, Is.False, "still recording");

        await FinishAsync();

        Assert.That(TestAudio.ReadWav(_files.Segment(1)).Samples, Is.EqualTo(new short[] { 1000, -1000, -7, 7 }), "the audio after the Q was kept");
    }

    /// <summary>A Q more than five seconds after the first one counts as a first press again; another Q right after it stops.</summary>
    [Test]
    public async Task A_Q_more_than_five_seconds_after_the_first_counts_as_a_first_press()
    {
        await StartAsync();
        _capture.Deliver(TestAudio.Shorts(1000, -1000));
        _ui.Type('q');
        await Wait.UntilAsync(() => SecondQRequests() == 1);

        _time.Advance(TimeSpan.FromSeconds(6));
        _ui.Type('q');
        await Wait.UntilAsync(() => SecondQRequests() == 2);
        Assert.That(_loop!.IsCompleted, Is.False, "still recording");

        _time.Advance(TimeSpan.FromSeconds(4));
        _ui.Type('q');
        Assert.That((await _loop.WaitAsync(TimeSpan.FromSeconds(15))).Phase, Is.EqualTo(RecorderPhase.Stopping));
    }

    /// <summary>
    /// The five seconds between two Qs are the time that has really passed, whatever Windows does to its clock: set back
    /// an hour between two Qs ten seconds apart, it does not make them stop the recording, and set forward an hour between
    /// two quick ones, it does not keep them from stopping it.
    /// </summary>
    [Test]
    public async Task Two_Qs_are_timed_by_the_time_that_has_really_passed()
    {
        await StartAsync();
        _capture.Deliver(TestAudio.Shorts(1000, -1000));
        _ui.Type('q');
        await Wait.UntilAsync(() => SecondQRequests() == 1);

        _time.StepWallClock(TimeSpan.FromHours(-1));
        _time.Advance(TimeSpan.FromSeconds(10));
        _ui.Type('q');
        await Wait.UntilAsync(() => SecondQRequests() == 2);
        Assert.That(_loop!.IsCompleted, Is.False, "still recording: the second Q came ten seconds after the first");

        _time.StepWallClock(TimeSpan.FromHours(1));
        _ui.Type('q');
        Assert.That((await _loop.WaitAsync(TimeSpan.FromSeconds(15))).Phase, Is.EqualTo(RecorderPhase.Stopping));
    }

    /// <summary>P pauses and resumes: audio that arrives during the pause is not kept, and the window sees each at once.</summary>
    [Test]
    public async Task P_pauses_and_resumes_the_recording()
    {
        await StartAsync();
        _capture.Deliver(TestAudio.Shorts(1000, -1000));
        _ui.Type('p');
        await Wait.UntilAsync(() => _status.Last?.Phase == RecorderPhase.Paused);
        _capture.Deliver(TestAudio.Shorts(5, 5));
        _ui.Type('p');
        await Wait.UntilAsync(() => _status.Last?.Phase == RecorderPhase.Recording);
        _capture.Deliver(TestAudio.Shorts(-7, 7));

        await FinishAsync();

        Assert.That(TestAudio.ReadWav(_files.Segment(1)).Samples, Is.EqualTo(new short[] { 1000, -1000, -7, 7 }));
        Assert.That(_log.Messages, Has.Some.EqualTo("Paused. Nothing is recorded until you press P again").And.Some.EqualTo("Resumed"));
    }

    /// <summary>M marks the moment: its position goes to the marks file at once, and the line says which mark it is and where.</summary>
    [Test]
    public async Task M_marks_the_moment_in_the_recording()
    {
        await StartAsync();
        _capture.Deliver(TestAudio.Shorts(TestAudio.Tone(16000, seconds: 0.5, frequency: 440, amplitude: 0.5)));
        _ui.Type('m');
        await Wait.UntilAsync(() => File.Exists(_files.Marks) && _log.Messages.Any(message => message.StartsWith("Mark 1", StringComparison.Ordinal)));

        await FinishAsync();

        Assert.That(File.ReadAllLines(_files.Marks), Is.EqualTo(new[] { "00:00:00.500" }));
        Assert.That(_log.Messages, Has.Some.EqualTo("Mark 1 at 00:00:00"));
    }

    /// <summary>Positions a day or more into the recording, in the status line and in the line for a mark, show all their hours.</summary>
    [Test]
    public async Task Positions_a_day_or_more_into_the_recording_show_all_their_hours()
    {
        _provider = new FakeCaptureProvider();
        _capture = _provider.Enqueue(new FakeAudioCapture(new AudioFormat(1, 1, 16, false)));   // one sample a second
        await StartAsync(Settings(stopAfterHours: 0));
        _capture.Deliver(TestAudio.Shorts(new short[25 * 3600]));
        await TickAsync();
        _ui.Type('m');
        await Wait.UntilAsync(() => _log.Messages.Any(message => message.StartsWith("Mark 1 at", StringComparison.Ordinal)));
        await FinishAsync();

        Assert.That(_log.Messages, Has.Some.EqualTo("Recording: 25:00:00 captured from \"Fake Speakers\""));
        Assert.That(_log.Messages, Has.Some.EqualTo("Mark 1 at 25:00:00"));
    }

    // ------------------------------------------------------------------ copied: the silence warning and the status line

    /// <summary>
    /// A minute without sound brings one warning, and while the silence lasts it comes back once a minute, not more. It is
    /// the only sign that the class plays on a device that is not recorded.
    /// </summary>
    [Test]
    public async Task Silence_is_warned_about_once_a_minute()
    {
        await StartAsync();

        await TickAsync(59);
        Assert.That(SilenceWarnings(), Is.Empty, "not before a minute has passed");
        await TickAsync(1);
        Assert.That(SilenceWarnings(), Is.EqualTo(new[] { "No sound for 60 seconds" }));
        await TickAsync(90);
        await StopAsync();

        Assert.That(SilenceWarnings(), Is.EqualTo(new[] { "No sound for 60 seconds", "No sound for 120 seconds" }));
    }

    /// <summary>The silence warning gives its length in words, here for a second without sound, with a limit of one second.</summary>
    [Test]
    public async Task The_silence_warning_gives_its_length_in_words()
    {
        await StartAsync(Settings(silenceWarningSeconds: 1));

        await TickAsync(1);
        await StopAsync();

        Assert.That(SilenceWarnings(), Is.EqualTo(new[] { "No sound for 1 second" }));
    }

    /// <summary>
    /// After a sleep longer than the silence limit, the silence warning does not blame the device or Windows' sound for the
    /// time asleep: the first tick after waking gives the sleep line alone. The silence counts from waking, as the wait for
    /// audio does, so the warning comes once the class has been silent for the limit since then.
    /// </summary>
    [Test]
    public async Task After_a_sleep_the_silence_counts_from_waking()
    {
        await StartAsync();
        for (var second = 0; second < 3; second++)
        {
            _capture.Deliver(TestAudio.Shorts(1000, -1000));
            await TickAsync(1);
        }

        await SleepAsync(TimeSpan.FromMinutes(2));
        Assert.That(FreezeLines(), Has.Length.EqualTo(1).And.All.StartWith("The laptop slept from "));
        Assert.That(SilenceWarnings(), Is.Empty, "the time asleep is no silence of the class");

        await TickAsync(59);
        Assert.That(SilenceWarnings(), Is.Empty, "not before a minute without sound since waking");
        await TickAsync(1);
        await StopAsync();

        Assert.That(SilenceWarnings(), Is.EqualTo(new[] { "No sound for 60 seconds" }));
    }

    /// <summary>
    /// Windows setting its clock back or forward an hour neither stops nor hurries the status line or the silence warning:
    /// the status line still comes every second, and the warning once a minute.
    /// </summary>
    [TestCase(-60)]
    [TestCase(60)]
    public async Task A_change_of_the_time_of_day_does_not_disturb_the_status_line_or_the_silence_warning(int minutes)
    {
        await StartAsync();
        await TickAsync(60);
        Assert.That(SilenceWarnings(), Is.EqualTo(new[] { "No sound for 60 seconds" }));

        _time.StepWallClock(TimeSpan.FromMinutes(minutes));
        var lines = StatusLines();
        await TickAsync(59);
        Assert.That(StatusLines(), Is.EqualTo(lines + 59), "a status line every second");
        Assert.That(SilenceWarnings(), Is.EqualTo(new[] { "No sound for 60 seconds" }), "not again before another minute has passed");
        await TickAsync(1);
        await StopAsync();

        Assert.That(SilenceWarnings(), Is.EqualTo(new[] { "No sound for 60 seconds", "No sound for 120 seconds" }));
    }

    /// <summary>While paused there is no warning about silence, and the status line says the recording is paused.</summary>
    [Test]
    public async Task No_silence_warning_is_given_while_paused()
    {
        await StartAsync();
        _ui.Type('p');
        await Wait.UntilAsync(() => _status.Last?.Phase == RecorderPhase.Paused);

        await TickAsync(61);
        await StopAsync();

        Assert.That(_log.Messages, Has.Some.Contains("(paused)"));
        Assert.That(_log.Problems.Where(problem => problem.StartsWith("No sound", StringComparison.Ordinal)), Is.Empty);
    }

    /// <summary>While nothing can be recorded yet, the status line says what is being waited for, and there is no warning about silence.</summary>
    [Test]
    public async Task While_waiting_for_the_source_the_status_says_so()
    {
        _provider.Enqueue(new FakeAudioCapture(Pcm16KMono, "Speakers"));
        await StartAsync(source: new ClassSource.Device("Pixel Buds"));

        await TickAsync(61);
        await StopAsync();

        Assert.That(_log.Messages, Has.Some.Contains("waiting for the output device \"Pixel Buds\""));
        Assert.That(_log.Problems.Where(problem => problem.StartsWith("No sound", StringComparison.Ordinal)), Is.Empty);
    }

    // ------------------------------------------------------------------ copied: a time ClassScribe did not run

    /// <summary>
    /// 18 seconds in which ClassScribe did not run gives one line, from the last tick before to the first tick after, in
    /// local time: worded as a sleep when the laptop slept, and as a hold-up when it stayed awake. The ticks after it, a
    /// second apart again, give no more.
    /// </summary>
    [TestCase(true, "The laptop slept from {0} to {1}; nothing was recorded then")]
    [TestCase(false, "ClassScribe was held up from {0} to {1} (Windows paused it); nothing was recorded then")]
    public async Task A_freeze_gives_one_line_worded_as_a_sleep_or_a_hold_up(bool slept, string line)
    {
        await StartAsync();
        _capture.Deliver(TestAudio.Shorts(1000, -1000));
        await TickAsync(1);

        if (slept)
            await SleepAsync(TimeSpan.FromSeconds(18));
        else
            await JumpAsync(TimeSpan.FromSeconds(18));
        await TickAsync(10);
        await StopAsync();

        Assert.That(FreezeLines(), Is.EqualTo(new[] { string.Format(CultureInfo.InvariantCulture, line, Local(TestRuns.Start.AddSeconds(1)), Local(TestRuns.Start.AddSeconds(19))) }));
    }

    /// <summary>A class during which ClassScribe ran all along, ticking a second apart, gives no such line.</summary>
    [Test]
    public async Task Ticks_a_second_apart_give_no_freeze_line()
    {
        await StartAsync();
        _capture.Deliver(TestAudio.Shorts(1000, -1000));

        await TickAsync(30);
        await StopAsync();

        Assert.That(FreezeLines(), Is.Empty);
    }

    /// <summary>
    /// The loop held up by its own work, as when the recording's lock waits for a slow disk to save, is not taken for a
    /// time ClassScribe did not run: the freeze watch goes on ticking on a timer of its own meanwhile. Here the loop is
    /// held for 5 seconds as it logs a status line, while the rest of ClassScribe runs on.
    /// </summary>
    [Test]
    public async Task The_loop_held_up_by_its_own_work_is_not_taken_for_a_freeze()
    {
        await StartAsync();
        _capture.Deliver(TestAudio.Shorts(1000, -1000));
        await TickAsync(1);

        using (var release = new ManualResetEventSlim())
        {
            _log.HoldNext("Recording: ", release);

            // The loop's next second ends on a thread of its own, on which the loop then runs and is held.
            var nextSecond = Task.Run(() => _time.Advance(TimeSpan.FromSeconds(1)));
            await Wait.UntilAsync(() => _log.Held);
            for (var second = 0; second < 5; second++) _time.Advance(TimeSpan.FromSeconds(1));
            release.Set();
            await nextSecond.WaitAsync(TimeSpan.FromSeconds(15));
        }

        await TickAsync(3);
        await StopAsync();

        Assert.That(FreezeLines(), Is.Empty);
    }

    /// <summary>
    /// A second that ends while the loop handles a key is not lost: the loop waits for the same second across a key press,
    /// so its tick, and the status line, still come for it. Here the second ends after the loop has handled P and before
    /// it has begun to wait again, while it starts to read the next key.
    /// </summary>
    [Test]
    public async Task A_second_that_ends_while_a_key_is_handled_is_not_lost()
    {
        await StartAsync();

        using (var release = new ManualResetEventSlim())
        {
            _ui.HoldNextRead(release);
            _ui.Type('p');
            await Wait.UntilAsync(() => _ui.ReadHeld);
            var reported = StatusLines();
            _time.Advance(TimeSpan.FromSeconds(1));
            release.Set();

            await Wait.UntilAsync(() => StatusLines() > reported);
        }

        await StopAsync();
        Assert.That(_log.Messages, Has.Some.StartsWith("Paused"));
    }

    // ------------------------------------------------------------------ copied: audio that stops arriving

    /// <summary>
    /// An app's sound, which arrives all the time, silence included, that stops arriving for 5 seconds is warned about, and
    /// its capture is reopened. The new capture stays silent too, but the warning and the reopening come again only 30
    /// seconds after the last.
    /// </summary>
    [Test]
    public async Task A_continuous_source_that_hands_over_nothing_for_5_seconds_is_warned_about_and_reopened_at_most_once_per_30_seconds()
    {
        var first = _provider.AddApp(4242, "chrome", "Speakers", level: 0.3f, AppCapture(4242))!;
        var second = _provider.AddApp(4242, "chrome", "Speakers", level: 0.3f, AppCapture(4242))!;   // ready for each reopening
        var third = _provider.AddApp(4242, "chrome", "Speakers", level: 0.3f, AppCapture(4242))!;
        await StartAsync(source: new ClassSource.App("chrome"));
        await Wait.UntilAsync(() => first.Started);

        await TickAsync(4);
        Assert.That(ArrivalWarnings(), Is.Empty, "not before 5 seconds");
        await TickAsync(1);
        Assert.That(ArrivalWarnings(), Is.EqualTo(new[]
        {
            "No audio has arrived from \"chrome\" for 5 seconds although it should; the recording has fallen behind the clock. Reopening it"
        }));
        await TickAsync(1);
        await Wait.UntilAsync(() => second.Started);
        Assert.That(first.Disposed, Is.True, "the stalled capture is replaced");

        await TickAsync(28);
        Assert.That(ArrivalWarnings(), Has.Length.EqualTo(1), "not again within 30 seconds");
        await TickAsync(1);
        Assert.That(ArrivalWarnings(), Has.Length.EqualTo(2), "again 30 seconds after the first");
        await TickAsync(1);
        await Wait.UntilAsync(() => third.Started);
        await StopAsync();
    }

    /// <summary>
    /// While paused, an app's sound that stops arriving is reopened too, so that the recording can go on when the pause
    /// ends; the warning says so, rather than speak of a recording falling behind while nothing is being recorded.
    /// </summary>
    [Test]
    public async Task While_paused_a_continuous_source_that_stops_arriving_is_reopened_for_after_the_pause()
    {
        var first = _provider.AddApp(4242, "chrome", "Speakers", level: 0.3f, AppCapture(4242))!;
        var second = _provider.AddApp(4242, "chrome", "Speakers", level: 0.3f, AppCapture(4242))!;   // ready for the reopening
        await StartAsync(source: new ClassSource.App("chrome"));
        await Wait.UntilAsync(() => first.Started);
        _ui.Type('p');
        await Wait.UntilAsync(() => _status.Last?.Phase == RecorderPhase.Paused);

        await TickAsync(5);
        Assert.That(ArrivalWarnings(), Is.EqualTo(new[]
        {
            "No audio has arrived from \"chrome\" for 5 seconds although it should. Reopening it, so that the recording can go on when you press P again"
        }));
        await TickAsync(1);
        await Wait.UntilAsync(() => second.Started);

        await StopAsync();
        Assert.That(first.Disposed, Is.True, "the stalled capture is replaced during the pause");
    }

    /// <summary>
    /// A device whose meter has shown sound for 10 seconds while no audio arrived from its capture is warned about, and its
    /// capture is reopened: a device's capture hands over audio whenever something plays on it. The checks' first look
    /// comes at the start, so the meter has shown sound for 10 seconds at the tenth second.
    /// </summary>
    [Test]
    public async Task A_device_that_plays_for_10_seconds_while_no_audio_arrives_is_warned_about_and_reopened()
    {
        _provider.SetLevel("Fake Speakers", 0.5f);
        var reopened = _provider.Enqueue(new FakeAudioCapture(Pcm16KMono));   // the same device, ready for the reopening
        await StartAsync();

        await TickAsync(9);
        Assert.That(ArrivalWarnings(), Is.Empty, "not before the meter has shown sound for 10 seconds");
        await TickAsync(1);
        Assert.That(ArrivalWarnings(), Is.EqualTo(new[]
        {
            "No audio has arrived from \"Fake Speakers\" for 10 seconds although Windows shows sound playing there; the recording has fallen behind the clock. Reopening it"
        }));
        await TickAsync(1);
        await Wait.UntilAsync(() => reopened.Started);

        await StopAsync();
        Assert.That(_capture.Disposed, Is.True, "the stalled capture is replaced");
    }

    /// <summary>
    /// A device that falls quiet hands over nothing, and that is no stall: once its meter shows no sound, the time it showed
    /// sound before no longer counts, so a quiet stretch after the class has played is neither warned about nor reopened,
    /// however long it lasts.
    /// </summary>
    [Test]
    public async Task A_device_that_falls_quiet_is_not_warned_about()
    {
        _provider.SetLevel("Fake Speakers", 0.5f);
        _provider.Enqueue(new FakeAudioCapture(Pcm16KMono));   // the same device, ready for a reopening that must not come
        var recording = await StartAsync();
        for (var second = 0; second < 3; second++)
        {
            _capture.Deliver(TestAudio.Shorts(1000, -1000));
            await TickAsync(1);
        }

        await SetLevelAsync(recording, "Fake Speakers", 0f);   // the class falls quiet: nothing plays, so nothing arrives
        await TickAsync(40);
        await StopAsync();

        Assert.That(ArrivalWarnings(), Is.Empty);
        Assert.That(_provider.Created, Is.EqualTo(new[] { _capture }), "never reopened");
    }

    /// <summary>
    /// The meter has to show sound at every tick: one tick with it quiet starts the count again. So a device that plays
    /// while nothing arrives is warned about 10 seconds after its meter last came back, not 10 seconds after it first
    /// showed sound.
    /// </summary>
    [Test]
    public async Task One_quiet_tick_on_the_meter_starts_the_count_again()
    {
        _provider.SetLevel("Fake Speakers", 0.5f);
        _provider.Enqueue(new FakeAudioCapture(Pcm16KMono));   // the same device, ready for the reopening
        var recording = await StartAsync();

        await TickAsync(6);
        await SetLevelAsync(recording, "Fake Speakers", 0f);
        await TickAsync(1);
        await SetLevelAsync(recording, "Fake Speakers", 0.5f);
        await TickAsync(10);
        Assert.That(ArrivalWarnings(), Is.Empty, "not before the meter has shown sound for 10 seconds again");
        await TickAsync(1);
        Assert.That(ArrivalWarnings(), Is.EqualTo(new[]
        {
            "No audio has arrived from \"Fake Speakers\" for 10 seconds although Windows shows sound playing there; the recording has fallen behind the clock. Reopening it"
        }));

        await StopAsync();
    }

    /// <summary>
    /// Silence is audio too: an app's capture that hands over only silence, as it does while the class is quiet, keeps
    /// arriving, so it is neither warned about nor reopened. Only a capture that hands over nothing at all is.
    /// </summary>
    [Test]
    public async Task An_app_that_hands_over_only_silence_is_not_warned_about()
    {
        var chrome = _provider.AddApp(4242, "chrome", "Speakers", capture: AppCapture(4242))!;
        await StartAsync(source: new ClassSource.App("chrome"));
        await Wait.UntilAsync(() => chrome.Started);

        for (var second = 0; second < 40; second++)
        {
            chrome.Deliver(TestAudio.Shorts(0, 0));
            await TickAsync(1);
        }

        await StopAsync();

        Assert.That(ArrivalWarnings(), Is.Empty);
        Assert.That(_provider.Created, Is.EqualTo(new[] { chrome }), "never reopened");
    }

    /// <summary>Audio that keeps arriving is never warned about: from a device whose meter shows sound, as from an app, nor is its capture reopened.</summary>
    [TestCase(false)]
    [TestCase(true)]
    public async Task Audio_that_keeps_arriving_is_not_warned_about(bool app)
    {
        var capture = _capture;
        if (app)
            capture = _provider.AddApp(4242, "chrome", "Speakers", level: 0.3f, AppCapture(4242))!;
        else
            _provider.SetLevel("Fake Speakers", 0.5f);
        await StartAsync(source: app ? new ClassSource.App("chrome") : null);
        await Wait.UntilAsync(() => capture.Started);

        for (var second = 0; second < 40; second++)
        {
            capture.Deliver(TestAudio.Shorts(1000, -1000));
            await TickAsync(1);
        }

        await StopAsync();

        Assert.That(ArrivalWarnings(), Is.Empty);
        Assert.That(_provider.Created, Is.EqualTo(new[] { capture }), "never reopened");
    }

    /// <summary>
    /// A sleep is no fault of the capture: no audio arrives while the laptop sleeps, so the wait for audio starts again on
    /// waking. A capture that delivers again is not warned about; one that stays silent is, 5 seconds after waking.
    /// </summary>
    [TestCase(true)]
    [TestCase(false)]
    public async Task After_a_sleep_audio_is_awaited_for_5_seconds_from_waking(bool arrivesAgain)
    {
        var chrome = _provider.AddApp(4242, "chrome", "Speakers", level: 0.3f, AppCapture(4242))!;
        _provider.AddApp(4242, "chrome", "Speakers", level: 0.3f, AppCapture(4242));   // ready for a reopening
        await StartAsync(source: new ClassSource.App("chrome"));
        await Wait.UntilAsync(() => chrome.Started);
        for (var second = 0; second < 2; second++)
        {
            chrome.Deliver(TestAudio.Shorts(1000, -1000));
            await TickAsync(1);
        }

        await SleepAsync(TimeSpan.FromSeconds(18));
        Assert.That(ArrivalWarnings(), Is.Empty, "not on waking");
        for (var second = 1; second <= 5; second++)
        {
            if (arrivesAgain) chrome.Deliver(TestAudio.Shorts(1000, -1000));
            await TickAsync(1);
            if (second < 5) Assert.That(ArrivalWarnings(), Is.Empty, $"not {second} seconds after waking");
        }

        await StopAsync();

        Assert.That(FreezeLines(), Has.Length.EqualTo(1));
        Assert.That(ArrivalWarnings(), arrivesAgain
            ? Is.Empty
            : Is.EqualTo(new[] { "No audio has arrived from \"chrome\" for 5 seconds although it should; the recording has fallen behind the clock. Reopening it" }));
    }

    // ------------------------------------------------------------------ Ctrl+C

    /// <summary>
    /// Ctrl+C, as the recorder passes on one pressed in a window or as this window's own handler takes it: one press while
    /// recording only asks for a second and the recording goes on; a second within five seconds stops it, which the run
    /// file puts down to Gulzar; after the stop, one press cancels whatever still runs, such as the upload.
    /// </summary>
    [Test]
    public async Task Ctrl_C_twice_stops_the_recording_and_one_press_after_the_stop_cancels()
    {
        var signalsLog = new RecordingLogger<ConsoleUserSignals>();
        using var signals = new ConsoleUserSignals(_time, signalsLog);
        await RunAsync(new FakeRecording(), signals: signals);

        signals.CtrlCPressed();
        Assert.That(signalsLog.Messages, Is.EqualTo(new[] { "Press Ctrl+C again within 5 seconds to stop" }));
        await TickAsync();
        Assert.That(_loop!.IsCompleted, Is.False, "still recording after one press");

        signals.CtrlCPressed();
        var last = await _loop.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.That(last.Phase, Is.EqualTo(RecorderPhase.Stopping));
        Assert.That(_run.Current.EndedBy, Is.EqualTo(RunEndCause.Q));
        Assert.That(signals.Cancellation.IsCancellationRequested, Is.False, "the second press stops; it cancels nothing");

        _time.Advance(TimeSpan.FromSeconds(3));
        signals.CtrlCPressed();
        Assert.That(signals.Cancellation.IsCancellationRequested, Is.True);
    }

    /// <summary>
    /// A key held with Ctrl or Alt is no recording key, as a shortcut meant for another program is not: Ctrl+Q pressed
    /// twice neither asks for a second Q nor stops, Ctrl+P does not pause, Ctrl+V switches nothing and Alt+M marks nothing.
    /// Nor is a Ctrl+C that the console hands over as a key while the loop reads one: the console's own Ctrl+C handler gives
    /// that press to the signals, so the loop leaves it alone and it is never counted twice. M on its own still marks.
    /// </summary>
    [Test]
    public async Task Keys_held_with_Ctrl_or_Alt_are_no_recording_keys()
    {
        var recording = new FakeRecording(hasMicrophone: true);
        await RunAsync(recording);

        _ui.TypeHolding(ConsoleModifiers.Control, 'q', 'q', 'p', 'v');
        _ui.TypeHolding(ConsoleModifiers.Alt, 'm');
        _ui.Type('\u0003', 'm');
        await Wait.UntilAsync(() => _log.Messages.Any(message => message.StartsWith("Mark ", StringComparison.Ordinal)) || _loop!.IsCompleted);

        Assert.That(_loop!.IsCompleted, Is.False, "Ctrl+Q twice does not stop the recording");
        Assert.That(SecondQRequests(), Is.Zero);
        Assert.That(recording.Calls, Is.Empty, "nothing paused, and the microphone not switched");
        Assert.That(_log.Messages.Where(message => message.StartsWith("Mark ", StringComparison.Ordinal)), Is.EqualTo(new[] { "Mark 1 at 00:00:00" }),
            "only the M on its own marks");
        Assert.That(_signals.CtrlCPresses, Is.Zero, "the console's handler passes the Ctrl+C on, not the loop");
    }

    // ------------------------------------------------------------------ stops by itself, and what ended the recording

    /// <summary>
    /// With --minutes the recording stops by itself when they are up, here after 3 seconds: the stop is asked for, so that
    /// what waits for it sees it, the run file says the minutes ended it, and the window knew from the start when.
    /// </summary>
    [Test]
    public async Task The_recording_stops_by_itself_when_the_minutes_are_up()
    {
        await RunAsync(new FakeRecording(), request: _request with { Minutes = 0.05 });
        Assert.That(_status.States[0].StopsAtUtc, Is.EqualTo(TestRuns.Start.AddSeconds(3)), "said from the first state");

        await TickAsync(2);
        Assert.That(_loop!.IsCompleted, Is.False);
        _time.Advance(TimeSpan.FromSeconds(1));
        var last = await _loop.WaitAsync(TimeSpan.FromSeconds(15));

        Assert.That(last.Phase, Is.EqualTo(RecorderPhase.Stopping));
        Assert.That(_signals.StopRequested.IsCompleted, Is.True);
        Assert.That(_run.Current.EndedBy, Is.EqualTo(RunEndCause.Minutes));
        Assert.That(_log.Messages, Has.Some.EqualTo("The time given with --minutes 0.05 is up; stopping the recording"));
    }

    /// <summary>The safety stop ends a recording that has reached Recording:StopAfterHours, and the run file says so.</summary>
    [Test]
    public async Task The_safety_stop_ends_the_recording()
    {
        await RunAsync(new FakeRecording { Written = TimeSpan.FromHours(9) });

        _time.Advance(TimeSpan.FromSeconds(1));
        await _loop!.WaitAsync(TimeSpan.FromSeconds(15));

        Assert.That(_run.Current.EndedBy, Is.EqualTo(RunEndCause.SafetyStop));
        Assert.That(_log.Problems, Has.Some.StartsWith("This recording stops now, after 9 hours"));
    }

    /// <summary>
    /// A stop that came from elsewhere, Ctrl+C twice or the stop asked for from another window, is put down to Gulzar in
    /// the run file, as Q twice is.
    /// </summary>
    [Test]
    public async Task A_stop_from_elsewhere_is_put_down_to_Gulzar()
    {
        await RunAsync(new FakeRecording());

        await StopAsync();

        Assert.That(_run.Current.EndedBy, Is.EqualTo(RunEndCause.Q));
    }

    /// <summary>A cause named by whoever asked for the stop, such as Windows ending the session, is kept.</summary>
    [Test]
    public async Task A_cause_named_with_the_stop_is_kept()
    {
        await RunAsync(new FakeRecording());

        _run.SetEndedBy(RunEndCause.Windows);
        await StopAsync();

        Assert.That(_run.Current.EndedBy, Is.EqualTo(RunEndCause.Windows));
    }

    // ------------------------------------------------------------------ the state, the heartbeat, notes and reopening

    /// <summary>
    /// A state goes to the window at the start and once a second: recording "Kalam" in the background, where it is, what
    /// it records, and that nothing needs attention.
    /// </summary>
    [Test]
    public async Task A_state_is_published_at_the_start_and_every_second()
    {
        var recording = new FakeRecording();
        recording.Start(_files, TimeSpan.Zero);
        await RunAsync(recording);

        for (var second = 0; second < 3; second++)
        {
            recording.Advance(TimeSpan.FromSeconds(1));
            await TickAsync();
        }

        Assert.That(_status.States.Select(state => state.Position), Is.EqualTo(new[] { 0, 1, 2, 3 }.Select(seconds => TimeSpan.FromSeconds(seconds))));
        Assert.That(_status.States, Has.All.Matches<RecorderState>(state =>
            state is { Phase: RecorderPhase.Recording, Name: "Kalam", Source: "Fake Speakers", WaitingFor: null, Microphone: null, UploadPercent: null, StopsAtUtc: null, InWindow: false }
            && state.Attention.Level == AttentionLevel.None));
    }

    /// <summary>P is shown at once, without waiting for the next second, and so is the stop, with which the loop ends.</summary>
    [Test]
    public async Task A_pause_and_the_stop_are_shown_at_once()
    {
        var recording = new FakeRecording();
        await RunAsync(recording);

        _ui.Type('p');
        await Wait.UntilAsync(() => _status.Last?.Phase == RecorderPhase.Paused);
        _ui.Type('p');
        await Wait.UntilAsync(() => _status.Last?.Phase == RecorderPhase.Recording && _status.Count == 3);
        var last = await StopAsync();

        Assert.That(recording.Calls, Is.EqualTo(new[] { "Pause", "Resume" }));
        Assert.That(_status.States.Select(state => state.Phase), Is.EqualTo(new[] { RecorderPhase.Recording, RecorderPhase.Paused, RecorderPhase.Recording, RecorderPhase.Stopping }));
        Assert.That(last, Is.SameAs(_status.Last));
    }

    /// <summary>
    /// Nothing recorded for 15 seconds reaches the window as an Alert, with the phase and what the recording waits for: in
    /// full for the view, and in a few words for the title. A run recording in this window says so.
    /// </summary>
    [Test]
    public async Task Nothing_recorded_reaches_the_window_as_an_Alert()
    {
        _run.Dispose();
        _run = _runtime.Journal.Begin(TestRuns.Record(TestRuns.Id(2), RunState.Recording) with { Home = RunHome.InWindow });
        await RunAsync(new FakeRecording { Main = FakeRecording.Track("chrome", waitingFor: "\"chrome\" to play sound", capturing: false) },
            request: _request with { Source = new SourcePlan(RecordingMode.Class, new ClassSource.App("chrome"), Microphone: null) });

        await TickAsync(15);

        Assert.That(_status.Last, Is.Not.Null);
        Assert.That(_status.Last!.Phase, Is.EqualTo(RecorderPhase.NotRecording));
        Assert.That(_status.Last.WaitingFor, Is.EqualTo("\"chrome\" to play sound"));
        Assert.That(_status.Last.InWindow, Is.True);
        Assert.That(_status.Last.Attention.Level, Is.EqualTo(AttentionLevel.Alert));
        Assert.That(_status.Last.Attention.Short, Is.EqualTo("NOT RECORDING (waiting for \"chrome\")"));
    }

    /// <summary>
    /// A run without a run file, as when ClassScribe's own folder cannot be written, records in this window: every state
    /// says so, from the first to the stop, Esc says how to stop it, and Q twice stops it. Nothing fails for want of a file
    /// to write the heartbeat and the cause of the stop to.
    /// </summary>
    [Test]
    public async Task A_run_without_a_run_file_records_in_this_window()
    {
        var recording = new FakeRecording();
        await RunAsync(recording, withRunFile: false);
        Assert.That(_status.States[0].InWindow, Is.True, "from the first state");

        _ui.Type('\u001b');
        await Wait.UntilAsync(() => _log.Messages.Contains("This recording runs in this window; Q twice stops it"));
        for (var second = 0; second < 10; second++)   // past the time of the first heartbeat
        {
            recording.Advance(TimeSpan.FromSeconds(1));
            await TickAsync();
        }

        _ui.Type('q', 'q');
        var last = await _loop!.WaitAsync(TimeSpan.FromSeconds(15));

        Assert.That(last.Phase, Is.EqualTo(RecorderPhase.Stopping));
        Assert.That(_status.States, Has.All.Matches<RecorderState>(state => state.InWindow), "the stop included");
        Assert.That(_log.Problems, Is.Empty);
    }

    /// <summary>
    /// The run file's heartbeat is written every 10 seconds with where the recording is, and once more as the loop ends, so
    /// that a recording that ends unexpectedly is reported with its position.
    /// </summary>
    [Test]
    public async Task A_heartbeat_is_written_every_10_seconds_and_at_the_stop()
    {
        var recording = new FakeRecording();
        await RunAsync(recording);
        var begun = _run.Current.HeartbeatUtc;

        for (var second = 1; second <= 20; second++)
        {
            recording.Advance(TimeSpan.FromSeconds(1));
            await TickAsync();
            if (second == 9) Assert.That(_run.Current.HeartbeatUtc, Is.EqualTo(begun), "not before 10 seconds");
            if (second == 10) Assert.That((_run.Current.HeartbeatUtc, _run.Current.Position), Is.EqualTo((TestRuns.Start.AddSeconds(10), TimeSpan.FromSeconds(10))));
            if (second == 19) Assert.That(_run.Current.Position, Is.EqualTo(TimeSpan.FromSeconds(10)));
        }

        Assert.That((_run.Current.HeartbeatUtc, _run.Current.Position), Is.EqualTo((TestRuns.Start.AddSeconds(20), TimeSpan.FromSeconds(20))));
        recording.Advance(TimeSpan.FromSeconds(3));
        await StopAsync();
        Assert.That(_run.Current.Position, Is.EqualTo(TimeSpan.FromSeconds(23)), "the position at the stop");
    }

    /// <summary>
    /// The Zoom crash of 18 October, in the loop: every look at Windows' lists fails with E_INVALIDARG as .NET's own mapping
    /// gives it, an ArgumentException, the output devices' for the echo check every second and the microphones' for the
    /// Bluetooth check every 10 seconds, in the seconds the heartbeat is due. The second's work still reaches the heartbeat
    /// and the state: the run file says where the recording is every 10 seconds, the window gets a state every second, and
    /// no second's work fails.
    /// </summary>
    [Test]
    public async Task The_heartbeat_and_the_state_go_on_while_every_look_at_Windows_lists_fails()
    {
        var invalid = new ArgumentException("Value does not fall within the expected range.");
        _provider.FailListings(invalid);
        _microphones.ListingFailure = invalid;
        var recording = new FakeRecording(hasMicrophone: true);
        await RunAsync(recording, request: _request with { Source = new SourcePlan(RecordingMode.ClassAndMicrophone, new ClassSource.Automatic(), new MicrophoneChoice(null)) });
        var published = _status.Count;

        for (var second = 1; second <= 20; second++)
        {
            recording.Advance(TimeSpan.FromSeconds(1));
            await TickAsync();
        }

        Assert.That((_run.Current.HeartbeatUtc, _run.Current.Position), Is.EqualTo((TestRuns.Start.AddSeconds(20), TimeSpan.FromSeconds(20))));
        Assert.That(_status.Count - published, Is.EqualTo(20), "a state every second");
        Assert.That(_microphones.Listings, Is.EqualTo(3), "the Bluetooth check looked at the start and every 10 seconds");
        Assert.That(_log.Problems, Is.Empty, "no second's work failed");
        Assert.That(_loop!.IsCompleted, Is.False, "still recording");
    }

    /// <summary>
    /// Notes go into the recording's marks file: the checks' note for a time the laptop slept, at the position the recording
    /// had, and a note the recording raises itself.
    /// </summary>
    [Test]
    public async Task Notes_go_into_the_marks_file()
    {
        var recording = new FakeRecording { Written = TimeSpan.FromSeconds(42) };
        await RunAsync(recording);
        await TickAsync();
        await SleepAsync(TimeSpan.FromSeconds(18));

        recording.Note(TimeSpan.FromSeconds(50), "About 3 minutes with nothing playing were left out here");
        await TickAsync();
        await StopAsync();

        Assert.That(File.ReadAllLines(_files.Marks), Is.EqualTo(new[]
        {
            "00:00:42.000 ~ Not recorded: the laptop slept from 23:00:03 to 23:00:21 UTC",
            "00:00:50.000 ~ About 3 minutes with nothing playing were left out here"
        }));
    }

    /// <summary>A track from which no audio arrives although it should is reopened, as the checks ask.</summary>
    [Test]
    public async Task A_stalled_track_is_reopened()
    {
        var recording = new FakeRecording { Main = FakeRecording.Track("chrome", deliversContinuously: true, sinceLastPacket: TimeSpan.FromMinutes(1)) };
        await RunAsync(recording);

        await TickAsync(5);

        Assert.That(recording.Reopened, Is.EqualTo(new[] { (Track.Main, "no audio has arrived for 5 seconds") }));
        Assert.That(ArrivalWarnings(), Has.Length.EqualTo(1));
    }

    /// <summary>A check that fails is logged, once, and the class goes on recording: the next second checks again.</summary>
    [Test]
    public async Task A_check_that_fails_is_logged_and_the_recording_goes_on()
    {
        await RunAsync(new FakeRecording());

        _status.FailNext(new InvalidOperationException("The window's title could not be set."));
        await TickAsync();
        await Wait.UntilAsync(() => _log.Problems.Count > 0);
        await TickAsync(2);

        Assert.That(_loop!.IsCompleted, Is.False, "still recording");
        Assert.That(_log.Problems, Is.EqualTo(new[] { TickFailure }));
    }

    /// <summary>
    /// Work that keeps failing is an error the first time and is then logged only for diagnosis, so that a window whose title
    /// cannot be set does not fill the log with an error every second; the class goes on recording all along. Each kind of
    /// work counts for itself: a key that works meanwhile does not make the next failed check an error again, and a key that
    /// fails is an error of its own, since Gulzar pressed it. Once the checks work again, a new failure is an error again.
    /// </summary>
    [Test]
    public async Task Work_that_keeps_failing_is_an_error_once_for_each_kind()
    {
        var recording = new FakeRecording();
        await RunAsync(recording);
        _status.FailFromNow(new InvalidOperationException("The window's title could not be set."));

        await TickAsync(2);
        _ui.Type('m');
        await Wait.UntilAsync(() => _log.Messages.Contains("Mark 1 at 00:00:00"));
        await TickAsync();
        _ui.Type('p');
        await Wait.UntilAsync(() => _log.Messages.Contains(KeyFailure));
        await TickAsync();
        _status.Recover();
        await TickAsync();
        _status.FailNext(new InvalidOperationException("The window's title could not be set."));
        await TickAsync();
        await Wait.UntilAsync(() => LevelsOf(TickFailure).Length == 5);

        Assert.That(_loop!.IsCompleted, Is.False, "still recording");
        Assert.That(recording.Paused, Is.True, "P paused, though the window could not show it");
        Assert.That(LevelsOf(TickFailure), Is.EqualTo(new[] { LogLevel.Error, LogLevel.Debug, LogLevel.Debug, LogLevel.Debug, LogLevel.Error }));
        Assert.That(LevelsOf(KeyFailure), Is.EqualTo(new[] { LogLevel.Error }));
    }

    /// <summary>
    /// The state the run stops in, when it cannot be shown, is logged as such, and the loop still ends with it: the
    /// recording stops all the same.
    /// </summary>
    [Test]
    public async Task A_stop_that_cannot_be_shown_still_ends_the_loop()
    {
        await RunAsync(new FakeRecording());

        _status.FailNext(new InvalidOperationException("The window's title could not be set."));
        var last = await StopAsync();

        Assert.That(last.Phase, Is.EqualTo(RecorderPhase.Stopping));
        Assert.That(_log.Problems, Is.EqualTo(new[] { StopFailure }));
    }

    // ------------------------------------------------------------------ V, Esc, and nobody at the keyboard

    /// <summary>V in a recording of the class alone says there is no microphone to switch.</summary>
    [Test]
    public async Task V_says_when_there_is_no_microphone()
    {
        await RunAsync(new FakeRecording());

        _ui.Type('v');

        await Wait.UntilAsync(() => _log.Messages.Contains("There is no microphone in this recording"));
    }

    /// <summary>V switches the microphone off and on again, and the window sees each at once.</summary>
    [Test]
    public async Task V_switches_the_microphone_off_and_on()
    {
        var recording = new FakeRecording(hasMicrophone: true);
        await RunAsync(recording);

        _ui.Type('v');
        await Wait.UntilAsync(() => _status.Last?.Microphone is { On: false });
        Assert.That(recording.MicrophoneOn, Is.False);
        _ui.Type('v');
        await Wait.UntilAsync(() => _status.Last?.Microphone is { On: true });

        Assert.That(recording.MicrophoneOn, Is.True);
        Assert.That(_log.Messages, Has.Some.EqualTo("Microphone off; V switches it on again").And.Some.EqualTo("Microphone on"));
    }

    /// <summary>Esc in a run recording in this window says that it records here and how to stop it; it stops nothing.</summary>
    [Test]
    public async Task Esc_in_a_run_in_this_window_says_how_to_stop_it()
    {
        _run.Dispose();
        _run = _runtime.Journal.Begin(TestRuns.Record(TestRuns.Id(2), RunState.Recording) with { Home = RunHome.InWindow });
        await RunAsync(new FakeRecording());

        _ui.Type('\u001b');
        await Wait.UntilAsync(() => _log.Messages.Contains("This recording runs in this window; Q twice stops it"));

        Assert.That(_loop!.IsCompleted, Is.False);
    }

    /// <summary>
    /// With nobody at the keyboard, as when the input is redirected, no key is read, and the loop still checks on the
    /// recording every second and ends when the stop comes.
    /// </summary>
    [Test]
    public async Task With_nobody_at_the_keyboard_the_loop_still_runs()
    {
        _ui = new FakeConsoleUi { IsInteractive = false };
        await RunAsync(new FakeRecording());

        await TickAsync(2);
        var last = await StopAsync();

        Assert.That(last.Phase, Is.EqualTo(RecorderPhase.Stopping));
        Assert.That(_status.Count, Is.EqualTo(4));
    }

    /// <summary>
    /// Keys that run out, as when the source of the keys closes, are not read again and again: the loop reads no more keys,
    /// goes on checking on the recording every second, and ends when the stop comes.
    /// </summary>
    [Test]
    public async Task Keys_that_run_out_are_not_read_again()
    {
        _ui = new FakeConsoleUi { KeysClosed = true };
        await RunAsync(new FakeRecording());

        await TickAsync(3);
        var last = await StopAsync();

        Assert.That(_ui.KeyReads, Is.EqualTo(1));
        Assert.That(last.Phase, Is.EqualTo(RecorderPhase.Stopping));
        Assert.That(_status.Count, Is.EqualTo(5));
    }

    /// <summary>A loop runs one run: asked to run another, it refuses, as each run has its own.</summary>
    [Test]
    public async Task A_loop_runs_once()
    {
        var loop = CreateLoop(TestOptions.Create(Settings()), _signals);
        _loop = loop.RunAsync(new FakeRecording(), _files, _request, _run, TimeSpan.Zero);
        await Wait.UntilAsync(() => _status.Count > 0);

        Assert.ThrowsAsync<InvalidOperationException>(() => loop.RunAsync(new FakeRecording(), _files, _request, _run, TimeSpan.Zero));
    }

    /// <summary>
    /// The loop and its checks resolve in a run's scope from what the app registers, once the free space, a status sink and
    /// the two of them are registered as a run's registration adds them: each run gets a loop of its own.
    /// </summary>
    [Test, Platform("Win")]
    [SupportedOSPlatform("windows")]
    public void The_loop_resolves_in_a_run_scope_from_the_apps_services()
    {
        File.WriteAllText(_folder.File("appsettings.json"), "{}");
        using var app = AppHost.Build(new SettingsPlaces(_folder.Path, userFolder: null), "dummy-key-for-a-local-test", services => services
            .AddSingleton<ILoopbackCaptureProvider>(_provider)
            .AddSingleton(_runtime.Folder)   // never the real %LOCALAPPDATA%\ClassScribe
            .AddSingleton<IFreeSpace, FreeSpace>()
            .AddScoped<IRecorderStatusSink, FakeStatusSink>()
            .AddScoped<RecordingMonitor>()
            .AddScoped<RecordingLoop>());
        using var first = app.Services.CreateScope();
        using var second = app.Services.CreateScope();

        Assert.That(first.ServiceProvider.GetRequiredService<RecordingLoop>(), Is.Not.SameAs(second.ServiceProvider.GetRequiredService<RecordingLoop>()));
    }

    // ------------------------------------------------------------------ the microphone: the start, the state and V

    /// <summary>A recording of the class and your microphone that follows chrome through a gate for an app whose mute is <paramref name="appMute"/>, started into the tests' files.</summary>
    private FakeMicrophoneRecording FollowingChrome(ClassAppMute appMute)
    {
        var recording = new FakeMicrophoneRecording();
        recording.Follow(new FollowRule.Named("chrome"), appMute, _time);
        recording.Start(_files, TimeSpan.Zero);
        return recording;
    }

    /// <summary>The checks' first look comes at the start, so the privacy line is in the log before the first second.</summary>
    [Test]
    public async Task The_first_look_comes_at_the_start()
    {
        await RunAsync(FollowingChrome(ClassAppMute.Visible), request: _request with { Source = new SourcePlan(RecordingMode.ClassAndMicrophone, new ClassSource.Automatic(), new MicrophoneChoice(null)) });

        Assert.That(_log.Messages, Does.Contain("Your microphone is recorded too, and uploaded to ElevenLabs with the class."));
        Assert.That(_status.Count, Is.EqualTo(1));
    }

    /// <summary>The state carries your microphone with the app it follows and the gate's state and words, for the title and the windows.</summary>
    [Test]
    public async Task The_state_carries_the_microphone_the_app_it_follows_and_the_gates_words()
    {
        var recording = FollowingChrome(ClassAppMute.Visible);
        await RunAsync(recording, request: _request with { Source = new SourcePlan(RecordingMode.ClassAndMicrophone, new ClassSource.Automatic(), new MicrophoneChoice(null)) });
        recording.Gate!.Record(Looks.NotListening(TimeSpan.FromTicks(_time.GetTimestamp())));

        await TickAsync();

        Assert.That(_status.Last!.Microphone, Is.EqualTo(new MicrophoneState("Microphone Array", On: true, Capturing: true, Problem: null,
            Follows: "chrome", Gate: MicrophoneGateState.NotListening, Why: "chrome is not listening")));
    }

    /// <summary>
    /// When the app's mute cannot be seen the microphone starts off, and V arms it: the line says what it then waits
    /// for, the state shows the gate's words, and V again switches it off.
    /// </summary>
    [Test]
    public async Task V_arms_the_gate_and_says_what_the_microphone_waits_for()
    {
        var recording = FollowingChrome(ClassAppMute.Unknown);
        await RunAsync(recording, request: _request with { Source = new SourcePlan(RecordingMode.ClassAndMicrophone, new ClassSource.Automatic(), new MicrophoneChoice(null)) });
        Assert.That(_status.Last!.Microphone, Is.EqualTo(new MicrophoneState("Microphone Array", On: false, Capturing: true, Problem: null,
            Follows: "chrome", Gate: MicrophoneGateState.Off, Why: "off (V records your voice while chrome holds the microphone)")));

        _ui.Type('v');
        await Wait.UntilAsync(() => _status.Last?.Microphone is { On: true });
        _ui.Type('v');
        await Wait.UntilAsync(() => _status.Last?.Microphone is { On: false });

        Assert.That(_log.Messages, Does.Contain("Microphone on; waiting for chrome to open the microphone").And.Contain("Microphone off; V switches it on again"));
        Assert.That(_status.States.Select(state => state.Microphone?.Gate), Does.Contain(MicrophoneGateState.NotOpened));
        Assert.That(recording.Gate!.Armed, Is.False);
    }

    /// <summary>
    /// Without a gate the microphone is unfollowed: recorded whenever it is on, and off when switched off with V. A
    /// recording that says no more about its microphone than its snapshot still has it named at the start, not waited for.
    /// </summary>
    [Test]
    public async Task Without_a_gate_the_microphone_is_unfollowed()
    {
        var recording = new FakeRecording(hasMicrophone: true);
        await RunAsync(recording, request: _request with { Source = new SourcePlan(RecordingMode.ClassAndMicrophone, new ClassSource.Automatic(), new MicrophoneChoice(null)) });
        Assert.That(_status.Last!.Microphone, Is.EqualTo(new MicrophoneState("Microphone Array", On: true, Capturing: true, Problem: null,
            Follows: null, Gate: MicrophoneGateState.Unfollowed, Why: null)));
        Assert.That(_log.Messages, Has.One.EqualTo("Microphone: \"Microphone Array\", without Windows' sound processing (RAW)"));
        Assert.That(_log.Messages, Has.None.StartsWith("Microphone: waiting for"));

        _ui.Type('v');
        await Wait.UntilAsync(() => _status.Last?.Microphone is { On: false });

        Assert.That(_status.Last!.Microphone!.Gate, Is.EqualTo(MicrophoneGateState.Off));
    }

    /// <summary>A microphone that cannot be recorded names what it waits for as its problem.</summary>
    [Test]
    public async Task A_missing_microphone_names_what_it_waits_for_as_its_problem()
    {
        var recording = new FakeRecording(hasMicrophone: true) { Microphone = FakeRecording.Track("", waitingFor: "the microphone \"OBSBOT\"", capturing: false) };
        await RunAsync(recording, request: _request with { Source = new SourcePlan(RecordingMode.ClassAndMicrophone, new ClassSource.Automatic(), new MicrophoneChoice("OBSBOT")) });

        Assert.That(_status.Last!.Microphone, Is.EqualTo(new MicrophoneState("", On: true, Capturing: false, Problem: "the microphone \"OBSBOT\"",
            Follows: null, Gate: MicrophoneGateState.Unfollowed, Why: null)));
    }
}
