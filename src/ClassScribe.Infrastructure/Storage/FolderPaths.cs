using System.Globalization;

namespace ClassScribe.Infrastructure.Storage;

/// <summary>
/// The recordings folder a setting or an option names: where it is, and whether its full path is longer than any path on
/// Windows can be, which the settings check, the saved classes' check, the command line and the menus refuse as they refuse a
/// character no path may hold. It reads the environment (its variables and the user profile) and nothing of the disk, and the
/// folder need not be there; it lives in Infrastructure, beside <see cref="ClassFolders"/>, since Core and Application call
/// nothing of the machine: the saved classes' check in Core is handed <see cref="TooLong(string?)"/> by its callers, and the
/// class command reaches it through <c>IClassFolders.TooLong</c>.
/// </summary>
public static class FolderPaths
{
    /// <summary>
    /// The most characters a full path on Windows may have: 32,767 with the null that ends it, Windows' limit for any path,
    /// a \\?\ one included. Path.GetFullPath refuses a path of 32,767 characters or more with a
    /// <see cref="PathTooLongException"/>, but passes a \\?\ path as it is, whatever its length, so both are looked at. It is
    /// the limit of any path, not of a recordings folder: one just under it passes the check and still fails where it is used,
    /// once the course, the date folder and the file names are added to it.
    /// </summary>
    public const int LongestPath = 32_766;

    /// <summary>How many characters of a value too long a problem shows: enough to tell which value it is, never the whole of it.</summary>
    public const int ShownStart = 40;

    /// <summary>
    /// The folder a recordings folder setting of <paramref name="value"/> names, as a path and nothing more: environment
    /// variables such as %USERPROFILE% expanded and any surrounding quotes removed, a relative path taken from the user
    /// profile, and an empty one meaning a ClassScribe folder there. Throws a <see cref="PathTooLongException"/> for a value
    /// whose full path is longer than any path can be (see <see cref="TooLong(string?)"/>).
    /// </summary>
    public static string Named(string? value)
    {
        var configured = Expanded(value);
        return configured.Length == 0
            ? Path.Combine(Profile, "ClassScribe")
            : Path.GetFullPath(configured, Profile);
    }

    /// <summary>
    /// What is wrong with <paramref name="value"/>, a recordings folder setting, when its full path, as <see cref="Named"/>
    /// works it out, is longer than <see cref="LongestPath"/>: the words that follow the setting's name, such as "names a
    /// folder whose full path is 40,012 characters long, starting "C:\Classes\aaa..."; no path on Windows can be longer than
    /// 32,766 characters". Null when it fits, when it is empty, and when it holds a character no path may hold, which is a
    /// problem of its own.
    /// </summary>
    public static string? TooLong(string? value) => TooLong(value, Expanded(value), Profile);

    /// <summary>
    /// The same for <paramref name="value"/> as a command-line option gives it: taken as it is, a relative one from
    /// <paramref name="baseFolder"/>, the current folder, as Path.GetFullPath takes it; and when that fits, as the run takes
    /// the full path it is given, with its environment variables expanded (<see cref="ClassFolders.RootFor"/>), so that a
    /// value short as written but too long once expanded is refused on the command line too, by the length the run would use.
    /// </summary>
    public static string? TooLong(string value, string baseFolder)
    {
        var path = value.Trim();
        if (TooLong(value, path, baseFolder) is { } asWritten) return asWritten;
        if (path.Length == 0 || path.IndexOfAny(Path.GetInvalidPathChars()) >= 0) return null;

        return TooLong(value, Expanded(Path.GetFullPath(path, baseFolder)), Profile);
    }

    /// <summary>
    /// The words for <paramref name="value"/> as written when <paramref name="path"/>, the path it names, taken from
    /// <paramref name="baseFolder"/>, is longer than any path can be; null when it fits.
    /// </summary>
    private static string? TooLong(string? value, string path, string baseFolder)
    {
        if (FullLength(path, baseFolder) is not { } length) return null;

        var written = (value ?? "").Trim();
        var start = written.Length > ShownStart ? written[..ShownStart] + "..." : written;
        return string.Create(CultureInfo.InvariantCulture,
            $"names a folder whose full path is {length:N0} characters long, starting \"{start}\"; no path on Windows can be longer than {LongestPath:N0} characters");
    }

    /// <summary>
    /// The length of the full path <paramref name="path"/> names, a relative one taken from <paramref name="baseFolder"/>,
    /// when it is longer than <see cref="LongestPath"/>; null when it fits, is empty, or holds a character no path may hold.
    /// </summary>
    private static int? FullLength(string path, string baseFolder)
    {
        if (path.Length == 0 || path.IndexOfAny(Path.GetInvalidPathChars()) >= 0) return null;

        try
        {
            var full = Path.GetFullPath(path, baseFolder);
            return full.Length > LongestPath ? full.Length : null;
        }
        catch (PathTooLongException)
        {
            // Deliberately not rethrown: the refusal is the answer, said in words of its own by the caller. Its length is the
            // path as joined, never less than the first length Windows refuses, since Windows refused it.
            var joined = Path.IsPathFullyQualified(path) ? path.Length : Path.Join(baseFolder, path).Length;
            return Math.Max(joined, LongestPath + 1);
        }
    }

    /// <summary>A setting's path as it is used: environment variables such as %USERPROFILE% expanded, and the quotes and spaces around it gone.</summary>
    private static string Expanded(string? value) => Environment.ExpandEnvironmentVariables(value ?? "").Trim().Trim('"').Trim();

    /// <summary>The user profile, from which a relative recordings folder is taken.</summary>
    private static string Profile => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
}
