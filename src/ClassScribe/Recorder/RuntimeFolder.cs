namespace ClassScribe.Recorder;

/// <summary>
/// ClassScribe's own folder on this computer, %LOCALAPPDATA%\ClassScribe, which never roams and never syncs: the lock that
/// allows one recording at a time (<see cref="LockFile"/>), and the runs folder with each run's file and the request a
/// window hands the recorder it starts (<see cref="Runs"/>). It is also the recorder's working folder, so that nothing it
/// runs holds the user's folders or anything under OneDrive open. Nothing is made until it is needed.
/// </summary>
/// <param name="root">The folder to use instead, for tests; null for %LOCALAPPDATA%\ClassScribe.</param>
public sealed class RuntimeFolder(string? root = null)
{
    /// <summary>The name of the lock file, in <see cref="Root"/>.</summary>
    public const string LockFileName = "recording.lock";

    /// <summary>The name of the runs folder, in <see cref="Root"/>.</summary>
    public const string RunsFolderName = "runs";

    /// <summary>ClassScribe's own folder, as a full path.</summary>
    public string Root { get; } = root is null ? DefaultRoot() : Path.GetFullPath(root);

    /// <summary>The runs folder: one file per run, and the request files of runs about to start.</summary>
    public string Runs => Path.Combine(Root, RunsFolderName);

    /// <summary>The lock file, held open without sharing while a run captures (see <see cref="RecordingLock"/>).</summary>
    public string LockFile => Path.Combine(Root, LockFileName);

    /// <summary>True when the folder and its runs folder can be written, making them if needed (see <see cref="DescribeWriteProblem"/>).</summary>
    public bool CanWrite() => DescribeWriteProblem() is null;

    /// <summary>
    /// Makes the folder and its runs folder if needed and writes a file into each, deleted as soon as it is closed. Returns
    /// null when that works, or what went wrong, naming the folder, in words fit to show before a recording: without the
    /// folder a run records in the window that started it, without the lock and the report after a crash.
    /// </summary>
    public string? DescribeWriteProblem()
    {
        try
        {
            Directory.CreateDirectory(Runs);
            foreach (var folder in new[] { Root, Runs })
            {
                using var probe = new FileStream(Path.Combine(folder, $"write-check.{Guid.NewGuid():N}.tmp"), FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    bufferSize: 1, FileOptions.DeleteOnClose);
                probe.WriteByte(0);
            }

            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Deliberately not rethrown: whether the folder can be written is the answer asked for, and the reason is
            // handed back with it, for the caller to show and log.
            return $"{Root} cannot be written: {ex.Message}";
        }
    }

    /// <summary>%LOCALAPPDATA%\ClassScribe for the user ClassScribe runs as.</summary>
    private static string DefaultRoot()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify);
        if (string.IsNullOrEmpty(localAppData))
            throw new InvalidOperationException("Windows did not say where this user's local application data folder (%LOCALAPPDATA%) is, so ClassScribe has no folder of its own for its runs.");

        return Path.Combine(localAppData, "ClassScribe");
    }
}
