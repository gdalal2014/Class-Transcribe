using ClassScribe.Drive;

namespace ClassScribe.Tests;

/// <summary>
/// How a step of an upload is asked again: truncated exponential backoff as Google describes it (2, 4, 8 ... seconds, plus up
/// to a second at random, at most 64), 8 attempts in all, on the test's clock; only Google out of reach or busy is asked
/// again, and the caller's cancel ends a wait at once.
/// </summary>
[TestFixture]
public class DriveRetryTests
{
    private ManualTimeProvider _time = null!;
    private TestLoggerFactory _logs = null!;

    /// <summary>A clock the test moves and a log to read.</summary>
    [SetUp]
    public void SetUp()
    {
        _time = new ManualTimeProvider(TestRuns.Start);
        _logs = new TestLoggerFactory();
    }

    /// <summary>A retry whose random part is <paramref name="jitter"/> milliseconds.</summary>
    private DriveRetry Retry(int jitter = 0) => new(_time, new Microsoft.Extensions.Logging.Logger<DriveRetry>(_logs), () => jitter);

    /// <summary>The waits double from 2 seconds and stop growing at 64.</summary>
    [Test]
    public void The_waits_double_from_2_seconds_to_at_most_64()
    {
        var retry = Retry();

        Assert.That(Enumerable.Range(1, 8).Select(failures => retry.WaitAfter(failures).TotalSeconds), Is.EqualTo(new[] { 2.0, 4, 8, 16, 32, 64, 64, 64 }));
    }

    /// <summary>The random part adds up to a second to each wait, and the longest wait stays 64 seconds.</summary>
    [Test]
    public void The_random_part_adds_up_to_a_second_but_never_past_64()
    {
        var retry = Retry(jitter: 1000);

        Assert.That(Enumerable.Range(1, 7).Select(failures => retry.WaitAfter(failures).TotalSeconds), Is.EqualTo(new[] { 3.0, 5, 9, 17, 33, 64, 64 }));
    }

    /// <summary>
    /// Google out of reach is asked again 7 times after waits of 2 to 64 seconds, 190 seconds in all, and then the last
    /// failure stands; the first failure is said, with how long ClassScribe goes on asking.
    /// </summary>
    [Test]
    public async Task Google_out_of_reach_is_asked_eight_times_then_the_failure_stands()
    {
        var attempts = 0;
        var step = Retry().RunAsync<int>(_ =>
        {
            attempts++;
            throw new GoogleDriveException(DriveProblem.Unreachable, $"Google Drive could not be reached (attempt {attempts}).");
        }, "show a file", CancellationToken.None);

        var moved = await _time.AdvanceUntilAsync(() => step.IsCompleted, TimeSpan.FromSeconds(1));

        var failure = Assert.ThrowsAsync<GoogleDriveException>(() => step)!;
        Assert.That((attempts, failure.Message), Is.EqualTo((8, "Google Drive could not be reached (attempt 8).")));
        Assert.That(moved, Is.InRange(TimeSpan.FromSeconds(190), TimeSpan.FromSeconds(200)), "the waits of 2, 4, 8, 16, 32, 64 and 64 seconds");
        Assert.That(_logs.Messages[0], Is.EqualTo("Google Drive could not be reached (attempt 1). ClassScribe asks again in 2 seconds, and goes on asking for about 4 minutes"));
    }

    /// <summary>A busy Drive that answers on the third attempt gives its answer, after waits of 2 and 4 seconds.</summary>
    [Test]
    public async Task A_busy_Drive_that_answers_later_gives_its_answer()
    {
        var attempts = 0;
        var step = Retry().RunAsync(_ => ++attempts < 3
            ? throw new GoogleDriveException(DriveProblem.Busy, "Google Drive refused to show a file (HTTP 503).")
            : Task.FromResult("answer"), "show a file", CancellationToken.None);

        var moved = await _time.AdvanceUntilAsync(() => step.IsCompleted, TimeSpan.FromSeconds(1));

        Assert.That(await step, Is.EqualTo("answer"));
        Assert.That(moved, Is.InRange(TimeSpan.FromSeconds(6), TimeSpan.FromSeconds(8)), "the waits of 2 and 4 seconds");
    }

    /// <summary>A refusal, or a sign-in that has gone, is not asked again: it goes through at once.</summary>
    [TestCase(DriveProblem.Refused)]
    [TestCase(DriveProblem.SignInEnded)]
    [TestCase(DriveProblem.Full)]
    [TestCase(DriveProblem.NotSignedIn)]
    public void Other_failures_go_through_at_once(DriveProblem problem)
    {
        var attempts = 0;

        Assert.ThrowsAsync<GoogleDriveException>(() => Retry().RunAsync<int>(_ =>
        {
            attempts++;
            throw new GoogleDriveException(problem, "No.");
        }, "show a file", CancellationToken.None));
        Assert.That(attempts, Is.EqualTo(1));
    }

    /// <summary>The caller's cancel ends a wait at once, as a cancel.</summary>
    [Test]
    public async Task A_cancel_ends_a_wait()
    {
        using var cancel = new CancellationTokenSource();
        var step = Retry().RunAsync<int>(_ => throw new GoogleDriveException(DriveProblem.Unreachable, "Out of reach."), "show a file", cancel.Token);

        await Wait.UntilAsync(() => _time.ActiveTimers > 0);
        await cancel.CancelAsync();

        Assert.That(async () => await step, Throws.InstanceOf<OperationCanceledException>());
    }
}
