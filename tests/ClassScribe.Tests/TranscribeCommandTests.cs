using ClassScribe.Audio;
using ClassScribe.Commands;
using ClassScribe.Settings;
using ClassScribe.Storage;
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
    private Mock<IDesktop> _desktop = null!;

    /// <summary>Creates a temporary folder and the mocks.</summary>
    [SetUp]
    public void SetUp()
    {
        _folder = new TempFolder();
        _preparer = Mocks.AudioPreparer();
        _client = Mocks.TranscriptionClient();
        _desktop = new Mock<IDesktop>();
    }

    /// <summary>Deletes the temporary folder. That fails if a test left a file open, which is the point.</summary>
    [TearDown]
    public void TearDown() => _folder.Dispose();

    /// <summary>Builds the command with the real workflow around the mocks, with its recordings folder inside the temporary folder.</summary>
    private TranscribeCommand CreateCommand(bool openWhenDone = false)
    {
        var recording = Options.Create(new RecordingOptions
        {
            OutputFolder = _folder.File("Recordings"),
            OpenFolderWhenDone = openWhenDone,
            OpenTranscriptWhenDone = openWhenDone
        });
        var elevenLabs = Options.Create(new ElevenLabsOptions { ApiKey = "test-key", KeytermsFile = _folder.File("no-glossary.txt") });
        var workflow = new TranscriptionWorkflow(
            _preparer.Object,
            _client.Object,
            new KeytermLoader(NullLogger<KeytermLoader>.Instance),
            new TranscriptFormatter(Options.Create(new TranscriptOptions())),
            new ClassFolders(recording, TimeProvider.System),
            new ClassMarks(NullLogger<ClassMarks>.Instance),
            elevenLabs,
            recording,
            NullLogger<TranscriptionWorkflow>.Instance);

        return new TranscribeCommand(workflow, new FakeUserSignals(), _desktop.Object, recording, elevenLabs, NullLogger<TranscribeCommand>.Instance);
    }

    /// <summary>A class folder is sent as its segment files, in the order they were recorded, and nothing else.</summary>
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

    /// <summary>A folder without the recorder's segment files is an error, and nothing is sent.</summary>
    [Test]
    public async Task A_folder_without_segments_is_an_error()
    {
        File.WriteAllBytes(_folder.File("upload.wav"), [0]);

        var exitCode = await CreateCommand().RunAsync(_folder.Path);

        Assert.That(exitCode, Is.EqualTo(1));
        _client.Verify(Calls.AnyTranscription, Times.Never);
    }

    /// <summary>A single file is sent on its own.</summary>
    [Test]
    public async Task A_single_file_is_sent_on_its_own()
    {
        var video = _folder.File("class.mp4");
        File.WriteAllBytes(video, [1, 2, 3]);

        var exitCode = await CreateCommand().RunAsync(video);

        Assert.That(exitCode, Is.EqualTo(0));
        _client.Verify(Calls.TranscriptionOf(video), Times.Once);
    }

    /// <summary>When the transcript is ready, it and its folder are opened, as the settings ask.</summary>
    [Test]
    public async Task The_transcript_and_its_folder_are_opened_when_ready()
    {
        var video = _folder.File("class.mp4");
        File.WriteAllBytes(video, [1, 2, 3]);
        var transcriptFolder = Path.Combine(_folder.File("Recordings"), "class");

        await CreateCommand(openWhenDone: true).RunAsync(video);

        _desktop.Verify(d => d.OpenFolder(transcriptFolder), Times.Once);
        _desktop.Verify(d => d.OpenFile(Path.Combine(transcriptFolder, "transcript.txt")), Times.Once);
    }
}
