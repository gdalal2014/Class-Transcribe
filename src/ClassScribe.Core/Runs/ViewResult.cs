using ClassScribe.Recorder;

namespace ClassScribe.Runs;

/// <summary>How a window shows a run.</summary>
public enum ViewMode
{
    /// <summary>The window that started the run: it opens the results at the end.</summary>
    Owner,

    /// <summary>Another window showing the run, such as ClassScribe started again while it records.</summary>
    Viewer,

    /// <summary>A window the recorder opened by itself: it ignores every key until Enter.</summary>
    Alert
}

/// <summary>Why a window stopped showing a run.</summary>
public enum ViewEnd
{
    /// <summary>The run ended; its outcome says how.</summary>
    RunEnded,

    /// <summary>Esc: the window left the view, and the run carries on.</summary>
    Left,

    /// <summary>The recorder ended unexpectedly while the window showed it.</summary>
    RecorderLost,

    /// <summary>The recorder stopped answering, and the window stopped waiting for it.</summary>
    NotAnswering,

    /// <summary>The recorder was started by another build of ClassScribe, which speaks another protocol.</summary>
    Refused
}

/// <summary>How showing a run ended.</summary>
/// <param name="End">Why the window stopped showing it.</param>
/// <param name="ExitCode">The exit code the run ended with, when it ended; null otherwise.</param>
/// <param name="Outcome">How the run ended, when it ended; null otherwise.</param>
public sealed record ViewResult(ViewEnd End, int? ExitCode, RunOutcome? Outcome);
