using System.ComponentModel;

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
/// started with a plain argument. It also starts the one program that is not ClassScribe and shows a window: Chrome, for a
/// YouTube video's window on the record path.
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

    /// <summary>
    /// Starts Chrome, <paramref name="program"/>, for a YouTube video's window (design 4.1): <c>"&lt;chrome&gt;"</c> and
    /// <paramref name="arguments"/>, each quoted as Windows reads it back; a normal window; inheriting no handle; with this
    /// process's environment without ELEVENLABS_API_KEY or any ElevenLabs__ setting, so the key never reaches it; in
    /// <paramref name="workingFolder"/>, the YouTube feature's own folder, never the window's profile folder, which may hold a
    /// Google sign-in and which ClassScribe never touches; and outside the job ClassScribe runs in where the job
    /// allows it, so that the video plays on when ClassScribe's window is closed, as its recorder records on. Throws a
    /// <see cref="Win32Exception"/> carrying Windows' error when Windows does not start it.
    /// </summary>
    StartedProcess StartBrowser(string program, IReadOnlyList<string> arguments, string workingFolder);
}

/// <summary>How a start that Windows refuses is reported, so that the starter and the tests' stand-in for it say the same.</summary>
internal static class StartRefusal
{
    /// <summary>What the recorder is called in the message.</summary>
    public const string Recorder = "the recorder";

    /// <summary>What the alert window is called in the message.</summary>
    public const string AlertWindow = "the alert window";

    /// <summary>What Chrome, started for a YouTube video's window, is called in the message.</summary>
    public const string Browser = "Chrome";

    /// <summary>
    /// The exception for <paramref name="what"/> refused with Windows' <paramref name="error"/>, which it carries as its
    /// <see cref="Win32Exception.NativeErrorCode"/>: "Windows did not start the recorder: Access is denied (error 5)".
    /// </summary>
    public static Win32Exception For(string what, int error) =>
        new(error, $"Windows did not start {what}: {new Win32Exception(error).Message.TrimEnd('.')} (error {error})");
}
