using ClassScribe.Commands;
using ClassScribe.Settings;
using ClassScribe.Storage;
using Microsoft.Extensions.Options;
using Moq;

namespace ClassScribe.Tests;

/// <summary>The start-up menu, driven by a scripted keyboard, with the commands and the desktop mocked.</summary>
[TestFixture]
public class MenuCommandTests
{
    private TempFolder _folder = null!;
    private FakeConsoleUi _ui = null!;
    private Mock<ICommandRunner> _commands = null!;
    private Mock<IDesktop> _desktop = null!;
    private RecordingLogger<MenuCommand> _log = null!;

    /// <summary>Creates a temporary recordings folder, the scripted keyboard and the mocks.</summary>
    [SetUp]
    public void SetUp()
    {
        _folder = new TempFolder();
        _ui = new FakeConsoleUi();
        _commands = new Mock<ICommandRunner>();
        _commands.Setup(c => c.RecordAsync(It.IsAny<bool>())).ReturnsAsync(0);
        _commands.Setup(c => c.TranscribeAsync(It.IsAny<string>())).ReturnsAsync(0);
        _desktop = new Mock<IDesktop>();
        _log = new RecordingLogger<MenuCommand>();
    }

    /// <summary>Deletes the temporary folder.</summary>
    [TearDown]
    public void TearDown() => _folder.Dispose();

    /// <summary>Runs the menu until it is left, failing the test rather than hanging if it never is.</summary>
    private Task<int> RunMenuAsync(string? recordings = null)
    {
        var folders = new ClassFolders(Options.Create(new RecordingOptions { OutputFolder = recordings ?? _folder.Path }), TimeProvider.System);
        var menu = new MenuCommand(_commands.Object, _ui, folders, _desktop.Object, TimeProvider.System, _log);
        return menu.RunAsync().WaitAsync(TimeSpan.FromSeconds(15));
    }

    /// <summary>Creates a recorded class folder, last recorded at the given time.</summary>
    private string WriteClass(string name, DateTime recordedUtc, bool withTranscript = false)
    {
        var folder = Directory.CreateDirectory(_folder.File(name)).FullName;
        var segment = Path.Combine(folder, "recording-001.wav");
        TestAudio.WriteWav(segment, 16000, new short[1600]);
        File.SetLastWriteTimeUtc(segment, recordedUtc);
        if (withTranscript) File.WriteAllText(Path.Combine(folder, "transcript.txt"), "[00:00:00] Hello.");
        return folder;
    }

    /// <summary>The menu lists its choices and where the files go; Q leaves it.</summary>
    [Test]
    public async Task The_menu_shows_its_choices_and_Q_quits()
    {
        _ui.Type('q');

        var exitCode = await RunMenuAsync();

        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(_ui.Screen, Does.Contain("1  Record only")
            .And.Contain("2  Record and transcribe")
            .And.Contain("3  Transcribe a recent class")
            .And.Contain("4  Transcribe a file")
            .And.Contain("5  Open the recordings folder")
            .And.Contain(_folder.Path));
        _commands.VerifyNoOtherCalls();
    }

    /// <summary>1 records only; 2 records and then transcribes. The menu comes back afterwards.</summary>
    [TestCase('1', false)]
    [TestCase('2', true)]
    public async Task The_record_choices_record_with_or_without_a_transcript(char choice, bool transcribe)
    {
        _ui.Type(choice, 'q');

        await RunMenuAsync();

        _commands.Verify(c => c.RecordAsync(transcribe), Times.Once);
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

    /// <summary>3 lists recent classes, newest first, and transcribes the one picked by its number.</summary>
    [Test]
    public async Task Three_transcribes_the_recent_class_picked()
    {
        var older = WriteClass("2026-10-11_14-05-00", new DateTime(2026, 10, 11, 16, 0, 0, DateTimeKind.Utc));
        WriteClass("2026-10-14_19-30-00", new DateTime(2026, 10, 14, 20, 0, 0, DateTimeKind.Utc));
        _ui.Type('3', '2', 'q');

        await RunMenuAsync();

        _commands.Verify(c => c.TranscribeAsync(older), Times.Once);
        Assert.That(_ui.Screen.IndexOf("2026-10-14_19-30-00", StringComparison.Ordinal),
            Is.LessThan(_ui.Screen.IndexOf("2026-10-11_14-05-00", StringComparison.Ordinal)), "newest first");
    }

    /// <summary>A class that already has a transcript is billed again, so it is only redone after a yes.</summary>
    [Test]
    public async Task A_class_with_a_transcript_is_only_redone_after_a_yes()
    {
        var done = WriteClass("2026-10-14_19-30-00", new DateTime(2026, 10, 14, 20, 0, 0, DateTimeKind.Utc), withTranscript: true);
        _ui.Type('3', '1', 'n', '3', '1', 'y', 'q');

        await RunMenuAsync();

        _commands.Verify(c => c.TranscribeAsync(done), Times.Once);
        Assert.That(_ui.Screen, Does.Contain("billed again"));
    }

    /// <summary>Esc leaves the list of classes without transcribing anything.</summary>
    [Test]
    public async Task Esc_leaves_the_class_list()
    {
        WriteClass("2026-10-14_19-30-00", new DateTime(2026, 10, 14, 20, 0, 0, DateTimeKind.Utc));
        _ui.Type('3', '\u001b', 'q');

        await RunMenuAsync();

        _commands.VerifyNoOtherCalls();
    }

    /// <summary>Without any recorded class the menu says so instead of showing an empty list.</summary>
    [Test]
    public async Task Without_recorded_classes_the_menu_says_so()
    {
        _ui.Type('3', 'q');

        await RunMenuAsync();

        Assert.That(_ui.Screen, Does.Contain("No recorded classes"));
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

        _commands.Verify(c => c.TranscribeAsync(video), Times.Once);
    }

    /// <summary>An empty line at the file prompt goes back to the menu.</summary>
    [Test]
    public async Task Four_with_an_empty_line_goes_back()
    {
        _ui.TypeLine("");
        _ui.Type('4', 'q');

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

    /// <summary>A choice that fails is reported, and the menu carries on.</summary>
    [Test]
    public async Task A_failed_choice_returns_to_the_menu()
    {
        _commands.Setup(c => c.RecordAsync(true)).ThrowsAsync(new InvalidOperationException("Windows reports no audio output device to record."));
        _ui.Type('2', '1', 'q');

        Assert.That(await RunMenuAsync(), Is.EqualTo(0));
        _commands.Verify(c => c.RecordAsync(false), Times.Once, "the menu was still there for the next choice");
        Assert.That(_log.Problems, Has.Count.EqualTo(1));
    }

    /// <summary>A choice cancelled with Ctrl+C returns to the menu.</summary>
    [Test]
    public async Task A_cancelled_choice_returns_to_the_menu()
    {
        _commands.Setup(c => c.RecordAsync(true)).ThrowsAsync(new OperationCanceledException());
        _ui.Type('2', 'q');

        Assert.That(await RunMenuAsync(), Is.EqualTo(0));
    }
}
