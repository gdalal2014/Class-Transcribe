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

    /// <summary>From an hour up, a length is written in hours and minutes, the minutes rounded down, as the YouTube plans say it.</summary>
    [TestCase(3600.0, "1 h 0 min")]
    [TestCase(31_179.0, "8 h 39 min")]
    [TestCase(90_000.0, "25 h 0 min")]
    public void From_an_hour_a_length_is_written_in_hours_and_minutes(double seconds, string expected) =>
        Assert.That(Durations.Hours(TimeSpan.FromSeconds(seconds)), Is.EqualTo(expected));

    /// <summary>Under an hour, a length is written in whole minutes, rounded, and never as less than one.</summary>
    [TestCase(10.0, "1 min")]
    [TestCase(1500.0, "25 min")]
    [TestCase(3540.0, "59 min")]
    public void Under_an_hour_a_length_is_written_in_minutes(double seconds, string expected) =>
        Assert.That(Durations.Hours(TimeSpan.FromSeconds(seconds)), Is.EqualTo(expected));
}
