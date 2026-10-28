namespace ClassScribe.Camera;

/// <summary>What became of a lesson's video.</summary>
public enum LessonVideoOutcome
{
    /// <summary>Made, checked and renamed into place.</summary>
    Made,

    /// <summary>Not made: the camera process said why, ended without a word, gave no progress for too long, or did not start. The pieces are kept.</summary>
    Failed,

    /// <summary>Cancelled, as by Ctrl+C after the stop: the unfinished video is deleted and the pieces kept.</summary>
    Cancelled
}

/// <summary>What became of a lesson's video, and the video when it was made.</summary>
/// <param name="Outcome">What became of it.</param>
/// <param name="Path">The video, when made.</param>
/// <param name="Length">Its length, when made.</param>
/// <param name="Bytes">Its size, when made.</param>
/// <param name="Why">Why it was not made, in words, with the command that makes it again where that helps; null when made.</param>
public sealed record LessonVideoResult(LessonVideoOutcome Outcome, string? Path = null, TimeSpan Length = default, long Bytes = 0, string? Why = null);
