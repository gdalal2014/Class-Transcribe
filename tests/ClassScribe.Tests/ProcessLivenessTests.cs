using System.Diagnostics;
using System.Runtime.Versioning;
using ClassScribe.Recorder;

namespace ClassScribe.Tests;

/// <summary>
/// Whether the process a run file names still runs, asked of Windows: the process with that id must run and have started
/// within 2 seconds of the start time written down, so that an id Windows gave to a new process does not keep a run alive.
/// And when the computer last started. The tests look at this test's own process and at one cmd.exe, without a window,
/// that ends by itself at once.
/// </summary>
[TestFixture, Platform("Win"), Category("WindowsProcess")]
[SupportedOSPlatform("windows")]
public class ProcessLivenessTests
{
    private RecordingLogger<ProcessLiveness> _log = null!;
    private ProcessLiveness _liveness = null!;

    /// <summary>Makes the check under test, over the computer's own clock.</summary>
    [SetUp]
    public void SetUp()
    {
        _log = new RecordingLogger<ProcessLiveness>();
        _liveness = new ProcessLiveness(TimeProvider.System, _log);
    }

    /// <summary>When <paramref name="process"/> started, in UTC, as ClassScribe writes it into a run file.</summary>
    private static DateTimeOffset StartOf(Process process) => new(process.StartTime.ToUniversalTime());

    /// <summary>This test's own process runs, with its own start time.</summary>
    [Test]
    public void This_process_is_alive_with_its_own_start_time()
    {
        using var me = Process.GetCurrentProcess();

        Assert.That(_liveness.IsAlive(me.Id, StartOf(me)), Is.True);
    }

    /// <summary>A start time written down within 2 seconds of the process's own, either way, is the same process.</summary>
    [TestCase(-1.9)]
    [TestCase(1.9)]
    public void A_start_time_within_two_seconds_is_the_same_process(double offSeconds)
    {
        using var me = Process.GetCurrentProcess();

        Assert.That(_liveness.IsAlive(me.Id, StartOf(me).AddSeconds(offSeconds)), Is.True);
    }

    /// <summary>
    /// A start time further off is another process, one Windows gave the same id to after the one written down had ended,
    /// so the run whose file names it is not alive.
    /// </summary>
    [TestCase(-2.5)]
    [TestCase(2.5)]
    [TestCase(-3600)]
    public void A_start_time_further_off_is_another_process_given_the_same_id(double offSeconds)
    {
        using var me = Process.GetCurrentProcess();

        Assert.That(_liveness.IsAlive(me.Id, StartOf(me).AddSeconds(offSeconds)), Is.False);
    }

    /// <summary>
    /// A process that has ended is not alive, with its own start time: while its handle is still held, which keeps its id
    /// taken, and once nothing holds it any more.
    /// </summary>
    [Test]
    public void A_process_that_has_ended_is_not_alive()
    {
        var start = new ProcessStartInfo("cmd.exe", "/d /c exit 0") { UseShellExecute = false, CreateNoWindow = true };
        int id;
        DateTimeOffset started;
        bool whileHeld;

        using (var child = Process.Start(start)!)
        {
            id = child.Id;
            started = StartOf(child);
            child.WaitForExit();
            whileHeld = _liveness.IsAlive(id, started);
        }

        var afterwards = _liveness.IsAlive(id, started);

        Assert.That(whileHeld, Is.False);
        Assert.That(afterwards, Is.False);
    }

    /// <summary>An id no process can have is not alive, and asking is no problem.</summary>
    [TestCase(0)]
    [TestCase(-1)]
    public void An_id_no_process_can_have_is_not_alive(int id)
    {
        Assert.That(_liveness.IsAlive(id, DateTimeOffset.UtcNow), Is.False);
        Assert.That(_log.Problems, Is.Empty);
    }

    /// <summary>
    /// The computer last started as long ago as Windows has counted since it started, time asleep included, give or take a
    /// second: so before this test's process did, and within the last year, as Windows' updates see to. The count is read
    /// before and after the call, so the check holds however long the tests have run.
    /// </summary>
    [Test]
    public void The_computer_last_started_as_long_ago_as_Windows_has_counted_since()
    {
        using var me = Process.GetCurrentProcess();
        var leeway = TimeSpan.FromSeconds(1);
        var upBefore = TimeSpan.FromMilliseconds(Environment.TickCount64);
        var nowBefore = DateTimeOffset.UtcNow;

        var boot = _liveness.LastBootUtc();

        var nowAfter = DateTimeOffset.UtcNow;
        var upAfter = TimeSpan.FromMilliseconds(Environment.TickCount64);
        Assert.That(boot, Is.InRange(nowBefore - upAfter - leeway, nowAfter - upBefore + leeway));
        Assert.That(boot, Is.LessThanOrEqualTo(StartOf(me).AddSeconds(1)));
        Assert.That(boot, Is.GreaterThan(DateTimeOffset.UtcNow.AddYears(-1)));
    }
}

/// <summary>The rule that two readings of a process's start time are the same process, which needs no Windows.</summary>
[TestFixture]
public class ProcessStartTimeTests
{
    /// <summary>Two readings of a start time within 2 seconds of each other, either way, are the same process; further apart, they are two.</summary>
    [TestCase(0, true)]
    [TestCase(2, true)]
    [TestCase(-2, true)]
    [TestCase(2.001, false)]
    [TestCase(-3, false)]
    [TestCase(86400, false)]
    public void Two_readings_of_a_start_time_within_two_seconds_are_the_same_process(double apartSeconds, bool same)
    {
        var started = new DateTimeOffset(2026, 10, 15, 22, 59, 1, TimeSpan.Zero);

        Assert.That(IProcessLiveness.StartTimeTolerance, Is.EqualTo(TimeSpan.FromSeconds(2)));
        Assert.That(IProcessLiveness.StartedTogether(started.AddSeconds(apartSeconds), started), Is.EqualTo(same));
    }
}
