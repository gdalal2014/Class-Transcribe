using System.Text.RegularExpressions;
using ClassScribe.Audio;
using ClassScribe.Commands;
using ClassScribe.Settings;
using ClassScribe.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ClassScribe.Tests;

/// <summary>What each command line does, with the commands mocked and the settings read through the gate from a file of the test's own.</summary>
[TestFixture]
public class AppTests
{
    /// <summary>The program the printed command lines of these tests name.</summary>
    private const string Exe = @"C:\Program Files\ClassScribe\ClassScribe.exe";

    private TempFolder _folder = null!;
    private TempFolder _settingsFolder = null!;
    private TestSettings _settings = null!;
    private RecordingLogger<RunGate> _gateLog = null!;
    private Mock<ICommandRunner> _commands = null!;
    private FakeConsoleUi _ui = null!;

    /// <summary>Creates a temporary folder, settings that can be used, and the mocked commands.</summary>
    [SetUp]
    public void SetUp()
    {
        _folder = new TempFolder();
        _settingsFolder = new TempFolder();
        _settings = new TestSettings(_settingsFolder.Path);
        _gateLog = new RecordingLogger<RunGate>();
        _commands = new Mock<ICommandRunner>();
        _commands.Setup(c => c.RecordAsync(It.IsAny<string>(), It.IsAny<bool>())).ReturnsAsync(0);
        _commands.Setup(c => c.TranscribeAsync(It.IsAny<TranscribeRequest>())).ReturnsAsync(0);
        _commands.Setup(c => c.RebuildAsync(It.IsAny<string>(), It.IsAny<bool>())).ReturnsAsync(0);
    }

    /// <summary>What transcribing <paramref name="path"/> from the command line asks for, with the options given.</summary>
    private static TranscribeRequest FromCommandLine(string path, bool open = true, bool again = false) =>
        new(path, Open: open, Again: again, FromCommandLine: true);

    /// <summary>
    /// The arguments a command line printed by <see cref="CommandLine"/> hands the program, as PowerShell splits it: the
    /// words after the program, each quoted path as one argument without its quotes.
    /// </summary>
    private static string[] ArgumentsOf(string printed)
    {
        var words = Regex.Matches(printed, "\"([^\"]*)\"|(\\S+)")
            .Select(match => match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value)
            .ToArray();
        Assert.That(words[..2], Is.EqualTo(new[] { "&", Exe }), "the program, named by its full path");
        return words[2..];
    }

    /// <summary>Deletes the temporary folders.</summary>
    [TearDown]
    public void TearDown()
    {
        _folder.Dispose();
        _settingsFolder.Dispose();
    }

    /// <summary>Creates the app with a scripted keyboard that a person can, or cannot, type on.</summary>
    private App CreateApp(bool interactive = true)
    {
        _ui = new FakeConsoleUi { IsInteractive = interactive };
        var recording = TestOptions.Create(new RecordingOptions { OutputFolder = _folder.Path });
        var folders = new ClassFolders(recording, TimeProvider.System, NullLogger<ClassFolders>.Instance);
        var prompt = new RecordingNamePrompt(_ui);
        var gate = _settings.Gate(_gateLog);
        var menu = new MenuCommand(_commands.Object, _ui, gate, _settings.File, folders, new FakeCaptureProvider(), new SourceChoice(recording), prompt, Mock.Of<IDesktop>(), TimeProvider.System, NullLogger<MenuCommand>.Instance);
        return new App(_commands.Object, menu, prompt, _ui, gate, NullLogger<App>.Instance);
    }

    /// <summary>Makes the settings file one that cannot be used: a value that is not a whole number.</summary>
    private void BreakTheSettings() => _settings.Write("""{ "Recording": { "FlushSeconds": "five" } }""");

    /// <summary>
    /// The settings are read and checked before each command. While they cannot be used, the command does not run and
    /// the exit code is 1; the log says what is wrong and that nothing was started.
    /// </summary>
    [TestCase("record", "Kalam")]
    [TestCase("record-only", "Kalam")]
    [TestCase("transcribe", @"C:\Classes\Kalam")]
    [TestCase("transcribe", @"C:\Classes\Kalam", "--again")]
    [TestCase("rebuild", @"C:\Classes\Kalam")]
    [TestCase("rebuild", @"C:\Classes\Kalam", "--no-open")]
    public async Task A_command_does_not_run_while_the_settings_cannot_be_used(string command, string argument, string? option = null)
    {
        BreakTheSettings();

        var exitCode = await CreateApp().RunAsync(option is null ? [command, argument] : [command, argument, option]);

        Assert.That(exitCode, Is.EqualTo(1));
        _commands.VerifyNoOtherCalls();
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

        Assert.That(await CreateApp().RunAsync([video]), Is.EqualTo(1));
        _commands.VerifyNoOtherCalls();
    }

    /// <summary>Record without a name does not ask for one while the settings cannot be used: nothing could be recorded under it.</summary>
    [Test]
    public async Task Record_without_a_name_does_not_ask_while_the_settings_cannot_be_used()
    {
        BreakTheSettings();
        var app = CreateApp();
        _ui.TypeLine("Kalam");

        Assert.That(await app.RunAsync(["record"]), Is.EqualTo(1));
        Assert.That(_ui.Screen, Does.Not.Contain("Name for this recording"));
        _commands.VerifyNoOtherCalls();
    }

    /// <summary>A mistake on the command line is reported first, with exit code 2, before the settings are read; help needs no settings at all.</summary>
    [TestCase(false, "frobnicate", 2)]
    [TestCase(false, "record", 2)]
    [TestCase(true, "help", 0)]
    public async Task Usage_and_help_come_before_the_settings(bool interactive, string argument, int expectedExitCode)
    {
        BreakTheSettings();

        var exitCode = await CreateApp(interactive).RunAsync([argument]);

        Assert.That(exitCode, Is.EqualTo(expectedExitCode));
        Assert.That(_gateLog.Messages, Is.Empty, "the settings were not read");
        _commands.VerifyNoOtherCalls();
    }

    /// <summary>An edit that fixes the file counts for the next command: the settings are read again each time.</summary>
    [Test]
    public async Task A_fixed_file_counts_for_the_next_command()
    {
        BreakTheSettings();
        Assert.That(await CreateApp().RunAsync(["record", "Kalam"]), Is.EqualTo(1));
        _settings.Write("""{ "Recording": { "FlushSeconds": 5 } }""");

        Assert.That(await CreateApp().RunAsync(["record", "Kalam"]), Is.EqualTo(0));
        _commands.Verify(c => c.RecordAsync("Kalam", true), Times.Once);
    }

    /// <summary>Started without arguments by someone at the keyboard, the app shows the menu.</summary>
    [Test]
    public async Task Without_arguments_the_menu_is_shown()
    {
        var app = CreateApp();
        _ui.Type('q');

        var exitCode = await app.RunAsync([]).WaitAsync(TimeSpan.FromSeconds(15));

        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(_ui.Screen, Does.Contain("Record only"));
    }

    /// <summary>Without a keyboard a menu is useless, so the usage is shown instead.</summary>
    [Test]
    public async Task Without_arguments_or_a_keyboard_the_usage_is_shown()
    {
        var exitCode = await CreateApp(interactive: false).RunAsync([]);

        Assert.That(exitCode, Is.EqualTo(2));
        Assert.That(_ui.Screen, Does.Contain("transcribe <path>"));
        _commands.VerifyNoOtherCalls();
    }

    /// <summary>"record" records and transcribes; "record-only" only records; both take the recording's name.</summary>
    [TestCase("record", true)]
    [TestCase("record-only", false)]
    public async Task The_record_commands_record_under_the_name_given(string command, bool transcribe)
    {
        var exitCode = await CreateApp().RunAsync([command, "Kalam lesson 12"]);

        Assert.That(exitCode, Is.EqualTo(0));
        _commands.Verify(c => c.RecordAsync("Kalam lesson 12", transcribe), Times.Once);
    }

    /// <summary>Without a name on the command line, someone at the keyboard is asked for one.</summary>
    [Test]
    public async Task Record_without_a_name_asks_for_one()
    {
        var app = CreateApp();
        _ui.TypeLine("Kalam");

        var exitCode = await app.RunAsync(["record"]);

        Assert.That(exitCode, Is.EqualTo(0));
        _commands.Verify(c => c.RecordAsync("Kalam", true), Times.Once);
    }

    /// <summary>Without a name and without a keyboard, there is nothing to record under, so the usage is shown.</summary>
    [Test]
    public async Task Record_without_a_name_or_a_keyboard_shows_the_usage()
    {
        var exitCode = await CreateApp(interactive: false).RunAsync(["record-only"]);

        Assert.That(exitCode, Is.EqualTo(2));
        _commands.VerifyNoOtherCalls();
    }

    /// <summary>"transcribe" takes the path that follows it, as a request from the command line, which nobody is asked about.</summary>
    [Test]
    public async Task The_transcribe_command_transcribes_its_path()
    {
        Assert.That(await CreateApp().RunAsync(["transcribe", @"C:\Classes\Kalam"]), Is.EqualTo(0));

        _commands.Verify(c => c.TranscribeAsync(FromCommandLine(@"C:\Classes\Kalam")), Times.Once);
        _commands.VerifyNoOtherCalls();
    }

    /// <summary>
    /// "transcribe" takes --again, to transcribe a recording that already has a transcript, and --no-open, to open
    /// nothing at the end: before or after the path, in any order and in any case.
    /// </summary>
    [TestCase(new[] { "transcribe", @"C:\Classes\Kalam", "--again" }, true, true)]
    [TestCase(new[] { "transcribe", @"C:\Classes\Kalam", "--no-open" }, false, false)]
    [TestCase(new[] { "transcribe", @"C:\Classes\Kalam", "--no-open", "--again" }, true, false)]
    [TestCase(new[] { "transcribe", "--AGAIN", @"C:\Classes\Kalam" }, true, true)]
    [TestCase(new[] { "Transcribe", "--No-Open", @"C:\Classes\Kalam", "--again" }, true, false)]
    public async Task Transcribe_takes_again_and_no_open(string[] args, bool again, bool open)
    {
        Assert.That(await CreateApp().RunAsync(args), Is.EqualTo(0));

        _commands.Verify(c => c.TranscribeAsync(FromCommandLine(@"C:\Classes\Kalam", open: open, again: again)), Times.Once);
        _commands.VerifyNoOtherCalls();
    }

    /// <summary>"rebuild" rebuilds the transcript of the path that follows it, opening it at the end unless --no-open says otherwise.</summary>
    [TestCase(new[] { "rebuild", @"C:\Classes\Kalam" }, true)]
    [TestCase(new[] { "rebuild", @"C:\Classes\Kalam", "--no-open" }, false)]
    [TestCase(new[] { "REBUILD", "--NO-OPEN", @"C:\Classes\Kalam" }, false)]
    public async Task Rebuild_rebuilds_its_path(string[] args, bool open)
    {
        Assert.That(await CreateApp().RunAsync(args), Is.EqualTo(0));

        _commands.Verify(c => c.RebuildAsync(@"C:\Classes\Kalam", open), Times.Once);
        _commands.VerifyNoOtherCalls();
    }

    /// <summary>
    /// A command line that transcribe or rebuild cannot take is a mistake: the usage is shown, the exit code is 2, and
    /// nothing runs. That is a missing path, a second path, an option the command does not take, and one it does not know.
    /// </summary>
    [TestCase("transcribe")]
    [TestCase("transcribe", "--again")]
    [TestCase("transcribe", @"C:\Classes\Kalam", @"C:\Classes\Fiqh")]
    [TestCase("transcribe", @"C:\Classes\Kalam", "--output", @"C:\Transcripts")]
    [TestCase("rebuild")]
    [TestCase("rebuild", "--no-open")]
    [TestCase("rebuild", @"C:\Classes\Kalam", "--again")]
    [TestCase("rebuild", @"C:\Classes\Kalam", @"C:\Classes\Fiqh")]
    public async Task A_command_line_transcribe_or_rebuild_cannot_take_shows_the_usage(params string[] args)
    {
        Assert.That(await CreateApp().RunAsync(args), Is.EqualTo(2));

        Assert.That(_ui.Screen, Does.Contain("rebuild <path>"));
        _commands.VerifyNoOtherCalls();
    }

    /// <summary>The usage names the rebuild command and the options of transcribe and rebuild.</summary>
    [Test]
    public async Task The_usage_names_rebuild_and_the_options()
    {
        await CreateApp().RunAsync(["help"]);

        Assert.That(_ui.Screen, Does.Contain("ClassScribe rebuild <path>").And.Contain("--again").And.Contain("--no-open"));
    }

    /// <summary>
    /// The command lines ClassScribe prints for later run as printed: transcribing a recording, transcribing it again
    /// with --again, and rebuilding it, each handed over the way PowerShell splits the printed line.
    /// </summary>
    [Test]
    public async Task The_printed_command_lines_run_as_printed()
    {
        const string recording = @"C:\Users\gdala\ClassScribe\Kalam\Kalam_2026-10-14_19-30-00Z.wav";
        const string folder = @"C:\Program Files\ClassScribe\";
        var again = FromCommandLine(recording, again: true);

        await CreateApp().RunAsync(ArgumentsOf(CommandLine.Transcribe(recording, Exe, folder)));
        await CreateApp().RunAsync(ArgumentsOf(CommandLine.Transcribe(recording, Exe, folder, again: true)));
        await CreateApp().RunAsync(ArgumentsOf(CommandLine.Rebuild(recording, Exe, folder)));

        _commands.Verify(c => c.TranscribeAsync(FromCommandLine(recording)), Times.Once);
        _commands.Verify(c => c.TranscribeAsync(again), Times.Once);
        _commands.Verify(c => c.RebuildAsync(recording, true), Times.Once);
        _commands.VerifyNoOtherCalls();
    }

    /// <summary>A file or folder dropped on the exe arrives as its only argument and is transcribed, as from the command line: nobody is asked about it.</summary>
    [Test]
    public async Task A_file_or_folder_dropped_on_the_app_is_transcribed()
    {
        var video = _folder.File("Lecture 5.mp4");
        File.WriteAllBytes(video, [1, 2, 3]);

        await CreateApp().RunAsync([video]);
        await CreateApp().RunAsync([_folder.Path]);

        _commands.Verify(c => c.TranscribeAsync(FromCommandLine(video)), Times.Once);
        _commands.Verify(c => c.TranscribeAsync(FromCommandLine(_folder.Path)), Times.Once);
    }

    /// <summary>
    /// Windows PowerShell 5.1 hands a quoted folder that ends in a backslash over with a stray quote at
    /// the end, which tab completion produces whenever the path has a space. A quote cannot be part of a
    /// Windows path, so it is taken off, for the transcribe command and for a bare path alike.
    /// </summary>
    [Test]
    public async Task A_stray_quote_from_PowerShell_is_taken_off_the_path()
    {
        var withStrayQuote = _folder.Path + "\"";

        await CreateApp().RunAsync(["transcribe", withStrayQuote]);
        await CreateApp().RunAsync([withStrayQuote]);
        await CreateApp().RunAsync(["rebuild", withStrayQuote]);

        _commands.Verify(c => c.TranscribeAsync(FromCommandLine(_folder.Path)), Times.Exactly(2));
        _commands.Verify(c => c.RebuildAsync(_folder.Path, true), Times.Once);
    }

    /// <summary>
    /// When an option follows such a path, the stray quote also joins the option to the path: PowerShell 5.1 passes
    /// "C:\Classes\Uṣūl al-dīn\" --again, and Windows reads the backslash and quote as a quote inside one argument.
    /// A quote cannot be part of a Windows path, so the argument is split there, and the option counts.
    /// </summary>
    [Test]
    public async Task An_option_joined_to_a_path_by_a_stray_quote_still_counts()
    {
        await CreateApp().RunAsync(["transcribe", @"C:\Classes\Uṣūl al-dīn"" --again --no-open"]);
        await CreateApp().RunAsync(["rebuild", @"C:\Classes\Uṣūl al-dīn"" --no-open"]);

        _commands.Verify(c => c.TranscribeAsync(FromCommandLine(@"C:\Classes\Uṣūl al-dīn", open: false, again: true)), Times.Once);
        _commands.Verify(c => c.RebuildAsync(@"C:\Classes\Uṣūl al-dīn", false), Times.Once);
        _commands.VerifyNoOtherCalls();
    }

    /// <summary>
    /// Only options are taken out of an argument at a quote inside it: a recording's name with quotes in it stays one
    /// name, as before, so that a class starts under it rather than with the usage. Its quotes are left to the name's
    /// own tidying when the recording is made.
    /// </summary>
    [Test]
    public async Task A_name_with_quotes_inside_stays_one_argument()
    {
        Assert.That(await CreateApp().RunAsync(["record", "Reading \"Kitāb al-Tawḥīd\""]), Is.EqualTo(0));

        _commands.Verify(c => c.RecordAsync("Reading \"Kitāb al-Tawḥīd", true), Times.Once);
        _commands.VerifyNoOtherCalls();
    }

    /// <summary>A recording's name that looks like an option keeps its case: only the options transcribe and rebuild take are tidied.</summary>
    [Test]
    public async Task A_name_that_looks_like_an_option_keeps_its_case()
    {
        await CreateApp().RunAsync(["record-only", "--Kalam"]);

        _commands.Verify(c => c.RecordAsync("--Kalam", false), Times.Once);
    }

    /// <summary>Command names are taken in any case, so Transcribe, typed by habit, is not an unknown command.</summary>
    [Test]
    public async Task Command_names_are_taken_in_any_case()
    {
        await CreateApp().RunAsync(["Transcribe", @"C:\Classes\Kalam"]);
        await CreateApp().RunAsync(["RECORD-ONLY", "Kalam"]);
        await CreateApp().RunAsync(["Rebuild", @"C:\Classes\Kalam"]);

        _commands.Verify(c => c.TranscribeAsync(FromCommandLine(@"C:\Classes\Kalam")), Times.Once);
        _commands.Verify(c => c.RecordAsync("Kalam", false), Times.Once);
        _commands.Verify(c => c.RebuildAsync(@"C:\Classes\Kalam", true), Times.Once);
    }

    /// <summary>Asking for help shows the usage and succeeds; anything unknown shows it and fails.</summary>
    [TestCase("help", 0)]
    [TestCase("--help", 0)]
    [TestCase("frobnicate", 2)]
    public async Task Help_and_unknown_commands_show_the_usage(string argument, int expectedExitCode)
    {
        var exitCode = await CreateApp().RunAsync([argument]);

        Assert.That(exitCode, Is.EqualTo(expectedExitCode));
        Assert.That(_ui.Screen, Does.Contain("record-only").And.Contain("Q twice (or Ctrl+C) stops"));
        _commands.VerifyNoOtherCalls();
    }

    /// <summary>A failure becomes exit code 1, and a cancel exit code 130.</summary>
    [Test]
    public async Task Failures_and_cancels_have_their_own_exit_codes()
    {
        _commands.Setup(c => c.RecordAsync("Kalam", true)).ThrowsAsync(new InvalidOperationException("No output device."));
        _commands.Setup(c => c.RecordAsync("Kalam", false)).ThrowsAsync(new OperationCanceledException());

        Assert.That(await CreateApp().RunAsync(["record", "Kalam"]), Is.EqualTo(1));
        Assert.That(await CreateApp().RunAsync(["record-only", "Kalam"]), Is.EqualTo(130));
    }
}
