using System.Linq.Expressions;
using System.Net;
using System.Text;
using ClassScribe.Audio;
using ClassScribe.Settings;
using ClassScribe.Storage;
using ClassScribe.Transcription;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NAudio.Wave;

namespace ClassScribe.Tests;

/// <summary>Preparation, upload and saving, and the free rebuild of a transcript, with a mocked preparer and service.</summary>
[TestFixture]
public class TranscriptionWorkflowTests
{
    private static readonly DateTimeOffset Started = new(2026, 10, 15, 20, 55, 27, TimeSpan.Zero);
    private static readonly string Blank = Environment.NewLine + Environment.NewLine;

    /// <summary>
    /// An answer in ElevenLabs' form whose words carry their times, so that a transcript made from it again, by a
    /// rebuild, comes out as the one made when it arrived: two sentences, the second after a pause of three seconds.
    /// </summary>
    private const string Answer = """{"language_code":"en","language_probability":0.99,"text":"Hello world. Goodbye.","words":[{"text":"Hello","start":0.0,"end":0.4,"type":"word","speaker_id":"speaker_0"},{"text":" ","start":0.4,"end":0.5,"type":"spacing","speaker_id":"speaker_0"},{"text":"world.","start":0.5,"end":1.0,"type":"word","speaker_id":"speaker_0"},{"text":" ","start":1.0,"end":4.0,"type":"spacing","speaker_id":"speaker_0"},{"text":"Goodbye.","start":4.0,"end":4.6,"type":"word","speaker_id":"speaker_0"}],"transcription_id":"id-answer"}""";

    /// <summary>The line the preparer logs when it leaves a microphone track out of the upload because it never rose above the sound threshold.</summary>
    private const string StayedSilent = "Your microphone stayed silent; only the class is sent.";

    /// <summary>Any request to mix the tracks of a recording into an upload file, as a recording with a microphone track is prepared.</summary>
    private static readonly Expression<Action<IAudioPreparer>> AnyTrackPreparation =
        preparer => preparer.Prepare(It.IsAny<UploadTracks>(), It.IsAny<string>(), It.IsAny<CancellationToken>());

    private TempFolder _folder = null!;
    private string _recordings = null!;
    private RecordingFiles _files = null!;
    private Mock<IAudioPreparer> _preparer = null!;
    private Mock<ITranscriptionClient> _client = null!;
    private FakeElevenLabsAccount _account = null!;

    /// <summary>What the mocked preparer was given to mix, one entry per request with both tracks and the token, in order.</summary>
    private List<(UploadTracks Tracks, string OutputPath, CancellationToken Token)> _trackPreparations = null!;

    /// <summary>
    /// Creates a temporary folder, a recordings folder with one recording's folder inside it, the recording's first
    /// segment on disk, and the mocks. The mocked preparer answers a request to mix the tracks as it answers one to
    /// prepare files: it writes the placeholder upload file, and keeps what it was given in <see cref="_trackPreparations"/>.
    /// </summary>
    [SetUp]
    public void SetUp()
    {
        _folder = new TempFolder();
        _recordings = _folder.File("Recordings");
        _files = RecordingFiles.ForNew(Path.Combine(_recordings, "Kalam"), "Kalam", Started);
        Directory.CreateDirectory(_files.Folder);
        WriteSegment(1);
        _preparer = Mocks.AudioPreparer();
        _trackPreparations = [];
        _preparer.Setup(AnyTrackPreparation)
            .Callback((UploadTracks tracks, string outputPath, CancellationToken token) =>
            {
                _trackPreparations.Add((tracks, outputPath, token));
                using var upload = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write);
                upload.Write([1, 2, 3]);
            });
        _client = Mocks.TranscriptionClient();
        _account = new FakeElevenLabsAccount();
    }

    /// <summary>Deletes the temporary folder. That fails if a test left a file open, which is the point.</summary>
    [TearDown]
    public void TearDown() => _folder.Dispose();

    /// <summary>
    /// Creates the workflow under test around the mocks, or around the preparer, settings, progress and loggers given; the
    /// account's report runs over the fake reader. The glossary is looked for beside the program alone, unless
    /// <paramref name="places"/> says where else.
    /// </summary>
    private TranscriptionWorkflow CreateWorkflow(
        bool keepUploadFile = false,
        string? keytermsFile = null,
        IAudioPreparer? preparer = null,
        ILogger<TranscriptionWorkflow>? logger = null,
        ILogger<KeytermLoader>? glossaryLogger = null,
        string apiKey = "dummy-key-for-a-local-test",
        TestOptions<TranscriptOptions>? transcript = null,
        TranscriptionProgress? progress = null,
        SettingsPlaces? places = null)
    {
        var recording = TestOptions.Create(new RecordingOptions { KeepUploadFile = keepUploadFile, OutputFolder = _recordings });
        return new(
            preparer ?? _preparer.Object,
            _client.Object,
            new KeytermLoader(places ?? new SettingsPlaces(AppContext.BaseDirectory, userFolder: null), glossaryLogger ?? NullLogger<KeytermLoader>.Instance),
            new TranscriptFormatter(transcript ?? TestOptions.Create(new TranscriptOptions())),
            new ClassFolders(recording, ZonedClock.Eastern(new ManualTimeProvider(Started)), NullLogger<ClassFolders>.Instance),
            new ClassMarks(NullLogger<ClassMarks>.Instance),
            TestOptions.Create(new ElevenLabsOptions { ApiKey = apiKey, KeytermsFile = keytermsFile ?? _folder.File("no-glossary.txt") }),
            recording,
            progress ?? new TranscriptionProgress(),
            new UsageReport(_account, TimeProvider.System, NullLogger<UsageReport>.Instance),
            logger ?? NullLogger<TranscriptionWorkflow>.Instance);
    }

    /// <summary>
    /// The account's credits are read before the upload, so that the difference afterwards is this transcript's, and the
    /// read never holds the upload up: here it answers only once the transcript is saved, and the upload went ahead.
    /// </summary>
    [Test]
    public async Task The_account_is_read_as_the_upload_begins_and_never_holds_it_up()
    {
        _account.Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var readsWhenUploading = -1;
        _client.Setup(Calls.AnyTranscription).Callback(() => readsWhenUploading = _account.Reads).ReturnsAsync(Mocks.Transcript);

        var saved = await CreateWorkflow().TranscribeRecordingAsync(_files, CancellationToken.None);
        _account.Hold.SetResult();

        Assert.That(File.Exists(saved.TranscriptPath), Is.True);
        Assert.That(readsWhenUploading, Is.EqualTo(1), "begun before the service was called");
        Assert.That(_account.Reads, Is.EqualTo(1), "the workflow reports nothing itself; the command does, once the results are open");
    }

    /// <summary>A failed upload has begun the read all the same, which nobody reports; a rebuild, which sends nothing, reads nothing.</summary>
    [Test]
    public async Task A_failed_upload_began_the_read_and_a_rebuild_reads_nothing()
    {
        _client.Setup(Calls.AnyTranscription).ThrowsAsync(new TranscriptionException("ElevenLabs is down."));
        File.WriteAllText(_files.Json(), Answer);
        var workflow = CreateWorkflow();

        Assert.ThrowsAsync<TranscriptionException>(async () => await workflow.TranscribeRecordingAsync(_files, CancellationToken.None));
        var afterTheFailure = _account.Reads;
        await workflow.RebuildAsync(_files, answerPath: null, CancellationToken.None);

        Assert.That((afterTheFailure, _account.Reads), Is.EqualTo((1, 1)));
    }

    /// <summary>
    /// Whoever follows the transcription is told that the upload copy is being prepared, and then that the upload begins,
    /// before the service is called; how far the upload gets is the service's to report.
    /// </summary>
    [Test]
    public async Task The_workflow_reports_preparing_and_then_the_upload_beginning()
    {
        var progress = new TranscriptionProgress();
        List<(TranscriptionStep Step, int? Percent)> reported = [];
        progress.Changed += (step, percent) => reported.Add((step, percent));
        var reportedBeforeSending = -1;
        _client.Setup(Calls.AnyTranscription).Callback(() => reportedBeforeSending = reported.Count).ReturnsAsync(Mocks.Transcript);

        await CreateWorkflow(progress: progress).TranscribeRecordingAsync(_files, CancellationToken.None);

        Assert.That(reported, Is.EqualTo(new[] { (TranscriptionStep.Preparing, (int?)null), (TranscriptionStep.Uploading, (int?)null) }));
        Assert.That(reportedBeforeSending, Is.EqualTo(2), "both before the service was called");
    }

    /// <summary>The real preparer, for tests that need the upload file it actually makes.</summary>
    private static AudioPreparer RealPreparer() => new(NullLogger<AudioPreparer>.Instance);

    /// <summary>The bytes of <paramref name="text"/> in UTF-8, without the byte order mark, which <see cref="Encoding.GetBytes(string)"/> never adds.</summary>
    private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);

    /// <summary>Writes segment <paramref name="number"/> of the recording's main track: a fifth of a second of tone, as the recorder writes it.</summary>
    private void WriteSegment(int number) =>
        TestAudio.WriteWav(_files.Segment(number), 16000, TestAudio.Tone(16000, seconds: 0.2, frequency: 440, amplitude: 0.5));

    /// <summary>Writes segment <paramref name="number"/> of the microphone's track as the samples given, at 16 kHz, as the recorder writes it.</summary>
    private void WriteMicrophoneSegment(int number, short[] samples) =>
        TestAudio.WriteWav(_files.Segment(number, Track.Microphone), 16000, samples);

    /// <summary>
    /// Writes segment <paramref name="number"/> of <paramref name="track"/> as <paramref name="length"/> of audio, with the
    /// recorder's own writer, at ten samples a second, so that hours of it take less than a megabyte.
    /// </summary>
    private void WriteLongSegment(int number, TimeSpan length, Track track = Track.Main)
    {
        File.Delete(_files.Segment(number, track));
        using var writer = new WavSegmentWriter(_files.Segment(number, track), 10);
        writer.Write(new byte[(int)Math.Round(length.TotalSeconds * 10) * 2]);
    }

    /// <summary>The samples of the file the mocked service was last given to transcribe (see <see cref="KeepTheUploadedSamples"/>).</summary>
    private short[] _uploaded = [];

    /// <summary>Has the mocked service keep the samples of the file it is given to transcribe in <see cref="_uploaded"/>, and answer as usual.</summary>
    private void KeepTheUploadedSamples() =>
        _client.Setup(Calls.AnyTranscription).ReturnsAsync((string path, IReadOnlyList<string> _, CancellationToken _) =>
        {
            _uploaded = TestAudio.ReadWav(path).Samples;
            return Mocks.Transcript;
        });

    /// <summary>Has the mocked service answer with <see cref="Answer"/>, read as the client reads it.</summary>
    private void AnswerWithTimedWords() =>
        _client.Setup(Calls.AnyTranscription).ReturnsAsync(ElevenLabsAnswer.Parse(Answer, HttpStatusCode.OK));

    /// <summary>
    /// A recording's segments on disk are prepared into one upload, and its transcript is saved beside them under the
    /// recording's name: the Markdown and ElevenLabs' answer byte for byte, in UTF-8 without a byte order mark.
    /// </summary>
    [Test]
    public async Task A_recording_is_prepared_uploaded_and_saved_under_its_name()
    {
        WriteSegment(2);

        var files = await CreateWorkflow().TranscribeRecordingAsync(_files, CancellationToken.None);

        _preparer.Verify(p => p.Prepare(new[] { _files.Segment(1), _files.Segment(2) }, _files.Upload, It.IsAny<CancellationToken>()), Times.Once);
        _client.Verify(Calls.TranscriptionOf(_files.Upload), Times.Once);
        Assert.That(files.TranscriptPath, Is.EqualTo(_files.Transcript()));
        Assert.That(files.JsonPath, Is.EqualTo(_files.Json()));
        Assert.That(File.ReadAllBytes(files.TranscriptPath), Is.EqualTo(Utf8($"# Kalam{Blank}Recorded 15 October 2026, 20:55 UTC.{Blank}[00:00:00] Hello world.{Environment.NewLine}")));
        Assert.That(File.ReadAllBytes(files.JsonPath), Is.EqualTo(Utf8(Mocks.Transcript.RawJson)));
        Assert.That(File.Exists(_files.Upload), Is.False, "the upload copy is removed after success");
    }

    /// <summary>
    /// Every segment of the recording on disk is sent, in recording order, and nothing else: not the upload copy a run
    /// left behind, nor another recording in the same folder.
    /// </summary>
    [Test]
    public async Task Every_segment_on_disk_is_prepared_in_order_and_nothing_else()
    {
        WriteSegment(3);
        WriteSegment(2);
        File.WriteAllBytes(_files.Upload, [1, 2, 3]);
        TestAudio.WriteWav(RecordingFiles.ForNew(_files.Folder, "Kalam", Started.AddDays(1)).Segment(1), 16000, [1000, -1000]);

        await CreateWorkflow().TranscribeRecordingAsync(_files, CancellationToken.None);

        _preparer.Verify(p => p.Prepare(new[] { _files.Segment(1), _files.Segment(2), _files.Segment(3) }, _files.Upload, It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// A recording made with the class and your microphone has the segments of both tracks on disk, and both are handed
    /// to the preparer together to be mixed, each track's segments in recording order and neither taking the other's
    /// files, with the run's token; the mixed file is what is sent, and it is removed afterwards as any upload copy is.
    /// </summary>
    [Test]
    public async Task Both_tracks_on_disk_are_handed_to_the_preparer_together_to_be_mixed()
    {
        using var cancellation = new CancellationTokenSource();
        WriteSegment(3);
        WriteSegment(2);
        WriteMicrophoneSegment(2, [500, 500]);
        WriteMicrophoneSegment(1, [500, 500]);

        await CreateWorkflow().TranscribeRecordingAsync(_files, cancellation.Token);

        Assert.That(_trackPreparations, Has.Count.EqualTo(1));
        var (tracks, outputPath, token) = _trackPreparations[0];
        Assert.That(tracks.Main, Is.EqualTo(new[] { _files.Segment(1), _files.Segment(2), _files.Segment(3) }));
        Assert.That(tracks.Microphone, Is.EqualTo(new[] { _files.Segment(1, Track.Microphone), _files.Segment(2, Track.Microphone) }));
        Assert.That(outputPath, Is.EqualTo(_files.Upload));
        Assert.That(token, Is.EqualTo(cancellation.Token), "Ctrl+C reaches the mix");
        _preparer.Verify(Calls.AnyPreparation, Times.Never, "the main track is not prepared on its own as well");
        _client.Verify(Calls.TranscriptionOf(_files.Upload), Times.Once);
        Assert.That(File.Exists(_files.Upload), Is.False, "the upload copy is removed after success");
    }

    /// <summary>
    /// A recording without microphone files, as in person, where your microphone is the main track under the recording's
    /// plain names, or with the class alone, is prepared from its main track alone, as before: there is nothing to mix.
    /// </summary>
    [Test]
    public async Task Without_microphone_files_the_main_track_is_prepared_alone_as_in_person_or_with_the_class_alone()
    {
        WriteSegment(2);

        await CreateWorkflow().TranscribeRecordingAsync(_files, CancellationToken.None);

        _preparer.Verify(p => p.Prepare(new[] { _files.Segment(1), _files.Segment(2) }, _files.Upload, It.IsAny<CancellationToken>()), Times.Once);
        Assert.That(_trackPreparations, Is.Empty);
    }

    /// <summary>
    /// Preparing a recording with a microphone track is announced with the segments of both tracks, so the wait for the
    /// mix of a long class is not taken for a hang.
    /// </summary>
    [Test]
    public async Task Preparing_a_recording_with_a_microphone_track_is_announced_with_both_tracks()
    {
        var log = new RecordingLogger<TranscriptionWorkflow>();
        WriteSegment(2);
        WriteMicrophoneSegment(1, [500, 500]);

        await CreateWorkflow(logger: log).TranscribeRecordingAsync(_files, CancellationToken.None);

        Assert.That(log.Messages, Has.Some.EqualTo("Preparing the recording for upload (2 segment(s) of the class and 1 of your microphone); this takes a moment for a long class"));
    }

    /// <summary>
    /// With the real preparer, the class and your microphone are mixed into the one file that is uploaded: each sample
    /// of the upload is the sum of the two tracks' samples at that moment.
    /// </summary>
    [Test]
    public async Task The_class_and_the_microphone_are_mixed_into_the_upload()
    {
        File.Delete(_files.Segment(1));
        TestAudio.WriteWav(_files.Segment(1), 16000, [1000, 1000, 1000]);
        WriteMicrophoneSegment(1, [500, 500, 500]);
        KeepTheUploadedSamples();

        await CreateWorkflow(preparer: RealPreparer()).TranscribeRecordingAsync(_files, CancellationToken.None);

        Assert.That(_uploaded, Is.EqualTo(new short[] { 1500, 1500, 1500 }));
    }

    /// <summary>
    /// A microphone track that never rose above the sound threshold, as when the microphone stayed off or muted all
    /// class, is left out: the upload is the class's alone, and the preparer's log says so, in plain words, at the level
    /// of the other lines of a normal run.
    /// </summary>
    [Test]
    public async Task A_microphone_that_stayed_silent_is_left_out_of_the_upload_and_the_log_says_so()
    {
        var preparerLog = new RecordingLogger<AudioPreparer>();
        File.Delete(_files.Segment(1));
        TestAudio.WriteWav(_files.Segment(1), 16000, [1000, 1000, 1000]);
        WriteMicrophoneSegment(1, [0, 50, -50]);
        KeepTheUploadedSamples();

        await CreateWorkflow(preparer: new AudioPreparer(preparerLog)).TranscribeRecordingAsync(_files, CancellationToken.None);

        Assert.That(_uploaded, Is.EqualTo(new short[] { 1000, 1000, 1000 }));
        Assert.That(preparerLog.Entries, Has.Some.EqualTo((LogLevel.Information, StayedSilent)));
        Assert.That(File.Exists(_files.Segment(1, Track.Microphone)), Is.True, "the microphone's files stay on disk whatever happens");
    }

    /// <summary>
    /// A recording with only your microphone's files, which a class whose device could not be read leaves behind, is sent
    /// on its own, as in person: the .mic.wav files are prepared as the one track, nothing is mixed, the transcript goes
    /// under the recording's name, and the log says so.
    /// </summary>
    [Test]
    public async Task A_recording_with_only_the_microphones_files_is_sent_on_its_own()
    {
        var log = new RecordingLogger<TranscriptionWorkflow>();
        File.Delete(_files.Segment(1));
        WriteMicrophoneSegment(2, [500, 500]);
        WriteMicrophoneSegment(1, [500, 500]);

        var files = await CreateWorkflow(logger: log).TranscribeRecordingAsync(_files, CancellationToken.None);

        _preparer.Verify(p => p.Prepare(new[] { _files.Segment(1, Track.Microphone), _files.Segment(2, Track.Microphone) }, _files.Upload, It.IsAny<CancellationToken>()), Times.Once);
        Assert.That(_trackPreparations, Is.Empty, "nothing to mix");
        _client.Verify(Calls.TranscriptionOf(_files.Upload), Times.Once);
        Assert.That(files.TranscriptPath, Is.EqualTo(_files.Transcript()));
        Assert.That(log.Messages, Has.Some.EqualTo("Only your microphone was recorded (2 segment(s)); it is sent on its own"));
    }

    /// <summary>A recording of the microphone's files alone is measured by them, and refused when too long, with advice that names the .mic.wav files, since a folder is read by its .wav files.</summary>
    [Test]
    public void A_recording_with_only_the_microphones_files_is_measured_by_them()
    {
        File.Delete(_files.Segment(1));
        WriteLongSegment(1, new TimeSpan(9, 30, 1), Track.Microphone);
        var workflow = CreateWorkflow();

        var error = Assert.ThrowsAsync<RecordingTooLongException>(async () => await workflow.TranscribeRecordingAsync(_files, CancellationToken.None));

        Assert.That(error!.Message, Does.StartWith($"{_files.BaseName} holds 09:30:01 of audio").And.Contain("copy some of its .mic.wav files into a folder of their own"));
        _preparer.Verify(Calls.AnyPreparation, Times.Never);
    }

    /// <summary>A recording with no audio on disk is refused, by its name, before anything is prepared or sent.</summary>
    [Test]
    public void A_recording_without_audio_on_disk_is_refused_before_anything_is_prepared_or_sent()
    {
        File.Delete(_files.Segment(1));
        var workflow = CreateWorkflow();

        var error = Assert.ThrowsAsync<FileNotFoundException>(async () => await workflow.TranscribeRecordingAsync(_files, CancellationToken.None));

        Assert.That(error!.Message, Does.Contain(_files.BaseName));
        _preparer.Verify(Calls.AnyPreparation, Times.Never);
        _client.Verify(Calls.AnyTranscription, Times.Never);
    }

    /// <summary>
    /// A recording longer than nine and a half hours, here in two segments, is refused before anything is prepared or
    /// sent: ElevenLabs takes at most 10 hours per file. The message gives the length and says what to do instead.
    /// </summary>
    [Test]
    public void A_recording_longer_than_nine_and_a_half_hours_is_refused_before_anything_is_prepared_or_sent()
    {
        WriteLongSegment(1, new TimeSpan(4, 45, 0));
        WriteLongSegment(2, new TimeSpan(4, 45, 1));
        var workflow = CreateWorkflow();

        var error = Assert.ThrowsAsync<RecordingTooLongException>(async () => await workflow.TranscribeRecordingAsync(_files, CancellationToken.None));

        Assert.That(error!.Message, Does.StartWith($"{_files.BaseName} holds 09:30:01 of audio").And.Contain("at most 10 hours per file").And.Contain("Nothing was prepared or sent"));
        Assert.That(error.Message, Does.Contain("copy some of its .wav files into a folder of their own and transcribe that folder").And.Not.Contain(".mic.wav"),
            "a recording without a microphone track gets the plain advice");
        _preparer.Verify(Calls.AnyPreparation, Times.Never);
        _client.Verify(Calls.AnyTranscription, Times.Never);
        Assert.That(File.Exists(_files.Upload), Is.False);
    }

    /// <summary>
    /// The length check reads the main track alone: your microphone's track runs on the same clock as the class, so it
    /// is as long, and adding the two would refuse a class of five hours. Here each track holds five hours, and the
    /// recording is mixed and sent.
    /// </summary>
    [Test]
    public async Task The_length_check_reads_the_main_track_alone_and_never_adds_the_microphones()
    {
        WriteLongSegment(1, TimeSpan.FromHours(5));
        WriteLongSegment(1, TimeSpan.FromHours(5), Track.Microphone);

        await CreateWorkflow().TranscribeRecordingAsync(_files, CancellationToken.None);

        Assert.That(_trackPreparations, Has.Count.EqualTo(1));
        _client.Verify(Calls.TranscriptionOf(_files.Upload), Times.Once);
    }

    /// <summary>A recording with a microphone track whose main track is too long is refused the same way, before either track is read for the mix.</summary>
    [Test]
    public void A_recording_with_a_microphone_track_that_is_too_long_is_refused_before_anything_is_prepared()
    {
        WriteLongSegment(1, new TimeSpan(9, 30, 1));
        WriteLongSegment(1, TimeSpan.FromSeconds(1), Track.Microphone);
        var workflow = CreateWorkflow();

        Assert.ThrowsAsync<RecordingTooLongException>(async () => await workflow.TranscribeRecordingAsync(_files, CancellationToken.None));

        Assert.That(_trackPreparations, Is.Empty);
        _preparer.Verify(Calls.AnyPreparation, Times.Never);
        _client.Verify(Calls.AnyTranscription, Times.Never);
    }

    /// <summary>
    /// The advice in the refusal of a recording with a microphone track names the .mic.wav files: a folder holding only
    /// the copied .wav files looks like a class-only recording, so following the plain advice would drop your microphone
    /// from every piece without a word. The message says to copy the matching .mic.wav files beside them under the same
    /// names, and that without them only the class is sent.
    /// </summary>
    [Test]
    public void The_refusal_of_a_recording_with_a_microphone_track_says_to_copy_the_mic_wav_files_with_the_pieces()
    {
        WriteLongSegment(1, new TimeSpan(9, 30, 1));
        WriteLongSegment(1, TimeSpan.FromSeconds(1), Track.Microphone);
        var workflow = CreateWorkflow();

        var error = Assert.ThrowsAsync<RecordingTooLongException>(async () => await workflow.TranscribeRecordingAsync(_files, CancellationToken.None));

        Assert.That(error!.Message, Does.StartWith($"{_files.BaseName} holds 09:30:01 of audio").And.Contain("Nothing was prepared or sent"));
        Assert.That(error.Message, Does.Contain("copy some of its .wav files into a folder of their own")
            .And.Contain("the matching .mic.wav files")
            .And.Contain("under the same names")
            .And.Contain("only the class is sent"));
    }

    /// <summary>A recording of exactly nine and a half hours is still sent.</summary>
    [Test]
    public async Task A_recording_of_nine_and_a_half_hours_is_still_sent()
    {
        WriteLongSegment(1, TimeSpan.FromHours(9.5));

        await CreateWorkflow().TranscribeRecordingAsync(_files, CancellationToken.None);

        _client.Verify(Calls.TranscriptionOf(_files.Upload), Times.Once);
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

        var files = await CreateWorkflow().TranscribeRecordingAsync(_files, CancellationToken.None);

        Assert.That(File.ReadAllBytes(files.TranscriptPath), Is.EqualTo(Utf8($"# Kalam{Blank}Recorded 15 October 2026, 20:55 UTC.{Blank}[00:00:00] {text}{Environment.NewLine}")));
        Assert.That(File.ReadAllBytes(files.JsonPath), Is.EqualTo(Utf8(answer)));
    }

    /// <summary>The marks set while recording appear in the recording's transcript.</summary>
    [Test]
    public async Task The_marks_of_a_recording_appear_in_its_transcript()
    {
        File.WriteAllLines(_files.Marks, ["00:00:00.450"]);

        var files = await CreateWorkflow().TranscribeRecordingAsync(_files, CancellationToken.None);

        Assert.That(File.ReadAllText(files.TranscriptPath), Does.Contain($"[00:00:00] Hello{Blank}## [00:00:00] Mark 1{Blank}[00:00:00] world."));
    }

    /// <summary>Transcribing a recording again keeps the earlier transcript and writes a second one.</summary>
    [Test]
    public async Task Transcribing_a_recording_again_keeps_the_earlier_transcript()
    {
        var workflow = CreateWorkflow();

        var first = await workflow.TranscribeRecordingAsync(_files, CancellationToken.None);
        var second = await workflow.TranscribeRecordingAsync(_files, CancellationToken.None);

        Assert.That(second.TranscriptPath, Is.EqualTo(_files.Transcript(2)));
        Assert.That(second.JsonPath, Is.EqualTo(_files.Json(2)));
        Assert.That(File.Exists(first.TranscriptPath) && File.Exists(second.TranscriptPath), Is.True);
    }

    /// <summary>With KeepUploadFile the 16 kHz copy stays after success.</summary>
    [Test]
    public async Task The_upload_copy_can_be_kept()
    {
        await CreateWorkflow(keepUploadFile: true).TranscribeRecordingAsync(_files, CancellationToken.None);

        Assert.That(File.Exists(_files.Upload), Is.True);
    }

    /// <summary>The glossary file's terms go with the audio.</summary>
    [Test]
    public async Task The_glossary_is_sent_with_the_audio()
    {
        var glossary = _folder.File("keyterms.txt");
        File.WriteAllLines(glossary, ["# terms", "kalām", "Ashʿarī"]);

        await CreateWorkflow(keytermsFile: glossary).TranscribeRecordingAsync(_files, CancellationToken.None);

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

        await CreateWorkflow(keytermsFile: "", glossaryLogger: glossaryLog).TranscribeRecordingAsync(_files, CancellationToken.None);

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
        using (new TemporaryVariable(variable, _folder.Path))
        {
            await CreateWorkflow(keytermsFile: $@"%{variable}%\terms.txt", glossaryLogger: glossaryLog)
                .TranscribeRecordingAsync(_files, CancellationToken.None);
        }

        _client.Verify(c => c.TranscribeAsync(It.IsAny<string>(), new[] { "kalām", "Ashʿarī" }, It.IsAny<CancellationToken>()), Times.Once);
        Assert.That(glossaryLog.Problems, Is.Empty);
    }

    /// <summary>
    /// A relative ElevenLabs:KeytermsFile, keyterms.txt as shipped, is taken from the folder the settings were read from
    /// first, the user's own glossary, and from the program's folder second, the shipped one.
    /// </summary>
    [Test]
    public async Task A_relative_glossary_name_is_taken_from_the_users_folder_first_then_beside_the_program()
    {
        using var program = new TempFolder();
        using var user = new TempFolder();
        File.WriteAllText(user.File("appsettings.json"), "{}");
        File.WriteAllLines(program.File("keyterms.txt"), ["shipped"]);
        var places = new SettingsPlaces(program.Path, user.Path);
        var sent = new List<IReadOnlyList<string>>();
        _client.Setup(Calls.AnyTranscription).Callback((string _, IReadOnlyList<string> terms, CancellationToken _) => sent.Add(terms)).ReturnsAsync(Mocks.Transcript);

        await CreateWorkflow(keytermsFile: "keyterms.txt", places: places).TranscribeRecordingAsync(_files, CancellationToken.None);
        File.WriteAllLines(user.File("keyterms.txt"), ["kalām"]);
        await CreateWorkflow(keytermsFile: "keyterms.txt", places: places).TranscribeRecordingAsync(_files, CancellationToken.None);

        Assert.That(sent, Is.EqualTo(new[] { new[] { "shipped" }, new[] { "kalām" } }));
    }

    /// <summary>
    /// A video goes up as it is, and its transcript gets a folder of its own in the recordings folder, named after it, with
    /// the date folder of the day it was transcribed in it, and is stamped.
    /// </summary>
    [Test]
    public async Task A_video_file_is_uploaded_as_it_is_and_its_transcript_goes_to_the_recordings_folder()
    {
        var video = _folder.File("class.mp4");
        File.WriteAllBytes(video, [1, 2, 3]);

        var files = await CreateWorkflow().TranscribeFileAsync(video, outputRoot: null, CancellationToken.None);

        _preparer.Verify(Calls.AnyPreparation, Times.Never);
        _client.Verify(Calls.TranscriptionOf(video), Times.Once);
        Assert.That(files.TranscriptPath, Is.EqualTo(Path.Combine(_recordings, "class", "2026-10-15", "class_2026-10-15_20-55-27Z.md")));
        Assert.That(files.JsonPath, Is.EqualTo(Path.Combine(_recordings, "class", "2026-10-15", "class_2026-10-15_20-55-27Z.json")));
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

        var first = await workflow.TranscribeFileAsync(video, outputRoot: null, CancellationToken.None);
        var second = await workflow.TranscribeFileAsync(video, outputRoot: null, CancellationToken.None);

        Assert.That(second.TranscriptPath, Is.Not.EqualTo(first.TranscriptPath));
        Assert.That(File.Exists(first.TranscriptPath) && File.Exists(second.TranscriptPath), Is.True);
    }

    /// <summary>A WAV file is reduced to a 16 kHz copy in the transcript's folder, which goes after success.</summary>
    [Test]
    public async Task A_wav_file_is_reduced_before_upload()
    {
        var wav = _folder.File("class.wav");
        var upload = Path.Combine(_recordings, "class", "2026-10-15", "class_2026-10-15_20-55-27Z.upload.wav");
        File.WriteAllBytes(wav, [1, 2, 3]);

        await CreateWorkflow().TranscribeFileAsync(wav, outputRoot: null, CancellationToken.None);

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

        await CreateWorkflow(logger: log).TranscribeFileAsync(wav, outputRoot: null, CancellationToken.None);

        Assert.That(log.Messages, Has.Some.StartsWith("Preparing class.wav for upload"));
    }

    /// <summary>A WAV file the preparer cannot read is uploaded as it is.</summary>
    [Test]
    public async Task A_wav_file_that_cannot_be_reduced_is_uploaded_as_it_is()
    {
        var wav = _folder.File("odd.wav");
        File.WriteAllBytes(wav, [1, 2, 3]);
        _preparer.Setup(Calls.AnyPreparation).Throws(new NotSupportedException("six channels"));

        await CreateWorkflow().TranscribeFileAsync(wav, outputRoot: null, CancellationToken.None);

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

        await CreateWorkflow(preparer: RealPreparer()).TranscribeFileAsync(wav, outputRoot: null, CancellationToken.None);

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

        Assert.ThrowsAsync<NoAudioException>(async () => await workflow.TranscribeFileAsync(wav, outputRoot: null, CancellationToken.None));
        _client.Verify(Calls.AnyTranscription, Times.Never);
    }

    /// <summary>A failed upload surfaces as an error; the recording stays for another try, and the upload copy made for it is removed.</summary>
    [Test]
    public void A_failed_upload_surfaces_keeps_the_recording_and_removes_the_upload_copy()
    {
        _client.Setup(Calls.AnyTranscription).ThrowsAsync(new TranscriptionException("ElevenLabs is down."));
        var workflow = CreateWorkflow();

        Assert.ThrowsAsync<TranscriptionException>(async () => await workflow.TranscribeRecordingAsync(_files, CancellationToken.None));

        _client.Verify(Calls.TranscriptionOf(_files.Upload), Times.Once);
        Assert.That(File.Exists(_files.Upload), Is.False, "the upload copy is removed after a failure");
        Assert.That(File.Exists(_files.Segment(1)), Is.True, "the recording stays");
        Assert.That(File.Exists(_files.Transcript()), Is.False);
    }

    /// <summary>An upload cancelled with Ctrl+C leaves no upload copy behind either; the recording stays.</summary>
    [Test]
    public void A_cancelled_upload_leaves_no_upload_copy()
    {
        using var cancellation = new CancellationTokenSource();
        _client.Setup(Calls.AnyTranscription).Returns(() =>
        {
            cancellation.Cancel();
            return Task.FromCanceled<TranscriptionResult>(cancellation.Token);
        });
        var workflow = CreateWorkflow();

        Assert.CatchAsync<OperationCanceledException>(async () => await workflow.TranscribeRecordingAsync(_files, cancellation.Token));

        _client.Verify(Calls.TranscriptionOf(_files.Upload), Times.Once);
        Assert.That(File.Exists(_files.Upload), Is.False);
        Assert.That(File.Exists(_files.Segment(1)), Is.True);
    }

    /// <summary>With KeepUploadFile the upload copy stays after a failed upload as well.</summary>
    [Test]
    public void With_KeepUploadFile_the_upload_copy_stays_after_a_failed_upload()
    {
        _client.Setup(Calls.AnyTranscription).ThrowsAsync(new TranscriptionException("ElevenLabs is down."));
        var workflow = CreateWorkflow(keepUploadFile: true);

        Assert.ThrowsAsync<TranscriptionException>(async () => await workflow.TranscribeRecordingAsync(_files, CancellationToken.None));

        Assert.That(File.Exists(_files.Upload), Is.True);
    }

    /// <summary>
    /// When the upload copy cannot be removed after a failed upload, because another program holds it, the failure
    /// is still the error reported, and the copy left behind is named with a warning that does not claim a saved transcript.
    /// </summary>
    [Test]
    public void An_upload_copy_that_cannot_be_removed_after_a_failure_leaves_the_failure_as_the_error()
    {
        var log = new RecordingLogger<TranscriptionWorkflow>();
        FileStream? holder = null;
        _client.Setup(Calls.AnyTranscription)
            .Callback((string path, IReadOnlyList<string> _, CancellationToken _) => holder = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            .ThrowsAsync(new TranscriptionException("ElevenLabs is down."));
        var workflow = CreateWorkflow(logger: log);

        try
        {
            var error = Assert.ThrowsAsync<TranscriptionException>(async () => await workflow.TranscribeRecordingAsync(_files, CancellationToken.None));

            Assert.That(error!.Message, Is.EqualTo("ElevenLabs is down."));
            Assert.That(log.Problems, Has.Some.Contains(Path.GetFileName(_files.Upload)).And.Not.Contains("transcript is saved"));
        }
        finally
        {
            holder?.Dispose();
        }
    }

    /// <summary>The upload copy of a WAV file transcribed on its own is removed after a failed upload too; the file itself stays.</summary>
    [Test]
    public void The_upload_copy_of_a_wav_file_is_removed_after_a_failed_upload()
    {
        var wav = _folder.File("class.wav");
        File.WriteAllBytes(wav, [1, 2, 3]);
        var upload = Path.Combine(_recordings, "class", "2026-10-15", "class_2026-10-15_20-55-27Z.upload.wav");
        _client.Setup(Calls.AnyTranscription).ThrowsAsync(new TranscriptionException("ElevenLabs is down."));
        var workflow = CreateWorkflow();

        Assert.ThrowsAsync<TranscriptionException>(async () => await workflow.TranscribeFileAsync(wav, outputRoot: null, CancellationToken.None));

        _client.Verify(Calls.TranscriptionOf(upload), Times.Once);
        Assert.That(File.Exists(upload), Is.False);
        Assert.That(File.Exists(wav), Is.True);
    }

    /// <summary>
    /// A recording can be transcribed again after a failed upload with the real preparer, which never writes over a
    /// file, and an upload copy left behind by a run that was ended part-way, before it could remove it, is made afresh
    /// rather than tripped over.
    /// </summary>
    [Test]
    public async Task A_recording_can_be_transcribed_again_after_a_failure_or_a_run_that_was_ended()
    {
        _client.SetupSequence(Calls.AnyTranscription)
            .ThrowsAsync(new TranscriptionException("ElevenLabs is down."))
            .ReturnsAsync(Mocks.Transcript);
        var workflow = CreateWorkflow(preparer: RealPreparer());
        Assert.ThrowsAsync<TranscriptionException>(async () => await workflow.TranscribeRecordingAsync(_files, CancellationToken.None));
        File.WriteAllBytes(_files.Upload, [9, 9, 9]);   // as a run ended part-way leaves it

        var saved = await workflow.TranscribeRecordingAsync(_files, CancellationToken.None);

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

        await workflow.TranscribeRecordingAsync(_files, cancellation.Token);
        await workflow.TranscribeFileAsync(wav, outputRoot: null, cancellation.Token);

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

        Assert.CatchAsync<OperationCanceledException>(async () => await workflow.TranscribeRecordingAsync(_files, cancellation.Token));

        _preparer.Verify(Calls.AnyPreparation, Times.Never);
        _client.Verify(Calls.AnyTranscription, Times.Never);
    }

    /// <summary>
    /// A cancel that comes while the upload file is made stops the run before anything is sent or announced as sent,
    /// and the upload file it made is removed.
    /// </summary>
    [Test]
    public void A_cancel_during_the_preparation_sends_nothing_and_leaves_no_upload_copy()
    {
        using var cancellation = new CancellationTokenSource();
        _preparer.Setup(Calls.AnyPreparation).Callback((IReadOnlyList<string> _, string outputPath, CancellationToken _) =>
        {
            File.WriteAllBytes(outputPath, [1, 2, 3]);
            cancellation.Cancel();   // Ctrl+C as the last of the upload file is written
        });
        var workflow = CreateWorkflow();

        Assert.CatchAsync<OperationCanceledException>(async () => await workflow.TranscribeRecordingAsync(_files, cancellation.Token));

        _client.Verify(Calls.AnyTranscription, Times.Never);
        Assert.That(File.Exists(_files.Upload), Is.False);
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

        var files = await CreateWorkflow().TranscribeRecordingAsync(_files, cancellation.Token);

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
            var files = await CreateWorkflow(logger: log).TranscribeRecordingAsync(_files, CancellationToken.None);

            Assert.That(File.Exists(files.TranscriptPath), Is.True);
            Assert.That(log.Problems, Has.Some.Contains(Path.GetFileName(_files.Upload)).And.Contains("transcript is saved"));
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
            var files = await CreateWorkflow(logger: log).TranscribeFileAsync(wav, outputRoot: null, CancellationToken.None);

            Assert.That(File.Exists(files.TranscriptPath), Is.True);
            Assert.That(holder?.Name, Is.EqualTo(Path.Combine(_recordings, "class", "2026-10-15", "class_2026-10-15_20-55-27Z.upload.wav")), "the upload copy was sent");
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

        var error = Assert.ThrowsAsync<TranscriptionException>(async () => await workflow.TranscribeRecordingAsync(_files, CancellationToken.None));

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

        var error = Assert.ThrowsAsync<TranscriptionException>(async () => await workflow.TranscribeRecordingAsync(_files, CancellationToken.None));

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
        Assert.ThrowsAsync<TranscriptionException>(async () => await workflow.TranscribeRecordingAsync(_files, CancellationToken.None));

        var saved = await workflow.TranscribeRecordingAsync(_files, CancellationToken.None);

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
            Assert.ThrowsAsync<TranscriptionException>(async () => await workflow.TranscribeRecordingAsync(_files, CancellationToken.None));

        Assert.That(File.ReadAllText(Path.Combine(_files.Folder, "Kalam_2026-10-15_20-55-27Z.unread.json")), Is.EqualTo("""{"first":1}"""));
        Assert.That(File.ReadAllText(Path.Combine(_files.Folder, "Kalam_2026-10-15_20-55-27Z (2).unread.json")), Is.EqualTo("""{"second":2}"""));
    }

    /// <summary>A refusal is no answer to keep: nothing is saved anywhere when ElevenLabs turns the request down.</summary>
    [Test]
    public void A_refusal_is_not_kept()
    {
        _client.Setup(Calls.AnyTranscription).ThrowsAsync(new TranscriptionException("ElevenLabs rejected the API key.", HttpStatusCode.Unauthorized, """{"detail":{"status":"invalid_api_key"}}"""));
        var workflow = CreateWorkflow();

        Assert.ThrowsAsync<TranscriptionException>(async () => await workflow.TranscribeRecordingAsync(_files, CancellationToken.None));

        Assert.That(Directory.GetFiles(_recordings, "*.json", SearchOption.AllDirectories), Is.Empty);
    }

    /// <summary>
    /// When saving fails after the upload (here a folder is in the way of the answer's .json), the error surfaces, only
    /// the one upload has been made, and the answer, already paid for, is kept beside the recording under a name of its
    /// own, byte for byte, which the error gives. The upload copy is removed as after any failure, and the recording does
    /// not count as transcribed.
    /// </summary>
    [Test]
    public void An_answer_that_cannot_be_saved_is_kept_under_a_name_of_its_own()
    {
        Directory.CreateDirectory(_files.Json());   // a folder in the way of the answer, so saving it fails
        var log = new RecordingLogger<TranscriptionWorkflow>();
        var workflow = CreateWorkflow(logger: log);

        var error = Assert.ThrowsAsync<UnauthorizedAccessException>(async () => await workflow.TranscribeRecordingAsync(_files, CancellationToken.None));

        _client.Verify(Calls.AnyTranscription, Times.Once);
        Assert.That(File.ReadAllBytes(_files.UnreadAnswer()), Is.EqualTo(Utf8(Mocks.Transcript.RawJson)));
        Assert.That(log.Problems, Has.Some.Contains(_files.UnreadAnswer()));
        Assert.That(File.Exists(_files.Upload), Is.False, "the upload copy is removed");
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

        var error = Assert.CatchAsync<IOException>(async () => await workflow.TranscribeRecordingAsync(_files, CancellationToken.None));

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

        var error = Assert.CatchAsync(async () => await workflow.TranscribeRecordingAsync(_files, CancellationToken.None));

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

        var error = Assert.CatchAsync(async () => await workflow.TranscribeRecordingAsync(_files, CancellationToken.None));

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
            if (!Directory.Exists(_files.Folder))
            {
                // The recording comes back, as when it is put back after the first attempt took its folder away.
                Directory.CreateDirectory(_files.Folder);
                WriteSegment(1);
            }

            Assert.CatchAsync<IOException>(async () => await workflow.TranscribeRecordingAsync(_files, CancellationToken.None));
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

        var error = Assert.CatchAsync<IOException>(async () => await workflow.TranscribeRecordingAsync(_files, CancellationToken.None));

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
        var folder = Path.Combine(_recordings, savedAs, "2026-10-15");

        var files = await CreateWorkflow().TranscribeFileAsync(audio, outputRoot: null, CancellationToken.None);

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

        var files = await CreateWorkflow(logger: log).TranscribeRecordingAsync(_files, CancellationToken.None);

        Assert.That(File.Exists(files.TranscriptPath), Is.True);
        Assert.That(log.Problems, Has.Some.Contains("no speech"));
    }

    /// <summary>
    /// A rebuild of a recording whose marks and settings have not changed since it was transcribed gives the same
    /// transcript, byte for byte, as the next copy: the title, the recording time, the paragraphs and the mark.
    /// </summary>
    [Test]
    public async Task A_rebuild_gives_the_same_transcript_byte_for_byte_when_nothing_has_changed()
    {
        File.WriteAllLines(_files.Marks, ["00:00:02.000"]);
        AnswerWithTimedWords();
        var workflow = CreateWorkflow();
        var transcribed = await workflow.TranscribeRecordingAsync(_files, CancellationToken.None);

        var rebuilt = await workflow.RebuildAsync(_files, answerPath: null, CancellationToken.None);

        Assert.That(rebuilt, Is.EqualTo(_files.Transcript(2)));
        Assert.That(File.ReadAllText(transcribed.TranscriptPath), Does.Contain("Recorded 15 October 2026").And.Contain("## [00:00:02] Mark 1").And.Contain("[00:00:04] Goodbye."));
        Assert.That(File.ReadAllBytes(rebuilt), Is.EqualTo(File.ReadAllBytes(transcribed.TranscriptPath)));
    }

    /// <summary>A rebuild of a file transcribed on its own gives the same transcript too, without a recording time, as the first had none.</summary>
    [Test]
    public async Task A_rebuild_of_a_file_transcribed_on_its_own_gives_the_same_transcript()
    {
        var video = _folder.File("class.mp4");
        File.WriteAllBytes(video, [1, 2, 3]);
        AnswerWithTimedWords();
        var workflow = CreateWorkflow();
        var transcribed = await workflow.TranscribeFileAsync(video, outputRoot: null, CancellationToken.None);
        var files = new RecordingFiles(Path.GetDirectoryName(transcribed.TranscriptPath)!, Path.GetFileNameWithoutExtension(transcribed.TranscriptPath));

        var rebuilt = await workflow.RebuildAsync(files, answerPath: null, CancellationToken.None);

        Assert.That(File.ReadAllText(rebuilt), Does.Not.Contain("Recorded"));
        Assert.That(File.ReadAllBytes(rebuilt), Is.EqualTo(File.ReadAllBytes(transcribed.TranscriptPath)));
    }

    /// <summary>
    /// A rebuild of a recording whose audio was deleted after it was transcribed, and which never had marks, still gives
    /// the same transcript byte for byte, its recording time included: the transcript saved with the answer says when
    /// the recording was made.
    /// </summary>
    [Test]
    public async Task A_rebuild_after_the_audio_was_deleted_gives_the_same_transcript_byte_for_byte()
    {
        AnswerWithTimedWords();
        var workflow = CreateWorkflow();
        var transcribed = await workflow.TranscribeRecordingAsync(_files, CancellationToken.None);
        File.Delete(_files.Segment(1));

        var rebuilt = await workflow.RebuildAsync(_files, answerPath: null, CancellationToken.None);

        Assert.That(File.ReadAllText(transcribed.TranscriptPath), Does.Contain("Recorded 15 October 2026, 20:55 UTC."));
        Assert.That(File.ReadAllBytes(rebuilt), Is.EqualTo(File.ReadAllBytes(transcribed.TranscriptPath)));
    }

    /// <summary>
    /// A rebuild keeps a recording's time while anything beside it shows that it is a recording: its audio, its marks,
    /// or a transcript made of it. Here only <paramref name="left"/> is left of the three.
    /// </summary>
    [TestCase("audio")]
    [TestCase("marks")]
    [TestCase("transcript")]
    public async Task A_rebuild_keeps_the_recording_time_while_its_audio_its_marks_or_a_transcript_is_left(string left)
    {
        File.WriteAllLines(_files.Marks, ["00:00:02.000"]);
        AnswerWithTimedWords();
        var workflow = CreateWorkflow();
        await workflow.TranscribeRecordingAsync(_files, CancellationToken.None);
        if (left != "audio") File.Delete(_files.Segment(1));
        if (left != "marks") File.Delete(_files.Marks);
        if (left != "transcript") File.Delete(_files.Transcript());

        var rebuilt = await workflow.RebuildAsync(_files, answerPath: null, CancellationToken.None);

        Assert.That(File.ReadAllText(rebuilt), Does.StartWith($"# Kalam{Blank}Recorded 15 October 2026, 20:55 UTC.{Blank}"));
    }

    /// <summary>
    /// Your microphone's track counts as the recording's audio too: with the main track, the marks and every transcript
    /// gone, a rebuild still keeps the recording time while a .mic.wav file is left beside the answer.
    /// </summary>
    [Test]
    public async Task A_rebuild_keeps_the_recording_time_while_the_microphones_track_is_left()
    {
        File.Delete(_files.Segment(1));
        WriteMicrophoneSegment(1, [500, 500]);
        File.WriteAllText(_files.Json(), Answer);

        var rebuilt = await CreateWorkflow().RebuildAsync(_files, answerPath: null, CancellationToken.None);

        Assert.That(File.ReadAllText(rebuilt), Does.StartWith($"# Kalam{Blank}Recorded 15 October 2026, 20:55 UTC.{Blank}"));
    }

    /// <summary>
    /// The transcript that says when the recording was made need not be the one saved with the answer: here that one was
    /// deleted with the audio, and a copy rebuilt earlier was kept, so the next rebuild comes out as that copy.
    /// </summary>
    [Test]
    public async Task A_rebuild_keeps_the_recording_time_that_any_transcript_of_the_recording_gives()
    {
        AnswerWithTimedWords();
        var workflow = CreateWorkflow();
        var transcribed = await workflow.TranscribeRecordingAsync(_files, CancellationToken.None);
        var kept = await workflow.RebuildAsync(_files, answerPath: null, CancellationToken.None);
        File.Delete(_files.Segment(1));
        File.Delete(transcribed.TranscriptPath);

        var rebuilt = await workflow.RebuildAsync(_files, answerPath: null, CancellationToken.None);

        Assert.That(rebuilt, Is.EqualTo(_files.Transcript(3)));
        Assert.That(File.ReadAllText(kept), Does.Contain("Recorded 15 October 2026, 20:55 UTC."));
        Assert.That(File.ReadAllBytes(rebuilt), Is.EqualTo(File.ReadAllBytes(kept)));
    }

    /// <summary>
    /// With nothing of a recording left but ElevenLabs' answer, a rebuild cannot tell it from a file transcribed on its
    /// own, whose name gives the time it was transcribed rather than recorded, so it gives no recording time at all.
    /// </summary>
    [Test]
    public async Task A_rebuild_of_an_answer_with_nothing_else_of_its_recording_left_gives_no_recording_time()
    {
        File.Delete(_files.Segment(1));
        File.WriteAllText(_files.Json(), Answer);

        var rebuilt = await CreateWorkflow().RebuildAsync(_files, answerPath: null, CancellationToken.None);

        Assert.That(File.ReadAllText(rebuilt), Is.EqualTo($"# Kalam{Blank}[00:00:00] Hello world.{Blank}[00:00:04] Goodbye.{Environment.NewLine}"));
    }

    /// <summary>
    /// A rebuild lays the answer out with the marks as they are now, such as a label typed in after the class, and
    /// with the Transcript settings as they are now; the earlier transcript stays as it was.
    /// </summary>
    [Test]
    public async Task A_rebuild_uses_the_marks_and_the_settings_as_they_are_now()
    {
        File.WriteAllLines(_files.Marks, ["00:00:02.000"]);
        AnswerWithTimedWords();
        var transcript = TestOptions.Create(new TranscriptOptions());
        var workflow = CreateWorkflow(transcript: transcript);
        var transcribed = await workflow.TranscribeRecordingAsync(_files, CancellationToken.None);
        var before = File.ReadAllBytes(transcribed.TranscriptPath);
        File.WriteAllLines(_files.Marks, ["00:00:02.000 Ashʿarī on atoms"]);
        transcript.Set(new TranscriptOptions { IncludeTimestamps = false });

        var rebuilt = await workflow.RebuildAsync(_files, answerPath: null, CancellationToken.None);

        Assert.That(File.ReadAllText(rebuilt), Is.EqualTo($"# Kalam{Blank}Recorded 15 October 2026, 20:55 UTC.{Blank}Hello world.{Blank}## Mark 1: Ashʿarī on atoms{Blank}Goodbye.{Environment.NewLine}"));
        Assert.That(File.ReadAllBytes(transcribed.TranscriptPath), Is.EqualTo(before));
    }

    /// <summary>
    /// A rebuild needs no API key and makes no request: nothing is prepared and nothing goes to ElevenLabs. It writes
    /// the transcript alone, without a new answer beside it, at the first copy that has neither file: here the answer,
    /// whose own transcript is missing, keeps copy 1 to itself.
    /// </summary>
    [Test]
    public async Task A_rebuild_needs_no_key_and_sends_nothing()
    {
        File.WriteAllText(_files.Json(), Answer);

        var rebuilt = await CreateWorkflow(apiKey: "").RebuildAsync(_files, answerPath: null, CancellationToken.None);

        Assert.That(rebuilt, Is.EqualTo(_files.Transcript(2)));
        Assert.That(File.ReadAllText(rebuilt), Does.Contain("[00:00:00] Hello world."));
        _preparer.Verify(Calls.AnyPreparation, Times.Never);
        _client.Verify(Calls.AnyTranscription, Times.Never);
        Assert.That(Directory.GetFiles(_files.Folder, "*.json"), Is.EqualTo(new[] { _files.Json() }), "no new answer");
    }

    /// <summary>A rebuild takes the newest saved answer, never an answer kept as unread, however new.</summary>
    [Test]
    public async Task A_rebuild_takes_the_newest_answer_and_never_an_unread_one()
    {
        File.WriteAllText(_files.Json(1), """{"text":"First answer."}""");
        File.WriteAllText(_files.Json(2), """{"text":"Second answer."}""");
        File.WriteAllText(_files.UnreadAnswer(1), """{"text":"Unread answer."}""");

        var rebuilt = await CreateWorkflow().RebuildAsync(_files, answerPath: null, CancellationToken.None);

        Assert.That(File.ReadAllText(rebuilt), Does.Contain("Second answer.").And.Not.Contain("Unread"));
    }

    /// <summary>A rebuild from an answer named by hand uses that answer, even one kept as unread because it could not be saved in its place.</summary>
    [TestCase(false)]
    [TestCase(true)]
    public async Task A_rebuild_from_an_answer_named_by_hand_uses_that_answer(bool unread)
    {
        File.WriteAllText(_files.Json(1), """{"text":"First answer."}""");
        File.WriteAllText(_files.Json(2), """{"text":"Second answer."}""");
        File.WriteAllText(_files.UnreadAnswer(1), """{"text":"Unread answer."}""");

        var rebuilt = await CreateWorkflow().RebuildAsync(_files, unread ? _files.UnreadAnswer(1) : _files.Json(1), CancellationToken.None);

        Assert.That(File.ReadAllText(rebuilt), Does.Contain(unread ? "Unread answer." : "First answer."));
    }

    /// <summary>
    /// A rebuild writes the next free copy and never overwrites one: the transcripts there, and a file made by hand at the
    /// next number, stay as they were.
    /// </summary>
    [Test]
    public async Task A_rebuild_writes_the_next_free_copy_and_never_overwrites_one()
    {
        File.WriteAllText(_files.Json(1), Answer);
        File.WriteAllText(_files.Transcript(1), "first");
        File.WriteAllText(_files.Transcript(2), "made by hand");

        var rebuilt = await CreateWorkflow().RebuildAsync(_files, answerPath: null, CancellationToken.None);

        Assert.That(rebuilt, Is.EqualTo(_files.Transcript(3)));
        Assert.That(File.ReadAllText(_files.Transcript(1)), Is.EqualTo("first"));
        Assert.That(File.ReadAllText(_files.Transcript(2)), Is.EqualTo("made by hand"));
    }

    /// <summary>A rebuild says what it was made from and where it went, and that nothing was uploaded.</summary>
    [Test]
    public async Task A_rebuild_says_what_it_was_made_from_and_where_it_went()
    {
        var log = new RecordingLogger<TranscriptionWorkflow>();
        File.WriteAllText(_files.Json(1), Answer);
        File.WriteAllText(_files.Transcript(1), "first");

        await CreateWorkflow(logger: log).RebuildAsync(_files, answerPath: null, CancellationToken.None);

        Assert.That(log.Messages, Has.Some.EqualTo("Rebuilt from Kalam_2026-10-15_20-55-27Z.json into Kalam_2026-10-15_20-55-27Z (2).md; nothing was uploaded"));
    }

    /// <summary>A recording without a saved answer cannot be rebuilt: the error says to transcribe it first, and nothing is written.</summary>
    [Test]
    public void A_rebuild_without_a_saved_answer_says_to_transcribe_it_first()
    {
        File.WriteAllText(_files.UnreadAnswer(1), Answer);
        var workflow = CreateWorkflow();

        var error = Assert.ThrowsAsync<FileNotFoundException>(async () => await workflow.RebuildAsync(_files, answerPath: null, CancellationToken.None));

        Assert.That(error!.Message, Is.EqualTo("This recording has no saved answer from ElevenLabs yet; transcribe it first."));
        Assert.That(Directory.GetFiles(_files.Folder, "*.md"), Is.Empty);
    }

    /// <summary>A saved answer that cannot be read is refused with the reason, the log names it, and nothing is written.</summary>
    [Test]
    public void A_rebuild_from_an_answer_that_cannot_be_read_is_refused_and_writes_nothing()
    {
        var log = new RecordingLogger<TranscriptionWorkflow>();
        File.WriteAllText(_files.Json(1), "not an answer");
        var workflow = CreateWorkflow(logger: log);

        Assert.ThrowsAsync<TranscriptionException>(async () => await workflow.RebuildAsync(_files, answerPath: null, CancellationToken.None));

        Assert.That(log.Problems, Has.Some.Contains("Kalam_2026-10-15_20-55-27Z.json"));
        Assert.That(Directory.GetFiles(_files.Folder, "*.md"), Is.Empty);
    }

    /// <summary>
    /// The notes in a recording's marks file reach its transcript as headings without numbers, among the numbered marks, and
    /// a recording that was continued says under its title in how many parts it was made. A rebuild lays it out the same
    /// way, byte for byte.
    /// </summary>
    [Test]
    public async Task Notes_and_parts_reach_the_transcript_and_its_rebuild()
    {
        File.WriteAllLines(_files.Marks, ["00:00:02.000", "00:00:03.000 ~ Resumed at 21:47 UTC, about 4 minutes not recorded", "00:00:04.200"]);
        AnswerWithTimedWords();
        var workflow = CreateWorkflow();

        var transcribed = await workflow.TranscribeRecordingAsync(_files, CancellationToken.None);
        var rebuilt = await workflow.RebuildAsync(_files, answerPath: null, CancellationToken.None);

        Assert.That(File.ReadAllText(transcribed.TranscriptPath), Is.EqualTo(
            $"# Kalam{Blank}Recorded 15 October 2026, 20:55 UTC, in 2 parts.{Blank}[00:00:00] Hello world.{Blank}## [00:00:02] Mark 1{Blank}" +
            $"## [00:00:03] Resumed at 21:47 UTC, about 4 minutes not recorded{Blank}[00:00:04] Goodbye.{Blank}## [00:00:04] Mark 2{Environment.NewLine}"));
        Assert.That(File.ReadAllBytes(rebuilt), Is.EqualTo(File.ReadAllBytes(transcribed.TranscriptPath)));
    }

    /// <summary>
    /// A transcript of a recording made in parts still tells a later rebuild that it is a recording's: with its audio and
    /// its marks deleted, the rebuild keeps the time the recording started, although it can no longer count the parts.
    /// </summary>
    [Test]
    public async Task A_transcript_of_a_recording_made_in_parts_still_gives_a_rebuild_its_recording_time()
    {
        File.WriteAllLines(_files.Marks, ["00:00:03.000 ~ Resumed at 21:47 UTC, about 4 minutes not recorded"]);
        AnswerWithTimedWords();
        var workflow = CreateWorkflow();
        var transcribed = await workflow.TranscribeRecordingAsync(_files, CancellationToken.None);
        File.Delete(_files.Segment(1));
        File.Delete(_files.Marks);

        var rebuilt = await workflow.RebuildAsync(_files, answerPath: null, CancellationToken.None);

        Assert.That(File.ReadAllText(transcribed.TranscriptPath), Does.Contain($"Recorded 15 October 2026, 20:55 UTC, in 2 parts.{Blank}"));
        Assert.That(File.ReadAllText(rebuilt), Does.StartWith($"# Kalam{Blank}Recorded 15 October 2026, 20:55 UTC.{Blank}"));
    }

    /// <summary>
    /// The multipart report's alignment check, across a continue: a class recorded first at 44.1 kHz, then at 48 kHz after a
    /// device switch, is continued later from its length on disk, and the continued run rolls over into another part. A
    /// tone that starts at a mark set in the continued run starts at that mark's position in the 16 kHz upload, within a
    /// millisecond, so that the mark's heading lands where the tone is in the transcript.
    /// </summary>
    [Test]
    public async Task A_tone_at_a_mark_set_after_a_continue_starts_at_the_marks_position_in_the_upload()
    {
        File.Delete(_files.Segment(1));
        TestAudio.WriteWav(_files.Segment(1), 44100, new short[103_441]);   // the first run, until a device switch
        TestAudio.WriteWav(_files.Segment(2), 48000, new short[59_256]);    // the first run, after it
        var folders = new ClassFolders(TestOptions.Create(new RecordingOptions { OutputFolder = _recordings }), ZonedClock.Eastern(new ManualTimeProvider(Started)), NullLogger<ClassFolders>.Instance);
        var marks = new ClassMarks(NullLogger<ClassMarks>.Instance);

        var files = folders.Open(_files.Folder, _files.BaseName);
        var offset = folders.LengthOnDisk(files);
        marks.AddResume(files, offset, Started.AddMinutes(50), TimeSpan.FromMinutes(4));
        TestAudio.WriteWav(files.Segment(3), 48000, new short[36_000]);   // the continued run, until a rollover
        var toneAt = offset + TimeSpan.FromSeconds(36_000 / 48000.0) + TimeSpan.FromSeconds(14_400 / 48000.0);
        marks.Add(files, toneAt);
        TestAudio.WriteWav(files.Segment(4), 48000, [.. new short[14_400], .. TestAudio.Tone(48000, seconds: 0.5, frequency: 1000, amplitude: 0.5)]);

        short[] uploaded = [];
        _client.Setup(Calls.AnyTranscription).ReturnsAsync((string path, IReadOnlyList<string> _, CancellationToken _) =>
        {
            uploaded = TestAudio.ReadWav(path).Samples;
            return Mocks.Transcript;
        });

        await CreateWorkflow(preparer: RealPreparer()).TranscribeRecordingAsync(files, CancellationToken.None);

        var mark = marks.Read(files).Single(m => !m.IsNote);
        var onset = Array.FindIndex(uploaded, sample => Math.Abs((int)sample) > short.MaxValue / 4);
        Assert.That(offset.TotalSeconds, Is.EqualTo(103_441 / 44100.0 + 59_256 / 48000.0).Within(1e-6));
        Assert.That(onset / (double)AudioPreparer.TargetSampleRate, Is.EqualTo(mark.Position.TotalSeconds).Within(0.001));
    }

    /// <summary>
    /// A file transcribed with a recordings folder given for it (--output) gets the folder of its transcript under that
    /// folder rather than under Recording:OutputFolder; the file itself stays where it is.
    /// </summary>
    [Test]
    public async Task A_file_transcribed_with_a_recordings_folder_of_its_own_gets_its_folder_there()
    {
        var video = _folder.File("class.mp4");
        File.WriteAllBytes(video, [1, 2, 3]);
        var elsewhere = _folder.File("Elsewhere");

        var files = await CreateWorkflow().TranscribeFileAsync(video, elsewhere, CancellationToken.None);

        Assert.That(files.TranscriptPath, Is.EqualTo(Path.Combine(elsewhere, "class", "2026-10-15", "class_2026-10-15_20-55-27Z.md")));
        Assert.That(File.Exists(video), Is.True);
        Assert.That(Directory.Exists(Path.Combine(_recordings, "class")), Is.False);
    }
}
