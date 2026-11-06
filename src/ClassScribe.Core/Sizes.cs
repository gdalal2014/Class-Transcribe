using System.Globalization;

namespace ClassScribe;

/// <summary>
/// Writes sizes as ClassScribe prints them everywhere, of files, uploads and room on a drive: in the units Google One shows,
/// such as 727 MB or 1.2 TB.
/// </summary>
public static class Sizes
{
    /// <summary>A size in the units Google One shows (1 TB is 1024 GB): one decimal below 10, as "1.2 TB", none from 10 up, as "30 TB".</summary>
    public static string Describe(long bytes)
    {
        string[] units = ["bytes", "KB", "MB", "GB", "TB", "PB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        var number = unit == 0 || value >= 10 ? Math.Round(value).ToString("0", CultureInfo.InvariantCulture) : value.ToString("0.#", CultureInfo.InvariantCulture);
        return $"{number} {units[unit]}";
    }
}
