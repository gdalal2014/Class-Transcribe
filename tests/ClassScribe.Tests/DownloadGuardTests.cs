using ClassScribe.Settings;
using ClassScribe.Transcription;
using ClassScribe.YouTube;

namespace ClassScribe.Tests;

/// <summary>
/// The guard on downloads (design 3.5, with Gulzar's answer 7): each video gets the first verdict that applies. A course's
/// channel is never downloaded; what needs a sign-in, a stream not finished, and a length unknown or too long for one file
/// are left out; a channel he may download and a public video are ready; an unlisted video, or one yt-dlp is not sure of,
/// needs his word, which --allow-unlisted gives for the whole batch, and which never overrides any other refusal.
/// </summary>
[TestFixture]
public class DownloadGuardTests
{
    private const string Course = "UCcoursecoursecoursecour";

    /// <summary>The guard, with the course's channel and his own channel listed.</summary>
    private static DownloadGuard Guard() => new(new YouTubeOptions { CourseChannels = [Course, "@CourseHandle"], MayDownloadChannels = ["@yourchannel"] });

    /// <summary>A video's information, as yt-dlp reads it.</summary>
    private static YtDlpInfo Video(string? availability = "public", string liveStatus = "not_live", int ageLimit = 0, double? duration = 3753,
        string channelId = "UCotherotherotherotherot", string handle = "@other") =>
        YtDlpRead.Info(YtDlpReadTests.Info(availability: availability, liveStatus: liveStatus, ageLimit: ageLimit, duration: duration, channelId: channelId, handle: handle), "x");

    /// <summary>A public video is ready, under the batch's confirmation.</summary>
    [Test]
    public void A_public_video_is_ready() =>
        Assert.That(Guard().Judge(Video(), allowUnlisted: false), Is.EqualTo(new GuardVerdict(GuardVerdictKind.Ready, "ready", VideoSource.ByYes)));

    /// <summary>A video of a channel he may download is ready, unlisted or not, by that channel.</summary>
    [TestCase("unlisted")]
    [TestCase(null)]
    public void A_video_of_a_channel_he_may_download_is_ready(string? availability) =>
        Assert.That(Guard().Judge(Video(availability, handle: "@YourChannel"), allowUnlisted: false),
            Is.EqualTo(new GuardVerdict(GuardVerdictKind.Ready, "ready (YouTube:MayDownloadChannels)", VideoSource.ByChannel)));

    /// <summary>An unlisted video, or one yt-dlp is not sure of, needs his word.</summary>
    [TestCase("unlisted")]
    [TestCase(null)]
    public void An_unlisted_video_needs_his_word(string? availability) =>
        Assert.That(Guard().Judge(Video(availability), allowUnlisted: false).Kind, Is.EqualTo(GuardVerdictKind.NeedsWord));

    /// <summary>--allow-unlisted gives his word for every unlisted video of the batch, and the verdict says so.</summary>
    [Test]
    public void Allow_unlisted_gives_his_word_for_the_batch() =>
        Assert.That(Guard().Judge(Video("unlisted"), allowUnlisted: true),
            Is.EqualTo(new GuardVerdict(GuardVerdictKind.Ready, "ready (--allow-unlisted)", VideoSource.ByAllowUnlisted)));

    /// <summary>
    /// --allow-unlisted takes only what yt-dlp says is unlisted (Gulzar's answer 7): a video yt-dlp is not sure is public still
    /// needs his word, in the menu.
    /// </summary>
    [Test]
    public void Allow_unlisted_never_takes_a_video_not_known_to_be_public() =>
        Assert.That(Guard().Judge(Video(availability: null), allowUnlisted: true), Is.EqualTo(new GuardVerdict(GuardVerdictKind.NeedsWord, "needs your word", null)));

    /// <summary>
    /// While the course's channels are listed, a video whose read names no channel cannot be checked against them: it needs his
    /// word, public or not, and --allow-unlisted never gives it. Without channels listed, a public one is ready as always.
    /// </summary>
    [TestCase("public")]
    [TestCase("unlisted")]
    public void A_video_whose_channel_is_unknown_needs_his_word(string availability)
    {
        var unknown = YtDlpRead.Info($$"""{"id":"abcdefghijk","duration":60,"availability":"{{availability}}","live_status":"not_live","age_limit":0}""", "x");

        Assert.That(Guard().Judge(unknown, allowUnlisted: true), Is.EqualTo(new GuardVerdict(GuardVerdictKind.NeedsWord, DownloadGuard.ChannelUnknown, null)));
        Assert.That(new DownloadGuard(new YouTubeOptions()).Judge(YtDlpRead.Info("""{"id":"abcdefghijk","duration":60,"availability":"public"}""", "x"), allowUnlisted: false).Kind,
            Is.EqualTo(GuardVerdictKind.Ready));
    }

    /// <summary>A course's channel is found by its /channel/ address too, when the read gives no channel id: never downloaded.</summary>
    [Test]
    public void A_courses_channel_is_found_by_its_address()
    {
        var info = YtDlpRead.Info($$"""{"id":"abcdefghijk","duration":60,"availability":"unlisted","channel_url":"https://www.youtube.com/channel/{{Course}}","uploader_id":"@other"}""", "x");

        Assert.That(Guard().Judge(info, allowUnlisted: true).Kind, Is.EqualTo(GuardVerdictKind.CourseChannel));
    }

    /// <summary>A course's channel named by a handle of Arabic letters is found when yt-dlp gives the handle percent-encoded.</summary>
    [Test]
    public void A_courses_handle_is_found_when_yt_dlp_gives_it_encoded()
    {
        var info = YtDlpRead.Info(
            """{"id":"abcdefghijk","duration":60,"availability":"public","uploader_id":"@%D9%82%D9%86%D8%A7%D8%A9","uploader_url":"https://www.youtube.com/@%D9%82%D9%86%D8%A7%D8%A9"}""", "x");

        Assert.That(new DownloadGuard(new YouTubeOptions { CourseChannels = ["@قناة"] }).Judge(info, allowUnlisted: false).Kind, Is.EqualTo(GuardVerdictKind.CourseChannel));
    }

    /// <summary>A course's channel is never downloaded, by its UC id or its @handle in any letter case, whatever else holds, --allow-unlisted included.</summary>
    [TestCase(Course, "@x")]
    [TestCase("UCotherotherotherotherot", "@coursehandle")]
    public void A_courses_channel_is_never_downloaded(string channelId, string handle)
    {
        foreach (var availability in new[] { "public", "unlisted", null })
        {
            var verdict = Guard().Judge(Video(availability, channelId: channelId, handle: handle), allowUnlisted: true);

            Assert.That(verdict.Kind, Is.EqualTo(GuardVerdictKind.CourseChannel));
            Assert.That(verdict.AcceptedBy, Is.Null);
        }
    }

    /// <summary>What needs a sign-in is left out, --allow-unlisted or not: a private, members' or Premium video, or one restricted by age.</summary>
    [TestCase("private", 0)]
    [TestCase("subscriber_only", 0)]
    [TestCase("premium_only", 0)]
    [TestCase("needs_auth", 0)]
    [TestCase("public", 18)]
    public void What_needs_a_sign_in_is_left_out(string availability, int ageLimit) =>
        Assert.That(Guard().Judge(Video(availability, ageLimit: ageLimit), allowUnlisted: true).Kind, Is.EqualTo(GuardVerdictKind.NeedsSignIn));

    /// <summary>A stream that is live, upcoming or still being processed is not finished yet.</summary>
    [TestCase("is_live")]
    [TestCase("is_upcoming")]
    [TestCase("post_live")]
    public void A_stream_not_finished_is_left_out(string liveStatus) =>
        Assert.That(Guard().Judge(Video(liveStatus: liveStatus), allowUnlisted: true).Kind, Is.EqualTo(GuardVerdictKind.NotFinished));

    /// <summary>A length unknown, or longer than ElevenLabs takes in one file, is left out.</summary>
    [TestCase(null, GuardVerdictKind.UnknownLength)]
    [TestCase(9.5 * 3600 + 1, GuardVerdictKind.TooLong)]
    public void A_length_unknown_or_too_long_is_left_out(double? duration, GuardVerdictKind kind) =>
        Assert.That(Guard().Judge(Video(duration: duration), allowUnlisted: true).Kind, Is.EqualTo(kind));

    /// <summary>Only Ready verdicts are downloaded; the words of each kind say why the others are left out.</summary>
    [Test]
    public void The_words_say_why_a_video_is_left_out()
    {
        Assert.That(Guard().Judge(Video(channelId: Course), false).Words, Is.EqualTo("your course's channel: never downloaded"));
        Assert.That(Guard().Judge(Video("private"), false).Words, Is.EqualTo("needs a sign-in: left out"));
        Assert.That(Guard().Judge(Video(liveStatus: "is_live"), false).Words, Is.EqualTo("not finished yet: left out"));
        Assert.That(Guard().Judge(Video(duration: null), false).Words, Is.EqualTo("length unknown: left out"));
        Assert.That(Guard().Judge(Video(duration: 40000), false).Words, Is.EqualTo("longer than 9 h 30 min: left out"));
        Assert.That(Guard().Judge(Video("unlisted"), false).Words, Is.EqualTo("needs your word"));
    }

    /// <summary>
    /// The longest video downloaded is the longest recording ClassScribe sends, ElevenLabs:LongestFileHours less half an hour,
    /// and the verdict's words are built from it (architecture 7.2): with a file of 5 hours, a second over 4 hours 30 minutes
    /// is left out and 4 hours 30 minutes are ready; with 9.5 hours, the words say 9 hours.
    /// </summary>
    [Test]
    public void The_longest_video_is_one_file_less_half_an_hour_by_the_settings()
    {
        var five = new DownloadGuard(new YouTubeOptions(), TranscriptionLimits.From(new ElevenLabsOptions { LongestFileHours = 5 }));
        var nineAndAHalf = new DownloadGuard(new YouTubeOptions(), TranscriptionLimits.From(new ElevenLabsOptions { LongestFileHours = 9.5 }));

        Assert.That(five.Judge(Video(duration: 4.5 * 3600 + 1), false), Is.EqualTo(new GuardVerdict(GuardVerdictKind.TooLong, "longer than 4 h 30 min: left out", AcceptedBy: null)));
        Assert.That(five.Judge(Video(duration: 4.5 * 3600), false).Kind, Is.EqualTo(GuardVerdictKind.Ready));
        Assert.That(nineAndAHalf.Judge(Video(duration: 40000), false).Words, Is.EqualTo("longer than 9 hours: left out"));
    }
}
