namespace ClassScribe.Settings;

/// <summary>Settings for how the transcript text file is laid out.</summary>
public sealed class TranscriptOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Transcript";

    /// <summary>Start every paragraph with its position in the recording, such as [00:12:34].</summary>
    public bool IncludeTimestamps { get; init; } = true;

    /// <summary>
    /// A pause between words at least this long, in seconds, starts a new paragraph. 0 turns these breaks
    /// off, so that paragraphs break only where they grow long and at marks. A value below 0 is refused when
    /// ClassScribe starts (see <c>SettingsCheck</c>).
    /// </summary>
    public double ParagraphPauseSeconds { get; init; } = 2.0;

    /// <summary>
    /// A paragraph longer than this, in characters, is split at the next sentence end. 0 turns this splitting
    /// off, so that paragraphs break only at pauses and marks. A value below 0 is refused when ClassScribe
    /// starts (see <c>SettingsCheck</c>).
    /// </summary>
    public int MaxParagraphCharacters { get; init; } = 900;

    /// <summary>
    /// In a recording of the class alone, silence while nothing plays is not recorded. Where at least this many seconds
    /// of it were left out, the transcript gets a heading saying so, such as "About 3 minutes with nothing playing were
    /// left out here". 0 writes no such heading. A value below 0 is refused when ClassScribe starts (see <c>SettingsCheck</c>).
    /// </summary>
    public int ShowGapsLongerThanSeconds { get; init; } = 60;
}
