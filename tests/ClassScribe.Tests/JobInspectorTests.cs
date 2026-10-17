using System.Runtime.Versioning;
using ClassScribe.Desktop;
using ClassScribe.Desktop.Windows;
using Win32Exception = System.ComponentModel.Win32Exception;

namespace ClassScribe.Tests;

/// <summary>
/// Reading the job ClassScribe runs in. Most tests stand in for Windows' job functions. The test in the WindowsProcess
/// category asks Windows about the test's own process; it changes nothing and starts nothing.
/// </summary>
[TestFixture, Platform("Win")]
[SupportedOSPlatform("windows")]
public class JobInspectorTests
{
    /// <summary>Windows' error code for "Access is denied".</summary>
    private const int AccessDenied = 5;

    /// <summary>Windows' error code for "The parameter is incorrect".</summary>
    private const int InvalidParameter = 87;

    private FakeJobApi _api = null!;
    private RecordingLogger<JobInspector> _log = null!;
    private JobInspector _inspector = null!;

    /// <summary>Creates a stand-in for Windows' job functions, a log to inspect, and the inspector under test.</summary>
    [SetUp]
    public void SetUp()
    {
        _api = new FakeJobApi();
        _log = new RecordingLogger<JobInspector>();
        _inspector = new JobInspector(_api, _log);
    }

    /// <summary>The words Windows has for an error code.</summary>
    private static string WindowsWords(int error) => new Win32Exception(error).Message.TrimEnd('.');

    /// <summary>Outside a job the facts say so, and no job's limits are asked for, since there is no job to ask about.</summary>
    [Test]
    public void Outside_a_job_the_facts_say_so_and_no_limits_are_asked_for()
    {
        Assert.That(_inspector.Inspect(), Is.EqualTo(JobFacts.NoJob));

        Assert.That(_api.LimitReads, Is.Zero);
        Assert.That(_log.Problems, Is.Empty);
    }

    /// <summary>
    /// In a job, its limits become the facts: dotnet run's job (0x2000) kills on close and lets nothing out; the job the
    /// shell of a coding agent ran in here (0x1800) lets processes out, silently too, and does not kill on close.
    /// </summary>
    [TestCase(0x2000u, true, false, false)]
    [TestCase(0x2800u, true, true, false)]
    [TestCase(0x3000u, true, false, true)]
    [TestCase(0x1800u, false, true, true)]
    [TestCase(0x0000u, false, false, false)]
    public void In_a_job_its_limits_become_the_facts(uint limits, bool killOnClose, bool breakawayAllowed, bool silentBreakaway)
    {
        _api.InJob = true;
        _api.LimitFlags = limits;

        Assert.That(_inspector.Inspect(), Is.EqualTo(new JobFacts(InJob: true, killOnClose, breakawayAllowed, silentBreakaway)));
        Assert.That(_api.LimitReads, Is.EqualTo(1));
        Assert.That(_log.Problems, Is.Empty);
    }

    /// <summary>The job's other limits, such as a limit on its processes or its memory, are left out.</summary>
    [Test]
    public void The_jobs_other_limits_are_left_out()
    {
        _api.InJob = true;
        _api.LimitFlags = JobFacts.KillOnJobCloseLimit | 0x8 | 0x100 | 0x200 | 0x400 | 0x4000;

        Assert.That(_inspector.Inspect(), Is.EqualTo(FakeJobInspector.DotnetRun));
    }

    /// <summary>Each inspection asks Windows again, so that a job ClassScribe was put in later counts.</summary>
    [Test]
    public void Each_inspection_asks_Windows_again()
    {
        Assert.That(_inspector.Inspect(), Is.EqualTo(JobFacts.NoJob));

        _api.InJob = true;
        _api.LimitFlags = JobFacts.KillOnJobCloseLimit;

        Assert.That(_inspector.Inspect(), Is.EqualTo(FakeJobInspector.DotnetRun));
    }

    /// <summary>The facts read are logged, for the record of how a recording's place was chosen.</summary>
    [Test]
    public void The_facts_read_are_logged()
    {
        _api.InJob = true;
        _api.LimitFlags = 0x2800;

        _inspector.Inspect();

        Assert.That(_log.Messages, Has.Some.Matches<string>(message => message.Contains("0x2800")));
    }

    /// <summary>
    /// When Windows does not say whether ClassScribe runs in a job, the facts are the cautious ones, as under dotnet run,
    /// so that a recording runs where closing its window is said to end it; and Windows' reason is logged as a warning.
    /// </summary>
    [Test]
    public void When_Windows_does_not_say_whether_ClassScribe_runs_in_a_job_the_cautious_facts_are_taken()
    {
        _api.InJob = true;
        _api.InJobError = AccessDenied;

        var facts = _inspector.Inspect();

        Assert.That(facts, Is.EqualTo(JobFacts.Unknown));
        Assert.That(facts.RecorderOutlivesWindow(facts.Breakaway), Is.False);
        Assert.That(_api.LimitReads, Is.Zero);
        Assert.That(_log.Problems, Has.Count.EqualTo(1));
        Assert.That(_log.Problems[0], Does.Contain(WindowsWords(AccessDenied)).And.Contain("error 5"));
    }

    /// <summary>When Windows does not give the job's limits, the facts are the cautious ones too, and the reason is logged as a warning.</summary>
    [Test]
    public void When_Windows_does_not_give_the_jobs_limits_the_cautious_facts_are_taken()
    {
        _api.InJob = true;
        _api.LimitFlags = 0x1800;
        _api.LimitsError = InvalidParameter;

        var facts = _inspector.Inspect();

        Assert.That(facts, Is.EqualTo(JobFacts.Unknown));
        Assert.That(_log.Problems, Has.Count.EqualTo(1));
        Assert.That(_log.Problems[0], Does.Contain(WindowsWords(InvalidParameter)).And.Contain("error 87"));
    }

    /// <summary>
    /// Windows answers both questions for the test's own process, whatever job it runs in: the facts are read without an
    /// exception and without a warning, and outside a job they are those of no job.
    /// </summary>
    [Test, Category("WindowsProcess")]
    public void The_facts_for_this_process_are_read_from_Windows_without_an_exception()
    {
        var log = new RecordingLogger<JobInspector>();
        JobFacts facts = null!;

        Assert.DoesNotThrow(() => facts = new JobInspector(log).Inspect());

        Assert.That(log.Problems, Is.Empty);
        if (!facts.InJob) Assert.That(facts, Is.EqualTo(JobFacts.NoJob));
    }
}

/// <summary>What the job facts decide: whether to start the recorder outside the job, and whether it can outlive the window that starts it.</summary>
[TestFixture]
public class JobFactsTests
{
    /// <summary>The limits have the values Windows documents (winnt.h).</summary>
    [Test]
    public void The_limits_have_the_values_Windows_documents()
    {
        Assert.That(JobFacts.BreakawayOkLimit, Is.EqualTo(0x800u), "JOB_OBJECT_LIMIT_BREAKAWAY_OK");
        Assert.That(JobFacts.SilentBreakawayOkLimit, Is.EqualTo(0x1000u), "JOB_OBJECT_LIMIT_SILENT_BREAKAWAY_OK");
        Assert.That(JobFacts.KillOnJobCloseLimit, Is.EqualTo(0x2000u), "JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE");
    }

    /// <summary>A job's limits become the facts of being in that job.</summary>
    [Test]
    public void A_jobs_limits_become_the_facts_of_being_in_it()
    {
        Assert.That(JobFacts.FromLimitFlags(0x2000), Is.EqualTo(new JobFacts(InJob: true, KillOnClose: true, BreakawayAllowed: false, SilentBreakaway: false)));
        Assert.That(JobFacts.FromLimitFlags(0x1800), Is.EqualTo(new JobFacts(InJob: true, KillOnClose: false, BreakawayAllowed: true, SilentBreakaway: true)));
        Assert.That(JobFacts.FromLimitFlags(0), Is.EqualTo(new JobFacts(InJob: true, KillOnClose: false, BreakawayAllowed: false, SilentBreakaway: false)));
    }

    /// <summary>
    /// The recorder is started outside the job only in a job that allows that: Windows refuses breakaway in any other job,
    /// silent breakaway alone included, and outside a job it means nothing.
    /// </summary>
    [TestCase(false, 0x0000u, false)]
    [TestCase(true, 0x2000u, false)]
    [TestCase(true, 0x2800u, true)]
    [TestCase(true, 0x3000u, false)]
    [TestCase(true, 0x3800u, true)]
    [TestCase(true, 0x1800u, true)]
    [TestCase(true, 0x0800u, true)]
    [TestCase(true, 0x0000u, false)]
    public void Breakaway_is_asked_for_only_in_a_job_that_allows_it(bool inJob, uint limits, bool breakaway)
    {
        var facts = inJob ? JobFacts.FromLimitFlags(limits) : JobFacts.NoJob;

        Assert.That(facts.Breakaway, Is.EqualTo(breakaway));
    }

    /// <summary>
    /// A recorder outlives the window that starts it outside a job, in a job that does not kill on close, and in one that
    /// lets its processes out: by themselves (silent breakaway), or when asked (breakaway). Under dotnet run's job it does
    /// not, either way; in a job that kills on close and allows breakaway, only by breaking away, so that after a refused
    /// breakaway there a start without it would not outlive the window.
    /// </summary>
    [TestCase(false, 0x0000u, true, true)]
    [TestCase(true, 0x2000u, false, false)]
    [TestCase(true, 0x2800u, true, false)]
    [TestCase(true, 0x3000u, true, true)]
    [TestCase(true, 0x3800u, true, true)]
    [TestCase(true, 0x1800u, true, true)]
    [TestCase(true, 0x0800u, true, true)]
    [TestCase(true, 0x0000u, true, true)]
    public void A_recorder_outlives_the_window_only_where_the_job_lets_it(bool inJob, uint limits, bool withBreakaway, bool withoutBreakaway)
    {
        var facts = inJob ? JobFacts.FromLimitFlags(limits) : JobFacts.NoJob;

        Assert.That(facts.RecorderOutlivesWindow(facts.Breakaway), Is.EqualTo(withBreakaway), "started as the facts say");
        Assert.That(facts.RecorderOutlivesWindow(breakaway: false), Is.EqualTo(withoutBreakaway), "started without breakaway");
    }

    /// <summary>
    /// What is taken when Windows does not say is dotnet run's job: no breakaway, and no recorder that outlives the window,
    /// so that ClassScribe never says that closing the window is safe when it may not be.
    /// </summary>
    [Test]
    public void The_cautious_facts_are_those_of_dotnet_runs_job()
    {
        Assert.That(JobFacts.Unknown, Is.EqualTo(FakeJobInspector.DotnetRun));
        Assert.That(JobFacts.Unknown.Breakaway, Is.False);
        Assert.That(JobFacts.Unknown.RecorderOutlivesWindow(breakaway: false), Is.False);
    }
}
