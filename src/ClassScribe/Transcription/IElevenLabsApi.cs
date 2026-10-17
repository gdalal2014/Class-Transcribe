using Refit;

namespace ClassScribe.Transcription;

/// <summary>
/// The ElevenLabs endpoints this app calls, declared for Refit: speech to text, for which Refit builds the multipart
/// request (one part per field, one <c>keyterms</c> part per glossary term, and the audio streamed from disk), and the
/// account's subscription, read for the credits a transcript cost.
/// </summary>
public interface IElevenLabsApi
{
    /// <summary>
    /// Reads the account's subscription (GET /v1/user/subscription): the credits used and allowed in the current period,
    /// when the period resets, and any usage-based overage. It needs the key's User permission, which a key restricted to
    /// Speech to Text lacks, so the response comes back whatever its status: a refusal is read, never thrown.
    /// </summary>
    [Get("/v1/user/subscription")]
    Task<HttpResponseMessage> GetSubscriptionAsync([Header("xi-api-key")] string apiKey, CancellationToken cancellationToken);

    /// <summary>
    /// Uploads audio for transcription. The response comes back whatever its status, so the caller can
    /// keep the JSON exactly as it arrived and turn a failure into a message that says what to check.
    /// Optional fields passed as null are left out of the request.
    /// </summary>
    // Refit needs the boundary as a constant. This one is long and random enough never to occur in audio.
    [Multipart("ClassScribe-5f0c6a2e9d1b4c7e8a3f2b6d0e9c4a1f")]
    [Post("/v1/speech-to-text")]
    Task<HttpResponseMessage> TranscribeAsync(
        [Header("xi-api-key")] string apiKey,
        [AliasAs("model_id")] string modelId,
        [AliasAs("tag_audio_events")] string tagAudioEvents,
        [AliasAs("timestamps_granularity")] string timestampsGranularity,
        [AliasAs("no_verbatim")] string? noVerbatim,
        [AliasAs("language_code")] string? languageCode,
        [AliasAs("keyterms")] IEnumerable<string> keyterms,
        [AliasAs("file")] StreamPart file,
        CancellationToken cancellationToken);
}

/// <summary>The Refit settings <see cref="IElevenLabsApi"/> is used with, in the app and in the tests.</summary>
public static class ElevenLabsApiSettings
{
    /// <summary>
    /// Refit's defaults, except that a request that cannot be sent (a broken connection, the app's own
    /// time limit) fails with the original exception instead of Refit's wrapper. That lets the client tell
    /// a network error, which is worth another attempt, from its own timeout, which is not.
    /// </summary>
    public static RefitSettings Create() => new() { TransportExceptionFactory = (_, error, _) => error };
}
