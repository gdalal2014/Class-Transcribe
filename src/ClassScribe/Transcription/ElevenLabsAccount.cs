using System.Globalization;
using System.Net;
using System.Text.Json;
using ClassScribe.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

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

/// <summary>
/// Reads the account's subscription through <see cref="IElevenLabsApi"/>, when ElevenLabs:ReadAccountUsage asks for it:
/// one request, no retry, <see cref="Limit"/> of its own. A 401 or 403 is a refusal, with ElevenLabs' code kept for the
/// line; any other failure is unavailable, noted at Debug, because the transcript is already safe and the account line is a
/// courtesy. The key is never logged. The settings are those of the run's own scope.
/// </summary>
public sealed class ElevenLabsAccount(IElevenLabsApi api, IOptionsSnapshot<ElevenLabsOptions> options, TimeProvider time, ILogger<ElevenLabsAccount> logger) : IElevenLabsAccount
{
    /// <summary>How long one read may take, the answer included.</summary>
    public static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

    /// <summary>The words for a read that ran into <see cref="Limit"/>.</summary>
    public static readonly string NoAnswerInTime = $"no answer within {(int)Limit.TotalSeconds} seconds";

    /// <inheritdoc />
    public async Task<AccountReading> ReadAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value;
        if (!settings.ReadAccountUsage || string.IsNullOrWhiteSpace(settings.ApiKey)) return AccountReading.Off;

        using var limit = new CancellationTokenSource(Limit, time);
        using var limited = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, limit.Token);
        try
        {
            using var response = await api.GetSubscriptionAsync(settings.ApiKey.Trim(), settings.ApiVersion, limited.Token);
            var body = await response.Content.ReadAsStringAsync(limited.Token);
            var status = (int)response.StatusCode;
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return new AccountReading(AccountReadingKind.Refused, Status: status, Reason: RefusalReason(body));
            if (status is < 200 or > 299)
                return new AccountReading(AccountReadingKind.Unavailable, Status: status, Reason: $"HTTP {status}");

            return Parse(body) ?? new AccountReading(AccountReadingKind.Unavailable, Status: status, Reason: "the answer could not be read");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex)
        {
            // Deliberately not rethrown: the transcript is already safe; the account line is a courtesy, and a read that ran
            // into its own limit is reported as unavailable.
            logger.LogDebug(ex, "The account's credits were not read: {Reason}", NoAnswerInTime);
            return new AccountReading(AccountReadingKind.Unavailable, Reason: NoAnswerInTime);
        }
        catch (HttpRequestException ex)
        {
            // Deliberately not rethrown: as above; a connection that failed is reported as unavailable.
            logger.LogDebug(ex, "The account's credits were not read: the connection failed");
            return new AccountReading(AccountReadingKind.Unavailable, Reason: "the connection failed");
        }
        catch (Exception ex)
        {
            // Deliberately not rethrown: as above. Whatever else the request runs into must never reach the run, whose
            // transcript is saved; it is reported as unavailable, with the reason.
            logger.LogDebug(ex, "The account's credits were not read: the request failed");
            return new AccountReading(AccountReadingKind.Unavailable, Reason: $"the request failed: {ex.Message.TrimEnd('.')}");
        }
    }

    /// <summary>
    /// The reading in a subscription answer: the count must be there; the limit, the reset and the overage are read when
    /// they are. Null for a body that is not ElevenLabs' answer.
    /// </summary>
    private static AccountReading? Parse(string body)
    {
        SubscriptionResponse? subscription;
        try
        {
            subscription = JsonSerializer.Deserialize(body, ElevenLabsJson.Default.SubscriptionResponse);
        }
        catch (JsonException)
        {
            // Deliberately not rethrown: an answer in another shape is no answer; the caller reports the read as unavailable.
            return null;
        }

        if (subscription?.CharacterCount is not { } count) return null;

        var (amount, currency) = Overage(subscription.CurrentOverage);
        return new AccountReading(AccountReadingKind.Read, count, Math.Max(0, subscription.CharacterLimit ?? 0), Reset(subscription.NextCharacterCountResetUnix), amount, currency);
    }

    /// <summary>The moment a Unix time in seconds names, or null when there is none or it is out of range.</summary>
    private static DateTimeOffset? Reset(long? unixSeconds)
    {
        if (unixSeconds is not { } seconds) return null;

        try
        {
            return DateTimeOffset.FromUnixTimeSeconds(seconds);
        }
        catch (ArgumentOutOfRangeException)
        {
            // Deliberately not rethrown: a reset time outside the calendar is left out of the line; the credits still count.
            return null;
        }
    }

    /// <summary>The overage's amount and currency, from an object whose amount is text or a number; nothing when it is neither.</summary>
    private static (decimal? Amount, string? Currency) Overage(JsonElement? overage)
    {
        if (overage is not { ValueKind: JsonValueKind.Object } element) return (null, null);

        decimal? amount = null;
        if (element.TryGetProperty("amount", out var amountElement))
        {
            if (amountElement.ValueKind == JsonValueKind.Number && amountElement.TryGetDecimal(out var number)) amount = number;
            else if (amountElement.ValueKind == JsonValueKind.String && decimal.TryParse(amountElement.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed)) amount = parsed;
        }

        var currency = element.TryGetProperty("currency", out var currencyElement) && currencyElement.ValueKind == JsonValueKind.String ? currencyElement.GetString() : null;
        return (amount, currency);
    }

    /// <summary>
    /// ElevenLabs' reason for a refusal, from the body's detail: its code (the current field), or its status (the older
    /// one), such as missing_permissions; or the detail itself when it is plain words. Null when the body says nothing readable.
    /// </summary>
    private static string? RefusalReason(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (!document.RootElement.TryGetProperty("detail", out var detail)) return null;

            return detail.ValueKind switch
            {
                JsonValueKind.Object => Text(detail, "code") ?? Text(detail, "status") ?? Text(detail, "message"),
                JsonValueKind.String => Shorten(detail.GetString()),
                _ => null
            };
        }
        catch (JsonException)
        {
            // Deliberately not rethrown: a refusal without a readable body is still a refusal; the line then gives the status alone.
            return null;
        }
    }

    /// <summary>The string property <paramref name="name"/> of <paramref name="element"/>, shortened, or null when it is not a string.</summary>
    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? Shorten(value.GetString()) : null;

    /// <summary>Words from ElevenLabs cut to a length that fits on a line, and trimmed; null for nothing.</summary>
    private static string? Shorten(string? words)
    {
        var trimmed = words?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return null;
        return trimmed.Length > 80 ? trimmed[..80] + "..." : trimmed;
    }
}
