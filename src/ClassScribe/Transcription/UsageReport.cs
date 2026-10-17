using System.Globalization;
using ClassScribe.Settings;
using Microsoft.Extensions.Logging;

namespace ClassScribe.Transcription;

/// <summary>
/// Says, after a transcript, what it cost the account in credits: the account's credits are read before the upload begins
/// and again once the transcript is saved and the results are open, and the difference is what ElevenLabs charged for it,
/// with what is left until the period resets. Where the key may not read the account, as a key restricted to Speech to
/// Text may not, the refusal is said once, with what to allow; where ElevenLabs could not be read, that is said and nothing
/// more. Nothing here delays the transcript, fails the run or throws. One per run's scope: the workflow begins the first
/// read as the upload starts, and the report is the last step, after everything anyone waits for: in a window the command
/// reports once the results are open; in a recorder its host reports once the end has gone to the windows, which open
/// them, so the line is then in the recording's log rather than on a screen (see <see cref="IElevenLabsAccount"/>).
/// </summary>
public sealed class UsageReport(IElevenLabsAccount account, TimeProvider time, ILogger<UsageReport> logger)
{
    /// <summary>The words that end the line for a refusal: what to allow on the key, and how to stop asking.</summary>
    public const string RefusedAdvice =
        "To see the credits here, allow User (read) on the key under Developers > API Keys on elevenlabs.io, or set " +
        $"{ElevenLabsOptions.SectionName}:{nameof(ElevenLabsOptions.ReadAccountUsage)} to false to stop asking";

    private readonly Lock _gate = new();

    /// <summary>The read begun before the upload, while it runs or waits to be reported; null before <see cref="Begin"/> and after the report.</summary>
    private Task<AccountReading?>? _before;

    /// <summary>
    /// Begins the read of the account's credits before the upload, and remembers it for <see cref="ReportAsync"/>; a second
    /// call in the same run changes nothing. It is not waited for, so the upload is never held up by it; a read that ends
    /// badly is kept as unavailable, never thrown.
    /// </summary>
    public void Begin(CancellationToken cancellationToken)
    {
        lock (_gate) _before ??= ReadSafelyAsync(cancellationToken);
    }

    /// <summary>
    /// Reads the account's credits again, now that the transcript is saved, and logs one line: what the transcript cost in
    /// credits when both reads succeeded, the account's standing when only this one did, the refusal once when the key may
    /// not read the account (the second read is then not made), or that the credits could not be read. Nothing when
    /// ElevenLabs:ReadAccountUsage is false. Never throws: a cancel from <paramref name="cancellationToken"/> ends it quietly.
    /// </summary>
    public async Task ReportAsync(CancellationToken cancellationToken)
    {
        Task<AccountReading?>? begun;
        lock (_gate)
        {
            begun = _before;
            _before = null;
        }

        var before = begun is null ? null : await begun;
        if (before is { Kind: AccountReadingKind.Off }) return;
        if (before is { Kind: AccountReadingKind.Refused })
        {
            LogRefused(before);
            return;
        }

        var after = await ReadSafelyAsync(cancellationToken);
        if (after is null) return;

        switch (after.Kind)
        {
            case AccountReadingKind.Off:
                return;
            case AccountReadingKind.Refused:
                LogRefused(after);
                return;
            case AccountReadingKind.Unavailable:
                logger.LogInformation("The account's credits could not be read ({Reason}); the transcript is not affected", after.Reason ?? "no answer");
                return;
        }

        var overage = Overage(after);
        if (before is { Kind: AccountReadingKind.Read } && after.CharacterCount >= before.CharacterCount)
        {
            var charged = after.CharacterCount - before.CharacterCount;
            if (charged == 0)
            {
                logger.LogInformation("ElevenLabs shows no change in the account's credits for this transcript ({Used}); on usage-based API billing the charge appears on the usage page instead{Overage}",
                    Used(after), overage);
            }
            else
            {
                logger.LogInformation("ElevenLabs charged {Charged} credits for this transcript{Left}{Overage}", Number(charged), Left(after), overage);
            }

            return;
        }

        logger.LogInformation("ElevenLabs account: {Standing}{Overage}", Standing(after), overage);
    }

    /// <summary>
    /// Reads the account, never throwing: a cancel from the run's own token gives null, noted at Debug, and anything else
    /// the reader lets through, which it should not, is reported as unavailable with the reason.
    /// </summary>
    private async Task<AccountReading?> ReadSafelyAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await account.ReadAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Deliberately not rethrown: the transcript is saved, and a Ctrl+C now only ends this courtesy read.
            logger.LogDebug("The account's credits were not read: the run was cancelled");
            return null;
        }
        catch (Exception ex)
        {
            // Deliberately not rethrown: the transcript is saved; the account line is a courtesy, and whatever the reader
            // let through is reported in its place rather than failing the run.
            logger.LogDebug(ex, "The account's credits were not read: the reader failed");
            return new AccountReading(AccountReadingKind.Unavailable, Reason: $"the request failed: {ex.Message.TrimEnd('.')}");
        }
    }

    /// <summary>The refusal, once per run, at Information: never a warning, since the transcript needs nothing more than the key has.</summary>
    private void LogRefused(AccountReading refused)
    {
        var status = refused.Status is { } code ? $"HTTP {code}" : "refused";
        var reason = refused.Reason is { } why ? $"{status}, {why}" : status;
        logger.LogInformation("ElevenLabs would not show the account's credits to this key ({Reason}): most likely it is restricted to Speech to Text, which is all the transcript needs. {Advice}",
            reason, RefusedAdvice);
    }

    /// <summary>The account's standing in words: the credits used of those allowed this period, and what is left until the reset.</summary>
    private string Standing(AccountReading reading) => reading.CharacterLimit > 0
        ? $"{Number(reading.CharacterCount)} of {Number(reading.CharacterLimit)} credits used this period; {Number(Math.Max(0, reading.CharacterLimit - reading.CharacterCount))} left{Until(reading)}"
        : $"{Number(reading.CharacterCount)} credits used this period{Until(reading)}";

    /// <summary>What is left after a charge, as a clause: "; 56,638 of 100,000 credits are left until 1 November", or nothing when no limit is known.</summary>
    private string Left(AccountReading reading) => reading.CharacterLimit > 0
        ? $"; {Number(Math.Max(0, reading.CharacterLimit - reading.CharacterCount))} of {Number(reading.CharacterLimit)} credits are left{Until(reading)}"
        : "";

    /// <summary>The credits used, in parentheses' worth of words: "43,362 of 100,000 used", or "43,362 used" without a limit.</summary>
    private static string Used(AccountReading reading) => reading.CharacterLimit > 0
        ? $"{Number(reading.CharacterCount)} of {Number(reading.CharacterLimit)} used"
        : $"{Number(reading.CharacterCount)} used";

    /// <summary>" until 1 November", the period's reset by the local day and month, or nothing when it is not known.</summary>
    private string Until(AccountReading reading) => reading.NextReset is { } reset
        ? $" until {TimeZoneInfo.ConvertTime(reset, time.LocalTimeZone).ToString("d MMMM", CultureInfo.InvariantCulture)}"
        : "";

    /// <summary>The usage-based overage as a clause, when there is one above zero: "; usage-based billing so far this period: $1.23".</summary>
    private static string Overage(AccountReading reading)
    {
        if (reading.OverageAmount is not { } amount || amount <= 0) return "";

        var money = reading.OverageCurrency is null || reading.OverageCurrency.Equals("usd", StringComparison.OrdinalIgnoreCase)
            ? string.Create(CultureInfo.InvariantCulture, $"${amount:0.00}")
            : string.Create(CultureInfo.InvariantCulture, $"{amount:0.00} {reading.OverageCurrency.ToUpperInvariant()}");
        return $"; usage-based billing so far this period: {money}";
    }

    /// <summary>A count of credits with thousands separated, such as 43,362.</summary>
    private static string Number(long count) => count.ToString("N0", CultureInfo.InvariantCulture);
}
