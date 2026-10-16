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

    /// <summary>Writes a recording's segment files into the temporary folder and returns the recording.</summary>
    private RecordingFiles WriteRecording(string name, DateTimeOffset started, int segments = 1)
    {
        var files = RecordingFiles.ForNew(_folder.Path, name, started);
        for (var i = 1; i <= segments; i++) File.WriteAllBytes(files.Segment(i), [0]);
        return files;
    }

    /// <summary>A folder is transcribed from the recording started last in it: its segments in order, and nothing else.</summary>
    [Test]
    public async Task A_folder_is_transcribed_from_its_newest_recording()
    {
        WriteRecording("Kalam", new DateTimeOffset(2026, 10, 11, 14, 5, 0, TimeSpan.Zero));
        var newest = WriteRecording("Kalam", new DateTimeOffset(2026, 10, 14, 19, 30, 0, TimeSpan.Zero), segments: 3);
        foreach (var stray in new[] { newest.Upload, _folder.File("notes.wav"), _folder.File("transcript.txt") })
            File.WriteAllBytes(stray, [0]);

        var exitCode = await CreateCommand().RunAsync(_folder.Path);

        Assert.That(exitCode, Is.EqualTo(0));
        _preparer.Verify(p => p.Prepare(new[] { newest.Segment(1), newest.Segment(2), newest.Segment(3) }, newest.Upload), Times.Once);
    }

    /// <summary>A segment file of a recording, dropped on the app for instance, means the whole recording, with its marks.</summary>
    [Test]
    public async Task A_segment_file_is_transcribed_as_its_whole_recording()
    {
        var recording = WriteRecording("Kalam", new DateTimeOffset(2026, 10, 14, 19, 30, 0, TimeSpan.Zero), segments: 2);
        File.WriteAllLines(recording.Marks, ["00:00:00.450"]);

        var exitCode = await CreateCommand().RunAsync(recording.Segment(2));

        Assert.That(exitCode, Is.EqualTo(0));
        _preparer.Verify(p => p.Prepare(new[] { recording.Segment(1), recording.Segment(2) }, recording.Upload), Times.Once);
        Assert.That(File.ReadAllText(recording.Transcript()), Does.Contain("## [00:00:00] Mark 1"));
    }

    /// <summary>A folder without any recording is an error, and nothing is sent.</summary>
    [Test]
    public async Task A_folder_without_recordings_is_an_error()
    {
        File.WriteAllBytes(_folder.File("notes.wav"), [0]);

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
        _desktop.Verify(d => d.OpenFile(It.Is<string>(p => p.StartsWith(transcriptFolder) && p.EndsWith(".md"))), Times.Once);
    }
}
