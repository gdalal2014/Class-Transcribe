using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using ClassScribe.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClassScribe.Transcription;

/// <summary>
/// Calls the ElevenLabs speech-to-text endpoint. The request mirrors what ElevenLabs' own SDK sends:
/// a multipart form with <c>model_id</c>, one <c>keyterms</c> field per glossary term, and the file.
/// </summary>
public sealed class ElevenLabsClient(
    HttpClient http,
    IOptions<ElevenLabsOptions> options,
    ILogger<ElevenLabsClient> logger) : ITranscriptionClient
{
    private const string Endpoint = "v1/speech-to-text";
    private const string ApiKeyHeader = "xi-api-key";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

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
                await Task.Delay(delay, cancellationToken);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                logger.LogError(ex, "The ElevenLabs request for {File} failed", Path.GetFileName(audioPath));
                throw;
            }
        }
    }

    /// <summary>Uploads the file once and parses the answer.</summary>
    private async Task<TranscriptionResult> SendOnceAsync(string audioPath, IReadOnlyList<string> keyterms, ElevenLabsOptions settings, CancellationToken cancellationToken)
    {
        await using var file = new FileStream(audioPath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 81920, useAsync: true);
        var megabytes = file.Length / (1024.0 * 1024.0);
        logger.LogInformation("Uploading {File} ({Megabytes:F1} MB) with {Keyterms} keyterms", Path.GetFileName(audioPath), megabytes, keyterms.Count);

        using var content = new MultipartFormDataContent();
        AddField(content, "model_id", settings.ModelId);
        AddField(content, "tag_audio_events", settings.TagAudioEvents ? "true" : "false");
        AddField(content, "timestamps_granularity", "word");
        if (settings.NoVerbatim) AddField(content, "no_verbatim", "true");
        if (!string.IsNullOrWhiteSpace(settings.LanguageCode)) AddField(content, "language_code", settings.LanguageCode.Trim());
        foreach (var term in keyterms) AddField(content, "keyterms", term);
        content.Add(CreateFilePart(file, audioPath));
        UnquoteBoundary(content);

        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint) { Content = content };
        request.Headers.Add(ApiKeyHeader, settings.ApiKey.Trim());

        using var response = await SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new TranscriptionException(Describe(response.StatusCode, body), response.StatusCode, body);

        var result = Parse(body);
        logger.LogInformation("Transcript received: {Characters} characters, language {Language}", result.Text.Length, result.LanguageCode ?? "unknown");
        return result;
    }

    /// <summary>
    /// Sends the request. .NET reports its own timeout as a cancellation, so that case is turned into a
    /// <see cref="TranscriptionException"/> here: only the person at the keyboard cancels.
    /// </summary>
    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        try
        {
            return await http.SendAsync(request, cancellationToken);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TranscriptionException(
                $"ElevenLabs did not answer in time (limit: {http.Timeout.TotalMinutes:0.#} min, set by {ElevenLabsOptions.SectionName}:{nameof(ElevenLabsOptions.TimeoutMinutes)}). The audio is untouched, so the transcript can be requested again.",
                innerException: ex);
        }
    }

    /// <summary>Adds one plain form field with a quoted name, the way browsers and the ElevenLabs SDK write it.</summary>
    private static void AddField(MultipartFormDataContent content, string name, string value)
    {
        var part = new StringContent(value, Encoding.UTF8);
        part.Headers.ContentDisposition = new ContentDispositionHeaderValue("form-data") { Name = $"\"{name}\"" };
        content.Add(part);
    }

    /// <summary>
    /// .NET writes the multipart boundary in quotes. That is legal, but curl and the ElevenLabs SDK
    /// send it bare, so this does the same to stay on the path their servers see every day.
    /// </summary>
    private static void UnquoteBoundary(MultipartFormDataContent content)
    {
        var boundary = content.Headers.ContentType?.Parameters.FirstOrDefault(p => p.Name == "boundary");
        if (boundary?.Value is { } value) boundary.Value = value.Trim('"');
    }

    /// <summary>Wraps the file as the <c>file</c> form field and logs upload progress every ten percent.</summary>
    private StreamContent CreateFilePart(FileStream file, string audioPath)
    {
        var length = file.Length;
        var nextStep = 10;
        var stream = new ProgressReadStream(file, read =>
        {
            var percent = length == 0 ? 100 : (int)(read * 100 / length);
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

        var part = new StreamContent(stream);
        part.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        part.Headers.ContentDisposition = new ContentDispositionHeaderValue("form-data") { Name = "\"file\"", FileName = $"\"{fileName}\"" };
        return part;
    }

    /// <summary>Reads the fields this app uses out of the service's JSON answer.</summary>
    private static TranscriptionResult Parse(string json)
    {
        Response? response;
        try
        {
            response = JsonSerializer.Deserialize<Response>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new TranscriptionException("ElevenLabs answered with something that is not valid JSON.", responseBody: json, innerException: ex);
        }

        if (response?.Text is null)
            throw new TranscriptionException("ElevenLabs answered without a transcript.", responseBody: json);

        var words = (response.Words ?? [])
            .Select(w => new TranscriptWord(w.Text ?? "", w.Start, w.End, w.Type, w.SpeakerId))
            .ToList();

        return new TranscriptionResult(response.Text, response.LanguageCode, response.LanguageProbability, response.AudioDurationSecs, response.TranscriptionId, words, json);
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

    /// <summary>Builds an error message that says what to check.</summary>
    private static string Describe(HttpStatusCode status, string body)
    {
        var hint = status switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                $"ElevenLabs rejected the API key. Check {ElevenLabsOptions.ApiKeyVariable} and that the key is allowed to use Speech to Text.",
            HttpStatusCode.UnprocessableEntity or HttpStatusCode.BadRequest =>
                "ElevenLabs could not accept the request.",
            HttpStatusCode.TooManyRequests =>
                "ElevenLabs is rate limiting this account, or the plan's quota is used up.",
            _ => "ElevenLabs could not transcribe the file."
        };

        var detail = body.Length > 500 ? body[..500] + "..." : body;
        return $"{hint} (HTTP {(int)status}) {detail}".TrimEnd();
    }

    /// <summary>The part of the service's answer this app reads.</summary>
    private sealed record Response(
        string? Text,
        string? LanguageCode,
        double? LanguageProbability,
        double? AudioDurationSecs,
        string? TranscriptionId,
        List<ResponseWord>? Words);

    /// <summary>One word, spacing or sound item in the answer.</summary>
    private sealed record ResponseWord(string? Text, double? Start, double? End, string? Type, string? SpeakerId);
}
