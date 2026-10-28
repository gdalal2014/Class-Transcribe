using System.Text.RegularExpressions;

namespace ClassScribe.YouTube;

/// <summary>
/// Reads why a yt-dlp run failed (design 3.8). yt-dlp exits 1 whenever anything failed, so its lines say why, and they are
/// YouTube's words, which change: they are matched loosely, in lower case with the typographic apostrophe made plain, in
/// this order: a bot check, a rate limit, a missing helper, options refused (exit code 2), a missing format, a sign-in, a
/// video not shown here or gone, YouTube out of reach, and anything else in yt-dlp's own words. yt-dlp adds advice to pass
/// cookies to any reason that mentions signing in; ClassScribe never passes cookies, so every sentence that mentions them
/// is taken out of what is shown.
/// </summary>
public static partial class YtDlpErrors
{
    /// <summary>The command that updates winget's yt-dlp, the first thing to try when it fails with a format error.</summary>
    public const string Update = "winget upgrade --source winget --id yt-dlp.yt-dlp";

    /// <summary>yt-dlp's own update to its newest nightly build, when winget finds nothing newer.</summary>
    public const string UpdateToNightly = "yt-dlp --update-to nightly";

    /// <summary>The failure of a run that ended with <paramref name="exitCode"/> after writing <paramref name="errorLines"/>, its standard error.</summary>
    public static DownloadFailure Classify(int exitCode, IReadOnlyList<string> errorLines)
    {
        var text = string.Join('\n', errorLines).ToLowerInvariant().Replace('’', '\'');
        // True when the error lines say any of the words.
        bool Says(params string[] words) => words.Any(word => text.Contains(word, StringComparison.Ordinal));

        if (Says("not a bot"))
            return new(DownloadFailureKind.BotCheck,
                "YouTube asked to confirm you are not a bot. Wait a day or more before downloading again; ClassScribe never signs in or uses your browser's cookies. These videos can be recorded as they play instead.",
                StopsFetching: true);
        if (Says("rate-limited", "rate limited", "try again later", "captcha", "http error 429"))
            return new(DownloadFailureKind.RateLimit, "YouTube is limiting this connection for up to an hour. Run the same command later; what is done is skipped.", StopsFetching: true);
        if (Says("no supported javascript runtime", "challenge solving failed", "ffmpeg not found", "ffprobe and ffmpeg not found", "ffprobe not found"))
            return new(DownloadFailureKind.Setup, "yt-dlp is missing a helper (Deno or FFmpeg): ClassScribe youtube check says which.", StopsFetching: true);
        if (exitCode == 2)
            return new(DownloadFailureKind.OptionsRefused, "yt-dlp did not accept ClassScribe's options; it may be too old or much newer: ClassScribe youtube check.", StopsFetching: true);
        if (Says("requested format is not available", "only images are available"))
            return new(DownloadFailureKind.Format,
                $"yt-dlp could not get this video's audio, most often because it is out of date: {Update}; if winget finds nothing newer, {UpdateToNightly}.",
                StopsFetching: false);
        if (Says("private video", "this video is private", "channel's members", "members-only", "confirm your age", "sign in to view"))
            return new(DownloadFailureKind.SignIn, "It needs a sign-in, which ClassScribe never does. If you can watch it, record it as it plays.", StopsFetching: false);
        if (Says("not made this video available in your country", "not available in your country"))
            return new(DownloadFailureKind.Unavailable, "It is not shown in your country.", StopsFetching: false);
        if (Says("removed", "video unavailable", "does not exist", "has been terminated"))
            return new(DownloadFailureKind.Unavailable, "It is gone from YouTube, or not available.", StopsFetching: false);
        if (Says("unable to download", "timed out", "getaddrinfo", "connection"))
            return new(DownloadFailureKind.Network, "YouTube could not be reached; run the same command again.", StopsFetching: false);

        var line = errorLines.FirstOrDefault(error => error.StartsWith("ERROR", StringComparison.Ordinal))
                   ?? errorLines.LastOrDefault(error => !string.IsNullOrWhiteSpace(error));
        return new(DownloadFailureKind.Other,
            line is null ? $"yt-dlp ended with exit code {exitCode} and said nothing." : $"yt-dlp said: {WithoutCookieAdvice(line)}",
            StopsFetching: false);
    }

    /// <summary><paramref name="line"/> without any sentence that mentions cookies: ClassScribe never passes yt-dlp cookies, so that advice does not apply.</summary>
    public static string WithoutCookieAdvice(string line) =>
        string.Join(' ', SentenceEnd().Split(line.Trim()).Where(sentence => !sentence.Contains("cookie", StringComparison.OrdinalIgnoreCase))).Trim();

    /// <summary>The space after a sentence's full stop.</summary>
    [GeneratedRegex(@"(?<=\.)\s+", RegexOptions.CultureInvariant)]
    private static partial Regex SentenceEnd();
}
