using System.Collections.Frozen;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace ClassScribe.Storage;

/// <summary>
/// The names of the recordings' folders, as pure rules: a name made safe for a Windows folder, the date folders of a course
/// (2026-10-17, and 2026-10-17 (2) for a later class of that day), and the folder of the microphone check's logs. Nothing
/// here reads the disk: the folders themselves are found and made by the class folders of Storage.
/// </summary>
public static partial class FolderNames
{
    /// <summary>How a date folder's name writes the local date a class started on: 2026-10-17.</summary>
    public const string DateFolderFormat = "yyyy-MM-dd";

    /// <summary>
    /// The names Windows keeps for devices, alone or followed by an extension, so that no file or folder
    /// can have them. The list is Microsoft's: https://learn.microsoft.com/windows/win32/fileio/naming-a-file
    /// </summary>
    private static readonly FrozenSet<string> DeviceNames = FrozenSet.Create(StringComparer.OrdinalIgnoreCase,
        "CON", "PRN", "AUX", "NUL",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "COM¹", "COM²", "COM³",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9", "LPT¹", "LPT²", "LPT³");

    /// <summary>The name of the date folder of <paramref name="date"/>: 2026-10-17, and 2026-10-17 (2) from the second class of a name that day.</summary>
    public static string DateFolderName(DateOnly date, int copy = 1)
    {
        var day = date.ToString(DateFolderFormat, CultureInfo.InvariantCulture);
        return copy == 1 ? day : string.Create(CultureInfo.InvariantCulture, $"{day} ({copy})");
    }

    /// <summary>
    /// True when <paramref name="name"/> is a date folder's name: the date of a real day as yyyy-MM-dd, alone or followed by
    /// a space and a number in brackets, as in 2026-10-17 (2). Any other name, such as Slides, is not.
    /// </summary>
    public static bool IsDateFolder(string name) => DateFolderMatch(name) is not null;

    /// <summary>
    /// The name of <paramref name="folder"/> when it is the date folder of a later class of its day, such as
    /// 2026-10-17 (2), which the list shows to tell the day's classes apart; null for any other folder, the day's first
    /// date folder included.
    /// </summary>
    public static string? LaterClassOfItsDay(string folder) =>
        Path.GetFileName(Path.TrimEndingDirectorySeparator(folder)) is var name && DateFolderMatch(name) is { } match && match.Groups["copy"].Success ? name : null;

    /// <summary>
    /// Cleans a name for use as a folder and file name: the characters Windows forbids go, runs of
    /// spaces become one, and trailing dots go, as Windows would drop them from a folder's name. A name
    /// Windows keeps for a device, such as NUL or COM1, gets an underscore after it. The result is empty
    /// when nothing usable is left.
    /// </summary>
    public static string SafeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var kept = new string([.. name.Where(c => !invalid.Contains(c))]);
        var tidy = string.Join(' ', kept.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).TrimEnd('.', ' ');

        // Windows 11 reads "nul" as the device, and Windows 10 "nul.backup" and "nul .backup" too, so the part before the first dot decides.
        var device = tidy.Split('.')[0].TrimEnd(' ');
        return DeviceNames.Contains(device) ? tidy.Insert(device.Length, "_") : tidy;
    }

    /// <summary>The match of <paramref name="name"/> as a date folder's name, its date that of a real day; null when it is no date folder's name.</summary>
    private static Match? DateFolderMatch(string name) =>
        DateFolderShape().Match(name) is { Success: true } match
        && DateOnly.TryParseExact(match.Groups["date"].Value, DateFolderFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out _)
            ? match
            : null;

    /// <summary>
    /// The shape of a date folder's name: four, two and two digits for the date, then for a later class of the day a space
    /// and a number in brackets. Digits are 0 to 9 only, and \z ends the match at the very end.
    /// </summary>
    [GeneratedRegex(@"^(?<date>[0-9]{4}-[0-9]{2}-[0-9]{2})(?<copy> \([0-9]{1,9}\))?\z", RegexOptions.CultureInvariant)]
    private static partial Regex DateFolderShape();

    /// <summary>The folder under the recordings folder that the microphone check's logs go in.</summary>
    public const string MicCheck = "mic-check";
}