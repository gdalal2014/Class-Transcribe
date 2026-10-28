using System.Text.RegularExpressions;

namespace ClassScribe.YouTube;

/// <summary>
/// The titles of a video's window (design 4.1), as the run reads them to see the video end: YouTube writes the video's title,
/// with "(3) " before it when there are notifications and " - YouTube" after it, and Chrome adds " - Google Chrome" to an
/// ordinary window's; what is left is the video's title. YouTube alone, as between two videos, is no title. Every title and
/// channel name from YouTube, its pages, oEmbed or yt-dlp, goes through <see cref="Plain"/> before ClassScribe keeps it.
/// </summary>
public static partial class VideoTitles
{
    /// <summary>
    /// <paramref name="text"/>, words from YouTube such as a title or a channel's name, fit to show and keep: each control
    /// character becomes a space, since a BEL written to the window can ring, and the class recording would take the sound in,
    /// and an escape can start a sequence; line breaks and tabs too, as a title is one line. Null stays null.
    /// </summary>
    public static string? Plain(string? text) =>
        text is null || !text.Any(char.IsControl) ? text : new string([.. text.Select(character => char.IsControl(character) ? ' ' : character)]);

    /// <summary>The video's title in a window's <paramref name="raw"/> title, without control characters; null for none.</summary>
    public static string? Clean(string? raw)
    {
        var title = (Plain(raw) ?? "").Trim();
        title = Notifications().Replace(title, "");
        foreach (var ending in new[] { " - Google Chrome", " - YouTube" })
        {
            if (title.EndsWith(ending, StringComparison.Ordinal)) title = title[..^ending.Length].TrimEnd();
        }

        return title.Length == 0 || title.Equals("YouTube", StringComparison.Ordinal) ? null : title;
    }

    /// <summary>The first title of <paramref name="titles"/> that names a video; null for none.</summary>
    public static string? First(IEnumerable<string> titles) => titles.Select(Clean).FirstOrDefault(title => title is not null);

    /// <summary>YouTube's count of notifications before a title, such as "(3) ".</summary>
    [GeneratedRegex(@"^\([0-9]{1,4}\+?\)\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Notifications();
}
