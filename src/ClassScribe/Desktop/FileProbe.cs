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
    public string FullPath(string path) => Path.GetFullPath(path);

    /// <inheritdoc />
    public string? VersionOf(string path) =>
        File.Exists(path) && FileVersionInfo.GetVersionInfo(path).ProductVersion is { Length: > 0 } version ? version.Trim() : null;
}
