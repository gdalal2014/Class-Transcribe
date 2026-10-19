using ClassScribe.Drive;
using ClassScribe.Settings;
using Microsoft.Extensions.Configuration;

namespace ClassScribe.Tests;

/// <summary>
/// The YouTube section's check (design 5, with Gulzar's answers): yt-dlp's path, the channels he may download and his
/// course's channels, and what of a video's recording goes to Google Drive, which is everything unless he says otherwise.
/// </summary>
[TestFixture]
public class YouTubeSettingsCheckTests
{
    /// <summary>What the check finds wrong with the given settings.</summary>
    private static IReadOnlyList<string> Problems(Dictionary<string, string?> settings) =>
        new SettingsCheck().FindProblems(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());

    /// <summary>The section's defaults: yt-dlp found by itself, no channels listed, and everything of a video's recording sent to Drive, as a class's is.</summary>
    [Test]
    public void The_defaults_find_yt_dlp_list_no_channels_and_send_everything()
    {
        var defaults = new YouTubeOptions();

        Assert.That(defaults.YtDlpPath, Is.Empty);
        Assert.That(defaults.MayDownloadChannels, Is.Empty);
        Assert.That(defaults.CourseChannels, Is.Empty);
        Assert.That(defaults.DriveUpload, Is.EqualTo(DriveUploadScope.Everything));
        Assert.That(Problems([]), Is.Empty);
    }

    /// <summary>The channels are read as lists, and the entries of either shape pass.</summary>
    [Test]
    public void Channels_of_either_shape_pass()
    {
        var settings = new Dictionary<string, string?>
        {
            ["YouTube:MayDownloadChannels:0"] = "@yourchannel",
            ["YouTube:MayDownloadChannels:1"] = "UCabcdefghijklmnopqrstuv",
            ["YouTube:CourseChannels:0"] = "@Course.Channel_1-2",
            ["YouTube:DriveUpload"] = "WithoutAudio"
        };

        Assert.That(Problems(settings), Is.Empty);
        var bound = new ConfigurationBuilder().AddInMemoryCollection(settings).Build().GetSection(YouTubeOptions.SectionName).Get<YouTubeOptions>()!;
        Assert.That(bound.MayDownloadChannels, Is.EqualTo(["@yourchannel", "UCabcdefghijklmnopqrstuv"]));
        Assert.That(bound.CourseChannels, Is.EqualTo(["@Course.Channel_1-2"]));
        Assert.That(bound.DriveUpload, Is.EqualTo(DriveUploadScope.WithoutAudio));
    }

    /// <summary>An entry of another shape is refused, by its setting and place, with the two shapes it may have.</summary>
    [TestCase("yourchannel")]
    [TestCase("@ab")]
    [TestCase("@this-handle-is-far-too-long-to-be-one")]
    [TestCase("UCabc")]
    [TestCase("https://www.youtube.com/@yourchannel")]
    [TestCase("@your channel")]
    public void A_channel_of_another_shape_is_refused(string entry)
    {
        var problems = Problems(new() { ["YouTube:CourseChannels:0"] = entry });

        Assert.That(problems, Has.Count.EqualTo(1));
        Assert.That(problems[0], Does.StartWith("YouTube:CourseChannels:0 is").And.Contain("@handle").And.Contain("UC"));
    }

    /// <summary>A channel in both lists is refused: a course's channel is never downloaded, whatever the other list says.</summary>
    [Test]
    public void A_channel_in_both_lists_is_refused()
    {
        var problems = Problems(new()
        {
            ["YouTube:MayDownloadChannels:0"] = "@Course",
            ["YouTube:CourseChannels:0"] = "@course"
        });

        Assert.That(problems, Has.Count.EqualTo(1));
        Assert.That(problems[0], Does.StartWith("YouTube:MayDownloadChannels").And.Contain("@Course").And.Contain("YouTube:CourseChannels"));
    }

    /// <summary>A list written as a single value, rather than in square brackets, is refused.</summary>
    [Test]
    public void A_list_written_as_one_value_is_refused()
    {
        var problems = Problems(new() { ["YouTube:CourseChannels"] = "@course" });

        Assert.That(problems, Has.Count.EqualTo(1));
        Assert.That(problems[0], Does.StartWith("YouTube:CourseChannels is \"@course\"").And.Contain("square brackets"));
    }

    /// <summary>What goes to Drive is named by one of its two names.</summary>
    [Test]
    public void What_goes_to_Drive_is_one_of_its_names()
    {
        var problems = Problems(new() { ["YouTube:DriveUpload"] = "Some" });

        Assert.That(problems, Is.EqualTo(["YouTube:DriveUpload is \"Some\", which is not Everything or WithoutAudio."]));
    }

    /// <summary>yt-dlp's path, when given, must be a full path to an .exe that is there.</summary>
    [Test]
    public void The_path_of_yt_dlp_must_be_a_full_path_to_an_exe_that_is_there()
    {
        using var folder = new TempFolder();
        var exe = folder.File("yt-dlp.exe");
        File.WriteAllBytes(exe, [0]);
        var script = folder.File("yt-dlp.cmd");
        File.WriteAllBytes(script, [0]);

        Assert.That(Problems(new() { ["YouTube:YtDlpPath"] = exe }), Is.Empty);
        Assert.That(Problems(new() { ["YouTube:YtDlpPath"] = $"\"{exe}\"" }), Is.Empty, "quotes around it go");
        Assert.That(Problems(new() { ["YouTube:YtDlpPath"] = "yt-dlp.exe" }).Single(), Does.Contain("full path"));
        Assert.That(Problems(new() { ["YouTube:YtDlpPath"] = folder.File("missing.exe") }).Single(), Does.Contain("is not there"));
        Assert.That(Problems(new() { ["YouTube:YtDlpPath"] = script }).Single(), Does.Contain(".exe"));
    }

    /// <summary>The record path's defaults: Chrome found by itself, a video's recording stopped at 4 hours, and YouTube's own address for the titles.</summary>
    [Test]
    public void The_record_paths_defaults_find_Chrome_stop_at_4_hours_and_read_titles_from_YouTube()
    {
        var defaults = new YouTubeOptions();

        Assert.That(defaults.BrowserPath, Is.Empty);
        Assert.That(defaults.LongestVideoHours, Is.EqualTo(4));
        Assert.That(defaults.OEmbedBaseUrl, Is.EqualTo("https://www.youtube.com/"));
    }

    /// <summary>Chrome's path, when given, must be a full path to an .exe that is there.</summary>
    [Test]
    public void The_path_of_Chrome_must_be_a_full_path_to_an_exe_that_is_there()
    {
        using var folder = new TempFolder();
        var exe = folder.File("chrome.exe");
        File.WriteAllBytes(exe, [0]);
        var shortcut = folder.File("chrome.lnk");
        File.WriteAllBytes(shortcut, [0]);

        Assert.That(Problems(new() { ["YouTube:BrowserPath"] = exe }), Is.Empty);
        Assert.That(Problems(new() { ["YouTube:BrowserPath"] = $"\"{exe}\"" }), Is.Empty, "quotes around it go");
        Assert.That(Problems(new() { ["YouTube:BrowserPath"] = "chrome.exe" }).Single(), Does.StartWith("YouTube:BrowserPath is").And.Contain("full path"));
        Assert.That(Problems(new() { ["YouTube:BrowserPath"] = folder.File("missing.exe") }).Single(), Does.Contain("is not there"));
        Assert.That(Problems(new() { ["YouTube:BrowserPath"] = shortcut }).Single(), Does.Contain(".exe"));
    }

    /// <summary>The longest a video is recorded for is from a quarter of an hour to 9 hours.</summary>
    [TestCase("0.25", true)]
    [TestCase("9", true)]
    [TestCase("0.2", false)]
    [TestCase("9.5", false)]
    [TestCase("-1", false)]
    [TestCase("NaN", false)]
    public void The_longest_video_is_from_a_quarter_of_an_hour_to_9_hours(string hours, bool passes)
    {
        var problems = Problems(new() { ["YouTube:LongestVideoHours"] = hours });

        if (passes)
            Assert.That(problems, Is.Empty);
        else
            Assert.That(problems.Single(), Does.StartWith("YouTube:LongestVideoHours is").And.Contain("from 0.25 to 9"));
    }

    /// <summary>
    /// One above Recording:StopAfterHours, whose safety stop then ends a video's recording first, is a warning, never a
    /// refusal: a refused file would hold up every class over a setting of the videos'. With the safety stop off (0), or at
    /// or above the video's limit, nothing is said.
    /// </summary>
    [Test]
    public void The_longest_video_above_the_safety_stop_is_a_warning_not_a_refusal()
    {
        // The warnings the check gives about the YouTube section for these settings.
        static IReadOnlyList<string> Warnings(Dictionary<string, string?> settings) =>
            new SettingsCheck().FindWarnings(new ConfigurationBuilder().AddInMemoryCollection(settings).Build()).Where(warning => warning.StartsWith("YouTube:", StringComparison.Ordinal)).ToList();
        Dictionary<string, string?> above = new() { ["YouTube:LongestVideoHours"] = "6", ["Recording:StopAfterHours"] = "5" };

        Assert.That(Problems(above), Is.Empty);
        Assert.That(Warnings(above).Single(), Does.StartWith("YouTube:LongestVideoHours is 6, above Recording:StopAfterHours (5)").And.Contain("stops at 5 hours"));
        Assert.That(Warnings(new() { ["Recording:StopAfterHours"] = "3" }).Single(), Does.StartWith("YouTube:LongestVideoHours is 4,"), "the default counts too");
        Assert.That(Warnings(new() { ["YouTube:LongestVideoHours"] = "5", ["Recording:StopAfterHours"] = "5" }), Is.Empty);
        Assert.That(Warnings(new() { ["YouTube:LongestVideoHours"] = "9", ["Recording:StopAfterHours"] = "0" }), Is.Empty);
        Assert.That(Warnings([]), Is.Empty, "4 hours and 9");
    }

    /// <summary>The address titles are read from is https, or http to this computer only, for a local stub in a dry run.</summary>
    [TestCase("https://www.youtube.com/", true)]
    [TestCase("http://127.0.0.1:5005/", true)]
    [TestCase("http://localhost:5005/", true)]
    [TestCase("http://www.youtube.com/", false)]
    [TestCase("www.youtube.com", false)]
    [TestCase("", false)]
    public void The_titles_address_is_https_or_a_local_stub(string address, bool passes)
    {
        var problems = Problems(new() { ["YouTube:OEmbedBaseUrl"] = address });

        if (passes)
            Assert.That(problems, Is.Empty);
        else
            Assert.That(problems.Single(), Does.StartWith("YouTube:OEmbedBaseUrl is").And.Contain("https://www.youtube.com/"));
    }
}
