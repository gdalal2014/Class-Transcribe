using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.RegularExpressions;

namespace ClassScribe.YouTube;

/// <summary>What a YouTube link names.</summary>
public enum YouTubeLinkKind
{
    /// <summary>One video: a watch link, a short link (youtu.be), or a short's.</summary>
    Video,

    /// <summary>One video given by its live link (/live/ID): a stream, whose state only a read of it can tell, so the download path alone takes it.</summary>
    Live,

    /// <summary>A playlist: the download path alone takes it, and reads its videos from it.</summary>
    Playlist
}

/// <summary>
/// A YouTube link as ClassScribe takes it: the kind and the ID, nothing else of what was typed. Every link handed on, to
/// yt-dlp, a source file or a transcript, is rebuilt from the ID (<see cref="Url"/>), so that nothing typed reaches a tool as
/// it was typed.
/// </summary>
/// <param name="Kind">What it names.</param>
/// <param name="Id">The video's ID (11 characters) or the playlist's.</param>
/// <param name="StartSeconds">Where a video's link starts it, in seconds (t=), kept for the record path; null when it names none.</param>
public sealed record YouTubeLink(YouTubeLinkKind Kind, string Id, int? StartSeconds = null)
{
    /// <summary>The link rebuilt from the ID: https://www.youtube.com/watch?v=ID for a video, https://www.youtube.com/playlist?list=ID for a playlist.</summary>
    public string Url => Kind == YouTubeLinkKind.Playlist ? YouTubeLinks.PlaylistUrl(Id) : YouTubeLinks.WatchUrl(Id);

    /// <summary>The video's link with the time it starts at (&amp;t=754s), or the plain one when it names none.</summary>
    public string UrlAtStart => StartSeconds is { } seconds ? YouTubeLinks.WatchUrl(Id, seconds) : Url;
}

/// <summary>What reading one link came to: the link, or why it is not taken.</summary>
/// <param name="Link">The link; null when it is not taken.</param>
/// <param name="Problem">Why it is not taken, in a sentence fit to show; null when it is.</param>
public sealed record LinkReading(YouTubeLink? Link, string? Problem);

/// <summary>
/// Reads YouTube links (design 1.2): a watch link (www., m., music. or none), youtu.be and /shorts/ for a video, /live/ for a
/// stream, /playlist?list= for a playlist. A watch link that names a playlist too means that one video. A channel's link is
/// refused (a channel's uploads can be hundreds of hours; a playlist is the way to give many), and so is an embedded
/// player's (/embed/, youtube-nocookie.com), most likely copied from a web page such as the course's, which ClassScribe never
/// reads. Anything else, a link that is not YouTube's or one that names no video, is refused with its reason.
/// </summary>
public static partial class YouTubeLinks
{
    /// <summary>How the refusal of an embedded player's link begins, so that the menu and the command line say it alike.</summary>
    public const string EmbeddedPlayer = "This is the link of a player embedded in a web page, most likely your course page.";

    /// <summary>The advice after <see cref="EmbeddedPlayer"/>: the player's own button gives the video's link, which can be recorded as it plays.</summary>
    public const string EmbeddedPlayerAdvice = "Use the YouTube button on the player for the video's own link.";

    /// <summary>The hosts of YouTube's own pages, in any letter case.</summary>
    private static readonly HashSet<string> PageHosts = new(StringComparer.OrdinalIgnoreCase) { "youtube.com", "www.youtube.com", "m.youtube.com", "music.youtube.com" };

    /// <summary>The hosts of the player YouTube offers for embedding without cookies.</summary>
    private static readonly HashSet<string> NoCookieHosts = new(StringComparer.OrdinalIgnoreCase) { "youtube-nocookie.com", "www.youtube-nocookie.com" };

    /// <summary>The first word of a channel's address on youtube.com.</summary>
    private static readonly HashSet<string> ChannelWords = new(StringComparer.OrdinalIgnoreCase) { "channel", "c", "user" };

    /// <summary>The link of video <paramref name="videoId"/>, starting at <paramref name="startSeconds"/> when given.</summary>
    public static string WatchUrl(string videoId, int? startSeconds = null) =>
        startSeconds is { } seconds
            ? string.Create(CultureInfo.InvariantCulture, $"https://www.youtube.com/watch?v={videoId}&t={seconds}s")
            : $"https://www.youtube.com/watch?v={videoId}";

    /// <summary>The link of playlist <paramref name="playlistId"/>.</summary>
    public static string PlaylistUrl(string playlistId) => $"https://www.youtube.com/playlist?list={playlistId}";

    /// <summary>True when <paramref name="text"/> is a video's ID: 11 characters of A-Z, a-z, 0-9, _ and -.</summary>
    public static bool IsVideoId([NotNullWhen(true)] string? text) => text is not null && VideoId().IsMatch(text);

    /// <summary>True when <paramref name="text"/> is a playlist's ID: 2 to 64 characters of A-Z, a-z, 0-9, _ and -.</summary>
    public static bool IsPlaylistId([NotNullWhen(true)] string? text) => text is not null && PlaylistId().IsMatch(text);

    /// <summary>Reads one link, as typed or as a line of a list file gives it, with or without its https://.</summary>
    public static LinkReading Read(string text)
    {
        var typed = text.Trim();
        var withScheme = typed.Contains("://", StringComparison.Ordinal) ? typed : "https://" + typed;
        if (typed.Length == 0
            || !Uri.TryCreate(withScheme, UriKind.Absolute, out var address)
            || address.Scheme is not ("https" or "http"))
            return Refused($"\"{typed}\" is not a YouTube link.");

        var host = address.Host;
        if (NoCookieHosts.Contains(host)) return Refused($"{EmbeddedPlayer} {EmbeddedPlayerAdvice}");

        var segments = address.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (host.Equals("youtu.be", StringComparison.OrdinalIgnoreCase))
            return segments is [var shortId] ? Video(YouTubeLinkKind.Video, shortId, address, typed) : NamesNoVideo(typed);

        if (!PageHosts.Contains(host)) return Refused($"\"{typed}\" is not a YouTube link.");

        return segments switch
        {
            ["watch"] => Video(YouTubeLinkKind.Video, Query(address, "v"), address, typed),
            ["shorts", var id] => Video(YouTubeLinkKind.Video, id, address, typed),
            ["live", var id] => Video(YouTubeLinkKind.Live, id, address, typed),
            ["embed", ..] => Refused($"{EmbeddedPlayer} {EmbeddedPlayerAdvice}"),
            ["playlist"] => Query(address, "list") is var list && IsPlaylistId(list)
                ? new LinkReading(new YouTubeLink(YouTubeLinkKind.Playlist, list), null)
                : NamesNoVideo(typed),
            [var first, ..] when first.StartsWith('@') || ChannelWords.Contains(first) =>
                Refused($"\"{typed}\" is a channel's link; a channel's uploads can be hundreds of hours, so give a playlist's link, or the videos' own."),
            _ => NamesNoVideo(typed)
        };
    }

    /// <summary>A video of <paramref name="kind"/> when <paramref name="id"/> is a video's ID, with the time the link starts at; otherwise the refusal.</summary>
    private static LinkReading Video(YouTubeLinkKind kind, string? id, Uri address, string typed) =>
        IsVideoId(id) ? new LinkReading(new YouTubeLink(kind, id, StartOf(Query(address, "t"))), null) : NamesNoVideo(typed);

    /// <summary>The refusal of a YouTube link that names no video.</summary>
    private static LinkReading NamesNoVideo(string typed) => Refused($"\"{typed}\" is a YouTube link that names no video or playlist.");

    /// <summary>A refusal with <paramref name="problem"/>.</summary>
    private static LinkReading Refused(string problem) => new(null, problem);

    /// <summary>The value of <paramref name="name"/> in the address's query, unescaped; null when it is not there.</summary>
    private static string? Query(Uri address, string name)
    {
        foreach (var pair in address.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = pair.IndexOf('=');
            var key = equals < 0 ? pair : pair[..equals];
            if (key.Equals(name, StringComparison.Ordinal)) return equals < 0 ? "" : Uri.UnescapeDataString(pair[(equals + 1)..]);
        }

        return null;
    }

    /// <summary>A start time as YouTube writes it, 754, 754s, 12m34s or 1h2m3s, in seconds; null for none, or one it cannot read.</summary>
    private static int? StartOf(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        if (StartTime().Match(text) is not { Success: true } match) return null;

        int Part(string group) => match.Groups[group].Success ? int.Parse(match.Groups[group].Value, CultureInfo.InvariantCulture) : 0;
        var seconds = Part("h") * 3600L + Part("m") * 60L + Part("s");
        return seconds is > 0 and < int.MaxValue ? (int)seconds : null;
    }

    /// <summary>A video's ID: 11 of YouTube's characters, and nothing else; \z ends the match at the very end.</summary>
    [GeneratedRegex(@"^[A-Za-z0-9_-]{11}\z", RegexOptions.CultureInvariant)]
    private static partial Regex VideoId();

    /// <summary>A playlist's ID: 2 to 64 of YouTube's characters.</summary>
    [GeneratedRegex(@"^[A-Za-z0-9_-]{2,64}\z", RegexOptions.CultureInvariant)]
    private static partial Regex PlaylistId();

    /// <summary>A start time: seconds alone, or hours, minutes and seconds each with its letter.</summary>
    [GeneratedRegex(@"^(?:(?<s>[0-9]{1,6})s?|(?:(?<h>[0-9]{1,3})h)?(?:(?<m>[0-9]{1,4})m)?(?:(?<s>[0-9]{1,6})s)?)\z", RegexOptions.CultureInvariant)]
    private static partial Regex StartTime();
}
