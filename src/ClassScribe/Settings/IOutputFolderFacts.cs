namespace ClassScribe.Settings;

/// <summary>
/// What the settings check needs to know about the folders on this computer to warn about where the recordings go
/// (layout-config 2): whether a folder holds another of a given name, a drive's label, and the folders Dropbox keeps in
/// sync. Each answer is only advice, so none of them throws.
/// </summary>
public interface IOutputFolderFacts
{
    /// <summary>True when <paramref name="folder"/> holds a folder named <paramref name="name"/>.</summary>
    bool HasFolder(string folder, string name);

    /// <summary>The label of the drive whose root is <paramref name="root"/>, such as G:\; null when it has none, or it cannot be read.</summary>
    string? VolumeLabel(string root);

    /// <summary>The folders Dropbox keeps in sync, as its info.json files name them; empty when there are none, or they cannot be read.</summary>
    IReadOnlyList<string> DropboxFolders();
}
