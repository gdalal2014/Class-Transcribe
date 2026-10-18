using ClassScribe.YouTube;

namespace ClassScribe.Tests;

/// <summary>
/// The links a batch takes (design 1.2): every form of a video's link read as its ID and rebuilt from it, a playlist read as
/// one, a live link kept apart, and a channel's or an embedded player's link refused with its reason. Nothing typed is ever
/// handed on as it was typed: each link is rebuilt from its ID.
/// </summary>
[TestFixture]
public class YouTubeLinkTests
{
    /// <summary>Every form of a video's link gives the video's ID, and the link rebuilt from it alone.</summary>
    [TestCase("https://www.youtube.com/watch?v=abcdefghijk")]
    [TestCase("https://youtube.com/watch?v=abcdefghijk")]
    [TestCase("https://m.youtube.com/watch?v=abcdefghijk")]
    [TestCase("https://music.youtube.com/watch?v=abcdefghijk")]
    [TestCase("http://www.youtube.com/watch?v=abcdefghijk")]
    [TestCase("www.youtube.com/watch?v=abcdefghijk")]
    [TestCase("https://www.youtube.com/watch?v=abcdefghijk&list=PLxyz123&index=3&pp=abc&si=xyz")]
    [TestCase("https://www.youtube.com/watch?feature=share&v=abcdefghijk")]
    [TestCase("https://youtu.be/abcdefghijk")]
    [TestCase("https://youtu.be/abcdefghijk?si=Zx-123")]
    [TestCase("youtu.be/abcdefghijk")]
    [TestCase("https://www.youtube.com/shorts/abcdefghijk")]
    [TestCase("https://WWW.YOUTUBE.COM/watch?v=abcdefghijk")]
    public void A_videos_link_is_read_as_its_id_and_rebuilt_from_it(string text)
    {
        var reading = YouTubeLinks.Read(text);

        Assert.That(reading.Problem, Is.Null);
        Assert.That(reading.Link, Is.EqualTo(new YouTubeLink(YouTubeLinkKind.Video, "abcdefghijk")));
        Assert.That(reading.Link!.Url, Is.EqualTo("https://www.youtube.com/watch?v=abcdefghijk"));
    }

    /// <summary>The time a link starts at is kept, in each of the ways YouTube writes it, for the record path; the link itself is the plain one.</summary>
    [TestCase("https://youtu.be/abcdefghijk?t=754", 754)]
    [TestCase("https://www.youtube.com/watch?v=abcdefghijk&t=754s", 754)]
    [TestCase("https://www.youtube.com/watch?v=abcdefghijk&t=12m34s", 754)]
    [TestCase("https://www.youtube.com/watch?v=abcdefghijk&t=1h2m3s", 3723)]
    public void The_time_a_link_starts_at_is_kept(string text, int seconds)
    {
        var link = YouTubeLinks.Read(text).Link!;

        Assert.That(link.StartSeconds, Is.EqualTo(seconds));
        Assert.That(link.Url, Is.EqualTo("https://www.youtube.com/watch?v=abcdefghijk"));
        Assert.That(link.UrlAtStart, Is.EqualTo($"https://www.youtube.com/watch?v=abcdefghijk&t={seconds}s"));
    }

    /// <summary>A live stream's link is the video's ID, marked as live, which only the download path takes.</summary>
    [Test]
    public void A_live_link_is_a_video_marked_live()
    {
        var link = YouTubeLinks.Read("https://www.youtube.com/live/abcdefghijk?si=abc").Link;

        Assert.That(link, Is.EqualTo(new YouTubeLink(YouTubeLinkKind.Live, "abcdefghijk")));
        Assert.That(link!.Url, Is.EqualTo("https://www.youtube.com/watch?v=abcdefghijk"));
    }

    /// <summary>A playlist's link gives the playlist's ID, and its link rebuilt from it.</summary>
    [Test]
    public void A_playlists_link_is_read_as_its_id()
    {
        var link = YouTubeLinks.Read("https://www.youtube.com/playlist?list=PLabc_DEF-123&si=xyz").Link;

        Assert.That(link, Is.EqualTo(new YouTubeLink(YouTubeLinkKind.Playlist, "PLabc_DEF-123")));
        Assert.That(link!.Url, Is.EqualTo("https://www.youtube.com/playlist?list=PLabc_DEF-123"));
    }

    /// <summary>A channel's link is refused, with the advice to give a playlist.</summary>
    [TestCase("https://www.youtube.com/@jawed")]
    [TestCase("https://www.youtube.com/@jawed/videos")]
    [TestCase("https://www.youtube.com/channel/UCabcdefghijklmnopqrstuv")]
    [TestCase("https://www.youtube.com/c/Something")]
    [TestCase("https://www.youtube.com/user/Something")]
    public void A_channels_link_is_refused(string text)
    {
        var reading = YouTubeLinks.Read(text);

        Assert.That(reading.Link, Is.Null);
        Assert.That(reading.Problem, Does.Contain("channel").And.Contain("playlist"));
    }

    /// <summary>An embedded player's link is refused, as most likely copied from a web page such as the course's, with the advice to use the player's own YouTube button.</summary>
    [TestCase("https://www.youtube.com/embed/abcdefghijk")]
    [TestCase("https://www.youtube-nocookie.com/embed/abcdefghijk?rel=0")]
    [TestCase("https://youtube-nocookie.com/embed/abcdefghijk")]
    public void An_embedded_players_link_is_refused(string text)
    {
        var reading = YouTubeLinks.Read(text);

        Assert.That(reading.Link, Is.Null);
        Assert.That(reading.Problem, Does.StartWith(YouTubeLinks.EmbeddedPlayer));
        Assert.That(reading.Problem, Does.Contain("YouTube button"));
    }

    /// <summary>What is not YouTube's, or names no video, is refused with its reason.</summary>
    [TestCase("https://vimeo.com/12345")]
    [TestCase("https://www.youtube.com.evil.example/watch?v=abcdefghijk")]
    [TestCase("ftp://www.youtube.com/watch?v=abcdefghijk")]
    [TestCase("hello")]
    [TestCase("")]
    public void What_is_not_YouTubes_is_refused(string text)
    {
        var reading = YouTubeLinks.Read(text);

        Assert.That(reading.Link, Is.Null);
        Assert.That(reading.Problem, Does.Contain("not a YouTube link"));
    }

    /// <summary>A YouTube link whose video ID is not 11 of YouTube's characters, or that names no video, is refused.</summary>
    [TestCase("https://www.youtube.com/watch?v=abc")]
    [TestCase("https://www.youtube.com/watch?v=abcdefghij!")]
    [TestCase("https://youtu.be/abcdefghijkl")]
    [TestCase("https://www.youtube.com/watch")]
    [TestCase("https://www.youtube.com/")]
    [TestCase("https://www.youtube.com/playlist?list=")]
    [TestCase("https://www.youtube.com/playlist?list=PL\"abc")]
    public void A_YouTube_link_that_names_no_video_is_refused(string text)
    {
        var reading = YouTubeLinks.Read(text);

        Assert.That(reading.Link, Is.Null);
        Assert.That(reading.Problem, Does.Contain("names no video"));
    }

    /// <summary>A video ID is 11 characters of A-Z, a-z, 0-9, _ and -.</summary>
    [TestCase("abcdefghijk", true)]
    [TestCase("A_b-0123456", true)]
    [TestCase("abcdefghij", false)]
    [TestCase("abcdefghijkl", false)]
    [TestCase("abcdefghij١", false)]
    [TestCase("abc def ghi", false)]
    public void A_video_id_is_eleven_of_YouTubes_characters(string id, bool isOne) =>
        Assert.That(YouTubeLinks.IsVideoId(id), Is.EqualTo(isOne));
}
