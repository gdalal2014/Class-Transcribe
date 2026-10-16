using System.Net;
using System.Runtime.Versioning;
using ClassScribe.Audio;
using ClassScribe.Commands;
using ClassScribe.Settings;
using ClassScribe.Storage;
using ClassScribe.Transcription;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace ClassScribe.Tests;

/// <summary>
/// The record command from start to saved transcript: the real session, preparer and workflow,
/// driven by a simulated audio device and keyboard, with the transcription service mocked.
/// </summary>
[TestFixture]
public class RecordCommandTests
{
    private TempFolder _folder = null!;
    private FakeCaptureProvider _provider = null!;
    private FakeAudioCapture _capture = null!;
    private FakeUserSignals _signals = null!;
    private FakeConsoleUi _ui = null!;
    private Mock<ITranscriptionClient> _client = null!;
    private Mock<IDesktop> _desktop = null!;
    private Mock<IExitWarnings> _exitWarnings = null!;
    private RecordingLogger<RecordCommand> _log = null!;

    /// <summary>Creates a temporary folder, a simulated audio device and keyboard, and the mocked services.</summary>
    [SetUp]
    public void SetUp()
    {
        _folder = new TempFolder();
        _provider = new FakeCaptureProvider();
        _capture = _provider.Enqueue(new FakeAudioCapture(new AudioFormat(16000, 1, 16, false)));
        _signals = new FakeUserSignals();
        _ui = new FakeConsoleUi();
        _client = Mocks.TranscriptionClient();
        _desktop = new Mock<IDesktop>();
        _exitWarnings = new Mock<IExitWarnings>();
        _exitWarnings.Setup(w => w.Register(It.IsAny<Action>())).Returns(Mock.Of<IDisposable>());
        _log = new RecordingLogger<RecordCommand>();
    }

    /// <summary>Deletes the temporary folder. That fails if a test left a file open, which is the point.</summary>
    [TearDown]
    public void TearDown() => _folder.Dispose();

    /// <summary>
    /// Builds the command with the real session, preparer and workflow around the simulated device and the
    /// mocked service. The recordings go to the temporary folder unless <paramref name="outputFolder"/> names another.
    /// </summary>
    private RecordCommand CreateCommand(
        string apiKey, TimeProvider? time = null, bool openWhenDone = false, int statusIntervalSeconds = 60, string device = "", string? outputFolder = null, int silenceWarningSeconds = 60)
    {
        time ??= TimeProvider.System;
        var recording = Options.Create(new RecordingOptions
        {
            OutputFolder = outputFolder ?? _folder.Path,
            OpenFolderWhenDone = openWhenDone,
            OpenTranscriptWhenDone = openWhenDone,
            StatusIntervalSeconds = statusIntervalSeconds,
            SilenceWarningSeconds = silenceWarningSeconds,
            Device = device
        });
        var elevenLabs = Options.Create(new ElevenLabsOptions { ApiKey = apiKey, KeytermsFile = _folder.File("no-glossary.txt") });
        var folders = new ClassFolders(recording, time, NullLogger<ClassFolders>.Instance);
        var marks = new ClassMarks(NullLogger<ClassMarks>.Instance);
        var session = new RecordingSession(_provider, new CaptureChooser(time, new SourceChoice(recording)), recording, time, NullLogger<RecordingSession>.Instance);
        var workflow = new TranscriptionWorkflow(
            new AudioPreparer(NullLogger<AudioPreparer>.Instance),
            _client.Object,
            new KeytermLoader(NullLogger<KeytermLoader>.Instance),
            new TranscriptFormatter(Options.Create(new TranscriptOptions())),
            folders,
            marks,
            elevenLabs,
            recording,
            NullLogger<TranscriptionWorkflow>.Instance);

        return new RecordCommand(session, workflow, _signals, _ui, folders, marks, _desktop.Object, _exitWarnings.Object, recording, elevenLabs, time, _log);
    }

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
    /// hours and minutes, so that it reads at a glance however long the class has been silent.
    /// </summary>
    [TestCase(1, "No sound for 1 second")]
    [TestCase(120, "No sound for 120 seconds")]
    [TestCase(121, "No sound for 2 minutes")]
    [TestCase(179, "No sound for 2 minutes")]
    [TestCase(3599, "No sound for 59 minutes")]
    [TestCase(3600, "No sound for 1 hour")]
    [TestCase(3660, "No sound for 1 hour 1 minute")]
    [TestCase(9006, "No sound for 2 hours 30 minutes")]
    [TestCase(90000, "No sound for 25 hours")]
    public async Task A_silence_is_given_in_units_that_read_at_a_glance(int seconds, string warning)
    {
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var run = CreateCommand("test-key", time, statusIntervalSeconds: 1, silenceWarningSeconds: 1).RunAsync("Kalam", transcribe: false);
        await Wait.UntilAsync(() => _capture.Started);

        await JumpAsync(time, TimeSpan.FromSeconds(seconds));
        _signals.RequestStop();
        await run;

        Assert.That(SilenceWarnings(), Is.EqualTo(new[] { warning }));
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
        var problem = new ClassFolders(Options.Create(new RecordingOptions { OutputFolder = outputFolder }), TimeProvider.System, NullLogger<ClassFolders>.Instance).DescribeRootProblem();
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
    /// An answer that ElevenLabs charged for but that could not be read is kept, and the hint points to it
    /// before offering another try, which is charged again.
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
        Assert.That(_log.Problems.Last(), Does.Contain($"kept in {Only("*.unread.json")}")
            .And.Contain("charged again")
            .And.Contain(CommandLine.Transcribe(Recording())));
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
}
