using ClassScribe.Audio;
using ClassScribe.Settings;
using ClassScribe.Storage;
using ClassScribe.Transcription;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace ClassScribe.Tests;

/// <summary>Preparation, upload and saving, with a mocked preparer and service.</summary>
[TestFixture]
public class TranscriptionWorkflowTests
{
    private static readonly DateTimeOffset Started = new(2026, 10, 15, 20, 55, 27, TimeSpan.Zero);
    private static readonly string Blank = Environment.NewLine + Environment.NewLine;

    private TempFolder _folder = null!;
    private string _recordings = null!;
    private RecordingFiles _files = null!;
    private Mock<IAudioPreparer> _preparer = null!;
    private Mock<ITranscriptionClient> _client = null!;

    /// <summary>Creates a temporary folder, a recordings folder with one recording's folder inside it, and the mocks.</summary>
    [SetUp]
    public void SetUp()
    {
        _folder = new TempFolder();
        _recordings = _folder.File("Recordings");
        _files = RecordingFiles.ForNew(Path.Combine(_recordings, "Kalam"), "Kalam", Started);
        Directory.CreateDirectory(_files.Folder);
        _preparer = Mocks.AudioPreparer();
        _client = Mocks.TranscriptionClient();
    }

    /// <summary>Deletes the temporary folder. That fails if a test left a file open, which is the point.</summary>
    [TearDown]
    public void TearDown() => _folder.Dispose();

    /// <summary>Creates the workflow under test around the mocks.</summary>
    private TranscriptionWorkflow CreateWorkflow(bool keepUploadFile = false, string? keytermsFile = null)
    {
        var recording = Options.Create(new RecordingOptions { KeepUploadFile = keepUploadFile, OutputFolder = _recordings });
        return new(
            _preparer.Object,
            _client.Object,
            new KeytermLoader(NullLogger<KeytermLoader>.Instance),
            new TranscriptFormatter(Options.Create(new TranscriptOptions())),
            new ClassFolders(recording, new ManualTimeProvider(Started)),
            new ClassMarks(NullLogger<ClassMarks>.Instance),
            Options.Create(new ElevenLabsOptions { ApiKey = "test-key", KeytermsFile = keytermsFile ?? _folder.File("no-glossary.txt") }),
            recording,
            NullLogger<TranscriptionWorkflow>.Instance);
    }

    /// <summary>The one transcript in the folder a transcribed file got.</summary>
    private static string OnlyTranscript(string folder) => Directory.GetFiles(folder, "*.md").Single();

    /// <summary>A recording's segments are prepared into one upload, and its transcript is saved beside them under the recording's name.</summary>
    [Test]
    public async Task A_recording_is_prepared_uploaded_and_saved_under_its_name()
    {
        string[] segments = [_files.Segment(1), _files.Segment(2)];

        var files = await CreateWorkflow().TranscribeRecordingAsync(_files, segments, CancellationToken.None);

        _preparer.Verify(p => p.Prepare(segments, _files.Upload), Times.Once);
        _client.Verify(Calls.TranscriptionOf(_files.Upload), Times.Once);
        Assert.That(files.TranscriptPath, Is.EqualTo(_files.Transcript()));
        Assert.That(files.JsonPath, Is.EqualTo(_files.Json()));
        Assert.That(File.ReadAllText(files.TranscriptPath), Is.EqualTo($"# Kalam{Blank}Recorded 15 October 2026, 20:55 UTC.{Blank}[00:00:00] Hello world.{Environment.NewLine}"));
        Assert.That(File.ReadAllText(files.JsonPath), Is.EqualTo(Mocks.Transcript.RawJson));
        Assert.That(File.Exists(_files.Upload), Is.False, "the upload copy is removed after success");
    }

    /// <summary>The marks set while recording appear in the recording's transcript.</summary>
    [Test]
    public async Task The_marks_of_a_recording_appear_in_its_transcript()
    {
        File.WriteAllLines(_files.Marks, ["00:00:00.450"]);

        var files = await CreateWorkflow().TranscribeRecordingAsync(_files, [_files.Segment(1)], CancellationToken.None);

        Assert.That(File.ReadAllText(files.TranscriptPath), Does.Contain($"[00:00:00] Hello{Blank}## [00:00:00] Mark 1{Blank}[00:00:00] world."));
    }

    /// <summary>Transcribing a recording again keeps the earlier transcript and writes a second one.</summary>
    [Test]
    public async Task Transcribing_a_recording_again_keeps_the_earlier_transcript()
    {
        var workflow = CreateWorkflow();

        var first = await workflow.TranscribeRecordingAsync(_files, [_files.Segment(1)], CancellationToken.None);
        var second = await workflow.TranscribeRecordingAsync(_files, [_files.Segment(1)], CancellationToken.None);

        Assert.That(second.TranscriptPath, Is.EqualTo(_files.Transcript(2)));
        Assert.That(second.JsonPath, Is.EqualTo(_files.Json(2)));
        Assert.That(File.Exists(first.TranscriptPath) && File.Exists(second.TranscriptPath), Is.True);
    }

    /// <summary>With KeepUploadFile the 16 kHz copy stays after success.</summary>
    [Test]
    public async Task The_upload_copy_can_be_kept()
    {
        await CreateWorkflow(keepUploadFile: true).TranscribeRecordingAsync(_files, [_files.Segment(1)], CancellationToken.None);

        Assert.That(File.Exists(_files.Upload), Is.True);
    }

    /// <summary>The glossary file's terms go with the audio.</summary>
    [Test]
    public async Task The_glossary_is_sent_with_the_audio()
    {
        var glossary = _folder.File("keyterms.txt");
        File.WriteAllLines(glossary, ["# terms", "kalām", "Ashʿarī"]);

        await CreateWorkflow(keytermsFile: glossary).TranscribeRecordingAsync(_files, [_files.Segment(1)], CancellationToken.None);

        _client.Verify(c => c.TranscribeAsync(It.IsAny<string>(), new[] { "kalām", "Ashʿarī" }, It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>A video goes up as it is, and its transcript gets a folder of its own in the recordings folder, named after it and stamped.</summary>
    [Test]
    public async Task A_video_file_is_uploaded_as_it_is_and_its_transcript_goes_to_the_recordings_folder()
    {
        var video = _folder.File("class.mp4");
        File.WriteAllBytes(video, [1, 2, 3]);

        var files = await CreateWorkflow().TranscribeFileAsync(video, CancellationToken.None);

        _preparer.Verify(Calls.AnyPreparation, Times.Never);
        _client.Verify(Calls.TranscriptionOf(video), Times.Once);
        Assert.That(files.TranscriptPath, Is.EqualTo(Path.Combine(_recordings, "class", "class_2026-10-15_20-55-27Z.md")));
        Assert.That(files.JsonPath, Is.EqualTo(Path.Combine(_recordings, "class", "class_2026-10-15_20-55-27Z.json")));
        Assert.That(File.ReadAllText(files.TranscriptPath), Does.StartWith($"# class{Blank}[00:00:00] Hello world."), "a file has no recording time");
        Assert.That(File.Exists(video), Is.True);
    }

    /// <summary>Transcribing the same file again gives a second transcript; the first is kept.</summary>
    [Test]
    public async Task The_same_file_transcribed_twice_gets_a_second_transcript()
    {
        var video = _folder.File("class.mp4");
        File.WriteAllBytes(video, [1, 2, 3]);
        var workflow = CreateWorkflow();

        var first = await workflow.TranscribeFileAsync(video, CancellationToken.None);
        var second = await workflow.TranscribeFileAsync(video, CancellationToken.None);

        Assert.That(second.TranscriptPath, Is.Not.EqualTo(first.TranscriptPath));
        Assert.That(File.Exists(first.TranscriptPath) && File.Exists(second.TranscriptPath), Is.True);
    }

    /// <summary>A WAV file is reduced to a 16 kHz copy in the transcript's folder, which goes after success.</summary>
    [Test]
    public async Task A_wav_file_is_reduced_before_upload()
    {
        var wav = _folder.File("class.wav");
        var upload = Path.Combine(_recordings, "class", "class_2026-10-15_20-55-27Z.upload.wav");
        File.WriteAllBytes(wav, [1, 2, 3]);

        await CreateWorkflow().TranscribeFileAsync(wav, CancellationToken.None);

        _preparer.Verify(p => p.Prepare(new[] { wav }, upload), Times.Once);
        _client.Verify(Calls.TranscriptionOf(upload), Times.Once);
        Assert.That(File.Exists(upload), Is.False);
        Assert.That(File.Exists(wav), Is.True, "the original is never deleted");
    }

    /// <summary>A WAV file the preparer cannot read is uploaded as it is.</summary>
    [Test]
    public async Task A_wav_file_that_cannot_be_reduced_is_uploaded_as_it_is()
    {
        var wav = _folder.File("odd.wav");
        File.WriteAllBytes(wav, [1, 2, 3]);
        _preparer.Setup(Calls.AnyPreparation).Throws(new NotSupportedException("six channels"));

        await CreateWorkflow().TranscribeFileAsync(wav, CancellationToken.None);

        _client.Verify(Calls.TranscriptionOf(wav), Times.Once);
    }

    /// <summary>A WAV file without sound is not worth paying for, so nothing is uploaded.</summary>
    [Test]
    public void A_wav_file_without_any_sound_is_not_uploaded()
    {
        var wav = _folder.File("silent.wav");
        File.WriteAllBytes(wav, [1, 2, 3]);
        _preparer.Setup(Calls.AnyPreparation).Throws(new NoAudioException("Nothing in it."));
        var workflow = CreateWorkflow();

        Assert.ThrowsAsync<NoAudioException>(async () => await workflow.TranscribeFileAsync(wav, CancellationToken.None));
        _client.Verify(Calls.AnyTranscription, Times.Never);
    }

    /// <summary>A failed upload surfaces as an error, and the audio and upload copy stay for another try.</summary>
    [Test]
    public void A_failed_upload_surfaces_and_keeps_the_audio()
    {
        _client.Setup(Calls.AnyTranscription).ThrowsAsync(new TranscriptionException("ElevenLabs is down."));
        var workflow = CreateWorkflow();

        Assert.ThrowsAsync<TranscriptionException>(async () =>
            await workflow.TranscribeRecordingAsync(_files, [_files.Segment(1)], CancellationToken.None));

        Assert.That(File.Exists(_files.Upload), Is.True, "kept so the upload can be retried");
        Assert.That(File.Exists(_files.Transcript()), Is.False);
    }
}
