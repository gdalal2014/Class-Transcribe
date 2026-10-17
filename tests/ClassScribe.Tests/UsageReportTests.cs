using ClassScribe.Transcription;
using Microsoft.Extensions.Logging;

namespace ClassScribe.Tests;

/// <summary>
/// The line that says what a transcript cost the account, from a read before the upload and one after: the credits charged
/// and what is left, the account's standing when only one read succeeded, the refusal said once, the account unavailable,
/// nothing when the read is off, and a reader that fails or is cancelled, which never reaches the run.
/// </summary>
[TestFixture]
public class UsageReportTests
{
    /// <summary>When the period resets: 1 November 2026 at noon UTC, 1 November in the test's Eastern time too.</summary>
    private static readonly DateTimeOffset Reset = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

    private FakeElevenLabsAccount _account = null!;
    private RecordingLogger<UsageReport> _log = null!;
    private UsageReport _report = null!;

    /// <summary>The fake reader, the log to look at, and the report over them, on a clock whose local time is US Eastern.</summary>
    [SetUp]
    public void SetUp()
    {
        _account = new FakeElevenLabsAccount();
        _log = new RecordingLogger<UsageReport>();
        _report = new UsageReport(_account, ZonedTimeProvider.EasternSummer, _log);
    }

    /// <summary>Every line about the account is information: never a warning, whatever happened.</summary>
    [TearDown]
    public void TearDown() => Assert.That(_log.Problems, Is.Empty);

    /// <summary>What the report said at Information, which the window and the recording's log show; its Debug notes are left out.</summary>
    private IReadOnlyList<string> Said => [.. _log.Entries.Where(entry => entry.Level == LogLevel.Information).Select(entry => entry.Message)];

    /// <summary>A reading that succeeded, with the period's reset.</summary>
    private static AccountReading Read(long used, long limit = 100_000) => FakeElevenLabsAccount.Read(used, limit, Reset);

    /// <summary>Both reads succeeded: the difference is what the transcript cost, with what is left until the reset, by the local day and month.</summary>
    [Test]
    public async Task Both_reads_give_what_the_transcript_cost()
    {
        _account.Answer(Read(0), Read(43362));

        _report.Begin(CancellationToken.None);
        await _report.ReportAsync(CancellationToken.None);

        Assert.That(Said, Is.EqualTo(["ElevenLabs charged 43,362 credits for this transcript; 56,638 of 100,000 credits are left until 1 November"]));
        Assert.That(_account.Reads, Is.EqualTo(2));
    }

    /// <summary>A key that may not read the account is said once, with what to allow, and the second read is not made: it would be refused too.</summary>
    [Test]
    public async Task A_refused_first_read_is_said_once_and_the_second_read_is_not_made()
    {
        _account.Answer(new AccountReading(AccountReadingKind.Refused, Status: 401, Reason: "missing_permissions"));

        _report.Begin(CancellationToken.None);
        await _report.ReportAsync(CancellationToken.None);

        Assert.That(Said, Has.Count.EqualTo(1));
        Assert.That(Said[0], Does.StartWith("ElevenLabs would not show the account's credits to this key (HTTP 401, missing_permissions): most likely it is restricted to Speech to Text, which is all the transcript needs. ")
            .And.EndWith(UsageReport.RefusedAdvice));
        Assert.That(UsageReport.RefusedAdvice, Does.Contain("allow User (read) on the key under Developers > API Keys on elevenlabs.io").And.Contain("ElevenLabs:ReadAccountUsage to false"));
        Assert.That(_account.Reads, Is.EqualTo(1));
    }

    /// <summary>A report without a first read, or after one that was unavailable, gives the account's standing from the second alone.</summary>
    [Test]
    public async Task A_report_with_only_the_second_read_gives_the_accounts_standing()
    {
        _account.Answer(Read(43362), new AccountReading(AccountReadingKind.Unavailable, Reason: ElevenLabsAccount.NoAnswerInTime), Read(43362));

        await _report.ReportAsync(CancellationToken.None);
        _report.Begin(CancellationToken.None);
        await _report.ReportAsync(CancellationToken.None);

        Assert.That(Said, Is.EqualTo(Enumerable.Repeat("ElevenLabs account: 43,362 of 100,000 credits used this period; 56,638 left until 1 November", 2)));
    }

    /// <summary>No change between the reads is said as such, because API usage on a plan may be billed in dollars rather than credits.</summary>
    [Test]
    public async Task No_change_in_the_credits_is_said_as_such()
    {
        _account.Answer(Read(43362), Read(43362));

        _report.Begin(CancellationToken.None);
        await _report.ReportAsync(CancellationToken.None);

        Assert.That(Said, Is.EqualTo(["ElevenLabs shows no change in the account's credits for this transcript (43,362 of 100,000 used); on usage-based API billing the charge appears on the usage page instead"]));
    }

    /// <summary>A usage-based overage above zero is appended, in dollars, or in the currency given when it is another.</summary>
    [Test]
    public async Task An_overage_is_appended()
    {
        _account.Answer(Read(0), FakeElevenLabsAccount.Read(500, 100_000, Reset, 1.23m, "usd"), FakeElevenLabsAccount.Read(500, 100_000, Reset, 0.5m, "eur"), FakeElevenLabsAccount.Read(500, 100_000, Reset, 0m, "usd"));

        _report.Begin(CancellationToken.None);
        await _report.ReportAsync(CancellationToken.None);
        await _report.ReportAsync(CancellationToken.None);
        await _report.ReportAsync(CancellationToken.None);

        Assert.That(Said[0], Does.EndWith("; usage-based billing so far this period: $1.23"));
        Assert.That(Said[1], Does.EndWith("; usage-based billing so far this period: 0.50 EUR"));
        Assert.That(Said[2], Does.Not.Contain("usage-based billing"));
    }

    /// <summary>Without a limit in the answer, the lines have no "left" clause.</summary>
    [Test]
    public async Task Without_a_limit_nothing_is_said_to_be_left()
    {
        _account.Answer(FakeElevenLabsAccount.Read(0, limit: 0), FakeElevenLabsAccount.Read(100, limit: 0), FakeElevenLabsAccount.Read(100, limit: 0));

        _report.Begin(CancellationToken.None);
        await _report.ReportAsync(CancellationToken.None);
        await _report.ReportAsync(CancellationToken.None);

        Assert.That(Said, Is.EqualTo(["ElevenLabs charged 100 credits for this transcript", "ElevenLabs account: 100 credits used this period"]));
    }

    /// <summary>A second count below the first, as after the period reset between the reads, gives the standing rather than a negative charge.</summary>
    [Test]
    public async Task A_second_count_below_the_first_gives_the_standing()
    {
        _account.Answer(Read(90000), Read(100));

        _report.Begin(CancellationToken.None);
        await _report.ReportAsync(CancellationToken.None);

        Assert.That(Said, Is.EqualTo(["ElevenLabs account: 100 of 100,000 credits used this period; 99,900 left until 1 November"]));
    }

    /// <summary>With the read switched off nothing is said, and no second read is made.</summary>
    [Test]
    public async Task Switched_off_nothing_is_said()
    {
        _report.Begin(CancellationToken.None);
        await _report.ReportAsync(CancellationToken.None);

        Assert.That(Said, Is.Empty);
        Assert.That(_account.Reads, Is.EqualTo(1));
    }

    /// <summary>A second read that is unavailable says so, and that the transcript is not affected; a refusal at the second read is said as a refusal.</summary>
    [Test]
    public async Task A_second_read_that_fails_says_so()
    {
        _account.Answer(Read(0), new AccountReading(AccountReadingKind.Unavailable, Reason: "HTTP 500"), new AccountReading(AccountReadingKind.Refused, Status: 403, Reason: "insufficient_permissions"));

        _report.Begin(CancellationToken.None);
        await _report.ReportAsync(CancellationToken.None);
        await _report.ReportAsync(CancellationToken.None);

        Assert.That(Said[0], Is.EqualTo("The account's credits could not be read (HTTP 500); the transcript is not affected"));
        Assert.That(Said[1], Does.StartWith("ElevenLabs would not show the account's credits to this key (HTTP 403, insufficient_permissions)"));
    }

    /// <summary>A reader that throws, which the real one never does, is reported as unavailable and never reaches the run.</summary>
    [Test]
    public async Task A_reader_that_throws_is_reported_as_unavailable_and_never_propagates()
    {
        _account.Failure = new InvalidOperationException("boom");

        _report.Begin(CancellationToken.None);
        await _report.ReportAsync(CancellationToken.None);

        Assert.That(Said, Is.EqualTo(["The account's credits could not be read (the request failed: boom); the transcript is not affected"]));
    }

    /// <summary>The first read is begun, not waited for: a read that takes its time never holds up the upload, and the report waits for it later.</summary>
    [Test]
    public async Task Begin_does_not_wait_for_the_read()
    {
        _account.Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _account.Answer(Read(0), Read(10));

        _report.Begin(CancellationToken.None);
        var begun = _account.Reads;
        var report = _report.ReportAsync(CancellationToken.None);
        await Task.Delay(50);
        var reportedWhileHeld = report.IsCompleted;
        _account.Hold.SetResult();
        await report.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.That((begun, reportedWhileHeld), Is.EqualTo((1, false)));
        Assert.That(Said, Is.EqualTo(["ElevenLabs charged 10 credits for this transcript; 99,990 of 100,000 credits are left until 1 November"]));
    }

    /// <summary>A run cancelled with Ctrl+C ends the reads quietly: nothing is said, and nothing is thrown.</summary>
    [Test]
    public async Task A_cancel_ends_the_report_quietly()
    {
        using var cancellation = new CancellationTokenSource();
        _account.Answer(Read(0), Read(10));
        cancellation.Cancel();

        _report.Begin(cancellation.Token);
        await _report.ReportAsync(cancellation.Token);

        Assert.That(Said, Is.Empty);
    }
}
