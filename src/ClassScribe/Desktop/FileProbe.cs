using System.Diagnostics;

namespace ClassScribe.Desktop;

/// <summary>Looks at the disk itself.</summary>
public sealed class FileProbe : IFileProbe
{
    /// <inheritdoc />
    public bool FileExists(string path) => File.Exists(path);

    /// <inheritdoc />
    public bool DirectoryExists(string path) => Directory.Exists(path);

    /// <inheritdoc />
    public DateTime LastWriteUtc(string path) => File.GetLastWriteTimeUtc(path);

    /// <inheritdoc />
    public long Length(string path) => new FileInfo(path).Length;

    /// <inheritdoc />
    public (long Length, DateTime LastWriteUtc)? Describe(string path)
    {
        // One FileInfo: Exists reads the file's facts once, and Length and LastWriteTimeUtc come from that same read.
        var info = new FileInfo(path);
        return info.Exists ? (info.Length, info.LastWriteTimeUtc) : null;
    }

    /// <inheritdoc />
    public string FullPath(string path) => Path.GetFullPath(path);

    /// <inheritdoc />
    public string? VersionOf(string path) =>
        File.Exists(path) && FileVersionInfo.GetVersionInfo(path).ProductVersion is { Length: > 0 } version ? version.Trim() : null;
}
