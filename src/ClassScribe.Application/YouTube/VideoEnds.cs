using ClassScribe.Recorder;

namespace ClassScribe.YouTube;

/// <summary>
/// How a YouTube video's recording ended, in its source file's words (design 2.3 and 4.4), from what ended its run: whether
/// its end is confirmed, so that it holds the whole video, or it may be cut short and the same command records it again. The
/// record path is attended, so the window closed and Q are Gulzar deciding the video is done, even midway, and count as
/// confirmed, as does the window moving on to the next video; anything else may be cut short. The words follow "but" in
/// the plan's line for a recording made before ("fetched before (...), but ended after 2 minutes of silence, so it may be cut
/// short: fetched again").
/// </summary>
public static class VideoEnds
{
    /// <summary>What a video's source file says while its recording has not ended, and keeps when its recorder was lost.</summary>
    public const string NotFinished = "its recording did not finish";

    /// <summary>What a video's source file says when its window was gone before the recording began.</summary>
    public const string ClosedBeforeItBegan = "the video window was closed before the recording began";

    /// <summary>The words and whether the end is confirmed, for a run ended by <paramref name="endedBy"/> (see <see cref="RunEndCause"/>); null for a run with no cause written.</summary>
    public static (string Ended, bool Confirmed) Of(string? endedBy) => endedBy switch
    {
        RunEndCause.VideoClosed => ("the video window was closed", true),
        RunEndCause.VideoEnded => ("the window moved on to the next video", true),
        RunEndCause.Q => ("stopped with Q", true),
        RunEndCause.ClassStarted => ("stopped so that a class could record", false),
        RunEndCause.VideoSilent => ("ended after 2 minutes of silence", false),
        RunEndCause.NothingPlayed => ("nothing played", false),
        RunEndCause.VideoLimit => ("stopped at the longest a video is recorded for (YouTube:LongestVideoHours)", false),
        RunEndCause.SafetyStop => ("stopped by the safety stop (Recording:StopAfterHours)", false),
        RunEndCause.Windows => ("Windows ended the session", false),
        RunEndCause.WindowClosed => ("ClassScribe's window was closed", false),
        RunEndCause.Error => ("the recording failed", false),
        _ => (NotFinished, false)
    };
}

/// <summary>
/// A YouTube video's window was gone when its run was to begin recording (design 4.3): the run fails closed, recording
/// nothing, rather than take any other Chrome.
/// </summary>
public sealed class VideoWindowClosedException() : InvalidOperationException("The video window was closed before the recording began");
