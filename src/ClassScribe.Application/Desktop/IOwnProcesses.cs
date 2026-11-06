namespace ClassScribe.Desktop;

/// <summary>
/// Tells ClassScribe's own processes from other apps': this process, whether it is the window or the recorder, and
/// any other process of ClassScribe's program. A recording's own capture holds the microphone for the whole class and
/// shows up as a session on it, which must never count as an app listening.
/// </summary>
public interface IOwnProcesses
{
    /// <summary>
    /// True when the process <paramref name="processId"/>, whose program is named <paramref name="processName"/> (without
    /// .exe, null when it could not be read), is ClassScribe: this process, or another process of the same program.
    /// </summary>
    bool IsOwn(int processId, string? processName);
}
