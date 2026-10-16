using System.Globalization;
using System.Net;
using System.Runtime.Versioning;
using ClassScribe.Audio;
using ClassScribe.Commands;
using ClassScribe.Desktop;
using ClassScribe.Desktop.Windows;
using ClassScribe.Recorder;
using ClassScribe.Settings;
using ClassScribe.Storage;
using ClassScribe.Transcription;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ClassScribe.Tests;

/// <summary>
/// The record command from start to saved transcript: the real session, preparer and workflow,
/// driven by a simulated audio device and keyboard, with the transcription service mocked, and
/// stand-ins for keeping the laptop awake and for the computer's clocks.
/// </summary>
[TestFixture]
public class RecordCommandTests
{
    private static readonly AudioFormat Pcm16KMono = new(16000, 1, 16, false);

    /// <summary>The laptop awake and ClassScribe running, as every recording needs.</summary>
    private const PowerNeeds Awake = PowerNeeds.System | PowerNeeds.Execution;

    private TempFolder _folder = null!;
    private FakeCaptureProvider _provider = null!;
    private FakeAudioCapture _capture = null!;
    private FakeUserSignals _signals = null!;
    private FakeConsoleUi _ui = null!;
    private Mock<ITranscriptionClient> _client = null!;
    private Mock<IDesktop> _desktop = null!;
    private Mock<IExitWarnings> _exitWarnings = null!;
    private FakePowerRequests _power = null!;
    private FakePowerSource _powerSource = null!;
    private FakeSystemTimes _times = null!;
    private RecordingSession _session = null!;
    private RecordingLogger<RecordCommand> _log = null!;

    /// <summary>Creates a temporary folder, a simulated audio device and keyboard, the mocked services, and the power stand-ins.</summary>
    [SetUp]
    public void SetUp()
    {
        _folder = new TempFolder();
        _provider = new FakeCaptureProvider();
        _capture = _provider.Enqueue(new FakeAudioCapture(Pcm16KMono));
        _signals = new FakeUserSignals();
        _ui = new FakeConsoleUi();
        _client = Mocks.TranscriptionClient();
        _desktop = new Mock<IDesktop>();
        _exitWarnings = new Mock<IExitWarnings>();
        _exitWarnings.Setup(w => w.Register(It.IsAny<Action>())).Returns(Mock.Of<IDisposable>());
        _power = new FakePowerRequests();
        _powerSource = new FakePowerSource();
        _log = new RecordingLogger<RecordCommand>();
    }

    /// <summary>Deletes the temporary folder. That fails if a test left a file open, which is the point.</summary>
    [TearDown]
    public void TearDown() => _folder.Dispose();

    /// <summary>
    /// Builds the command with the real session, preparer and workflow around the simulated device and the
    /// mocked service. The recordings go to the temporary folder unless <paramref name="outputFolder"/> names another.
    /// The computer's clocks (<see cref="_times"/>) are read from <paramref name="time"/>. The session is kept in
    /// <see cref="_session"/>, so that a test can see what its looks have read.
    /// </summary>
    private RecordCommand CreateCommand(
        string apiKey, TimeProvider? time = null, bool openWhenDone = false, int statusIntervalSeconds = 60, string device = "", string? outputFolder = null, int silenceWarningSeconds = 60,
        string app = "", KeepScreenOn keepScreenOn = KeepScreenOn.OnBattery)
    {
        time ??= TimeProvider.System;
        var recording = TestOptions.Create(new RecordingOptions
        {
            OutputFolder = outputFolder ?? _folder.Path,
            OpenFolderWhenDone = openWhenDone,
            OpenTranscriptWhenDone = openWhenDone,
            StatusIntervalSeconds = statusIntervalSeconds,
            SilenceWarningSeconds = silenceWarningSeconds,
            Device = device,
            App = app,
            KeepScreenOn = keepScreenOn
        });
        var elevenLabs = TestOptions.Create(new ElevenLabsOptions { ApiKey = apiKey, KeytermsFile = _folder.File("no-glossary.txt") });
        var folders = new ClassFolders(recording, time, NullLogger<ClassFolders>.Instance);
        var marks = new ClassMarks(NullLogger<ClassMarks>.Instance);
        _session = new RecordingSession(_provider, new CaptureChooser(time, new SourceChoice(recording)), recording, time, NullLogger<RecordingSession>.Instance);
        var workflow = new TranscriptionWorkflow(
            new AudioPreparer(NullLogger<AudioPreparer>.Instance),
            _client.Object,
            new KeytermLoader(NullLogger<KeytermLoader>.Instance),
            new TranscriptFormatter(TestOptions.Create(new TranscriptOptions())),
            folders,
            marks,
            elevenLabs,
            recording,
            NullLogger<TranscriptionWorkflow>.Instance);

        _times = new FakeSystemTimes(time);
        return new RecordCommand(
            _session, workflow, _signals, _ui, folders, marks, _desktop.Object, _exitWarnings.Object, _power, _powerSource,
            new FreezeWatch(_times, time, NullLogger<FreezeWatch>.Instance),
            recording, elevenLabs, time, _log);
    }

    /// <summary>A capture of an app's sound, which like Windows' process loopback hands over audio all the time, silence included.</summary>
    private static FakeAudioCapture AppCapture(int processId) => new(Pcm16KMono, "chrome", $"process:{processId}") { DeliversContinuously = true };

    /// <summary>The lines about a time ClassScribe did not run, given so far.</summary>
    private string[] FreezeLines() => [.. _log.Problems.Where(p => p.EndsWith("; nothing was recorded then", StringComparison.Ordinal))];

    /// <summary>The warnings that audio has stopped arriving, given so far.</summary>
    private string[] ArrivalWarnings() => [.. _log.Problems.Where(p => p.StartsWith("No audio has arrived from ", StringComparison.Ordinal))];

    /// <summary>A moment as the freeze lines give it: the local time of day, to the second.</summary>
    private static string Local(DateTimeOffset moment) =>
        TimeZoneInfo.ConvertTime(moment, TimeZoneInfo.Local).ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>The folder of the recording named Kalam.</summary>
    private string KalamFolder() => Path.Combine(_folder.Path, "Kalam");

    /// <summary>The one file in the Kalam folder matching the pattern.</summary>
    private string Only(string pattern) => Directory.GetFiles(KalamFolder(), pattern).Single();

    /// <summary>The recording's own audio file, as opposed to the upload copy made from it.</summary>
    private string Recording() => Directory.GetFiles(KalamFolder(), "*.wav").Single(f => !f.EndsWith(".upload.wav", StringComparison.Ordinal));

    /// <summary>How many times the command has asked for a second Q.</summary>
    private int SecondQRequests() => _log.Messages.Count(m => m == "Press Q again within 5 seconds to stop");

    /// <summary>How many status lines the recording loop has logged.</summary>
    private int StatusLines() => _log.Messages.Count(m => m.StartsWith("Recording: ", StringComparison.Ordinal));

    /// <summary>The silence warnings given so far, each cut to its first sentence, such as "No sound for 60 seconds".</summary>
    private string[] SilenceWarnings() => [.. _log.Problems.Where(p => p.StartsWith("No sound for ", StringComparison.Ordinal)).Select(p => p.Split('.')[0])];

    /// <summary>
    /// Moves the clock on one second at a time, each time waiting until the recording loop has logged
    /// its status line for that second. That needs a status interval of one second.
    /// </summary>
    private async Task TickAsync(ManualTimeProvider time, int seconds)
    {
        for (var i = 0; i < seconds; i++)
            await JumpAsync(time, TimeSpan.FromSeconds(1));
    }

    /// <summary>
    /// Moves the clock on by <paramref name="step"/> at once, then waits until the recording loop has logged its
    /// status line for that moment. That needs a status interval of one second.
    /// </summary>
    private async Task JumpAsync(ManualTimeProvider time, TimeSpan step)
    {
        var reported = StatusLines();
        time.Advance(step);
        await Wait.UntilAsync(() => StatusLines() > reported);
    }

    /// <summary>
    /// Sets what Windows' meter shows for <paramref name="deviceId"/>, has the session look at the devices at once, as a
    /// device notification would, and waits until that look has read the level. So the recording loop's next tick sees it,
    /// without the clock moving meanwhile.
    /// </summary>
    private async Task SetLevelAsync(string deviceId, float level)
    {
        _provider.SetLevel(deviceId, level);
        _provider.RaiseDevicesChanged();
        await Wait.UntilAsync(() => _session.GetStatus().RecordedLevel == level);
    }

    /// <summary>
    /// Records a moment of sound and stops, with a folder put where the transcript's
    /// <paramref name="blockedExtension"/> file is to go, so that saving it fails after ElevenLabs has
    /// answered and charged for the answer. Returns what the command failed with.
    /// </summary>
    private async Task<Exception?> RecordAndFailToSaveAsync(string blockedExtension)
    {
        var run = CreateCommand("test-key").RunAsync("Kalam", transcribe: true);
        await Wait.UntilAsync(() => _capture.Started);
        _capture.Deliver(TestAudio.Shorts(1000, -1000));
        Directory.CreateDirectory(Path.ChangeExtension(Recording(), blockedExtension));
        _signals.RequestStop();

        return Assert.CatchAsync(async () => await run);
    }

    /// <summary>
    /// Checks that the hint after a failure whose paid answer is kept in <paramref name="answer"/> ends with two commands
    /// that run as printed: the free rebuild from that answer, then, for another try, transcribe with --again. On the
    /// command line, transcribe refuses a recording whose paid answer is kept unless --again is given, so a plain
    /// transcribe command is never offered for it.
    /// </summary>
    private void AssertTheCommandsAfterAKeptAnswer(string answer)
    {
        Assert.That(_log.Messages.TakeLast(2), Is.EqualTo(new[]
        {
            $"To lay that answer out as a transcript for free, run: {CommandLine.Rebuild(answer)}",
            $"To transcribe it again all the same, choose 3 in the menu, or run: {CommandLine.Transcribe(Recording(), again: true)}"
        }));
        Assert.That(_log.Messages, Has.None.EndsWith(CommandLine.Transcribe(Recording())), "the plain command, which the command line refuses here");
    }

    /// <summary>Recording runs until stopped, and the recording is then transcribed under its name.</summary>
    [Test]
    public async Task A_class_is_recorded_until_stopped_and_then_transcribed()
    {
        var run = CreateCommand("test-key").RunAsync("Kalam", transcribe: true);
        await Wait.UntilAsync(() => _capture.Started);
        _capture.Deliver(TestAudio.Shorts(TestAudio.Tone(16000, seconds: 0.5, frequency: 440, amplitude: 0.5)));
        _signals.RequestStop();

        var exitCode = await run;

        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(Path.GetFileName(Only("*.wav")), Does.Match(@"^Kalam_\d{4}-\d{2}-\d{2}_\d{2}-\d{2}-\d{2}Z\.wav$"));
        Assert.That(File.ReadAllText(Only("*.md")), Does.StartWith("# Kalam").And.Contain("Hello world."));
        _client.Verify(Calls.TranscriptionOf(Path.ChangeExtension(Only("*.wav"), ".upload.wav")), Times.Once);
        Assert.That(_log.Messages, Has.Some.Contains("Recording \"Kalam\""));
    }

    /// <summary>Record only keeps the recording and sends nothing.</summary>
    [Test]
    public async Task Record_only_keeps_the_audio_and_sends_nothing()
    {
        var run = CreateCommand("test-key", openWhenDone: true).RunAsync("Kalam", transcribe: false);
        await Wait.UntilAsync(() => _capture.Started);
        _capture.Deliver(TestAudio.Shorts(1000, -1000));
        _signals.RequestStop();

        var exitCode = await run;

        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(TestAudio.ReadWav(Only("*.wav")).Samples, Is.EqualTo(new short[] { 1000, -1000 }));
        Assert.That(Directory.GetFiles(KalamFolder(), "*.md"), Is.Empty);
        _client.Verify(Calls.AnyTranscription, Times.Never);
        _desktop.Verify(d => d.OpenFolder(KalamFolder()), Times.Once);
    }

    /// <summary>When the transcript is ready, its folder and the transcript itself are opened, as the settings ask.</summary>
    [Test]
    public async Task The_folder_and_the_transcript_are_opened_when_it_is_ready()
    {
        var run = CreateCommand("test-key", openWhenDone: true).RunAsync("Kalam", transcribe: true);
        await Wait.UntilAsync(() => _capture.Started);
        _capture.Deliver(TestAudio.Shorts(1000, -1000));
        _signals.RequestStop();
        await run;

        _desktop.Verify(d => d.OpenFolder(KalamFolder()), Times.Once);
        _desktop.Verify(d => d.OpenFile(Only("*.md")), Times.Once);
    }

    /// <summary>Q pressed twice stops the recording, and the hint at the start says so.</summary>
    [Test]
    public async Task Q_pressed_twice_stops_the_recording()
    {
        var run = CreateCommand("test-key").RunAsync("Kalam", transcribe: false);
        await Wait.UntilAsync(() => _capture.Started);
        _capture.Deliver(TestAudio.Shorts(1000, -1000));
        _ui.Type('q', 'q');

        Assert.That(await run.WaitAsync(TimeSpan.FromSeconds(15)), Is.EqualTo(0));
        Assert.That(_log.Messages, Has.Some.StartsWith("Press Q twice (or Ctrl+C) to stop, P to pause or resume, M to mark a moment"));
    }

    /// <summary>A single Q, typed into the window by mistake, only asks for a second one: the recording goes on.</summary>
    [Test]
    public async Task A_single_Q_does_not_stop_the_recording()
    {
        var run = CreateCommand("test-key").RunAsync("Kalam", transcribe: false);
        await Wait.UntilAsync(() => _capture.Started);
        _capture.Deliver(TestAudio.Shorts(1000, -1000));
        _ui.Type('q');
        await Wait.UntilAsync(() => SecondQRequests() == 1);
        _capture.Deliver(TestAudio.Shorts(-7, 7));
        Assert.That(run.IsCompleted, Is.False, "still recording");

        _signals.RequestStop();
        await run;

        Assert.That(TestAudio.ReadWav(Only("*.wav")).Samples, Is.EqualTo(new short[] { 1000, -1000, -7, 7 }), "the audio after the Q was kept");
    }

    /// <summary>A Q more than five seconds after the first one counts as a first press again; another Q right after it stops.</summary>
    [Test]
    public async Task A_Q_more_than_five_seconds_after_the_first_counts_as_a_first_press()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var run = CreateCommand("test-key", time).RunAsync("Kalam", transcribe: false);
        await Wait.UntilAsync(() => _capture.Started);
        _capture.Deliver(TestAudio.Shorts(1000, -1000));
        _ui.Type('q');
        await Wait.UntilAsync(() => SecondQRequests() == 1);

        time.Advance(TimeSpan.FromSeconds(6));
        _ui.Type('q');
        await Wait.UntilAsync(() => SecondQRequests() == 2);
        Assert.That(run.IsCompleted, Is.False, "still recording");

        time.Advance(TimeSpan.FromSeconds(4));
        _ui.Type('q');
        Assert.That(await run.WaitAsync(TimeSpan.FromSeconds(15)), Is.EqualTo(0));
    }

    /// <summary>
    /// The five seconds between two Qs are the time that has really passed, whatever Windows does to its
    /// clock: set back an hour between two Qs ten seconds apart, it does not make them stop the recording,
    /// and set forward an hour between two quick ones, it does not keep them from stopping it.
    /// </summary>
    [Test]
    public async Task Two_Qs_are_timed_by_the_time_that_has_really_passed()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var run = CreateCommand("test-key", time).RunAsync("Kalam", transcribe: false);
        await Wait.UntilAsync(() => _capture.Started);
        _capture.Deliver(TestAudio.Shorts(1000, -1000));
        _ui.Type('q');
        await Wait.UntilAsync(() => SecondQRequests() == 1);

        time.StepWallClock(TimeSpan.FromHours(-1));
        time.Advance(TimeSpan.FromSeconds(10));
        _ui.Type('q');
        await Wait.UntilAsync(() => SecondQRequests() == 2);
        Assert.That(run.IsCompleted, Is.False, "still recording: the second Q came ten seconds after the first");

        time.StepWallClock(TimeSpan.FromHours(1));
        _ui.Type('q');
        Assert.That(await run.WaitAsync(TimeSpan.FromSeconds(15)), Is.EqualTo(0));
    }

    /// <summary>P pauses and resumes: audio that arrives during the pause is not kept.</summary>
    [Test]
    public async Task P_pauses_and_resumes_the_recording()
    {
        var run = CreateCommand("test-key").RunAsync("Kalam", transcribe: false);
        await Wait.UntilAsync(() => _capture.Started);
        _capture.Deliver(TestAudio.Shorts(1000, -1000));
        _ui.Type('p');
        await Wait.UntilAsync(() => _log.Messages.Any(m => m.StartsWith("Paused")));
        _capture.Deliver(TestAudio.Shorts(5, 5));
        _ui.Type('p');
        await Wait.UntilAsync(() => _log.Messages.Any(m => m.StartsWith("Resumed")));
        _capture.Deliver(TestAudio.Shorts(-7, 7));
        _signals.RequestStop();
        await run;

        Assert.That(TestAudio.ReadWav(Only("*.wav")).Samples, Is.EqualTo(new short[] { 1000, -1000, -7, 7 }));
    }

    /// <summary>
    /// A minute without sound brings one warning, and while the silence lasts it comes back once a
    /// minute, not more. It is the only sign that the class plays on a device that is not recorded.
    /// </summary>
    [Test]
    public async Task Silence_is_warned_about_once_a_minute()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var run = CreateCommand("test-key", time, statusIntervalSeconds: 1).RunAsync("Kalam", transcribe: false);
        await Wait.UntilAsync(() => _capture.Started);

        await TickAsync(time, seconds: 59);
        Assert.That(SilenceWarnings(), Is.Empty, "not before a minute has passed");
        await TickAsync(time, seconds: 1);
        Assert.That(SilenceWarnings(), Is.EqualTo(new[] { "No sound for 60 seconds" }));
        await TickAsync(time, seconds: 90);
        _signals.RequestStop();
        await run;

        Assert.That(SilenceWarnings(), Is.EqualTo(new[] { "No sound for 60 seconds", "No sound for 120 seconds" }));
    }

    /// <summary>
    /// A silence of up to two minutes is given in seconds; a longer one in whole minutes, and from an hour on in
    /// hours and minutes, so that it reads at a glance however long the class has been silent. The silence warning
    /// and the warning that audio has stopped arriving both give their lengths so.
    /// </summary>
    [TestCase(1, "1 second")]
    [TestCase(120, "120 seconds")]
    [TestCase(121, "2 minutes")]
    [TestCase(179, "2 minutes")]
    [TestCase(3599, "59 minutes")]
    [TestCase(3600, "1 hour")]
    [TestCase(3660, "1 hour 1 minute")]
    [TestCase(9006, "2 hours 30 minutes")]
    [TestCase(90000, "25 hours")]
    public void A_silence_is_given_in_units_that_read_at_a_glance(int seconds, string length) =>
        Assert.That(RecordCommand.DescribeSilence(TimeSpan.FromSeconds(seconds)), Is.EqualTo(length));

    /// <summary>
    /// The silence warning gives its length as <see cref="A_silence_is_given_in_units_that_read_at_a_glance"/> says, here
    /// for a second without sound, with a limit of one second.
    /// </summary>
    [Test]
    public async Task The_silence_warning_gives_its_length_in_words()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var run = CreateCommand("test-key", time, statusIntervalSeconds: 1, silenceWarningSeconds: 1).RunAsync("Kalam", transcribe: false);
        await Wait.UntilAsync(() => _capture.Started);

        await TickAsync(time, seconds: 1);
        _signals.RequestStop();
        await run;

        Assert.That(SilenceWarnings(), Is.EqualTo(new[] { "No sound for 1 second" }));
    }

    /// <summary>
    /// After a sleep longer than the silence limit, the silence warning does not blame the device or Windows' sound for
    /// the time asleep: the first tick after waking gives the sleep line alone. The silence counts from waking, as the
    /// wait for audio does, so the warning comes once the class has been silent for the limit since then.
    /// </summary>
    [Test]
    public async Task After_a_sleep_the_silence_counts_from_waking()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var run = CreateCommand("test-key", time, statusIntervalSeconds: 1).RunAsync("Kalam", transcribe: false);
        await Wait.UntilAsync(() => _capture.Started);
        for (var second = 0; second < 3; second++)
        {
            _capture.Deliver(TestAudio.Shorts(1000, -1000));
            await TickAsync(time, seconds: 1);
        }

        var reported = StatusLines();
        _times.Sleep(TimeSpan.FromMinutes(2));
        await Wait.UntilAsync(() => StatusLines() > reported);
        Assert.That(FreezeLines(), Has.Length.EqualTo(1).And.All.StartWith("The laptop slept from "));
        Assert.That(SilenceWarnings(), Is.Empty, "the time asleep is no silence of the class");

        await TickAsync(time, seconds: 59);
        Assert.That(SilenceWarnings(), Is.Empty, "not before a minute without sound since waking");
        await TickAsync(time, seconds: 1);
        _signals.RequestStop();
        await run;

        Assert.That(SilenceWarnings(), Is.EqualTo(new[] { "No sound for 60 seconds" }));
    }

    /// <summary>
    /// Windows setting its clock back or forward an hour neither stops nor hurries the status line or the
    /// silence warning: the status line still comes every second, and the warning once a minute.
    /// </summary>
    [TestCase(-60)]
    [TestCase(60)]
    public async Task A_change_of_the_time_of_day_does_not_disturb_the_status_line_or_the_silence_warning(int minutes)
    {
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var run = CreateCommand("test-key", time, statusIntervalSeconds: 1).RunAsync("Kalam", transcribe: false);
        await Wait.UntilAsync(() => _capture.Started);
        await TickAsync(time, seconds: 60);
        Assert.That(SilenceWarnings(), Is.EqualTo(new[] { "No sound for 60 seconds" }));

        time.StepWallClock(TimeSpan.FromMinutes(minutes));
        await TickAsync(time, seconds: 59);
        Assert.That(SilenceWarnings(), Is.EqualTo(new[] { "No sound for 60 seconds" }), "not again before another minute has passed");
        await TickAsync(time, seconds: 1);
        _signals.RequestStop();
        await run;

        Assert.That(SilenceWarnings(), Is.EqualTo(new[] { "No sound for 60 seconds", "No sound for 120 seconds" }));
    }

    /// <summary>While paused there is no warning about silence, and the status line says the recording is paused.</summary>
    [Test]
    public async Task No_silence_warning_is_given_while_paused()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var run = CreateCommand("test-key", time, statusIntervalSeconds: 1).RunAsync("Kalam", transcribe: false);
        await Wait.UntilAsync(() => _capture.Started);
        _ui.Type('p');
        await Wait.UntilAsync(() => _log.Messages.Any(m => m.StartsWith("Paused")));

        // Each step moves the clock past the silence limit; the loop reports at its next tick.
        await Wait.UntilAsync(() =>
        {
            time.Advance(TimeSpan.FromSeconds(61));
            return _log.Messages.Any(m => m.Contains("(paused)"));
        });
        _signals.RequestStop();
        await run;

        Assert.That(_log.Problems.Where(p => p.StartsWith("No sound")), Is.Empty);
    }

    /// <summary>While nothing can be recorded yet, the status line says what is being waited for, and there is no warning about silence.</summary>
    [Test]
    public async Task While_waiting_for_the_source_the_status_says_so()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        _provider.Enqueue(new FakeAudioCapture(new AudioFormat(16000, 1, 16, false), "Speakers"));
        var run = CreateCommand("test-key", time, statusIntervalSeconds: 1, device: "Pixel Buds").RunAsync("Kalam", transcribe: false);

        await Wait.UntilAsync(() =>
        {
            time.Advance(TimeSpan.FromSeconds(61));
            return _log.Messages.Any(m => m.Contains("waiting for the output device \"Pixel Buds\""));
        });
        _signals.RequestStop();
        await run;

        Assert.That(_log.Problems.Where(p => p.StartsWith("No sound")), Is.Empty);
    }

    /// <summary>M marks the moment: its position goes to the marks file at once, and the mark shows in the transcript.</summary>
    [Test]
    public async Task M_marks_the_moment_in_the_recording_and_the_transcript()
    {
        var run = CreateCommand("test-key").RunAsync("Kalam", transcribe: true);
        await Wait.UntilAsync(() => _capture.Started);
        _capture.Deliver(TestAudio.Shorts(TestAudio.Tone(16000, seconds: 0.5, frequency: 440, amplitude: 0.5)));
        _ui.Type('m');
        await Wait.UntilAsync(() => Directory.GetFiles(KalamFolder(), "*.marks.txt").Length == 1);
        _signals.RequestStop();
        await run;

        Assert.That(File.ReadAllLines(Only("*.marks.txt")), Is.EqualTo(new[] { "00:00:00.500" }));
        Assert.That(File.ReadAllText(Only("*.md")), Does.Contain("## [00:00:00] Mark 1"));
    }

    /// <summary>A recording of a day or more is reported with all its hours, not the hours past the last full day.</summary>
    [Test]
    public async Task A_recording_of_a_day_or_more_is_reported_with_all_its_hours()
    {
        _provider = new FakeCaptureProvider();
        _capture = _provider.Enqueue(new FakeAudioCapture(new AudioFormat(1, 1, 16, false)));   // one sample a second
        var run = CreateCommand("test-key").RunAsync("Kalam", transcribe: false);
        await Wait.UntilAsync(() => _capture.Started);
        _capture.Deliver(TestAudio.Shorts(new short[25 * 3600]));
        _signals.RequestStop();
        await run;

        Assert.That(_log.Messages, Has.Some.EqualTo("Recorded 25:00:00 of audio"));
    }

    /// <summary>Positions a day or more into the recording, in the status line and in the line for a mark, show all their hours.</summary>
    [Test]
    public async Task Positions_a_day_or_more_into_the_recording_show_all_their_hours()
    {
        _provider = new FakeCaptureProvider();
        _capture = _provider.Enqueue(new FakeAudioCapture(new AudioFormat(1, 1, 16, false)));   // one sample a second
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var run = CreateCommand("test-key", time, statusIntervalSeconds: 1).RunAsync("Kalam", transcribe: false);
        await Wait.UntilAsync(() => _capture.Started);
        _capture.Deliver(TestAudio.Shorts(new short[25 * 3600]));
        await TickAsync(time, seconds: 1);
        _ui.Type('m');
        await Wait.UntilAsync(() => _log.Messages.Any(m => m.StartsWith("Mark 1 at", StringComparison.Ordinal)));
        _signals.RequestStop();
        await run;

        Assert.That(_log.Messages, Has.Some.EqualTo("Recording: 25:00:00 captured from \"Fake Speakers\""));
        Assert.That(_log.Messages, Has.Some.EqualTo("Mark 1 at 25:00:00"));
    }

    /// <summary>When the window is closed or the process crashes mid-recording, the file on disk is brought up to date at once.</summary>
    [Test]
    public async Task A_process_about_to_end_brings_the_recording_on_disk_up_to_date()
    {
        Action? processEnding = null;
        _exitWarnings.Setup(w => w.Register(It.IsAny<Action>())).Callback((Action onExit) => processEnding = onExit).Returns(Mock.Of<IDisposable>());
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);   // the clock stands still, so nothing is saved on the usual schedule
        var run = CreateCommand("test-key", time).RunAsync("Kalam", transcribe: false);
        await Wait.UntilAsync(() => _capture.Started && processEnding is not null);
        _capture.Deliver(TestAudio.Shorts(1000, -1000));
        Assert.That(TestAudio.ReadWav(Only("*.wav")).Samples, Is.Empty, "not saved yet");

        processEnding!();

        Assert.That(TestAudio.ReadWav(Only("*.wav")).Samples, Is.EqualTo(new short[] { 1000, -1000 }));
        _signals.RequestStop();
        await run;
    }

    /// <summary>
    /// When the recording on disk cannot be brought up to date as the process ends, here because another
    /// program has locked the start of the file, that is logged as an error and goes no further: the process
    /// is on its way out. The recording itself is finished as usual afterwards.
    /// </summary>
    [Test, Platform("Win")]
    [SupportedOSPlatform("windows")]
    public async Task A_save_that_fails_as_the_process_ends_is_logged_and_goes_no_further()
    {
        Action? processEnding = null;
        _exitWarnings.Setup(w => w.Register(It.IsAny<Action>())).Callback((Action onExit) => processEnding = onExit).Returns(Mock.Of<IDisposable>());
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);   // the clock stands still, so nothing is saved on the usual schedule
        var run = CreateCommand("test-key", time).RunAsync("Kalam", transcribe: false);
        await Wait.UntilAsync(() => _capture.Started && processEnding is not null);
        _capture.Deliver(TestAudio.Shorts(1000, -1000));

        using (var otherProgram = new FileStream(Only("*.wav"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            otherProgram.Lock(0, WavSegmentWriter.HeaderSize);
            Assert.DoesNotThrow(() => processEnding!());
            otherProgram.Unlock(0, WavSegmentWriter.HeaderSize);
        }

        Assert.That(_log.Problems, Has.Some.StartsWith("Could not bring the recording on disk up to date before exiting"));
        _signals.RequestStop();
        Assert.That(await run, Is.EqualTo(0));
        Assert.That(TestAudio.ReadWav(Only("*.wav")).Samples, Is.EqualTo(new short[] { 1000, -1000 }), "the audio is kept");
    }

    /// <summary>Without a key the class is still recorded, so it can be transcribed later, and the hint says how.</summary>
    [Test]
    public async Task Without_a_key_the_class_is_still_recorded()
    {
        var run = CreateCommand(apiKey: "").RunAsync("Kalam", transcribe: true);
        await Wait.UntilAsync(() => _capture.Started);
        _capture.Deliver(TestAudio.Shorts(1000, -1000));
        _signals.RequestStop();

        var exitCode = await run;

        Assert.That(exitCode, Is.EqualTo(1));
        Assert.That(TestAudio.ReadWav(Only("*.wav")).Samples, Is.EqualTo(new short[] { 1000, -1000 }));
        _client.Verify(Calls.AnyTranscription, Times.Never);
        Assert.That(_log.Problems.Last(), Does.Contain("open a new terminal").And.Contain("quit every VS Code window")
            .And.Contain("choose 3 in the menu").And.Contain(CommandLine.Transcribe(Only("*.wav"))));
    }

    /// <summary>The hint for transcribing later names menu choice 3 and a transcribe command that runs as printed.</summary>
    [Test]
    public async Task The_hint_for_transcribing_later_gives_a_command_that_runs_as_printed()
    {
        var run = CreateCommand("test-key").RunAsync("Kalam", transcribe: false);
        await Wait.UntilAsync(() => _capture.Started);
        _capture.Deliver(TestAudio.Shorts(1000, -1000));
        _signals.RequestStop();
        await run;

        var hint = _log.Messages.Single(m => m.StartsWith("The recording is in", StringComparison.Ordinal));
        Assert.That(hint, Does.Contain("choose 3 in the menu").And.Contain(CommandLine.Transcribe(Only("*.wav"))));
        Assert.That(hint, Does.Not.Contain("run: ClassScribe transcribe"), "a bare ClassScribe is not a command anywhere");
    }

    /// <summary>
    /// A problem with the recordings folder, here an environment variable in the setting that is not set, is
    /// warned about before the recording starts, because a recording started from the command line never
    /// shows the menu that points it out. The class is recorded all the same.
    /// </summary>
    [Test]
    public async Task A_problem_with_the_recordings_folder_is_warned_about_before_recording()
    {
        var outputFolder = Path.Combine(_folder.Path, $"%CLASSSCRIBE_UNSET_{Guid.NewGuid():N}%");
        var problem = new ClassFolders(TestOptions.Create(new RecordingOptions { OutputFolder = outputFolder }), TimeProvider.System, NullLogger<ClassFolders>.Instance).DescribeRootProblem();
        Assert.That(problem, Is.Not.Null, "the setting really has a problem");
        var run = CreateCommand("test-key", outputFolder: outputFolder).RunAsync("Kalam", transcribe: false);
        await Wait.UntilAsync(() => _capture.Started);
        _capture.Deliver(TestAudio.Shorts(1000, -1000));
        _signals.RequestStop();

        Assert.That(await run, Is.EqualTo(0), "recorded all the same");
        var messages = _log.Messages.ToList();
        var recording = messages.FindIndex(m => m.StartsWith("Recording \"Kalam\" to", StringComparison.Ordinal));
        Assert.That(messages.IndexOf(problem!), Is.GreaterThanOrEqualTo(0).And.LessThan(recording), "the warning comes before the recording starts");
        Assert.That(_log.Problems, Has.Member(problem), "as a warning");
    }

    /// <summary>Nothing captured means nothing to pay for, so nothing is uploaded.</summary>
    [Test]
    public async Task Nothing_is_uploaded_when_nothing_was_captured()
    {
        var run = CreateCommand("test-key").RunAsync("Kalam", transcribe: true);
        await Wait.UntilAsync(() => _capture.Started);
        _signals.RequestStop();

        var exitCode = await run;

        Assert.That(exitCode, Is.EqualTo(1));
        _client.Verify(Calls.AnyTranscription, Times.Never);
    }

    /// <summary>A failed transcript leaves the recording in place for another try, and the hint says how to make it.</summary>
    [Test]
    public async Task A_failed_transcript_leaves_the_recording_in_place()
    {
        _client.Setup(Calls.AnyTranscription).ThrowsAsync(new TranscriptionException("ElevenLabs is down."));
        var run = CreateCommand("test-key").RunAsync("Kalam", transcribe: true);
        await Wait.UntilAsync(() => _capture.Started);
        _capture.Deliver(TestAudio.Shorts(1000, -1000));
        _signals.RequestStop();

        Assert.ThrowsAsync<TranscriptionException>(async () => await run);
        Assert.That(TestAudio.ReadWav(Recording()).Samples, Is.EqualTo(new short[] { 1000, -1000 }));
        Assert.That(_log.Problems.Last(), Does.Contain("choose 3 in the menu").And.Contain(CommandLine.Transcribe(Recording())));
        Assert.That(_log.Problems.Last(), Does.Not.Contain("kept in"), "no answer arrived, so none was kept");
    }

    /// <summary>
    /// A recording with nothing audible in it is not sent, and because trying again would find the same
    /// silence, the hint says what to check rather than how to try again.
    /// </summary>
    [Test]
    public async Task A_silent_recording_is_not_offered_for_another_try()
    {
        var run = CreateCommand("test-key").RunAsync("Kalam", transcribe: true);
        await Wait.UntilAsync(() => _capture.Started);
        _capture.Deliver(TestAudio.Shorts(0, 0, 0, 0));
        _signals.RequestStop();

        Assert.ThrowsAsync<NoAudioException>(async () => await run);
        _client.Verify(Calls.AnyTranscription, Times.Never);
        Assert.That(_log.Problems.Last(), Does.StartWith("Nothing audible was captured from \"Fake Speakers\"").And.Contain("Windows is not muted"));
        Assert.That(_log.Problems, Has.None.Contains("try the transcript again"));
        Assert.That(_log.Problems, Has.None.Contains(CommandLine.Transcribe(Recording())));
    }

    /// <summary>
    /// A recording longer than ElevenLabs takes in one file is refused before anything is sent, and because a new try
    /// would be refused the same way, the hint says where the audio is rather than how to try again. The recording is
    /// nine and a half hours and a second long, captured at 10 Hz so that its file stays small.
    /// </summary>
    [Test]
    public async Task A_recording_too_long_to_send_is_not_offered_for_another_try()
    {
        _provider = new FakeCaptureProvider();
        _capture = _provider.Enqueue(new FakeAudioCapture(new AudioFormat(10, 1, 16, false)));
        var run = CreateCommand("test-key").RunAsync("Kalam", transcribe: true);
        await Wait.UntilAsync(() => _capture.Started);
        _capture.Deliver(TestAudio.Shorts([.. Enumerable.Repeat((short)1000, (int)new TimeSpan(9, 30, 1).TotalSeconds * 10)]));
        _signals.RequestStop();

        Assert.ThrowsAsync<RecordingTooLongException>(async () => await run);
        _client.Verify(Calls.AnyTranscription, Times.Never);
        Assert.That(_log.Problems.Last(), Does.StartWith($"The audio is safe in {KalamFolder()}"));
        Assert.That(_log.Messages, Has.None.Contains("try the transcript again"));
        Assert.That(_log.Messages, Has.None.Contains(CommandLine.Transcribe(Recording())));
    }

    /// <summary>
    /// An answer that ElevenLabs charged for but that could not be read is kept, and the hint points to it
    /// before offering a free rebuild from it and another try, which is charged again.
    /// </summary>
    [Test]
    public async Task An_answer_that_could_not_be_read_is_pointed_to_before_another_try()
    {
        _client.Setup(Calls.AnyTranscription).ThrowsAsync(new TranscriptionException("The answer could not be read.", HttpStatusCode.OK, "{\"text\": \"Hello"));
        var run = CreateCommand("test-key").RunAsync("Kalam", transcribe: true);
        await Wait.UntilAsync(() => _capture.Started);
        _capture.Deliver(TestAudio.Shorts(1000, -1000));
        _signals.RequestStop();

        Assert.ThrowsAsync<TranscriptionException>(async () => await run);
        var answer = Only("*.unread.json");
        Assert.That(_log.Problems.Last(), Does.Contain($"kept in {answer}").And.Contain("charged again"));
        AssertTheCommandsAfterAKeptAnswer(answer);
    }

    /// <summary>
    /// When the transcript cannot be written after ElevenLabs' answer was saved, the hint points to that
    /// answer before offering another try, which is charged again.
    /// </summary>
    [Test]
    public async Task A_transcript_that_could_not_be_written_points_to_the_answer_saved_beside_it()
    {
        var error = await RecordAndFailToSaveAsync(".md");

        Assert.That(error, Is.InstanceOf<IOException>().Or.InstanceOf<UnauthorizedAccessException>());
        var answer = Path.ChangeExtension(Recording(), ".json");
        Assert.That(File.Exists(answer), Is.True, "the answer is saved");
        Assert.That(_log.Problems.Last(), Does.Contain($"kept in {answer}").And.Contain("charged again"));
        AssertTheCommandsAfterAKeptAnswer(answer);
    }

    /// <summary>
    /// When ElevenLabs' answer cannot be saved as the transcript's answer, it is kept beside the recording under a
    /// name of its own, and the hint points there.
    /// </summary>
    [Test]
    public async Task An_answer_that_could_not_be_saved_as_the_transcripts_is_pointed_to()
    {
        var error = await RecordAndFailToSaveAsync(".json");

        Assert.That(error, Is.InstanceOf<IOException>().Or.InstanceOf<UnauthorizedAccessException>());
        var answer = Path.ChangeExtension(Recording(), ".unread.json");
        Assert.That(File.Exists(answer), Is.True, "the answer is kept beside the recording");
        Assert.That(_log.Problems.Last(), Does.Contain($"kept in {answer}").And.Contain("charged again"));
        AssertTheCommandsAfterAKeptAnswer(answer);
    }

    /// <summary>
    /// Records a moment of sound and stops, with a folder put in the way of every place ElevenLabs' answer could go:
    /// the transcript's .json, and the answer's name of its own beside the recording and in the recordings folder.
    /// Returns what the command failed with.
    /// </summary>
    private async Task<Exception?> RecordWithNowhereToKeepTheAnswerAsync()
    {
        var run = CreateCommand("test-key").RunAsync("Kalam", transcribe: true);
        await Wait.UntilAsync(() => _capture.Started);
        _capture.Deliver(TestAudio.Shorts(1000, -1000));
        var files = RecordingFiles.TryParse(Recording())!;
        foreach (var inTheWay in new[] { files.Json(), files.UnreadAnswer(), (files with { Folder = _folder.Path }).UnreadAnswer() })
            Directory.CreateDirectory(inTheWay);
        _signals.RequestStop();

        return Assert.CatchAsync(async () => await run);
    }

    /// <summary>
    /// When ElevenLabs' answer can be kept nowhere, neither beside the recording nor in the recordings folder, the
    /// hint still says plainly that ElevenLabs has charged for it and that it could not be saved. It gives the
    /// answer's transcription ID at ElevenLabs, with which the transcript can be fetched from there, and says that
    /// transcribing again is charged again.
    /// </summary>
    [Test]
    public async Task An_answer_that_could_be_kept_nowhere_is_reported_as_charged_for_with_its_transcription_id()
    {
        var error = await RecordWithNowhereToKeepTheAnswerAsync();

        Assert.That(error, Is.InstanceOf<IOException>().Or.InstanceOf<UnauthorizedAccessException>());
        Assert.That(_log.Problems.Last(), Does.Contain("ElevenLabs has charged for this answer, but it could not be saved anywhere.")
            .And.Contain($"Its transcription ID at ElevenLabs is {Mocks.Transcript.TranscriptionId}, with which the transcript can be fetched from ElevenLabs.")
            .And.Contain("Transcribing again is charged again.")
            .And.Contain(CommandLine.Transcribe(Recording()))
            .And.Not.Contain("kept in"));
    }

    /// <summary>
    /// An answer ElevenLabs charged for that could not be read, and that could be kept nowhere either, is still
    /// reported as charged for. No transcription ID could be read from it, so the hint says plainly that the ID is
    /// unknown rather than point to one.
    /// </summary>
    [Test]
    public async Task An_answer_that_could_not_be_read_or_kept_is_reported_as_charged_for_with_its_id_unknown()
    {
        _client.Setup(Calls.AnyTranscription).ThrowsAsync(new TranscriptionException("The answer could not be read.", HttpStatusCode.OK, "{\"text\": \"Hello"));

        var error = await RecordWithNowhereToKeepTheAnswerAsync();

        Assert.That(error, Is.InstanceOf<TranscriptionException>());
        Assert.That(_log.Problems.Last(), Does.Contain("ElevenLabs has charged for this answer, but it could not be saved anywhere, and its transcription ID at ElevenLabs is unknown.")
            .And.Contain("Transcribing again is charged again.")
            .And.Contain(CommandLine.Transcribe(Recording()))
            .And.Not.Contain("kept in"));
    }

    /// <summary>
    /// An earlier transcript left under the same name, say of a recording whose audio was since deleted, is
    /// not taken for an answer kept in this run: a failure that charged nothing gets the usual hint.
    /// </summary>
    [Test]
    public async Task An_earlier_transcript_is_not_taken_for_a_kept_answer()
    {
        var started = new DateTimeOffset(2026, 10, 15, 20, 55, 27, TimeSpan.Zero);
        var earlier = RecordingFiles.ForNew(KalamFolder(), "Kalam", started);
        Directory.CreateDirectory(KalamFolder());
        File.WriteAllText(earlier.Json(), "{}");
        File.WriteAllText(earlier.Transcript(), "# Kalam");
        _client.Setup(Calls.AnyTranscription).ThrowsAsync(new TranscriptionException("ElevenLabs is down."));
        var run = CreateCommand("test-key", new ManualTimeProvider(started)).RunAsync("Kalam", transcribe: true);
        await Wait.UntilAsync(() => _capture.Started);
        _capture.Deliver(TestAudio.Shorts(1000, -1000));
        _signals.RequestStop();

        Assert.ThrowsAsync<TranscriptionException>(async () => await run);
        Assert.That(Recording(), Is.EqualTo(earlier.Segment(1)), "the recording has the earlier one's name");
        Assert.That(_log.Problems.Last(), Does.Contain("To try the transcript again").And.Not.Contain("kept in"));
    }

    /// <summary>
    /// An answer kept by an earlier recording of the same name, in the name's folder or in the recordings
    /// folder, is not taken for this run's. Only the answer this run's error reports counts: a failure that
    /// charged nothing must not send the user to last week's paid answer.
    /// </summary>
    [Test]
    public async Task An_answer_kept_by_an_earlier_recording_of_the_name_is_not_pointed_to()
    {
        var started = new DateTimeOffset(2026, 10, 15, 20, 55, 27, TimeSpan.Zero);
        var lastWeek = RecordingFiles.ForNew(KalamFolder(), "Kalam", started.AddDays(-7));
        Directory.CreateDirectory(KalamFolder());
        File.WriteAllText(Path.Combine(KalamFolder(), $"{lastWeek.BaseName}.unread.json"), "{\"text\": \"Hello");
        File.WriteAllText(_folder.File($"{lastWeek.BaseName}.unread.json"), "{\"text\": \"Hello");
        _client.Setup(Calls.AnyTranscription).ThrowsAsync(new TranscriptionException("ElevenLabs is down."));
        var run = CreateCommand("test-key", new ManualTimeProvider(started)).RunAsync("Kalam", transcribe: true);
        await Wait.UntilAsync(() => _capture.Started);
        _capture.Deliver(TestAudio.Shorts(1000, -1000));
        _signals.RequestStop();

        Assert.ThrowsAsync<TranscriptionException>(async () => await run);
        Assert.That(_log.Problems.Last(), Does.Contain("To try the transcript again")
            .And.Contain(CommandLine.Transcribe(Recording()))
            .And.Not.Contain("kept in"));
    }

    /// <summary>
    /// Ctrl+C during the upload cancels it, as the line logged before the upload says; the recording
    /// stays for another try. The cancel reaches the caller, which reports it with its own exit code.
    /// </summary>
    [Test]
    public async Task Ctrl_C_during_the_upload_cancels_it_and_keeps_the_recording()
    {
        _client.Setup(Calls.AnyTranscription).Returns(async (string _, IReadOnlyList<string> _, CancellationToken cancellation) =>
        {
            await Task.Delay(Timeout.Infinite, cancellation);
            return Mocks.Transcript;
        });
        var run = CreateCommand("test-key").RunAsync("Kalam", transcribe: true);
        await Wait.UntilAsync(() => _capture.Started);
        _capture.Deliver(TestAudio.Shorts(1000, -1000));
        _signals.RequestStop();
        await Wait.UntilAsync(() => _client.Invocations.Count == 1);

        _signals.Cancel();

        Assert.CatchAsync<OperationCanceledException>(async () => await run);
        Assert.That(TestAudio.ReadWav(Recording()).Samples, Is.EqualTo(new short[] { 1000, -1000 }));
        Assert.That(_log.Messages, Has.Some.StartsWith("Press Ctrl+C to cancel the upload"));
    }

    /// <summary>
    /// The laptop is kept awake from the start of capture until the transcript is saved: the hold, with its reason, is
    /// taken by the time capture starts, still held while the recording uploads, and let go once, after the transcript
    /// is on disk.
    /// </summary>
    [Test]
    public async Task The_laptop_is_kept_awake_from_the_start_of_capture_until_the_transcript_is_saved()
    {
        bool? heldDuringUpload = null;
        bool? savedBeforeRelease = null;
        _client.Setup(Calls.AnyTranscription).Returns((string _, IReadOnlyList<string> _, CancellationToken _) =>
        {
            heldDuringUpload = _power.Holding;
            return Task.FromResult(Mocks.Transcript);
        });
        _power.OnRelease = () => savedBeforeRelease = Directory.GetFiles(KalamFolder(), "*.md").Length == 1;
        var run = CreateCommand("test-key").RunAsync("Kalam", transcribe: true);
        await Wait.UntilAsync(() => _capture.Started);
        var hold = _power.Holds.Single();
        var heldWhileRecording = !hold.Released;
        _capture.Deliver(TestAudio.Shorts(1000, -1000));
        _signals.RequestStop();

        Assert.That(await run, Is.EqualTo(0));
        Assert.That(hold.Reason, Is.EqualTo("ClassScribe is recording a class"));
        Assert.That(hold.Needs() & Awake, Is.EqualTo(Awake), "the laptop awake and ClassScribe running");
        Assert.That(heldWhileRecording, Is.True);
        Assert.That(heldDuringUpload, Is.True);
        Assert.That(savedBeforeRelease, Is.True, "let go after the transcript was saved");
        Assert.That(hold.Releases, Is.EqualTo(1));
    }

    /// <summary>Record only keeps the laptop awake until the recording's files are closed: by then its header is final.</summary>
    [Test]
    public async Task Record_only_keeps_the_laptop_awake_until_the_files_are_closed()
    {
        short[]? onDiskAtRelease = null;
        _power.OnRelease = () => onDiskAtRelease = TestAudio.ReadWav(Only("*.wav")).Samples;
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);   // the clock stands still, so only closing the file writes its header
        var run = CreateCommand("test-key", time).RunAsync("Kalam", transcribe: false);
        await Wait.UntilAsync(() => _capture.Started);
        _capture.Deliver(TestAudio.Shorts(1000, -1000));
        _signals.RequestStop();

        Assert.That(await run, Is.EqualTo(0));
        Assert.That(onDiskAtRelease, Is.EqualTo(new short[] { 1000, -1000 }));
        Assert.That(_power.Holds.Single().Releases, Is.EqualTo(1));
    }

    /// <summary>A transcript that fails lets the laptop go, once, as the failure goes on to the caller.</summary>
    [Test]
    public async Task The_laptop_is_let_go_after_a_failed_transcript()
    {
        _client.Setup(Calls.AnyTranscription).ThrowsAsync(new TranscriptionException("ElevenLabs is down."));
        var run = CreateCommand("test-key").RunAsync("Kalam", transcribe: true);
        await Wait.UntilAsync(() => _capture.Started);
        _capture.Deliver(TestAudio.Shorts(1000, -1000));
        _signals.RequestStop();

        Assert.ThrowsAsync<TranscriptionException>(async () => await run);
        Assert.That(_power.Holds.Single().Releases, Is.EqualTo(1));
    }

    /// <summary>A recording with nothing captured lets the laptop go, once.</summary>
    [Test]
    public async Task The_laptop_is_let_go_when_nothing_was_captured()
    {
        var run = CreateCommand("test-key").RunAsync("Kalam", transcribe: true);
        await Wait.UntilAsync(() => _capture.Started);
        _signals.RequestStop();

        Assert.That(await run, Is.EqualTo(1));
        Assert.That(_power.Holds.Single().Releases, Is.EqualTo(1));
    }

    /// <summary>A device in a format ClassScribe cannot read ends the run at the start, and lets the laptop go, once.</summary>
    [Test]
    public void The_laptop_is_let_go_when_capturing_cannot_start()
    {
        _provider = new FakeCaptureProvider();
        _capture = _provider.Enqueue(new FakeAudioCapture(new AudioFormat(16000, 1, 8, false)));   // 8-bit: not readable

        Assert.ThrowsAsync<NotSupportedException>(() => CreateCommand("test-key").RunAsync("Kalam", transcribe: false));
        Assert.That(_power.Holds.Single().Releases, Is.EqualTo(1));
    }

    /// <summary>
    /// The screen is kept on as Recording:KeepScreenOn says: by default (OnBattery) only while the laptop runs on battery,
    /// with Always on mains too, and with Never not even on battery.
    /// </summary>
    [TestCase(KeepScreenOn.OnBattery, true, true)]
    [TestCase(KeepScreenOn.OnBattery, false, false)]
    [TestCase(KeepScreenOn.Always, false, true)]
    [TestCase(KeepScreenOn.Never, true, false)]
    public async Task The_screen_is_kept_on_as_the_setting_says(KeepScreenOn setting, bool onBattery, bool screenOn)
    {
        _powerSource.OnBattery = onBattery;
        var run = CreateCommand("test-key", keepScreenOn: setting).RunAsync("Kalam", transcribe: false);
        await Wait.UntilAsync(() => _capture.Started);
        var needs = _power.Holds.Single().Needs();
        _signals.RequestStop();
        await run;

        Assert.That(needs, Is.EqualTo(screenOn ? Awake | PowerNeeds.Display : Awake));
    }

    /// <summary>By default the screen follows the power source during the class: what the hold needs is read from it each time.</summary>
    [Test]
    public async Task By_default_the_screen_follows_the_power_source_during_the_class()
    {
        var run = CreateCommand("test-key").RunAsync("Kalam", transcribe: false);
        await Wait.UntilAsync(() => _capture.Started);
        var hold = _power.Holds.Single();
        var onMains = hold.Needs();
        _powerSource.OnBattery = true;
        var onBattery = hold.Needs();
        _signals.RequestStop();
        await run;

        Assert.That(onMains, Is.EqualTo(Awake));
        Assert.That(onBattery, Is.EqualTo(Awake | PowerNeeds.Display));
    }

    /// <summary>The start lines say that the lid and the power button still put the laptop to sleep.</summary>
    [Test]
    public async Task The_start_lines_say_to_keep_the_lid_open_and_the_laptop_plugged_in()
    {
        var run = CreateCommand("test-key").RunAsync("Kalam", transcribe: false);
        await Wait.UntilAsync(() => _capture.Started);
        _signals.RequestStop();
        await run;

        Assert.That(_log.Messages, Has.Some.EqualTo("Keep the lid open and the laptop plugged in; closing the lid still puts it to sleep"));
    }

    /// <summary>
    /// 18 seconds in which ClassScribe did not run gives one line, from the last tick before to the first tick after, in
    /// local time: worded as a sleep when the laptop slept, and as a hold-up when it stayed awake. The ticks after it,
    /// a second apart again, give no more.
    /// </summary>
    [TestCase(true, "The laptop slept from {0} to {1}; nothing was recorded then")]
    [TestCase(false, "ClassScribe was held up from {0} to {1} (Windows paused it); nothing was recorded then")]
    public async Task A_freeze_gives_one_line_worded_as_a_sleep_or_a_hold_up(bool slept, string line)
    {
        var start = new DateTimeOffset(2026, 10, 15, 23, 42, 10, TimeSpan.Zero);
        var time = new ManualTimeProvider(start);
        var run = CreateCommand("test-key", time, statusIntervalSeconds: 1).RunAsync("Kalam", transcribe: false);
        await Wait.UntilAsync(() => _capture.Started);
        _capture.Deliver(TestAudio.Shorts(1000, -1000));
        await TickAsync(time, seconds: 1);

        var reported = StatusLines();
        if (slept)
            _times.Sleep(TimeSpan.FromSeconds(18));
        else
            time.Advance(TimeSpan.FromSeconds(18));
        await Wait.UntilAsync(() => StatusLines() > reported);
        await TickAsync(time, seconds: 10);
        _signals.RequestStop();
        await run;

        Assert.That(FreezeLines(), Is.EqualTo(new[] { string.Format(CultureInfo.InvariantCulture, line, Local(start.AddSeconds(1)), Local(start.AddSeconds(19))) }));
    }

    /// <summary>A class during which ClassScribe ran all along, ticking a second apart, gives no such line.</summary>
    [Test]
    public async Task Ticks_a_second_apart_give_no_freeze_line()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var run = CreateCommand("test-key", time, statusIntervalSeconds: 1).RunAsync("Kalam", transcribe: false);
        await Wait.UntilAsync(() => _capture.Started);
        _capture.Deliver(TestAudio.Shorts(1000, -1000));

        await TickAsync(time, seconds: 30);
        _signals.RequestStop();
        await run;

        Assert.That(FreezeLines(), Is.Empty);
    }

    /// <summary>
    /// The recording loop held up by its own work, as when the recording's lock waits for a slow disk to save, is not
    /// taken for a time ClassScribe did not run: the freeze watch goes on ticking on a timer of its own meanwhile. Here the
    /// loop is held for 5 seconds as it logs a status line, while the rest of ClassScribe runs on.
    /// </summary>
    [Test]
    public async Task The_loop_held_up_by_its_own_work_is_not_taken_for_a_freeze()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var run = CreateCommand("test-key", time, statusIntervalSeconds: 1).RunAsync("Kalam", transcribe: false);
        await Wait.UntilAsync(() => _capture.Started);
        _capture.Deliver(TestAudio.Shorts(1000, -1000));
        await TickAsync(time, seconds: 1);

        using (var release = new ManualResetEventSlim())
        {
            _log.HoldNext("Recording: ", release);

            // The loop's next second ends on a thread of its own, on which the loop then runs and is held.
            var nextSecond = Task.Run(() => time.Advance(TimeSpan.FromSeconds(1)));
            await Wait.UntilAsync(() => _log.Held);
            for (var second = 0; second < 5; second++) time.Advance(TimeSpan.FromSeconds(1));
            release.Set();
            await nextSecond.WaitAsync(TimeSpan.FromSeconds(15));
        }

        await TickAsync(time, seconds: 3);
        _signals.RequestStop();
        await run;

        Assert.That(FreezeLines(), Is.Empty);
    }

    /// <summary>
    /// A second that ends while the loop handles a key is not lost: the loop waits for the same second across a key
    /// press, so its tick, and the status line, still come for it. Here the second ends after the loop has handled P and
    /// before it has begun to wait again, while it starts to read the next key.
    /// </summary>
    [Test]
    public async Task A_second_that_ends_while_a_key_is_handled_is_not_lost()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var run = CreateCommand("test-key", time, statusIntervalSeconds: 1).RunAsync("Kalam", transcribe: false);
        await Wait.UntilAsync(() => _capture.Started);

        using (var release = new ManualResetEventSlim())
        {
            _ui.HoldNextRead(release);
            _ui.Type('p');
            await Wait.UntilAsync(() => _ui.ReadHeld);
            var reported = StatusLines();
            time.Advance(TimeSpan.FromSeconds(1));
            release.Set();

            await Wait.UntilAsync(() => StatusLines() > reported);
        }

        _signals.RequestStop();
        await run;
        Assert.That(_log.Messages, Has.Some.StartsWith("Paused"));
    }

    /// <summary>
    /// An app's sound, which arrives all the time, silence included, that stops arriving for 5 seconds is warned about, and
    /// its capture is reopened. The new capture stays silent too, but the warning and the reopening come again only 30
    /// seconds after the last.
    /// </summary>
    [Test]
    public async Task A_continuous_source_that_hands_over_nothing_for_5_seconds_is_warned_about_and_reopened_at_most_once_per_30_seconds()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var first = _provider.AddApp(4242, "chrome", "Speakers", level: 0.3f, AppCapture(4242))!;
        var second = _provider.AddApp(4242, "chrome", "Speakers", level: 0.3f, AppCapture(4242))!;   // ready for each reopening
        var third = _provider.AddApp(4242, "chrome", "Speakers", level: 0.3f, AppCapture(4242))!;
        var run = CreateCommand("test-key", time, statusIntervalSeconds: 1, app: "chrome").RunAsync("Kalam", transcribe: false);
        await Wait.UntilAsync(() => first.Started);

        await TickAsync(time, seconds: 4);
        Assert.That(ArrivalWarnings(), Is.Empty, "not before 5 seconds");
        await TickAsync(time, seconds: 1);
        Assert.That(ArrivalWarnings(), Is.EqualTo(new[]
        {
            "No audio has arrived from \"chrome\" for 5 seconds although it should; the recording has fallen behind the clock. Reopening it"
        }));
        await TickAsync(time, seconds: 1);
        await Wait.UntilAsync(() => second.Started);
        Assert.That(first.Disposed, Is.True, "the stalled capture is replaced");

        await TickAsync(time, seconds: 28);
        Assert.That(ArrivalWarnings(), Has.Length.EqualTo(1), "not again within 30 seconds");
        await TickAsync(time, seconds: 1);
        Assert.That(ArrivalWarnings(), Has.Length.EqualTo(2), "again 30 seconds after the first");
        await TickAsync(time, seconds: 1);
        await Wait.UntilAsync(() => third.Started);

        _signals.RequestStop();
        Assert.That(await run, Is.EqualTo(1), "nothing was captured");
    }

    /// <summary>
    /// While paused, an app's sound that stops arriving is reopened too, so that the recording can go on when the pause
    /// ends; the warning says so, rather than speak of a recording falling behind while nothing is being recorded.
    /// </summary>
    [Test]
    public async Task While_paused_a_continuous_source_that_stops_arriving_is_reopened_for_after_the_pause()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var first = _provider.AddApp(4242, "chrome", "Speakers", level: 0.3f, AppCapture(4242))!;
        var second = _provider.AddApp(4242, "chrome", "Speakers", level: 0.3f, AppCapture(4242))!;   // ready for the reopening
        var run = CreateCommand("test-key", time, statusIntervalSeconds: 1, app: "chrome").RunAsync("Kalam", transcribe: false);
        await Wait.UntilAsync(() => first.Started);
        _ui.Type('p');
        await Wait.UntilAsync(() => _log.Messages.Any(m => m.StartsWith("Paused", StringComparison.Ordinal)));

        await TickAsync(time, seconds: 5);
        Assert.That(ArrivalWarnings(), Is.EqualTo(new[]
        {
            "No audio has arrived from \"chrome\" for 5 seconds although it should. Reopening it, so that the recording can go on when you press P again"
        }));
        await TickAsync(time, seconds: 1);
        await Wait.UntilAsync(() => second.Started);

        _signals.RequestStop();
        await run;
        Assert.That(first.Disposed, Is.True, "the stalled capture is replaced during the pause");
    }

    /// <summary>
    /// A device whose meter has shown sound for 10 seconds while no audio arrived from its capture is warned about, and its
    /// capture is reopened: a device's capture hands over audio whenever something plays on it.
    /// </summary>
    [Test]
    public async Task A_device_that_plays_for_10_seconds_while_no_audio_arrives_is_warned_about_and_reopened()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        _provider.SetLevel("Fake Speakers", 0.5f);
        var reopened = _provider.Enqueue(new FakeAudioCapture(Pcm16KMono));   // the same device, ready for the reopening
        var run = CreateCommand("test-key", time, statusIntervalSeconds: 1).RunAsync("Kalam", transcribe: false);
        await Wait.UntilAsync(() => _capture.Started);

        await TickAsync(time, seconds: 10);
        Assert.That(ArrivalWarnings(), Is.Empty, "not before the meter has shown sound for 10 seconds");
        await TickAsync(time, seconds: 1);
        Assert.That(ArrivalWarnings(), Is.EqualTo(new[]
        {
            "No audio has arrived from \"Fake Speakers\" for 10 seconds although Windows shows sound playing there; the recording has fallen behind the clock. Reopening it"
        }));
        await TickAsync(time, seconds: 1);
        await Wait.UntilAsync(() => reopened.Started);

        _signals.RequestStop();
        await run;
        Assert.That(_capture.Disposed, Is.True, "the stalled capture is replaced");
    }

    /// <summary>
    /// A device that falls quiet hands over nothing, and that is no stall: once its meter shows no sound, the time it
    /// showed sound before no longer counts, so a quiet stretch after the class has played is neither warned about nor
    /// reopened, however long it lasts.
    /// </summary>
    [Test]
    public async Task A_device_that_falls_quiet_is_not_warned_about()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        _provider.SetLevel("Fake Speakers", 0.5f);
        _provider.Enqueue(new FakeAudioCapture(Pcm16KMono));   // the same device, ready for a reopening that must not come
        var run = CreateCommand("test-key", time, statusIntervalSeconds: 1).RunAsync("Kalam", transcribe: false);
        await Wait.UntilAsync(() => _capture.Started);
        for (var second = 0; second < 3; second++)
        {
            _capture.Deliver(TestAudio.Shorts(1000, -1000));
            await TickAsync(time, seconds: 1);
        }

        await SetLevelAsync("Fake Speakers", 0f);   // the class falls quiet: nothing plays, so nothing arrives
        await TickAsync(time, seconds: 40);
        _signals.RequestStop();
        await run;

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
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        _provider.SetLevel("Fake Speakers", 0.5f);
        _provider.Enqueue(new FakeAudioCapture(Pcm16KMono));   // the same device, ready for the reopening
        var run = CreateCommand("test-key", time, statusIntervalSeconds: 1).RunAsync("Kalam", transcribe: false);
        await Wait.UntilAsync(() => _capture.Started);

        await TickAsync(time, seconds: 6);
        await SetLevelAsync("Fake Speakers", 0f);
        await TickAsync(time, seconds: 1);
        await SetLevelAsync("Fake Speakers", 0.5f);
        await TickAsync(time, seconds: 10);
        Assert.That(ArrivalWarnings(), Is.Empty, "not before the meter has shown sound for 10 seconds again");
        await TickAsync(time, seconds: 1);
        Assert.That(ArrivalWarnings(), Is.EqualTo(new[]
        {
            "No audio has arrived from \"Fake Speakers\" for 10 seconds although Windows shows sound playing there; the recording has fallen behind the clock. Reopening it"
        }));

        _signals.RequestStop();
        await run;
    }

    /// <summary>
    /// Silence is audio too: an app's capture that hands over only silence, as it does while the class is quiet, keeps
    /// arriving, so it is neither warned about nor reopened. Only a capture that hands over nothing at all is.
    /// </summary>
    [Test]
    public async Task An_app_that_hands_over_only_silence_is_not_warned_about()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var chrome = _provider.AddApp(4242, "chrome", "Speakers", capture: AppCapture(4242))!;
        var run = CreateCommand("test-key", time, statusIntervalSeconds: 1, app: "chrome").RunAsync("Kalam", transcribe: false);
        await Wait.UntilAsync(() => chrome.Started);

        for (var second = 0; second < 40; second++)
        {
            chrome.Deliver(TestAudio.Shorts(0, 0));
            await TickAsync(time, seconds: 1);
        }

        _signals.RequestStop();
        await run;

        Assert.That(ArrivalWarnings(), Is.Empty);
        Assert.That(_provider.Created, Is.EqualTo(new[] { chrome }), "never reopened");
    }

    /// <summary>
    /// Audio that keeps arriving is never warned about: from a device whose meter shows sound, as from an app, nor is its
    /// capture reopened.
    /// </summary>
    [TestCase(false)]
    [TestCase(true)]
    public async Task Audio_that_keeps_arriving_is_not_warned_about(bool app)
    {
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var capture = _capture;
        if (app)
            capture = _provider.AddApp(4242, "chrome", "Speakers", level: 0.3f, AppCapture(4242))!;
        else
            _provider.SetLevel("Fake Speakers", 0.5f);
        var run = CreateCommand("test-key", time, statusIntervalSeconds: 1, app: app ? "chrome" : "").RunAsync("Kalam", transcribe: false);
        await Wait.UntilAsync(() => capture.Started);

        for (var second = 0; second < 40; second++)
        {
            capture.Deliver(TestAudio.Shorts(1000, -1000));
            await TickAsync(time, seconds: 1);
        }

        _signals.RequestStop();
        await run;

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
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var chrome = _provider.AddApp(4242, "chrome", "Speakers", level: 0.3f, AppCapture(4242))!;
        _provider.AddApp(4242, "chrome", "Speakers", level: 0.3f, AppCapture(4242));   // ready for a reopening
        var run = CreateCommand("test-key", time, statusIntervalSeconds: 1, app: "chrome").RunAsync("Kalam", transcribe: false);
        await Wait.UntilAsync(() => chrome.Started);
        for (var second = 0; second < 2; second++)
        {
            chrome.Deliver(TestAudio.Shorts(1000, -1000));
            await TickAsync(time, seconds: 1);
        }

        var reported = StatusLines();
        _times.Sleep(TimeSpan.FromSeconds(18));
        await Wait.UntilAsync(() => StatusLines() > reported);
        Assert.That(ArrivalWarnings(), Is.Empty, "not on waking");
        for (var second = 1; second <= 5; second++)
        {
            if (arrivesAgain) chrome.Deliver(TestAudio.Shorts(1000, -1000));
            await TickAsync(time, seconds: 1);
            if (second < 5) Assert.That(ArrivalWarnings(), Is.Empty, $"not {second} seconds after waking");
        }

        _signals.RequestStop();
        await run;

        Assert.That(FreezeLines(), Has.Length.EqualTo(1));
        Assert.That(ArrivalWarnings(), arrivesAgain
            ? Is.Empty
            : Is.EqualTo(new[] { "No audio has arrived from \"chrome\" for 5 seconds although it should; the recording has fallen behind the clock. Reopening it" }));
    }

    /// <summary>
    /// The record command resolves in a run's scope with what keeps the laptop awake and the computer's clocks, as the app
    /// registers them, and each run gets a freeze watch of its own.
    /// </summary>
    [Test, Platform("Win")]
    [SupportedOSPlatform("windows")]
    public void The_record_command_resolves_in_a_run_scope_with_a_freeze_watch_of_its_own()
    {
        File.WriteAllText(_folder.File("appsettings.json"), "{}");
        using var app = AppHost.Build(_folder.Path, "dummy-key-for-a-local-test", services => services.AddSingleton<ILoopbackCaptureProvider>(_provider));
        using var first = app.Services.CreateScope();
        using var second = app.Services.CreateScope();

        Assert.That(first.ServiceProvider.GetRequiredService<RecordCommand>(), Is.Not.Null);
        Assert.That(first.ServiceProvider.GetRequiredService<FreezeWatch>(), Is.SameAs(first.ServiceProvider.GetRequiredService<FreezeWatch>()), "one per run");
        Assert.That(first.ServiceProvider.GetRequiredService<FreezeWatch>(), Is.Not.SameAs(second.ServiceProvider.GetRequiredService<FreezeWatch>()), "each run its own");
        Assert.That(app.Services.GetRequiredService<IPowerRequests>(), Is.InstanceOf<WindowsPowerRequests>());
        Assert.That(app.Services.GetRequiredService<IPowerSource>(), Is.InstanceOf<WindowsPowerSource>());
        Assert.That(app.Services.GetRequiredService<ISystemTimes>(), Is.InstanceOf<WindowsSystemTimes>());
    }
}
