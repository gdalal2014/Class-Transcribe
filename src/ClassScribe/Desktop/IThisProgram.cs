namespace ClassScribe.Desktop;

/// <summary>
/// This program, as the process that runs it: the program's path and the folder of its files, which the commands printed
/// for later name so that they run as printed from any folder, and this process's id and start, which the run files name
/// the process that records a run by (architecture 4.3). Read once by the adapter, from Windows; a test gives a program of
/// its own.
/// </summary>
public interface IThisProgram
{
    /// <summary>The program's full path: ClassScribe.exe, or dotnet.exe running ClassScribe.dll; null when Windows does not say.</summary>
    string? ProcessPath { get; }

    /// <summary>The folder of ClassScribe's files, where ClassScribe.dll is.</summary>
    string AppFolder { get; }

    /// <summary>This process's id.</summary>
    int ProcessId { get; }

    /// <summary>When this process started, in UTC, which tells it from a later process given the same id.</summary>
    DateTimeOffset StartedUtc { get; }
}
