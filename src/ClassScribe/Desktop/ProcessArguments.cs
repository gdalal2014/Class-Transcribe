using System.Diagnostics.CodeAnalysis;
using ClassScribe.Commands;

namespace ClassScribe.Desktop;

/// <summary>
/// The words on the command lines of the processes a run starts, so that the code that starts them and the code that
/// reads them agree: <c>ClassScribe.exe --recorder &lt;run id&gt;</c> for the recorder, which Program takes before the
/// command line is parsed, and <c>ClassScribe.exe attach --alert &lt;run id&gt;</c> for the alert window, the hidden
/// attach command.
/// </summary>
public static class ProcessArguments
{
    /// <summary>What makes ClassScribe the recorder of the run named after it.</summary>
    public const string Recorder = "--recorder";

    /// <summary>The hidden command that shows a run that is recording.</summary>
    public const string Attach = "attach";

    /// <summary>The attach command's option for the alert window, followed by the run's id: no key counts until Enter.</summary>
    public const string Alert = "--alert";

    /// <summary>
    /// Whether <paramref name="runId"/> is a run id as <see cref="RunIds.New"/> makes them, such as
    /// 20261016-230002Z-0a1b2c3d: the same rule (<see cref="RunIds.IsWellFormed"/>) as the run files, the stop event and
    /// the command line keep, so a recorder is never started with an id it cannot use for its run file, its pipe and its
    /// stop event. A run id goes on command lines and into the names of files, pipes and events, where a space, a quote, a
    /// character a shell reads or a backslash would mean something else, and a leading dash would read as an option; none
    /// of those can be in one.
    /// </summary>
    public static bool IsRunId([NotNullWhen(true)] string? runId) => RunIds.IsWellFormed(runId);
}
