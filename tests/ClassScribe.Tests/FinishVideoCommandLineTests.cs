using ClassScribe.Commands;
using Moq;

namespace ClassScribe.Tests;

/// <summary>
/// finish-video on the command line (teach-video 1.5): a lesson's folder or file, passed on as a full path, with --no-open; no
/// path, or one that is not there, is a mistake, exit 2, with the help.
/// </summary>
[TestFixture]
public sealed class FinishVideoCommandLineTests
{
    private TempFolder _folder = null!;
    private TempFolder _settingsFolder = null!;
    private Mock<ICommandRunner> _commands = null!;
    private TestCommandLine _line = null!;
    private List<FinishVideoRequest> _asked = null!;

    /// <summary>The folders, the mocked commands, which succeed, and the command line over a console a person can type into.</summary>
    [SetUp]
    public void SetUp()
    {
        _folder = new TempFolder();
        _settingsFolder = new TempFolder();
        _asked = [];
        _commands = new Mock<ICommandRunner>();
        _commands.Setup(c => c.FinishVideoAsync(It.IsAny<FinishVideoRequest>())).Callback((FinishVideoRequest request) => _asked.Add(request)).ReturnsAsync(0);
        _line = new TestCommandLine(_commands.Object, new FakeConsoleUi { IsInteractive = true }, new TestSettings(_settingsFolder.Path), _folder.Path,
            new ManualTimeProvider(new DateTimeOffset(2026, 10, 19, 14, 0, 2, TimeSpan.Zero)));
    }

    /// <summary>Deletes the folders.</summary>
    [TearDown]
    public void TearDown()
    {
        _folder.Dispose();
        _settingsFolder.Dispose();
    }

    /// <summary>Runs the command line, failing the test rather than waiting.</summary>
    private Task<int> RunAsync(params string[] args) => _line.RunAsync(args).WaitAsync(TimeSpan.FromSeconds(15));

    /// <summary>finish-video takes a lesson's folder or file as a full path, and opens the folder at the end unless --no-open says otherwise.</summary>
    [TestCase(false)]
    [TestCase(true)]
    public async Task Finish_video_takes_its_path(bool noOpen)
    {
        var lesson = Directory.CreateDirectory(_folder.File("Tajwid")).FullName;
        string[] args = noOpen ? ["finish-video", lesson, "--no-open"] : ["Finish-Video", lesson];

        Assert.That(await RunAsync(args), Is.EqualTo(0));
        Assert.That(_asked, Is.EqualTo(new[] { new FinishVideoRequest(lesson, Open: !noOpen) }));
    }

    /// <summary>No path, or one that is not there, is a mistake: exit 2, nothing run, the help written.</summary>
    [Test]
    public async Task A_missing_path_is_a_mistake()
    {
        Assert.That(await RunAsync("finish-video"), Is.EqualTo(2));
        Assert.That(await RunAsync("finish-video", _folder.File("nowhere")), Is.EqualTo(2));
        Assert.That(_asked, Is.Empty);
        Assert.That(_line.Errors.ToString(), Does.Contain($"Nothing found at {_folder.File("nowhere")}"));
        Assert.That(_line.Output.ToString(), Does.Contain("Usage:"));
    }
}
