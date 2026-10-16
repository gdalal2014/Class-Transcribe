using ClassScribe.Desktop;
using ClassScribe.Recorder;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ClassScribe.Tests;

/// <summary>
/// The freeze watch, ticked as the recording loop ticks it and by a timer of its own, on a clock moved by hand: ticks a
/// second apart are normal, a jump of the steady clock is a time ClassScribe did not run, and the interrupt times tell a
/// sleep from a hold-up.
/// </summary>
[TestFixture]
public class FreezeWatchTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 15, 23, 42, 10, TimeSpan.Zero);

    private ManualTimeProvider _time = null!;
    private FakeSystemTimes _times = null!;
    private RecordingLogger<FreezeWatch> _log = null!;
    private FreezeWatch _watch = null!;

    /// <summary>Creates a clock moved by hand, the computer's clocks read from it, a log to inspect, and the watch.</summary>
    [SetUp]
    public void SetUp()
    {
        _time = new ManualTimeProvider(Start);
        _times = new FakeSystemTimes(_time);
        _log = new RecordingLogger<FreezeWatch>();
        _watch = new FreezeWatch(_times, _time, _log);
    }

    /// <summary>Ticks the watch <paramref name="count"/> times a second apart, as the recording loop does, and returns what each tick handed on.</summary>
    private List<IReadOnlyList<Freeze>> TickEverySecond(int count)
    {
        List<IReadOnlyList<Freeze>> found = [];
        for (var tick = 0; tick < count; tick++)
        {
            _time.Advance(TimeSpan.FromSeconds(1));
            found.Add(_watch.Tick());
        }

        return found;
    }

    /// <summary>The first tick only starts the watch: there is no tick before it to compare with.</summary>
    [Test]
    public void The_first_tick_only_starts_the_watch()
    {
        _time.Advance(TimeSpan.FromMinutes(5));

        Assert.That(_watch.Tick(), Is.Empty);
    }

    /// <summary>Ticks a second apart, as the recording loop makes them for a whole class, find nothing.</summary>
    [Test]
    public void Ticks_a_second_apart_find_nothing()
    {
        _watch.Tick();

        Assert.That(TickEverySecond(3600), Is.All.Empty);
    }

    /// <summary>
    /// 18 seconds between two ticks while the laptop stays awake is ClassScribe held up, from the tick before to the tick
    /// after. The stretch to leave out of a track keeps a second and a half at each edge, for the audio Windows held there.
    /// </summary>
    [Test]
    public void Eighteen_seconds_while_the_laptop_stays_awake_is_a_hold_up()
    {
        _watch.Tick();
        TickEverySecond(1);

        _time.Advance(TimeSpan.FromSeconds(18));
        var found = _watch.Tick();

        Assert.That(found, Is.EqualTo(new[]
        {
            new Freeze(Start.AddSeconds(1), Start.AddSeconds(19), TimeSpan.FromSeconds(2.5).Ticks, TimeSpan.FromSeconds(17.5).Ticks, Slept: false)
        }));
    }

    /// <summary>18 seconds of sleep between two ticks is the laptop asleep: the unbiased interrupt time left it out.</summary>
    [Test]
    public void Eighteen_seconds_of_sleep_is_a_sleep()
    {
        _watch.Tick();

        _times.Sleep(TimeSpan.FromSeconds(18));
        var found = _watch.Tick();

        Assert.That(found, Has.Count.EqualTo(1));
        Assert.That(found[0].Slept, Is.True);
        Assert.That((found[0].FromUtc, found[0].ToUtc), Is.EqualTo((Start, Start.AddSeconds(18))));
    }

    /// <summary>
    /// A freeze is a jump of 3 seconds or more beyond the one-second tick: 3.9 seconds between two ticks is a slow tick, 4
    /// seconds a freeze.
    /// </summary>
    [TestCase(3.9, false)]
    [TestCase(4.0, true)]
    public void A_freeze_is_a_jump_of_three_seconds_or_more_beyond_the_tick(double seconds, bool freeze)
    {
        _watch.Tick();

        _time.Advance(TimeSpan.FromSeconds(seconds));

        Assert.That(_watch.Tick(), Has.Count.EqualTo(freeze ? 1 : 0));
    }

    /// <summary>A freeze is found once: the ticks after it, a second apart again, find nothing more.</summary>
    [Test]
    public void A_freeze_is_found_once()
    {
        _watch.Tick();
        _time.Advance(TimeSpan.FromSeconds(18));
        Assert.That(_watch.Tick(), Has.Count.EqualTo(1));

        Assert.That(TickEverySecond(10), Is.All.Empty);
    }

    /// <summary>Windows setting its time of day back or forward an hour between two ticks is not a freeze: only the steady clock counts.</summary>
    [TestCase(-60)]
    [TestCase(60)]
    public void A_change_of_the_time_of_day_is_not_a_freeze(int minutes)
    {
        _watch.Tick();

        _time.StepWallClock(TimeSpan.FromMinutes(minutes));
        _time.Advance(TimeSpan.FromSeconds(1));

        Assert.That(_watch.Tick(), Is.Empty);
    }

    /// <summary>
    /// A freeze counts as a sleep when the unbiased interrupt time left out 2 seconds or more of it: 2 seconds of sleep in
    /// a 10-second freeze is a sleep, 1.9 seconds a hold-up.
    /// </summary>
    [TestCase(1.9, false)]
    [TestCase(2.0, true)]
    public void A_freeze_counts_as_a_sleep_from_two_seconds_of_sleep(double asleep, bool slept)
    {
        _watch.Tick();

        _times.Sleep(TimeSpan.FromSeconds(asleep));
        _time.Advance(TimeSpan.FromSeconds(10 - asleep));
        var found = _watch.Tick();

        Assert.That(found, Has.Count.EqualTo(1));
        Assert.That(found[0].Slept, Is.EqualTo(slept));
    }

    /// <summary>
    /// While the watch ticks on its own timer, a caller that was held up by its own work, here for 6 seconds, is not taken
    /// for a time ClassScribe did not run: its tick compares with the timer's last one, a second before at most.
    /// </summary>
    [Test]
    public void A_caller_held_up_while_the_timer_ticks_is_no_freeze()
    {
        using var everySecond = _watch.Start();
        _watch.Tick();

        for (var second = 0; second < 6; second++) _time.Advance(TimeSpan.FromSeconds(1));

        Assert.That(_watch.Tick(), Is.Empty);
    }

    /// <summary>
    /// A freeze the timer finds first, as when it runs before the caller on waking, is kept for the caller's next tick,
    /// which hands it on once, with the times the timer read.
    /// </summary>
    [Test]
    public void A_freeze_the_timer_finds_is_handed_on_once_by_the_next_tick()
    {
        using var everySecond = _watch.Start();
        _time.Advance(TimeSpan.FromSeconds(1));

        _times.Sleep(TimeSpan.FromSeconds(18));
        var handedOn = _watch.Tick();

        Assert.That(handedOn, Has.Count.EqualTo(1));
        Assert.That((handedOn[0].FromUtc, handedOn[0].ToUtc, handedOn[0].Slept), Is.EqualTo((Start.AddSeconds(1), Start.AddSeconds(19), true)));
        Assert.That(_watch.Tick(), Is.Empty, "handed on once");
        Assert.That(TickEverySecond(5), Is.All.Empty);
    }

    /// <summary>Freezes the timer finds while the caller is held up are all handed on, oldest first, at the caller's next tick.</summary>
    [Test]
    public void Freezes_the_timer_finds_while_the_caller_is_held_up_are_all_handed_on()
    {
        using var everySecond = _watch.Start();
        _time.Advance(TimeSpan.FromSeconds(1));

        _times.Sleep(TimeSpan.FromSeconds(10));
        _time.Advance(TimeSpan.FromSeconds(1));
        _time.Advance(TimeSpan.FromSeconds(20));
        var handedOn = _watch.Tick();

        Assert.That(handedOn.Select(freeze => (freeze.FromUtc, freeze.Slept)), Is.EqualTo(new[] { (Start.AddSeconds(1), true), (Start.AddSeconds(12), false) }));
    }

    /// <summary>The timer stops when it is let go, so that a finished recording leaves no timer behind.</summary>
    [Test]
    public void The_timer_stops_when_let_go()
    {
        var everySecond = _watch.Start();
        Assert.That(_time.ActiveTimers, Is.EqualTo(1));

        everySecond.Dispose();

        Assert.That(_time.ActiveTimers, Is.Zero);
    }

    /// <summary>
    /// A clock that cannot be read on the timer is logged, once rather than every second, and nothing leaves the timer,
    /// where it would end the process and the recording with it: the timer goes on, and so do the caller's ticks.
    /// </summary>
    [Test]
    public void A_clock_that_cannot_be_read_on_the_timer_is_logged_once_and_the_watch_goes_on()
    {
        var reads = 0;
        var times = new Mock<ISystemTimes>();
        times.Setup(t => t.TimestampFrequency).Returns(_time.TimestampFrequency);
        times.Setup(t => t.Timestamp()).Returns(() => ++reads is 2 or 3 ? throw new InvalidOperationException("The clock cannot be read.") : _time.GetTimestamp());
        times.Setup(t => t.UtcNow()).Returns(_time.GetUtcNow);
        var watch = new FreezeWatch(times.Object, _time, _log);
        using var everySecond = watch.Start();

        for (var second = 0; second < 3; second++)
            Assert.DoesNotThrow(() => _time.Advance(TimeSpan.FromSeconds(1)));

        Assert.That(_log.Problems, Is.EqualTo(new[] { "Could not read the computer's clocks to watch for a time ClassScribe did not run" }));
        Assert.That(watch.Tick(), Is.Empty);
    }

    /// <summary>A watch made by the app's services, as a run's scope makes it, has no timer running until it is started.</summary>
    [Test]
    public void A_watch_made_without_a_start_runs_no_timer()
    {
        _ = new FreezeWatch(_times, _time, NullLogger<FreezeWatch>.Instance);

        Assert.That(_time.ActiveTimers, Is.Zero);
    }
}
