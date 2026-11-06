namespace ClassScribe.Transcription;

/// <summary>
/// What a failed transcription says about an answer ElevenLabs had already sent, and charged for, when no transcript
/// came of it: where the answer is kept, or that it could be kept nowhere, and its transcription ID at ElevenLabs when
/// that is known. Transcribing again is charged again, so whoever reports the failure should say so. The workflow
/// marks the exception itself, in its <see cref="Exception.Data"/> under <see cref="ChargedKey"/>,
/// <see cref="KeptAnswerKey"/> and <see cref="TranscriptionIdKey"/>, so the mark travels with it through every
/// <c>throw;</c>, whatever its type.
/// </summary>
/// <param name="KeptIn">The file that holds the answer, or null when it could be kept nowhere.</param>
/// <param name="TranscriptionId">
/// The answer's transcription ID at ElevenLabs, with which the transcript can be fetched from there, or null when it is
/// not known: an answer that could not be read gives none, and ElevenLabs may send an answer without one.
/// </param>
public sealed record ChargedAnswer(string? KeptIn, string? TranscriptionId)
{
    /// <summary>The <see cref="Exception.Data"/> key that is true when ElevenLabs had charged for an answer that did not become a transcript.</summary>
    public const string ChargedKey = "ClassScribe.Charged";

    /// <summary>The <see cref="Exception.Data"/> key that holds the path of the file that answer was kept in, when it was kept.</summary>
    public const string KeptAnswerKey = "ClassScribe.KeptAnswer";

    /// <summary>The <see cref="Exception.Data"/> key that holds that answer's transcription ID at ElevenLabs, when it is known.</summary>
    public const string TranscriptionIdKey = "ClassScribe.TranscriptionId";

    /// <summary>
    /// Marks <paramref name="exception"/> as a failure that came after ElevenLabs had charged for its answer, which is
    /// kept in <paramref name="keptIn"/>, or nowhere when that is null, and whose transcription ID at ElevenLabs is
    /// <paramref name="transcriptionId"/>, or not known when that is null.
    /// </summary>
    public static void Mark(Exception exception, string? keptIn, string? transcriptionId)
    {
        exception.Data[ChargedKey] = true;
        if (keptIn is not null) exception.Data[KeptAnswerKey] = keptIn;
        if (transcriptionId is not null) exception.Data[TranscriptionIdKey] = transcriptionId;
    }

    /// <summary>The charged answer <paramref name="exception"/> is marked with, or null when it came before ElevenLabs charged for anything.</summary>
    public static ChargedAnswer? Of(Exception exception) =>
        exception.Data[ChargedKey] is true
            ? new ChargedAnswer(exception.Data[KeptAnswerKey] as string, exception.Data[TranscriptionIdKey] as string)
            : null;
}
