using ClassScribe.Commands;
using ClassScribe.Recorder;
using ClassScribe.Settings;
using ClassScribe.Storage;
using ClassScribe.YouTube;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClassScribe.Tests;

/// <summary>
/// The list command: every recording under the recordings folder, newest first, one line each, with its start in local
/// time (US Eastern here, where an evening class has a UTC stamp on the next day), and what the run files say is going on
/// with it now. The recordings are real files in a temporary folder, the run files real files in a folder of the test's
/// own, and the processes they name are played by the test.
/// </summary>
[TestFixture]
public class ListCommandTests
{
    private TempFolder _recordings = null!;
    private TestRuntime _runtime = null!;
    private FakeConsoleUi _ui = null!;

    /// <summary>Creates the recordings folder, ClassScribe's own folder for the run files, and the console the list is written to.</summary>
    [SetUp]
    public void SetUp()
    {
        _recordings = new TempFolder();
        _runtime = new TestRuntime();
        _ui = new FakeConsoleUi();
    }

    /// <summary>Deletes the temporary folders. That fails if a test left a file open, which is the point.</summary>
    [TearDown]
    public void TearDown()
    {
        _recordings.Dispose();
        _runtime.Dispose();
    }

    /// <summary>The command, with Recording:OutputFolder set to the test's recordings folder and local time in US Eastern summer time.</summary>
    private ListCommand CreateCommand() =>
        new(new ClassFolders(TestOptions.Create(new RecordingOptions { OutputFolder = _recordings.Path }), TimeProvider.System, NullLogger<ClassFolders>.Instance),
            _runtime.Journal, _ui, ZonedTimeProvider.EasternSummer);

    /// <summary>What the command wrote, line by line.</summary>
    private string[] Lines => _ui.Screen.Split(Environment.NewLine);

    /// <summary>
    /// Writes a recording of <paramref name="name"/> started at <paramref name="startedUtc"/>, <paramref name="seconds"/>
    /// long, into the folder of its name under <paramref name="root"/>, or under the recordings folder, as recordings were
    /// kept before the course and date folders; or into <paramref name="folder"/> there, such as "Mantiq\2026-10-17". One
    /// sample a second keeps a long recording small on disk.
    /// </summary>
    private RecordingFiles WriteRecording(string name, DateTimeOffset startedUtc, int seconds, string? root = null, string? folder = null)
    {
        var files = RecordingFiles.ForNew(Path.Combine(root ?? _recordings.Path, folder ?? name), name, startedUtc);
        Directory.CreateDirectory(files.Folder);
        TestAudio.WriteWav(files.Segment(1), sampleRate: 1, new short[seconds]);
        File.SetLastWriteTimeUtc(files.Segment(1), startedUtc.UtcDateTime);
        return files;
    }

    /// <summary>
    /// The recordings of both layouts are listed together, those in the course and date folders and those flat in the
    /// folder of their name; a second class of a name on the same day, in the date folder marked "(2)", is shown with that
    /// folder, which tells the day's classes apart, while the day's first class needs none. His own files there, such as
    /// the class's PDFs, and his own folders, such as Slides, are passed over.
    /// </summary>
    [Test]
    public void Recordings_of_both_layouts_are_listed_and_a_later_class_of_the_day_shows_its_folder()
    {
        WriteRecording("Mantiq", new DateTimeOffset(2026, 10, 17, 14, 4, 42, TimeSpan.Zero), seconds: 7860, folder: @"Mantiq\2026-10-17");
        WriteRecording("Mantiq", new DateTimeOffset(2026, 10, 17, 22, 0, 0, TimeSpan.Zero), seconds: 3600, folder: @"Mantiq\2026-10-17 (2)");
        WriteRecording("Mantiq", new DateTimeOffset(2026, 10, 18, 22, 0, 0, TimeSpan.Zero), seconds: 60, folder: @"Mantiq\Slides");
        File.WriteAllBytes(Path.Combine(_recordings.Path, "Mantiq", "2026-10-17", "Class_1_slides.pdf"), [1]);
        WriteRecording("Kalam", new DateTimeOffset(2026, 10, 15, 23, 0, 2, TimeSpan.Zero), seconds: 60);

        CreateCommand().Run(root: null);

        Assert.That(Lines, Is.EqualTo(new[]
        {
            $"Recordings in {_recordings.Path}, newest first (times are local):",
            "  Mantiq  17 Oct 18:00  01:00:00  in 2026-10-17 (2)  no transcript yet",
            "  Mantiq  17 Oct 10:04  02:11:00  no transcript yet",
            "  Kalam   15 Oct 19:00  00:01:00  no transcript yet"
        }));
    }

    /// <summary>
    /// Each recording has a line, newest first by its start: its name, when it started in local time, its length, the
    /// parts it was made in when there were more than one, "class + microphone" when your microphone was recorded with the
    /// class, and its transcripts (how many there are when more than one), an answer kept without a transcript, or none.
    /// </summary>
    [Test]
    public void Each_recording_has_a_line_with_its_local_start_length_parts_microphone_and_transcripts()
    {
        var kalam = WriteRecording("Kalam", new DateTimeOffset(2026, 10, 15, 23, 0, 2, TimeSpan.Zero), seconds: 7090);
        File.WriteAllText(kalam.Marks, "00:47:10.000 ~ Resumed at 23:51 UTC, about 4 minutes not recorded" + Environment.NewLine);
        TestAudio.WriteWav(Path.Combine(kalam.Folder, $"{kalam.BaseName}.mic.wav"), sampleRate: 1, new short[3600]);
        File.WriteAllText(kalam.Transcript(), "# Kalam");
        File.WriteAllText(kalam.Transcript(2), "# Kalam");
        WriteRecording("Fiqh", new DateTimeOffset(2026, 10, 14, 23, 30, 0, TimeSpan.Zero), seconds: 3600);
        var kept = WriteRecording("Fiqh", new DateTimeOffset(2026, 10, 3, 23, 30, 0, TimeSpan.Zero), seconds: 1800);
        File.WriteAllText(kept.UnreadAnswer(), "{}");

        var exitCode = CreateCommand().Run(root: null);

        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(Lines, Is.EqualTo(new[]
        {
            $"Recordings in {_recordings.Path}, newest first (times are local):",
            "  Kalam  15 Oct 19:00  01:58:10  2 parts  class + microphone  transcript (2)",
            "  Fiqh   14 Oct 19:30  01:00:00  no transcript yet",
            "  Fiqh    3 Oct 19:30  00:30:00  answer kept"
        }));
    }

    /// <summary>
    /// What the run files say is going on is shown after the transcript: RECORDING NOW for a recording a live recorder
    /// records, and for one whose newest segment a recorder holds open for writing although no run file says so;
    /// TRANSCRIBING for one a live recorder transcribes; ENDED UNEXPECTEDLY for one whose recorder is gone while its file
    /// still says it records. A run that finished, or an unexpected end that has been dealt with, shows nothing. The
    /// transcriptions in the background follow the list, with how far their upload is.
    /// </summary>
    [Test]
    public void What_the_runs_are_doing_now_is_shown_and_the_transcriptions_in_the_background_follow()
    {
        var recordingNow = WriteRecording("Kalam", new DateTimeOffset(2026, 10, 16, 23, 0, 2, TimeSpan.Zero), seconds: 2533);
        var transcribing = WriteRecording("Fiqh", new DateTimeOffset(2026, 10, 15, 23, 0, 0, TimeSpan.Zero), seconds: 3600);
        var ended = WriteRecording("Kalam", new DateTimeOffset(2026, 10, 14, 23, 0, 0, TimeSpan.Zero), seconds: 600);
        var held = WriteRecording("Tafsir", new DateTimeOffset(2026, 10, 13, 23, 0, 0, TimeSpan.Zero), seconds: 60);
        var finished = WriteRecording("Kalam", new DateTimeOffset(2026, 10, 12, 23, 0, 0, TimeSpan.Zero), seconds: 60);
        File.WriteAllText(finished.Transcript(), "# Kalam");
        var forgotten = WriteRecording("Kalam", new DateTimeOffset(2026, 10, 11, 23, 0, 0, TimeSpan.Zero), seconds: 60);
        _runtime.WriteRun(1, recordingNow, RunState.Recording, alive: true);
        _runtime.WriteRun(2, transcribing, RunState.Transcribing, alive: true, run => run with { UploadPercent = 40 });
        _runtime.WriteRun(3, ended, RunState.Recording, alive: false);
        _runtime.WriteRun(5, finished, RunState.Finished, alive: false,
            run => run with { Outcome = new RunOutcome(OutcomeKind.Transcribed, 0, "Transcript saved", finished.Transcript(), finished.Folder, null) });
        _runtime.WriteRun(6, forgotten, RunState.Recording, alive: false, run => run with { Seen = true });

        int exitCode;
        using (new FileStream(held.Segment(1), FileMode.Open, FileAccess.Write, FileShare.Read))
            exitCode = CreateCommand().Run(root: null);

        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(Lines, Is.EqualTo(new[]
        {
            $"Recordings in {_recordings.Path}, newest first (times are local):",
            "  Kalam   16 Oct 19:00  00:42:13  no transcript yet  RECORDING NOW",
            "  Fiqh    15 Oct 19:00  01:00:00  no transcript yet  TRANSCRIBING",
            "  Kalam   14 Oct 19:00  00:10:00  no transcript yet  ENDED UNEXPECTEDLY",
            "  Tafsir  13 Oct 19:00  00:01:00  no transcript yet  RECORDING NOW",
            "  Kalam   12 Oct 19:00  00:01:00  transcript",
            "  Kalam   11 Oct 19:00  00:01:00  no transcript yet",
            "Being transcribed in the background: \"Fiqh\" (uploading 40%)."
        }));
    }

    /// <summary>
    /// The newest run of a recording says what is going on with it: one that ended unexpectedly and was then continued by
    /// a run that records now is RECORDING NOW, not ENDED UNEXPECTEDLY.
    /// </summary>
    [Test]
    public void The_newest_run_of_a_recording_says_what_is_going_on_with_it()
    {
        var continued = WriteRecording("Kalam", new DateTimeOffset(2026, 10, 15, 23, 0, 2, TimeSpan.Zero), seconds: 2830);
        _runtime.WriteRun(1, continued, RunState.Recording, alive: false, run => run with { StartedUtc = TestRuns.Start });
        _runtime.WriteRun(2, continued, RunState.Recording, alive: true, run => run with { StartedUtc = TestRuns.Start.AddMinutes(51) });

        CreateCommand().Run(root: null);

        Assert.That(Lines[1], Is.EqualTo("  Kalam  15 Oct 19:00  00:47:10  no transcript yet  RECORDING NOW"));
    }

    /// <summary>
    /// A transcription in the background is listed at the end even when its recording is in another recordings folder, and
    /// without a percentage before its upload has begun.
    /// </summary>
    [Test]
    public void Transcriptions_in_the_background_are_listed_whatever_folder_their_recording_is_in()
    {
        using var elsewhere = new TempFolder();
        var fiqh = WriteRecording("Fiqh", new DateTimeOffset(2026, 10, 15, 23, 0, 0, TimeSpan.Zero), seconds: 60, root: elsewhere.Path);
        _runtime.WriteRun(1, fiqh, RunState.Transcribing, alive: true);

        var exitCode = CreateCommand().Run(root: null);

        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(Lines, Is.EqualTo(new[]
        {
            $"No recordings in {_recordings.Path}.",
            "Being transcribed in the background: \"Fiqh\"."
        }));
    }

    /// <summary>A recordings folder without recordings, or one that is not there yet, is said to hold none; that is no failure.</summary>
    [TestCase(true)]
    [TestCase(false)]
    public void Without_recordings_the_list_says_so(bool folderExists)
    {
        var root = folderExists ? _recordings.Path : _recordings.File("Not yet");

        var exitCode = CreateCommand().Run(root);

        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(Lines, Is.EqualTo(new[] { $"No recordings in {root}." }));
    }

    /// <summary>The folder given with --output is listed instead of Recording:OutputFolder.</summary>
    [Test]
    public void The_folder_given_is_listed_instead_of_the_recordings_folder()
    {
        WriteRecording("Kalam", new DateTimeOffset(2026, 10, 15, 23, 0, 2, TimeSpan.Zero), seconds: 60);
        using var elsewhere = new TempFolder();
        WriteRecording("Fiqh", new DateTimeOffset(2026, 10, 14, 23, 0, 0, TimeSpan.Zero), seconds: 60, root: elsewhere.Path);

        var exitCode = CreateCommand().Run(elsewhere.Path);

        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(Lines, Is.EqualTo(new[]
        {
            $"Recordings in {elsewhere.Path}, newest first (times are local):",
            "  Fiqh  14 Oct 19:00  00:01:00  no transcript yet"
        }));
    }
}

/// <summary>A batch's YouTube videos in the list: each is named by its video's title and how its audio was obtained.</summary>
[TestFixture]
public class ListCommandVideoTests
{
    /// <summary>
    /// Two videos of one batch, in one date folder, are told apart by their titles, which come first among the notes; a
    /// class beside them has none.
    /// </summary>
    [Test]
    public void A_videos_recording_is_named_by_its_title()
    {
        using var recordings = new TempFolder();
        using var runtime = new TestRuntime();
        var ui = new FakeConsoleUi();
        var folder = Path.Combine(recordings.Path, "Risala", "2026-10-18");
        Directory.CreateDirectory(folder);
        var first = RecordingFiles.ForNew(folder, "Risala", new DateTimeOffset(2026, 10, 18, 14, 2, 11, TimeSpan.Zero));
        var second = RecordingFiles.ForNew(folder, "Risala", new DateTimeOffset(2026, 10, 18, 14, 3, 40, TimeSpan.Zero));
        foreach (var (files, title) in new[] { (first, "The creed, part 1"), (second, "The creed, part 2") })
        {
            TestAudio.WriteWav(files.Segment(1), sampleRate: 1, new short[60]);
            VideoSourceFile.Write(files.Source, VideoSourceFileTests.Downloaded(title: title));
        }

        new ListCommand(new ClassFolders(TestOptions.Create(new RecordingOptions { OutputFolder = recordings.Path }), TimeProvider.System, NullLogger<ClassFolders>.Instance),
            runtime.Journal, ui, ZonedTimeProvider.EasternSummer).Run(root: null);

        Assert.That(ui.Screen.Split(Environment.NewLine).Skip(1), Is.EqualTo(new[]
        {
            "  Risala  18 Oct 10:03  00:01:00  \"The creed, part 2\" (YouTube, downloaded)  no transcript yet",
            "  Risala  18 Oct 10:02  00:01:00  \"The creed, part 1\" (YouTube, downloaded)  no transcript yet"
        }));
    }
}
