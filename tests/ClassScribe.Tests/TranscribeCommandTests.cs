using ClassScribe.Audio;
using ClassScribe.Commands;
using ClassScribe.Settings;
using ClassScribe.Transcription;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace ClassScribe.Tests;

/// <summary>The transcribe command's choice of what to send, with a mocked preparer and service.</summary>
[TestFixture]
public class TranscribeCommandTests
{
    private TempFolder _folder = null!;
    private Mock<IAudioPreparer> _preparer = null!;
    private Mock<ITranscriptionClient> _client = null!;

    /// <summary>Creates a temporary folder and the mocks.</summary>
    [SetUp]
    public void SetUp()
    {
        _folder = new TempFolder();
        _preparer = Mocks.AudioPreparer();
        _client = Mocks.TranscriptionClient();
    }

    /// <summary>Deletes the temporary folder. That fails if a test left a file open, which is the point.</summary>
    [TearDown]
    public void TearDown() => _folder.Dispose();

    /// <summary>Builds the command with the real workflow around the mocks.</summary>
    private TranscribeCommand CreateCommand()
    {
        var elevenLabs = Options.Create(new ElevenLabsOptions { ApiKey = "test-key", KeytermsFile = _folder.File("no-glossary.txt") });
        var workflow = new TranscriptionWorkflow(
            _preparer.Object,
            _client.Object,
            new KeytermLoader(NullLogger<KeytermLoader>.Instance),
            new TranscriptFormatter(Options.Create(new TranscriptOptions())),
            elevenLabs,
            Options.Create(new RecordingOptions()),
            NullLogger<TranscriptionWorkflow>.Instance);

        return new TranscribeCommand(workflow, new FakeUserSignals(), elevenLabs, NullLogger<TranscribeCommand>.Instance);
    }

    [Test]
    public async Task A_class_folder_is_sent_as_its_segments_in_recording_order()
    {
        string[] names =
        [
            "recording-010.wav", "recording-002.wav", "recording-001.wav",   // the recorder's segments
            "recording-001.upload.wav", "recording-notes.wav", "upload.wav", "transcript.txt"   // everything else stays out
        ];
        foreach (var name in names) File.WriteAllBytes(_folder.File(name), [0]);

        var exitCode = await CreateCommand().RunAsync(_folder.Path);

        string[] segments = [_folder.File("recording-001.wav"), _folder.File("recording-002.wav"), _folder.File("recording-010.wav")];
        Assert.That(exitCode, Is.EqualTo(0));
        _preparer.Verify(p => p.Prepare(segments, _folder.File("upload.wav")), Times.Once);
    }

    [Test]
    public async Task A_folder_without_segments_is_an_error()
    {
        File.WriteAllBytes(_folder.File("upload.wav"), [0]);

        var exitCode = await CreateCommand().RunAsync(_folder.Path);

        Assert.That(exitCode, Is.EqualTo(1));
        _client.Verify(Calls.AnyTranscription, Times.Never);
    }

    [Test]
    public async Task A_single_file_is_sent_on_its_own()
    {
        var video = _folder.File("class.mp4");
        File.WriteAllBytes(video, [1, 2, 3]);

        var exitCode = await CreateCommand().RunAsync(video);

        Assert.That(exitCode, Is.EqualTo(0));
        _client.Verify(Calls.TranscriptionOf(video), Times.Once);
    }
}
