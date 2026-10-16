using ClassScribe.Audio.Windows;

namespace ClassScribe.Tests;

/// <summary>How the capture loop notices that it was held up long enough to lose audio.</summary>
[TestFixture]
public class CaptureGapWatchTests
{
    private static readonly TimeSpan Buffer = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ReportEvery = TimeSpan.FromSeconds(30);

    private ManualTimeProvider _time = null!;
    private CaptureGapWatch _watch = null!;

    /// <summary>Creates a watch over a one-second buffer on a clock the test moves by hand, and has the loop start by emptying the buffer.</summary>
    [SetUp]
    public void SetUp()
    {
        _time = new ManualTimeProvider(new DateTimeOffset(2026, 10, 15, 19, 0, 0, TimeSpan.Zero));
        _watch = new CaptureGapWatch(Buffer, ReportEvery, _time);
        _watch.Drained(TimeSpan.Zero);
    }

    /// <summary>Drains that follow each other closely enough are no hold-up, however long the recording runs.</summary>
    [Test]
    public void Regular_drains_are_no_hold_up()
    {
        var reports = new List<CaptureGap>();
        for (var poll = 0; poll < 1000; poll++)
        {
            _time.Advance(TimeSpan.FromMilliseconds(60));
            if (_watch.Drained(TimeSpan.FromMilliseconds(5)) is { } gap) reports.Add(gap);
        }

        Assert.That(reports, Is.Empty);
    }

    /// <summary>A loop that did not run for as long as Windows keeps audio waiting (a computer too busy to schedule it) is a hold-up, though storing took no time.</summary>
    [Test]
    public void A_loop_that_did_not_run_for_the_length_of_the_buffer_is_a_hold_up()
    {
        _time.Advance(TimeSpan.FromMilliseconds(1100));

        var gap = _watch.Drained(TimeSpan.FromMilliseconds(5));

        Assert.That(gap, Is.EqualTo(new CaptureGap(1, TimeSpan.FromMilliseconds(1100), TimeSpan.FromMilliseconds(5))));
    }

    /// <summary>The wait before a drain counts as well as the storing in it: 970 ms of storing after a 50 ms wait is a hold-up, though neither alone is.</summary>
    [Test]
    public void The_wait_and_the_storing_add_up()
    {
        _time.Advance(TimeSpan.FromMilliseconds(1020));

        var gap = _watch.Drained(TimeSpan.FromMilliseconds(970));

        Assert.That(gap, Is.EqualTo(new CaptureGap(1, TimeSpan.FromMilliseconds(1020), TimeSpan.FromMilliseconds(970))));
    }

    /// <summary>Hold-ups are reported at most once per interval; those in between come with the next report, counted, with the longest of them.</summary>
    [Test]
    public void Reports_come_at_most_once_per_interval_and_count_what_came_between()
    {
        var reports = new List<(TimeSpan At, CaptureGap Gap)>();

        // One pass of the capture loop: wait, then drain the buffer, keeping any hold-up reported and when it came.
        void Drain(TimeSpan after, TimeSpan storing)
        {
            _time.Advance(after);
            if (_watch.Drained(storing) is { } gap) reports.Add((TimeSpan.FromTicks(_time.GetTimestamp()), gap));
        }

        Drain(TimeSpan.FromSeconds(2), TimeSpan.Zero);                          // reported at once
        Drain(TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(2));                // held back: too soon after the first report
        Drain(TimeSpan.FromSeconds(1.5), TimeSpan.Zero);                        // held back as well
        while (_time.GetTimestamp() < TimeSpan.FromSeconds(40).Ticks)
            Drain(TimeSpan.FromMilliseconds(50), TimeSpan.Zero);                // nothing more goes wrong

        Assert.That(reports, Has.Count.EqualTo(2));
        Assert.That(reports[0].Gap, Is.EqualTo(new CaptureGap(1, TimeSpan.FromSeconds(2), TimeSpan.Zero)));
        Assert.That(reports[1].Gap, Is.EqualTo(new CaptureGap(2, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(2))));
        Assert.That(reports[1].At, Is.EqualTo(TimeSpan.FromSeconds(32)).Within(TimeSpan.FromMilliseconds(50)), "thirty seconds after the first report");
    }
}
