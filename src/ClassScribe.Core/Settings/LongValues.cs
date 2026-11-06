using System.Globalization;

namespace ClassScribe.Core.Settings;

/// <summary>
/// How a line says a value too long to read whole in it, such as a folder longer than any path on Windows can be: by its
/// length and its start, never the whole of it. One rule for every layer that says such a value: the settings check's and the
/// command line's words for a folder longer than any path, a recording's line for a setting it left out, and the refusals of
/// a folder with a character no path may hold.
/// </summary>
public static class LongValues
{
    /// <summary>
    /// The longest value a line quotes whole: longer than any ordinary path, a deep one under OneDrive or WinGet's packages
    /// included, so that every real path is still quoted whole and only a value that cannot be read in a line is shortened.
    /// </summary>
    public const int LongestQuoted = 1_024;

    /// <summary>How many characters of a value too long to quote a line shows: enough to tell which value it is.</summary>
    public const int ShownStart = 40;

    /// <summary>
    /// The start of <paramref name="value"/> a line shows: its first <see cref="ShownStart"/> characters and "...", or the
    /// whole of it when it is no longer than that.
    /// </summary>
    public static string Start(string value) => value.Length > ShownStart ? value[..ShownStart] + "..." : value;

    /// <summary>Whether <paramref name="value"/> is too long for a line to quote whole (longer than <see cref="LongestQuoted"/>).</summary>
    public static bool TooLongToQuote(string? value) => value is { Length: > LongestQuoted };

    /// <summary>
    /// <paramref name="value"/> as a line says it when it is too long to quote: "a value 40,000 characters long, starting
    /// "C:\aaa..."", the words a line puts where the value in quotes would go.
    /// </summary>
    public static string Described(string value) =>
        string.Create(CultureInfo.InvariantCulture, $"a value {value.Length:N0} characters long, starting \"{Start(value)}\"");
}
