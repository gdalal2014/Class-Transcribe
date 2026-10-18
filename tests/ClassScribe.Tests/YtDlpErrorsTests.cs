using ClassScribe.YouTube;

namespace ClassScribe.Tests;

/// <summary>
/// yt-dlp's failures, matched loosely (design 3.8, with Gulzar's answer 10): YouTube's words change, so they are matched in
/// lower case with the typographic apostrophe made plain, in order; a bot check, a rate limit, a missing helper or options
/// refused stop the fetching, the rest are that video's alone; and yt-dlp's advice to pass cookies is never shown.
/// </summary>
[TestFixture]
public class YtDlpErrorsTests
{
    /// <summary>Classifies one error line, with exit code 1.</summary>
    private static DownloadFailure Classify(string line, int exitCode = 1) => YtDlpErrors.Classify(exitCode, ["[youtube] abcdefghijk: Downloading webpage", line]);

    /// <summary>A bot check stops the fetching, whatever else the line says, the typographic apostrophe included, and its advice never involves cookies.</summary>
    [TestCase("ERROR: [youtube] abcdefghijk: Sign in to confirm you’re not a bot. Use --cookies-from-browser or --cookies for the authentication.")]
    [TestCase("ERROR: [youtube] abcdefghijk: Sign in to confirm you're not a bot")]
    public void A_bot_check_stops_the_fetching(string line)
    {
        var failure = Classify(line);

        Assert.That(failure.Kind, Is.EqualTo(DownloadFailureKind.BotCheck));
        Assert.That(failure.StopsFetching, Is.True);
        Assert.That(failure.Said, Does.StartWith("YouTube asked to confirm you are not a bot. Wait a day or more before downloading again"));
        Assert.That(failure.Said, Does.Contain("never signs in or uses your browser's cookies"));
    }

    /// <summary>A rate limit, a captcha or HTTP 429 stops the fetching for the batch.</summary>
    [TestCase("ERROR: [youtube] abcdefghijk: The current session has been rate-limited by YouTube for up to an hour")]
    [TestCase("ERROR: unable to download video data: HTTP Error 429: Too Many Requests")]
    [TestCase("ERROR: [youtube] abcdefghijk: Please solve the CAPTCHA")]
    [TestCase("ERROR: [youtube] abcdefghijk: Try again later.")]
    public void A_rate_limit_stops_the_fetching(string line)
    {
        var failure = Classify(line);

        Assert.That((failure.Kind, failure.StopsFetching), Is.EqualTo((DownloadFailureKind.RateLimit, true)));
        Assert.That(failure.Said, Does.StartWith("YouTube is limiting this connection for up to an hour"));
    }

    /// <summary>A missing helper stops the fetching, and points to youtube check.</summary>
    [TestCase("WARNING: [youtube] No supported JavaScript runtime could be found")]
    [TestCase("ERROR: [youtube] abcdefghijk: n challenge solving failed")]
    [TestCase("ERROR: Postprocessing: ffprobe and ffmpeg not found. Please install or provide the path using --ffmpeg-location")]
    public void A_missing_helper_stops_the_fetching(string line)
    {
        var failure = Classify(line);

        Assert.That((failure.Kind, failure.StopsFetching), Is.EqualTo((DownloadFailureKind.Setup, true)));
        Assert.That(failure.Said, Does.Contain("ClassScribe youtube check"));
    }

    /// <summary>Exit code 2 is yt-dlp refusing ClassScribe's options, which stops the fetching.</summary>
    [Test]
    public void Exit_code_2_is_options_refused()
    {
        var failure = YtDlpErrors.Classify(2, ["yt-dlp: error: no such option: --js-runtimes"]);

        Assert.That((failure.Kind, failure.StopsFetching), Is.EqualTo((DownloadFailureKind.OptionsRefused, true)));
        Assert.That(failure.Said, Does.Contain("did not accept ClassScribe's options"));
    }

    /// <summary>A format that is not available is that video's alone, with the update commands: winget's first, then yt-dlp's own nightly.</summary>
    [TestCase("ERROR: [youtube] abcdefghijk: Requested format is not available. Use --list-formats for a list of available formats")]
    [TestCase("ERROR: [youtube] abcdefghijk: Only images are available for download")]
    public void A_missing_format_is_that_videos_alone_with_the_update_commands(string line)
    {
        var failure = Classify(line);

        Assert.That((failure.Kind, failure.StopsFetching), Is.EqualTo((DownloadFailureKind.Format, false)));
        Assert.That(failure.Said, Does.Contain("winget upgrade --source winget --id yt-dlp.yt-dlp").And.Contain("--update-to nightly"));
    }

    /// <summary>A video that needs a sign-in is that video's alone, and may be recorded as it plays.</summary>
    [TestCase("ERROR: [youtube] abcdefghijk: Private video. Sign in if you've been granted access to this video")]
    [TestCase("ERROR: [youtube] abcdefghijk: Join this channel to get access to members-only content like this video")]
    [TestCase("ERROR: [youtube] abcdefghijk: Sign in to confirm your age. This video may be inappropriate for some users.")]
    public void A_video_that_needs_a_sign_in_is_that_videos_alone(string line)
    {
        var failure = Classify(line);

        Assert.That((failure.Kind, failure.StopsFetching), Is.EqualTo((DownloadFailureKind.SignIn, false)));
        Assert.That(failure.Said, Does.Contain("record it as it plays"));
        Assert.That(failure.Said, Does.Not.Contain("cookies"));
    }

    /// <summary>A video not shown in this country, or gone, is that video's alone.</summary>
    [TestCase("ERROR: [youtube] abcdefghijk: The uploader has not made this video available in your country", "not shown in your country")]
    [TestCase("ERROR: [youtube] abcdefghijk: Video unavailable. This video has been removed by the uploader", "gone from YouTube")]
    public void A_video_not_shown_here_or_gone_is_that_videos_alone(string line, string said)
    {
        var failure = Classify(line);

        Assert.That((failure.Kind, failure.StopsFetching), Is.EqualTo((DownloadFailureKind.Unavailable, false)));
        Assert.That(failure.Said, Does.Contain(said));
    }

    /// <summary>YouTube out of reach is that video's alone; the batch counts them, and three in a row stop it.</summary>
    [TestCase("ERROR: Unable to download webpage: <urlopen error [Errno 11001] getaddrinfo failed>")]
    [TestCase("ERROR: [youtube] abcdefghijk: Read timed out.")]
    public void YouTube_out_of_reach_is_a_network_failure(string line) =>
        Assert.That((Classify(line).Kind, Classify(line).StopsFetching), Is.EqualTo((DownloadFailureKind.Network, false)));

    /// <summary>Anything else is said in yt-dlp's own words, without its advice to pass cookies.</summary>
    [Test]
    public void Anything_else_is_said_in_yt_dlps_words_without_the_cookie_advice()
    {
        var failure = Classify("ERROR: [youtube] abcdefghijk: Something new happened. Use --cookies-from-browser or --cookies for the authentication. See https://github.com/yt-dlp/yt-dlp/wiki/FAQ#how-do-i-pass-cookies-to-yt-dlp for how to manually pass cookies");

        Assert.That(failure.Kind, Is.EqualTo(DownloadFailureKind.Other));
        Assert.That(failure.Said, Is.EqualTo("yt-dlp said: ERROR: [youtube] abcdefghijk: Something new happened."));
    }

    /// <summary>Without an ERROR line, the last line yt-dlp wrote is said; without any, the exit code.</summary>
    [Test]
    public void Without_an_error_line_the_last_line_or_the_exit_code_is_said()
    {
        Assert.That(YtDlpErrors.Classify(1, ["first", "last words"]).Said, Is.EqualTo("yt-dlp said: last words"));
        Assert.That(YtDlpErrors.Classify(7, []).Said, Is.EqualTo("yt-dlp ended with exit code 7 and said nothing."));
    }
}
