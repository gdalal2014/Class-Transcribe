using ClassScribe.Settings;
using ClassScribe.YouTube;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClassScribe.Tests;

/// <summary>
/// Finding yt-dlp, Deno, FFmpeg and FFprobe (design 3.1): yt-dlp by the setting, then winget's links folder, then each
/// folder of PATH as the registry holds it now, the user's before the machine's; its helpers beside it first. Nothing real
/// is looked at: the registry and the files are fakes.
/// </summary>
[TestFixture]
public class ToolLocatorTests
{
    private const string LocalAppData = @"C:\Users\gdala\AppData\Local";
    private const string Links = @"C:\Users\gdala\AppData\Local\Microsoft\WinGet\Links";
    private FakeRegistryValues _registry = null!;
    private FakeFileProbe _files = null!;
    private TestOptions<YouTubeOptions> _options = null!;

    /// <summary>An empty registry and disk, and the default settings.</summary>
    [SetUp]
    public void SetUp()
    {
        _registry = new FakeRegistryValues();
        _files = new FakeFileProbe();
        _options = TestOptions.Create(new YouTubeOptions());
    }

    /// <summary>The locator over the fakes, with this laptop's local application data folder.</summary>
    private ToolLocator Locator() => new(_registry, _files, _options, NullLogger<ToolLocator>.Instance) { LocalAppData = LocalAppData };

    /// <summary>winget's links folder holds all four, as winget install yt-dlp.yt-dlp leaves them.</summary>
    [Test]
    public void Winget_links_hold_all_four()
    {
        _files.Add($@"{Links}\yt-dlp.exe", $@"{Links}\deno.exe", $@"{Links}\ffmpeg.exe", $@"{Links}\ffprobe.exe");

        var found = Locator().Find();

        Assert.That(found.Paths, Is.EqualTo(new ToolPaths($@"{Links}\yt-dlp.exe", $@"{Links}\deno.exe", Links)));
        Assert.That(found.Missing, Is.Empty);
    }

    /// <summary>The setting wins, and the helpers beside the yt-dlp it names come before winget's links.</summary>
    [Test]
    public void The_setting_wins_and_the_helpers_beside_it_come_first()
    {
        _options.Set(new YouTubeOptions { YtDlpPath = @"""D:\Tools\yt-dlp.exe""" });
        _files.Add(@"D:\Tools\yt-dlp.exe", @"D:\Tools\deno.exe", @"D:\Tools\ffmpeg.exe", @"D:\Tools\ffprobe.exe",
            $@"{Links}\yt-dlp.exe", $@"{Links}\deno.exe", $@"{Links}\ffmpeg.exe", $@"{Links}\ffprobe.exe");

        Assert.That(Locator().Find().Paths, Is.EqualTo(new ToolPaths(@"D:\Tools\yt-dlp.exe", @"D:\Tools\deno.exe", @"D:\Tools")));
    }

    /// <summary>
    /// PATH is read from the registry as it is now, the user's before the machine's, so that a window opened before winget
    /// changed PATH still finds the tools; quotes, blanks and variables in it are dealt with.
    /// </summary>
    [Test]
    public void Path_is_read_from_the_registry_the_users_first()
    {
        _registry.Set(ToolLocator.UserPathKey, "Path", @"C:\Empty;; ""C:\User Tools"" ;%SystemRoot%\Missing");
        _registry.SetLocalMachine(ToolLocator.MachinePathKey, "Path", @"C:\Machine Tools;C:\FFmpeg\bin");
        _files.Add(@"C:\User Tools\yt-dlp.exe", @"C:\Machine Tools\yt-dlp.exe", @"C:\Machine Tools\deno.exe", @"C:\FFmpeg\bin\ffmpeg.exe", @"C:\FFmpeg\bin\ffprobe.exe");

        Assert.That(Locator().Find().Paths, Is.EqualTo(new ToolPaths(@"C:\User Tools\yt-dlp.exe", @"C:\Machine Tools\deno.exe", @"C:\FFmpeg\bin")));
    }

    /// <summary>FFmpeg and FFprobe are taken from one folder: a folder with FFmpeg alone is passed over.</summary>
    [Test]
    public void FFmpeg_and_FFprobe_come_from_one_folder()
    {
        _registry.Set(ToolLocator.UserPathKey, "Path", @"C:\OnlyFfmpeg;C:\Both");
        _files.Add($@"{Links}\yt-dlp.exe", $@"{Links}\deno.exe", @"C:\OnlyFfmpeg\ffmpeg.exe", @"C:\Both\ffmpeg.exe", @"C:\Both\ffprobe.exe");

        Assert.That(Locator().Find().Paths!.FfmpegFolder, Is.EqualTo(@"C:\Both"));
    }

    /// <summary>What is missing is named, with the winget command that installs it; nothing is found, nothing is given.</summary>
    [Test]
    public void What_is_missing_is_named_with_its_winget_command()
    {
        var found = Locator().Find();

        Assert.That(found.Paths, Is.Null);
        Assert.That(found.Missing, Is.EqualTo(new[]
        {
            "yt-dlp was not found (YouTube:YtDlpPath, winget's links folder, PATH). Install it: winget install --source winget --id yt-dlp.yt-dlp",
            "Deno was not found (beside yt-dlp, winget's links folder, PATH). Install it: winget install --source winget --id DenoLand.Deno",
            "FFmpeg and FFprobe were not found in one folder (beside yt-dlp, winget's links folder, PATH). Install them: winget install --source winget --id yt-dlp.FFmpeg"
        }));
    }

    /// <summary>A registry that cannot be read leaves PATH out of the search, and winget's links are still looked in.</summary>
    [Test]
    public void A_registry_that_cannot_be_read_leaves_path_out()
    {
        _registry.Failure = new UnauthorizedAccessException("denied");
        _files.Add($@"{Links}\yt-dlp.exe", $@"{Links}\deno.exe", $@"{Links}\ffmpeg.exe", $@"{Links}\ffprobe.exe");

        Assert.That(Locator().Find().Paths, Is.Not.Null);
    }
}
