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
        _log = new RecordingLogger<RecordCommand>();
    }

    /// <summary>Deletes the temporary folder. That fails if a test left a file open, which is the point.</summary>
    [TearDown]
    public void TearDown() => _folder.Dispose();

    /// <summary>Builds the command with the real session, preparer and workflow around the simulated device and the mocked service.</summary>
    private RecordCommand CreateCommand(string apiKey, TimeProvider? time = null, bool openWhenDone = false, int statusIntervalSeconds = 60, string device = "")
    {
        time ??= TimeProvider.System;
        var recording = Options.Create(new RecordingOptions
        {
            OutputFolder = _folder.Path,
            OpenFolderWhenDone = openWhenDone,
            OpenTranscriptWhenDone = openWhenDone,
            StatusIntervalSeconds = statusIntervalSeconds,
            Device = device
        });
        var elevenLabs = Options.Create(new ElevenLabsOptions { ApiKey = apiKey, KeytermsFile = _folder.File("no-glossary.txt") });
        var folders = new ClassFolders(recording, time);
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

        return new RecordCommand(session, workflow, _signals, _ui, folders, marks, _desktop.Object, recording, elevenLabs, time, _log);
    }

    /// <summary>The one class folder the command created.</summary>
    private string ClassFolder() => Directory.GetDirectories(_folder.Path).Single();

    /// <summary>Recording runs until stopped, and the recording is then transcribed.</summary>
    [Test]
    public async Task A_class_is_recorded_until_stopped_and_then_transcribed()
    {
        var run = CreateCommand("test-key").RunAsync(transcribe: true);
        await Wait.UntilAsync(() => _capture.Started);
        _capture.Deliver(TestAudio.Shorts(TestAudio.Tone(16000, seconds: 0.5, frequency: 440, amplitude: 0.5)));
        _signals.RequestStop();

        var exitCode = await run;

        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(File.Exists(Path.Combine(ClassFolder(), "recording-001.wav")), Is.True);
        Assert.That(File.ReadAllText(Path.Combine(ClassFolder(), "transcript.txt")), Does.Contain("Hello world."));
        _client.Verify(Calls.TranscriptionOf(Path.Combine(ClassFolder(), "upload.wav")), Times.Once);
    }

    /// <summary>Record only keeps the recording and sends nothing.</summary>
    [Test]
    public async Task Record_only_keeps_the_audio_and_sends_nothing()
    {
        var run = CreateCommand("test-key", openWhenDone: true).RunAsync(transcribe: false);
        await Wait.UntilAsync(() => _capture.Started);
        _capture.Deliver(TestAudio.Shorts(1000, -1000));
        _signals.RequestStop();

        var exitCode = await run;

        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(TestAudio.ReadWav(Path.Combine(ClassFolder(), "recording-001.wav")).Samples, Is.EqualTo(new short[] { 1000, -1000 }));
        Assert.That(File.Exists(Path.Combine(ClassFolder(), "transcript.txt")), Is.False);
        _client.Verify(Calls.AnyTranscription, Times.Never);
        _desktop.Verify(d => d.OpenFolder(ClassFolder()), Times.Once);
    }

    /// <summary>When the transcript is ready, its folder and the transcript itself are opened, as the settings ask.</summary>
    [Test]
    public async Task The_folder_and_the_transcript_are_opened_when_it_is_ready()
    {
        var run = CreateCommand("test-key", openWhenDone: true).RunAsync(transcribe: true);
        await Wait.UntilAsync(() => _capture.Started);
        _capture.Deliver(TestAudio.Shorts(1000, -1000));
        _signals.RequestStop();
        await run;

        _desktop.Verify(d => d.OpenFolder(ClassFolder()), Times.Once);
        _desktop.Verify(d => d.OpenFile(Path.Combine(ClassFolder(), "transcript.txt")), Times.Once);
    }

    /// <summary>Q stops the recording, as the first Ctrl+C does.</summary>
    [Test]
    public async Task Q_stops_the_recording()
    {
        var run = CreateCommand("test-key").RunAsync(transcribe: false);
        await Wait.UntilAsync(() => _capture.Started);
        _capture.Deliver(TestAudio.Shorts(1000, -1000));
        _ui.Type('q');

        Assert.That(await run, Is.EqualTo(0));
    }

    /// <summary>P pauses and resumes: audio that arrives during the pause is not kept.</summary>
    [Test]
    public async Task P_pauses_and_resumes_the_recording()
    {
        var run = CreateCommand("test-key").RunAsync(transcribe: false);
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

        Assert.That(TestAudio.ReadWav(Path.Combine(ClassFolder(), "recording-001.wav")).Samples, Is.EqualTo(new short[] { 1000, -1000, -7, 7 }));
    }

    /// <summary>While paused there is no warning about silence, and the status line says the recording is paused.</summary>
    [Test]
    public async Task No_silence_warning_is_given_while_paused()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var run = CreateCommand("test-key", time, statusIntervalSeconds: 1).RunAsync(transcribe: false);
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
        var run = CreateCommand("test-key", time, statusIntervalSeconds: 1, device: "Pixel Buds").RunAsync(transcribe: false);

        await Wait.UntilAsync(() =>
        {
            time.Advance(TimeSpan.FromSeconds(61));
            return _log.Messages.Any(m => m.Contains("waiting for the output device \"Pixel Buds\""));
        });
        _signals.RequestStop();
        await run;

        Assert.That(_log.Problems.Where(p => p.StartsWith("No sound")), Is.Empty);
    }

    /// <summary>M marks the moment: its position goes to marks.txt at once, and the mark shows in the transcript.</summary>
    [Test]
    public async Task M_marks_the_moment_in_the_recording_and_the_transcript()
    {
        var run = CreateCommand("test-key").RunAsync(transcribe: true);
        await Wait.UntilAsync(() => _capture.Started);
        _capture.Deliver(TestAudio.Shorts(TestAudio.Tone(16000, seconds: 0.5, frequency: 440, amplitude: 0.5)));
        _ui.Type('m');
        await Wait.UntilAsync(() => File.Exists(Path.Combine(ClassFolder(), ClassMarks.FileName)));
        _signals.RequestStop();
        await run;

        Assert.That(File.ReadAllLines(Path.Combine(ClassFolder(), ClassMarks.FileName)), Is.EqualTo(new[] { "00:00:00.500" }));
        Assert.That(File.ReadAllText(Path.Combine(ClassFolder(), "transcript.txt")), Does.Contain("*** Mark 1 ***"));
    }

    /// <summary>Without a key the class is still recorded, so it can be transcribed later.</summary>
    [Test]
    public async Task Without_a_key_the_class_is_still_recorded()
    {
        var run = CreateCommand(apiKey: "").RunAsync(transcribe: true);
        await Wait.UntilAsync(() => _capture.Started);
        _capture.Deliver(TestAudio.Shorts(1000, -1000));
        _signals.RequestStop();

        var exitCode = await run;

        Assert.That(exitCode, Is.EqualTo(1));
        Assert.That(TestAudio.ReadWav(Path.Combine(ClassFolder(), "recording-001.wav")).Samples, Is.EqualTo(new short[] { 1000, -1000 }));
        _client.Verify(Calls.AnyTranscription, Times.Never);
    }

    /// <summary>Nothing captured means nothing to pay for, so nothing is uploaded.</summary>
    [Test]
    public async Task Nothing_is_uploaded_when_nothing_was_captured()
    {
        var run = CreateCommand("test-key").RunAsync(transcribe: true);
        await Wait.UntilAsync(() => _capture.Started);
        _signals.RequestStop();

        var exitCode = await run;

        Assert.That(exitCode, Is.EqualTo(1));
        _client.Verify(Calls.AnyTranscription, Times.Never);
    }

    /// <summary>A failed transcript leaves the recording in place for another try.</summary>
    [Test]
    public async Task A_failed_transcript_leaves_the_recording_in_place()
    {
        _client.Setup(Calls.AnyTranscription).ThrowsAsync(new TranscriptionException("ElevenLabs is down."));
        var run = CreateCommand("test-key").RunAsync(transcribe: true);
        await Wait.UntilAsync(() => _capture.Started);
        _capture.Deliver(TestAudio.Shorts(1000, -1000));
        _signals.RequestStop();

        Assert.ThrowsAsync<TranscriptionException>(async () => await run);
        Assert.That(File.Exists(Path.Combine(ClassFolder(), "recording-001.wav")), Is.True);
    }
}
