namespace ClassScribe.Drive;

/// <summary>A folder on Drive: its id and name, and its address in a browser.</summary>
/// <param name="Id">Drive's id for it.</param>
/// <param name="Name">Its name now.</param>
public sealed record DriveFolder(string Id, string Name)
{
    /// <summary>The folder's address in a browser.</summary>
    public string Link => $"https://drive.google.com/drive/folders/{Id}";
}

/// <summary>A folder under ClassScribe's own folder on Drive, found or made, and where it is.</summary>
/// <param name="Folder">The folder.</param>
/// <param name="Path">Where it is, from ClassScribe's own folder down, by the names it has: ClassScribe/Mantiq/2026-10-17.</param>
public sealed record DriveFolderPath(DriveFolder Folder, string Path)
{
    /// <summary>Where it is, as Gulzar finds it in Drive: My Drive &gt; ClassScribe &gt; Mantiq &gt; 2026-10-17.</summary>
    public string Location => $"My Drive > {string.Join(" > ", Path.Split('/'))}";
}
