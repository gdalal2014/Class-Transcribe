namespace ClassScribe.Desktop;

/// <summary>Whether a file is there, behind an interface so that no test looks into the real program folders, PATH or winget's links.</summary>
public interface IFileProbe
{
    /// <summary>True when a file is at <paramref name="path"/>; false for none, for a folder, and for a path that cannot be one.</summary>
    bool FileExists(string path);
}

/// <summary>Looks at the disk itself.</summary>
public sealed class FileProbe : IFileProbe
{
    /// <inheritdoc />
    public bool FileExists(string path) => File.Exists(path);
}
