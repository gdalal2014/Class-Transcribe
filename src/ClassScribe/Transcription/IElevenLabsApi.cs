using Refit;

namespace ClassScribe.Transcription;

/// <summary>
/// The ElevenLabs speech-to-text endpoint, declared for Refit. Refit builds the multipart request: one
/// part per field, one <c>keyterms</c> part per glossary term, and the audio streamed from disk.
/// </summary>
public interface IElevenLabsApi
{
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
