namespace ClassScribe.Desktop;

/// <summary>
/// What the use cases ask of a file or a folder on disk, behind an interface so that no test looks into the real program
/// folders, PATH, winget's links or the recordings: whether it is there, when it was last written, how long it is, its
/// full path, and a program's version. Each answer is the file system's own, as .NET gives it, so that a caller's handling
/// of a file that is not there keeps its meaning.
/// </summary>
public interface IFileProbe
{
    /// <summary>True when a file is at <paramref name="path"/>; false for none, for a folder, and for a path that cannot be one.</summary>
    bool FileExists(string path);

    /// <summary>True when a folder is at <paramref name="path"/>; false for none, for a file, and for a path that cannot be one.</summary>
    bool DirectoryExists(string path);

    /// <summary>
    /// When the file at <paramref name="path"/> was last written, in UTC, as the file system records it; for a file that is
    /// not there, the earliest time the file system has (midnight on 1 January 1601), never an error.
    /// </summary>
    DateTime LastWriteUtc(string path);

    /// <summary>The length of the file at <paramref name="path"/>, in bytes.</summary>
    /// <exception cref="System.IO.FileNotFoundException">There is no file at <paramref name="path"/>, its folder being there or not.</exception>
    long Length(string path);

    /// <summary>
    /// The full path of <paramref name="path"/>: a relative one resolved against the current folder, and <c>.</c>, <c>..</c>
    /// and doubled separators folded away.
    /// </summary>
    string FullPath(string path);

    /// <summary>The product version a program's file at <paramref name="path"/> carries, as Windows shows it; null when there is none, or no such file.</summary>
    string? VersionOf(string path);
}
