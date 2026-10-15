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
/// JSON handling for the ElevenLabs answer, generated at compile time: snake_case names, read as
/// leniently as System.Text.Json's web defaults allow. Refit's generated client is built with it too.
/// </summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(SpeechToTextResponse))]
public sealed partial class ElevenLabsJson : JsonSerializerContext;
