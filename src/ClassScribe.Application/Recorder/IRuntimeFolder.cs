namespace ClassScribe.Recorder;

/// <summary>
/// ClassScribe's own folder on this computer, %LOCALAPPDATA%\ClassScribe, which never roams and never syncs (architecture
/// 4.3; a test gives a temporary folder instead): the lock that allows one recording at a time (<see cref="LockFile"/>), and
/// the runs folder with each run's file and the request a window hands the recorder it starts (<see cref="Runs"/>). It is
/// also the recorder's working folder, so that nothing it runs holds the user's folders or anything under OneDrive open.
/// Nothing is made until it is needed.
/// </summary>
public interface IRuntimeFolder
{
    /// <summary>ClassScribe's own folder, as a full path.</summary>
    string Root { get; }

    /// <summary>The runs folder: one file per run, and the request files of runs about to start.</summary>
    string Runs { get; }

    /// <summary>The lock file, held open without sharing while a run captures (see <see cref="IRecordingLock"/>).</summary>
    string LockFile { get; }

    /// <summary>True when the folder and its runs folder can be written, making them if needed (see <see cref="DescribeWriteProblem"/>).</summary>
    bool CanWrite();

    /// <summary>
    /// Makes the folder and its runs folder if needed and writes a file into each, deleted as soon as it is closed. Returns
    /// null when that works, or what went wrong, naming the folder, in words fit to show before a recording: without the
    /// folder a run records in the window that started it, without the lock and the report after a crash.
    /// </summary>
    string? DescribeWriteProblem();
}
