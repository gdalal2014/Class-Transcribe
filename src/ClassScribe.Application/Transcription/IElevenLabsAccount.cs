namespace ClassScribe.Transcription;

/// <summary>How a read of the account's credits ended.</summary>
public enum AccountReadingKind
{
    /// <summary>ElevenLabs answered with the account's numbers.</summary>
    Read,

    /// <summary>ElevenLabs would not show the account to this key (HTTP 401 or 403): the key lacks the User permission.</summary>
    Refused,

    /// <summary>No usable answer: another status, no answer within the limit, a broken connection, or an answer that could not be read.</summary>
    Unavailable,

    /// <summary>Nothing was asked: ElevenLabs:ReadAccountUsage is false, or there is no key.</summary>
    Off
}

/// <summary>One read of the account's credits (see <see cref="IElevenLabsAccount"/>).</summary>
/// <param name="Kind">How the read ended; the numbers mean something only for <see cref="AccountReadingKind.Read"/>.</param>
/// <param name="CharacterCount">The credits used in the current period, which ElevenLabs counts as characters.</param>
/// <param name="CharacterLimit">The credits the plan allows in the period; 0 when ElevenLabs gives none.</param>
/// <param name="NextReset">When the period's count resets, when ElevenLabs says.</param>
/// <param name="OverageAmount">The usage-based overage of the period so far, when ElevenLabs gives one.</param>
/// <param name="OverageCurrency">The overage's currency, as ElevenLabs writes it, such as usd.</param>
/// <param name="Status">The HTTP status of the answer, when there was one.</param>
/// <param name="Reason">Why the read was refused or unavailable, in words for the log: ElevenLabs' code, such as missing_permissions, or what went wrong.</param>
public sealed record AccountReading(
    AccountReadingKind Kind,
    long CharacterCount = 0,
    long CharacterLimit = 0,
    DateTimeOffset? NextReset = null,
    decimal? OverageAmount = null,
    string? OverageCurrency = null,
    int? Status = null,
    string? Reason = null)
{
    /// <summary>The reading when nothing was asked.</summary>
    public static AccountReading Off { get; } = new(AccountReadingKind.Off);
}

/// <summary>Reads the account's credits from ElevenLabs, for the line that says what a transcript cost.</summary>
public interface IElevenLabsAccount
{
    /// <summary>
    /// Reads the account once, within a limit of its own, and never throws for anything ElevenLabs or the network does: a
    /// refusal, a failure and a missing answer each come back as a reading that says so. Only <paramref name="cancellationToken"/>,
    /// the caller's own, ends it with an <see cref="OperationCanceledException"/>.
    /// </summary>
    Task<AccountReading> ReadAsync(CancellationToken cancellationToken);
}
