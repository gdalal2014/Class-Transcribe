using System.Text.RegularExpressions;
using ClassScribe.Commands;
using Moq;

namespace ClassScribe.Tests;

/// <summary>
/// What App does around the command line: a mistake, help, the menu and attach come before the settings and need none;
/// every other command runs only once the settings, read again, pass the check, and after the report at the start is
/// printed; a failure and a cancel have their own exit codes. The commands and the report are mocked; the settings are
/// read through the gate from a file of the test's own.
/// </summary>
[TestFixture]
public class AppTests
{
    /// <summary>The program the printed command lines of these tests name.</summary>
    private const string Exe = @"C:\Program Files\ClassScribe\ClassScribe.exe";

    private TempFolder _folder = null!;
    private TempFolder _settingsFolder = null!;
    private TestSettings _settings = null!;
    private RecordingLogger<RunGate> _gateLog = null!;
    private RecordingLogger<App> _log = null!;
    private Mock<ICommandRunner> _commands = null!;
    private Mock<IStartupReport> _report = null!;
    private List<string> _order = null!;
    private FakeConsoleUi _ui = null!;
    private TestCommandLine _line = null!;

    /// <summary>Creates a temporary folder, settings that can be used, the mocked commands and report, which note the order they run in.</summary>
    [SetUp]
    public void SetUp()
    {
        _folder = new TempFolder();
        _settingsFolder = new TempFolder();
        _settings = new TestSettings(_settingsFolder.Path);
        _gateLog = new RecordingLogger<RunGate>();
        _log = new RecordingLogger<App>();
        _order = [];
        _commands = new Mock<ICommandRunner>();
        _commands.Setup(c => c.RecordAsync(It.IsAny<RecordRequest>(), It.IsAny<StartOrigin>())).Callback(() => _order.Add("record")).ReturnsAsync(0);
        _commands.Setup(c => c.TranscribeAsync(It.IsAny<TranscribeRequest>())).Callback(() => _order.Add("transcribe")).ReturnsAsync(0);
        _commands.Setup(c => c.RebuildAsync(It.IsAny<string>(), It.IsAny<bool>())).Callback(() => _order.Add("rebuild")).ReturnsAsync(0);
        _commands.Setup(c => c.ListAsync(It.IsAny<string?>())).Callback(() => _order.Add("list")).ReturnsAsync(0);
        _commands.Setup(c => c.DevicesAsync()).Callback(() => _order.Add("devices")).ReturnsAsync(0);
        _commands.Setup(c => c.AttachAsync(It.IsAny<string?>(), It.IsAny<bool>())).Callback(() => _order.Add("attach")).ReturnsAsync(0);
        _report = new Mock<IStartupReport>();
        _report.Setup(r => r.ShowAsync(It.IsAny<bool>())).Callback(() => _order.Add("report")).ReturnsAsync((StartupChoice?)null);
    }

    /// <summary>Deletes the temporary folders.</summary>
    [TearDown]
    public void TearDown()
    {
        _folder.Dispose();
        _settingsFolder.Dispose();
    }

    /// <summary>Creates the app over a scripted keyboard that a person can, or cannot, type on.</summary>
    private App CreateApp(bool interactive = true)
    {
        _ui = new FakeConsoleUi { IsInteractive = interactive };
        _line = new TestCommandLine(_commands.Object, _ui, _settings, _folder.Path, gate: _settings.Gate(_gateLog));
        return new App(_line.Tree, _line.Gate, _report.Object, _log);
    }

    /// <summary>Runs the app once over a keyboard a person can type on, failing the test rather than waiting for an answer nobody gives.</summary>
    private Task<int> RunAsync(params string[] args) => CreateApp().RunAsync(args).WaitAsync(TimeSpan.FromSeconds(15));

    /// <summary>Makes the settings file one that cannot be used: a value that is not a whole number.</summary>
    private void BreakTheSettings() => _settings.Write("""{ "Recording": { "FlushSeconds": "five" } }""");

    /// <summary>What transcribing <paramref name="path"/> from the command line asks for, with the options given.</summary>
    private static TranscribeRequest FromCommandLine(string path, bool open = true, bool again = false) =>
        new(path, Open: open, Again: again, FromCommandLine: true);

    /// <summary>
    /// The arguments a command line printed by <see cref="CommandLine"/> hands the program, as PowerShell splits it: the
    /// words after the program, each quoted path as one argument without its quotes, and with the backticks that escape a
    /// character inside the quotes taken off, as PowerShell takes them off.
    /// </summary>
    private static string[] ArgumentsOf(string printed)
    {
        var words = Regex.Matches(printed, "\"((?:`.|[^\"`])*)\"|(\\S+)")
            .Select(match => match.Groups[1].Success ? Regex.Replace(match.Groups[1].Value, "`(.)", "$1") : match.Groups[2].Value)
            .ToArray();
        Assert.That(words[..2], Is.EqualTo(new[] { "&", Exe }), "the program, named by its full path");
        return words[2..];
    }

    /// <summary>
    /// A refused settings file never stops a recording (build-plan amendment 4): record and record-only run, with the report
    /// first, keeping every value that passes and leaving out the one the check refuses, which the first lines name with the
    /// value used in its place; a file that is not valid JSON gives the defaults and the command line, and says so.
    /// </summary>
    [TestCase("record", "Kalam", "--device", "auto", "--output", @"D:\Classes")]
    [TestCase("record-only", "Kalam", "--app", "chrome")]
    public async Task A_recording_runs_without_the_settings_the_check_refuses(params string[] args)
    {
        _settings.Write("""{ "Recording": { "FlushSeconds": "five", "SilenceWarningSeconds": 30 } }""");

        Assert.That(await RunAsync(args), Is.EqualTo(0));

        Assert.That(_order, Is.EqualTo(new[] { "report", "record" }));
        Assert.That(_gateLog.Problems[0], Is.EqualTo($"Recording:FlushSeconds is \"five\" in {_settings.File.Path}, which the check refuses; this recording uses the default, \"5\" in its place."));
        Assert.That(_settings.Configuration["Recording:SilenceWarningSeconds"], Is.EqualTo("30"), "what passes is kept");
        Assert.That(_settings.Configuration["Recording:FlushSeconds"], Is.Null, "what fails is left out");

        _settings.Write("{ not json");
        _gateLog = new RecordingLogger<RunGate>();
        Assert.That(await RunAsync(args), Is.EqualTo(0));
        Assert.That(_gateLog.Problems, Has.Some.StartsWith($"Nothing of {_settings.File.Path} could be taken, so this recording uses the settings ClassScribe read last"));
    }

    /// <summary>
    /// The settings are read and checked before each command. While they cannot be used, the command does not run, nor
    /// does the report at the start, and the exit code is 1; the log says what is wrong and that nothing was started.
    /// </summary>
    [TestCase("transcribe", @"C:\Classes\Kalam")]
    [TestCase("transcribe", @"C:\Classes\Kalam", "--again")]
    [TestCase("rebuild", @"C:\Classes\Kalam")]
    [TestCase("rebuild", @"C:\Classes\Kalam", "--no-open")]
    [TestCase("list")]
    [TestCase("devices")]
    public async Task A_command_does_not_run_while_the_settings_cannot_be_used(params string[] args)
    {
        BreakTheSettings();

        Assert.That(await RunAsync(args), Is.EqualTo(1));

        _commands.VerifyNoOtherCalls();
        _report.VerifyNoOtherCalls();
        Assert.That(_gateLog.Problems[0], Does.StartWith("Recording:FlushSeconds is \"five\""));
        Assert.That(_gateLog.Problems[^1], Does.StartWith("Nothing was started.").And.EndWith("then run the command again"));
    }

    /// <summary>A file dropped on the app is not transcribed while the settings cannot be used.</summary>
    [Test]
    public async Task A_dropped_file_is_not_transcribed_while_the_settings_cannot_be_used()
    {
        var video = _folder.File("Lecture 5.mp4");
        File.WriteAllBytes(video, [1, 2, 3]);
        BreakTheSettings();

        Assert.That(await RunAsync(video), Is.EqualTo(1));
        _commands.VerifyNoOtherCalls();
    }

    /// <summary>
    /// A mistake on the command line is reported first, with exit code 2, before the settings are read; help and the
    /// version need no settings at all. None of them runs the report at the start.
    /// </summary>
    [TestCase(2, "frobnicate")]
    [TestCase(2, "record")]
    [TestCase(2, "transcribe")]
    [TestCase(2, "record", "Kalam", "--device", "auto", "--output", @"D:\Classes", "--minutes", "1,5")]
    [TestCase(0, "--help")]
    [TestCase(0, "help")]
    [TestCase(0, "record", "--help")]
    [TestCase(0, "--version")]
    public async Task Mistakes_and_help_come_before_the_settings(int exitCode, params string[] args)
    {
        BreakTheSettings();

        Assert.That(await RunAsync(args), Is.EqualTo(exitCode));

        Assert.That(_gateLog.Messages, Is.Empty, "the settings were not read");
        _commands.VerifyNoOtherCalls();
        _report.VerifyNoOtherCalls();
    }

    /// <summary>
    /// Showing a recording in progress is never held up by the settings: a broken file must not keep anyone from seeing or
    /// stopping a recording. Nor is the report printed, which is about recordings that are not in progress.
    /// </summary>
    [Test]
    public async Task Attaching_is_never_held_up_by_the_settings()
    {
        BreakTheSettings();

        Assert.That(await RunAsync("attach", "--alert", "20261015-230002Z-1f3a9c07"), Is.EqualTo(0));

        _commands.Verify(c => c.AttachAsync("20261015-230002Z-1f3a9c07", true), Times.Once);
        Assert.That(_gateLog.Messages, Is.Empty, "the settings were not read");
        _report.VerifyNoOtherCalls();
    }

    /// <summary>
    /// settings is a command of its own kind (layout-config 1.8): it must be able to fix a file the gate refuses, so neither
    /// the gate nor the report at the start runs before it, and it runs with a broken file.
    /// </summary>
    [TestCase("settings", "list")]
    [TestCase("settings", "set", "Recording:FlushSeconds", "5")]
    [TestCase("SETTINGS", "reset", "--all")]
    public async Task Settings_runs_without_the_gate_and_the_report(params string[] args)
    {
        BreakTheSettings();
        _commands.Setup(c => c.SettingsAsync(It.IsAny<SettingsRequest>())).Callback(() => _order.Add("settings")).ReturnsAsync(0);

        Assert.That(await RunAsync(args), Is.EqualTo(0));

        Assert.That(_order, Is.EqualTo(new[] { "settings" }));
        Assert.That(_gateLog.Messages, Is.Empty, "the settings were not read by the gate");
        _report.VerifyNoOtherCalls();
    }

    /// <summary>The menu reads the settings itself before each choice, and shows the report itself, so App does neither before it.</summary>
    [Test]
    public async Task The_menu_reads_the_settings_itself()
    {
        BreakTheSettings();
        var app = CreateApp();
        _ui.Type('q');

        Assert.That(await app.RunAsync([]).WaitAsync(TimeSpan.FromSeconds(15)), Is.EqualTo(0));

        Assert.That(_ui.Screen, Does.Contain("Record only"));
        Assert.That(_gateLog.Messages, Is.Empty, "no choice was made, so the settings were not read");
        _report.VerifyNoOtherCalls();
    }

    /// <summary>
    /// Without a keyboard a menu is useless, so the usage is shown instead, with exit code 2. Should the menu be shown after
    /// all, it would wait for a key nobody presses, so the test fails after 15 seconds rather than hang the run.
    /// </summary>
    [Test]
    public async Task Without_arguments_or_a_keyboard_the_usage_is_shown()
    {
        var exitCode = await CreateApp(interactive: false).RunAsync([]).WaitAsync(TimeSpan.FromSeconds(15));

        Assert.That(exitCode, Is.EqualTo(2));
        Assert.That(_line.Output.ToString(), Does.Contain("transcribe"));
        _commands.VerifyNoOtherCalls();
    }

    /// <summary>The command line never asks: record without a name names what is missing and exits 2, even with someone at the keyboard.</summary>
    [Test]
    public async Task Nothing_is_asked_on_the_command_line()
    {
        var app = CreateApp();
        _ui.TypeLine("Kalam");

        Assert.That(await app.RunAsync(["record", "--device", "auto", "--output", @"D:\Classes"]).WaitAsync(TimeSpan.FromSeconds(15)), Is.EqualTo(2));

        Assert.That(_ui.Screen, Does.Not.Contain("Name for this recording"));
        Assert.That(_line.Errors.ToString(), Does.Contain("record needs a name"));
        _commands.VerifyNoOtherCalls();
    }

    /// <summary>Every command prints the report at the start, without asking anything, after the settings check and before it runs.</summary>
    [TestCase("record", new[] { "record", "Kalam", "--device", "auto", "--output", @"D:\Classes" })]
    [TestCase("record", new[] { "record-only", "Kalam", "--device", "auto", "--output", @"D:\Classes" })]
    [TestCase("transcribe", new[] { "transcribe", @"C:\Classes\Kalam" })]
    [TestCase("rebuild", new[] { "rebuild", @"C:\Classes\Kalam" })]
    [TestCase("list", new[] { "list" })]
    [TestCase("devices", new[] { "devices" })]
    public async Task The_report_at_the_start_comes_before_each_command(string command, string[] args)
    {
        Assert.That(await RunAsync(args), Is.EqualTo(0));

        Assert.That(_order, Is.EqualTo(new[] { "report", command }));
        _report.Verify(r => r.ShowAsync(false), Times.Once);
        Assert.That(_gateLog.Messages, Is.Empty, "the settings could be used, so the gate said nothing");
    }

    /// <summary>A file dropped on the app gets the report too, before it is transcribed.</summary>
    [Test]
    public async Task A_dropped_file_gets_the_report_first()
    {
        var video = _folder.File("Lecture 5.mp4");
        File.WriteAllBytes(video, [1, 2, 3]);

        await RunAsync(video);

        Assert.That(_order, Is.EqualTo(new[] { "report", "transcribe" }));
        _commands.Verify(c => c.TranscribeAsync(FromCommandLine(video)), Times.Once);
    }

    /// <summary>An edit that fixes the file counts for the next command: the settings are read again each time.</summary>
    [Test]
    public async Task A_fixed_file_counts_for_the_next_command()
    {
        string[] transcribe = ["transcribe", @"C:\Classes\Kalam"];
        BreakTheSettings();
        Assert.That(await RunAsync(transcribe), Is.EqualTo(1));
        _settings.Write("""{ "Recording": { "FlushSeconds": 5 } }""");

        Assert.That(await RunAsync(transcribe), Is.EqualTo(0));
        _commands.Verify(c => c.TranscribeAsync(FromCommandLine(@"C:\Classes\Kalam", true, false)), Times.Once);
    }

    /// <summary>A failure becomes exit code 1 with the reason logged, and a cancel exit code 130.</summary>
    [Test]
    public async Task Failures_and_cancels_have_their_own_exit_codes()
    {
        _commands.Setup(c => c.RecordAsync(It.Is<RecordRequest>(request => request.Transcribe), StartOrigin.CommandLine)).ThrowsAsync(new InvalidOperationException("No output device."));
        _commands.Setup(c => c.RecordAsync(It.Is<RecordRequest>(request => !request.Transcribe), StartOrigin.CommandLine)).ThrowsAsync(new OperationCanceledException());

        Assert.That(await RunAsync("record", "Kalam", "--device", "auto", "--output", @"D:\Classes"), Is.EqualTo(1));
        Assert.That(await RunAsync("record-only", "Kalam", "--device", "auto", "--output", @"D:\Classes"), Is.EqualTo(130));
        Assert.That(_log.Problems, Is.EqualTo(new[] { "ClassScribe stopped because of an error", "Cancelled" }));
    }

    /// <summary>
    /// App runs what was parsed with the command line's own invocation, the one that leaves Ctrl+C with ClassScribe: help
    /// asked for through App is written where the command line writes, not to the console System.CommandLine would use by
    /// itself.
    /// </summary>
    [Test]
    public async Task App_runs_the_command_line_with_its_own_invocation()
    {
        Assert.That(await RunAsync("devices", "--help"), Is.EqualTo(0));

        Assert.That(_line.Output.ToString(), Does.Contain("Usage:").And.Contain("ClassScribe devices"));
        _commands.VerifyNoOtherCalls();
    }

    /// <summary>A failure in the report at the start is a failure like any other: exit code 1, and the command does not run.</summary>
    [Test]
    public async Task A_failing_report_is_a_failure()
    {
        _report.Setup(r => r.ShowAsync(false)).ThrowsAsync(new IOException("The runs folder cannot be read."));

        Assert.That(await RunAsync("list"), Is.EqualTo(1));

        _commands.VerifyNoOtherCalls();
    }

    /// <summary>
    /// The command lines ClassScribe prints for later run as printed: transcribing a recording, transcribing it again
    /// with --again, and rebuilding it; and uploading to Google Drive a recording whose folder holds a $, a backtick and
    /// Arabic letters, and what waits; each handed over the way PowerShell splits the printed line.
    /// </summary>
    [Test]
    public async Task The_printed_command_lines_run_as_printed()
    {
        const string recording = @"C:\Users\gdala\ClassScribe\Kalam\Kalam_2026-10-14_19-30-00Z.wav";
        const string folder = @"C:\Program Files\ClassScribe\";
        var again = FromCommandLine(recording, again: true);
        _commands.Setup(c => c.DriveUploadAsync(It.IsAny<string?>())).ReturnsAsync(0);
        var course = Directory.CreateDirectory(Path.Combine(_folder.Path, "Uṣūl $al-fiqh `أصول`", "2026-10-17")).FullName;
        var upload = Path.Combine(course, "Uṣūl $al-fiqh `أصول`_2026-10-17_14-04-42Z.wav");
        File.WriteAllBytes(upload, [1]);

        await RunAsync(ArgumentsOf(CommandLine.Transcribe(recording, Exe, folder)));
        await RunAsync(ArgumentsOf(CommandLine.Transcribe(recording, Exe, folder, again: true)));
        await RunAsync(ArgumentsOf(CommandLine.Rebuild(recording, Exe, folder)));
        await RunAsync(ArgumentsOf(CommandLine.DriveUpload(upload, Exe, folder)));
        await RunAsync(ArgumentsOf(CommandLine.DriveUpload(null, Exe, folder)));

        _commands.Verify(c => c.TranscribeAsync(FromCommandLine(recording, true, false)), Times.Once);
        _commands.Verify(c => c.TranscribeAsync(again), Times.Once);
        _commands.Verify(c => c.RebuildAsync(recording, true), Times.Once);
        _commands.Verify(c => c.DriveUploadAsync(upload), Times.Once);
        _commands.Verify(c => c.DriveUploadAsync(null), Times.Once);
        _commands.VerifyNoOtherCalls();
    }

    /// <summary>
    /// The arguments are tidied before they are read: a command and its options in any case, and a stray quote from
    /// Windows PowerShell 5.1 taken off a path, with the options it joined to the path counted again.
    /// </summary>
    [Test]
    public async Task The_arguments_are_tidied_first()
    {
        await RunAsync("Transcribe", @"C:\Classes\Uṣūl al-dīn"" --AGAIN --no-open");

        _commands.Verify(c => c.TranscribeAsync(FromCommandLine(@"C:\Classes\Uṣūl al-dīn", open: false, again: true)), Times.Once);
        _commands.VerifyNoOtherCalls();
    }
}
