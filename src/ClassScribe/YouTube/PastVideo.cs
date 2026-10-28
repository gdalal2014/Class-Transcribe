using ClassScribe.Storage;

namespace ClassScribe.YouTube;

/// <summary>A recording of a video made before, as its source file and its files say.</summary>
/// <param name="Files">The recording's files.</param>
/// <param name="Source">Its source file.</param>
/// <param name="HasAudio">True when its audio is in its folder.</param>
/// <param name="HasTranscript">True when it has a transcript.</param>
/// <param name="KeptAnswer">An answer ElevenLabs was paid for that did not become a transcript, kept beside it; null when none is.</param>
public sealed record PastVideo(RecordingFiles Files, VideoSource Source, bool HasAudio, bool HasTranscript, string? KeptAnswer)
{
    /// <summary>True when its audio holds the whole video: a download, or a recording whose end was confirmed.</summary>
    public bool IsWhole => HasAudio && Source.EndConfirmed;
}

/// <summary>What was done before with one video (design 2.6).</summary>
public enum PastKind
{
    /// <summary>Never fetched: it is fetched.</summary>
    None,

    /// <summary>A recording of it that holds the whole video has a transcript: it is left out.</summary>
    Transcribed,

    /// <summary>A recording of it holds the whole video and has no transcript: it is not fetched again, and its transcript is made when asked.</summary>
    Whole,

    /// <summary>Its recordings hold no audio, or may be cut short, transcribed or not: it is fetched again, as a new recording.</summary>
    Unfinished
}

/// <summary>What was done before with one video, with the recording that counts, and the words the plan shows.</summary>
/// <param name="Kind">What was done.</param>
/// <param name="Recording">The recording that counts: the transcribed one, or the whole one; null otherwise.</param>
/// <param name="Words">What the plan says of it, such as "already transcribed on 12 Oct: Risala_2026-10-12_14-02-11Z.md".</param>
public sealed record PastVerdict(PastKind Kind, PastVideo? Recording, string Words);
