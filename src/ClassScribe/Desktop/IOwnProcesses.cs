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

/// <summary>
/// ClassScribe's own processes, by this process's id and its program's name: ClassScribe.exe for the installed program,
/// or dotnet under dotnet run, where every process of that program counts as ClassScribe's own, as the loopback provider
/// counts them when it lists the apps playing sound.
/// </summary>
public sealed class OwnProcesses : IOwnProcesses
{
    /// <summary>The name of ClassScribe's program, which is ClassScribe's own however it was started.</summary>
    public const string ProgramName = "ClassScribe";

    private readonly int _ownProcessId;
    private readonly string? _ownProcessName;

    /// <summary>The own processes of this process and its program.</summary>
    public OwnProcesses()
        : this(Environment.ProcessId, Path.GetFileNameWithoutExtension(Environment.ProcessPath))
    {
    }

    /// <summary>The own processes of the process <paramref name="ownProcessId"/> and its program <paramref name="ownProcessName"/>. Tests pass their own.</summary>
    internal OwnProcesses(int ownProcessId, string? ownProcessName)
    {
        _ownProcessId = ownProcessId;
        _ownProcessName = ownProcessName;
    }

    /// <inheritdoc />
    public bool IsOwn(int processId, string? processName) =>
        processId == _ownProcessId
        || (processName is not null
            && (string.Equals(processName, ProgramName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(processName, _ownProcessName, StringComparison.OrdinalIgnoreCase)));
}
