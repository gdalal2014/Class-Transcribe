using ClassScribe.Recorder;

namespace ClassScribe.Commands;

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

/// <summary>Shows a run in this window: what happened so far, then what happens, with the recording keys passed on to it.</summary>
public interface IRunViewer
{
    /// <summary>Shows <paramref name="run"/> as <paramref name="mode"/> says until it ends, the window leaves it, or the recorder is lost.</summary>
    Task<ViewResult> ShowAsync(RunRecord run, ViewMode mode, CancellationToken cancellationToken);
}

/// <summary>
/// Starts a run: one recording at a time, the question whether to continue a recent one, the choice between a separate
/// recorder and this window, and the view of the run. Returns the exit code.
/// </summary>
public interface IRecordStarter
{
    /// <summary>Starts the run <paramref name="request"/> describes, from <paramref name="origin"/>, and returns its exit code.</summary>
    Task<int> RunAsync(RecordRequest request, StartOrigin origin);
}

/// <summary>
/// One run itself, in a separate recorder or in the window: record, stop, and transcribe when asked. The record command
/// does it; the starter resolves it in a scope of its own when the run is in the window, and the recorder's host when it
/// is in a separate recorder.
/// </summary>
public interface IRecordRun
{
    /// <summary>Records as <paramref name="request"/> says, and returns the exit code.</summary>
    Task<int> RunAsync(RecordRequest request);
}

/// <summary>What the report at the start offers to do about a run that needs a decision.</summary>
public enum StartupAction
{
    /// <summary>C: continue the recording as its next part.</summary>
    Continue,

    /// <summary>T: transcribe what was recorded.</summary>
    Transcribe,

    /// <summary>O: open the transcript made while no window showed the run.</summary>
    Open,

    /// <summary>F: forget the run, doing nothing about it.</summary>
    Forget
}

/// <summary>What was chosen in the report at the start.</summary>
/// <param name="RunId">The run the choice is about.</param>
/// <param name="Action">What to do about it.</param>
public sealed record StartupChoice(string RunId, StartupAction Action);

/// <summary>
/// The report at the start: runs that ended unexpectedly, transcriptions interrupted, and outcomes nobody has seen yet,
/// with what can be done about them; and the runs going on now, for the menu's header.
/// </summary>
public interface IStartupReport
{
    /// <summary>
    /// Shows the report. When <paramref name="interactive"/> is true it offers what can be done, carries out the choice
    /// made itself, and returns that choice, or null for none, so that the caller knows what was done and does not do it
    /// again; otherwise it prints the commands that would do it, and returns null.
    /// </summary>
    Task<StartupChoice?> ShowAsync(bool interactive);

    /// <summary>The lines about runs going on now, for the menu's header, such as a recording or a transcription in the background.</summary>
    IReadOnlyList<string> HeaderLines();

    /// <summary>The run recording now that the menu shows at once, or null when none is.</summary>
    RunRecord? RecordingNow();
}

/// <summary>A report at the start that reports nothing: it stands in until the real one is registered.</summary>
public sealed class NoStartupReport : IStartupReport
{
    /// <inheritdoc />
    public Task<StartupChoice?> ShowAsync(bool interactive) => Task.FromResult<StartupChoice?>(null);

    /// <inheritdoc />
    public IReadOnlyList<string> HeaderLines() => [];

    /// <inheritdoc />
    public RunRecord? RecordingNow() => null;
}

/// <summary>Shows how a run ended, at its end.</summary>
public interface IResultShower
{
    /// <summary>Shows <paramref name="outcome"/>, and opens the results when <paramref name="open"/> is true and the settings ask for it.</summary>
    void Show(RunOutcome outcome, bool open);
}
