using ClassScribe.Commands;
using ClassScribe.YouTube;

namespace ClassScribe.Tests;

/// <summary>
/// youtube check (design 3.1, with Gulzar's answer 10): where yt-dlp, Deno, FFmpeg and FFprobe are and their versions;
/// what is missing with the winget command; yt-dlp more than 90 days old, by its release date on the test's clock, warned
/// with the update command; exit code 0 only when all is in place and current. Nothing real is run or looked at.
/// </summary>
[TestFixture]
public class YtDlpCheckTests
{
    private YouTubeWorld _world = null!;

    /// <summary>A world whose disk holds the four tools in winget's links folder; the clock is at 15 October 2026.</summary>
    [SetUp]
    public void SetUp() => _world = new YouTubeWorld();

    /// <summary>Deletes the world's folders.</summary>
    [TearDown]
    public void TearDown() => _world.Dispose();

    /// <summary>The check over <paramref name="world"/>'s disk and played tools.</summary>
    private static YtDlpCheck Check(YouTubeWorld world) =>
        new(new ToolLocator(new FakeRegistryValues(), world.Files, world.YouTube, world.Log<ToolLocator>()) { LocalAppData = @"C:\Users\gdala\AppData\Local" },
            world.Tools, world.Folders, world.Runtime.Time, world.Log<YtDlpCheck>());

    /// <summary>The command over <paramref name="world"/>'s check, for its lines and exit code.</summary>
    private static YouTubeCommand Command(YouTubeWorld world) =>
        new(world.Batch(), Check(world), new FakeUserSignals(), world.Ui, world.YouTube, world.Log<YouTubeCommand>());

    /// <summary>All four found, yt-dlp current: each line names the tool, its version and its path; exit code 0. Only --version and -version were asked.</summary>
    [Test]
    public async Task All_four_current_pass_with_their_versions()
    {
        var exitCode = await Command(_world).CheckAsync();

        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(_world.Screen, Is.EqualTo(new[]
        {
            $@"yt-dlp   2026.10.10   {YouTubeWorld.Links}\yt-dlp.exe",
            $@"Deno     2.9.7        {YouTubeWorld.Links}\deno.exe",
            $@"FFmpeg   7.1-full_build-www.gyan.dev {YouTubeWorld.Links}\ffmpeg.exe",
            $@"FFprobe  7.1-full_build-www.gyan.dev {YouTubeWorld.Links}\ffprobe.exe",
            "yt-dlp and its helpers are in place."
        }));
        Assert.That(_world.Tools.Calls.Select(call => call.Arguments.Single()), Is.EqualTo(["--version", "--version", "-version", "-version"]));
    }

    /// <summary>yt-dlp more than 90 days old is warned of, with winget's update command; exit code 1. At 90 days it passes.</summary>
    [TestCase("2026.07.17", 0)]
    [TestCase("2026.07.16", 1)]
    [TestCase("2026.07.10.232658", 1)]
    public async Task Yt_dlp_more_than_ninety_days_old_is_warned_of(string version, int exitCode)
    {
        _world.Tools.YtDlpVersion = version;

        Assert.That(await Command(_world).CheckAsync(), Is.EqualTo(exitCode));
        if (exitCode == 1)
            Assert.That(_world.Logs.Problems.Single(), Does.StartWith($"yt-dlp {version} is").And.Contain("days old").And.EndWith("winget upgrade --source winget --id yt-dlp.yt-dlp"));
    }

    /// <summary>What is missing is said with the winget command; exit code 1, and nothing that is not there is run.</summary>
    [Test]
    public async Task What_is_missing_is_said_with_its_winget_command()
    {
        using var empty = new YouTubeWorld(withTools: false);

        var exitCode = await Command(empty).CheckAsync();

        Assert.That(exitCode, Is.EqualTo(1));
        Assert.That(empty.Tools.Calls, Is.Empty);
        Assert.That(empty.Logs.Problems, Has.Count.EqualTo(3));
        Assert.That(empty.Logs.Problems[0], Does.EndWith("winget install --source winget --id yt-dlp.yt-dlp"));
    }

    /// <summary>A tool that does not say its version, as one Bitdefender blocked, is a problem with what to do.</summary>
    [Test]
    public async Task A_tool_that_says_no_version_is_a_problem()
    {
        _world.Tools.YtDlpVersion = "";

        var found = await Check(_world).RunAsync(CancellationToken.None);

        Assert.That(found.Paths, Is.Null);
        Assert.That(found.Problems.Single(), Does.StartWith("yt-dlp is at").And.Contain("Bitdefender"));
    }
}
