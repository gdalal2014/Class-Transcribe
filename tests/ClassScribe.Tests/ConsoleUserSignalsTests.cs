using ClassScribe.Commands;

namespace ClassScribe.Tests;

/// <summary>
/// What Ctrl+C means during a run. While recording, two presses within five seconds stop, as Q twice does, so that a
/// Ctrl+C pressed to get out of the window does not end the class; a single press only asks for the second. After the
/// stop, a press cancels whatever still runs, such as the upload, except right after the press that stopped, which a
/// double press or a held key gives. The same holds for a Ctrl+C a window passes on to the recorder. Once the run's
/// signals are disposed, a press does nothing.
/// </summary>
[TestFixture]
public class ConsoleUserSignalsTests
{
    /// <summary>What a single Ctrl+C while recording says.</summary>
    private const string SecondPressRequest = "Press Ctrl+C again within 5 seconds to stop";

    private ManualTimeProvider _time = null!;
    private RecordingLogger<ConsoleUserSignals> _log = null!;
    private ConsoleUserSignals _signals = null!;

    /// <summary>Creates the signals on a clock the test moves by hand, with a log to inspect.</summary>
    [SetUp]
    public void SetUp()
    {
        _time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        _log = new RecordingLogger<ConsoleUserSignals>();
        _signals = new ConsoleUserSignals(_time, _log);
    }

    /// <summary>Unhooks the signals from Ctrl+C.</summary>
    [TearDown]
    public void TearDown() => _signals.Dispose();

    /// <summary>How many times the signals have asked for a second Ctrl+C.</summary>
    private int SecondPressRequests() => _log.Messages.Count(message => message == SecondPressRequest);

    /// <summary>A single Ctrl+C while recording, pressed by mistake or to get out of the window, only asks for a second one: nothing stops.</summary>
    [Test]
    public void A_single_Ctrl_C_while_recording_only_asks_for_a_second()
    {
        _signals.CtrlCPressed();

        Assert.That(_signals.StopRequested.IsCompleted, Is.False, "still recording");
        Assert.That(_signals.Cancellation.IsCancellationRequested, Is.False);
        Assert.That(SecondPressRequests(), Is.EqualTo(1));
    }

    /// <summary>
    /// A second Ctrl+C within five seconds of the first stops the recording without cancelling anything; a later one, after
    /// the stop, cancels the upload. The presses reach the signals through the interface, as the recorder passes on a
    /// Ctrl+C from a window.
    /// </summary>
    [Test]
    public void A_second_Ctrl_C_within_5_seconds_stops_and_a_later_one_cancels()
    {
        IUserSignals signals = _signals;
        signals.CtrlCPressed();
        _time.Advance(TimeSpan.FromSeconds(4));
        signals.CtrlCPressed();

        Assert.That(signals.StopRequested.IsCompleted, Is.True);
        Assert.That(signals.Cancellation.IsCancellationRequested, Is.False);
        Assert.That(SecondPressRequests(), Is.EqualTo(1), "the second press stops without asking again");

        _time.Advance(TimeSpan.FromSeconds(3));
        signals.CtrlCPressed();

        Assert.That(signals.Cancellation.IsCancellationRequested, Is.True);
    }

    /// <summary>A Ctrl+C more than five seconds after the first counts as a first press again; another right after it stops.</summary>
    [Test]
    public void A_Ctrl_C_more_than_5_seconds_after_the_first_counts_as_a_first_press()
    {
        _signals.CtrlCPressed();
        _time.Advance(TimeSpan.FromSeconds(6));
        _signals.CtrlCPressed();

        Assert.That(_signals.StopRequested.IsCompleted, Is.False, "still recording");
        Assert.That(SecondPressRequests(), Is.EqualTo(2));

        _time.Advance(TimeSpan.FromSeconds(1));
        _signals.CtrlCPressed();

        Assert.That(_signals.StopRequested.IsCompleted, Is.True);
        Assert.That(_signals.Cancellation.IsCancellationRequested, Is.False);
    }

    /// <summary>
    /// The five seconds between two presses are the time that has really passed, whatever Windows does to its clock: set
    /// back an hour between two presses ten seconds apart, it does not make them stop the recording, and set forward an
    /// hour between two quick ones, it does not keep them from stopping it.
    /// </summary>
    [Test]
    public void Two_Ctrl_Cs_are_timed_by_the_time_that_has_really_passed()
    {
        _signals.CtrlCPressed();
        _time.StepWallClock(TimeSpan.FromHours(-1));
        _time.Advance(TimeSpan.FromSeconds(10));
        _signals.CtrlCPressed();

        Assert.That(_signals.StopRequested.IsCompleted, Is.False, "still recording: the second press came ten seconds after the first");

        _time.StepWallClock(TimeSpan.FromHours(1));
        _signals.CtrlCPressed();

        Assert.That(_signals.StopRequested.IsCompleted, Is.True);
    }

    /// <summary>
    /// Ctrl+C held down, which repeats many times a second, stops the recording and goes no further: the presses right
    /// after the one that stopped still mean stop, and the upload goes ahead.
    /// </summary>
    [Test]
    public void A_Ctrl_C_right_after_the_one_that_stopped_does_not_cancel()
    {
        _signals.CtrlCPressed();
        _time.Advance(TimeSpan.FromMilliseconds(150));
        _signals.CtrlCPressed();
        _time.Advance(TimeSpan.FromMilliseconds(150));
        _signals.CtrlCPressed();
        _time.Advance(TimeSpan.FromMilliseconds(1500));
        _signals.CtrlCPressed();

        Assert.That(_signals.StopRequested.IsCompleted, Is.True);
        Assert.That(_signals.Cancellation.IsCancellationRequested, Is.False);
    }

    /// <summary>
    /// The two seconds after the Ctrl+C that stopped the recording are timed by the time that has really passed. A clock
    /// set back in the meantime, as a time sync may do, does not stretch them: a Ctrl+C ten seconds later still cancels.
    /// </summary>
    [Test]
    public void A_clock_set_back_after_the_stop_does_not_keep_a_later_Ctrl_C_from_cancelling()
    {
        _signals.CtrlCPressed();
        _signals.CtrlCPressed();
        _time.StepWallClock(TimeSpan.FromHours(-1));
        _time.Advance(TimeSpan.FromSeconds(10));
        _signals.CtrlCPressed();

        Assert.That(_signals.Cancellation.IsCancellationRequested, Is.True);
    }

    /// <summary>
    /// A clock set forward right after the Ctrl+C that stopped the recording does not cut the two seconds short: another
    /// Ctrl+C at once is still part of the stop, and the upload goes ahead.
    /// </summary>
    [Test]
    public void A_clock_set_forward_after_the_stop_does_not_turn_a_double_press_into_a_cancel()
    {
        _signals.CtrlCPressed();
        _signals.CtrlCPressed();
        _time.StepWallClock(TimeSpan.FromHours(1));
        _signals.CtrlCPressed();

        Assert.That(_signals.StopRequested.IsCompleted, Is.True);
        Assert.That(_signals.Cancellation.IsCancellationRequested, Is.False);
    }

    /// <summary>
    /// When the stop came from elsewhere (Q, or the transcribe command, which has nothing to stop), the first Ctrl+C
    /// already cancels, at once, without asking for a second.
    /// </summary>
    [Test]
    public void After_a_stop_from_elsewhere_the_first_Ctrl_C_cancels_at_once()
    {
        _signals.RequestStop();
        _signals.CtrlCPressed();

        Assert.That(_signals.Cancellation.IsCancellationRequested, Is.True);
        Assert.That(SecondPressRequests(), Is.Zero);
    }

    /// <summary>
    /// A Ctrl+C pressed while recording keeps its meaning when the recording stops another way before the second press,
    /// as when Q is pressed twice meanwhile: a second Ctrl+C within the five seconds completes that stop and cancels
    /// nothing, and so does one right after it. Only a press after that cancels.
    /// </summary>
    [Test]
    public void A_second_Ctrl_C_after_a_stop_from_elsewhere_still_belongs_to_the_first()
    {
        _signals.CtrlCPressed();
        _time.Advance(TimeSpan.FromSeconds(1));
        _signals.RequestStop();
        _time.Advance(TimeSpan.FromSeconds(2));
        _signals.CtrlCPressed();
        _time.Advance(TimeSpan.FromSeconds(1));
        _signals.CtrlCPressed();

        Assert.That(_signals.Cancellation.IsCancellationRequested, Is.False, "the presses belong to the stop");

        _time.Advance(TimeSpan.FromSeconds(3));
        _signals.CtrlCPressed();

        Assert.That(_signals.Cancellation.IsCancellationRequested, Is.True);
    }

    /// <summary>
    /// A Ctrl+C that comes once the run's signals are disposed, as one a window passes on to the recorder while the run
    /// ends, does nothing and does not fail: after the stop it cancels nothing, and before it, it neither asks for a second
    /// press nor counts as one.
    /// </summary>
    [TestCase(true)]
    [TestCase(false)]
    public void A_Ctrl_C_after_the_signals_are_disposed_does_nothing(bool stopped)
    {
        var cancellation = _signals.Cancellation;
        if (stopped) _signals.RequestStop();
        _signals.Dispose();

        Assert.DoesNotThrow(_signals.CtrlCPressed);
        Assert.DoesNotThrow(_signals.CtrlCPressed);

        Assert.That(cancellation.IsCancellationRequested, Is.False);
        Assert.That(_signals.StopRequested.IsCompleted, Is.EqualTo(stopped));
        Assert.That(_log.Messages, Is.Empty);
    }
}
