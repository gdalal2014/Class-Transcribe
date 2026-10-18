using ClassScribe.Commands;
using ClassScribe.YouTube;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ClassScribe.Tests;

/// <summary>
/// youtube on the command line (design 1.1, with Gulzar's answer 7): links and a list file, joined in order, each taken once;
/// --name, --output, --download, and exactly one of --yes and --plan; every mistake named with the command's help and exit
/// code 2, a list file's by its line; and youtube check. The commands are mocked.
/// </summary>
[TestFixture]
public class YouTubeCommandLineTests
{
    private TempFolder _folder = null!;
    private TempFolder _settingsFolder = null!;
    private Mock<ICommandRunner> _commands = null!;
    private List<YouTubeRequest> _asked = null!;
    private TestCommandLine _line = null!;

    /// <summary>The folders, the mocked commands, which succeed, and the command line.</summary>
    [SetUp]
    public void SetUp()
    {
        _folder = new TempFolder();
        _settingsFolder = new TempFolder();
        _asked = [];
        _commands = new Mock<ICommandRunner>();
        _commands.Setup(c => c.YouTubeAsync(It.IsAny<YouTubeRequest>())).Callback((YouTubeRequest request) => _asked.Add(request)).ReturnsAsync(0);
        _commands.Setup(c => c.YouTubeCheckAsync()).ReturnsAsync(0);
        _line = new TestCommandLine(_commands.Object, new FakeConsoleUi(), new TestSettings(_settingsFolder.Path), _folder.Path, new ManualTimeProvider(TestRuns.Start));
    }

    /// <summary>Deletes the folders.</summary>
    [TearDown]
    public void TearDown()
    {
        _folder.Dispose();
        _settingsFolder.Dispose();
    }

    /// <summary>Runs the command line.</summary>
    private Task<int> RunAsync(params string[] args) => _line.RunAsync(args).WaitAsync(TimeSpan.FromSeconds(15));

    /// <summary>The recordings folder these tests give as --output.</summary>
    private string Classes => _folder.File("Classes");

    /// <summary>
    /// Links on the line and a list file are joined in order, each taken once, with a line for each one given again; the
    /// request carries the course, the folder as a full path, and the options.
    /// </summary>
    [Test]
    public async Task Links_and_a_list_file_are_joined_in_order()
    {
        var list = _folder.File("risala-links.txt");
        File.WriteAllText(list, "# the creed\nhttps://youtu.be/bcdefghijkl  https://www.youtube.com/playlist?list=PLxyz\nhttps://youtu.be/abcdefghijk\n");

        var exitCode = await RunAsync("youtube", "https://youtu.be/abcdefghijk", "--list", list, "--name", "Risala", "--output", Classes, "--download", "--yes",
            "--no-transcribe", "--no-open", "--again", "--allow-unlisted");

        Assert.That(exitCode, Is.EqualTo(0));
        var request = _asked.Single();
        Assert.That(request.Links.Select(link => link.Id), Is.EqualTo(["abcdefghijk", "bcdefghijkl", "PLxyz"]));
        Assert.That((request.Name, request.OutputRoot, request.Download, request.Confirmation), Is.EqualTo(("Risala", Classes, true, BatchConfirmation.Yes)));
        Assert.That((request.Transcribe, request.Open, request.Again, request.AllowUnlisted), Is.EqualTo((false, false, true, true)));
        Assert.That(_line.Output.ToString(), Does.Contain("Line 3 of risala-links.txt lists the video abcdefghijk again; it is taken once"));
    }

    /// <summary>--plan asks for the plan alone; the defaults transcribe, open the folder, and fetch nothing again.</summary>
    [Test]
    public async Task Plan_asks_for_the_plan_alone()
    {
        await RunAsync("youtube", "https://youtu.be/abcdefghijk", "--name", "Risala", "--output", Classes, "--download", "--plan");

        Assert.That(_asked.Single(), Is.EqualTo(new YouTubeRequest(_asked.Single().Links, "Risala", Classes, true, BatchConfirmation.PlanOnly)));
    }

    /// <summary>Every mistake is named in one run, with the help and exit code 2, and nothing runs.</summary>
    [Test]
    public async Task Every_mistake_is_named_at_once()
    {
        var exitCode = await RunAsync("youtube");

        Assert.That(exitCode, Is.EqualTo(CommandLineTree.Mistake));
        Assert.That(_line.Mistakes, Is.EqualTo(new[]
        {
            "youtube needs --name \"<course>\", the course the videos belong to, such as --name \"Risala\"",
            "youtube needs --output \"<folder>\", the folder the recordings go to",
            "youtube needs at least one YouTube link, or --list \"<file>\" of links",
            "youtube needs --yes to go ahead after the plan, or --plan to print the plan alone",
            "youtube needs --download: recording YouTube videos as they play is not part of this build yet"
        }));
        Assert.That(_line.Output.ToString(), Does.Contain("Examples:"));
        _commands.Verify(c => c.YouTubeAsync(It.IsAny<YouTubeRequest>()), Times.Never);
    }

    /// <summary>
    /// A word that is no link the batch takes is named by where it stands, a list file's by its line: a channel's link, an
    /// embedded player's, one that is not YouTube's; a list file that is not there; --yes with --plan.
    /// </summary>
    [Test]
    public async Task A_word_that_is_no_link_is_named_by_where_it_stands()
    {
        var list = _folder.File("links.txt");
        File.WriteAllText(list, "https://youtu.be/abcdefghijk\nhttps://www.youtube.com/@teacher\n");

        var exitCode = await RunAsync("youtube", "https://www.youtube.com/embed/abcdefghijk", "--list", list, "--name", "Risala", "--output", Classes, "--download",
            "--yes", "--plan");

        Assert.That(exitCode, Is.EqualTo(CommandLineTree.Mistake));
        Assert.That(_line.Mistakes, Has.Some.StartsWith($"The command line: {YouTubeLinks.EmbeddedPlayer}"));
        Assert.That(_line.Mistakes, Has.Some.StartsWith("Line 2 of links.txt: \"https://www.youtube.com/@teacher\" is a channel's link"));
        Assert.That(_line.Mistakes, Has.Some.EqualTo("--yes and --plan cannot go together"));
        Assert.That(await RunAsync("youtube", "--list", _folder.File("none.txt"), "--name", "Risala", "--output", Classes, "--download", "--yes"), Is.EqualTo(CommandLineTree.Mistake));
        Assert.That(_line.Mistakes, Has.Some.EqualTo($"Nothing found at {_folder.File("none.txt")}"));
    }

    /// <summary>youtube check runs the check; anything after it is a mistake.</summary>
    [Test]
    public async Task Check_runs_the_check_alone()
    {
        Assert.That(await RunAsync("youtube", "check"), Is.EqualTo(0));
        _commands.Verify(c => c.YouTubeCheckAsync(), Times.Once);

        Assert.That(await RunAsync("youtube", "check", "--yes"), Is.EqualTo(CommandLineTree.Mistake));
    }

    /// <summary>The help of youtube and of youtube check says what they do, with examples.</summary>
    [Test]
    public async Task The_help_has_examples()
    {
        Assert.That(await RunAsync("youtube", "--help"), Is.EqualTo(0));
        Assert.That(_line.Output.ToString(), Does.Contain("--allow-unlisted").And.Contain("ClassScribe youtube check"));
    }

    /// <summary>
    /// --allow-unlisted is refused with a plain message and exit code 2 while YouTube:CourseChannels is empty, so that a
    /// course video, which course pages embed unlisted, is never taken for one he may download; and a batch without
    /// --download is refused in this build. Nothing is run either way.
    /// </summary>
    [Test]
    public async Task Allow_unlisted_needs_the_course_channels()
    {
        using var world = new YouTubeWorld();
        world.YouTube.Set(new Settings.YouTubeOptions());
        var log = new RecordingLogger<YouTubeCommand>();
        var command = new YouTubeCommand(world.Batch(), null!, new FakeUserSignals(), world.Ui, world.YouTube, log);

        var exitCode = await command.RunAsync(world.Request(["https://youtu.be/abcdefghijk"], allowUnlisted: true));

        Assert.That(exitCode, Is.EqualTo(2));
        Assert.That(log.Problems.Single(), Is.EqualTo(YouTubeCommand.AllowUnlistedNeedsCourseChannels));
        Assert.That(log.Problems.Single(), Does.Contain("list them in YouTube:CourseChannels first"));
        Assert.That(world.Tools.Calls, Is.Empty);

        var withoutDownload = await command.RunAsync(world.Request(["https://youtu.be/abcdefghijk"]) with { Download = false });

        Assert.That(withoutDownload, Is.EqualTo(2));
        Assert.That(world.Tools.Calls, Is.Empty);
    }

    /// <summary>More than 500 videos, once the ones given twice are taken once, is a mistake of the command line.</summary>
    [Test]
    public async Task More_than_five_hundred_videos_is_a_mistake()
    {
        var list = _folder.File("many.txt");
        File.WriteAllLines(list, Enumerable.Range(0, DownloadBatch.MostVideos + 1).Select(i => $"https://youtu.be/v{i:0000000000}").Append("https://youtu.be/v0000000000"));

        var exitCode = await RunAsync("youtube", "--list", list, "--name", "Risala", "--output", Classes, "--download", "--plan");

        Assert.That(exitCode, Is.EqualTo(CommandLineTree.Mistake));
        Assert.That(_line.Mistakes, Has.Some.EqualTo("youtube takes at most 500 videos at a time; these links name 501"));
    }
}
