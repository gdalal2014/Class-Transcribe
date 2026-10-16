namespace ClassScribe.Tests;

/// <summary>How lengths and positions are written.</summary>
[TestFixture]
public class DurationsTests
{
    /// <summary>Lengths are written as hh:mm:ss, rounded down to the second.</summary>
    [TestCase(0.0, "00:00:00")]
    [TestCase(59.9, "00:00:59")]
    [TestCase(3725.0, "01:02:05")]
    public void Lengths_are_written_as_hours_minutes_and_seconds(double seconds, string expected) =>
        Assert.That(Durations.Format(TimeSpan.FromSeconds(seconds)), Is.EqualTo(expected));

    /// <summary>A day or more keeps all its hours instead of wrapping round to zero.</summary>
    [Test]
    public void A_day_or_more_keeps_all_its_hours() =>
        Assert.That(Durations.Format(new TimeSpan(1, 1, 3, 7)), Is.EqualTo("25:03:07"));

    /// <summary>A negative length, such as a position before the start, is written as zero.</summary>
    [Test]
    public void A_negative_length_is_written_as_zero() =>
        Assert.That(Durations.Format(TimeSpan.FromSeconds(-3)), Is.EqualTo("00:00:00"));
}
