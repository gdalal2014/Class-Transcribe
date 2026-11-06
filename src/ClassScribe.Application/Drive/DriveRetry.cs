using Microsoft.Extensions.Logging;

namespace ClassScribe.Drive;

/// <summary>
/// How a step of an upload is asked again when Google could not be reached or was busy, as Google describes it: truncated
/// exponential backoff, a wait of 2, 4, 8 ... seconds plus up to a second at random, at most 64 seconds, and 8 attempts in
/// all, about 4 minutes, rather than for ever. The waits run on the injected clock, so tests run them on the manual one. Any
/// other failure, and the caller's own cancel, goes through at once.
/// </summary>
public sealed class DriveRetry(TimeProvider time, ILogger<DriveRetry> logger)
{
    /// <summary>How many times a step is tried before its failure stands.</summary>
    public const int MaxAttempts = 8;

    /// <summary>The longest wait between two attempts.</summary>
    public static readonly TimeSpan LongestWait = TimeSpan.FromSeconds(64);

    /// <summary>The random part of each wait, in milliseconds, from 0 to 1,000; tests give a fixed one.</summary>
    private readonly Func<int> _jitter = () => Random.Shared.Next(0, 1001);

    /// <summary>A retry whose random part of each wait is <paramref name="jitterMilliseconds"/>, for tests.</summary>
    internal DriveRetry(TimeProvider time, ILogger<DriveRetry> logger, Func<int> jitterMilliseconds) : this(time, logger) => _jitter = jitterMilliseconds;

    /// <summary>True for a failure that may pass if the step is asked again after a wait: Google out of reach, or busy.</summary>
    public static bool IsPassing(Exception failure) => failure is GoogleDriveException { Problem: DriveProblem.Unreachable or DriveProblem.Busy };

    /// <summary>The wait after the <paramref name="failures"/>th failure in a row: 2 to the power of it in seconds, plus the random part, at most 64 seconds.</summary>
    public TimeSpan WaitAfter(int failures)
    {
        var seconds = Math.Pow(2, Math.Clamp(failures, 1, 16));
        var wait = TimeSpan.FromSeconds(seconds) + TimeSpan.FromMilliseconds(Math.Clamp(_jitter(), 0, 1000));
        return wait < LongestWait ? wait : LongestWait;
    }

    /// <summary>
    /// Runs <paramref name="step"/>, which does <paramref name="what"/>, and asks it again after a wait while it fails in a
    /// way that may pass (see <see cref="IsPassing"/>), up to <see cref="MaxAttempts"/> attempts; the last failure stands.
    /// </summary>
    public async Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> step, string what, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await step(cancellationToken);
            }
            catch (GoogleDriveException ex) when (IsPassing(ex) && attempt < MaxAttempts)
            {
                // Deliberately not rethrown: Google out of reach or busy for a moment is asked again after a wait, as Google
                // advises; the last attempt's failure goes to the caller.
                await WaitAsync(attempt, ex, what, cancellationToken);
            }
        }
    }

    /// <summary>
    /// Waits after the <paramref name="failures"/>th failure in a row of <paramref name="what"/>, which failed with
    /// <paramref name="failure"/>: the first is said, with how long ClassScribe goes on asking; the rest are noted at Debug.
    /// </summary>
    public async Task WaitAsync(int failures, GoogleDriveException failure, string what, CancellationToken cancellationToken)
    {
        var wait = WaitAfter(failures);
        if (failures == 1)
            logger.LogInformation("{Problem} ClassScribe asks again in {Seconds} seconds, and goes on asking for about 4 minutes", failure.Message, (int)Math.Round(wait.TotalSeconds));
        else
            logger.LogDebug("Asking Google Drive again to {What} in {Seconds} seconds (attempt {Attempt} of {Attempts})", what, (int)Math.Round(wait.TotalSeconds), failures + 1, MaxAttempts);

        await Task.Delay(wait, time, cancellationToken);
    }
}
