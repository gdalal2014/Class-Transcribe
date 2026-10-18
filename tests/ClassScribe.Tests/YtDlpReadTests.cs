using ClassScribe.YouTube;

namespace ClassScribe.Tests;

/// <summary>
/// What ClassScribe reads of yt-dlp's answers (design 3.4): a video's information file and a playlist's flat listing, with
/// the fields it needs and everything else passed over, an Arabic title escaped as yt-dlp writes it, the source's sample rate
/// for the room the audio takes, and yt-dlp's version, which is its release date.
/// </summary>
[TestFixture]
public class YtDlpReadTests
{
    /// <summary>A public video's information file, as yt-dlp writes it, cut down to what matters and a little more.</summary>
    internal const string PublicInfo = """
        {"id": "abcdefghijk", "title": "The creed, part 1", "duration": 3753, "channel": "Your Channel", "channel_id": "UCxxxxxxxxxxxxxxxxxxxxxx",
         "uploader_id": "@yourchannel", "channel_url": "https://www.youtube.com/channel/UCxxxxxxxxxxxxxxxxxxxxxx", "upload_date": "20250303",
         "webpage_url": "https://www.youtube.com/watch?v=abcdefghijk", "availability": "public", "live_status": "not_live", "was_live": false,
         "age_limit": 0, "thumbnails": [{"url": "https://i.ytimg.com/x.jpg"}],
         "formats": [{"format_id": "139", "ext": "m4a", "acodec": "mp4a.40.5", "vcodec": "none", "asr": 22050, "filesize": 1000},
                     {"format_id": "140", "ext": "m4a", "acodec": "mp4a.40.2", "vcodec": "none", "asr": 44100, "filesize": 60000000},
                     {"format_id": "251", "ext": "webm", "acodec": "opus", "vcodec": "none", "asr": 48000},
                     {"format_id": "18", "ext": "mp4", "acodec": "mp4a.40.2", "vcodec": "avc1", "asr": 44100}]}
        """;

    /// <summary>A video's information with its fields as given.</summary>
    internal static string Info(string id = "abcdefghijk", string title = "The creed, part 1", double? duration = 3753, string? availability = "public",
        string liveStatus = "not_live", int ageLimit = 0, string channelId = "UCxxxxxxxxxxxxxxxxxxxxxx", string handle = "@yourchannel") =>
        $$"""
        {"id": "{{id}}", "title": "{{title}}", "duration": {{(duration is { } d ? d.ToString(System.Globalization.CultureInfo.InvariantCulture) : "null")}},
         "channel": "Your Channel", "channel_id": "{{channelId}}", "uploader_id": "{{handle}}", "channel_url": "https://www.youtube.com/channel/{{channelId}}",
         "upload_date": "20250303", "availability": {{(availability is null ? "null" : $"\"{availability}\"")}}, "live_status": "{{liveStatus}}",
         "age_limit": {{ageLimit}}, "formats": [{"format_id": "140", "ext": "m4a", "acodec": "mp4a.40.2", "vcodec": "none", "asr": 44100}]}
        """;

    /// <summary>The fields a video's read needs are read, the rest passed over.</summary>
    [Test]
    public void A_videos_information_is_read()
    {
        var info = YtDlpRead.Info(PublicInfo, "abcdefghijk.info.json");

        Assert.That(info.Id, Is.EqualTo("abcdefghijk"));
        Assert.That(info.Title, Is.EqualTo("The creed, part 1"));
        Assert.That(info.Length, Is.EqualTo(TimeSpan.FromSeconds(3753)));
        Assert.That((info.Channel, info.ChannelId, info.Handle), Is.EqualTo(("Your Channel", "UCxxxxxxxxxxxxxxxxxxxxxx", "@yourchannel")));
        Assert.That(info.Uploaded, Is.EqualTo("2025-03-03"));
        Assert.That((info.Availability, info.LiveStatus, info.AgeLimit), Is.EqualTo(("public", "not_live", 0)));
    }

    /// <summary>The source's rate is that of the best m4a audio-only format, the one a download takes first.</summary>
    [Test]
    public void The_sources_rate_is_the_m4a_audios() => Assert.That(YtDlpRead.Info(PublicInfo, "x").SourceRate, Is.EqualTo(44100));

    /// <summary>Without an m4a audio-only format, the highest rate of any audio-only format counts; with none known, 48 kHz, the most YouTube gives.</summary>
    [Test]
    public void Without_m4a_the_highest_audio_rate_counts()
    {
        Assert.That(YtDlpRead.Info("""{"id":"abcdefghijk","formats":[{"ext":"webm","acodec":"opus","vcodec":"none","asr":48000},{"ext":"mp4","acodec":"mp4a","vcodec":"avc1","asr":96000}]}""", "x").SourceRate,
            Is.EqualTo(48000));
        Assert.That(YtDlpRead.Info("""{"id":"abcdefghijk"}""", "x").SourceRate, Is.EqualTo(48000));
    }

    /// <summary>An Arabic title, which yt-dlp writes escaped, reads as written.</summary>
    [Test]
    public void An_arabic_title_reads_as_written()
    {
        var info = YtDlpRead.Info("""{"id":"abcdefghijk","title":"العقيدة"}""", "x");

        Assert.That(info.Title, Is.EqualTo("العقيدة"));
    }

    /// <summary>The handle comes from the channel's address when yt-dlp gives no @handle of its own.</summary>
    [Test]
    public void The_handle_comes_from_the_channels_address_when_none_is_given() =>
        Assert.That(YtDlpRead.Info("""{"id":"abcdefghijk","uploader_id":"UCxx","uploader_url":"https://www.youtube.com/@Teacher"}""", "x").Handle, Is.EqualTo("@Teacher"));

    /// <summary>What is not a video's information is refused, naming where it came from.</summary>
    [TestCase("not json")]
    [TestCase("null")]
    [TestCase("""{"title":"no id"}""")]
    public void What_is_not_a_videos_information_is_refused(string json)
    {
        var refused = Assert.Throws<InvalidDataException>(() => YtDlpRead.Info(json, "abcdefghijk.info.json"));

        Assert.That(refused!.Message, Does.Contain("abcdefghijk.info.json"));
    }

    /// <summary>A playlist's flat listing gives its title and its entries in order, an entry that is null passed over.</summary>
    [Test]
    public void A_playlists_listing_is_read()
    {
        const string json = """
            {"_type": "playlist", "id": "PLxyz", "title": "Risala, the creed", "entries": [
              {"_type": "url", "id": "abcdefghijk", "title": "The creed, part 1", "duration": 3753.0, "channel": "Your Channel", "url": "https://www.youtube.com/watch?v=abcdefghijk"},
              null,
              {"_type": "url", "id": "bcdefghijkl", "title": "The creed, part 2", "duration": null, "channel": null}]}
            """;

        var playlist = YtDlpRead.Playlist(json, "PLxyz");

        Assert.That((playlist.Id, playlist.Title), Is.EqualTo(("PLxyz", "Risala, the creed")));
        Assert.That(playlist.Entries.Select(entry => entry.Id), Is.EqualTo(["abcdefghijk", "bcdefghijkl"]));
        Assert.That(playlist.Entries[0].Title, Is.EqualTo("The creed, part 1"));
    }

    /// <summary>yt-dlp's version is its release date, stable or nightly; anything else is no version.</summary>
    [TestCase("2026.08.19", 2026, 8, 19)]
    [TestCase("2026.08.30.232658", 2026, 8, 30)]
    [TestCase("  2025.11.12  ", 2025, 11, 12)]
    public void The_version_is_its_release_date(string version, int year, int month, int day) =>
        Assert.That(YtDlpRead.ReleaseDate(version), Is.EqualTo(new DateOnly(year, month, day)));

    /// <summary>A line that is no version of yt-dlp's has no release date.</summary>
    [TestCase("")]
    [TestCase("yt-dlp 2026")]
    [TestCase("2026.13.40")]
    public void A_line_that_is_no_version_has_no_date(string version) => Assert.That(YtDlpRead.ReleaseDate(version), Is.Null);
}
