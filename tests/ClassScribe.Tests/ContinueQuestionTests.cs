using ClassScribe.Audio;
using ClassScribe.Commands;
using ClassScribe.Recorder;
using ClassScribe.Settings;
using ClassScribe.Storage;

namespace ClassScribe.Tests;

/// <summary>
/// Whether a new recording continues one of the same name that stopped recently without a transcript: the menu asks (C
/// continues it, N starts a new one, Esc goes back), and the command line never asks: --continue continues it, or says
/// there is none and starts a new one; --new says nothing; neither starts a new one and says how to continue instead. A
/// recording a run still owns, or one made with other tracks, is never offered. The recordings and run files are real
/// files in temporary folders.
/// </summary>
[TestFixture]
public class ContinueQuestionTests
{
    /// <summary>Now: 23:51:02 UTC, four minutes after the recording below stopped.</summary>
    private static readonly DateTimeOffset Now = TestRuns.Start.AddMinutes(51);

    private TestRuntime _runtime = null!;
    private TempFolder _recordings = null!;
    private TestOptions<RecordingOptions> _options = null!;
    private FakeConsoleUi _ui = null!;
    private RecordingLogger<ContinueQuestion> _log = null!;

    /// <summary>Makes the recordings folder, ClassScribe's own folder, the clock at <see cref="Now"/> and a console to type into.</summary>
    [SetUp]
    public void SetUp()
    {
        _runtime = new TestRuntime();
        _runtime.Time.Advance(Now - TestRuns.Start);
        _recordings = new TempFolder();
        _options = TestOptions.Create(new RecordingOptions { OutputFolder = _recordings.Path });
        _ui = new FakeConsoleUi();
        _log = new RecordingLogger<ContinueQuestion>();
    }

    /// <summary>Deletes the temporary folders.</summary>
    [TearDown]
    public void TearDown()
    {
        _recordings.Dispose();
        _runtime.Dispose();
    }

    /// <summary>The question under test.</summary>
    private ContinueQuestion Question() =>
        new(new ClassFolders(_options, _runtime.Time, new RecordingLogger<ClassFolders>()), _runtime.Journal, _ui, _options, _runtime.Time, _log);

    /// <summary>
    /// Writes a recording of "Kalam" that started at 23:00:02 UTC and stopped at 23:47:02, four minutes before now, holding
    /// 00:47:10 of audio, with its parts as given: as a crash, a closed window or a restart leaves it. It is flat in the
    /// folder of its name, as recordings were kept before the course and date folders, or in <paramref name="dateFolder"/>
    /// there, such as 2026-10-15.
    /// </summary>
    private RecordingFiles WriteStopped(string name = "Kalam", int parts = 1, string? dateFolder = null)
    {
        var course = Path.Combine(_recordings.Path, name);
        var files = RecordingFiles.ForNew(dateFolder is null ? course : Path.Combine(course, dateFolder), name, TestRuns.Start);
        Directory.CreateDirectory(files.Folder);
        for (var part = 1; part <= parts; part++)
        {
            using (var writer = new WavSegmentWriter(files.Segment(part), 10))
                writer.Write(new byte[(int)(TimeSpan.FromSeconds(2830).TotalSeconds * 10 / parts) * 2]);
            File.SetLastWriteTimeUtc(files.Segment(part), Now.AddMinutes(-4).UtcDateTime);
        }

        for (var resume = 2; resume <= parts; resume++)
            new ClassMarks(new RecordingLogger<ClassMarks>()).AddNote(files, TimeSpan.FromMinutes(20), "Resumed at 23:30 UTC, about 1 minute not recorded");

        return files;
    }

    /// <summary>The run file of a run that recorded <paramref name="files"/> in <paramref name="mode"/>, by process <paramref name="processId"/>.</summary>
    private void WriteRun(RecordingFiles files, RecordingMode mode = RecordingMode.Class, RunState state = RunState.Recording, int processId = TestRuns.ProcessId)
    {
        var plan = mode == RecordingMode.Microphone
            ? new SourcePlan(mode, null, new MicrophoneChoice(null))
            : new SourcePlan(mode, new ClassSource.Automatic(), mode == RecordingMode.ClassAndMicrophone ? new MicrophoneChoice(null) : null);
        var record = TestRuns.Record(TestRuns.Id(), state, processId) with
        {
            Request = TestRuns.Request(TestRuns.Id()) with { Source = plan, OutputRoot = _recordings.Path },
            BaseName = files.BaseName,
            Folder = files.Folder
        };
        _runtime.Journal.Write(_runtime.Journal.RunFile(record.RunId), record);
    }

    /// <summary>The menu asks about a recording that stopped recently without a transcript, and C continues it as its next part.</summary>
    [Test]
    public async Task The_menu_asks_and_C_continues_the_recording()
    {
        var files = WriteStopped();
        _ui.Type('c');

        var answer = await Question().AskAsync("Kalam", RecordingMode.Class, _recordings.Path);

        Assert.That(answer.GoBack, Is.False);
        Assert.That(answer.Continue, Is.EqualTo(files));
        Assert.That(answer.Hint, Is.EqualTo("Continuing Kalam_2026-10-15_23-00-02Z as its next part (00:47:10 recorded so far)."));
        Assert.That(_ui.Screen, Does.Contain(
            "Kalam_2026-10-15_23-00-02Z stopped 4 minutes ago (00:47:10 recorded, no transcript yet). C continues it as its next part, N starts a new recording, Esc goes back."));
    }

    /// <summary>
    /// A recording of the name in its date folder is asked about and continued as one of the old layout is: C gives its
    /// files in that folder, which the run then writes into; the command line's --continue takes it too.
    /// </summary>
    [Test]
    public async Task A_recording_in_a_date_folder_is_asked_about_and_continued()
    {
        var files = WriteStopped(dateFolder: "2026-10-15 (2)");
        _ui.Type('c');

        var answer = await Question().AskAsync("Kalam", RecordingMode.Class, _recordings.Path);
        var resolved = Question().Resolve("Kalam", RecordingMode.Class, _recordings.Path, ContinueChoice.Continue);

        Assert.That(answer.Continue, Is.EqualTo(files));
        Assert.That(resolved.Continue, Is.EqualTo(files));
    }

    /// <summary>A recording made in parts says so in the question.</summary>
    [Test]
    public async Task The_question_names_the_parts_of_a_recording_continued_before()
    {
        WriteStopped(parts: 2);
        _ui.Type('n');

        await Question().AskAsync("Kalam", RecordingMode.Class, _recordings.Path);

        Assert.That(_ui.Screen, Does.Contain("(00:47:10 recorded in 2 parts, no transcript yet)"));
    }

    /// <summary>N starts a new recording instead.</summary>
    [Test]
    public async Task N_starts_a_new_recording()
    {
        WriteStopped();
        _ui.Type('N');

        var answer = await Question().AskAsync("Kalam", RecordingMode.Class, _recordings.Path);

        Assert.That(answer, Is.EqualTo(new ContinueAnswer(GoBack: false, Continue: null, Hint: null)));
    }

    /// <summary>Esc goes back without starting anything, and so does Ctrl+C.</summary>
    [TestCase('\u001b')]
    [TestCase('\u0003')]
    public async Task Esc_goes_back(char key)
    {
        WriteStopped();
        _ui.Type(key);

        var answer = await Question().AskAsync("Kalam", RecordingMode.Class, _recordings.Path);

        Assert.That(answer.GoBack, Is.True);
        Assert.That(answer.Continue, Is.Null);
    }

    /// <summary>Any other key is passed over until C, N or Esc.</summary>
    [Test]
    public async Task Other_keys_are_passed_over()
    {
        var files = WriteStopped();
        _ui.Type('x', 'q', '\r', 'c');

        var answer = await Question().AskAsync("Kalam", RecordingMode.Class, _recordings.Path);

        Assert.That(answer.Continue, Is.EqualTo(files));
    }

    /// <summary>With nothing to continue, nothing is asked and a new recording starts.</summary>
    [Test]
    public async Task Without_a_recording_to_continue_nothing_is_asked()
    {
        var answer = await Question().AskAsync("Kalam", RecordingMode.Class, _recordings.Path);

        Assert.That(answer, Is.EqualTo(new ContinueAnswer(false, null, null)));
        Assert.That(_ui.KeyReads, Is.Zero);
        Assert.That(_ui.Screen, Is.Empty);
    }

    /// <summary>On the command line, --continue continues the recording, says so, and asks nothing.</summary>
    [Test]
    public void On_the_command_line_continue_takes_the_recording()
    {
        var files = WriteStopped();

        var answer = Question().Resolve("Kalam", RecordingMode.Class, _recordings.Path, ContinueChoice.Continue);

        Assert.That(answer.Continue, Is.EqualTo(files));
        Assert.That(answer.Hint, Is.EqualTo("Continuing Kalam_2026-10-15_23-00-02Z as its next part (00:47:10 recorded so far)."));
        Assert.That(_ui.KeyReads, Is.Zero);
    }

    /// <summary>--continue with nothing to continue says so and starts a new recording, so a shortcut can always pass it.</summary>
    [Test]
    public void On_the_command_line_continue_without_a_recording_says_so_and_starts_a_new_one()
    {
        var answer = Question().Resolve("Kalam", RecordingMode.Class, _recordings.Path, ContinueChoice.Continue);

        Assert.That(answer.GoBack, Is.False);
        Assert.That(answer.Continue, Is.Null);
        Assert.That(answer.Hint, Is.EqualTo(
            "There is no recording of \"Kalam\" to continue (one that stopped in the last 60 minutes without a transcript), so a new recording starts."));
    }

    /// <summary>
    /// Neither --continue nor --new: a new recording starts at once, never a question or a wait, with a line saying how to
    /// continue the other one instead.
    /// </summary>
    [Test]
    public void On_the_command_line_neither_flag_gives_the_hint_and_never_asks()
    {
        WriteStopped();

        var answer = Question().Resolve("Kalam", RecordingMode.Class, _recordings.Path, ContinueChoice.Ask);

        Assert.That(answer.Continue, Is.Null);
        Assert.That(answer.Hint, Is.EqualTo(
            "Kalam_2026-10-15_23-00-02Z stopped 4 minutes ago without a transcript; to add to it instead, run the same command with --continue."));
        Assert.That(_ui.KeyReads, Is.Zero);
    }

    /// <summary>--new starts a new recording without a word.</summary>
    [Test]
    public void On_the_command_line_new_says_nothing()
    {
        WriteStopped();

        var answer = Question().Resolve("Kalam", RecordingMode.Class, _recordings.Path, ContinueChoice.New);

        Assert.That(answer, Is.EqualTo(new ContinueAnswer(false, null, null)));
    }

    /// <summary>A recording a run still owns, such as one being transcribed right after its capture ended, is not offered.</summary>
    [Test]
    public void A_recording_a_run_still_owns_is_not_offered()
    {
        var files = WriteStopped();
        WriteRun(files, state: RunState.Transcribing, processId: 4400);
        _runtime.Processes.Start(4400, TestRuns.ProcessStart);

        Assert.That(Question().Find("Kalam", RecordingMode.Class, _recordings.Path), Is.Null);
    }

    /// <summary>
    /// A recording whose run file says it was made in person, a single track as a class recording is, is not offered to a
    /// class recording; the run file of a class recording does not hold it back.
    /// </summary>
    [Test]
    public void A_recording_made_with_other_tracks_by_its_run_file_is_not_offered()
    {
        var files = WriteStopped();
        WriteRun(files, RecordingMode.Microphone);

        Assert.That(Question().Find("Kalam", RecordingMode.Class, _recordings.Path), Is.Null);
        Assert.That(Question().Find("Kalam", RecordingMode.Microphone, _recordings.Path)?.Files, Is.EqualTo(files));
    }

    /// <summary>With Recording:ContinueWithinMinutes at 0, nothing is ever offered, and --continue says why.</summary>
    [Test]
    public async Task With_continue_within_zero_minutes_nothing_is_offered()
    {
        WriteStopped();
        _options.Set(new RecordingOptions { OutputFolder = _recordings.Path, ContinueWithinMinutes = 0 });

        var asked = await Question().AskAsync("Kalam", RecordingMode.Class, _recordings.Path);
        var resolved = Question().Resolve("Kalam", RecordingMode.Class, _recordings.Path, ContinueChoice.Continue);

        Assert.That(asked.Continue, Is.Null);
        Assert.That(_ui.KeyReads, Is.Zero);
        Assert.That(resolved.Hint, Is.EqualTo("Recording:ContinueWithinMinutes is 0, so no recording is continued; a new recording starts."));
    }

    /// <summary>
    /// A recording that cannot be looked at now, such as one whose audio another program holds open without sharing, never
    /// keeps the class from being recorded: a new recording starts, with a warning.
    /// </summary>
    [Test]
    public async Task A_recording_that_cannot_be_read_now_does_not_keep_the_class_from_recording()
    {
        var files = WriteStopped();
        using var held = new FileStream(files.Segment(1), FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var answer = await Question().AskAsync("Kalam", RecordingMode.Class, _recordings.Path);

        Assert.That(answer, Is.EqualTo(new ContinueAnswer(false, null, null)));
        Assert.That(_log.Problems.Single(), Does.StartWith("Could not look for a recording of \"Kalam\" to continue"));
    }

    /// <summary>
    /// A YouTube video recorded into the course ten minutes before a class starts is never offered to be continued as the
    /// class's next part: the menu asks nothing and the class starts as a new recording.
    /// </summary>
    [Test]
    public async Task A_video_recorded_just_before_a_class_is_never_offered()
    {
        var video = WriteStopped();
        YouTube.VideoSourceFile.Write(video.Source, VideoSourceFileTests.Downloaded());

        var answer = await Question().AskAsync("Kalam", RecordingMode.Class, _recordings.Path);

        Assert.That(answer.Continue, Is.Null);
        Assert.That(answer.GoBack, Is.False);
        Assert.That(_ui.Screen, Does.Not.Contain("continues it"));
    }
}
