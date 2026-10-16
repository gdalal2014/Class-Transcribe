using System.Runtime.Versioning;
using ClassScribe.Audio;
using ClassScribe.Commands;
using ClassScribe.Settings;
using ClassScribe.Storage;
using Moq;

namespace ClassScribe.Tests;

/// <summary>The start-up menu, driven by a scripted keyboard, with the commands and the desktop mocked.</summary>
[TestFixture]
public class MenuCommandTests
{
    private static readonly AudioFormat Pcm16KMono = new(16000, 1, 16, false);

    private TempFolder _folder = null!;
    private TempFolder _settingsFolder = null!;
    private TestSettings _settings = null!;
    private FakeConsoleUi _ui = null!;
    private FakeCaptureProvider _devices = null!;
    private SourceChoice _choice = null!;
    private Mock<ICommandRunner> _commands = null!;
    private Mock<IDesktop> _desktop = null!;
    private RecordingLogger<MenuCommand> _log = null!;
    private RecordingLogger<ClassFolders> _foldersLog = null!;
    private RecordingLogger<RunGate> _gateLog = null!;

    /// <summary>Creates a temporary recordings folder, settings that can be used, the scripted keyboard, the simulated devices, the mocks and the logs to inspect.</summary>
    [SetUp]
    public void SetUp()
    {
        _folder = new TempFolder();
        _settingsFolder = new TempFolder();
        _settings = new TestSettings(_settingsFolder.Path);
        _ui = new FakeConsoleUi();
        _devices = new FakeCaptureProvider();
        _choice = new SourceChoice(TestOptions.Create(new RecordingOptions()));
        _commands = new Mock<ICommandRunner>();
        _commands.Setup(c => c.RecordAsync(It.IsAny<string>(), It.IsAny<bool>())).ReturnsAsync(0);
        _commands.Setup(c => c.TranscribeAsync(It.IsAny<TranscribeRequest>())).ReturnsAsync(0);
        _commands.Setup(c => c.RebuildAsync(It.IsAny<string>(), It.IsAny<bool>())).ReturnsAsync(0);
        _desktop = new Mock<IDesktop>();
        _log = new RecordingLogger<MenuCommand>();
        _foldersLog = new RecordingLogger<ClassFolders>();
        _gateLog = new RecordingLogger<RunGate>();
    }

    /// <summary>Deletes the temporary folders.</summary>
    [TearDown]
    public void TearDown()
    {
        _folder.Dispose();
        _settingsFolder.Dispose();
    }

    /// <summary>Runs the menu until it is left, failing the test rather than hanging if it never is.</summary>
    private Task<int> RunMenuAsync(string? recordings = null, ILoopbackCaptureProvider? devices = null)
    {
        var menu = new MenuCommand(
            _commands.Object, _ui, _settings.Gate(_gateLog), _settings.File, Folders(recordings), devices ?? _devices, _choice, new RecordingNamePrompt(_ui), _desktop.Object, TimeProvider.System, _log);
        return menu.RunAsync().WaitAsync(TimeSpan.FromSeconds(15));
    }

    /// <summary>
    /// The recordings folders as the menu sees them, with the recordings in <paramref name="recordings"/> or else in the
    /// temporary folder. What they log goes to <see cref="_foldersLog"/>.
    /// </summary>
    private ClassFolders Folders(string? recordings = null) =>
        new(TestOptions.Create(new RecordingOptions { OutputFolder = recordings ?? _folder.Path }), TimeProvider.System, _foldersLog);

    /// <summary>A drive letter, such as Z:\, that no drive uses on this machine, or null when every one is in use.</summary>
    private static string? MissingDrive() =>
        Enumerable.Range('D', 'Z' - 'D' + 1)
            .Select(letter => $"{(char)letter}:\\")
            .Reverse()
            .FirstOrDefault(drive => !Directory.Exists(drive));

    /// <summary>The line the menu shows right under the one that names the recordings folder.</summary>
    private string LineUnderTheFolder(string root)
    {
        var lines = _ui.Screen.Split(Environment.NewLine);
        var folderLine = Array.IndexOf(lines, $"Recordings and transcripts: {root}");
        Assert.That(folderLine, Is.GreaterThanOrEqualTo(0), "the menu names the recordings folder");
        return lines[folderLine + 1];
    }

    /// <summary>
    /// Opens the menu on <paramref name="recordings"/>, which cannot be used, and picks 3. Checks that the
    /// problem shows under the folder's line, and that choice 3 gives it as the reason it lists nothing.
    /// </summary>
    private async Task AssertTheProblemIsPointedOutAsync(string recordings)
    {
        var problem = Folders(recordings).DescribeRootProblem();
        Assert.That(problem, Is.Not.Null, "the folder really cannot be used");
        _ui.Type('3', 'q');

        Assert.That(await RunMenuAsync(recordings), Is.EqualTo(0));

        Assert.That(LineUnderTheFolder(recordings), Is.EqualTo($"WARNING  {problem}."));
        Assert.That(_ui.Screen, Does.Contain($"No recordings found. {problem}.").And.Not.Contain("No recordings to pick from"));
        _commands.VerifyNoOtherCalls();
    }

    /// <summary>
    /// Writes a recording into its folder under the recordings root, started at the given time; with
    /// <paramref name="withTranscript"/>, also a transcript and the answer it was made from, as a transcription that went
    /// well leaves them.
    /// </summary>
    private RecordingFiles WriteRecording(string name, DateTimeOffset started, bool withTranscript = false, int sampleRate = 16000, int samples = 1600)
    {
        var files = RecordingFiles.ForNew(_folder.File(name), name, started);
        Directory.CreateDirectory(files.Folder);
        TestAudio.WriteWav(files.Segment(1), sampleRate, new short[samples]);
        File.SetLastWriteTimeUtc(files.Segment(1), started.UtcDateTime);
        if (withTranscript)
        {
            File.WriteAllText(files.Json(), """{"text":"Hello world."}""");
            File.WriteAllText(files.Transcript(), "# Kalam");
        }

        return files;
    }

    /// <summary>How many times the menu has been shown.</summary>
    private int MenusShown() => _ui.Screen.Split(Environment.NewLine).Count(line => line == "  Q  Quit");

    /// <summary>What transcribing <paramref name="path"/> from the menu asks for: the menu has asked whatever it had to ask.</summary>
    private static TranscribeRequest FromMenu(string path) => new(path);

    /// <summary>The menu lists its choices, the settings file it reads, where the files go and what is recorded; Q leaves it.</summary>
    [Test]
    public async Task The_menu_shows_its_choices_and_Q_quits()
    {
        _ui.Type('q');

        var exitCode = await RunMenuAsync();

        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(_ui.Screen, Does.Contain("1  Record only")
            .And.Contain("2  Record and transcribe")
            .And.Contain("3  Transcribe a recent recording")
            .And.Contain("4  Transcribe a file")
            .And.Contain("5  Open the recordings folder")
            .And.Contain("6  Choose what to record: a device or an app")
            .And.Contain("Recording from: the device the class plays on")
            .And.Contain(_folder.Path));
        Assert.That(_ui.Screen.Split(Environment.NewLine), Has.Member($"Settings: {_settings.File.Path}"));
        Assert.That(LineUnderTheFolder(_folder.Path), Does.StartWith("Recording from:"), "a usable folder brings no warning");
        _commands.VerifyNoOtherCalls();
    }

    /// <summary>
    /// Every choice reads the settings first. While they cannot be used, the choice does not run, whichever it is: the
    /// log says what is wrong and to choose again, and the menu comes back.
    /// </summary>
    [Test]
    public async Task No_choice_runs_while_the_settings_cannot_be_used()
    {
        WriteRecording("Kalam", new DateTimeOffset(2026, 10, 14, 19, 30, 0, TimeSpan.Zero));
        _settings.Write("""{ "Recording": { "KeepScreenOn": "Sometimes" } }""");
        _ui.TypeLine("Kalam");
        _ui.TypeLine("Kalam");
        _ui.Type('1', '2', '3', '4', '5', '6', 'q');

        Assert.That(await RunMenuAsync(), Is.EqualTo(0));

        _commands.VerifyNoOtherCalls();
        _desktop.VerifyNoOtherCalls();
        Assert.That(_ui.Screen, Does.Not.Contain("Name for this recording").And.Not.Contain("Recent recordings"));
        Assert.That(_gateLog.Problems.Count(problem => problem.EndsWith("then choose again", StringComparison.Ordinal)), Is.EqualTo(6));
        Assert.That(MenusShown(), Is.EqualTo(7), "the menu came back after each refused choice");
    }

    /// <summary>
    /// Settings that cannot be used when ClassScribe starts are shown in the menu's header, under the file's name, before
    /// any choice is made: each problem, then that no choice runs until the file is fixed, so the folder and the source
    /// shown below are not taken for the file's. Once the file is fixed, the next choice runs and the header is clear again.
    /// </summary>
    [Test]
    public async Task Settings_that_cannot_be_used_at_start_are_shown_in_the_menu_until_fixed()
    {
        _settings = new TestSettings(_settingsFolder.Path, """{ "Recording": { "FlushSeconds": "five" } }""");
        var menu = RunMenuAsync();
        await Wait.UntilAsync(() => MenusShown() == 1);
        var atStart = _ui.Screen.Split(Environment.NewLine);
        _settings.Write("""{ "Recording": { "FlushSeconds": 5 } }""");
        _ui.Type('5', 'q');

        Assert.That(await menu, Is.EqualTo(0));

        var settingsLine = Array.IndexOf(atStart, $"Settings: {_settings.File.Path}");
        Assert.That(settingsLine, Is.GreaterThanOrEqualTo(0), "the menu names the file it reads");
        Assert.That(atStart[(settingsLine + 1)..(settingsLine + 4)], Is.EqualTo(new[]
        {
            "ERROR  Recording:FlushSeconds is \"five\", which is not a whole number, such as 60.",
            "ERROR  No choice runs until the file is fixed. Until then ClassScribe keeps its last usable settings, or the defaults.",
            $"Recordings and transcripts: {_folder.Path}"
        }));
        _desktop.Verify(d => d.OpenFolder(_folder.Path), Times.Once, "the fixed file let the choice run");
        var lines = _ui.Screen.Split(Environment.NewLine);
        Assert.That(lines[Array.LastIndexOf(lines, "ClassScribe")..], Has.None.StartsWith("ERROR"), "the menu shown after it has nothing to say about the settings");
    }

    /// <summary>Once the file is fixed, the next choice runs: nothing has to be restarted.</summary>
    [Test]
    public async Task The_next_choice_runs_once_the_settings_are_fixed()
    {
        _settings.Write("""{ "Recording": { "FlushSeconds": "five" } }""");
        _ui.Type('5');
        var menu = RunMenuAsync();
        await Wait.UntilAsync(() => _gateLog.Problems.Count > 0);
        _settings.Write("""{ "Recording": { "FlushSeconds": 5 } }""");

        _ui.Type('5', 'q');
        Assert.That(await menu, Is.EqualTo(0));

        _desktop.Verify(d => d.OpenFolder(_folder.Path), Times.Once, "the first 5 was refused, the second ran");
    }

    /// <summary>
    /// A recordings folder on a drive that is not there, such as an unplugged USB drive, is pointed out under
    /// the folder's line, and choice 3 gives that reason rather than saying there are no recordings yet.
    /// </summary>
    [Test]
    public async Task A_recordings_folder_on_a_missing_drive_is_pointed_out()
    {
        var drive = MissingDrive();
        Assume.That(drive, Is.Not.Null, "every drive letter is in use on this machine");

        await AssertTheProblemIsPointedOutAsync(Path.Combine(drive!, "Classes"));
    }

    /// <summary>
    /// A file where the recordings folder should be is pointed out under the folder's line, and choice 3 gives
    /// that reason rather than saying there are no recordings yet.
    /// </summary>
    [Test]
    public async Task A_file_in_place_of_the_recordings_folder_is_pointed_out()
    {
        var inTheWay = _folder.File("Recordings");
        File.WriteAllText(inTheWay, "not a folder");

        await AssertTheProblemIsPointedOutAsync(inTheWay);
    }

    /// <summary>1 and 2 ask for the recording's name, then record only, or record and transcribe. The menu comes back afterwards.</summary>
    [TestCase('1', false)]
    [TestCase('2', true)]
    public async Task The_record_choices_record_under_the_name_given(char choice, bool transcribe)
    {
        _ui.TypeLine("Kalam lesson 12");
        _ui.Type(choice, 'q');

        await RunMenuAsync();

        Assert.That(_ui.Screen, Does.Contain("Name for this recording"));
        _commands.Verify(c => c.RecordAsync("Kalam lesson 12", transcribe), Times.Once);
        _commands.VerifyNoOtherCalls();
    }

    /// <summary>A recording needs a name: an empty line asks again, and the name is cleaned for use as a file name.</summary>
    [Test]
    public async Task A_recording_needs_a_name()
    {
        _ui.TypeLine("");
        _ui.TypeLine("   ");
        _ui.TypeLine(" Kalam: lesson 12 ");
        _ui.Type('1', 'q');

        await RunMenuAsync();

        Assert.That(_ui.Screen, Does.Contain("A name is needed"));
        _commands.Verify(c => c.RecordAsync("Kalam lesson 12", false), Times.Once);
    }

    /// <summary>Esc at the name prompt goes back to the menu without recording.</summary>
    [Test]
    public async Task Esc_at_the_name_prompt_goes_back()
    {
        _ui.TypeLine(null);
        _ui.Type('2', 'q');

        await RunMenuAsync();

        _commands.VerifyNoOtherCalls();
    }

    /// <summary>Enter and keys that are not choices do nothing, so a stray key press starts nothing.</summary>
    [Test]
    public async Task Enter_and_other_keys_do_nothing()
    {
        _ui.Type('\r', 'x', '9', 'q');

        Assert.That(await RunMenuAsync(), Is.EqualTo(0));
        _commands.VerifyNoOtherCalls();
    }

    /// <summary>Ctrl+C at the menu leaves it, like Q.</summary>
    [Test]
    public async Task Ctrl_C_at_the_menu_quits()
    {
        _ui.Type('\u0003');

        Assert.That(await RunMenuAsync(), Is.EqualTo(0));
        _commands.VerifyNoOtherCalls();
    }

    /// <summary>3 lists recent recordings, newest first, and transcribes the one picked by its number.</summary>
    [Test]
    public async Task Three_transcribes_the_recent_recording_picked()
    {
        var older = WriteRecording("Kalam", new DateTimeOffset(2026, 10, 11, 14, 5, 0, TimeSpan.Zero));
        var newer = WriteRecording("Fiqh", new DateTimeOffset(2026, 10, 14, 19, 30, 0, TimeSpan.Zero));
        _ui.Type('3', '2', 'q');

        await RunMenuAsync();

        _commands.Verify(c => c.TranscribeAsync(FromMenu(older.Segment(1))), Times.Once);
        Assert.That(_ui.Screen.IndexOf(newer.BaseName, StringComparison.Ordinal),
            Is.LessThan(_ui.Screen.IndexOf(older.BaseName, StringComparison.Ordinal)), "newest first");
    }

    /// <summary>A recording that already has a transcript is billed again, so it is only redone after a yes.</summary>
    [Test]
    public async Task A_recording_with_a_transcript_is_only_redone_after_a_yes()
    {
        var done = WriteRecording("Kalam", new DateTimeOffset(2026, 10, 14, 19, 30, 0, TimeSpan.Zero), withTranscript: true);
        _ui.Type('3', '1', 'n', '3', '1', 'y', 'q');

        await RunMenuAsync();

        _commands.Verify(c => c.TranscribeAsync(FromMenu(done.Segment(1))), Times.Once);
        Assert.That(_ui.Screen, Does.Contain("billed again"));
    }

    /// <summary>
    /// For a recording that already has a transcript, choice 3 offers the free rebuild before the billed new try: R
    /// rebuilds it from ElevenLabs' saved answer, Y transcribes it again, and any other key goes back without either.
    /// </summary>
    [TestCase("r", "rebuild")]
    [TestCase("R", "rebuild")]
    [TestCase("y", "transcribe")]
    [TestCase("n", "nothing")]
    [TestCase("Esc", "nothing")]
    public async Task Three_offers_R_to_rebuild_and_Y_to_transcribe_a_transcribed_recording_again(string key, string expected)
    {
        var done = WriteRecording("Kalam", new DateTimeOffset(2026, 10, 14, 19, 30, 0, TimeSpan.Zero), withTranscript: true);
        _ui.Type('3', '1', key == "Esc" ? '\u001b' : key[0], 'q');

        await RunMenuAsync();

        Assert.That(_ui.Screen.Split(Environment.NewLine), Has.Member(
            "This recording has a transcript. R rebuilds it from ElevenLabs' saved answer, with the current marks and Transcript settings (free, nothing is uploaded). Y transcribes it again (billed again). Any other key goes back."));
        _commands.Verify(c => c.RebuildAsync(done.Segment(1), true), expected == "rebuild" ? Times.Once() : Times.Never());
        _commands.Verify(c => c.TranscribeAsync(FromMenu(done.Segment(1))), expected == "transcribe" ? Times.Once() : Times.Never());
        _commands.VerifyNoOtherCalls();
    }

    /// <summary>
    /// For a recording with a transcript whose saved answer from ElevenLabs is no longer there, as when it was deleted by
    /// hand, choice 3 does not offer R, which would find nothing to rebuild from: it says why and asks only for Y, so R goes
    /// back as any other key does.
    /// </summary>
    [TestCase("r", "nothing")]
    [TestCase("y", "transcribe")]
    public async Task Three_offers_only_Y_when_the_saved_answer_of_a_transcript_is_gone(string key, string expected)
    {
        var done = WriteRecording("Kalam", new DateTimeOffset(2026, 10, 14, 19, 30, 0, TimeSpan.Zero), withTranscript: true);
        File.Delete(done.Json());
        _ui.Type('3', '1', key[0], 'q');

        await RunMenuAsync();

        Assert.That(_ui.Screen.Split(Environment.NewLine), Has.Member(
            "This recording has a transcript, but its saved answer from ElevenLabs is no longer there, so it cannot be rebuilt for free. Y transcribes it again (billed again). Any other key goes back."));
        _commands.Verify(c => c.TranscribeAsync(FromMenu(done.Segment(1))), expected == "transcribe" ? Times.Once() : Times.Never());
        _commands.VerifyNoOtherCalls();
    }

    /// <summary>
    /// For a recording with a transcript whose saved answer is no longer there but whose answer to another try was paid for
    /// and kept apart, as after R laid such an answer out, R makes a new transcript from that kept answer.
    /// </summary>
    [Test]
    public async Task Three_with_R_rebuilds_a_transcript_from_the_kept_answer_when_the_saved_one_is_gone()
    {
        var done = WriteRecording("Kalam", new DateTimeOffset(2026, 10, 14, 19, 30, 0, TimeSpan.Zero), withTranscript: true);
        File.Delete(done.Json());
        File.WriteAllText(done.UnreadAnswer(), "{}");
        _ui.Type('3', '1', 'r', 'q');

        await RunMenuAsync();

        Assert.That(_ui.Screen.Split(Environment.NewLine), Has.Member(
            $"This recording has a transcript. R makes a new transcript from ElevenLabs' answer kept in {done.UnreadAnswer()}, with the current marks and Transcript settings (free, nothing is uploaded). Y transcribes it again (billed again). Any other key goes back."));
        _commands.Verify(c => c.RebuildAsync(done.UnreadAnswer(), true), Times.Once);
        _commands.VerifyNoOtherCalls();
    }

    /// <summary>
    /// A recording whose answer was paid for but did not become a transcript is listed with "answer kept". Transcribing
    /// it again is billed again, so it is only redone after a yes, and the question says where the kept answer is.
    /// </summary>
    [Test]
    public async Task A_recording_with_a_kept_answer_is_only_redone_after_a_yes()
    {
        var kept = WriteRecording("Kalam", new DateTimeOffset(2026, 10, 14, 19, 30, 0, TimeSpan.Zero));
        File.WriteAllText(kept.UnreadAnswer(), "{}");
        _ui.Type('3', '1', 'n', '3', '1', 'y', 'q');

        await RunMenuAsync();

        _commands.Verify(c => c.TranscribeAsync(FromMenu(kept.Segment(1))), Times.Once);
        Assert.That(_ui.Screen, Does.Contain($"  1  {kept.BaseName}  00:00:00  answer kept")
            .And.Contain($"kept in {kept.UnreadAnswer()}")
            .And.Contain("billed again"));
    }

    /// <summary>For a recording whose answer is kept without a transcript, R lays that very answer out as a transcript, for free.</summary>
    [Test]
    public async Task Three_with_R_lays_a_kept_answer_out_for_free()
    {
        var kept = WriteRecording("Kalam", new DateTimeOffset(2026, 10, 14, 19, 30, 0, TimeSpan.Zero));
        File.WriteAllText(kept.UnreadAnswer(), "{}");
        _ui.Type('3', '1', 'r', 'q');

        await RunMenuAsync();

        Assert.That(_ui.Screen.Split(Environment.NewLine), Has.Member(
            $"ElevenLabs' answer to an earlier try, which has been paid for, is kept in {kept.UnreadAnswer()}: look there for the transcript's text first, because transcribing it again is billed again. R lays that answer out as a transcript (free, nothing is uploaded). Y transcribes it again (billed again). Any other key goes back."));
        _commands.Verify(c => c.RebuildAsync(kept.UnreadAnswer(), true), Times.Once);
        _commands.VerifyNoOtherCalls();
    }

    /// <summary>
    /// A recording that has a transcript and also an answer kept from another try is listed as "transcript ready", and
    /// the question before a new try is the one for a recording with a transcript. A no goes back without transcribing.
    /// </summary>
    [Test]
    public async Task A_recording_with_a_transcript_and_a_kept_answer_counts_as_transcribed()
    {
        var both = WriteRecording("Kalam", new DateTimeOffset(2026, 10, 14, 19, 30, 0, TimeSpan.Zero), withTranscript: true);
        File.WriteAllText(both.UnreadAnswer(), "{}");
        _ui.Type('3', '1', 'n', 'q');

        await RunMenuAsync();

        var lines = _ui.Screen.Split(Environment.NewLine);
        Assert.That(lines, Has.Member($"  1  {both.BaseName}  00:00:00  transcript ready"));
        Assert.That(lines, Has.Member("This recording has a transcript. R rebuilds it from ElevenLabs' saved answer, with the current marks and Transcript settings (free, nothing is uploaded). Y transcribes it again (billed again). Any other key goes back."));
        Assert.That(_ui.Screen, Does.Not.Contain("answer kept").And.Not.Contain("kept in"));
        _commands.VerifyNoOtherCalls();
    }

    /// <summary>
    /// A recording that is being recorded now, in another window, is listed as such, and picking it says to transcribe it
    /// once it has stopped: what was sent now would stop short. Nothing is sent, and the menu comes back.
    /// </summary>
    [Test]
    public async Task Three_refuses_a_recording_that_is_being_recorded_now()
    {
        var recordingNow = WriteRecording("Kalam", new DateTimeOffset(2026, 10, 14, 19, 30, 0, TimeSpan.Zero));
        _ui.Type('3', '1', 'q');

        using (new FileStream(recordingNow.Segment(1), FileMode.Open, FileAccess.Write, FileShare.Read))
            Assert.That(await RunMenuAsync(), Is.EqualTo(0));

        var lines = _ui.Screen.Split(Environment.NewLine);
        Assert.That(lines, Has.Member($"  1  {recordingNow.BaseName}  00:00:00  being recorded now"));
        Assert.That(lines, Has.Member($"{recordingNow.BaseName} is being recorded now; transcribe it once it has stopped."));
        Assert.That(MenusShown(), Is.EqualTo(2), "the menu came back");
        _commands.VerifyNoOtherCalls();
    }

    /// <summary>A recording of a day or more is listed with all its hours, not the hours past the last full day.</summary>
    [Test]
    public async Task A_recording_of_a_day_or_more_is_listed_with_all_its_hours()
    {
        WriteRecording("Overnight", new DateTimeOffset(2026, 10, 14, 19, 30, 0, TimeSpan.Zero), sampleRate: 1, samples: 25 * 3600);
        _ui.Type('3', '\u001b', 'q');

        await RunMenuAsync();

        Assert.That(_ui.Screen, Does.Contain("  25:00:00  "));
    }

    /// <summary>Esc leaves the list of recordings without transcribing anything.</summary>
    [Test]
    public async Task Esc_leaves_the_recording_list()
    {
        WriteRecording("Kalam", new DateTimeOffset(2026, 10, 14, 19, 30, 0, TimeSpan.Zero));
        _ui.Type('3', '\u001b', 'q');

        await RunMenuAsync();

        _commands.VerifyNoOtherCalls();
    }

    /// <summary>Without any recording the menu says so instead of showing an empty list, and names the folder it looked in.</summary>
    [Test]
    public async Task Without_recordings_the_menu_says_so()
    {
        _ui.Type('3', 'q');

        await RunMenuAsync();

        Assert.That(_ui.Screen, Does.Contain($"No recordings to pick from in {_folder.Path}."));
        _commands.VerifyNoOtherCalls();
    }

    /// <summary>4 transcribes a file dragged into the window; the quotes Windows adds around the path are removed.</summary>
    [Test]
    public async Task Four_transcribes_a_file_dragged_into_the_window()
    {
        var video = _folder.File("Lecture 5.mp4");
        File.WriteAllBytes(video, [1, 2, 3]);
        _ui.TypeLine($"\"{video}\"");
        _ui.Type('4', 'q');

        await RunMenuAsync();

        _commands.Verify(c => c.TranscribeAsync(FromMenu(video)), Times.Once);
    }

    /// <summary>
    /// A recording dragged into choice 4 that already has a transcript is billed again, so 4 asks first, as 3 does: R
    /// rebuilds it for free from ElevenLabs' saved answer, Y transcribes it again, and any other key goes back without either.
    /// </summary>
    [TestCase("r", "rebuild")]
    [TestCase("y", "transcribe")]
    [TestCase("n", "nothing")]
    public async Task Four_offers_R_to_rebuild_and_Y_to_transcribe_a_transcribed_recording_again(string key, string expected)
    {
        var done = WriteRecording("Kalam", new DateTimeOffset(2026, 10, 14, 19, 30, 0, TimeSpan.Zero), withTranscript: true);
        _ui.TypeLine($"\"{done.Segment(1)}\"");
        _ui.Type('4', key[0], 'q');

        await RunMenuAsync();

        Assert.That(_ui.Screen.Split(Environment.NewLine), Has.Member(
            "This recording has a transcript. R rebuilds it from ElevenLabs' saved answer, with the current marks and Transcript settings (free, nothing is uploaded). Y transcribes it again (billed again). Any other key goes back."));
        _commands.Verify(c => c.RebuildAsync(done.Segment(1), true), expected == "rebuild" ? Times.Once() : Times.Never());
        _commands.Verify(c => c.TranscribeAsync(FromMenu(done.Segment(1))), expected == "transcribe" ? Times.Once() : Times.Never());
        _commands.VerifyNoOtherCalls();
    }

    /// <summary>
    /// A folder typed into choice 4 means its newest recording, as transcribe takes it, so when that one has a transcript, 4
    /// asks first too: a no sends nothing, and a yes sends the folder, as typed.
    /// </summary>
    [Test]
    public async Task Four_asks_first_for_a_folder_whose_newest_recording_has_a_transcript()
    {
        WriteRecording("Kalam", new DateTimeOffset(2026, 10, 11, 14, 5, 0, TimeSpan.Zero));
        var newest = WriteRecording("Kalam", new DateTimeOffset(2026, 10, 14, 19, 30, 0, TimeSpan.Zero), withTranscript: true);
        _ui.TypeLine(newest.Folder);
        _ui.TypeLine(newest.Folder);
        _ui.Type('4', 'n', '4', 'y', 'q');

        await RunMenuAsync();

        Assert.That(_ui.Screen.Split(Environment.NewLine).Count(line => line.StartsWith("This recording has a transcript.", StringComparison.Ordinal)), Is.EqualTo(2));
        _commands.Verify(c => c.TranscribeAsync(FromMenu(newest.Folder)), Times.Once);
        _commands.VerifyNoOtherCalls();
    }

    /// <summary>For a recording dragged into choice 4 whose paid answer is kept without a transcript, R lays that answer out for free, as in choice 3.</summary>
    [Test]
    public async Task Four_with_R_lays_a_kept_answer_out_for_free()
    {
        var kept = WriteRecording("Kalam", new DateTimeOffset(2026, 10, 14, 19, 30, 0, TimeSpan.Zero));
        File.WriteAllText(kept.UnreadAnswer(), "{}");
        _ui.TypeLine(kept.Segment(1));
        _ui.Type('4', 'r', 'q');

        await RunMenuAsync();

        Assert.That(_ui.Screen, Does.Contain($"kept in {kept.UnreadAnswer()}"));
        _commands.Verify(c => c.RebuildAsync(kept.UnreadAnswer(), true), Times.Once);
        _commands.VerifyNoOtherCalls();
    }

    /// <summary>
    /// A recording dragged into choice 4 while it is being recorded, in another window, is not sent, as in choice 3: what
    /// was sent now would stop short. The menu comes back.
    /// </summary>
    [Test]
    public async Task Four_refuses_a_recording_that_is_being_recorded_now()
    {
        var recordingNow = WriteRecording("Kalam", new DateTimeOffset(2026, 10, 14, 19, 30, 0, TimeSpan.Zero));
        _ui.TypeLine(recordingNow.Segment(1));
        _ui.Type('4', 'q');

        using (new FileStream(recordingNow.Segment(1), FileMode.Open, FileAccess.Write, FileShare.Read))
            Assert.That(await RunMenuAsync(), Is.EqualTo(0));

        Assert.That(_ui.Screen.Split(Environment.NewLine), Has.Member($"{recordingNow.BaseName} is being recorded now; transcribe it once it has stopped."));
        Assert.That(MenusShown(), Is.EqualTo(2), "the menu came back");
        _commands.VerifyNoOtherCalls();
    }

    /// <summary>A recording without a transcript or a kept answer, dragged into choice 4, is sent at once: nothing about it is billed twice.</summary>
    [Test]
    public async Task Four_sends_a_recording_without_a_transcript_at_once()
    {
        var recording = WriteRecording("Kalam", new DateTimeOffset(2026, 10, 14, 19, 30, 0, TimeSpan.Zero));
        _ui.TypeLine(recording.Segment(1));
        _ui.Type('4', 'q');

        Assert.That(await RunMenuAsync(), Is.EqualTo(0));

        _commands.Verify(c => c.TranscribeAsync(FromMenu(recording.Segment(1))), Times.Once);
        _commands.VerifyNoOtherCalls();
        Assert.That(_ui.Screen, Does.Not.Contain("billed again"));
    }

    /// <summary>An empty line or Esc at the file prompt goes back to the menu.</summary>
    [Test]
    public async Task Four_with_an_empty_line_or_Esc_goes_back()
    {
        _ui.TypeLine("");
        _ui.TypeLine(null);
        _ui.Type('4', '4', 'q');

        await RunMenuAsync();

        _commands.VerifyNoOtherCalls();
    }

    /// <summary>A path that leads nowhere is reported, and nothing is sent.</summary>
    [Test]
    public async Task Four_with_a_path_that_does_not_exist_says_so()
    {
        _ui.TypeLine(_folder.File("missing.mp4"));
        _ui.Type('4', 'q');

        await RunMenuAsync();

        Assert.That(_ui.Screen, Does.Contain("Nothing found"));
        _commands.VerifyNoOtherCalls();
    }

    /// <summary>5 opens the recordings folder, creating it if it is not there yet.</summary>
    [Test]
    public async Task Five_opens_the_recordings_folder()
    {
        var recordings = _folder.File("Recordings");
        _ui.Type('5', 'q');

        await RunMenuAsync(recordings);

        Assert.That(Directory.Exists(recordings), Is.True);
        _desktop.Verify(d => d.OpenFolder(recordings), Times.Once);
    }

    /// <summary>6 lists the connected output devices, marking the default one and any that plays, and pins the one picked.</summary>
    [Test]
    public async Task Six_lists_the_devices_and_pins_the_one_picked()
    {
        _devices.Enqueue(new FakeAudioCapture(Pcm16KMono, "Speakers (Realtek(R) Audio)"));
        _devices.Enqueue(new FakeAudioCapture(Pcm16KMono, "Headphones (Pixel Buds Pro 2)"), level: 0.3f);
        _ui.Type('6', '2', 'q');

        await RunMenuAsync();

        Assert.That(_choice.Device, Is.EqualTo("Headphones (Pixel Buds Pro 2)"));
        Assert.That(_ui.Screen, Does.Contain("1  Speakers (Realtek(R) Audio)  (default)")
            .And.Contain("2  Headphones (Pixel Buds Pro 2)  (playing)")
            .And.Contain("Recording from: the output device \"Headphones (Pixel Buds Pro 2)\"")
            .And.Contain("Recording:Device"), "the menu says how to make the choice permanent");
    }

    /// <summary>The list goes on with the apps that have sound open, and picking one records that app alone.</summary>
    [Test]
    public async Task Six_lists_the_apps_with_sound_and_pins_the_one_picked()
    {
        _devices.Enqueue(new FakeAudioCapture(Pcm16KMono, "Speakers"));
        _devices.AddApp(4242, "chrome", "Speakers", level: 0.4f);
        _devices.AddApp(5151, "msedge", "Speakers");
        _ui.Type('6', '2', 'q');

        await RunMenuAsync();

        Assert.That((_choice.App, _choice.Device), Is.EqualTo(("chrome", (string?)null)));
        Assert.That(_ui.Screen, Does.Contain("2  chrome  (app on Speakers, playing)")
            .And.Contain("3  msedge  (app on Speakers)")
            .And.Contain("Recording from: the app \"chrome\"")
            .And.Contain("Recording:App"), "the menu says how to make the choice permanent");
    }

    /// <summary>
    /// A in the list goes back to automatic, and like a device or an app picked there, that holds until ClassScribe is
    /// closed, whatever the settings say: the menu says so, and how to keep it.
    /// </summary>
    [Test]
    public async Task Six_with_A_goes_back_to_automatic()
    {
        _devices.Enqueue(new FakeAudioCapture(Pcm16KMono, "Speakers"));
        _choice.App = "chrome";
        _ui.Type('6', 'a', 'q');

        await RunMenuAsync();

        Assert.That((_choice.App, _choice.Device), Is.EqualTo(((string?)null, (string?)null)));
        Assert.That(_ui.Screen, Does.Contain("Recording from: the device the class plays on"));
        Assert.That(_ui.Screen.Split(Environment.NewLine), Has.Member(
            $"Recording from the device the class plays on until ClassScribe is closed, whatever the settings say. To keep that, leave Recording:Device and Recording:App empty in {_settings.File.Path}."));
    }

    /// <summary>Esc leaves the list with the choice as it was.</summary>
    [Test]
    public async Task Six_with_Esc_keeps_the_choice()
    {
        _devices.Enqueue(new FakeAudioCapture(Pcm16KMono, "Speakers"));
        _choice.Device = "Headphones";
        _ui.Type('6', '\u001b', 'q');

        await RunMenuAsync();

        Assert.That(_choice.Device, Is.EqualTo("Headphones"));
    }

    /// <summary>Without any output device the list says so instead of showing nothing.</summary>
    [Test]
    public async Task Six_without_devices_says_so()
    {
        _ui.Type('6', 'q');

        await RunMenuAsync();

        Assert.That(_ui.Screen, Does.Contain("No output device is connected"));
    }

    /// <summary>A choice that fails is reported, and the menu carries on.</summary>
    [Test]
    public async Task A_failed_choice_returns_to_the_menu()
    {
        _commands.Setup(c => c.RecordAsync("Kalam", true)).ThrowsAsync(new InvalidOperationException("Windows reports no audio output device to record."));
        _ui.TypeLine("Kalam");
        _ui.TypeLine("Kalam");
        _ui.Type('2', '1', 'q');

        Assert.That(await RunMenuAsync(), Is.EqualTo(0));
        _commands.Verify(c => c.RecordAsync("Kalam", false), Times.Once, "the menu was still there for the next choice");
        Assert.That(_log.Problems, Has.Count.EqualTo(1));
    }

    /// <summary>A choice cancelled with Ctrl+C returns to the menu.</summary>
    [Test]
    public async Task A_cancelled_choice_returns_to_the_menu()
    {
        _commands.Setup(c => c.RecordAsync("Kalam", true)).ThrowsAsync(new OperationCanceledException());
        _ui.TypeLine("Kalam");
        _ui.Type('2', 'q');

        Assert.That(await RunMenuAsync(), Is.EqualTo(0));
    }

    /// <summary>
    /// Keys typed while a choice runs, such as a Q pressed during the upload, were not meant for the menu
    /// that comes back afterwards: it waits for a key pressed after it is shown.
    /// </summary>
    [Test]
    public async Task Keys_typed_while_a_choice_runs_are_not_taken_as_the_next_choice()
    {
        _commands.Setup(c => c.RecordAsync("Kalam", true)).Callback(() => _ui.TypeAhead('q', '1')).ReturnsAsync(0);
        _ui.TypeLine("Kalam");
        _ui.Type('2', '5', 'q');

        Assert.That(await RunMenuAsync(), Is.EqualTo(0));
        _desktop.Verify(d => d.OpenFolder(_folder.Path), Times.Once, "the menu waited for the 5 pressed after it came back");
        _commands.Verify(c => c.RecordAsync(It.IsAny<string>(), false), Times.Never);
    }

    /// <summary>
    /// A recording that cannot be read while the recent ones are listed (another program holds it open) is left out
    /// with a warning, and the others are still offered: here the readable one becomes number 1.
    /// </summary>
    [Test]
    public async Task Three_offers_the_recordings_that_can_be_read_when_one_cannot_be()
    {
        var held = WriteRecording("Kalam", new DateTimeOffset(2026, 10, 14, 19, 30, 0, TimeSpan.Zero));
        var readable = WriteRecording("Fiqh", new DateTimeOffset(2026, 10, 11, 14, 5, 0, TimeSpan.Zero));
        _ui.Type('3', '1', 'q');

        using (new FileStream(held.Segment(1), FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.That(await RunMenuAsync(), Is.EqualTo(0));

        _commands.Verify(c => c.TranscribeAsync(FromMenu(readable.Segment(1))), Times.Once);
        _commands.VerifyNoOtherCalls();
        Assert.That(_foldersLog.Problems, Has.Count.EqualTo(1));
        Assert.That(_foldersLog.Problems[0], Does.Contain(held.BaseName));
    }

    /// <summary>
    /// When the only recording cannot be read (another program holds it open), it is left out with a warning, and
    /// choice 3 says there is nothing to pick from, in words that do not deny that the recording exists.
    /// </summary>
    [Test]
    public async Task Three_with_only_a_recording_that_cannot_be_read_has_nothing_to_offer()
    {
        var held = WriteRecording("Kalam", new DateTimeOffset(2026, 10, 14, 19, 30, 0, TimeSpan.Zero));
        _ui.Type('3', 'q');

        using (new FileStream(held.Segment(1), FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.That(await RunMenuAsync(), Is.EqualTo(0));

        Assert.That(_ui.Screen, Does.Contain($"No recordings to pick from in {_folder.Path}."));
        Assert.That(_foldersLog.Problems, Has.Some.Contains(held.BaseName));
        _commands.VerifyNoOtherCalls();
    }

    /// <summary>
    /// A recordings folder ClassScribe may not look into makes choice 3 fail, since no list can be made. That is
    /// reported, and the menu comes back for the next choice.
    /// </summary>
    [Test, Platform("Win")]
    [SupportedOSPlatform("windows")]
    public async Task Three_with_a_recordings_folder_that_cannot_be_looked_into_returns_to_the_menu()
    {
        WriteRecording("Kalam", new DateTimeOffset(2026, 10, 14, 19, 30, 0, TimeSpan.Zero));
        _ui.Type('3', 'q');

        int exitCode;
        using (Denied.ListingOf(_folder.Path))
        {
            Assume.That(() => Directory.GetDirectories(_folder.Path), Throws.InstanceOf<UnauthorizedAccessException>(), "the folder could not be shut to this user");
            exitCode = await RunMenuAsync();
        }

        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(_log.Problems, Is.EqualTo(new[] { "Stopped because of an error" }));
        Assert.That(MenusShown(), Is.EqualTo(2), "the menu came back after the failed choice");
        _commands.VerifyNoOtherCalls();
    }

    /// <summary>A recordings folder that cannot be created (a file is in its way) is reported, and the menu carries on.</summary>
    [Test]
    public async Task Five_with_a_recordings_folder_that_cannot_be_created_returns_to_the_menu()
    {
        var inTheWay = _folder.File("Recordings");
        File.WriteAllText(inTheWay, "not a folder");
        _ui.Type('5', '5', 'q');

        Assert.That(await RunMenuAsync(inTheWay), Is.EqualTo(0));
        Assert.That(_log.Problems, Has.Count.EqualTo(2));
        Assert.That(MenusShown(), Is.EqualTo(3));
    }

    /// <summary>Output devices that cannot be listed, such as while Windows' audio service restarts, are reported, and the menu carries on.</summary>
    [Test]
    public async Task Six_when_the_devices_cannot_be_listed_returns_to_the_menu()
    {
        var devices = new Mock<ILoopbackCaptureProvider>();
        devices.Setup(d => d.GetOutputDevices()).Throws(new System.Runtime.InteropServices.COMException("The audio service is not running."));
        _ui.Type('6', '5', 'q');

        Assert.That(await RunMenuAsync(devices: devices.Object), Is.EqualTo(0));
        Assert.That(_log.Problems, Has.Count.EqualTo(1));
        _desktop.Verify(d => d.OpenFolder(_folder.Path), Times.Once, "the menu was still there for the next choice");
    }
}
