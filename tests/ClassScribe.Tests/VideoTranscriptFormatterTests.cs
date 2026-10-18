using ClassScribe.Settings;
using ClassScribe.Storage;
using ClassScribe.Transcription;
using ClassScribe.YouTube;

namespace ClassScribe.Tests;

/// <summary>
/// A YouTube video's transcript (design 2.9, with Gulzar's answer 8): headed by the video's title and link, with how its
/// audio was obtained, and in a downloaded video's transcript each position links to that moment on YouTube, since the
/// positions are the video's own. A recording made as the video played keeps plain positions, which ads and pauses shift.
/// </summary>
[TestFixture]
public class VideoTranscriptFormatterTests
{
    private static readonly string Blank = Environment.NewLine + Environment.NewLine;

    /// <summary>When the video was downloaded: 14:02 UTC on 18 October 2026.</summary>
    private static readonly DateTimeOffset Obtained = new(2026, 10, 18, 14, 2, 11, TimeSpan.Zero);

    /// <summary>A transcript of two paragraphs, at 0:00 and at 12:34.</summary>
    private static readonly TranscriptionResult Transcript = new("Bismillah. Next part.", "ar", 1.0, 3763, null,
    [
        new("Bismillah.", 0.0, 0.8, "word", null), new(" ", 0.8, 754.0, "spacing", null), new("Next", 754.0, 754.3, "word", null),
        new(" ", 754.3, 754.4, "spacing", null), new("part.", 754.4, 755.0, "word", null)
    ], "{}");

    /// <summary>The formatter, with positions on unless told otherwise.</summary>
    private static TranscriptFormatter Formatter(bool timestamps = true) => new(TestOptions.Create(new TranscriptOptions { IncludeTimestamps = timestamps }));

    /// <summary>
    /// A downloaded video's transcript is headed by its title, says when it was downloaded, names the video by its link with
    /// its channel, upload date, length and playlist and the yt-dlp that fetched it, and links each position to that moment.
    /// </summary>
    [Test]
    public void A_downloaded_videos_transcript_is_headed_by_its_title_and_links_each_moment()
    {
        var text = Formatter().Format(Transcript, "Risala", Obtained, video: VideoSourceFileTests.Downloaded());

        var url = "https://www.youtube.com/watch?v=abcdefghijk";
        Assert.That(text, Is.EqualTo(
            $"# The creed, part 1{Blank}" +
            $"Downloaded 18 October 2026, 14:02 UTC.{Blank}" +
            $"From YouTube: [The creed, part 1]({url}), by Your Channel, uploaded 3 March 2025, 01:02:33 long, number 1 in the playlist \"Risala, the creed\"; downloaded with yt-dlp 2026.08.19. Positions are the video's own, and each links to that moment on YouTube.{Blank}" +
            $"[[00:00:00]]({url}&t=0s) Bismillah.{Blank}" +
            $"[[00:12:34]]({url}&t=754s) Next part.{Environment.NewLine}"));
    }

    /// <summary>A mark's heading in a downloaded video's transcript links to its moment too.</summary>
    [Test]
    public void A_marks_heading_links_to_its_moment()
    {
        var text = Formatter().Format(Transcript, "Risala", Obtained, [new ClassMark(1, TimeSpan.FromSeconds(60), "Iman")], video: VideoSourceFileTests.Downloaded());

        Assert.That(text, Does.Contain($"## [[00:01:00]](https://www.youtube.com/watch?v=abcdefghijk&t=60s) Mark 1: Iman{Blank}"));
    }

    /// <summary>Without positions there is nothing to link, and the heading stays.</summary>
    [Test]
    public void Without_positions_nothing_is_linked()
    {
        var text = Formatter(timestamps: false).Format(Transcript, "Risala", Obtained, video: VideoSourceFileTests.Downloaded());

        Assert.That(text, Does.StartWith($"# The creed, part 1{Blank}Downloaded 18 October 2026"));
        Assert.That(text, Does.Contain($"{Blank}Bismillah.{Blank}Next part."));
        Assert.That(text, Does.Not.Contain("&t="));
    }

    /// <summary>
    /// A video recorded as it played says so, with the recording's length, and keeps plain positions: ads and pauses shift
    /// them from the video's own times, so a link would land in the wrong place.
    /// </summary>
    [Test]
    public void A_recorded_videos_transcript_keeps_plain_positions()
    {
        var recorded = VideoSourceFileTests.Downloaded() with
        {
            Obtained = VideoSource.Recorded, Tool = "Chrome 141.0", UploadDate = null, Duration = null, Availability = null, Playlist = null,
            Confirmed = null, AcceptedBy = null, Ended = "VideoEnded"
        };

        var text = Formatter().Format(Transcript, "Risala", Obtained, video: recorded);

        Assert.That(text, Does.StartWith(
            $"# The creed, part 1{Blank}Recorded 18 October 2026, 14:02 UTC.{Blank}" +
            "From YouTube: [The creed, part 1](https://www.youtube.com/watch?v=abcdefghijk), by Your Channel; recorded as it played in ClassScribe's window (01:02:43). " +
            $"Positions are in the recording, so ads and pauses shift them from the video's own times.{Blank}[00:00:00] Bismillah."));
    }

    /// <summary>A title with Markdown's own characters is escaped, so that it reads as written in the heading and in the link.</summary>
    [Test]
    public void A_title_with_Markdowns_characters_is_escaped()
    {
        var source = VideoSourceFileTests.Downloaded(title: "Part *1* [draft] _of_ <2> `x` a\\b");

        var text = Formatter().Format(Transcript, "Risala", Obtained, video: source);

        const string escaped = @"Part \*1\* \[draft\] \_of\_ \<2\> \`x\` a\\b";
        Assert.That(text, Does.StartWith($"# {escaped}{Blank}"));
        Assert.That(text, Does.Contain($"From YouTube: [{escaped}](https://www.youtube.com/watch?v=abcdefghijk)"));
    }

    /// <summary>Of what a download's line names, only what is known is said: a video without channel, date, length or playlist gets its link alone.</summary>
    [Test]
    public void Only_what_is_known_is_said()
    {
        var bare = VideoSourceFileTests.Downloaded() with { Channel = null, UploadDate = null, Duration = null, Playlist = null, Tool = null };

        var text = Formatter().Format(Transcript, "Risala", Obtained, video: bare);

        Assert.That(text, Does.Contain(
            "From YouTube: [The creed, part 1](https://www.youtube.com/watch?v=abcdefghijk); downloaded with yt-dlp. Positions are the video's own, and each links to that moment on YouTube."));
    }
}
