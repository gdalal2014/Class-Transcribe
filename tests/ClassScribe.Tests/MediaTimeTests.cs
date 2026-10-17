using System.Numerics;
using ClassScribe.Audio.Timeline;

namespace ClassScribe.Tests;

/// <summary>
/// Time on the recording clock in units of 100 nanoseconds: the steady clock's counts converted to it and back without
/// overflowing, whatever the clock's frequency, and a track's frames converted to it and back, rounded to the nearest.
/// </summary>
[TestFixture]
public sealed class MediaTimeTests
{
    /// <summary>A count of the steady clock becomes units of 100 nanoseconds: at 10 MHz, this laptop's frequency, the count is the time itself.</summary>
    [TestCase(10_000_000L, 12_345_678_901L, 12_345_678_901L)]
    [TestCase(3_579_545L, 3_579_545L, 10_000_000L)]
    [TestCase(24_000_000L, 36_000_000L, 15_000_000L)]
    [TestCase(1_000L, 1_234L, 12_340_000L)]
    public void A_count_of_the_steady_clock_becomes_units_of_100_nanoseconds(long frequency, long timestamp, long expected) =>
        Assert.That(MediaTime.From(timestamp, frequency), Is.EqualTo(expected));

    /// <summary>
    /// A count near 2 to the 62nd, at a frequency where multiplying it by ten million first would overflow a long, is
    /// converted as the exact arithmetic gives it, and back again to within the part of a unit the conversion drops: at
    /// 24 MHz a unit of 100 nanoseconds is 2.4 counts.
    /// </summary>
    [Test]
    public void A_count_near_two_to_the_62nd_does_not_overflow()
    {
        const long frequency = 24_000_000;
        const long timestamp = 1L << 62;
        var expected = (long)(new BigInteger(timestamp) * MediaTime.UnitsPerSecond / frequency);

        var units = MediaTime.From(timestamp, frequency);

        Assert.That(units, Is.EqualTo(expected));
        Assert.That(MediaTime.ToTimestamp(units, frequency), Is.EqualTo(timestamp).Within((long)Math.Ceiling((double)frequency / MediaTime.UnitsPerSecond)));
    }

    /// <summary>Units of 100 nanoseconds become a count of the steady clock, the reverse of <see cref="MediaTime.From"/>, truncated like it.</summary>
    [TestCase(10_000_000L, 15_000_000L, 15_000_000L)]
    [TestCase(3_579_545L, 10_000_000L, 3_579_545L)]
    [TestCase(1_000L, 12_349_999L, 1_234L)]
    public void Units_become_a_count_of_the_steady_clock(long frequency, long units, long expected) =>
        Assert.That(MediaTime.ToTimestamp(units, frequency), Is.EqualTo(expected));

    /// <summary>A count below zero, as a time before a clock's zero would be, converts toward zero as a positive one does away from it.</summary>
    [Test]
    public void A_count_below_zero_converts_toward_zero() =>
        Assert.That(MediaTime.From(-3_579_545L * 3 / 2, 3_579_545L), Is.EqualTo(-MediaTime.From(3_579_545L * 3 / 2, 3_579_545L)));

    /// <summary>A frequency of zero or less is no clock at all, and is refused rather than dividing by zero.</summary>
    [TestCase(0L)]
    [TestCase(-10_000_000L)]
    public void A_frequency_of_zero_or_less_is_refused(long frequency) =>
        Assert.That(() => MediaTime.From(1, frequency), Throws.TypeOf<ArgumentOutOfRangeException>());

    /// <summary>Now is the time provider's count converted, so a packet's stamp and the moment it arrives are on one clock.</summary>
    [Test]
    public void Now_is_the_time_providers_count_in_units()
    {
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 10, 17, 19, 0, 0, TimeSpan.Zero));
        time.Advance(TimeSpan.FromSeconds(12.5));

        Assert.That(MediaTime.Now(time), Is.EqualTo(TimeSpan.FromSeconds(12.5).Ticks));
    }

    /// <summary>Frames at a sample rate become the time they last: 480 frames at 48 kHz and 441 at 44.1 kHz are both 10 ms exactly, and a part of a unit is rounded to the nearest.</summary>
    [TestCase(480L, 48_000, 100_000L)]
    [TestCase(441L, 44_100, 100_000L)]
    [TestCase(1L, 48_000, 208L)]
    [TestCase(2L, 48_000, 417L)]
    [TestCase(48_000L * 7_200, 48_000, 72_000_000_000L)]
    public void Frames_become_the_time_they_last(long frames, int sampleRate, long expectedTicks) =>
        Assert.That(MediaTime.FromFrames(frames, sampleRate), Is.EqualTo(new TimeSpan(expectedTicks)));

    /// <summary>A length of time becomes the frames that last it at a sample rate, rounded to the nearest frame: less than half a frame is no frame.</summary>
    [TestCase(100_000L, 48_000, 480L)]
    [TestCase(500_000L, 48_000, 2_400L)]
    [TestCase(1L, 48_000, 0L)]
    [TestCase(104L, 48_000, 0L)]
    [TestCase(105L, 48_000, 1L)]
    [TestCase(-500_000L, 48_000, -2_400L)]
    public void A_length_becomes_the_frames_that_last_it(long ticks, int sampleRate, long expectedFrames) =>
        Assert.That(MediaTime.ToFrames(new TimeSpan(ticks), sampleRate), Is.EqualTo(expectedFrames));

    /// <summary>A whole class of frames converts to its time and back to the same frames, at the rates ClassScribe records at.</summary>
    [TestCase(44_100)]
    [TestCase(48_000)]
    [TestCase(96_000)]
    public void Frames_survive_the_round_trip_through_time(int sampleRate)
    {
        var frames = (long)sampleRate * 3 * 3600 + 7;

        Assert.That(MediaTime.ToFrames(MediaTime.FromFrames(frames, sampleRate), sampleRate), Is.EqualTo(frames));
    }
}
