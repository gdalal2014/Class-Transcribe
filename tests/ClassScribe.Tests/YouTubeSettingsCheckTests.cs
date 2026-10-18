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
}
