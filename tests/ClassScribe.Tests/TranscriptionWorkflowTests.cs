using ClassScribe.Audio;
using ClassScribe.Settings;
using ClassScribe.Transcription;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace ClassScribe.Tests;

/// <summary>Preparation, upload and saving, with a mocked preparer and service.</summary>
[TestFixture]
public class TranscriptionWorkflowTests
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

    /// <summary>Creates the workflow under test around the mocks.</summary>
    private TranscriptionWorkflow CreateWorkflow(bool keepUploadFile = false, string? keytermsFile = null) => new(
        _preparer.Object,
        _client.Object,
        new KeytermLoader(NullLogger<KeytermLoader>.Instance),
        new TranscriptFormatter(Options.Create(new TranscriptOptions())),
        Options.Create(new ElevenLabsOptions { ApiKey = "test-key", KeytermsFile = keytermsFile ?? _folder.File("no-glossary.txt") }),
        Options.Create(new RecordingOptions { KeepUploadFile = keepUploadFile }),
        NullLogger<TranscriptionWorkflow>.Instance);

    [Test]
    public async Task A_recording_is_prepared_uploaded_and_saved_in_its_folder()
    {
        string[] segments = [_folder.File("recording-001.wav"), _folder.File("recording-002.wav")];
        var upload = _folder.File("upload.wav");

        var files = await CreateWorkflow().TranscribeSegmentsAsync(segments, _folder.Path, CancellationToken.None);

        _preparer.Verify(p => p.Prepare(segments, upload), Times.Once);
        _client.Verify(Calls.TranscriptionOf(upload), Times.Once);
        Assert.That(files.TextPath, Is.EqualTo(_folder.File("transcript.txt")));
        Assert.That(File.ReadAllText(files.TextPath), Is.EqualTo("[00:00:00] Hello world." + Environment.NewLine));
        Assert.That(File.ReadAllText(files.JsonPath), Is.EqualTo(Mocks.Transcript.RawJson));
        Assert.That(File.Exists(upload), Is.False, "the upload copy is removed after success");
    }

    [Test]
    public async Task The_upload_copy_can_be_kept()
    {
        await CreateWorkflow(keepUploadFile: true).TranscribeSegmentsAsync([_folder.File("recording-001.wav")], _folder.Path, CancellationToken.None);

        Assert.That(File.Exists(_folder.File("upload.wav")), Is.True);
    }

    [Test]
    public async Task The_glossary_is_sent_with_the_audio()
    {
        var glossary = _folder.File("keyterms.txt");
        File.WriteAllLines(glossary, ["# terms", "kalām", "Ashʿarī"]);

        await CreateWorkflow(keytermsFile: glossary).TranscribeSegmentsAsync([_folder.File("recording-001.wav")], _folder.Path, CancellationToken.None);

        _client.Verify(c => c.TranscribeAsync(It.IsAny<string>(), new[] { "kalām", "Ashʿarī" }, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task A_video_file_is_uploaded_as_it_is_and_the_transcript_lands_next_to_it()
    {
        var video = _folder.File("class.mp4");
        File.WriteAllBytes(video, [1, 2, 3]);

        var files = await CreateWorkflow().TranscribeFileAsync(video, CancellationToken.None);

        _preparer.Verify(Calls.AnyPreparation, Times.Never);
        _client.Verify(Calls.TranscriptionOf(video), Times.Once);
        Assert.That(files.TextPath, Is.EqualTo(_folder.File("class.transcript.txt")));
        Assert.That(files.JsonPath, Is.EqualTo(_folder.File("class.transcript.json")));
        Assert.That(File.Exists(video), Is.True);
    }

    [Test]
    public async Task A_wav_file_is_reduced_before_upload()
    {
        var wav = _folder.File("class.wav");
        var upload = _folder.File("class.upload.wav");
        File.WriteAllBytes(wav, [1, 2, 3]);

        await CreateWorkflow().TranscribeFileAsync(wav, CancellationToken.None);

        _preparer.Verify(p => p.Prepare(new[] { wav }, upload), Times.Once);
        _client.Verify(Calls.TranscriptionOf(upload), Times.Once);
        Assert.That(File.Exists(upload), Is.False);
        Assert.That(File.Exists(wav), Is.True, "the original is never deleted");
    }

    [Test]
    public async Task A_wav_file_that_cannot_be_reduced_is_uploaded_as_it_is()
    {
        var wav = _folder.File("odd.wav");
        File.WriteAllBytes(wav, [1, 2, 3]);
        _preparer.Setup(Calls.AnyPreparation).Throws(new NotSupportedException("six channels"));

        await CreateWorkflow().TranscribeFileAsync(wav, CancellationToken.None);

        _client.Verify(Calls.TranscriptionOf(wav), Times.Once);
    }

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

    [Test]
    public void A_failed_upload_surfaces_and_keeps_the_audio()
    {
        _client.Setup(Calls.AnyTranscription).ThrowsAsync(new TranscriptionException("ElevenLabs is down."));
        var workflow = CreateWorkflow();

        Assert.ThrowsAsync<TranscriptionException>(async () =>
            await workflow.TranscribeSegmentsAsync([_folder.File("recording-001.wav")], _folder.Path, CancellationToken.None));

        Assert.That(File.Exists(_folder.File("upload.wav")), Is.True, "kept so the upload can be retried");
        Assert.That(File.Exists(_folder.File("transcript.txt")), Is.False);
    }
}
