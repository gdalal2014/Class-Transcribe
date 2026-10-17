using System.Runtime.Versioning;
using System.Text.Json;
using ClassScribe.Audio;
using ClassScribe.Commands;
using ClassScribe.Desktop;
using ClassScribe.Recorder;
using ClassScribe.Settings;
using ClassScribe.Storage;
using ClassScribe.Transcription;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace ClassScribe.Tests;

/// <summary>
/// The start-up menu, driven by a scripted keyboard, with the commands, the report at the start and the desktop mocked.
/// The menu reaches every command through the runner, and the runs only through the report.
/// </summary>
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
    private Mock<IStartupReport> _report = null!;
    private Mock<IDesktop> _desktop = null!;
    private RecordingLogger<MenuCommand> _log = null!;
    private RecordingLogger<ClassFolders> _foldersLog = null!;
    private RecordingLogger<RunGate> _gateLog = null!;

    /// <summary>
    /// Creates a temporary recordings folder, settings that can be used, the scripted keyboard, the simulated devices, the
    /// mocks and the logs to inspect. The report at the start has nothing to report, and no recording is in progress.
    /// </summary>
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
        _commands.Setup(c => c.RecordAsync(It.IsAny<RecordRequest>(), It.IsAny<StartOrigin>())).ReturnsAsync(0);
        _commands.Setup(c => c.TranscribeAsync(It.IsAny<TranscribeRequest>())).ReturnsAsync(0);
        _commands.Setup(c => c.RebuildAsync(It.IsAny<string>(), It.IsAny<bool>())).ReturnsAsync(0);
        _commands.Setup(c => c.AttachAsync(It.IsAny<string?>(), It.IsAny<bool>())).ReturnsAsync(0);
        _report = new Mock<IStartupReport>();
        _report.Setup(r => r.ShowAsync(It.IsAny<bool>())).ReturnsAsync((StartupChoice?)null);
        _report.Setup(r => r.HeaderLines()).Returns([]);
        _report.Setup(r => r.RecordingNow()).Returns((RunRecord?)null);
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

    /// <summary>
    /// Runs the menu until it is left, failing the test rather than hanging if it never is. <paramref name="time"/> is the
    /// clock, the system's unless given, such as one whose local time is US Eastern time.
    /// </summary>
    private Task<int> RunMenuAsync(string? recordings = null, ILoopbackCaptureProvider? devices = null, TimeProvider? time = null)
    {
        var menu = new MenuCommand(_commands.Object, _report.Object, _ui, _settings.Gate(_gateLog), _settings.File, Folders(recordings), devices ?? _devices, _choice,
            new RecordingNamePrompt(_ui), _desktop.Object, time ?? TimeProvider.System, _log);
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
        var lines = Lines();
        var folderLine = Array.IndexOf(lines, $"Recordings and transcripts: {root}");
        Assert.That(folderLine, Is.GreaterThanOrEqualTo(0), "the menu names the recordings folder");
        return lines[folderLine + 1];
    }

    /// <summary>Everything shown so far, line by line.</summary>
    private string[] Lines() => _ui.Screen.Split(Environment.NewLine);

    /// <summary>Makes the settings file one that cannot be used: a name that is not one of the setting's choices.</summary>
    private void BreakTheSettings() => _settings.Write("""{ "Recording": { "KeepScreenOn": "Sometimes" } }""");

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
    private int MenusShown() => Lines().Count(line => line == "  Q  Quit");

    /// <summary>What transcribing <paramref name="path"/> from the menu asks for: the menu has asked whatever it had to ask.</summary>
    private static TranscribeRequest FromMenu(string path) => new(path);

    /// <summary>A record request from the menu for <paramref name="name"/>, recorded only or also transcribed as <paramref name="transcribe"/> says.</summary>
    private static RecordRequest Recording(string name, bool transcribe) =>
        It.Is<RecordRequest>(request => request.Name == name && request.Transcribe == transcribe);

    /// <summary>A recording in progress in a recorder in the background, "Kalam", started at 23:00:02 UTC (19:00 in US Eastern time).</summary>
    private static RunRecord RecordingInTheBackground() => TestRuns.Record(TestRuns.Id(1), RunState.Recording);

    /// <summary>A recording in progress in another ClassScribe window, "Kalam", by process 4321: it has no link to show it by.</summary>
    private static RunRecord RecordingInAnotherWindow() => TestRuns.Record(TestRuns.Id(1), RunState.Recording) with { Home = RunHome.InWindow, Pipe = null };

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
        Assert.That(Lines(), Has.Member($"Settings: {_settings.File.Path}"));
        Assert.That(Lines(), Has.No.Member("  A  Show the recording in progress"), "no recording is in progress");
        Assert.That(LineUnderTheFolder(_folder.Path), Does.StartWith("Recording from:"), "a usable folder brings no warning");
        _commands.VerifyNoOtherCalls();
    }

    /// <summary>
    /// The report at the start comes first, with its offers, before the menu is shown: the report of recordings that ended
    /// unexpectedly, transcriptions that were cut off and outcomes nobody has seen. When nothing is chosen there, nothing
    /// is done about it.
    /// </summary>
    [Test]
    public async Task The_report_at_the_start_comes_before_the_menu_with_its_offers()
    {
        _report.Setup(r => r.ShowAsync(true)).Callback(() => _ui.WriteLine("(the report)")).ReturnsAsync((StartupChoice?)null);
        _ui.Type('q');

        Assert.That(await RunMenuAsync(), Is.EqualTo(0));

        _report.Verify(r => r.ShowAsync(true), Times.Once);
        _report.Verify(r => r.ShowAsync(false), Times.Never);
        Assert.That(Array.IndexOf(Lines(), "(the report)"), Is.LessThan(Array.IndexOf(Lines(), "ClassScribe")), "the report comes before the menu");
        _commands.VerifyNoOtherCalls();
    }

    /// <summary>
    /// The report at the start carries out what is chosen there itself, the check of the settings for C and T included: C
    /// has continued the recording, T transcribed it, O opened the transcript and F forgot the run, once, and the choice
    /// returned only says what was done. The menu does not do it again, whatever the settings: it hands the runner
    /// nothing, opens nothing and reads no settings for it, and then opens.
    /// </summary>
    [TestCase(StartupAction.Continue, false)]
    [TestCase(StartupAction.Transcribe, false)]
    [TestCase(StartupAction.Open, false)]
    [TestCase(StartupAction.Forget, false)]
    [TestCase(StartupAction.Continue, true)]
    [TestCase(StartupAction.Transcribe, true)]
    [TestCase(StartupAction.Open, true)]
    [TestCase(StartupAction.Forget, true)]
    public async Task What_was_chosen_in_the_report_at_the_start_is_not_done_again(StartupAction action, bool settingsBroken)
    {
        _report.Setup(r => r.ShowAsync(true)).ReturnsAsync(new StartupChoice(TestRuns.Id(1), action));
        if (settingsBroken) BreakTheSettings();
        _ui.Type('q');

        Assert.That(await RunMenuAsync(), Is.EqualTo(0));

        _commands.VerifyNoOtherCalls();
        _desktop.VerifyNoOtherCalls();
        Assert.That(_gateLog.Messages, Is.Empty, "the settings were not read for it");
        Assert.That(MenusShown(), Is.EqualTo(1), "the menu opened after it");
    }

    /// <summary>
    /// When a recording is in progress in the background, the menu shows it at once, after the report, before the menu
    /// itself: its log so far and what happens, through the runner, as a window that did not start it. Esc there brings
    /// the menu back, which offers A to show it again.
    /// </summary>
    [Test]
    public async Task A_recording_in_progress_is_shown_at_once_and_A_shows_it_again_after_Esc()
    {
        var inProgress = RecordingInTheBackground();
        _report.Setup(r => r.RecordingNow()).Returns(inProgress);
        _report.Setup(r => r.ShowAsync(true)).Callback(() => _ui.WriteLine("(the report)")).ReturnsAsync((StartupChoice?)null);
        _commands.Setup(c => c.AttachAsync(inProgress.RunId, false)).Callback(() => _ui.WriteLine("(the recording)")).ReturnsAsync(0);
        _ui.Type('a', 'q');

        Assert.That(await RunMenuAsync(), Is.EqualTo(0));

        _commands.Verify(c => c.AttachAsync(inProgress.RunId, false), Times.Exactly(2));
        _commands.VerifyNoOtherCalls();
        var lines = Lines();
        Assert.That(Array.IndexOf(lines, "(the report)"), Is.LessThan(Array.IndexOf(lines, "(the recording)")), "the report comes first");
        Assert.That(Array.IndexOf(lines, "(the recording)"), Is.LessThan(Array.IndexOf(lines, "ClassScribe")), "the recording is shown before the menu");
        Assert.That(lines, Has.Member("  A  Show the recording in progress"));
    }

    /// <summary>
    /// A recording continued with C in the report at the start was shown there, by the run that continued it; the menu does
    /// not show it again at once when that ends or is left, and offers A to show it.
    /// </summary>
    [Test]
    public async Task A_recording_continued_from_the_report_is_not_shown_again_at_once()
    {
        _report.Setup(r => r.ShowAsync(true)).ReturnsAsync(new StartupChoice(TestRuns.Id(1), StartupAction.Continue));
        _report.Setup(r => r.RecordingNow()).Returns(TestRuns.Record(TestRuns.Id(2), RunState.Recording));
        _ui.Type('q');

        Assert.That(await RunMenuAsync(), Is.EqualTo(0));

        _commands.VerifyNoOtherCalls();
        Assert.That(Lines(), Has.Member("  A  Show the recording in progress"));
    }

    /// <summary>
    /// After T, O or F in the report at the start, or when nothing was carried out there, as when the settings kept C from
    /// running, a recording in progress is still shown at once: only a recording continued with C has been shown already.
    /// </summary>
    [TestCase(StartupAction.Transcribe)]
    [TestCase(StartupAction.Open)]
    [TestCase(StartupAction.Forget)]
    [TestCase(null)]
    public async Task A_recording_in_progress_is_shown_at_once_after_any_other_end_of_the_report(StartupAction? action)
    {
        var inProgress = RecordingInTheBackground();
        _report.Setup(r => r.ShowAsync(true)).ReturnsAsync(action is { } done ? new StartupChoice(TestRuns.Id(2), done) : null);
        _report.Setup(r => r.RecordingNow()).Returns(inProgress);
        _ui.Type('q');

        Assert.That(await RunMenuAsync(), Is.EqualTo(0));

        _commands.Verify(c => c.AttachAsync(inProgress.RunId, false), Times.Once);
        _commands.VerifyNoOtherCalls();
    }

    /// <summary>
    /// Showing the recording in progress never waits for the settings: a broken file must not keep anyone from seeing or
    /// stopping a recording. Neither at the start nor with A are the settings read.
    /// </summary>
    [Test]
    public async Task Showing_the_recording_in_progress_is_never_held_up_by_the_settings()
    {
        var inProgress = RecordingInTheBackground();
        _report.Setup(r => r.RecordingNow()).Returns(inProgress);
        BreakTheSettings();
        _ui.Type('a', 'q');

        Assert.That(await RunMenuAsync(), Is.EqualTo(0));

        _commands.Verify(c => c.AttachAsync(inProgress.RunId, false), Times.Exactly(2));
        Assert.That(_gateLog.Messages, Is.Empty, "the settings were not read");
    }

    /// <summary>
    /// While a recording is in progress, 1 and 2 explain that there is one recording at a time, with when it started in
    /// local time and that A shows it, instead of asking for a name. Nothing else is started.
    /// </summary>
    [TestCase('1')]
    [TestCase('2')]
    public async Task Choices_1_and_2_explain_while_a_recording_is_in_progress(char choice)
    {
        var inProgress = RecordingInTheBackground();
        _report.Setup(r => r.RecordingNow()).Returns(inProgress);
        _ui.TypeLine("Fiqh");
        _ui.Type(choice, 'q');

        Assert.That(await RunMenuAsync(time: ZonedTimeProvider.EasternSummer), Is.EqualTo(0));

        Assert.That(Lines(), Has.Member("Only one recording at a time: \"Kalam\" has been recording since 19:00. A shows it."));
        Assert.That(_ui.Screen, Does.Not.Contain("Name for this recording"));
        _commands.Verify(c => c.AttachAsync(inProgress.RunId, false), Times.Once, "only at the start");
        _commands.VerifyNoOtherCalls();
        Assert.That(MenusShown(), Is.EqualTo(2), "the menu came back");
    }

    /// <summary>
    /// A recording in progress in another ClassScribe window has no link to show it by: at the start, with A, and with 1
    /// or 2, the menu names that window instead, and does not offer A.
    /// </summary>
    [Test]
    public async Task A_recording_in_another_window_is_named_rather_than_shown()
    {
        _report.Setup(r => r.RecordingNow()).Returns(RecordingInAnotherWindow());
        _ui.Type('a', '1', 'q');

        Assert.That(await RunMenuAsync(), Is.EqualTo(0));

        const string named = "\"Kalam\" is being recorded in another ClassScribe window (process 4321), which shows it; closing that window ends the recording";
        var lines = Lines();
        Assert.That(lines.Count(line => line == $"{named}."), Is.EqualTo(2), "at the start and with A");
        Assert.That(lines, Has.Member($"Only one recording at a time: {named}."));
        Assert.That(lines, Has.No.Member("  A  Show the recording in progress"));
        _commands.VerifyNoOtherCalls();
    }

    /// <summary>A without a recording in progress says so, and the menu comes back.</summary>
    [Test]
    public async Task A_without_a_recording_in_progress_says_so()
    {
        _ui.Type('a', 'q');

        Assert.That(await RunMenuAsync(), Is.EqualTo(0));

        Assert.That(Lines(), Has.Member("No recording is in progress."));
        Assert.That(MenusShown(), Is.EqualTo(2));
        _commands.VerifyNoOtherCalls();
    }

    /// <summary>The report's lines about the runs going on now come right under what is recorded, before the choices.</summary>
    [Test]
    public async Task The_lines_of_the_report_about_the_runs_now_are_in_the_header()
    {
        string[] header = ["Recording now: \"Kalam\" since 19:00 (00:42:13). A shows it.", "Being transcribed in the background: \"Fiqh\" (uploading 40%)."];
        _report.Setup(r => r.HeaderLines()).Returns(header);
        _ui.Type('q');

        await RunMenuAsync();

        var lines = Lines();
        var source = Array.IndexOf(lines, "Recording from: the device the class plays on");
        Assert.That(lines[(source + 1)..(source + 4)], Is.EqualTo(new[] { header[0], header[1], "" }));
    }

    /// <summary>
    /// Every choice reads the settings first. While they cannot be used, the choice does not run, whichever it is: the
    /// log says what is wrong and to choose again, and the menu comes back.
    /// </summary>
    [Test]
    public async Task No_choice_runs_while_the_settings_cannot_be_used()
    {
        WriteRecording("Kalam", new DateTimeOffset(2026, 10, 14, 19, 30, 0, TimeSpan.Zero));
        BreakTheSettings();
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
        var atStart = Lines();
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
        var lines = Lines();
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

    /// <summary>
    /// 1 and 2 ask for the recording's name, then hand the runner, as a run started from the menu, a request to record
    /// only, or to record and transcribe: a new run id, the name, what menu choice 6 or the settings say to record, the
    /// recordings folder of the settings, the question whether to continue a recent recording of the name left to the
    /// starter, no time limit, and the results opened at the end. The menu comes back afterwards.
    /// </summary>
    [TestCase('1', false)]
    [TestCase('2', true)]
    public async Task The_record_choices_hand_the_runner_a_request_from_the_menu(char choice, bool transcribe)
    {
        _choice.App = "chrome";
        var handed = new List<(RecordRequest Request, StartOrigin Origin)>();
        _commands.Setup(c => c.RecordAsync(It.IsAny<RecordRequest>(), It.IsAny<StartOrigin>()))
            .Callback((RecordRequest request, StartOrigin origin) => handed.Add((request, origin)))
            .ReturnsAsync(0);
        _ui.TypeLine("Kalam lesson 12");
        _ui.Type(choice, 'q');

        await RunMenuAsync();

        Assert.That(_ui.Screen, Does.Contain("Name for this recording"));
        Assert.That(handed, Has.Count.EqualTo(1));
        var (request, origin) = handed[0];
        Assert.That(RunIds.IsWellFormed(request.RunId), Is.True, $"a run id as ClassScribe makes them: {request.RunId}");
        Assert.That(request, Is.EqualTo(new RecordRequest(request.RunId, "Kalam lesson 12", transcribe, new SourcePlan(RecordingMode.Class, new ClassSource.App("chrome"), null),
            _folder.Path, ContinueChoice.Ask, ContinueBaseName: null, Minutes: null, Open: true)));
        Assert.That(origin, Is.EqualTo(StartOrigin.Menu));
        Assert.That(MenusShown(), Is.EqualTo(2), "the menu came back");
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
        _commands.Verify(c => c.RecordAsync(Recording("Kalam lesson 12", transcribe: false), StartOrigin.Menu), Times.Once);
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

    /// <summary>
    /// 3 notes what sets each recording apart: the parts it was made in when it was taken up again after it stopped, and
    /// "class + microphone" when your microphone was recorded with the class.
    /// </summary>
    [Test]
    public async Task Three_notes_the_parts_and_the_microphone_of_a_recording()
    {
        var both = WriteRecording("Kalam", new DateTimeOffset(2026, 10, 14, 19, 30, 0, TimeSpan.Zero));
        File.WriteAllText(both.Marks, "00:00:00.050 ~ Resumed at 19:31 UTC, about 4 minutes not recorded" + Environment.NewLine);
        TestAudio.WriteWav(Path.Combine(both.Folder, $"{both.BaseName}.mic.wav"), 16000, new short[1600]);
        _ui.Type('3', '\u001b', 'q');

        await RunMenuAsync();

        Assert.That(Lines(), Has.Member($"  1  {both.BaseName}  00:00:00  2 parts  class + microphone  no transcript yet"));
        _commands.VerifyNoOtherCalls();
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

        Assert.That(Lines(), Has.Member(
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

        Assert.That(Lines(), Has.Member(
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

        Assert.That(Lines(), Has.Member(
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

        Assert.That(Lines(), Has.Member(
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

        var lines = Lines();
        Assert.That(lines, Has.Member($"  1  {both.BaseName}  00:00:00  transcript ready"));
        Assert.That(lines, Has.Member("This recording has a transcript. R rebuilds it from ElevenLabs' saved answer, with the current marks and Transcript settings (free, nothing is uploaded). Y transcribes it again (billed again). Any other key goes back."));
        Assert.That(_ui.Screen, Does.Not.Contain("answer kept").And.Not.Contain("kept in"));
        _commands.VerifyNoOtherCalls();
    }

    /// <summary>
    /// A recording that is being recorded now, in another window, is listed as RECORDING NOW, and picking it says to
    /// transcribe it once it has stopped: what was sent now would stop short. Nothing is sent, and the menu comes back.
    /// </summary>
    [Test]
    public async Task Three_refuses_a_recording_that_is_being_recorded_now()
    {
        var recordingNow = WriteRecording("Kalam", new DateTimeOffset(2026, 10, 14, 19, 30, 0, TimeSpan.Zero));
        _ui.Type('3', '1', 'q');

        using (new FileStream(recordingNow.Segment(1), FileMode.Open, FileAccess.Write, FileShare.Read))
            Assert.That(await RunMenuAsync(), Is.EqualTo(0));

        var lines = Lines();
        Assert.That(lines, Has.Member($"  1  {recordingNow.BaseName}  00:00:00  RECORDING NOW"));
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

        Assert.That(Lines(), Has.Member(
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

        Assert.That(Lines().Count(line => line.StartsWith("This recording has a transcript.", StringComparison.Ordinal)), Is.EqualTo(2));
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

        Assert.That(Lines(), Has.Member($"{recordingNow.BaseName} is being recorded now; transcribe it once it has stopped."));
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

    /// <summary>
    /// The list goes on with the apps that have sound open, and picking one records that app alone. ClassScribe's own
    /// processes are not offered: a recording's own capture shows up as an app with sound.
    /// </summary>
    [Test]
    public async Task Six_lists_the_apps_with_sound_and_pins_the_one_picked()
    {
        _devices.Enqueue(new FakeAudioCapture(Pcm16KMono, "Speakers"));
        _devices.AddApp(4242, "chrome", "Speakers", level: 0.4f);
        _devices.AddApp(5151, "msedge", "Speakers");
        _devices.AddApp(6161, "ClassScribe", "Speakers", level: 0.2f);
        _ui.Type('6', '2', 'q');

        await RunMenuAsync();

        Assert.That((_choice.App, _choice.Device), Is.EqualTo(("chrome", (string?)null)));
        Assert.That(_ui.Screen, Does.Contain("2  chrome  (app on Speakers, playing)")
            .And.Contain("3  msedge  (app on Speakers)")
            .And.Contain("Recording from: the app \"chrome\"")
            .And.Contain("Recording:App"), "the menu says how to make the choice permanent");
        Assert.That(_ui.Screen, Does.Not.Contain("ClassScribe  (app on"));
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
        Assert.That(Lines(), Has.Member(
            $"Recording from the device the class plays on until ClassScribe is closed, whatever the settings say. To keep that, leave Recording:Device and Recording:App empty in {_settings.File.Path}."));
        _commands.VerifyNoOtherCalls();
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
        _commands.Setup(c => c.RecordAsync(Recording("Kalam", transcribe: true), StartOrigin.Menu))
            .ThrowsAsync(new InvalidOperationException("Windows reports no audio output device to record."));
        _ui.TypeLine("Kalam");
        _ui.TypeLine("Kalam");
        _ui.Type('2', '1', 'q');

        Assert.That(await RunMenuAsync(), Is.EqualTo(0));
        _commands.Verify(c => c.RecordAsync(Recording("Kalam", transcribe: false), StartOrigin.Menu), Times.Once, "the menu was still there for the next choice");
        Assert.That(_log.Problems, Has.Count.EqualTo(1));
    }

    /// <summary>A choice cancelled with Ctrl+C returns to the menu.</summary>
    [Test]
    public async Task A_cancelled_choice_returns_to_the_menu()
    {
        _commands.Setup(c => c.RecordAsync(Recording("Kalam", transcribe: true), StartOrigin.Menu)).ThrowsAsync(new OperationCanceledException());
        _ui.TypeLine("Kalam");
        _ui.Type('2', 'q');

        Assert.That(await RunMenuAsync(), Is.EqualTo(0));
    }

    /// <summary>
    /// What fails at the start, before the menu is first shown, such as the recording in progress that cannot be shown,
    /// is reported, and the menu opens all the same.
    /// </summary>
    [Test]
    public async Task A_failure_at_the_start_is_reported_and_the_menu_opens_all_the_same()
    {
        _report.Setup(r => r.RecordingNow()).Returns(RecordingInTheBackground());
        _commands.Setup(c => c.AttachAsync(It.IsAny<string?>(), false)).ThrowsAsync(new InvalidOperationException("The link to the recorder broke."));
        _ui.Type('q');

        Assert.That(await RunMenuAsync(), Is.EqualTo(0));

        Assert.That(_log.Problems, Is.EqualTo(new[] { "Stopped because of an error" }));
        Assert.That(MenusShown(), Is.EqualTo(1));
    }

    /// <summary>
    /// Ctrl+C that cancels a transcription while the recording in progress is shown at the start, as when the view went on
    /// with that recording in this window after its recorder was lost twice, is a normal way on to the menu: it is said
    /// to be cancelled, and the menu opens.
    /// </summary>
    [Test]
    public async Task A_transcription_cancelled_while_the_recording_was_shown_at_the_start_leads_on_to_the_menu()
    {
        _report.Setup(r => r.RecordingNow()).Returns(RecordingInTheBackground());
        _commands.Setup(c => c.AttachAsync(It.IsAny<string?>(), false)).ThrowsAsync(new OperationCanceledException());
        _ui.Type('q');

        Assert.That(await RunMenuAsync(), Is.EqualTo(0));

        Assert.That(_log.Problems, Is.EqualTo(new[] { "Cancelled" }));
        Assert.That(MenusShown(), Is.EqualTo(1));
    }

    /// <summary>
    /// Keys typed while a choice runs, such as a Q pressed during the upload, were not meant for the menu
    /// that comes back afterwards: it waits for a key pressed after it is shown.
    /// </summary>
    [Test]
    public async Task Keys_typed_while_a_choice_runs_are_not_taken_as_the_next_choice()
    {
        _commands.Setup(c => c.RecordAsync(Recording("Kalam", transcribe: true), StartOrigin.Menu)).Callback(() => _ui.TypeAhead('q', '1')).ReturnsAsync(0);
        _ui.TypeLine("Kalam");
        _ui.Type('2', '5', 'q');

        Assert.That(await RunMenuAsync(), Is.EqualTo(0));
        _desktop.Verify(d => d.OpenFolder(_folder.Path), Times.Once, "the menu waited for the 5 pressed after it came back");
        _commands.Verify(c => c.RecordAsync(It.Is<RecordRequest>(request => !request.Transcribe), It.IsAny<StartOrigin>()), Times.Never);
    }

    /// <summary>
    /// Keys typed while the recording in progress was shown at the start were not meant for the menu either: it waits for
    /// a key pressed after it is shown.
    /// </summary>
    [Test]
    public async Task Keys_typed_while_the_recording_was_shown_at_the_start_are_not_taken_as_a_choice()
    {
        _report.Setup(r => r.RecordingNow()).Returns(RecordingInTheBackground());
        _commands.Setup(c => c.AttachAsync(It.IsAny<string?>(), false)).Callback(() => _ui.TypeAhead('5')).ReturnsAsync(0);
        _ui.Type('q');

        Assert.That(await RunMenuAsync(), Is.EqualTo(0));

        _desktop.VerifyNoOtherCalls();
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

/// <summary>
/// The command runner: each command in a scope of its own, made when it starts and disposed when it ends, after the gate
/// the menu opens; a recording through the starter, showing a run through the attach command over the view, list and
/// devices through their commands, and transcribing only what no recorder is busy with. The container is the test's own:
/// the run files are real files in a folder of the test's own, the processes they name and the clock are played by the
/// test, the starter and the view are stand-ins, and the upload is mocked.
/// </summary>
[TestFixture]
public class CommandRunnerTests
{
    private TempFolder _recordings = null!;
    private TempFolder _settingsFolder = null!;
    private TestRuntime _runtime = null!;
    private FakeConsoleUi _ui = null!;
    private FakeCaptureProvider _devices = null!;
    private Mock<IDesktop> _desktop = null!;
    private Mock<ITranscriptionClient> _client = null!;
    private PlayedRuns _played = null!;
    private List<IServiceProvider> _resolvedIn = null!;
    private RecordingLogger<CommandRunner> _log = null!;
    private RecordingLogger<TranscribeCommand> _transcribeLog = null!;
    private WatchedScopes _scopes = null!;
    private ServiceProvider? _container;
    private CommandRunner? _runner;

    /// <summary>Creates the folders, the console, the simulated devices, the mocks, the stand-ins and the logs to inspect.</summary>
    [SetUp]
    public void SetUp()
    {
        _recordings = new TempFolder();
        _settingsFolder = new TempFolder();
        _runtime = new TestRuntime();
        _ui = new FakeConsoleUi();
        _devices = new FakeCaptureProvider();
        _desktop = new Mock<IDesktop>();
        _client = Mocks.TranscriptionClient();
        _played = new PlayedRuns();
        _resolvedIn = [];
        _log = new RecordingLogger<CommandRunner>();
        _transcribeLog = new RecordingLogger<TranscribeCommand>();
    }

    /// <summary>Disposes the container and deletes the folders.</summary>
    [TearDown]
    public void TearDown()
    {
        _container?.Dispose();
        _recordings.Dispose();
        _settingsFolder.Dispose();
        _runtime.Dispose();
    }

    /// <summary>
    /// The runner over a container of the test's own, holding the clock and the run files; with
    /// <paramref name="withCommands"/>, also the starter and the view the test plays, the real attach command over that
    /// view (which turns Ctrl+C on through the test, not through Windows), list, devices and transcribe, each made once per
    /// scope, which notes the scope it was made in. <paramref name="more"/> adds or replaces services.
    /// </summary>
    private CommandRunner CreateRunner(bool withCommands = true, Action<IServiceCollection>? more = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(_runtime.Time);
        services.AddSingleton(_runtime.Journal);
        if (withCommands)
        {
            services.AddScoped<IRecordStarter>(provider => Noted(provider, new FakeRecordStarter(_played, provider)));
            services.AddScoped<IRunViewer>(provider => Noted(provider, new PlayedRunViewer(_played, provider)));
            services.AddScoped(provider => new AttachCommand(_runtime.Journal, provider.GetRequiredService<IRunViewer>(), _runner!, _ui, new FakeWindowSignals(), _runtime.Time,
                NullLogger<AttachCommand>.Instance) { TurnOnCtrlC = () => 0 });
            services.AddScoped(provider => Noted(provider, new ListCommand(Folders(), _runtime.Journal, _ui, ZonedTimeProvider.EasternSummer)));
            services.AddScoped(provider => Noted(provider, new DevicesCommand(new Lazy<ILoopbackCaptureProvider>(() => _devices), _ui)));
            services.AddScoped(provider => Noted(provider, CreateTranscribeCommand()));
        }

        more?.Invoke(services);
        _container = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        _scopes = new WatchedScopes(_container.GetRequiredService<IServiceScopeFactory>());
        _runner = new CommandRunner(_scopes, _container.GetRequiredService<IServiceProviderIsService>(), _log);
        return _runner;
    }

    /// <summary>Notes that a command was made in the scope of <paramref name="provider"/>, and hands it on.</summary>
    private T Noted<T>(IServiceProvider provider, T command)
    {
        lock (_resolvedIn) _resolvedIn.Add(provider);
        return command;
    }

    /// <summary>The recordings folders, with Recording:OutputFolder set to the test's recordings folder.</summary>
    private ClassFolders Folders() =>
        new(TestOptions.Create(new RecordingOptions { OutputFolder = _recordings.Path }), TimeProvider.System, NullLogger<ClassFolders>.Instance);

    /// <summary>
    /// The transcribe command, with the real workflow around a mocked upload and the recordings folder of the test, and
    /// without an API key, so that it stops before any work, saying that there is no key: that shows it was reached.
    /// </summary>
    private TranscribeCommand CreateTranscribeCommand()
    {
        var recording = TestOptions.Create(new RecordingOptions { OutputFolder = _recordings.Path, OpenFolderWhenDone = false, OpenTranscriptWhenDone = false });
        var elevenLabs = TestOptions.Create(new ElevenLabsOptions { ApiKey = "", KeytermsFile = _recordings.File("no-glossary.txt") });
        var folders = new ClassFolders(recording, TimeProvider.System, NullLogger<ClassFolders>.Instance);
        var workflow = new TranscriptionWorkflow(Mocks.AudioPreparer().Object, _client.Object, new KeytermLoader(NullLogger<KeytermLoader>.Instance),
            new TranscriptFormatter(TestOptions.Create(new TranscriptOptions())), folders, new ClassMarks(NullLogger<ClassMarks>.Instance), elevenLabs, recording,
            new TranscriptionProgress(), NullLogger<TranscriptionWorkflow>.Instance);
        return new TranscribeCommand(workflow, folders, new FakeUserSignals(), _desktop.Object, new FakePowerRequests(), new FakePowerSource(), recording, elevenLabs,
            ZonedTimeProvider.EasternSummer, _transcribeLog);
    }

    /// <summary>Writes a recording of <paramref name="name"/>, a second of audio, started at the tests' class, into the folder of its name under the recordings folder.</summary>
    private RecordingFiles WriteRecording(string name = "Kalam")
    {
        var files = RecordingFiles.ForNew(_recordings.File(name), name, TestRuns.Start);
        Directory.CreateDirectory(files.Folder);
        TestAudio.WriteWav(files.Segment(1), 16000, new short[16000]);
        return files;
    }

    /// <summary>A record request for a new recording of "Kalam" into the recordings folder, as the menu makes them.</summary>
    private RecordRequest Request() =>
        new(RunIds.New(_runtime.Time.GetUtcNow()), "Kalam", Transcribe: true, new SourcePlan(RecordingMode.Class, new ClassSource.Automatic(), null), _recordings.Path,
            ContinueChoice.Ask, ContinueBaseName: null, Minutes: null, Open: true);

    /// <summary>
    /// Each command runs in a scope of its own: a scope is made when it starts, the command is made in that scope, and the
    /// scope is disposed when it ends, so that a run's settings, Ctrl+C signals and recording are its own.
    /// </summary>
    [TestCase("record")]
    [TestCase("transcribe")]
    [TestCase("list")]
    [TestCase("devices")]
    [TestCase("attach")]
    public async Task Each_command_runs_in_a_scope_of_its_own_made_when_it_starts_and_disposed_when_it_ends(string command)
    {
        var runner = CreateRunner();
        var inProgress = _runtime.WriteRun(1, WriteRecording(), RunState.Recording, alive: true);
        var stopped = WriteRecording("Fiqh");
        Task<int> Run() => command switch
        {
            "record" => runner.RecordAsync(Request(), StartOrigin.Menu),
            "transcribe" => runner.TranscribeAsync(new TranscribeRequest(stopped.Segment(1))),
            "list" => runner.ListAsync(null),
            "devices" => runner.DevicesAsync(),
            _ => runner.AttachAsync(inProgress.RunId, alert: false)
        };

        await Run();
        var first = _scopes.Made.ToList();
        await Run();

        Assert.That(first, Has.Count.EqualTo(1), "a scope for the first run");
        Assert.That(_scopes.Made, Has.Count.EqualTo(2), "and another for the second");
        Assert.That(_scopes.Made.Select(scope => scope.Disposed), Is.All.True, "each disposed once its command ended");
        Assert.That(_resolvedIn, Is.EqualTo(_scopes.Made.Select(scope => scope.ServiceProvider)), "each command made in the scope of its run");
    }

    /// <summary>
    /// The menu hands each run to the runner after the gate has read the settings again: the run's scope is made after
    /// that, so the starter, made in it, reads the settings as the gate last accepted them. An edit made between two
    /// choices is read by the second run, and the first kept what it read.
    /// </summary>
    [Test]
    public async Task The_menu_hands_each_run_to_the_runner_after_the_gate()
    {
        var first = _recordings.File("First");
        var second = _recordings.File("Second");
        string Settings(string folder) => JsonSerializer.Serialize(new { Recording = new { OutputFolder = folder } });
        var settings = new TestSettings(_settingsFolder.Path, Settings(first));
        var runner = CreateRunner(more: services => services
            .AddSettings(settings.Configuration)
            .AddScoped<IRecordStarter>(provider => Noted(provider, new FakeRecordStarter(_played, provider,
                scope => scope.GetRequiredService<IOptionsSnapshot<RecordingOptions>>().Value.OutputFolder))));
        var monitor = _container!.GetRequiredService<IOptionsMonitor<RecordingOptions>>();
        var menu = new MenuCommand(runner, new NoStartupReport(), _ui, settings.Gate(), settings.File,
            new ClassFolders(monitor, TimeProvider.System, NullLogger<ClassFolders>.Instance), _devices, new SourceChoice(monitor), new RecordingNamePrompt(_ui),
            _desktop.Object, TimeProvider.System, NullLogger<MenuCommand>.Instance);
        _ui.TypeLine("Kalam");
        _ui.TypeLine("Fiqh");
        _ui.Type('1');

        var running = menu.RunAsync();
        await Wait.UntilAsync(() => _played.Started.Count == 1);
        settings.Write(Settings(second));
        _ui.Type('2', 'q');
        Assert.That(await running.WaitAsync(TimeSpan.FromSeconds(15)), Is.EqualTo(0));

        var started = _played.Started;
        Assert.That(started.Select(run => (run.Request.Name, run.Request.OutputRoot, run.Seen, run.Origin)), Is.EqualTo(new[]
        {
            ("Kalam", first, first, StartOrigin.Menu),
            ("Fiqh", second, second, StartOrigin.Menu)
        }));
        Assert.That(started[0].Services, Is.Not.SameAs(started[1].Services), "each run in a scope of its own");
        Assert.That(_scopes.Made.Select(scope => scope.Disposed), Is.All.True);
    }

    /// <summary>A recording goes to the starter, with the request as it was made and where it was started from; its exit code comes back.</summary>
    [TestCase(StartOrigin.Menu)]
    [TestCase(StartOrigin.CommandLine)]
    public async Task A_recording_goes_to_the_starter_with_where_it_was_started_from(StartOrigin origin)
    {
        var runner = CreateRunner();
        var request = Request();
        _played.StartExitCode = 130;

        var exitCode = await runner.RecordAsync(request, origin);

        Assert.That(exitCode, Is.EqualTo(130));
        Assert.That(_played.Started.Select(run => (run.Request, run.Origin)), Is.EqualTo(new[] { (request, origin) }));
    }

    /// <summary>
    /// A command whose services this build does not register yet says so plainly, as a failure the app reports, before it
    /// makes a scope.
    /// </summary>
    [Test]
    public void A_command_this_build_has_no_services_for_says_so()
    {
        var runner = CreateRunner(withCommands: false);

        Assert.That(Assert.ThrowsAsync<NotSupportedException>(() => runner.ListAsync(null))!.Message, Does.Contain("list"));
        Assert.That(Assert.ThrowsAsync<NotSupportedException>(() => runner.DevicesAsync())!.Message, Does.Contain("devices"));
        Assert.That(Assert.ThrowsAsync<NotSupportedException>(() => runner.AttachAsync(null, alert: false))!.Message, Does.Contain("background"));
        Assert.That(_scopes.Made, Is.Empty);
    }

    /// <summary>
    /// A recording a live recorder records, or is stopping, or transcribes in the background, is not transcribed again here,
    /// whether it is named by one of its files or by its folder: what was sent while it is recorded would stop short, and
    /// one being transcribed would be billed twice. It says why, with where the transcript will appear, and nothing is
    /// sent; the transcribe command is not even made.
    /// </summary>
    [TestCase(RunState.Recording, RunHome.Separate, "is being recorded now; transcribe it once it has stopped")]
    [TestCase(RunState.Stopping, RunHome.Separate, "is being recorded now; transcribe it once it has stopped")]
    [TestCase(RunState.Transcribing, RunHome.Separate, "is being transcribed in the background; its transcript will appear in {0}")]
    [TestCase(RunState.Recording, RunHome.InWindow, "is being recorded now; transcribe it once it has stopped")]
    [TestCase(RunState.Transcribing, RunHome.InWindow, "is being transcribed in another ClassScribe window; its transcript will appear in {0}")]
    public async Task Transcribing_refuses_a_recording_a_recorder_is_busy_with(RunState state, RunHome home, string reason)
    {
        var runner = CreateRunner();
        var recording = WriteRecording();
        _runtime.WriteRun(1, recording, state, alive: true, run => run with { Home = home });

        var byFile = await runner.TranscribeAsync(new TranscribeRequest(recording.Segment(1)));
        var byFolder = await runner.TranscribeAsync(new TranscribeRequest(recording.Folder, FromCommandLine: true));

        Assert.That((byFile, byFolder), Is.EqualTo((1, 1)));
        Assert.That(_log.Problems, Is.EqualTo(Enumerable.Repeat($"{recording.BaseName} {string.Format(reason, recording.Folder)}", 2)));
        Assert.That(_resolvedIn, Is.Empty, "the transcribe command was not made");
        _client.VerifyNoOtherCalls();
    }

    /// <summary>
    /// A recording whose run is over, or that ended unexpectedly, goes on to the transcribe command: here it stops there,
    /// for want of a key, which shows that it got that far.
    /// </summary>
    [TestCase(RunState.Finished, true)]
    [TestCase(RunState.Recording, false)]
    [TestCase(RunState.Transcribing, false)]
    public async Task Transcribing_a_recording_no_recorder_is_busy_with_goes_on_to_the_transcribe_command(RunState state, bool alive)
    {
        var runner = CreateRunner();
        var recording = WriteRecording();
        _runtime.WriteRun(1, recording, state, alive,
            run => state == RunState.Finished ? run with { Outcome = new RunOutcome(OutcomeKind.Recorded, 0, "Recorded", null, recording.Folder, null) } : run);

        var exitCode = await runner.TranscribeAsync(new TranscribeRequest(recording.Segment(1)));

        Assert.That(exitCode, Is.EqualTo(1));
        Assert.That(_log.Problems, Is.Empty);
        Assert.That(_transcribeLog.Problems, Has.Some.StartsWith("No ElevenLabs API key found"));
    }

    /// <summary>
    /// Attach without a run id shows the recording in progress, as a window that did not start it; with none recording, a
    /// transcription in the background. Esc there leaves the view with exit code 0, and the run carries on.
    /// </summary>
    [TestCase(RunState.Recording)]
    [TestCase(RunState.Transcribing)]
    public async Task Attach_without_a_run_id_shows_the_run_in_progress_as_another_window(RunState state)
    {
        var runner = CreateRunner();
        _runtime.WriteRun(1, WriteRecording("Fiqh"), RunState.Finished, alive: false);
        var inProgress = _runtime.WriteRun(2, WriteRecording(), state, alive: true);

        var exitCode = await runner.AttachAsync(null, alert: false);

        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(_played.Shown.Select(shown => (shown.Run.RunId, shown.Mode)), Is.EqualTo(new[] { (inProgress.RunId, ViewMode.Viewer) }));
    }

    /// <summary>The window the recorder opens by itself shows the run it names in alert mode, where no key counts until Enter.</summary>
    [Test]
    public async Task Attach_with_alert_shows_the_run_named_in_alert_mode()
    {
        var runner = CreateRunner();
        var named = _runtime.WriteRun(1, WriteRecording("Fiqh"), RunState.Recording, alive: true);
        _runtime.WriteRun(2, WriteRecording(), RunState.Recording, alive: true);

        await runner.AttachAsync(named.RunId, alert: true);

        Assert.That(_played.Shown.Select(shown => (shown.Run.RunId, shown.Mode)), Is.EqualTo(new[] { (named.RunId, ViewMode.Alert) }));
    }

    /// <summary>
    /// When the run ends while it is shown, attach ends with the run's exit code, as the attach command gives it: the
    /// view's, or else the outcome's. (What the command does with each kind of end is its own tests' matter.)
    /// </summary>
    [TestCase(130, 1, 130)]
    [TestCase(null, 1, 1)]
    public async Task Attach_ends_with_the_exit_code_of_a_run_that_ended_while_shown(int? viewExitCode, int? outcomeExitCode, int expected)
    {
        var runner = CreateRunner();
        var inProgress = _runtime.WriteRun(1, WriteRecording(), RunState.Recording, alive: true);
        var outcome = outcomeExitCode is { } code ? new RunOutcome(OutcomeKind.TranscriptFailed, code, "The transcript failed", null, null, null) : null;
        _played.EndNextView(new ViewResult(ViewEnd.RunEnded, viewExitCode, outcome));

        Assert.That(await runner.AttachAsync(inProgress.RunId, alert: false), Is.EqualTo(expected));
    }
}
