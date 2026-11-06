namespace ClassScribe.YouTube;

/// <summary>What kind of failure a yt-dlp run ended with (design 3.8).</summary>
public enum DownloadFailureKind
{
    /// <summary>YouTube asked to confirm that this is not a bot: the fetching stops.</summary>
    BotCheck,

    /// <summary>YouTube is limiting this connection (rate limit, captcha, HTTP 429): the fetching stops.</summary>
    RateLimit,

    /// <summary>yt-dlp is missing a helper, Deno or FFmpeg: the fetching stops.</summary>
    Setup,

    /// <summary>yt-dlp refused ClassScribe's options (exit code 2): the fetching stops.</summary>
    OptionsRefused,

    /// <summary>yt-dlp could not get this video's audio, most often because it is out of date.</summary>
    Format,

    /// <summary>The video needs a sign-in, which ClassScribe never does.</summary>
    SignIn,

    /// <summary>The video is not shown in this country, or gone from YouTube.</summary>
    Unavailable,

    /// <summary>YouTube could not be reached: three in a row stop the fetching.</summary>
    Network,

    /// <summary>Anything else, in yt-dlp's own words.</summary>
    Other
}

/// <summary>What a failed yt-dlp run means, in words Gulzar reads.</summary>
/// <param name="Kind">The kind of failure.</param>
/// <param name="Said">What to show, with what to do.</param>
/// <param name="StopsFetching">True when no other video is fetched in this batch: the next would fail the same way, or deepen YouTube's limit.</param>
public sealed record DownloadFailure(DownloadFailureKind Kind, string Said, bool StopsFetching);
