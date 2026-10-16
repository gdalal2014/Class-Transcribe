using System.Net;
using System.Text;
using ClassScribe.Audio;
using ClassScribe.Settings;
using ClassScribe.Storage;
using ClassScribe.Transcription;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using NAudio.Wave;

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

    /// <summary>Creates the workflow under test around the mocks, or around the preparer and loggers given.</summary>
    private TranscriptionWorkflow CreateWorkflow(
        bool keepUploadFile = false,
        string? keytermsFile = null,
        IAudioPreparer? preparer = null,
        ILogger<TranscriptionWorkflow>? logger = null,
        ILogger<KeytermLoader>? glossaryLogger = null)
    {
        var recording = Options.Create(new RecordingOptions { KeepUploadFile = keepUploadFile, OutputFolder = _recordings });
        return new(
            preparer ?? _preparer.Object,
            _client.Object,
            new KeytermLoader(glossaryLogger ?? NullLogger<KeytermLoader>.Instance),
            new TranscriptFormatter(Options.Create(new TranscriptOptions())),
            new ClassFolders(recording, new ManualTimeProvider(Started), NullLogger<ClassFolders>.Instance),
            new ClassMarks(NullLogger<ClassMarks>.Instance),
            Options.Create(new ElevenLabsOptions { ApiKey = "test-key", KeytermsFile = keytermsFile ?? _folder.File("no-glossary.txt") }),
            recording,
            logger ?? NullLogger<TranscriptionWorkflow>.Instance);
    }

    /// <summary>The real preparer, for tests that need the upload file it actually makes.</summary>
    private static AudioPreparer RealPreparer() => new(NullLogger<AudioPreparer>.Instance);

    /// <summary>The bytes of <paramref name="text"/> in UTF-8, without the byte order mark, which <see cref="Encoding.GetBytes(string)"/> never adds.</summary>
    private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);

    /// <summary>
    /// A recording's segments are prepared into one upload, and its transcript is saved beside them under the
    /// recording's name: the Markdown and ElevenLabs' answer byte for byte, in UTF-8 without a byte order mark.
    /// </summary>
    [Test]
    public async Task A_recording_is_prepared_uploaded_and_saved_under_its_name()
    {
        string[] segments = [_files.Segment(1), _files.Segment(2)];

        var files = await CreateWorkflow().TranscribeRecordingAsync(_files, segments, CancellationToken.None);

        _preparer.Verify(p => p.Prepare(segments, _files.Upload, It.IsAny<CancellationToken>()), Times.Once);
        _client.Verify(Calls.TranscriptionOf(_files.Upload), Times.Once);
        Assert.That(files.TranscriptPath, Is.EqualTo(_files.Transcript()));
        Assert.That(files.JsonPath, Is.EqualTo(_files.Json()));
        Assert.That(File.ReadAllBytes(files.TranscriptPath), Is.EqualTo(Utf8($"# Kalam{Blank}Recorded 15 October 2026, 20:55 UTC.{Blank}[00:00:00] Hello world.{Environment.NewLine}")));
        Assert.That(File.ReadAllBytes(files.JsonPath), Is.EqualTo(Utf8(Mocks.Transcript.RawJson)));
        Assert.That(File.Exists(_files.Upload), Is.False, "the upload copy is removed after success");
    }

    /// <summary>
    /// Transliterated Arabic such as kalām and Ashʿarī is saved in UTF-8, without a byte order mark, in the transcript
    /// and in ElevenLabs' answer alike.
    /// </summary>
    [Test]
    public async Task Transliterated_arabic_is_saved_in_utf8_without_a_byte_order_mark()
    {
        const string text = "Kalām of the Ashʿarī.";
        const string answer = """{"text":"Kalām of the Ashʿarī."}""";
        _client.Setup(Calls.AnyTranscription).ReturnsAsync(Mocks.Transcript with { Text = text, Words = [new TranscriptWord(text, 0.0, 1.0, "word", null)], RawJson = answer });

        var files = await CreateWorkflow().TranscribeRecordingAsync(_files, [_files.Segment(1)], CancellationToken.None);

        Assert.That(File.ReadAllBytes(files.TranscriptPath), Is.EqualTo(Utf8($"# Kalam{Blank}Recorded 15 October 2026, 20:55 UTC.{Blank}[00:00:00] {text}{Environment.NewLine}")));
        Assert.That(File.ReadAllBytes(files.JsonPath), Is.EqualTo(Utf8(answer)));
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

    /// <summary>
    /// An empty ElevenLabs:KeytermsFile turns the glossary off on purpose: no terms are sent, and there is no warning
    /// about a missing glossary, which would name the app's own folder, only a note that none is set.
    /// </summary>
    [Test]
    public async Task An_empty_glossary_setting_sends_no_terms_and_warns_of_nothing()
    {
        var glossaryLog = new RecordingLogger<KeytermLoader>();

        await CreateWorkflow(keytermsFile: "", glossaryLogger: glossaryLog).TranscribeRecordingAsync(_files, [_files.Segment(1)], CancellationToken.None);

        _client.Verify(c => c.TranscribeAsync(It.IsAny<string>(), It.Is<IReadOnlyList<string>>(terms => terms.Count == 0), It.IsAny<CancellationToken>()), Times.Once);
        Assert.That(glossaryLog.Problems, Is.Empty);
        Assert.That(glossaryLog.Messages, Has.Some.StartsWith("No glossary is set"));
    }

    /// <summary>Environment variables in ElevenLabs:KeytermsFile, such as %USERPROFILE%, are expanded, so the glossary they point to is sent.</summary>
    [Test]
    public async Task Environment_variables_in_the_glossary_setting_are_expanded()
    {
        const string variable = "CLASSSCRIBE_TEST_WORKFLOW_GLOSSARY";
        var glossaryLog = new RecordingLogger<KeytermLoader>();
        File.WriteAllLines(_folder.File("terms.txt"), ["kalām", "Ashʿarī"]);
        Environment.SetEnvironmentVariable(variable, _folder.Path);
        try
        {
            await CreateWorkflow(keytermsFile: $@"%{variable}%\terms.txt", glossaryLogger: glossaryLog)
                .TranscribeRecordingAsync(_files, [_files.Segment(1)], CancellationToken.None);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }

        _client.Verify(c => c.TranscribeAsync(It.IsAny<string>(), new[] { "kalām", "Ashʿarī" }, It.IsAny<CancellationToken>()), Times.Once);
        Assert.That(glossaryLog.Problems, Is.Empty);
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

        _preparer.Verify(p => p.Prepare(new[] { wav }, upload, It.IsAny<CancellationToken>()), Times.Once);
        _client.Verify(Calls.TranscriptionOf(upload), Times.Once);
        Assert.That(File.Exists(upload), Is.False);
        Assert.That(File.Exists(wav), Is.True, "the original is never deleted");
    }

    /// <summary>Preparing a WAV file is announced, as it is for a recording, so the wait for a long file is not taken for a hang.</summary>
    [Test]
    public async Task Preparing_a_wav_file_is_announced()
    {
        var log = new RecordingLogger<TranscriptionWorkflow>();
        var wav = _folder.File("class.wav");
        File.WriteAllBytes(wav, [1, 2, 3]);

        await CreateWorkflow(logger: log).TranscribeFileAsync(wav, CancellationToken.None);

        Assert.That(log.Messages, Has.Some.StartsWith("Preparing class.wav for upload"));
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

    /// <summary>A WAV file another recorder never finished, whose header says it holds no audio, is reduced and uploaded, not refused as empty.</summary>
    [Test]
    public async Task A_wav_file_another_recorder_never_finished_is_reduced_and_uploaded()
    {
        var wav = _folder.File("rescued.wav");
        ForeignWav.Write(wav, new WaveFormat(16000, 16, 2), TestAudio.Shorts(1000, 3000, -1000, -3000));
        ForeignWav.LeaveUnfinished(wav, dataBytes: 0);
        short[] uploaded = [];
        _client.Setup(Calls.AnyTranscription).ReturnsAsync((string path, IReadOnlyList<string> _, CancellationToken _) =>
        {
            uploaded = TestAudio.ReadWav(path).Samples;
            return Mocks.Transcript;
        });

        await CreateWorkflow(preparer: RealPreparer()).TranscribeFileAsync(wav, CancellationToken.None);

        Assert.That(uploaded, Is.EqualTo(new short[] { 2000, -2000 }));
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

    /// <summary>
    /// A recording whose upload failed can be transcribed again with the real preparer, which never writes over
    /// a file: the upload copy the failed attempt left behind is made afresh rather than tripped over.
    /// </summary>
    [Test]
    public async Task A_recording_whose_upload_failed_can_be_transcribed_again()
    {
        TestAudio.WriteWav(_files.Segment(1), 16000, TestAudio.Tone(16000, seconds: 0.2, frequency: 440, amplitude: 0.5));
        _client.SetupSequence(Calls.AnyTranscription)
            .ThrowsAsync(new TranscriptionException("ElevenLabs is down."))
            .ReturnsAsync(Mocks.Transcript);
        var workflow = CreateWorkflow(preparer: RealPreparer());

        Assert.ThrowsAsync<TranscriptionException>(async () => await workflow.TranscribeRecordingAsync(_files, [_files.Segment(1)], CancellationToken.None));
        Assert.That(File.Exists(_files.Upload), Is.True, "the failed attempt leaves its upload copy");

        var saved = await workflow.TranscribeRecordingAsync(_files, [_files.Segment(1)], CancellationToken.None);

        Assert.That(File.Exists(saved.TranscriptPath), Is.True);
    }

    /// <summary>The run's Ctrl+C reaches the preparation, of a recording and of a WAV file, so it need not wait for the whole upload file.</summary>
    [Test]
    public async Task Ctrl_C_reaches_the_preparation()
    {
        using var cancellation = new CancellationTokenSource();
        var wav = _folder.File("class.wav");
        File.WriteAllBytes(wav, [1, 2, 3]);
        var workflow = CreateWorkflow();

        await workflow.TranscribeRecordingAsync(_files, [_files.Segment(1)], cancellation.Token);
        await workflow.TranscribeFileAsync(wav, cancellation.Token);

        _preparer.Verify(p => p.Prepare(It.IsAny<IReadOnlyList<string>>(), _files.Upload, cancellation.Token), Times.Once);
        _preparer.Verify(p => p.Prepare(new[] { wav }, It.IsAny<string>(), cancellation.Token), Times.Once);
    }

    /// <summary>A cancel that came before the transcription starts, such as a second Ctrl+C right after stopping, prepares and sends nothing.</summary>
    [Test]
    public void A_cancel_before_the_transcription_starts_prepares_and_sends_nothing()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var workflow = CreateWorkflow();

        Assert.CatchAsync<OperationCanceledException>(async () => await workflow.TranscribeRecordingAsync(_files, [_files.Segment(1)], cancellation.Token));

        _preparer.Verify(Calls.AnyPreparation, Times.Never);
        _client.Verify(Calls.AnyTranscription, Times.Never);
    }

    /// <summary>A cancel that comes while the upload file is made stops the run before anything is sent or announced as sent.</summary>
    [Test]
    public void A_cancel_during_the_preparation_sends_nothing()
    {
        using var cancellation = new CancellationTokenSource();
        _preparer.Setup(Calls.AnyPreparation).Callback((IReadOnlyList<string> _, string outputPath, CancellationToken _) =>
        {
            File.WriteAllBytes(outputPath, [1, 2, 3]);
            cancellation.Cancel();   // Ctrl+C as the last of the upload file is written
        });
        var workflow = CreateWorkflow();

        Assert.CatchAsync<OperationCanceledException>(async () => await workflow.TranscribeRecordingAsync(_files, [_files.Segment(1)], cancellation.Token));

        _client.Verify(Calls.AnyTranscription, Times.Never);
    }

    /// <summary>Ctrl+C just as the transcript arrives does not throw it away: it has been paid for, so it is saved.</summary>
    [Test]
    public async Task A_cancel_just_as_the_transcript_arrives_does_not_lose_it()
    {
        using var cancellation = new CancellationTokenSource();
        _client.Setup(Calls.AnyTranscription).ReturnsAsync(() =>
        {
            cancellation.Cancel();
            return Mocks.Transcript;
        });

        var files = await CreateWorkflow().TranscribeRecordingAsync(_files, [_files.Segment(1)], cancellation.Token);

        Assert.That(File.ReadAllText(files.JsonPath), Is.EqualTo(Mocks.Transcript.RawJson));
        Assert.That(File.Exists(files.TranscriptPath), Is.True);
    }

    /// <summary>
    /// A saved transcript counts as done even when the upload copy cannot be removed, as when another program
    /// holds it open: reporting the run as failed would invite a second, charged attempt.
    /// </summary>
    [Test]
    public async Task A_saved_transcript_counts_even_when_the_upload_copy_cannot_be_removed()
    {
        var log = new RecordingLogger<TranscriptionWorkflow>();
        FileStream? holder = null;
        _client.Setup(Calls.AnyTranscription).ReturnsAsync((string path, IReadOnlyList<string> _, CancellationToken _) =>
        {
            holder = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);   // open, and not willing to let it be deleted
            return Mocks.Transcript;
        });

        try
        {
            var files = await CreateWorkflow(logger: log).TranscribeRecordingAsync(_files, [_files.Segment(1)], CancellationToken.None);

            Assert.That(File.Exists(files.TranscriptPath), Is.True);
            Assert.That(log.Problems, Has.Some.Contains(Path.GetFileName(_files.Upload)));
        }
        finally
        {
            holder?.Dispose();
        }
    }

    /// <summary>
    /// The same holds for a WAV file transcribed on its own: once its transcript is saved, an upload copy that
    /// cannot be removed is only a warning, and the transcript is reported as saved.
    /// </summary>
    [Test]
    public async Task A_saved_transcript_of_a_wav_file_counts_even_when_its_upload_copy_cannot_be_removed()
    {
        var log = new RecordingLogger<TranscriptionWorkflow>();
        var wav = _folder.File("class.wav");
        File.WriteAllBytes(wav, [1, 2, 3]);
        FileStream? holder = null;
        _client.Setup(Calls.AnyTranscription).ReturnsAsync((string path, IReadOnlyList<string> _, CancellationToken _) =>
        {
            holder = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);   // open, and not willing to let it be deleted
            return Mocks.Transcript;
        });

        try
        {
            var files = await CreateWorkflow(logger: log).TranscribeFileAsync(wav, CancellationToken.None);

            Assert.That(File.Exists(files.TranscriptPath), Is.True);
            Assert.That(holder?.Name, Is.EqualTo(Path.Combine(_recordings, "class", "class_2026-10-15_20-55-27Z.upload.wav")), "the upload copy was sent");
            Assert.That(log.Problems, Has.Some.Contains("class_2026-10-15_20-55-27Z.upload.wav"));
        }
        finally
        {
            holder?.Dispose();
        }
    }

    /// <summary>The failure the client reports for an answer that arrived, and was charged for, but could not be read.</summary>
    private static TranscriptionException Unreadable(string answer) =>
        new("ElevenLabs answered in a form this app cannot read.", HttpStatusCode.OK, answer);

    /// <summary>
    /// An answer ElevenLabs sent, and charged for, that could not be read is kept beside the recording under a name
    /// of its own, which the warning gives, rather than in the place of a transcript's answer. The error says that
    /// the answer was charged for, and where it is kept.
    /// </summary>
    [Test]
    public void An_answer_that_could_not_be_read_is_kept_under_a_name_of_its_own()
    {
        const string answer = """{"text":"Hello world.","words":[{"text":"Hello","speaker_id":3}]}""";
        var log = new RecordingLogger<TranscriptionWorkflow>();
        _client.Setup(Calls.AnyTranscription).ThrowsAsync(Unreadable(answer));
        var workflow = CreateWorkflow(logger: log);

        var error = Assert.ThrowsAsync<TranscriptionException>(async () => await workflow.TranscribeRecordingAsync(_files, [_files.Segment(1)], CancellationToken.None));

        Assert.That(File.ReadAllText(Path.Combine(_files.Folder, "Kalam_2026-10-15_20-55-27Z.unread.json")), Is.EqualTo(answer));
        Assert.That(log.Problems, Has.Some.Contains("Kalam_2026-10-15_20-55-27Z.unread.json"));
        Assert.That(File.Exists(_files.Json()), Is.False, "not in the place of a transcript's answer");
        Assert.That(File.Exists(_files.Transcript()), Is.False);
        Assert.That(ChargedAnswer.Of(error!), Is.EqualTo(new ChargedAnswer(_files.UnreadAnswer(), TranscriptionId: null)));
    }

    /// <summary>
    /// An answer that could not be read, and that can be kept nowhere either, is still reported as charged for: the
    /// error says so, with no place to look, so that a new try is not taken for a free one. No transcription ID could
    /// be read from the answer, so the log says that the ID is unknown, and the error carries none.
    /// </summary>
    [Test]
    public void An_answer_that_could_not_be_read_or_kept_is_still_reported_as_charged_for()
    {
        var log = new RecordingLogger<TranscriptionWorkflow>();
        _client.Setup(Calls.AnyTranscription)
            .Callback(() => LeaveNowhereToKeepTheAnswer())
            .ThrowsAsync(Unreadable("""{"text":"Hello"""));
        var workflow = CreateWorkflow(logger: log);

        var error = Assert.ThrowsAsync<TranscriptionException>(async () => await workflow.TranscribeRecordingAsync(_files, [_files.Segment(1)], CancellationToken.None));

        Assert.That(log.Problems, Has.Some.Contains("could not be kept anywhere. Its transcription ID at ElevenLabs is unknown.").And.Contains("charged again"));
        Assert.That(ChargedAnswer.Of(error!), Is.EqualTo(new ChargedAnswer(KeptIn: null, TranscriptionId: null)));
    }

    /// <summary>
    /// An answer that could not be read does not count as a transcript: the next attempt that succeeds writes the
    /// recording's first transcript, so the recording shows as transcribed.
    /// </summary>
    [Test]
    public async Task After_an_answer_that_could_not_be_read_the_next_transcript_is_the_recordings_first()
    {
        _client.SetupSequence(Calls.AnyTranscription)
            .ThrowsAsync(Unreadable("""{"text":"Hello world.","words":[{"text":"Hello","speaker_id":3}]}"""))
            .ReturnsAsync(Mocks.Transcript);
        var workflow = CreateWorkflow();
        Assert.ThrowsAsync<TranscriptionException>(async () => await workflow.TranscribeRecordingAsync(_files, [_files.Segment(1)], CancellationToken.None));

        var saved = await workflow.TranscribeRecordingAsync(_files, [_files.Segment(1)], CancellationToken.None);

        Assert.That(saved, Is.EqualTo(new TranscriptFiles(_files.Transcript(), _files.Json())));
        Assert.That(_files.HasTranscript, Is.True);
    }

    /// <summary>A second answer that could not be read is kept beside the first, which is never overwritten.</summary>
    [Test]
    public void A_second_answer_that_could_not_be_read_is_kept_beside_the_first()
    {
        _client.SetupSequence(Calls.AnyTranscription)
            .ThrowsAsync(Unreadable("""{"first":1}"""))
            .ThrowsAsync(Unreadable("""{"second":2}"""));
        var workflow = CreateWorkflow();

        for (var attempt = 0; attempt < 2; attempt++)
            Assert.ThrowsAsync<TranscriptionException>(async () => await workflow.TranscribeRecordingAsync(_files, [_files.Segment(1)], CancellationToken.None));

        Assert.That(File.ReadAllText(Path.Combine(_files.Folder, "Kalam_2026-10-15_20-55-27Z.unread.json")), Is.EqualTo("""{"first":1}"""));
        Assert.That(File.ReadAllText(Path.Combine(_files.Folder, "Kalam_2026-10-15_20-55-27Z (2).unread.json")), Is.EqualTo("""{"second":2}"""));
    }

    /// <summary>A refusal is no answer to keep: nothing is saved anywhere when ElevenLabs turns the request down.</summary>
    [Test]
    public void A_refusal_is_not_kept()
    {
        _client.Setup(Calls.AnyTranscription).ThrowsAsync(new TranscriptionException("ElevenLabs rejected the API key.", HttpStatusCode.Unauthorized, """{"detail":{"status":"invalid_api_key"}}"""));
        var workflow = CreateWorkflow();

        Assert.ThrowsAsync<TranscriptionException>(async () => await workflow.TranscribeRecordingAsync(_files, [_files.Segment(1)], CancellationToken.None));

        Assert.That(Directory.GetFiles(_recordings, "*.json", SearchOption.AllDirectories), Is.Empty);
    }

    /// <summary>
    /// When saving fails after the upload (here a folder is in the way of the answer's .json), the error surfaces, only
    /// the one upload has been made, and the answer, already paid for, is kept beside the recording under a name of its
    /// own, byte for byte, which the error gives. The upload copy stays, and the recording does not count as transcribed.
    /// </summary>
    [Test]
    public void An_answer_that_cannot_be_saved_is_kept_under_a_name_of_its_own_and_the_upload_copy_stays()
    {
        Directory.CreateDirectory(_files.Json());   // a folder in the way of the answer, so saving it fails
        var log = new RecordingLogger<TranscriptionWorkflow>();
        var workflow = CreateWorkflow(logger: log);

        var error = Assert.ThrowsAsync<UnauthorizedAccessException>(async () => await workflow.TranscribeRecordingAsync(_files, [_files.Segment(1)], CancellationToken.None));

        _client.Verify(Calls.AnyTranscription, Times.Once);
        Assert.That(File.ReadAllBytes(_files.UnreadAnswer()), Is.EqualTo(Utf8(Mocks.Transcript.RawJson)));
        Assert.That(log.Problems, Has.Some.Contains(_files.UnreadAnswer()));
        Assert.That(File.Exists(_files.Upload), Is.True, "the upload copy stays");
        Assert.That(_files.HasTranscript, Is.False);
        Assert.That(ChargedAnswer.Of(error!), Is.EqualTo(new ChargedAnswer(_files.UnreadAnswer(), Mocks.Transcript.TranscriptionId)));
    }

    /// <summary>
    /// When the recording's folder will not take anything, because it went away during the upload, ElevenLabs' answer,
    /// already paid for, is kept in the recordings folder instead, under the name it would have had beside the recording,
    /// and the error gives that place.
    /// </summary>
    [Test]
    public void An_answer_that_cannot_be_saved_beside_the_recording_is_kept_in_the_recordings_folder()
    {
        var kept = Path.Combine(_recordings, Path.GetFileName(_files.UnreadAnswer()));
        _client.Setup(Calls.AnyTranscription).ReturnsAsync(() => GoneDuringTheUpload(Mocks.Transcript));
        var workflow = CreateWorkflow();

        var error = Assert.CatchAsync<IOException>(async () => await workflow.TranscribeRecordingAsync(_files, [_files.Segment(1)], CancellationToken.None));

        Assert.That(File.ReadAllText(kept), Is.EqualTo(Mocks.Transcript.RawJson));
        Assert.That(ChargedAnswer.Of(error!), Is.EqualTo(new ChargedAnswer(kept, Mocks.Transcript.TranscriptionId)));
    }

    /// <summary>
    /// When the transcript cannot be written (here a folder is in its way) after ElevenLabs' answer is saved, the error
    /// says that the answer was charged for, and points to the saved answer, which holds the transcript's text.
    /// </summary>
    [Test]
    public void A_transcript_that_cannot_be_written_points_to_the_answer_saved_beside_it()
    {
        Directory.CreateDirectory(_files.Transcript());   // a folder in the way of the transcript, so writing it fails
        var log = new RecordingLogger<TranscriptionWorkflow>();
        var workflow = CreateWorkflow(logger: log);

        var error = Assert.CatchAsync(async () => await workflow.TranscribeRecordingAsync(_files, [_files.Segment(1)], CancellationToken.None));

        Assert.That(File.ReadAllBytes(_files.Json()), Is.EqualTo(Utf8(Mocks.Transcript.RawJson)));
        Assert.That(log.Problems, Has.Some.Contains(_files.Json()).And.Contains("charged again"));
        Assert.That(ChargedAnswer.Of(error!), Is.EqualTo(new ChargedAnswer(_files.Json(), Mocks.Transcript.TranscriptionId)));
    }

    /// <summary>
    /// When no transcript can be made from ElevenLabs' answer at all, here because a word's time is past anything a
    /// recording can last, the error still says that the answer was charged for, and points to the saved answer.
    /// </summary>
    [Test]
    public void A_transcript_that_cannot_be_made_from_the_answer_points_to_the_answer_saved_beside_it()
    {
        _client.Setup(Calls.AnyTranscription).ReturnsAsync(Mocks.Transcript with { Words = [new TranscriptWord("Hello", 1e300, 1e300, "word", null)] });
        var log = new RecordingLogger<TranscriptionWorkflow>();
        var workflow = CreateWorkflow(logger: log);

        var error = Assert.CatchAsync(async () => await workflow.TranscribeRecordingAsync(_files, [_files.Segment(1)], CancellationToken.None));

        Assert.That(File.Exists(_files.Transcript()), Is.False, "no transcript could be made");
        Assert.That(File.ReadAllBytes(_files.Json()), Is.EqualTo(Utf8(Mocks.Transcript.RawJson)));
        Assert.That(log.Problems, Has.Some.Contains(_files.Json()).And.Contains("charged again"));
        Assert.That(ChargedAnswer.Of(error!), Is.EqualTo(new ChargedAnswer(_files.Json(), Mocks.Transcript.TranscriptionId)));
    }

    /// <summary>A second answer kept in the recordings folder goes beside the first one there, which is never overwritten.</summary>
    [Test]
    public void A_second_answer_kept_in_the_recordings_folder_is_kept_beside_the_first()
    {
        _client.SetupSequence(Calls.AnyTranscription)
            .ReturnsAsync(() => GoneDuringTheUpload(Mocks.Transcript with { RawJson = """{"first":1}""" }))
            .ReturnsAsync(() => GoneDuringTheUpload(Mocks.Transcript with { RawJson = """{"second":2}""" }));
        var workflow = CreateWorkflow();

        for (var attempt = 0; attempt < 2; attempt++)
        {
            Directory.CreateDirectory(_files.Folder);
            Assert.CatchAsync<IOException>(async () => await workflow.TranscribeRecordingAsync(_files, [_files.Segment(1)], CancellationToken.None));
        }

        Assert.That(File.ReadAllText(Path.Combine(_recordings, "Kalam_2026-10-15_20-55-27Z.unread.json")), Is.EqualTo("""{"first":1}"""));
        Assert.That(File.ReadAllText(Path.Combine(_recordings, "Kalam_2026-10-15_20-55-27Z (2).unread.json")), Is.EqualTo("""{"second":2}"""));
    }

    /// <summary>Removes the recording's folder, as when it is moved or deleted while ElevenLabs is at work, and passes on the answer.</summary>
    private TranscriptionResult GoneDuringTheUpload(TranscriptionResult answer)
    {
        Directory.Delete(_files.Folder, recursive: true);
        return answer;
    }

    /// <summary>Puts a file where the recordings folder was, as if it went away during the upload, so that nothing can go under it.</summary>
    private void LeaveNowhereToKeepTheAnswer()
    {
        Directory.Delete(_recordings, recursive: true);
        File.WriteAllText(_recordings, "");
    }

    /// <summary>
    /// When the answer can be kept nowhere, not even in the recordings folder, the error gives its transcription ID at
    /// ElevenLabs, so that the transcript that was paid for can still be fetched from there, and says that transcribing
    /// again is charged again. The error still reports the answer as charged for, with no place to look, and carries
    /// the ID for whoever reports it.
    /// </summary>
    [Test]
    public void An_answer_that_can_be_kept_nowhere_is_reported_with_its_transcription_id()
    {
        var log = new RecordingLogger<TranscriptionWorkflow>();
        _client.Setup(Calls.AnyTranscription).ReturnsAsync(() =>
        {
            LeaveNowhereToKeepTheAnswer();
            return Mocks.Transcript;
        });
        var workflow = CreateWorkflow(logger: log);

        var error = Assert.CatchAsync<IOException>(async () => await workflow.TranscribeRecordingAsync(_files, [_files.Segment(1)], CancellationToken.None));

        Assert.That(log.Problems, Has.Some.Contains("could not be kept anywhere").And.Contains(Mocks.Transcript.TranscriptionId).And.Contains("charged again"));
        Assert.That(ChargedAnswer.Of(error!), Is.EqualTo(new ChargedAnswer(KeptIn: null, Mocks.Transcript.TranscriptionId)));
    }

    /// <summary>
    /// A file whose name ends in a space or in dots before its extension, which Windows drops from a folder's name, is
    /// transcribed with a single upload, and its transcript and ElevenLabs' answer are saved under the tidied name.
    /// </summary>
    [TestCase("Lecture 5 .mp3", "Lecture 5")]
    [TestCase("Lecture 5. .mp4", "Lecture 5")]
    [TestCase("And then....mp4", "And then")]
    public async Task A_file_with_an_awkward_name_is_saved_after_a_single_upload(string fileName, string savedAs)
    {
        var audio = _folder.File(fileName);
        File.WriteAllBytes(audio, [1, 2, 3]);
        var folder = Path.Combine(_recordings, savedAs);

        var files = await CreateWorkflow().TranscribeFileAsync(audio, CancellationToken.None);

        _client.Verify(Calls.AnyTranscription, Times.Once);
        _client.Verify(Calls.TranscriptionOf(audio), Times.Once);
        Assert.That(files, Is.EqualTo(new TranscriptFiles(Path.Combine(folder, $"{savedAs}_2026-10-15_20-55-27Z.md"), Path.Combine(folder, $"{savedAs}_2026-10-15_20-55-27Z.json"))));
        Assert.That(File.ReadAllText(files.TranscriptPath), Does.StartWith($"# {savedAs}{Blank}"));
        Assert.That(File.ReadAllText(files.JsonPath), Is.EqualTo(Mocks.Transcript.RawJson));
    }

    /// <summary>A transcript without any words is saved, but with a warning, so an empty file is not taken for a normal result.</summary>
    [Test]
    public async Task An_empty_transcript_is_saved_with_a_warning()
    {
        var log = new RecordingLogger<TranscriptionWorkflow>();
        _client.Setup(Calls.AnyTranscription).ReturnsAsync(Mocks.Transcript with { Text = "", Words = [] });

        var files = await CreateWorkflow(logger: log).TranscribeRecordingAsync(_files, [_files.Segment(1)], CancellationToken.None);

        Assert.That(File.Exists(files.TranscriptPath), Is.True);
        Assert.That(log.Problems, Has.Some.Contains("no speech"));
    }
}
