using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using ClassScribe.Commands;

namespace ClassScribe.Desktop;

/// <summary>A process ClassScribe started.</summary>
/// <param name="ProcessId">Windows' id for the process.</param>
/// <param name="StartedUtc">
/// When Windows made the process, as it records it. With <paramref name="ProcessId"/> it tells the process from a later
/// one that Windows gives the same id, once this one has ended.
/// </param>
/// <param name="ExitCode">
/// The process's exit code once it has ended, and null while it runs. It can be asked at any time, from any thread, also
/// long after the process ended: the process's handle is held for it as long as this record is kept.
/// </param>
public sealed record StartedProcess(int ProcessId, DateTimeOffset StartedUtc, Func<int?> ExitCode);

/// <summary>
/// Starts the copies of ClassScribe that a run needs: the recorder, with no window, which records a class whatever happens
/// to the window that started it; and the alert window, which the recorder opens, minimized and without taking the focus,
/// when something needs Gulzar while no window shows the recording. Each is this very program, from the same path,
/// started with a plain argument.
/// </summary>
public interface IProcessStarter
{
    /// <summary>
    /// Windows' error ERROR_ACCESS_DENIED, which a start fails with when it asks for breakaway in a job that does not
    /// allow it (and for some other reasons too).
    /// </summary>
    const int AccessDenied = 5;

    /// <summary>
    /// Starts the recorder for run <paramref name="runId"/>: <c>"&lt;exe&gt;" --recorder &lt;run id&gt;</c>, with no
    /// window, in a process group of its own, inheriting no handle, with this process's environment as it is (so
    /// ELEVENLABS_API_KEY, and an ElevenLabs__BaseUrl pointing at a local stub, reach it that way and no other), in
    /// ClassScribe's own folder under %LOCALAPPDATA%. With <paramref name="breakaway"/>, outside the job ClassScribe runs
    /// in (see <see cref="JobFacts.Breakaway"/>). Throws a <see cref="Win32Exception"/> carrying Windows' error when
    /// Windows does not start it, <see cref="AccessDenied"/> among them, and an <see cref="ArgumentException"/> for a run
    /// id that is not one.
    /// </summary>
    StartedProcess StartRecorder(string runId, bool breakaway);

    /// <summary>
    /// Starts the alert window for run <paramref name="runId"/>: <c>"&lt;exe&gt;" attach --alert &lt;run id&gt;</c>, an
    /// ordinary ClassScribe window with a console of its own, shown minimized without taking the focus, so that it only
    /// appears on the taskbar. Started by the recorder, it most likely begins with Ctrl+C ignored, which Windows passes on
    /// from the recorder's process group; the attach command turns Ctrl+C back on if it is to work there. Throws as
    /// <see cref="StartRecorder"/> does.
    /// </summary>
    StartedProcess StartAlertWindow(string runId);
}

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

/// <summary>How a start that Windows refuses is reported, so that the starter and the tests' stand-in for it say the same.</summary>
internal static class StartRefusal
{
    /// <summary>What the recorder is called in the message.</summary>
    public const string Recorder = "the recorder";

    /// <summary>What the alert window is called in the message.</summary>
    public const string AlertWindow = "the alert window";

    /// <summary>
    /// The exception for <paramref name="what"/> refused with Windows' <paramref name="error"/>, which it carries as its
    /// <see cref="Win32Exception.NativeErrorCode"/>: "Windows did not start the recorder: Access is denied (error 5)".
    /// </summary>
    public static Win32Exception For(string what, int error) =>
        new(error, $"Windows did not start {what}: {new Win32Exception(error).Message.TrimEnd('.')} (error {error})");
}
