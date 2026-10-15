namespace ClassScribe.Transcription;

/// <summary>One item of a transcript: a word, the spacing between words, or a tagged sound.</summary>
public sealed record TranscriptWord(string Text, double? Start, double? End, string? Type, string? SpeakerId);

/// <summary>A finished transcript, with the service's raw response kept alongside the parsed fields.</summary>
public sealed record TranscriptionResult(
    string Text,
    string? LanguageCode,
    double? LanguageProbability,
    double? AudioDurationSecs,
    string? TranscriptionId,
    IReadOnlyList<TranscriptWord> Words,
    string RawJson);
