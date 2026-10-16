using ClassScribe.Commands;

namespace ClassScribe.Tests;

/// <summary>What Ctrl+C means during a run: stop first, cancel later, and a double press or a held key is not a cancel.</summary>
[TestFixture]
public class ConsoleUserSignalsTests
{
    private ManualTimeProvider _time = null!;
    private ConsoleUserSignals _signals = null!;

    /// <summary>Creates the signals on a clock the test moves by hand.</summary>
    [SetUp]
    public void SetUp()
    {
        _time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        _signals = new ConsoleUserSignals(_time);
    }

    /// <summary>Unhooks the signals from Ctrl+C.</summary>
    [TearDown]
    public void TearDown() => _signals.Dispose();

    /// <summary>The first Ctrl+C stops the recording without cancelling anything; a later one cancels the upload.</summary>
    [Test]
    public void The_first_Ctrl_C_stops_and_a_later_one_cancels()
    {
        _signals.CtrlCPressed();

        Assert.That(_signals.StopRequested.IsCompleted, Is.True);
        Assert.That(_signals.Cancellation.IsCancellationRequested, Is.False);

        _time.Advance(TimeSpan.FromSeconds(3));
        _signals.CtrlCPressed();

        Assert.That(_signals.Cancellation.IsCancellationRequested, Is.True);
    }

    /// <summary>
    /// A second Ctrl+C right after the one that stopped the recording, as a double press or a held key
    /// gives, still means stop: the upload goes ahead.
    /// </summary>
    [Test]
    public void A_Ctrl_C_right_after_the_one_that_stopped_does_not_cancel()
    {
        _signals.CtrlCPressed();
        _time.Advance(TimeSpan.FromMilliseconds(150));
        _signals.CtrlCPressed();
        _time.Advance(TimeSpan.FromMilliseconds(1500));
        _signals.CtrlCPressed();

        Assert.That(_signals.StopRequested.IsCompleted, Is.True);
        Assert.That(_signals.Cancellation.IsCancellationRequested, Is.False);
    }

    /// <summary>
    /// The two seconds after the Ctrl+C that stopped the recording are timed by the time that has really
    /// passed. A clock set back in the meantime, as a time sync may do, does not stretch them: a Ctrl+C ten
    /// seconds later still cancels the upload.
    /// </summary>
    [Test]
    public void A_clock_set_back_after_the_stop_does_not_keep_a_later_Ctrl_C_from_cancelling()
    {
        _signals.CtrlCPressed();
        _time.StepWallClock(TimeSpan.FromHours(-1));
        _time.Advance(TimeSpan.FromSeconds(10));
        _signals.CtrlCPressed();

        Assert.That(_signals.Cancellation.IsCancellationRequested, Is.True);
    }

    /// <summary>
    /// A clock set forward right after the Ctrl+C that stopped the recording does not cut the two seconds
    /// short: a second Ctrl+C at once is still part of the stop, and the upload goes ahead.
    /// </summary>
    [Test]
    public void A_clock_set_forward_after_the_stop_does_not_turn_a_double_press_into_a_cancel()
    {
        _signals.CtrlCPressed();
        _time.StepWallClock(TimeSpan.FromHours(1));
        _signals.CtrlCPressed();

        Assert.That(_signals.StopRequested.IsCompleted, Is.True);
        Assert.That(_signals.Cancellation.IsCancellationRequested, Is.False);
    }

    /// <summary>
    /// When the stop came from elsewhere (Q, or the transcribe command, which has nothing to stop), the
    /// first Ctrl+C already cancels, at once.
    /// </summary>
    [Test]
    public void After_a_stop_from_elsewhere_the_first_Ctrl_C_cancels_at_once()
    {
        _signals.RequestStop();
        _signals.CtrlCPressed();

        Assert.That(_signals.Cancellation.IsCancellationRequested, Is.True);
    }
}
