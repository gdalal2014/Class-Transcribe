using System.Globalization;

namespace ClassScribe;

/// <summary>
/// Writes lengths and positions in a recording as hours, minutes and seconds, with as many hours as
/// there are: 25:03:07 rather than the 01:03:07 that TimeSpan's own "hh" format gives.
/// </summary>
public static class Durations
{
    /// <summary>Formats <paramref name="duration"/> as hh:mm:ss, rounding down to the second; a negative value counts as zero.</summary>
    public static string Format(TimeSpan duration)
    {
        var length = duration < TimeSpan.Zero ? TimeSpan.Zero : duration;
        return string.Create(CultureInfo.InvariantCulture, $"{(int)length.TotalHours:00}:{length.Minutes:00}:{length.Seconds:00}");
    }
}
