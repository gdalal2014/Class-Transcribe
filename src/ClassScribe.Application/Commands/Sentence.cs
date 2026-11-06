using System.Globalization;

namespace ClassScribe.Commands;

/// <summary>Sentences that the report, the starter and the launcher build around a message from elsewhere, such as a run's outcome.</summary>
public static class Sentence
{
    /// <summary>
    /// <paramref name="text"/> as a whole sentence: without trailing spaces, and with a full stop added unless it already
    /// ends with one, a question mark or an exclamation mark, as "No audio was captured. Was anything playing?" does.
    /// </summary>
    public static string Of(string text)
    {
        var trimmed = text.TrimEnd();
        return trimmed is [.., '.' or '?' or '!'] ? trimmed : $"{trimmed}.";
    }
}

/// <summary>Moments as the report and the starter say them: in the local time of the clock given, the day added when it is not today.</summary>
public static class LocalTime
{
    /// <summary>
    /// <paramref name="utc"/> in <paramref name="time"/>'s local time, as "19:47", or with <paramref name="seconds"/> as
    /// "19:47:12"; with the day when it was not today, as "19:47 on 15 October".
    /// </summary>
    public static string Of(DateTimeOffset utc, TimeProvider time, bool seconds = false)
    {
        var local = TimeZoneInfo.ConvertTime(utc, time.LocalTimeZone);
        var clock = local.ToString(seconds ? "HH:mm:ss" : "HH:mm", CultureInfo.InvariantCulture);
        return local.Date == TimeZoneInfo.ConvertTime(time.GetUtcNow(), time.LocalTimeZone).Date
            ? clock
            : $"{clock} on {local.ToString("d MMMM", CultureInfo.InvariantCulture)}";
    }
}
