using ClassScribe.Audio;
using ClassScribe.Commands;
using ClassScribe.Settings;
using ClassScribe.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace ClassScribe.Tests;

/// <summary>What each command line does, with the commands mocked.</summary>
[TestFixture]
public class AppTests
{
    private TempFolder _folder = null!;
    private Mock<ICommandRunner> _commands = null!;
    private FakeConsoleUi _ui = null!;

    /// <summary>Creates a temporary folder and the mocked commands.</summary>
    [SetUp]
    public void SetUp()
    {
        _folder = new TempFolder();
        _commands = new Mock<ICommandRunner>();
        _commands.Setup(c => c.RecordAsync(It.IsAny<string>(), It.IsAny<bool>())).ReturnsAsync(0);
        _commands.Setup(c => c.TranscribeAsync(It.IsAny<string>())).ReturnsAsync(0);
    }

    /// <summary>Deletes the temporary folder.</summary>
    [TearDown]
    public void TearDown() => _folder.Dispose();

    /// <summary>Creates the app with a scripted keyboard that a person can, or cannot, type on.</summary>
    private App CreateApp(bool interactive = true)
    {
        _ui = new FakeConsoleUi { IsInteractive = interactive };
        var recording = Options.Create(new RecordingOptions { OutputFolder = _folder.Path });
        var folders = new ClassFolders(recording, TimeProvider.System, NullLogger<ClassFolders>.Instance);
        var prompt = new RecordingNamePrompt(_ui);
        var menu = new MenuCommand(_commands.Object, _ui, folders, new FakeCaptureProvider(), new SourceChoice(recording), prompt, Mock.Of<IDesktop>(), TimeProvider.System, NullLogger<MenuCommand>.Instance);
        return new App(_commands.Object, menu, prompt, _ui, NullLogger<App>.Instance);
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

    /// <summary>"transcribe" takes the path that follows it.</summary>
    [Test]
    public async Task The_transcribe_command_transcribes_its_path()
    {
        await CreateApp().RunAsync(["transcribe", @"C:\Classes\Kalam"]);

        _commands.Verify(c => c.TranscribeAsync(@"C:\Classes\Kalam"), Times.Once);
    }

    /// <summary>A file or folder dropped on the exe arrives as its only argument and is transcribed.</summary>
    [Test]
    public async Task A_file_or_folder_dropped_on_the_app_is_transcribed()
    {
        var video = _folder.File("Lecture 5.mp4");
        File.WriteAllBytes(video, [1, 2, 3]);

        await CreateApp().RunAsync([video]);
        await CreateApp().RunAsync([_folder.Path]);

        _commands.Verify(c => c.TranscribeAsync(video), Times.Once);
        _commands.Verify(c => c.TranscribeAsync(_folder.Path), Times.Once);
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

        _commands.Verify(c => c.TranscribeAsync(_folder.Path), Times.Exactly(2));
    }

    /// <summary>Command names are taken in any case, so Transcribe, typed by habit, is not an unknown command.</summary>
    [Test]
    public async Task Command_names_are_taken_in_any_case()
    {
        await CreateApp().RunAsync(["Transcribe", @"C:\Classes\Kalam"]);
        await CreateApp().RunAsync(["RECORD-ONLY", "Kalam"]);

        _commands.Verify(c => c.TranscribeAsync(@"C:\Classes\Kalam"), Times.Once);
        _commands.Verify(c => c.RecordAsync("Kalam", false), Times.Once);
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
