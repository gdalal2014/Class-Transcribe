using System.Text.Json;
using ClassScribe.Audio;
using ClassScribe.Commands;
using ClassScribe.Settings;
using ClassScribe.Storage;
using ClassScribe.Transcription;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ClassScribe.Tests;

/// <summary>
/// The rebuild command: which saved answer it lays out again for a folder, a file of a recording or an answer, and what it
/// refuses. It runs with the real workflow and no API key, around a preparer and a service that must never be called.
/// </summary>
[TestFixture]
public class RebuildCommandTests
{
    private TempFolder _folder = null!;
    private TestRuntime _runtime = null!;
    private string _class = null!;
    private Mock<IAudioPreparer> _preparer = null!;
    private Mock<ITranscriptionClient> _client = null!;
    private Mock<IDesktop> _desktop = null!;
    private RecordingLogger<RebuildCommand> _log = null!;

    /// <summary>Creates a temporary recordings folder with a class folder in it, ClassScribe's own folder in another, the mocks and the log to inspect.</summary>
    [SetUp]
    public void SetUp()
    {
        _folder = new TempFolder();
        _runtime = new TestRuntime();
        _class = _folder.File("Kalam");
        Directory.CreateDirectory(_class);
        _preparer = Mocks.AudioPreparer();
        _client = Mocks.TranscriptionClient();
        _desktop = new Mock<IDesktop>();
        _log = new RecordingLogger<RebuildCommand>();
    }

    /// <summary>Checks that nothing was prepared or sent, as no rebuild ever does, then deletes the temporary folders.</summary>
    [TearDown]
    public void TearDown()
    {
        try
        {
            _preparer.Verify(Calls.AnyPreparation, Times.Never);
            _client.Verify(Calls.AnyTranscription, Times.Never);
        }
        finally
        {
            _runtime.Dispose();
            _folder.Dispose();
        }
    }

    /// <summary>Builds the command around the real workflow, without an API key, opening what is done as <paramref name="openWhenDone"/> says.</summary>
    private RebuildCommand CreateCommand(bool openWhenDone = false)
    {
        var recording = TestOptions.Create(new RecordingOptions { OutputFolder = _folder.Path, OpenFolderWhenDone = openWhenDone, OpenTranscriptWhenDone = openWhenDone });
        var folders = new ClassFolders(recording, TimeProvider.System, NullLogger<ClassFolders>.Instance);
        var workflow = new TranscriptionWorkflow(
            _preparer.Object,
            _client.Object,
            new KeytermLoader(NullLogger<KeytermLoader>.Instance),
            new TranscriptFormatter(TestOptions.Create(new TranscriptOptions())),
            folders,
            new ClassMarks(NullLogger<ClassMarks>.Instance),
            TestOptions.Create(new ElevenLabsOptions { ApiKey = "" }),
            recording,
            new TranscriptionProgress(),
            NullLogger<TranscriptionWorkflow>.Instance);

        return new RebuildCommand(workflow, folders, _desktop.Object, recording, _log);
    }

    /// <summary>Writes a recording's first segment into the class folder, started at <paramref name="started"/>.</summary>
    private RecordingFiles WriteRecording(DateTimeOffset started)
    {
        var files = RecordingFiles.ForNew(_class, "Kalam", started);
        TestAudio.WriteWav(files.Segment(1), 16000, new short[1600]);
        return files;
    }

    /// <summary>Saves an answer from ElevenLabs holding <paramref name="text"/> as copy <paramref name="copy"/> of the recording's transcript, with its transcript.</summary>
    private static void SaveAnswer(RecordingFiles files, string text, int copy = 1)
    {
        File.WriteAllText(files.Json(copy), JsonSerializer.Serialize(new { text }));
        File.WriteAllText(files.Transcript(copy), "# Kalam");
    }

    /// <summary>
    /// A folder is rebuilt from its newest recording with a saved answer: the transcript is laid out again from that answer
    /// as the next free copy, the earlier copies stay as they were, and the command succeeds without a key or a request.
    /// </summary>
    [Test]
    public async Task A_folder_is_rebuilt_from_its_newest_recording_with_a_saved_answer()
    {
        var older = WriteRecording(new DateTimeOffset(2026, 10, 7, 19, 30, 0, TimeSpan.Zero));
        var newer = WriteRecording(new DateTimeOffset(2026, 10, 14, 19, 30, 0, TimeSpan.Zero));
        SaveAnswer(older, "Older answer.");
        SaveAnswer(newer, "Newer answer.");

        var exitCode = await CreateCommand().RunAsync(_class, open: true);

        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(File.ReadAllText(newer.Transcript(2)), Does.Contain("Newer answer."));
        Assert.That(File.ReadAllText(newer.Transcript()), Is.EqualTo("# Kalam"), "the earlier transcript stays as it was");
        Assert.That(File.Exists(older.Transcript(2)), Is.False);
        Assert.That(_log.Messages, Has.Member($"Transcript saved to {newer.Transcript(2)}"));
    }

    /// <summary>
    /// A recording started later that has no saved answer yet, such as one recorded but not transcribed, is passed over
    /// for the newest that has one, and the log says so, so that the one rebuilt is not taken for it.
    /// </summary>
    [Test]
    public async Task A_newer_recording_without_an_answer_is_passed_over_and_named()
    {
        var answered = WriteRecording(new DateTimeOffset(2026, 10, 7, 19, 30, 0, TimeSpan.Zero));
        var notYet = WriteRecording(new DateTimeOffset(2026, 10, 14, 19, 30, 0, TimeSpan.Zero));
        SaveAnswer(answered, "Answered.");

        Assert.That(await CreateCommand().RunAsync(_class, open: false), Is.EqualTo(0));

        Assert.That(File.ReadAllText(answered.Transcript(2)), Does.Contain("Answered."));
        Assert.That(_log.Messages, Has.Member(
            $"{notYet.BaseName} has no saved answer from ElevenLabs yet, so {answered.BaseName}, the newest recording here that has one, is rebuilt"));
    }

    /// <summary>
    /// A newer recording whose only answer was paid for but kept apart, beside it or in the recordings folder, is passed over
    /// for the newest recording with a saved answer, since a kept answer is not laid out unasked. The warning says where that
    /// answer is, rather than that the recording has no answer yet, and the command that lays it out for free follows.
    /// </summary>
    [TestCase(false)]
    [TestCase(true)]
    public async Task A_newer_recording_whose_paid_answer_is_kept_is_named_with_that_answer_and_its_free_rebuild(bool inTheRecordingsFolder)
    {
        var answered = WriteRecording(new DateTimeOffset(2026, 10, 7, 19, 30, 0, TimeSpan.Zero));
        var kept = WriteRecording(new DateTimeOffset(2026, 10, 14, 19, 30, 0, TimeSpan.Zero));
        SaveAnswer(answered, "Answered.");
        var keptAnswer = inTheRecordingsFolder ? _folder.File(Path.GetFileName(kept.UnreadAnswer())) : kept.UnreadAnswer();
        File.WriteAllText(keptAnswer, JsonSerializer.Serialize(new { text = "Kept answer." }));

        Assert.That(await CreateCommand().RunAsync(_class, open: false), Is.EqualTo(0));

        Assert.That(File.ReadAllText(answered.Transcript(2)), Does.Contain("Answered."));
        Assert.That(File.Exists(kept.Transcript()), Is.False, "the kept answer is not laid out unasked");
        Assert.That(_log.Problems, Is.EqualTo(new[]
        {
            $"{kept.BaseName} has no saved answer from ElevenLabs that became a transcript, but the answer to an earlier try, which has been paid for, is kept in {keptAnswer}. {answered.BaseName}, the newest recording here with a saved answer, is rebuilt instead"
        }));
        Assert.That(_log.Messages, Has.Member($"To lay that answer out as a transcript for free, run: {CommandLine.Rebuild(keptAnswer)}"));
    }

    /// <summary>
    /// Every recording started later than the one rebuilt is named with the reason it was passed over: here one with a
    /// transcript whose saved answer is no longer there, as when it was deleted by hand, and one with no saved answer yet.
    /// </summary>
    [Test]
    public async Task Every_newer_recording_passed_over_is_named_with_its_reason()
    {
        var answered = WriteRecording(new DateTimeOffset(2026, 10, 1, 19, 30, 0, TimeSpan.Zero));
        var notYet = WriteRecording(new DateTimeOffset(2026, 10, 7, 19, 30, 0, TimeSpan.Zero));
        var answerGone = WriteRecording(new DateTimeOffset(2026, 10, 14, 19, 30, 0, TimeSpan.Zero));
        SaveAnswer(answered, "Answered.");
        File.WriteAllText(answerGone.Transcript(), "# Kalam");

        Assert.That(await CreateCommand().RunAsync(_class, open: false), Is.EqualTo(0));

        Assert.That(File.ReadAllText(answered.Transcript(2)), Does.Contain("Answered."));
        Assert.That(_log.Messages.Take(2), Is.EqualTo(new[]
        {
            $"{answerGone.BaseName} has a transcript, but its saved answer from ElevenLabs is no longer there, so {answered.BaseName}, the newest recording here that still has one, is rebuilt",
            $"{notYet.BaseName} has no saved answer from ElevenLabs yet, so {answered.BaseName}, the newest recording here that has one, is rebuilt"
        }));
    }

    /// <summary>
    /// A folder whose only recording is known by an answer that was paid for and kept apart, its audio deleted since, still
    /// holds that recording: the refusal says where the answer is and gives the command that lays it out for free.
    /// </summary>
    [Test]
    public async Task A_folder_whose_only_answer_is_kept_points_to_it()
    {
        var recording = RecordingFiles.ForNew(_class, "Kalam", new DateTimeOffset(2026, 10, 14, 19, 30, 0, TimeSpan.Zero));
        File.WriteAllText(recording.UnreadAnswer(), JsonSerializer.Serialize(new { text = "Kept answer." }));

        Assert.That(await CreateCommand().RunAsync(_class, open: false), Is.EqualTo(1));

        Assert.That(_log.Problems, Is.EqualTo(new[]
        {
            $"{recording.BaseName} has no saved answer from ElevenLabs that became a transcript, but the answer to an earlier try, which has been paid for, is kept in {recording.UnreadAnswer()}"
        }));
        Assert.That(_log.Messages[^1], Is.EqualTo($"To lay that answer out as a transcript for free, run: {CommandLine.Rebuild(recording.UnreadAnswer())}"));
        Assert.That(Directory.GetFiles(_class, "*.md"), Is.Empty);
    }

    /// <summary>A recording whose audio was deleted after it was transcribed still counts in its folder: its saved answer is all a rebuild needs.</summary>
    [Test]
    public async Task A_recording_whose_audio_was_deleted_is_rebuilt_from_its_folder()
    {
        var transcribed = WriteRecording(new DateTimeOffset(2026, 10, 14, 19, 30, 0, TimeSpan.Zero));
        SaveAnswer(transcribed, "Still here.");
        File.Delete(transcribed.Segment(1));

        Assert.That(await CreateCommand().RunAsync(_class, open: false), Is.EqualTo(0));

        Assert.That(File.ReadAllText(transcribed.Transcript(2)), Does.Contain("Still here."));
    }

    /// <summary>
    /// Any file of a recording, a segment, its marks or one of its transcripts, means that recording, even when another
    /// in the folder is newer: it is rebuilt from its own newest saved answer.
    /// </summary>
    [TestCase("segment")]
    [TestCase("later segment")]
    [TestCase("marks")]
    [TestCase("transcript")]
    [TestCase("later transcript")]
    public async Task A_file_of_a_recording_rebuilds_that_recording(string given)
    {
        var meant = WriteRecording(new DateTimeOffset(2026, 10, 7, 19, 30, 0, TimeSpan.Zero));
        var newer = WriteRecording(new DateTimeOffset(2026, 10, 14, 19, 30, 0, TimeSpan.Zero));
        SaveAnswer(meant, "First answer.");
        SaveAnswer(meant, "Second answer.", copy: 2);
        SaveAnswer(newer, "Another class.");
        TestAudio.WriteWav(meant.Segment(2), 16000, new short[1600]);
        File.WriteAllLines(meant.Marks, ["00:00:00.050"]);
        var path = given switch
        {
            "segment" => meant.Segment(1),
            "later segment" => meant.Segment(2),
            "marks" => meant.Marks,
            "transcript" => meant.Transcript(),
            _ => meant.Transcript(2)
        };

        Assert.That(await CreateCommand().RunAsync(path, open: false), Is.EqualTo(0));

        Assert.That(File.ReadAllText(meant.Transcript(3)), Does.Contain("Second answer.").And.Contain("] Mark 1"), "its own answer and marks");
        Assert.That(File.Exists(newer.Transcript(2)), Is.False);
    }

    /// <summary>A saved answer given by its path is the one laid out, even an older one, or one kept as unread because it could not be saved in its place.</summary>
    [TestCase(false)]
    [TestCase(true)]
    public async Task An_answer_given_by_its_path_is_the_one_laid_out(bool unread)
    {
        var recording = WriteRecording(new DateTimeOffset(2026, 10, 14, 19, 30, 0, TimeSpan.Zero));
        SaveAnswer(recording, "First answer.");
        SaveAnswer(recording, "Second answer.", copy: 2);
        File.WriteAllText(recording.UnreadAnswer(), JsonSerializer.Serialize(new { text = "Kept answer." }));

        Assert.That(await CreateCommand().RunAsync(unread ? recording.UnreadAnswer() : recording.Json(), open: false), Is.EqualTo(0));

        Assert.That(File.ReadAllText(recording.Transcript(3)), Does.Contain(unread ? "Kept answer." : "First answer.").And.Not.Contain("Second answer."));
    }

    /// <summary>
    /// An answer saved under a name of its own, as one downloaded from ElevenLabs, is laid out beside it under its name;
    /// as the answer takes the first copy's number, the transcript is the second.
    /// </summary>
    [Test]
    public async Task An_answer_saved_under_a_name_of_its_own_is_laid_out_beside_it()
    {
        var answer = _folder.File("answer.json");
        File.WriteAllText(answer, JsonSerializer.Serialize(new { text = "Downloaded." }));

        Assert.That(await CreateCommand().RunAsync(answer, open: false), Is.EqualTo(0));

        Assert.That(File.ReadAllText(_folder.File("answer (2).md")), Does.StartWith("# answer").And.Contain("Downloaded."));
    }

    /// <summary>
    /// An answer kept in the recordings folder, because its recording's own folder would not take it, is laid out beside its
    /// recording, with the recording's marks and the time it was recorded, so that the recording counts as transcribed and
    /// nothing is written beside the answer.
    /// </summary>
    [Test]
    public async Task An_answer_kept_in_the_recordings_folder_is_laid_out_beside_its_recording()
    {
        var recording = WriteRecording(new DateTimeOffset(2026, 10, 14, 19, 30, 0, TimeSpan.Zero));
        File.WriteAllLines(recording.Marks, ["00:00:00.050"]);
        var kept = _folder.File(Path.GetFileName(recording.UnreadAnswer()));
        File.WriteAllText(kept, JsonSerializer.Serialize(new { text = "Kept answer." }));

        Assert.That(await CreateCommand().RunAsync(kept, open: false), Is.EqualTo(0));

        Assert.That(File.ReadAllText(recording.Transcript()),
            Does.Contain("Recorded 14 October 2026, 19:30 UTC.").And.Contain("] Mark 1").And.Contain("Kept answer."));
        Assert.That(recording.HasTranscript, Is.True, "the recording counts as transcribed");
        Assert.That(Directory.GetFiles(_folder.Path, "*.md"), Is.Empty, "nothing is written beside the kept answer");
        Assert.That(_log.Messages, Has.Member($"Transcript saved to {recording.Transcript()}"));
    }

    /// <summary>An answer kept in the recordings folder whose recording is gone, folder and all, is laid out beside the answer.</summary>
    [Test]
    public async Task An_answer_kept_in_the_recordings_folder_without_its_recording_is_laid_out_beside_it()
    {
        var recording = RecordingFiles.ForNew(_class, "Kalam", new DateTimeOffset(2026, 10, 14, 19, 30, 0, TimeSpan.Zero));
        Directory.Delete(_class);
        var kept = _folder.File(Path.GetFileName(recording.UnreadAnswer()));
        File.WriteAllText(kept, JsonSerializer.Serialize(new { text = "Kept answer." }));

        Assert.That(await CreateCommand().RunAsync(kept, open: false), Is.EqualTo(0));

        Assert.That(File.ReadAllText(_folder.File($"{recording.BaseName}.md")), Does.Contain("Kept answer."));
    }

    /// <summary>When the transcript is ready, it and its folder are opened as the settings ask; with --no-open, nothing is.</summary>
    [TestCase(true)]
    [TestCase(false)]
    public async Task The_rebuilt_transcript_is_opened_unless_no_open_says_otherwise(bool open)
    {
        var recording = WriteRecording(new DateTimeOffset(2026, 10, 14, 19, 30, 0, TimeSpan.Zero));
        SaveAnswer(recording, "Hello.");

        Assert.That(await CreateCommand(openWhenDone: true).RunAsync(recording.Segment(1), open), Is.EqualTo(0));

        if (open)
        {
            _desktop.Verify(d => d.OpenFolder(recording.Folder), Times.Once);
            _desktop.Verify(d => d.OpenFile(recording.Transcript(2)), Times.Once);
        }

        _desktop.VerifyNoOtherCalls();
    }

    /// <summary>A recording without a saved answer cannot be rebuilt: the command exits with 1, says to transcribe it first, and writes nothing.</summary>
    [Test]
    public async Task A_recording_without_a_saved_answer_is_refused()
    {
        var recording = WriteRecording(new DateTimeOffset(2026, 10, 14, 19, 30, 0, TimeSpan.Zero));

        Assert.That(await CreateCommand().RunAsync(recording.Segment(1), open: true), Is.EqualTo(1));
        Assert.That(await CreateCommand().RunAsync(_class, open: true), Is.EqualTo(1), "nor can a folder whose recordings have none");

        Assert.That(_log.Problems, Is.EqualTo(Enumerable.Repeat($"{recording.BaseName} has no saved answer from ElevenLabs yet; transcribe it first", 2)));
        Assert.That(Directory.GetFiles(_class, "*.md"), Is.Empty);
        _desktop.VerifyNoOtherCalls();
    }

    /// <summary>
    /// A recording with a transcript whose saved answer from ElevenLabs is no longer there, as when it was deleted by hand,
    /// cannot be rebuilt: the refusal says that, rather than to transcribe it first, and nothing is written.
    /// </summary>
    [Test]
    public async Task A_transcribed_recording_whose_saved_answer_is_gone_is_refused_with_that_reason()
    {
        var recording = WriteRecording(new DateTimeOffset(2026, 10, 14, 19, 30, 0, TimeSpan.Zero));
        File.WriteAllText(recording.Transcript(), "# Kalam");

        Assert.That(await CreateCommand().RunAsync(recording.Segment(1), open: false), Is.EqualTo(1));

        Assert.That(_log.Problems, Is.EqualTo(new[]
        {
            $"{recording.BaseName} has a transcript, but its saved answer from ElevenLabs is no longer there, so it cannot be rebuilt for free"
        }));
        Assert.That(Directory.GetFiles(_class, "*.md"), Is.EqualTo(new[] { recording.Transcript() }));
    }

    /// <summary>
    /// A recording whose only answer was paid for but kept as unread is not rebuilt from that answer unasked, since it was
    /// set aside; the refusal says where it is and gives the command that lays it out.
    /// </summary>
    [Test]
    public async Task A_recording_whose_only_answer_is_kept_as_unread_points_to_it()
    {
        var recording = WriteRecording(new DateTimeOffset(2026, 10, 14, 19, 30, 0, TimeSpan.Zero));
        File.WriteAllText(recording.UnreadAnswer(), JsonSerializer.Serialize(new { text = "Kept answer." }));

        Assert.That(await CreateCommand().RunAsync(recording.Segment(1), open: false), Is.EqualTo(1));

        Assert.That(_log.Problems, Is.EqualTo(new[]
        {
            $"{recording.BaseName} has no saved answer from ElevenLabs that became a transcript, but the answer to an earlier try, which has been paid for, is kept in {recording.UnreadAnswer()}"
        }));
        Assert.That(_log.Messages[^1], Is.EqualTo($"To lay that answer out as a transcript for free, run: {CommandLine.Rebuild(recording.UnreadAnswer())}"));
        Assert.That(Directory.GetFiles(_class, "*.md"), Is.Empty);
    }

    /// <summary>A folder without any recording, a path that leads nowhere, and a file that is no recording's and no answer are each refused with exit code 1 and the reason.</summary>
    [Test]
    public async Task What_is_no_recording_or_answer_is_refused_with_the_reason()
    {
        var video = _folder.File("lecture.mp4");
        File.WriteAllBytes(video, [1, 2, 3]);
        var missing = _folder.File("missing");

        Assert.That(await CreateCommand().RunAsync(_class, open: false), Is.EqualTo(1));
        Assert.That(await CreateCommand().RunAsync(missing, open: false), Is.EqualTo(1));
        Assert.That(await CreateCommand().RunAsync(video, open: false), Is.EqualTo(1));

        Assert.That(_log.Problems, Is.EqualTo(new[]
        {
            $"There is no ClassScribe recording in {_class}",
            $"Nothing found at {missing}",
            $"{video} is neither a file of a ClassScribe recording nor an answer from ElevenLabs saved as .json, so there is nothing to rebuild from it"
        }));
    }

    /// <summary>A saved answer that cannot be read fails the rebuild with the reason, and nothing is written or opened.</summary>
    [Test]
    public void An_answer_that_cannot_be_read_fails_and_writes_nothing()
    {
        var recording = WriteRecording(new DateTimeOffset(2026, 10, 14, 19, 30, 0, TimeSpan.Zero));
        File.WriteAllText(recording.Json(), "not an answer");

        Assert.ThrowsAsync<TranscriptionException>(async () => await CreateCommand(openWhenDone: true).RunAsync(recording.Segment(1), open: true));

        Assert.That(Directory.GetFiles(_class, "*.md"), Is.Empty);
        _desktop.VerifyNoOtherCalls();
    }

    /// <summary>
    /// "rebuild" runs through the app as Program puts it together, without a key: the command line is read, the settings
    /// pass the gate, the report at the start looks at the run files (in the test's own folder, never the real one, where a
    /// class may have left a report), the command is registered and runs in a scope of its own, and the transcript is written.
    /// </summary>
    [Test]
    public async Task Rebuild_runs_through_the_app_without_a_key()
    {
        var recording = WriteRecording(new DateTimeOffset(2026, 10, 14, 19, 30, 0, TimeSpan.Zero));
        SaveAnswer(recording, "Through the app.");
        using var settings = new TempFolder();
        File.WriteAllText(settings.File("appsettings.json"), JsonSerializer.Serialize(new { Recording = new { OutputFolder = _folder.Path } }));
        using var app = AppHost.Build(settings.Path, apiKey: null, services => services.AddSingleton(_runtime.Folder));

        var exitCode = await app.Services.GetRequiredService<App>().RunAsync(["rebuild", _class, "--no-open"]);

        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(File.ReadAllText(recording.Transcript(2)), Does.Contain("Through the app."));
        using var scope = app.Services.CreateScope();
        Assert.That(scope.ServiceProvider.GetRequiredService<RebuildCommand>(), Is.Not.Null);
    }
}
