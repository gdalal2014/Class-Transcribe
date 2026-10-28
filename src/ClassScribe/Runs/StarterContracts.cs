using ClassScribe.Recorder;

namespace ClassScribe.Runs;

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
