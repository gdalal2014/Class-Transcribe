using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace ClassScribe.YouTube;

/// <summary>One format a video offers, of which only what tells the source's sample rate is read.</summary>
/// <param name="FormatId">yt-dlp's id of the format.</param>
/// <param name="Ext">Its file extension, such as m4a or webm.</param>
/// <param name="Acodec">Its audio codec; "none" for video alone.</param>
/// <param name="Vcodec">Its video codec; "none" for audio alone.</param>
/// <param name="Asr">Its audio's sample rate, in Hz.</param>
public sealed record YtDlpFormat(string? FormatId, string? Ext, string? Acodec, string? Vcodec, double? Asr);

/// <summary>
/// A video as its read's information file gives it (design 3.4): the fields the guard, the plan, the source file and the
/// transcript need. Every other field yt-dlp writes is passed over.
/// </summary>
/// <param name="Id">The video's ID.</param>
/// <param name="Title">Its title.</param>
/// <param name="Duration">Its length in seconds; null while it is not known, as for a stream.</param>
/// <param name="Channel">Its channel's name.</param>
/// <param name="ChannelId">Its channel's UC id.</param>
/// <param name="UploaderId">Its uploader's id, normally the channel's @handle.</param>
/// <param name="UploaderUrl">Its uploader's address, normally https://www.youtube.com/@handle.</param>
/// <param name="ChannelUrl">Its channel's address.</param>
/// <param name="UploadDate">When it was uploaded, as yyyyMMdd.</param>
/// <param name="WebpageUrl">Its watch link.</param>
/// <param name="Availability">public, unlisted, private, needs_auth, subscriber_only or premium_only; null when yt-dlp is not sure.</param>
/// <param name="LiveStatus">not_live, is_live, is_upcoming, was_live or post_live.</param>
/// <param name="WasLive">True when it was a stream.</param>
/// <param name="AgeLimit">The age it is restricted to; 0 for none.</param>
/// <param name="Formats">The formats it offers.</param>
public sealed record YtDlpInfo(string? Id, string? Title, double? Duration, string? Channel, string? ChannelId, string? UploaderId, string? UploaderUrl,
    string? ChannelUrl, string? UploadDate, string? WebpageUrl, string? Availability, string? LiveStatus, bool? WasLive, int? AgeLimit,
    IReadOnlyList<YtDlpFormat>? Formats)
{
    /// <summary>The rate assumed when no format says its own: 48 kHz, the most YouTube's audio has.</summary>
    public const int HighestRate = 48000;

    /// <summary>Its length; null when it is not known.</summary>
    public TimeSpan? Length => Duration is { } seconds and > 0 && double.IsFinite(seconds) ? TimeSpan.FromSeconds(seconds) : null;

    /// <summary>
    /// Its channel's @handle: the uploader's id when it is one, else the one in the uploader's or the channel's address; null
    /// when none is given. A handle written percent-encoded, as an address writes one of Arabic letters, is read as written,
    /// so that it matches the handle a setting names.
    /// </summary>
    public string? Handle =>
        UploaderId is { } id && id.StartsWith('@') ? Unescaped(id)
        : new[] { UploaderUrl, ChannelUrl }.Select(HandleIn).FirstOrDefault(handle => handle is not null);

    /// <summary>When it was uploaded, as yyyy-MM-dd; null when not known.</summary>
    public string? Uploaded =>
        DateOnly.TryParseExact(UploadDate, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : null;

    /// <summary>
    /// The sample rate its downloaded WAV has, which keeps YouTube's own: that of the best m4a format of audio alone, which a
    /// download takes first, else the highest of any format of audio alone, else <see cref="HighestRate"/>.
    /// </summary>
    public int SourceRate
    {
        get
        {
            var audioOnly = (Formats ?? []).Where(format => format.Vcodec == "none" && format.Acodec is not (null or "none") && format.Asr is > 0).ToList();
            var m4a = audioOnly.Where(format => format.Ext == "m4a").Select(format => format.Asr!.Value).DefaultIfEmpty(0).Max();
            var any = audioOnly.Select(format => format.Asr!.Value).DefaultIfEmpty(0).Max();
            var rate = m4a > 0 ? m4a : any > 0 ? any : HighestRate;
            return (int)Math.Round(rate);
        }
    }

    /// <summary>The @handle in a channel's address, https://www.youtube.com/@handle, read as written, or null.</summary>
    private static string? HandleIn(string? address) =>
        address is not null && address.LastIndexOf("/@", StringComparison.Ordinal) is var at and >= 0 ? Unescaped(address[(at + 1)..].Split('/', '?', '#')[0]) : null;

    /// <summary>
    /// <paramref name="handle"/> with its percent-encoding undone, as OEmbedTitles reads a handle from an address, and without
    /// control characters; a % that starts no escape is left as it is.
    /// </summary>
    private static string Unescaped(string handle) => VideoTitles.Plain(Uri.UnescapeDataString(handle))!;
}

/// <summary>One entry of a playlist's flat listing.</summary>
/// <param name="Id">The video's ID.</param>
/// <param name="Title">Its title.</param>
/// <param name="Duration">Its length in seconds, when given.</param>
/// <param name="Channel">Its channel's name, when given.</param>
public sealed record YtDlpPlaylistEntry(string? Id, string? Title, double? Duration, string? Channel);

/// <summary>A playlist as its flat listing gives it.</summary>
/// <param name="Id">The playlist's ID.</param>
/// <param name="Title">Its title.</param>
/// <param name="Entries">Its videos, in order; an entry yt-dlp gave as null is left out.</param>
public sealed record YtDlpPlaylist(string? Id, string? Title, IReadOnlyList<YtDlpPlaylistEntry> Entries);

/// <summary>The JSON yt-dlp writes, read with its snake_case names, generated at compile time; a field this build does not know is passed over.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower, NumberHandling = JsonNumberHandling.AllowReadingFromString)]
[JsonSerializable(typeof(YtDlpInfo))]
[JsonSerializable(typeof(PlaylistListing))]
internal sealed partial class YtDlpJson : JsonSerializerContext;

/// <summary>A playlist's flat listing as it is read, before its null entries are left out.</summary>
/// <param name="Id">The playlist's ID.</param>
/// <param name="Title">Its title.</param>
/// <param name="Entries">Its entries, some perhaps null.</param>
internal sealed record PlaylistListing(string? Id, string? Title, IReadOnlyList<YtDlpPlaylistEntry?>? Entries);

/// <summary>Reads yt-dlp's answers: a video's information file, a playlist's flat listing, and its version.</summary>
public static partial class YtDlpRead
{
    /// <summary>The video's information in <paramref name="json"/>, from <paramref name="where"/>. What is not a video's information is refused with an <see cref="InvalidDataException"/> naming where it came from.</summary>
    public static YtDlpInfo Info(string json, string where)
    {
        YtDlpInfo? info;
        try
        {
            info = JsonSerializer.Deserialize(json, YtDlpJson.Default.YtDlpInfo);
        }
        catch (JsonException ex)
        {
            // Handed on as what it means here, an answer of yt-dlp that cannot be read, with the reader's own failure inside.
            throw new InvalidDataException($"yt-dlp's information in {where} cannot be read: {ex.Message}", ex);
        }

        if (info is not { Id: not null }) throw new InvalidDataException($"yt-dlp's information in {where} cannot be read: it names no video.");

        // YouTube's words are shown and kept: none of them carries a control character from here on.
        return info with
        {
            Title = VideoTitles.Plain(info.Title), Channel = VideoTitles.Plain(info.Channel), ChannelId = VideoTitles.Plain(info.ChannelId),
            UploaderId = VideoTitles.Plain(info.UploaderId), UploaderUrl = VideoTitles.Plain(info.UploaderUrl), ChannelUrl = VideoTitles.Plain(info.ChannelUrl),
            Availability = VideoTitles.Plain(info.Availability), LiveStatus = VideoTitles.Plain(info.LiveStatus)
        };
    }

    /// <summary>The playlist <paramref name="playlistId"/>'s flat listing in <paramref name="json"/>. What is not a listing is refused with an <see cref="InvalidDataException"/>.</summary>
    public static YtDlpPlaylist Playlist(string json, string playlistId)
    {
        PlaylistListing? listing;
        try
        {
            listing = JsonSerializer.Deserialize(json, YtDlpJson.Default.PlaylistListing);
        }
        catch (JsonException ex)
        {
            // Handed on as what it means here, an answer of yt-dlp that cannot be read, with the reader's own failure inside.
            throw new InvalidDataException($"yt-dlp's listing of the playlist {playlistId} cannot be read: {ex.Message}", ex);
        }

        if (listing is null) throw new InvalidDataException($"yt-dlp's listing of the playlist {playlistId} cannot be read: it is empty.");
        return new YtDlpPlaylist(listing.Id, VideoTitles.Plain(listing.Title),
        [
            .. (listing.Entries ?? []).OfType<YtDlpPlaylistEntry>().Where(entry => YouTubeLinks.IsVideoId(entry.Id))
                .Select(entry => entry with { Title = VideoTitles.Plain(entry.Title), Channel = VideoTitles.Plain(entry.Channel) })
        ]);
    }

    /// <summary>The release date yt-dlp's version gives, such as 2026.08.19, or a nightly's 2026.08.30.232658; null for a line that is no version.</summary>
    public static DateOnly? ReleaseDate(string version) =>
        Version().Match(version.Trim()) is { Success: true } match
        && DateOnly.TryParseExact(match.Groups["date"].Value, "yyyy.MM.dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : null;

    /// <summary>yt-dlp's version: its release date as yyyy.MM.dd, and for a nightly a time after it.</summary>
    [GeneratedRegex(@"^(?<date>[0-9]{4}\.[0-9]{2}\.[0-9]{2})(?:\.[0-9]+)?\z", RegexOptions.CultureInvariant)]
    private static partial Regex Version();
}
