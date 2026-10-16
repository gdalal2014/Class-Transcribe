using ClassScribe.Desktop;

namespace ClassScribe.Tests;

/// <summary>
/// What stands in for Windows' power requests and power source on another system, where ClassScribe can only transcribe:
/// nothing is asked, so transcribing, which keeps the laptop awake on Windows, works there all the same.
/// </summary>
[TestFixture]
public class OtherSystemPowerTests
{
    /// <summary>A hold asks for nothing: what it needs is never read, and letting it go, twice even, does nothing.</summary>
    [Test]
    public void A_hold_asks_for_nothing_and_reads_nothing()
    {
        var hold = new NoPowerRequests().Hold(StayAwake.TranscribingReason, () => throw new AssertionException("what is needed was read"));

        Assert.DoesNotThrow(hold.Dispose);
        Assert.DoesNotThrow(hold.Dispose);
    }

    /// <summary>The power source counts as battery, as Windows' own answer does when Windows cannot say.</summary>
    [Test]
    public void The_power_source_counts_as_battery() => Assert.That(new UnknownPowerSource().OnBattery, Is.True);
}
