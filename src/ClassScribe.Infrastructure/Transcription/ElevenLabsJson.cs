using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClassScribe.Transcription;

/// <summary>The part of ElevenLabs' speech-to-text answer this app reads.</summary>
public sealed record SpeechToTextResponse(
    string? Text,
    string? LanguageCode,
    double? LanguageProbability,
    double? AudioDurationSecs,
    string? TranscriptionId,
    List<SpeechToTextWord>? Words);

/// <summary>One word, spacing or sound item in the answer.</summary>
public sealed record SpeechToTextWord(string? Text, double? Start, double? End, string? Type, string? SpeakerId);

/// <summary>
/// The part of ElevenLabs' subscription answer (GET /v1/user/subscription) this app reads: the credits used and allowed in
/// the current period, when the period resets, and the usage-based overage, which ElevenLabs gives as an object whose
/// amount is written as text, kept as JSON here so that a number there reads too.
/// </summary>
public sealed record SubscriptionResponse(
    long? CharacterCount,
    long? CharacterLimit,
    long? NextCharacterCountResetUnix,
    string? Status,
    string? Tier,
    bool? CanExtendCharacterLimit,
    JsonElement? CurrentOverage);

/// <summary>
/// JSON handling for the ElevenLabs answers, generated at compile time: snake_case names, read as
/// leniently as System.Text.Json's web defaults allow. Refit's generated client is built with it too.
/// </summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(SpeechToTextResponse))]
[JsonSerializable(typeof(SubscriptionResponse))]
public sealed partial class ElevenLabsJson : JsonSerializerContext;
