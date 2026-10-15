using System.Globalization;
using ClassScribe.Audio;
using ClassScribe.Commands;
using ClassScribe.Settings;
using ClassScribe.Transcription;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace ClassScribe.Tests;

/// <summary>
/// The record command from start to saved transcript: the real session, preparer and workflow,
/// driven by a simulated audio device, with the transcription service mocked.
/// </summary>
[TestFixture]
public class RecordCommandTests
{
    private TempFolder _folder = null!;
    private FakeCaptureProvider _provider = null!;
    private FakeAudioCapture _capture = null!;
    private FakeUserSignals _signals = null!;
    private Mock<ITranscriptionClient> _client = null!;

    /// <summary>Creates a temporary folder, a simulated audio device and the mocked service.</summary>
    [SetUp]
    public void SetUp()
    {
        _folder = new TempFolder();
        _provider = new FakeCaptureProvider();
        _capture = _provider.Enqueue(new FakeAudioCapture(new AudioFormat(16000, 1, 16, false)));
        _signals = new FakeUserSignals();
        _client = Mocks.TranscriptionClient();
    }

    /// <summary>Deletes the temporary folder. That fails if a test left a file open, which is the point.</summary>
    [TearDown]
    public void TearDown() => _folder.Dispose();

    /// <summary>Builds the command with the real session, preparer and workflow around the simulated device and the mocked service.</summary>
    private RecordCommand CreateCommand(string apiKey)
    {
        var recording = Options.Create(new RecordingOptions { OutputFolder = _folder.Path, OpenFolderWhenDone = false });
        var elevenLabs = Options.Create(new ElevenLabsOptions { ApiKey = apiKey, KeytermsFile = _folder.File("no-glossary.txt") });
        var session = new RecordingSession(_provider, recording, TimeProvider.System, NullLogger<RecordingSession>.Instance);
        var workflow = new TranscriptionWorkflow(
            new AudioPreparer(NullLogger<AudioPreparer>.Instance),
            _client.Object,
            new KeytermLoader(NullLogger<KeytermLoader>.Instance),
            new TranscriptFormatter(Options.Create(new TranscriptOptions())),
            elevenLabs,
            recording,
            NullLogger<TranscriptionWorkflow>.Instance);

        return new RecordCommand(session, workflow, _signals, recording, elevenLabs, TimeProvider.System, NullLogger<RecordCommand>.Instance);
    }

    /// <summary>The one class folder the command created.</summary>
    private string ClassFolder() => Directory.GetDirectories(_folder.Path).Single();

    [Test]
    public async Task A_class_is_recorded_until_stopped_and_then_transcribed()
    {
        var run = CreateCommand("test-key").RunAsync();
        await Wait.UntilAsync(() => _capture.Started);
        _capture.Deliver(TestAudio.Shorts(TestAudio.Tone(16000, seconds: 0.5, frequency: 440, amplitude: 0.5)));
        _signals.RequestStop();

        var exitCode = await run;

        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(File.Exists(Path.Combine(ClassFolder(), "recording-001.wav")), Is.True);
        Assert.That(File.ReadAllText(Path.Combine(ClassFolder(), "transcript.txt")), Does.Contain("Hello world."));
        _client.Verify(Calls.TranscriptionOf(Path.Combine(ClassFolder(), "upload.wav")), Times.Once);
    }

    [Test, SetCulture("ar-SA")]   // a region whose own calendar would name the folder 1448-04-30
    public async Task The_class_folder_carries_the_Gregorian_date_under_any_regional_settings()
    {
        var run = CreateCommand("test-key").RunAsync();
        await Wait.UntilAsync(() => _capture.Started);
        _signals.RequestStop();
        await run;

        var year = DateTime.Now.Year.ToString(CultureInfo.InvariantCulture);
        Assert.That(Path.GetFileName(ClassFolder()), Does.Match($@"^{year}-\d\d-\d\d_\d\d-\d\d-\d\d$"));
    }

    [Test]
    public async Task Without_a_key_the_class_is_still_recorded()
    {
        var run = CreateCommand(apiKey: "").RunAsync();
        await Wait.UntilAsync(() => _capture.Started);
        _capture.Deliver(TestAudio.Shorts(1000, -1000));
        _signals.RequestStop();

        var exitCode = await run;

        Assert.That(exitCode, Is.EqualTo(1));
        Assert.That(TestAudio.ReadWav(Path.Combine(ClassFolder(), "recording-001.wav")).Samples, Is.EqualTo(new short[] { 1000, -1000 }));
        _client.Verify(Calls.AnyTranscription, Times.Never);
    }

    [Test]
    public async Task Nothing_is_uploaded_when_nothing_was_captured()
    {
        var run = CreateCommand("test-key").RunAsync();
        await Wait.UntilAsync(() => _capture.Started);
        _signals.RequestStop();

        var exitCode = await run;

        Assert.That(exitCode, Is.EqualTo(1));
        _client.Verify(Calls.AnyTranscription, Times.Never);
    }

    [Test]
    public async Task A_failed_transcript_leaves_the_recording_in_place()
    {
        _client.Setup(Calls.AnyTranscription).ThrowsAsync(new TranscriptionException("ElevenLabs is down."));
        var run = CreateCommand("test-key").RunAsync();
        await Wait.UntilAsync(() => _capture.Started);
        _capture.Deliver(TestAudio.Shorts(1000, -1000));
        _signals.RequestStop();

        Assert.ThrowsAsync<TranscriptionException>(async () => await run);
        Assert.That(File.Exists(Path.Combine(ClassFolder(), "recording-001.wav")), Is.True);
    }
}
