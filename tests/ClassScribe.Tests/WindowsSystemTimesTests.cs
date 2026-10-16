using System.Runtime.Versioning;
using ClassScribe.Desktop.Windows;

namespace ClassScribe.Tests;

/// <summary>
/// The computer's clocks as ClassScribe reads them on Windows. The interrupt times are Windows' own; nothing is changed by
/// reading them.
/// </summary>
[TestFixture, Platform("Win")]
[SupportedOSPlatform("windows")]
public class WindowsSystemTimesTests
{
    /// <summary>
    /// The interrupt time counts the time the laptop slept and the unbiased interrupt time leaves it out, so the interrupt
    /// time is never the smaller of the two, read in that order; both count from the start of Windows.
    /// </summary>
    [Test, Category("WindowsPower")]
    public void The_interrupt_time_is_never_below_the_unbiased_interrupt_time()
    {
        var times = new WindowsSystemTimes(TimeProvider.System);

        for (var reading = 0; reading < 1000; reading++)
        {
            var unbiased = times.UnbiasedInterruptTime100ns();
            var interrupt = times.InterruptTime100ns();

            Assert.That(unbiased, Is.GreaterThan(0));
            Assert.That(interrupt, Is.GreaterThanOrEqualTo(unbiased));
        }
    }

    /// <summary>Both interrupt times move on with time while the laptop is awake.</summary>
    [Test, Category("WindowsPower")]
    public async Task The_interrupt_times_move_on_with_time()
    {
        var times = new WindowsSystemTimes(TimeProvider.System);
        var interrupt = times.InterruptTime100ns();
        var unbiased = times.UnbiasedInterruptTime100ns();

        await Task.Delay(50);

        Assert.That(times.InterruptTime100ns(), Is.GreaterThan(interrupt));
        Assert.That(times.UnbiasedInterruptTime100ns(), Is.GreaterThan(unbiased));
    }

    /// <summary>The steady clock, its frequency and the time of day are those of the clock ClassScribe is given, so that tests can move them.</summary>
    [Test]
    public void The_steady_clock_and_the_time_of_day_come_from_the_clock_given()
    {
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 10, 15, 23, 42, 10, TimeSpan.Zero));
        time.Advance(TimeSpan.FromSeconds(90));
        var times = new WindowsSystemTimes(time);

        Assert.That(times.Timestamp(), Is.EqualTo(time.GetTimestamp()));
        Assert.That(times.TimestampFrequency, Is.EqualTo(time.TimestampFrequency));
        Assert.That(times.UtcNow(), Is.EqualTo(time.GetUtcNow()));
    }
}
