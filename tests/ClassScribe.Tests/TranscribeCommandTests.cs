using ClassScribe.Audio;
using ClassScribe.Commands;
using ClassScribe.Desktop;
using ClassScribe.Settings;
using ClassScribe.Storage;
using ClassScribe.Transcription;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ClassScribe.Tests;

/// <summary>
/// The transcribe command's choice of what to send, and what it refuses to send: a recording still being written, and,
/// on the command line, a recording that would be billed a second time without --again. It keeps the laptop awake while
/// it uploads. The preparer and the service are mocked, and stand-ins keep the laptop awake.
/// </summary>
[TestFixture]
public class TranscribeCommandTests
{
    /// <summary>The laptop awake and ClassScribe running, as every upload needs.</summary>
    private const PowerNeeds Awake = PowerNeeds.System | PowerNeeds.Execution;

    private TempFolder _folder = null!;
    private Mock<IAudioPreparer> _preparer = null!;
    private Mock<ITranscriptionClient> _client = null!;
    private Mock<IDesktop> _desktop = null!;
    private FakeUserSignals _signals = null!;
    private FakePowerRequests _power = null!;
    private FakePowerSource _powerSource = null!;
    private FakeElevenLabsAccount _account = null!;
    private RecordingLogger<UsageReport> _usageLog = null!;
    private RecordingLogger<TranscribeCommand> _log = null!;

    /// <summary>Creates a temporary folder, the mocks, the fake reader of the account, and the stand-ins for keeping the laptop awake.</summary>
    [SetUp]
    public void SetUp()
    {
        _folder = new TempFolder();
        _preparer = Mocks.AudioPreparer();
        _client = Mocks.TranscriptionClient();
        _desktop = new Mock<IDesktop>();
        _signals = new FakeUserSignals();
        _power = new FakePowerRequests();
        _powerSource = new FakePowerSource();
        _account = new FakeElevenLabsAccount();
        _usageLog = new RecordingLogger<UsageReport>();
        _log = new RecordingLogger<TranscribeCommand>();
    }

    /// <summary>Deletes the temporary folder. That fails if a test left a file open, which is the point.</summary>
    [TearDown]
    public void TearDown() => _folder.Dispose();

    /// <summary>
    /// Builds the command with the real workflow around the mocks, with its recordings folder inside the temporary folder
    /// and local time four hours behind UTC, as US Eastern time is in October.
    /// </summary>
    private TranscribeCommand CreateCommand(bool openWhenDone = false, string apiKey = "dummy-key-for-a-local-test", KeepScreenOn keepScreenOn = KeepScreenOn.OnBattery)
    {
        var recording = TestOptions.Create(new RecordingOptions
        {
            OutputFolder = _folder.File("Recordings"),
            OpenFolderWhenDone = openWhenDone,
            OpenTranscriptWhenDone = openWhenDone,
            KeepScreenOn = keepScreenOn
        });
        var elevenLabs = TestOptions.Create(new ElevenLabsOptions { ApiKey = apiKey, KeytermsFile = _folder.File("no-glossary.txt") });
        var folders = new ClassFolders(recording, TimeProvider.System, NullLogger<ClassFolders>.Instance);
        var usage = new UsageReport(_account, ZonedTimeProvider.EasternSummer, _usageLog);
        var workflow = new TranscriptionWorkflow(
            _preparer.Object,
            _client.Object,
            new KeytermLoader(NullLogger<KeytermLoader>.Instance),
            new TranscriptFormatter(TestOptions.Create(new TranscriptOptions())),
            folders,
            new ClassMarks(NullLogger<ClassMarks>.Instance),
            elevenLabs,
            recording,
            new TranscriptionProgress(),
            usage,
            NullLogger<TranscriptionWorkflow>.Instance);

        return new TranscribeCommand(workflow, usage, folders, _signals, _desktop.Object, _power, _powerSource, recording, elevenLabs, ZonedTimeProvider.EasternSummer, _log);
    }

    /// <summary>
    /// What the transcript cost the account is the command's last line: read once the transcript is saved and opened, from
    /// the read made as the upload began, for a recording and for a file alike.
    /// </summary>
    [Test]
    public async Task The_accounts_credits_are_reported_after_the_transcript_is_saved_and_opened()
    {
        var recording = WriteRecording("Kalam", new DateTimeOffset(2026, 10, 14, 19, 30, 0, TimeSpan.Zero));
        var video = _folder.File("class.mp4");
        File.WriteAllBytes(video, [1, 2, 3]);
        _account.Answer(FakeElevenLabsAccount.Read(0), FakeElevenLabsAccount.Read(500), FakeElevenLabsAccount.Read(500), FakeElevenLabsAccount.Read(1200));
        List<(int Read, int Saved, int Opened)> atEachRead = [];
        _account.OnRead = number => atEachRead.Add((number, _log.Messages.Count(message => message.StartsWith("Transcript saved to", StringComparison.Ordinal)), _desktop.Invocations.Count));
        var command = CreateCommand(openWhenDone: true);

        var first = await command.RunAsync(new TranscribeRequest(recording.Segment(1)));
        var second = await command.RunAsync(new TranscribeRequest(video));

        Assert.That((first, second), Is.EqualTo((0, 0)));
        Assert.That(atEachRead.Select(read => read.Read), Is.EqualTo([1, 2, 3, 4]));
        Assert.That(atEachRead[1].Saved, Is.EqualTo(1), "the recording's transcript was saved before its second read");
        Assert.That(atEachRead[1].Opened, Is.GreaterThan(0), "and opened");
        Assert.That(atEachRead[3].Saved, Is.EqualTo(2), "the file's transcript was saved before its second read");
        Assert.That(UsageLines(), Is.EqualTo([
            "ElevenLabs charged 500 credits for this transcript; 99,500 of 100,000 credits are left",
            "ElevenLabs charged 700 credits for this transcript; 98,800 of 100,000 credits are left"]));
    }

    /// <summary>A reader that fails does not change the exit code: the transcript is saved, and the line says the credits could not be read.</summary>
    [Test]
    public async Task A_failed_read_of_the_account_does_not_change_the_exit_code()
    {
        var recording = WriteRecording("Kalam", new DateTimeOffset(2026, 10, 14, 19, 30, 0, TimeSpan.Zero));
        _account.Failure = new InvalidOperationException("boom");

        var exitCode = await CreateCommand().RunAsync(new TranscribeRequest(recording.Segment(1)));

        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(File.Exists(recording.Transcript()), Is.True);
        Assert.That(UsageLines(), Is.EqualTo(["The account's credits could not be read (the request failed: boom); the transcript is not affected"]));
    }

    /// <summary>What the account's report said at Information, which the window shows; its Debug notes are left out.</summary>
    private IReadOnlyList<string> UsageLines() => [.. _usageLog.Entries.Where(entry => entry.Level == LogLevel.Information).Select(entry => entry.Message)];

    /// <summary>Writes a recording's segment files into the temporary folder and returns the recording.</summary>
    private RecordingFiles WriteRecording(string name, DateTimeOffset started, int segments = 1)
    {
        var files = RecordingFiles.ForNew(_folder.Path, name, started);
        for (var i = 1; i <= segments; i++) File.WriteAllBytes(files.Segment(i), [0]);
        return files;
    }

    /// <summary>Gives a recording a transcript and the answer it was made from, as a transcription that went well leaves them.</summary>
    private static void Transcribe(RecordingFiles files, int copy = 1)
    {
        File.WriteAllText(files.Json(copy), """{"text":"Hello world."}""");
        File.WriteAllText(files.Transcript(copy), "# Kalam");
    }

    /// <summary>Holds a segment open for writing while letting others read it, as the recorder holds the segment it writes.</summary>
    private static FileStream HoldForWriting(string segment) =>
        new(segment, FileMode.Open, FileAccess.Write, FileShare.Read);

    /// <summary>A folder is transcribed from the recording started last in it: its segments in order, and nothing else.</summary>
    [Test]
    public async Task A_folder_is_transcribed_from_its_newest_recording()
    {
        WriteRecording("Kalam", new DateTimeOffset(2026, 10, 11, 14, 5, 0, TimeSpan.Zero));
        var newest = WriteRecording("Kalam", new DateTimeOffset(2026, 10, 14, 19, 30, 0, TimeSpan.Zero), segments: 3);
        foreach (var stray in new[] { newest.Upload, _folder.File("notes.wav"), _folder.File("transcript.txt") })
            File.WriteAllBytes(stray, [0]);

        var exitCode = await CreateCommand().RunAsync(new TranscribeRequest(_folder.Path));

        Assert.That(exitCode, Is.EqualTo(0));
        _preparer.Verify(p => p.Prepare(new[] { newest.Segment(1), newest.Segment(2), newest.Segment(3) }, newest.Upload, It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>A segment file of a recording, dropped on the app for instance, means the whole recording, with its marks.</summary>
    [Test]
    public async Task A_segment_file_is_transcribed_as_its_whole_recording()
    {
        var recording = WriteRecording("Kalam", new DateTimeOffset(2026, 10, 14, 19, 30, 0, TimeSpan.Zero), segments: 2);
        File.WriteAllLines(recording.Marks, ["00:00:00.450"]);

        var exitCode = await CreateCommand().RunAsync(new TranscribeRequest(recording.Segment(2)));

        Assert.That(exitCode, Is.EqualTo(0));
        _preparer.Verify(p => p.Prepare(new[] { recording.Segment(1), recording.Segment(2) }, recording.Upload, It.IsAny<CancellationToken>()), Times.Once);
        Assert.That(File.ReadAllText(recording.Transcript()), Does.Contain("## [00:00:00] Mark 1"));
    }

    /// <summary>
    /// A recording whose class track was never written has only its .mic.wav files; given one of them, it is transcribed
    /// as the recording it is, its transcript beside it, rather than sent as a stray file into a folder of its own.
    /// </summary>
    [Test]
    public async Task A_microphone_file_of_a_recording_without_a_class_track_is_transcribed_as_the_recording()
    {
        var recording = WriteRecording("Kalam", new DateTimeOffset(2026, 10, 14, 19, 30, 0, TimeSpan.Zero));
        File.Move(recording.Segment(1), recording.Segment(1, Track.Microphone));

        var exitCode = await CreateCommand().RunAsync(new TranscribeRequest(recording.Segment(1, Track.Microphone)));

        Assert.That(exitCode, Is.EqualTo(0));
        _preparer.Verify(p => p.Prepare(new[] { recording.Segment(1, Track.Microphone) }, recording.Upload, It.IsAny<CancellationToken>()), Times.Once);
        Assert.That(File.Exists(recording.Transcript()), Is.True, "the transcript goes beside the recording");
    }

    /// <summary>A folder without any recording is an error, and nothing is sent.</summary>
    [Test]
    public async Task A_folder_without_recordings_is_an_error()
    {
        File.WriteAllBytes(_folder.File("notes.wav"), [0]);

        var exitCode = await CreateCommand().RunAsync(new TranscribeRequest(_folder.Path));

        Assert.That(exitCode, Is.EqualTo(1));
        _client.Verify(Calls.AnyTranscription, Times.Never);
    }

    /// <summary>A path that leads nowhere is an error that names it, and nothing is sent.</summary>
    [Test]
    public async Task A_path_that_leads_nowhere_is_an_error()
    {
        var missing = _folder.File("missing.mp4");

        Assert.That(await CreateCommand().RunAsync(new TranscribeRequest(missing)), Is.EqualTo(1));
        Assert.That(_log.Problems, Is.EqualTo(new[] { $"Nothing found at {missing}" }));
        _client.Verify(Calls.AnyTranscription, Times.Never);
    }

    /// <summary>A single file is sent on its own.</summary>
    [Test]
    public async Task A_single_file_is_sent_on_its_own()
    {
        var video = _folder.File("class.mp4");
        File.WriteAllBytes(video, [1, 2, 3]);

        var exitCode = await CreateCommand().RunAsync(new TranscribeRequest(video));

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

        await CreateCommand(openWhenDone: true).RunAsync(new TranscribeRequest(video));

        _desktop.Verify(d => d.OpenFolder(transcriptFolder), Times.Once);
        _desktop.Verify(d => d.OpenFile(It.Is<string>(p => p.StartsWith(transcriptFolder) && p.EndsWith(".md"))), Times.Once);
    }

    /// <summary>
    /// A file that is not a recording's, given with --output, has its transcript in a folder named after it inside the
    /// folder given, rather than under Recording:OutputFolder.
    /// </summary>
    [Test]
    public async Task A_single_file_goes_into_the_output_folder_given()
    {
        var video = _folder.File("class.mp4");
        File.WriteAllBytes(video, [1, 2, 3]);
        var elsewhere = _folder.File("Elsewhere");
        var transcriptFolder = Path.Combine(elsewhere, "class");

        Assert.That(await CreateCommand(openWhenDone: true).RunAsync(new TranscribeRequest(video, elsewhere, FromCommandLine: true)), Is.EqualTo(0));

        _client.Verify(Calls.TranscriptionOf(video), Times.Once);
        _desktop.Verify(d => d.OpenFolder(transcriptFolder), Times.Once);
        Assert.That(Directory.GetFiles(transcriptFolder, "*.md"), Has.Length.EqualTo(1));
        Assert.That(Directory.Exists(_folder.File("Recordings")), Is.False, "nothing went under Recording:OutputFolder");
        Assert.That(_log.Messages, Has.None.Contains("--output"), "the folder given was used, so there is nothing to say about it");
    }

    /// <summary>
    /// A recording, named by its folder or one of its files, keeps its transcript beside it when --output names another
    /// folder, since its audio, marks and earlier transcripts are there; ClassScribe says that --output was not used.
    /// </summary>
    [TestCase(true)]
    [TestCase(false)]
    public async Task A_recording_keeps_its_transcript_beside_it_and_says_output_was_not_used(bool byFolder)
    {
        var recording = WriteRecording("Kalam", new DateTimeOffset(2026, 10, 14, 19, 30, 0, TimeSpan.Zero));
        var elsewhere = _folder.File("Elsewhere");

        var exitCode = await CreateCommand().RunAsync(new TranscribeRequest(byFolder ? _folder.Path : recording.Segment(1), elsewhere, FromCommandLine: true));

        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(File.Exists(recording.Transcript()), Is.True);
        Assert.That(Directory.Exists(elsewhere), Is.False);
        Assert.That(_log.Messages, Has.Some.EqualTo($"--output is not used for a recording: its transcript goes beside it, in {_folder.Path}"));
    }

    /// <summary>
    /// With --no-open, nothing is opened when the transcript is ready, whatever the settings say: neither the folder nor
    /// the transcript, for a file sent on its own or for a recording.
    /// </summary>
    [Test]
    public async Task No_open_opens_nothing()
    {
        var video = _folder.File("class.mp4");
        File.WriteAllBytes(video, [1, 2, 3]);
        var recording = WriteRecording("Kalam", new DateTimeOffset(2026, 10, 14, 19, 30, 0, TimeSpan.Zero));

        Assert.That(await CreateCommand(openWhenDone: true).RunAsync(new TranscribeRequest(video, Open: false, FromCommandLine: true)), Is.EqualTo(0));
        Assert.That(await CreateCommand(openWhenDone: true).RunAsync(new TranscribeRequest(recording.Segment(1), Open: false, FromCommandLine: true)), Is.EqualTo(0));

        Assert.That(File.Exists(recording.Transcript()), Is.True);
        _desktop.VerifyNoOtherCalls();
    }

    /// <summary>
    /// Without a key the command stops before any work: a long recording is not prepared only to fail at
    /// the upload, and the error says where the key belongs.
    /// </summary>
    [Test]
    public async Task Without_a_key_nothing_is_prepared_or_sent()
    {
        var recording = WriteRecording("Kalam", new DateTimeOffset(2026, 10, 14, 19, 30, 0, TimeSpan.Zero));

        var exitCode = await CreateCommand(apiKey: " ").RunAsync(new TranscribeRequest(recording.Segment(1)));

        Assert.That(exitCode, Is.EqualTo(1));
        _preparer.Verify(Calls.AnyPreparation, Times.Never);
        _client.Verify(Calls.AnyTranscription, Times.Never);
        Assert.That(_log.Problems, Has.Some.StartsWith("No ElevenLabs API key found").And.Contain(ElevenLabsOptions.ApiKeyVariable)
            .And.Contain("quit every VS Code window"));
    }

    /// <summary>Nothing is recording here, so the run counts as stopped from the start, and the first Ctrl+C cancels the upload.</summary>
    [Test]
    public async Task The_run_counts_as_stopped_from_the_start()
    {
        var video = _folder.File("class.mp4");
        File.WriteAllBytes(video, [1, 2, 3]);

        await CreateCommand().RunAsync(new TranscribeRequest(video));

        Assert.That(_signals.StopRequested.IsCompleted, Is.True);
    }

    /// <summary>
    /// On the command line, a recording that already has a transcript is not sent again without --again, since that is
    /// billed again: the command exits with 1 before anything is prepared, names the newest transcript, and prints the two
    /// ways on, each as a command that runs as printed: the free rebuild first, then the same command with --again.
    /// </summary>
    [Test]
    public async Task On_the_command_line_a_transcribed_recording_needs_again_and_rebuild_is_offered()
    {
        var recording = WriteRecording("Kalam", new DateTimeOffset(2026, 10, 14, 19, 30, 0, TimeSpan.Zero), segments: 2);
        Transcribe(recording);
        File.WriteAllText(recording.Transcript(2), "# Kalam, rebuilt");

        var exitCode = await CreateCommand().RunAsync(new TranscribeRequest(recording.Segment(2), FromCommandLine: true));

        Assert.That(exitCode, Is.EqualTo(1));
        _preparer.Verify(Calls.AnyPreparation, Times.Never);
        _client.Verify(Calls.AnyTranscription, Times.Never);
        Assert.That(_log.Problems, Is.EqualTo(new[]
        {
            "Kalam_2026-10-14_19-30-00Z already has a transcript (Kalam_2026-10-14_19-30-00Z (2).md). Transcribing it again is billed again, so nothing was sent"
        }));
        Assert.That(_log.Messages.TakeLast(2), Is.EqualTo(new[]
        {
            $"To lay it out again for free, from ElevenLabs' saved answer, run: {CommandLine.Rebuild(recording.Segment(1))}",
            $"To transcribe it again all the same, run: {CommandLine.Transcribe(recording.Segment(1), again: true)}"
        }));
    }

    /// <summary>
    /// A transcribed recording whose answer from ElevenLabs is no longer there, such as one deleted by hand, is refused
    /// all the same, but without the offer to rebuild, which would find nothing to lay out.
    /// </summary>
    [Test]
    public async Task Without_a_saved_answer_the_refusal_does_not_offer_a_rebuild()
    {
        var recording = WriteRecording("Kalam", new DateTimeOffset(2026, 10, 14, 19, 30, 0, TimeSpan.Zero));
        File.WriteAllText(recording.Transcript(), "# Kalam");

        Assert.That(await CreateCommand().RunAsync(new TranscribeRequest(recording.Segment(1), FromCommandLine: true)), Is.EqualTo(1));

        _client.Verify(Calls.AnyTranscription, Times.Never);
        Assert.That(_log.Messages, Has.None.Contains(" rebuild "));
        Assert.That(_log.Messages[^1], Is.EqualTo($"To transcribe it again all the same, run: {CommandLine.Transcribe(recording.Segment(1), again: true)}"));
    }

    /// <summary>
    /// A transcribed recording whose saved answer is no longer there, but whose answer to another try was paid for and kept
    /// apart, as after a rebuild laid such an answer out, is refused with the free way on from that kept answer, as menu
    /// choice 3 offers it, and then the command with --again.
    /// </summary>
    [Test]
    public async Task Without_a_saved_answer_the_refusal_offers_to_lay_out_a_kept_answer()
    {
        var recording = WriteRecording("Kalam", new DateTimeOffset(2026, 10, 14, 19, 30, 0, TimeSpan.Zero));
        File.WriteAllText(recording.Transcript(), "# Kalam");
        File.WriteAllText(recording.UnreadAnswer(), """{"text":"Hello world."}""");

        Assert.That(await CreateCommand().RunAsync(new TranscribeRequest(recording.Segment(1), FromCommandLine: true)), Is.EqualTo(1));

        _client.Verify(Calls.AnyTranscription, Times.Never);
        Assert.That(_log.Messages.TakeLast(2), Is.EqualTo(new[]
        {
            $"To make a new transcript for free from ElevenLabs' answer kept in {recording.UnreadAnswer()}, run: {CommandLine.Rebuild(recording.UnreadAnswer())}",
            $"To transcribe it again all the same, run: {CommandLine.Transcribe(recording.Segment(1), again: true)}"
        }));
    }

    /// <summary>The refusal holds for a folder whose newest recording has a transcript, and for a file dropped on the app.</summary>
    [Test]
    public async Task On_the_command_line_a_folder_whose_newest_recording_has_a_transcript_needs_again()
    {
        var newest = WriteRecording("Kalam", new DateTimeOffset(2026, 10, 14, 19, 30, 0, TimeSpan.Zero));
        Transcribe(newest);

        Assert.That(await CreateCommand().RunAsync(new TranscribeRequest(_folder.Path, FromCommandLine: true)), Is.EqualTo(1));
        _client.Verify(Calls.AnyTranscription, Times.Never);
        Assert.That(_log.Problems, Has.Some.StartsWith("Kalam_2026-10-14_19-30-00Z already has a transcript"));
    }

    /// <summary>With --again, a recording that already has a transcript is transcribed again, as a new copy beside the first.</summary>
    [Test]
    public async Task With_again_a_transcribed_recording_is_transcribed_again()
    {
        var recording = WriteRecording("Kalam", new DateTimeOffset(2026, 10, 14, 19, 30, 0, TimeSpan.Zero));
        Transcribe(recording);

        var exitCode = await CreateCommand().RunAsync(new TranscribeRequest(recording.Segment(1), Again: true, FromCommandLine: true));

        Assert.That(exitCode, Is.EqualTo(0));
        _client.Verify(Calls.AnyTranscription, Times.Once);
        Assert.That(File.ReadAllText(recording.Transcript()), Is.EqualTo("# Kalam"), "the first transcript stays as it was");
        Assert.That(File.Exists(recording.Transcript(2)), Is.True);
    }

    /// <summary>From the menu, which asks before it transcribes a recording again, no --again is needed.</summary>
    [Test]
    public async Task From_the_menu_a_transcribed_recording_is_transcribed_without_again()
    {
        var recording = WriteRecording("Kalam", new DateTimeOffset(2026, 10, 14, 19, 30, 0, TimeSpan.Zero));
        Transcribe(recording);

        Assert.That(await CreateCommand().RunAsync(new TranscribeRequest(recording.Segment(1))), Is.EqualTo(0));
        _client.Verify(Calls.AnyTranscription, Times.Once);
    }

    /// <summary>
    /// On the command line, a recording without a transcript whose answer from an earlier try was paid for and kept is not
    /// sent again without --again either. The refusal says where the answer is kept, offers to lay that answer out for
    /// free, and gives the command with --again.
    /// </summary>
    [Test]
    public async Task On_the_command_line_a_recording_with_a_kept_answer_needs_again()
    {
        var recording = WriteRecording("Kalam", new DateTimeOffset(2026, 10, 14, 19, 30, 0, TimeSpan.Zero));
        File.WriteAllText(recording.UnreadAnswer(), """{"text":"Hello world."}""");

        var exitCode = await CreateCommand().RunAsync(new TranscribeRequest(recording.Segment(1), FromCommandLine: true));

        Assert.That(exitCode, Is.EqualTo(1));
        _client.Verify(Calls.AnyTranscription, Times.Never);
        Assert.That(_log.Problems, Is.EqualTo(new[]
        {
            $"ElevenLabs' answer to an earlier try at Kalam_2026-10-14_19-30-00Z, which has been paid for, is kept in {recording.UnreadAnswer()}. Transcribing it again is billed again, so nothing was sent"
        }));
        Assert.That(_log.Messages.TakeLast(2), Is.EqualTo(new[]
        {
            $"To lay that answer out as a transcript for free, run: {CommandLine.Rebuild(recording.UnreadAnswer())}",
            $"To transcribe it again all the same, run: {CommandLine.Transcribe(recording.Segment(1), again: true)}"
        }));
    }

    /// <summary>
    /// The refusal for --again comes before the check for a key, since the free rebuild it offers needs no key: without
    /// one, a transcribed recording still gets the advice rather than the error about the key.
    /// </summary>
    [Test]
    public async Task The_again_refusal_comes_before_the_key_check()
    {
        var recording = WriteRecording("Kalam", new DateTimeOffset(2026, 10, 14, 19, 30, 0, TimeSpan.Zero));
        Transcribe(recording);

        Assert.That(await CreateCommand(apiKey: "").RunAsync(new TranscribeRequest(recording.Segment(1), FromCommandLine: true)), Is.EqualTo(1));
        Assert.That(_log.Problems, Has.One.Contains("already has a transcript").And.None.Contains("API key"));
    }

    /// <summary>
    /// A recording whose newest segment a recorder holds open for writing is refused, wherever the request comes from: it
    /// is still being recorded, and what was sent now would stop short. Nothing is prepared or sent.
    /// </summary>
    [TestCase(false, false)]
    [TestCase(false, true)]
    [TestCase(true, true)]
    public async Task A_recording_being_written_is_refused(bool byFolder, bool fromCommandLine)
    {
        WriteRecording("Kalam", new DateTimeOffset(2026, 10, 11, 14, 5, 0, TimeSpan.Zero));
        var recordingNow = WriteRecording("Kalam", new DateTimeOffset(2026, 10, 14, 19, 30, 0, TimeSpan.Zero), segments: 2);

        int exitCode;
        using (HoldForWriting(recordingNow.Segment(2)))
            exitCode = await CreateCommand().RunAsync(new TranscribeRequest(byFolder ? _folder.Path : recordingNow.Segment(1), FromCommandLine: fromCommandLine));

        Assert.That(exitCode, Is.EqualTo(1));
        _preparer.Verify(Calls.AnyPreparation, Times.Never);
        _client.Verify(Calls.AnyTranscription, Times.Never);
        Assert.That(_log.Problems, Is.EqualTo(new[] { "Kalam_2026-10-14_19-30-00Z is being recorded now; transcribe it once it has stopped" }));
    }

    /// <summary>
    /// A folder's other recordings without a transcript are named, with their length, when they started in local time, and
    /// the command that transcribes each, before the upload and again after it, so that one left out is not missed.
    /// Recordings with a transcript, and the one transcribed, are not named.
    /// </summary>
    [Test]
    public async Task The_other_recordings_in_the_folder_without_a_transcript_are_named_before_and_after_the_upload()
    {
        var withTranscript = RecordingFiles.ForNew(_folder.Path, "Kalam", new DateTimeOffset(2026, 10, 7, 23, 0, 0, TimeSpan.Zero));
        TestAudio.WriteWav(withTranscript.Segment(1), 10, new short[10]);
        Transcribe(withTranscript);
        var leftOut = RecordingFiles.ForNew(_folder.Path, "Kalam", new DateTimeOffset(2026, 10, 14, 23, 0, 5, TimeSpan.Zero));
        TestAudio.WriteWav(leftOut.Segment(1), 10, new short[(47 * 60 + 10) * 10]);
        var newest = WriteRecording("Kalam", new DateTimeOffset(2026, 10, 15, 0, 1, 0, TimeSpan.Zero));
        IReadOnlyList<string> loggedBeforeTheUpload = [];
        _client.Setup(Calls.AnyTranscription).Callback(() => loggedBeforeTheUpload = _log.Messages).ReturnsAsync(Mocks.Transcript);
        var named = $"Also in this folder, without a transcript: Kalam_2026-10-14_23-00-05Z (00:47:10, 14 Oct 19:00 local). To transcribe it: {CommandLine.Transcribe(leftOut.Segment(1))}";

        var exitCode = await CreateCommand().RunAsync(new TranscribeRequest(_folder.Path, FromCommandLine: true));

        Assert.That(exitCode, Is.EqualTo(0));
        _client.Verify(Calls.AnyTranscription, Times.Once);
        Assert.That(File.Exists(newest.Transcript()), Is.True, "the newest recording was the one transcribed");
        Assert.That(loggedBeforeTheUpload, Has.One.EqualTo(named), "named before the upload");
        Assert.That(_log.Messages, Has.Exactly(2).EqualTo(named), "and again after it");
        Assert.That(_log.Messages[^1], Is.EqualTo(named), "last, where it is seen");
        Assert.That(_log.Messages, Has.None.Contains(withTranscript.BaseName).And.None.Contains($"transcript: {newest.BaseName}"));
    }

    /// <summary>
    /// When the folder's newest recording is refused, the others without a transcript are still named, with their commands:
    /// they are likely what was meant, as when the newest is the class being recorded now.
    /// </summary>
    [Test]
    public async Task The_other_recordings_are_named_when_the_newest_is_refused()
    {
        var leftOut = WriteRecording("Kalam", new DateTimeOffset(2026, 10, 7, 23, 0, 0, TimeSpan.Zero));
        var recordingNow = WriteRecording("Kalam", new DateTimeOffset(2026, 10, 14, 23, 0, 5, TimeSpan.Zero));

        using (HoldForWriting(recordingNow.Segment(1)))
            Assert.That(await CreateCommand().RunAsync(new TranscribeRequest(_folder.Path, FromCommandLine: true)), Is.EqualTo(1));

        Assert.That(_log.Messages, Has.One.EqualTo($"Also in this folder, without a transcript: {leftOut.BaseName} (00:00:00, 7 Oct 19:00 local). To transcribe it: {CommandLine.Transcribe(leftOut.Segment(1))}"));
    }

    /// <summary>
    /// A left-out recording whose answer from an earlier try was paid for and kept is named with that answer and the
    /// command that lays it out for free, rather than one that would bill it again, which the command line would refuse.
    /// </summary>
    [Test]
    public async Task A_left_out_recording_with_a_kept_answer_is_named_with_the_free_rebuild()
    {
        var kept = WriteRecording("Kalam", new DateTimeOffset(2026, 10, 7, 23, 0, 0, TimeSpan.Zero));
        File.WriteAllText(kept.UnreadAnswer(), """{"text":"Hello world."}""");
        WriteRecording("Kalam", new DateTimeOffset(2026, 10, 14, 23, 0, 5, TimeSpan.Zero));

        Assert.That(await CreateCommand().RunAsync(new TranscribeRequest(_folder.Path, FromCommandLine: true)), Is.EqualTo(0));

        Assert.That(_log.Messages, Has.Exactly(2).EqualTo(
            $"Also in this folder, without a transcript: {kept.BaseName} (00:00:00, 7 Oct 19:00 local). ElevenLabs' answer to an earlier try, which has been paid for, is kept in {kept.UnreadAnswer()}. To lay it out as a transcript for free: {CommandLine.Rebuild(kept.UnreadAnswer())}"));
        Assert.That(_log.Messages, Has.None.Contains(CommandLine.Transcribe(kept.Segment(1))));
    }

    /// <summary>
    /// The laptop is kept awake while a recording is transcribed, with the reason powercfg /requests shows: the hold is
    /// taken by the time the upload starts and let go once, after the transcript is saved. A laptop that slept during a
    /// long upload would drop it, perhaps after ElevenLabs had charged for an answer that then never arrives.
    /// </summary>
    [Test]
    public async Task The_laptop_is_kept_awake_while_a_recording_is_transcribed()
    {
        var recording = WriteRecording("Kalam", new DateTimeOffset(2026, 10, 14, 19, 30, 0, TimeSpan.Zero));
        bool? heldDuringUpload = null;
        bool? savedBeforeRelease = null;
        _client.Setup(Calls.AnyTranscription).Returns((string _, IReadOnlyList<string> _, CancellationToken _) =>
        {
            heldDuringUpload = _power.Holding;
            return Task.FromResult(Mocks.Transcript);
        });
        _power.OnRelease = () => savedBeforeRelease = File.Exists(recording.Transcript());

        Assert.That(await CreateCommand().RunAsync(new TranscribeRequest(recording.Segment(1))), Is.EqualTo(0));

        var hold = _power.Holds.Single();
        Assert.That(hold.Reason, Is.EqualTo("ClassScribe is transcribing a recording"));
        Assert.That(hold.Needs(), Is.EqualTo(Awake), "on mains the screen may turn off");
        Assert.That(heldDuringUpload, Is.True);
        Assert.That(savedBeforeRelease, Is.True, "let go after the transcript was saved");
        Assert.That(hold.Releases, Is.EqualTo(1));
    }

    /// <summary>A file sent on its own keeps the laptop awake too, while it uploads, and lets it go once.</summary>
    [Test]
    public async Task A_file_sent_on_its_own_keeps_the_laptop_awake_too()
    {
        var video = _folder.File("class.mp4");
        File.WriteAllBytes(video, [1, 2, 3]);
        bool? heldDuringUpload = null;
        _client.Setup(Calls.AnyTranscription).Returns((string _, IReadOnlyList<string> _, CancellationToken _) =>
        {
            heldDuringUpload = _power.Holding;
            return Task.FromResult(Mocks.Transcript);
        });

        Assert.That(await CreateCommand().RunAsync(new TranscribeRequest(video)), Is.EqualTo(0));

        Assert.That(heldDuringUpload, Is.True);
        Assert.That(_power.Holds.Single().Releases, Is.EqualTo(1));
    }

    /// <summary>An upload that fails, or that Ctrl+C cancels, lets the laptop go, once, as the failure or the cancel goes on to the caller.</summary>
    [TestCase(false)]
    [TestCase(true)]
    public async Task The_laptop_is_let_go_after_a_failed_or_cancelled_upload(bool cancelled)
    {
        var recording = WriteRecording("Kalam", new DateTimeOffset(2026, 10, 14, 19, 30, 0, TimeSpan.Zero));
        if (cancelled)
        {
            _client.Setup(Calls.AnyTranscription).Returns(async (string _, IReadOnlyList<string> _, CancellationToken cancellation) =>
            {
                _signals.Cancel();
                await Task.Delay(Timeout.Infinite, cancellation);
                return Mocks.Transcript;
            });
        }
        else
        {
            _client.Setup(Calls.AnyTranscription).ThrowsAsync(new TranscriptionException("ElevenLabs is down."));
        }

        var failure = Assert.CatchAsync(async () => await CreateCommand().RunAsync(new TranscribeRequest(recording.Segment(1))));

        Assert.That(failure, cancelled ? Is.InstanceOf<OperationCanceledException>() : Is.InstanceOf<TranscriptionException>());
        Assert.That(_power.Holds.Single().Releases, Is.EqualTo(1));
    }

    /// <summary>
    /// The screen is kept on during the upload as Recording:KeepScreenOn says, as while recording: by default (OnBattery)
    /// only while the laptop runs on battery, with Always on mains too, and with Never not even on battery.
    /// </summary>
    [TestCase(KeepScreenOn.OnBattery, true, true)]
    [TestCase(KeepScreenOn.OnBattery, false, false)]
    [TestCase(KeepScreenOn.Always, false, true)]
    [TestCase(KeepScreenOn.Never, true, false)]
    public async Task The_screen_is_kept_on_during_the_upload_as_the_setting_says(KeepScreenOn setting, bool onBattery, bool screenOn)
    {
        var recording = WriteRecording("Kalam", new DateTimeOffset(2026, 10, 14, 19, 30, 0, TimeSpan.Zero));
        _powerSource.OnBattery = onBattery;

        await CreateCommand(keepScreenOn: setting).RunAsync(new TranscribeRequest(recording.Segment(1)));

        Assert.That(_power.Holds.Single().Needs(), Is.EqualTo(screenOn ? Awake | PowerNeeds.Display : Awake));
    }

    /// <summary>A recording that is not sent, here one the command line refuses without --again, keeps nothing awake.</summary>
    [Test]
    public async Task A_recording_that_is_not_sent_keeps_nothing_awake()
    {
        var recording = WriteRecording("Kalam", new DateTimeOffset(2026, 10, 14, 19, 30, 0, TimeSpan.Zero));
        Transcribe(recording);

        Assert.That(await CreateCommand().RunAsync(new TranscribeRequest(recording.Segment(1), FromCommandLine: true)), Is.EqualTo(1));

        Assert.That(_power.Holds, Is.Empty);
    }

    /// <summary>A single segment given by its path transcribes that recording alone: the folder's other recordings are not named.</summary>
    [Test]
    public async Task A_segment_given_by_its_path_does_not_name_the_other_recordings()
    {
        WriteRecording("Kalam", new DateTimeOffset(2026, 10, 7, 23, 0, 0, TimeSpan.Zero));
        var given = WriteRecording("Kalam", new DateTimeOffset(2026, 10, 14, 23, 0, 5, TimeSpan.Zero));

        Assert.That(await CreateCommand().RunAsync(new TranscribeRequest(given.Segment(1), FromCommandLine: true)), Is.EqualTo(0));

        Assert.That(_log.Messages, Has.None.StartsWith("Also in this folder"));
    }
}
