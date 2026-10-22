using System.Diagnostics;

namespace ClassScribe.Desktop;

/// <summary>
/// Whether a file is there, and a program's version, behind an interface so that no test looks into the real program
/// folders, PATH or winget's links.
/// </summary>
public interface IFileProbe
{
    /// <summary>True when a file is at <paramref name="path"/>; false for none, for a folder, and for a path that cannot be one.</summary>
    bool FileExists(string path);

    /// <summary>The product version a program's file at <paramref name="path"/> carries, as Windows shows it; null when there is none, or no such file.</summary>
    string? VersionOf(string path);
}

/// <summary>Looks at the disk itself.</summary>
public sealed class FileProbe : IFileProbe
{
    /// <inheritdoc />
    public bool FileExists(string path) => File.Exists(path);

    /// <inheritdoc />
    public string? VersionOf(string path) =>
        File.Exists(path) && FileVersionInfo.GetVersionInfo(path).ProductVersion is { Length: > 0 } version ? version.Trim() : null;
}
