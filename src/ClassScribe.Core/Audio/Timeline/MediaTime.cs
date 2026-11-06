namespace ClassScribe.Audio.Timeline;

/// <summary>
/// Time on the recording clock, in units of 100 nanoseconds: the unit Windows stamps a packet in (the performance
/// counter's value when its first frame was recorded, as GetBuffer hands it over) and the unit of a <see cref="TimeSpan"/>'s
/// ticks. Converts the steady clock's counts to it and back without overflowing, whatever the clock's frequency, and a
/// track's frames to it and back, so that every track and every stamp is compared on one clock.
/// </summary>
public static class MediaTime
{
    /// <summary>Units of 100 nanoseconds in a second: ten million, the same as <see cref="TimeSpan.TicksPerSecond"/>.</summary>
    public const long UnitsPerSecond = TimeSpan.TicksPerSecond;

    /// <summary>
    /// A count of a steady clock that ticks <paramref name="frequency"/> times a second, as units of 100 nanoseconds. The
    /// whole seconds and the rest of the count are converted apart, so the arithmetic never overflows a long, even at
    /// the largest counts and frequencies; a part of a unit is dropped, toward zero.
    /// </summary>
    public static long From(long timestamp, long frequency)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frequency);
        var seconds = timestamp / frequency;
        var rest = timestamp % frequency;
        return seconds * UnitsPerSecond + rest * UnitsPerSecond / frequency;
    }

    /// <summary>Units of 100 nanoseconds as a count of a steady clock that ticks <paramref name="frequency"/> times a second: the reverse of <see cref="From"/>, without overflow and dropping a part of a count toward zero.</summary>
    public static long ToTimestamp(long units, long frequency)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frequency);
        var seconds = units / UnitsPerSecond;
        var rest = units % UnitsPerSecond;
        return seconds * frequency + rest * frequency / UnitsPerSecond;
    }

    /// <summary>The moment now on <paramref name="time"/>'s steady clock, in units of 100 nanoseconds: the clock a packet's stamp is on.</summary>
    public static long Now(TimeProvider time) => From(time.GetTimestamp(), time.TimestampFrequency);

    /// <summary>How long <paramref name="frames"/> frames last at <paramref name="sampleRate"/> frames a second, to the nearest unit of 100 nanoseconds.</summary>
    public static TimeSpan FromFrames(long frames, int sampleRate)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        return new TimeSpan(Scale(frames, UnitsPerSecond, sampleRate));
    }

    /// <summary>How many frames last <paramref name="length"/> at <paramref name="sampleRate"/> frames a second, to the nearest frame.</summary>
    public static long ToFrames(TimeSpan length, int sampleRate)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        return Scale(length.Ticks, sampleRate, UnitsPerSecond);
    }

    /// <summary>
    /// <paramref name="value"/> times <paramref name="numerator"/> over <paramref name="denominator"/>, to the nearest
    /// whole number, with the whole part and the rest scaled apart so that nothing overflows; a value below zero scales
    /// as its opposite does.
    /// </summary>
    private static long Scale(long value, long numerator, long denominator)
    {
        var magnitude = Math.Abs(value);
        var whole = magnitude / denominator;
        var rest = magnitude % denominator;
        var scaled = whole * numerator + (rest * numerator + denominator / 2) / denominator;
        return value < 0 ? -scaled : scaled;
    }
}
