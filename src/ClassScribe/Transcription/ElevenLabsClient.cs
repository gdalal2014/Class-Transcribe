using System.Net;
using ClassScribe.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Refit;

namespace ClassScribe.Transcription;

/// <summary>
/// Gets transcripts from ElevenLabs through <see cref="IElevenLabsApi"/>. Around the call itself it
/// checks the key, retries temporary failures, reports upload progress (to the log every ten percent,
/// and to the run that follows it at each percent, see <see cref="TranscriptionProgress"/>), holds the
/// whole exchange to a time limit that grows with the upload (see <see cref="UploadTimeLimit"/>) and
/// tells that apart from a cancel, and keeps the answer's JSON exactly as it arrived, read by
/// <see cref="ElevenLabsAnswer"/>. The settings are those of the run's own scope; the limit is its own
/// alone, as HttpClient's is switched off.
/// </summary>
public sealed class ElevenLabsClient(
    IElevenLabsApi api,
    IOptionsSnapshot<ElevenLabsOptions> options,
    TranscriptionProgress progress,
    TimeProvider time,
    ILogger<ElevenLabsClient> logger) : ITranscriptionClient
{
    /// <inheritdoc />
    public async Task<TranscriptionResult> TranscribeAsync(string audioPath, IReadOnlyList<string> keyterms, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        if (string.IsNullOrWhiteSpace(settings.ApiKey))
            throw new InvalidOperationException($"No ElevenLabs API key is set. Set the {ElevenLabsOptions.ApiKeyVariable} environment variable.");

        var maxAttempts = Math.Max(1, settings.MaxAttempts);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await SendOnceAsync(audioPath, keyterms, settings, cancellationToken);
            }
            catch (Exception ex) when (IsTemporary(ex) && attempt < maxAttempts)
            {
                var delay = TimeSpan.FromSeconds(Math.Max(0, settings.RetryDelaySeconds) * attempt);
                logger.LogWarning(ex, "Attempt {Attempt} of {MaxAttempts} failed; trying again in {Seconds} seconds", attempt, maxAttempts, delay.TotalSeconds);
                await Task.Delay(delay, time, cancellationToken);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                logger.LogError(ex, "The ElevenLabs request for {File} failed", Path.GetFileName(audioPath));
                if (MayBeAnEarlyRefusal(ex))
                    logger.LogWarning("If the connection breaks like this on every try, ElevenLabs may be turning the request down before it has read all of the upload, which looks the same from here. Check the API key and the account's credits before uploading again");
                throw;
            }
        }
    }

    /// <summary>
    /// Uploads the file once and parses the answer. The upload is announced with its size, in megabytes of a million
    /// bytes as upload speeds count them, and its time limit. The answer's arrival is logged with the transcription ID
    /// ElevenLabs gives it, when there is one, by which a transcript that was paid for can be fetched again should
    /// saving it fail.
    /// </summary>
    private async Task<TranscriptionResult> SendOnceAsync(string audioPath, IReadOnlyList<string> keyterms, ElevenLabsOptions settings, CancellationToken cancellationToken)
    {
        await using var file = new FileStream(audioPath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 81920, useAsync: true);
        var limit = UploadTimeLimit.For(audioPath, file.Length, settings);
        logger.LogInformation("Uploading {File} ({Megabytes:F1} MB) with {Keyterms} keyterms; time limit {Minutes} minutes",
            Path.GetFileName(audioPath), file.Length / 1_000_000.0, keyterms.Count, (long)limit.TotalMinutes);

        // Each try begins at nought, so that a second try shows as the new upload it is.
        progress.Report(TranscriptionStep.Uploading, 0);
        var (status, body) = await ExchangeAsync(file, audioPath, keyterms, settings, limit, cancellationToken);
        if ((int)status is < 200 or > 299)
            throw new TranscriptionException(Describe(status, body), status, body);

        var result = ElevenLabsAnswer.Parse(body, status);
        var language = result.LanguageCode ?? "unknown";
        if (string.IsNullOrWhiteSpace(result.TranscriptionId))
            logger.LogInformation("Transcript received: {Characters} characters, language {Language}", result.Text.Length, language);
        else
            logger.LogInformation("Transcript received: {Characters} characters, language {Language}, transcription ID {TranscriptionId}", result.Text.Length, language, result.TranscriptionId);

        LogTheEstimate(audioPath, keyterms.Count, result, settings);
        return result;
    }

    /// <summary>
    /// Logs what the transcript is estimated to have cost (see <see cref="TranscriptionCost"/>), right after its arrival:
    /// from the audio ElevenLabs says it counted, or else the upload file's length, at the price the settings give. The
    /// transcript has arrived and been paid for by now, so nothing here may fail it: a file whose length cannot be read any
    /// more only loses the estimate.
    /// </summary>
    private void LogTheEstimate(string audioPath, int keyterms, TranscriptionResult result, ElevenLabsOptions settings)
    {
        try
        {
            var counted = result.AudioDurationSecs is { } seconds && double.IsFinite(seconds) && seconds >= 0 && seconds < LongestCountedSeconds
                ? TimeSpan.FromSeconds(seconds)
                : (TimeSpan?)null;
            var estimate = TranscriptionCost.Estimate(counted, UploadTimeLimit.AudioLength(audioPath), keyterms, settings);
            logger.LogInformation("{Estimate}", TranscriptionCost.Describe(estimate));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Deliberately not rethrown: the transcript is in hand and has been paid for; the estimate is a courtesy, and a
            // file whose length cannot be read now only loses it.
            logger.LogDebug(ex, "Could not read the length of {File} for the cost estimate", Path.GetFileName(audioPath));
        }
    }

    /// <summary>The most seconds an answer's audio_duration_secs is believed: a hundred days, far beyond the most one file may hold (ElevenLabs:LongestFileHours, at most a day).</summary>
    private const double LongestCountedSeconds = 100 * 24 * 3600;

    /// <summary>
    /// Sends the request and reads the whole answer within <paramref name="limit"/>. Refit hands the response
    /// over as soon as its headers arrive, after which HttpClient's own timeout no longer applies, so the limit
    /// is set here and covers the body too. .NET reports a time limit as a cancellation, so that case is turned
    /// into a <see cref="TranscriptionException"/>: only the person at the keyboard cancels.
    /// </summary>
    private async Task<(HttpStatusCode Status, string Body)> ExchangeAsync(
        FileStream file, string audioPath, IReadOnlyList<string> keyterms, ElevenLabsOptions settings, TimeSpan limit, CancellationToken cancellationToken)
    {
        using var timeLimit = new CancellationTokenSource(limit, time);
        using var limited = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeLimit.Token);
        try
        {
            using var response = await api.TranscribeAsync(
                settings.ApiKey.Trim(),
                settings.ApiVersion,
                settings.ModelId,
                settings.TagAudioEvents ? "true" : "false",
                "word",
                settings.NoVerbatim ? "true" : null,
                string.IsNullOrWhiteSpace(settings.LanguageCode) ? null : settings.LanguageCode.Trim(),
                keyterms,
                CreateFilePart(file, audioPath),
                limited.Token);
            return (response.StatusCode, await response.Content.ReadAsStringAsync(limited.Token));
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TranscriptionException(
                $"ElevenLabs did not answer in time (limit: {(long)limit.TotalMinutes} minutes, at least {ElevenLabsOptions.SectionName}:{nameof(ElevenLabsOptions.TimeoutMinutes)} and more for a longer upload; on a slow line, a lower {ElevenLabsOptions.SectionName}:{nameof(ElevenLabsOptions.SlowestUploadMbps)} allows more). The audio is untouched, so the transcript can be requested again.",
                innerException: ex);
        }
    }

    /// <summary>
    /// Wraps the file as the <c>file</c> form field, logs upload progress every ten percent, and reports each percent to
    /// the run that follows the upload, with the wait for the transcript once all of it has gone.
    /// </summary>
    private StreamPart CreateFilePart(FileStream file, string audioPath)
    {
        var length = file.Length;
        var nextStep = 10;
        var reported = 0;
        var stream = new ProgressReadStream(file, read =>
        {
            var percent = length == 0 ? 100 : (int)Math.Min(100, read * 100 / length);
            if (percent != reported)
            {
                reported = percent;
                progress.Report(percent >= 100 ? TranscriptionStep.WaitingForTranscript : TranscriptionStep.Uploading, percent);
            }

            if (percent < nextStep) return;

            nextStep = percent / 10 * 10 + 10;
            if (percent >= 100)
                logger.LogInformation("Upload finished; waiting for ElevenLabs to transcribe");
            else
                logger.LogInformation("Uploaded {Percent}%", percent / 10 * 10);
        });

        // An ASCII file name keeps the header simple; only the extension matters to the service.
        var extension = Path.GetExtension(audioPath).ToLowerInvariant();
        var fileName = extension.Length is > 1 and <= 6 && extension.Skip(1).All(char.IsAsciiLetterOrDigit) ? $"audio{extension}" : "audio.bin";

        return new StreamPart(stream, fileName, "application/octet-stream");
    }

    /// <summary>
    /// True for failures worth another attempt: network errors, timeouts reported by the server, rate
    /// limits and server errors. Running into this app's own timeout is not one of them, because the
    /// next attempt would upload everything again and wait just as long.
    /// </summary>
    private static bool IsTemporary(Exception exception) => exception is HttpRequestException or TranscriptionException
    {
        StatusCode: HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests or >= HttpStatusCode.InternalServerError
    };

    /// <summary>
    /// True for a connection that broke without an answer once it was made. Over HTTP/1.1 the whole upload is sent
    /// before any answer is read, so a refusal ElevenLabs sends early, followed by a closed connection, reaches this
    /// app only as such a break. A connection that could not be made at all (the name did not resolve, the
    /// connection was refused or never answered, the TLS handshake or the proxy failed) never reached ElevenLabs.
    /// </summary>
    private static bool MayBeAnEarlyRefusal(Exception exception) => exception is HttpRequestException
    {
        StatusCode: null,
        HttpRequestError: not (HttpRequestError.NameResolutionError or HttpRequestError.ConnectionError
            or HttpRequestError.SecureConnectionError or HttpRequestError.ProxyTunnelError)
    };

    /// <summary>
    /// Builds an error message that says what to check, following the status codes ElevenLabs documents
    /// (https://elevenlabs.io/docs/developers/resources/errors): 402 for missing credits, 429 for rate and
    /// concurrency limits.
    /// </summary>
    private static string Describe(HttpStatusCode status, string body)
    {
        var hint = status switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                $"ElevenLabs rejected the API key. Check {ElevenLabsOptions.ApiKeyVariable} and that the key is allowed to use Speech to Text.",
            HttpStatusCode.PaymentRequired =>
                "The ElevenLabs account does not have enough credits for this transcript. Check the balance and the plan on elevenlabs.io.",
            HttpStatusCode.UnprocessableEntity or HttpStatusCode.BadRequest =>
                "ElevenLabs could not accept the request.",
            HttpStatusCode.TooManyRequests =>
                "ElevenLabs is rate limiting this account, or too many of its requests are running at once.",
            _ => "ElevenLabs could not transcribe the file."
        };

        var detail = body.Length > 500 ? body[..500] + "..." : body;
        return $"{hint} (HTTP {(int)status}) {detail}".TrimEnd();
    }
}
