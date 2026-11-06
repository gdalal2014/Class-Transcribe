namespace ClassScribe.Runs;

/// <summary>What the report at the start offers to do about a run that needs a decision, or about what waits for Google Drive.</summary>
public enum StartupAction
{
    /// <summary>C: continue the recording as its next part.</summary>
    Continue,

    /// <summary>T: transcribe what was recorded.</summary>
    Transcribe,

    /// <summary>O: open the transcript made while no window showed the run.</summary>
    Open,

    /// <summary>F: forget the run, doing nothing about it.</summary>
    Forget,

    /// <summary>U: upload what waits to go up to Google Drive now.</summary>
    Upload,

    /// <summary>V: make a lesson's video now, free, from its camera pieces and its sound (teach-video 6.9).</summary>
    MakeVideo
}

/// <summary>What was chosen in the report at the start.</summary>
/// <param name="RunId">The run the choice is about; null for U, which is about what waits for Google Drive.</param>
/// <param name="Action">What to do about it.</param>
public sealed record StartupChoice(string? RunId, StartupAction Action);
