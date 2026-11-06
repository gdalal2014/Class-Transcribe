namespace ClassScribe.Desktop;

/// <summary>
/// ClassScribe's own processes, by this process's id and its program's name: ClassScribe.exe for the installed program,
/// or dotnet under dotnet run, where every process of that program counts as ClassScribe's own, as the loopback provider
/// counts them when it lists the apps playing sound.
/// </summary>
public sealed class OwnProcesses : IOwnProcesses
{
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
            && (string.Equals(processName, ProgramFiles.Name, StringComparison.OrdinalIgnoreCase)
                || string.Equals(processName, _ownProcessName, StringComparison.OrdinalIgnoreCase)));
}
