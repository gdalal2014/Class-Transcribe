using ClassScribe.Recorder;
using ClassScribe.YouTube;

namespace ClassScribe.Tests;

/// <summary>
/// yt-dlp's arguments (design 3.3, with Gulzar's answers 5 and 9): one fixed set first in every call, which keeps out every
/// configuration file and every cookie; never an option that signs in, hands over cookies or a browser's data, runs a
/// command, or updates yt-dlp; --load-info-json only with a path inside the video's own staging folder; nothing that names
/// ClassScribe's video window's profile; the link last, rebuilt from its ID; and a download that keeps YouTube's own rate.
/// </summary>
[TestFixture]
public class YtDlpArgumentsTests
{
    private static readonly ToolPaths Tools = new(@"C:\Links\yt-dlp.exe", @"C:\Links\deno.exe", @"C:\FFmpeg\bin");
    private static readonly YouTubeFolders Folders = new(new RuntimeFolder(@"C:\Runtime\ClassScribe"));

    /// <summary>The arguments under test.</summary>
    private static YtDlpArguments Arguments() => new(Tools, Folders);

    /// <summary>Every list built: the reads and both downloads.</summary>
    private static IEnumerable<IReadOnlyList<string>> EveryList()
    {
        var arguments = Arguments();
        yield return arguments.ReadPlaylist("PLxyz");
        yield return arguments.ReadVideo("abcdefghijk");
        yield return arguments.Download("abcdefghijk", fromInfoFile: true);
        yield return arguments.Download("abcdefghijk", fromInfoFile: false);
    }

    /// <summary>The fixed set comes first in every call: no configuration, no cookies, nothing marked watched, a second between requests, Deno, FFmpeg, and a cache of ClassScribe's own.</summary>
    [Test]
    public void The_fixed_set_comes_first_in_every_call()
    {
        string[] fixedSet =
        [
            "--ignore-config", "--no-cookies", "--no-cookies-from-browser", "--no-mark-watched", "--no-colors", "--sleep-requests", "1",
            "--js-runtimes", @"deno:C:\Links\deno.exe", "--ffmpeg-location", @"C:\FFmpeg\bin", "--cache-dir", @"C:\Runtime\ClassScribe\youtube\yt-dlp-cache"
        ];

        foreach (var list in EveryList()) Assert.That(list.Take(fixedSet.Length), Is.EqualTo(fixedSet));
    }

    /// <summary>A video's read writes its information alone, into its staging folder, named by its ID, from the link rebuilt from the ID, last.</summary>
    [Test]
    public void A_videos_read_writes_its_information_into_its_staging_folder()
    {
        var list = Arguments().ReadVideo("abcdefghijk");

        Assert.That(list.Skip(13), Is.EqualTo(new[]
        {
            "--no-playlist", "--skip-download", "--write-info-json", "--no-write-playlist-metafiles",
            "-o", @"C:\Runtime\ClassScribe\youtube\staging\abcdefghijk\abcdefghijk.%(ext)s", "https://www.youtube.com/watch?v=abcdefghijk"
        }));
    }

    /// <summary>A playlist is read flat, as one JSON, from its link rebuilt from its ID.</summary>
    [Test]
    public void A_playlist_is_read_flat() =>
        Assert.That(Arguments().ReadPlaylist("PLxyz").Skip(13), Is.EqualTo(new[] { "--flat-playlist", "-J", "https://www.youtube.com/playlist?list=PLxyz" }));

    /// <summary>
    /// A download from the video's read loads its information file and gives no link; the best m4a audio first; the audio
    /// made a mono WAV at YouTube's own rate (no -ar), its temporary files and its result in the staging folder, and the
    /// result's path written to done.txt once it is moved into place.
    /// </summary>
    [Test]
    public void A_download_from_the_read_loads_its_information_and_keeps_youtubes_rate()
    {
        var staging = @"C:\Runtime\ClassScribe\youtube\staging\abcdefghijk";

        var list = Arguments().Download("abcdefghijk", fromInfoFile: true);

        Assert.That(list.Skip(13), Is.EqualTo(new[]
        {
            "--no-playlist", "--newline", "--load-info-json", $@"{staging}\abcdefghijk.info.json",
            "-f", "ba[ext=m4a]/ba[acodec^=mp4a]/ba/b", "-x", "--audio-format", "wav", "--postprocessor-args", "ExtractAudio+ffmpeg_o:-ac 1",
            "-P", $@"temp:{staging}\tmp", "-o", $@"{staging}\abcdefghijk.%(ext)s", "--print-to-file", "after_move:filepath", $@"{staging}\done.txt"
        }));
        Assert.That(list.Single(argument => argument.StartsWith("ExtractAudio", StringComparison.Ordinal)), Does.Not.Contain("-ar"), "FFmpeg keeps the source's rate");
    }

    /// <summary>A download without a fresh read gives the link, rebuilt from the ID, last, and loads no information file.</summary>
    [Test]
    public void A_download_without_a_read_gives_the_link_last()
    {
        var list = Arguments().Download("abcdefghijk", fromInfoFile: false);

        Assert.That(list[^1], Is.EqualTo("https://www.youtube.com/watch?v=abcdefghijk"));
        Assert.That(list, Has.None.EqualTo("--load-info-json"));
    }

    /// <summary>No list built ever holds a forbidden option.</summary>
    [Test]
    public void No_list_built_holds_a_forbidden_option()
    {
        foreach (var list in EveryList()) Assert.That(YtDlpArguments.ProblemWith(list, Folders), Is.Null);
    }

    /// <summary>
    /// A list that holds an option that signs in, hands over cookies or a browser's data, runs a command, reads more
    /// configuration or updates yt-dlp is refused, in any of its forms; -P (paths) is not -p (a password).
    /// </summary>
    [TestCase("--cookies")]
    [TestCase("--cookies=C:\\cookies.txt")]
    [TestCase("--cookies-from-browser")]
    [TestCase("--username")]
    [TestCase("-u")]
    [TestCase("--password")]
    [TestCase("-p")]
    [TestCase("--twofactor")]
    [TestCase("--netrc")]
    [TestCase("--netrc-location")]
    [TestCase("--netrc-cmd")]
    [TestCase("--video-password")]
    [TestCase("--ap-mso")]
    [TestCase("--ap-username")]
    [TestCase("--ap-password")]
    [TestCase("--client-certificate")]
    [TestCase("--client-certificate-key")]
    [TestCase("--client-certificate-password")]
    [TestCase("--remote-components")]
    [TestCase("--exec")]
    [TestCase("--config-locations")]
    [TestCase("-U")]
    [TestCase("--update")]
    [TestCase("--update-to")]
    public void A_forbidden_option_is_refused(string option)
    {
        IReadOnlyList<string> list = [.. Arguments().Fixed, option, "x"];

        Assert.That(YtDlpArguments.ProblemWith(list, Folders), Does.Contain(option.Split('=')[0]));
        Assert.That(YtDlpArguments.ProblemWith([.. Arguments().Fixed, "-P", "temp:C:\\x"], Folders), Is.Null);
    }

    /// <summary>--load-info-json takes only a path inside the video's own staging folder, never one ClassScribe did not build there.</summary>
    [TestCase(@"C:\Users\gdala\Downloads\x.info.json")]
    [TestCase(@"C:\Runtime\ClassScribe\youtube\staging\..\..\x.info.json")]
    [TestCase(@"C:\Runtime\ClassScribe\youtube\staging-other\abcdefghijk\abcdefghijk.info.json")]
    public void Load_info_json_takes_only_a_path_in_staging(string path) =>
        Assert.That(YtDlpArguments.ProblemWith([.. Arguments().Fixed, "--load-info-json", path], Folders), Does.Contain("--load-info-json"));

    /// <summary>No argument may name ClassScribe's video window's profile, which may hold Google sign-in cookies.</summary>
    [Test]
    public void Nothing_may_name_the_video_windows_profile() =>
        Assert.That(YtDlpArguments.ProblemWith([.. Arguments().Fixed, "-o", Path.Combine(Folders.VideoBrowser, "x.%(ext)s")], Folders),
            Does.Contain("profile"));

    /// <summary>A list without the fixed set first is refused.</summary>
    [Test]
    public void A_list_without_the_fixed_set_first_is_refused() =>
        Assert.That(YtDlpArguments.ProblemWith(["--version", "https://www.youtube.com/watch?v=abcdefghijk"], Folders), Does.Contain("--ignore-config"));

    /// <summary>The version call alone needs no fixed set: it asks yt-dlp nothing of YouTube.</summary>
    [Test]
    public void The_version_call_alone_needs_no_fixed_set() => Assert.That(YtDlpArguments.ProblemWith(YtDlpArguments.Version, Folders), Is.Null);
}
