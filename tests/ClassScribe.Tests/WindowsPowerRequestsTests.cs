using System.Runtime.Versioning;
using ClassScribe.Desktop;
using ClassScribe.Desktop.Windows;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClassScribe.Tests;

/// <summary>
/// Keeping the laptop awake through Windows' power requests. Most tests stand in for Windows' functions and move the clock
/// by hand. The tests in the WindowsPower category make a real request and let it go at once; they change no setting.
/// </summary>
[TestFixture, Platform("Win")]
[SupportedOSPlatform("windows")]
public class WindowsPowerRequestsTests
{
    /// <summary>The reason the tests' requests are made with.</summary>
    private const string Reason = "ClassScribe is recording a class";

    /// <summary>The laptop awake and ClassScribe running, as a recording always needs.</summary>
    private const PowerNeeds Awake = PowerNeeds.System | PowerNeeds.Execution;

    /// <summary>Windows' error code for "Access is denied".</summary>
    private const int AccessDenied = 5;

    private ManualTimeProvider _time = null!;
    private FakePowerRequestApi _api = null!;
    private RecordingLogger<WindowsPowerRequests> _log = null!;
    private WindowsPowerRequests _power = null!;

    /// <summary>Creates a clock moved by hand, a stand-in for Windows' functions, a log to inspect, and the requests under test.</summary>
    [SetUp]
    public void SetUp()
    {
        _time = new ManualTimeProvider(new DateTimeOffset(2026, 10, 15, 23, 0, 0, TimeSpan.Zero));
        _api = new FakePowerRequestApi();
        _log = new RecordingLogger<WindowsPowerRequests>();
        _power = new WindowsPowerRequests(_api, _time, _log);
    }

    /// <summary>The words Windows has for an error code, as the log gives them.</summary>
    private static string WindowsWords(int error) => new System.ComponentModel.Win32Exception(error).Message.TrimEnd('.');

    /// <summary>A hold makes one request, with the reason, and sets on it what is needed: the screen only when asked for.</summary>
    [TestCase(false)]
    [TestCase(true)]
    public void A_hold_makes_one_request_with_the_reason_and_sets_what_is_needed(bool screen)
    {
        using var hold = _power.Hold(Reason, () => Awake | (screen ? PowerNeeds.Display : PowerNeeds.None));

        var request = _api.Requests.Single();
        Assert.That(request.Reason, Is.EqualTo(Reason));
        Assert.That(request.Active, Is.EquivalentTo(screen
            ? new[] { PowerRequestType.System, PowerRequestType.Execution, PowerRequestType.Display }
            : new[] { PowerRequestType.System, PowerRequestType.Execution }));
        Assert.That(_log.Problems, Is.Empty);
    }

    /// <summary>
    /// What a hold needs is read again every 30 seconds, so the screen follows the power source: kept on once the laptop
    /// runs on battery, and let go once it is plugged in again.
    /// </summary>
    [Test]
    public void What_is_needed_is_read_again_every_30_seconds()
    {
        var onBattery = false;
        using var hold = _power.Hold(Reason, () => Awake | (onBattery ? PowerNeeds.Display : PowerNeeds.None));
        var request = _api.Requests.Single();

        onBattery = true;
        _time.Advance(TimeSpan.FromSeconds(29));
        Assert.That(request.Active, Does.Not.Contain(PowerRequestType.Display), "not before 30 seconds");
        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.That(request.Active, Does.Contain(PowerRequestType.Display));

        onBattery = false;
        _time.Advance(TimeSpan.FromSeconds(30));
        Assert.That(request.Active, Is.EquivalentTo(new[] { PowerRequestType.System, PowerRequestType.Execution }));
        Assert.That(_api.Requests, Has.Count.EqualTo(1), "the same request throughout");
    }

    /// <summary>
    /// Letting a hold go clears every request set on it and closes it, which ends the request for Windows. Nothing is read
    /// or set after that, and letting it go again does nothing.
    /// </summary>
    [Test]
    public void Letting_go_clears_every_request_and_closes_it()
    {
        var reads = 0;
        var hold = _power.Hold(Reason, () =>
        {
            reads++;
            return Awake | PowerNeeds.Display;
        });
        var request = _api.Requests.Single();

        hold.Dispose();
        var readsAtRelease = reads;
        _time.Advance(TimeSpan.FromMinutes(5));
        hold.Dispose();

        Assert.That(request.Calls, Is.EqualTo(new[]
        {
            "Set System", "Set Execution", "Set Display",
            "Clear Display", "Clear Execution", "Clear System", "Close"
        }));
        Assert.That(request.IsClosed, Is.True);
        Assert.That(reads, Is.EqualTo(readsAtRelease), "not read again after the hold was let go");
        Assert.That(_time.ActiveTimers, Is.Zero, "no check is left waiting");
    }

    /// <summary>
    /// A request Windows refuses to make is logged with what to do instead, and nothing is thrown: the recording goes on
    /// without it. Nothing is checked again.
    /// </summary>
    [Test]
    public void A_request_Windows_refuses_to_make_is_logged_with_advice()
    {
        _api.RefuseToCreate(AccessDenied);

        var hold = _power.Hold(Reason, () => Awake);

        Assert.DoesNotThrow(hold.Dispose);
        Assert.That(_log.Problems.Single(), Is.EqualTo(
            $"Windows did not accept the request to keep the laptop awake ({WindowsWords(AccessDenied)}, error {AccessDenied}). " +
            "Recording goes on, but the laptop may go to sleep if it is left alone: keep it plugged in, with sleep set to Never in Settings > System > Power & battery"));
        Assert.That(_api.Requests.Single().Calls, Is.Empty, "nothing set on a request that was not made");
        Assert.That(_time.ActiveTimers, Is.Zero);
    }

    /// <summary>
    /// A kind of request Windows refuses to set, here the screen's, is logged once with advice, not at every check, and is
    /// tried again at each check, so it is set once Windows accepts it. The others are set all the same.
    /// </summary>
    [Test]
    public void A_kind_of_request_Windows_refuses_is_logged_once_and_tried_again()
    {
        _api.RefuseToSet(PowerRequestType.Display, AccessDenied);
        using var hold = _power.Hold(Reason, () => Awake | PowerNeeds.Display);
        var request = _api.Requests.Single();

        _time.Advance(TimeSpan.FromSeconds(30));
        _time.Advance(TimeSpan.FromSeconds(30));
        Assert.That(request.Active, Is.EquivalentTo(new[] { PowerRequestType.System, PowerRequestType.Execution }));
        Assert.That(request.Calls.Count(call => call == "Refused Display"), Is.EqualTo(3), "tried at the start and at each check");

        _api.RefuseToSet(PowerRequestType.Display, 0);
        _time.Advance(TimeSpan.FromSeconds(30));

        Assert.That(request.Active, Does.Contain(PowerRequestType.Display));
        Assert.That(_log.Problems.Single(), Is.EqualTo(
            $"Windows did not accept the request to keep the screen on ({WindowsWords(AccessDenied)}, error {AccessDenied}). " +
            "Recording goes on, but on battery the laptop may go into standby once the screen turns off: keep it plugged in, with sleep set to Never in Settings > System > Power & battery"));
    }

    /// <summary>
    /// A refusal to keep the laptop awake, here of both the system and the execution request, is logged once with advice:
    /// not once per kind, and not at every check. Each kind is tried again at each check, so it is set once Windows accepts
    /// it, and the screen's request is set all the same.
    /// </summary>
    [Test]
    public void A_refusal_to_keep_the_laptop_awake_is_logged_once_and_tried_again()
    {
        _api.RefuseToSet(PowerRequestType.System, AccessDenied);
        _api.RefuseToSet(PowerRequestType.Execution, AccessDenied);
        using var hold = _power.Hold(Reason, () => Awake | PowerNeeds.Display);
        var request = _api.Requests.Single();

        _time.Advance(TimeSpan.FromSeconds(30));
        _time.Advance(TimeSpan.FromSeconds(30));
        Assert.That(request.Active, Is.EquivalentTo(new[] { PowerRequestType.Display }), "the screen's request is set all the same");
        Assert.That(request.Calls.Count(call => call == "Refused System"), Is.EqualTo(3), "tried at the start and at each check");
        Assert.That(request.Calls.Count(call => call == "Refused Execution"), Is.EqualTo(3), "tried at the start and at each check");

        _api.RefuseToSet(PowerRequestType.System, 0);
        _time.Advance(TimeSpan.FromSeconds(30));

        Assert.That(request.Active, Is.EquivalentTo(new[] { PowerRequestType.System, PowerRequestType.Display }), "set at the next check once Windows accepts it");
        Assert.That(_log.Problems, Is.EqualTo(new[]
        {
            $"Windows did not accept the request to keep the laptop awake ({WindowsWords(AccessDenied)}, error {AccessDenied}). " +
            "Recording goes on, but the laptop may go to sleep if it is left alone: keep it plugged in, with sleep set to Never in Settings > System > Power & battery"
        }));
    }

    /// <summary>
    /// A check that fails, here because what is needed could not be read, is logged and goes no further: it runs on a timer,
    /// where an exception would end the process. The requests stay as they were, and the next check runs as usual.
    /// </summary>
    [Test]
    public void A_check_that_fails_is_logged_and_the_requests_stay_as_they_were()
    {
        var fail = false;
        var screen = true;
        using var hold = _power.Hold(Reason, () => fail
            ? throw new InvalidOperationException("The power source could not be read.")
            : Awake | (screen ? PowerNeeds.Display : PowerNeeds.None));
        var request = _api.Requests.Single();

        fail = true;
        Assert.DoesNotThrow(() => _time.Advance(TimeSpan.FromSeconds(30)));
        Assert.That(request.Active, Is.EquivalentTo(new[] { PowerRequestType.System, PowerRequestType.Execution, PowerRequestType.Display }));
        Assert.That(_log.Problems.Single(), Does.StartWith("Could not check again whether to keep the screen on; the requests stay as they are"));

        fail = false;
        screen = false;
        _time.Advance(TimeSpan.FromSeconds(30));
        Assert.That(request.Active, Does.Not.Contain(PowerRequestType.Display), "the next check runs");
    }

    /// <summary>
    /// A mistake in what is needed shows at once, when the hold is taken, rather than later on a timer; the request made
    /// for it is closed again and no check is left waiting.
    /// </summary>
    [Test]
    public void A_mistake_in_what_is_needed_shows_when_the_hold_is_taken()
    {
        Assert.Throws<InvalidOperationException>(() => _power.Hold(Reason, () => throw new InvalidOperationException("A mistake.")));

        Assert.That(_api.Requests.Single().IsClosed, Is.True);
        Assert.That(_time.ActiveTimers, Is.Zero);
    }

    /// <summary>Windows makes a real request, sets it and clears it; it is let go at once.</summary>
    [Test, Category("WindowsPower")]
    public void Windows_makes_sets_and_clears_a_real_request()
    {
        var api = NativePowerRequestApi.Instance;
        using var request = api.Create("ClassScribe test, let go at once", out var error);
        var set = request.IsInvalid ? -1 : api.Set(request, PowerRequestType.System);
        var clear = request.IsInvalid ? -1 : api.Clear(request, PowerRequestType.System);

        Assert.That(error, Is.Zero);
        Assert.That(request.IsInvalid, Is.False);
        Assert.That(set, Is.Zero);
        Assert.That(clear, Is.Zero);
    }

    /// <summary>A real hold, taken and let go at once, gives no problem: Windows accepted every request.</summary>
    [Test, Category("WindowsPower")]
    public void A_real_hold_is_taken_and_let_go_without_a_problem()
    {
        var power = new WindowsPowerRequests(TimeProvider.System, _log);

        using (power.Hold("ClassScribe test, let go at once", () => Awake))
        {
        }

        Assert.That(_log.Problems, Is.Empty);
    }

    /// <summary>Windows says where the laptop takes its power from, whichever it is now.</summary>
    [Test, Category("WindowsPower")]
    public void The_power_source_is_read_from_Windows()
    {
        var source = new WindowsPowerSource(NullLogger<WindowsPowerSource>.Instance);

        Assert.DoesNotThrow(() => _ = source.OnBattery);
    }

    /// <summary>
    /// Windows' answer counts as battery unless it says the laptop is on mains (1) or has no battery (flag 128): offline
    /// (0), and a state Windows does not know (255 for either), keep the screen on, the safer side for a recording.
    /// </summary>
    [TestCase(1, 0, false)]
    [TestCase(1, 8, false)]
    [TestCase(0, 1, true)]
    [TestCase(255, 2, true)]
    [TestCase(255, 128, false)]
    [TestCase(255, 255, true)]
    public void Windows_answer_counts_as_battery_unless_on_mains_or_without_a_battery(int acLineStatus, int batteryFlag, bool onBattery)
    {
        Assert.That(WindowsPowerSource.IsOnBattery((byte)acLineStatus, (byte)batteryFlag), Is.EqualTo(onBattery));
    }
}
