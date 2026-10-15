namespace ClassScribe.Settings;

/// <summary>Settings for how the transcript text file is laid out.</summary>
public sealed class TranscriptOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Transcript";

    /// <summary>Start every paragraph with its position in the recording, such as [00:12:34].</summary>
    public bool IncludeTimestamps { get; init; } = true;

    /// <summary>A pause at least this long starts a new paragraph.</summary>
    public double ParagraphPauseSeconds { get; init; } = 2.0;

    /// <summary>A paragraph longer than this is split at the next sentence end.</summary>
    public int MaxParagraphCharacters { get; init; } = 900;
}
