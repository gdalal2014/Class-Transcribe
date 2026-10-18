using System.Text.Json;
using ClassScribe.Recorder;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

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

/// <summary>
/// ClassScribe's own folder in My Drive, which mirrors the recordings folder: a folder per course and in it one per class's
/// date folder, or a folder per name for a recording of the old flat layout (see ClassFolders.DriveLevelsOf). ClassScribe's
/// own folder is made once, named after Drive:FolderName, with ClassScribe's mark in its app properties ("private to the
/// requesting app"), and found again by its remembered id, checked with Drive (not in the trash, still a folder), else by
/// that mark, so it is found even when Gulzar renames or moves it in Drive. Each folder under it is found by its remembered
/// id, else by its name under its parent, else made there. With the drive.file scope a folder he made by hand is invisible
/// to ClassScribe and never used. The ids are remembered in %LOCALAPPDATA%\ClassScribe\drive\folders.json, the folders
/// under the root by their path under it. Folders are made only by an upload that holds the upload lock, so two processes
/// never make the same one twice. Nothing on Drive is ever deleted, trashed or changed here: a folder in the trash stays
/// there, and a new one is made. One per run.
/// </summary>
public sealed class DriveFolders(DriveClient drive, IOptionsSnapshot<DriveOptions> options, RuntimeFolder runtime, ILogger<DriveFolders> logger)
{
    /// <summary>The MIME type of a Drive folder.</summary>
    public const string FolderType = "application/vnd.google-apps.folder";

    /// <summary>The app property that marks ClassScribe's folders.</summary>
    public const string MarkKey = "classscribe";

    /// <summary>The value of <see cref="MarkKey"/> on ClassScribe's own folder in My Drive.</summary>
    public const string RootMark = "root";

    /// <summary>The name of the folder, under ClassScribe's own folder on this computer, that holds what it remembers of Drive.</summary>
    public const string DriveFolderName = "drive";

    /// <summary>The search for ClassScribe's own folder by its mark, leaving out what is in the trash.</summary>
    public const string RootQuery = $"mimeType = '{FolderType}' and appProperties has {{ key='{MarkKey}' and value='{RootMark}' }} and trashed = false";

    /// <summary>The file that remembers the folders' ids: %LOCALAPPDATA%\ClassScribe\drive\folders.json.</summary>
    public string RememberedFile => Path.Combine(runtime.Root, DriveFolderName, "folders.json");

    /// <summary>The search for the folder named <paramref name="name"/> in the folder <paramref name="parentId"/>, leaving out what is in the trash.</summary>
    public static string FolderQuery(string parentId, string name) =>
        $"'{Escape(parentId)}' in parents and name = '{Escape(name)}' and mimeType = '{FolderType}' and trashed = false";

    /// <summary>The search for everything in the folder <paramref name="folderId"/> that is not in the trash: with drive.file, only what ClassScribe made.</summary>
    public static string ChildrenQuery(string folderId) => $"'{Escape(folderId)}' in parents and trashed = false";

    /// <summary>A value written into a search, with its backslashes and single quotes escaped, as Drive's search asks.</summary>
    public static string Escape(string value) => value.Replace(@"\", @"\\", StringComparison.Ordinal).Replace("'", @"\'", StringComparison.Ordinal);

    /// <summary>ClassScribe's folder in My Drive, found by its remembered id or by its mark; null when there is none (in the trash counts as none). Makes nothing.</summary>
    public async Task<DriveFolder?> FindRootAsync(CancellationToken cancellationToken)
    {
        if (Remember().RootId is { } rememberedId && await drive.GetFileAsync(rememberedId, cancellationToken) is { } remembered && IsLiveFolder(remembered))
            return new DriveFolder(remembered.Id!, remembered.Name ?? "");

        var marked = (await drive.ListAsync(RootQuery, cancellationToken)).Where(IsLiveFolder).ToList();
        if (marked.Count == 0) return null;
        if (marked.Count > 1) logger.LogWarning("Google Drive holds {Count} folders marked as ClassScribe's own; using {Name} ({Id})", marked.Count, marked[0].Name, marked[0].Id);

        var found = new DriveFolder(marked[0].Id!, marked[0].Name ?? "");
        KeepRoot(found.Id);
        return found;
    }

    /// <summary>ClassScribe's folder in My Drive, found as <see cref="FindRootAsync"/> finds it, or else made, named after Drive:FolderName, with ClassScribe's mark.</summary>
    public async Task<DriveFolder> FindOrCreateRootAsync(CancellationToken cancellationToken)
    {
        if (await FindRootAsync(cancellationToken) is { } found) return found;

        var name = options.Value.FolderName.Trim();
        var made = await drive.CreateFolderAsync(new DriveFileMetadata(name, FolderType, Parents: null, new Dictionary<string, string> { [MarkKey] = RootMark }), cancellationToken);
        if (string.IsNullOrEmpty(made.Id)) throw new GoogleDriveException(DriveProblem.Refused, $"Google Drive made the folder {name} but did not say its id.");

        logger.LogInformation("Made the folder {Name} in My Drive for ClassScribe", name);
        KeepRoot(made.Id);
        return new DriveFolder(made.Id, made.Name ?? name);
    }

    /// <summary>
    /// The folder <paramref name="levels"/> name under ClassScribe's own folder, from the top down, such as Mantiq and
    /// 2026-10-17, found or made level by level: by its remembered id when that is still a folder outside the trash
    /// (wherever Gulzar has moved it since), else by its name in the level above, else made there. ClassScribe's own folder
    /// is found or made first. Call it only while holding the upload lock, so that two processes never make the same folder.
    /// </summary>
    public async Task<DriveFolderPath> FindOrCreatePathAsync(IReadOnlyList<string> levels, CancellationToken cancellationToken)
    {
        var root = await FindOrCreateRootAsync(cancellationToken);
        var parent = root;
        var key = "";
        List<string> names = [root.Name];
        foreach (var level in levels)
        {
            key = key.Length == 0 ? level : $"{key}/{level}";
            parent = await FindOrCreateLevelAsync(root.Id, parent, key, level, cancellationToken);
            names.Add(parent.Name);
        }

        return new DriveFolderPath(parent, string.Join('/', names));
    }

    /// <summary>
    /// The folder <paramref name="name"/> in <paramref name="parent"/>, remembered under <paramref name="key"/> for the root
    /// <paramref name="rootId"/>: by its remembered id, else by its name in the parent, else made there.
    /// </summary>
    private async Task<DriveFolder> FindOrCreateLevelAsync(string rootId, DriveFolder parent, string key, string name, CancellationToken cancellationToken)
    {
        var remembered = Remember();
        if (remembered.RootId == rootId && remembered.Folders?.GetValueOrDefault(key) is { } rememberedId
            && await drive.GetFileAsync(rememberedId, cancellationToken) is { } known && IsLiveFolder(known))
            return new DriveFolder(known.Id!, known.Name ?? name);

        var named = (await drive.ListAsync(FolderQuery(parent.Id, name), cancellationToken)).Where(IsLiveFolder).ToList();
        if (named.Count > 1) logger.LogDebug("Google Drive holds {Count} folders named {Name} in {Parent}; using {Id}", named.Count, name, parent.Name, named[0].Id);
        if (named.FirstOrDefault() is { } found)
        {
            KeepLevel(rootId, key, found.Id!);
            return new DriveFolder(found.Id!, found.Name ?? name);
        }

        var made = await drive.CreateFolderAsync(new DriveFileMetadata(name, FolderType, [parent.Id], AppProperties: null), cancellationToken);
        if (string.IsNullOrEmpty(made.Id)) throw new GoogleDriveException(DriveProblem.Refused, $"Google Drive made the folder {name} but did not say its id.");

        logger.LogDebug("Made the folder {Name} in {Parent} on Google Drive", name, parent.Name);
        KeepLevel(rootId, key, made.Id);
        return new DriveFolder(made.Id, made.Name ?? name);
    }

    /// <summary>True for a folder that is not in the trash.</summary>
    private static bool IsLiveFolder(DriveFile file) => !string.IsNullOrEmpty(file.Id) && file.MimeType == FolderType && file.Trashed != true;

    /// <summary>What is remembered; nothing when the file is not there or cannot be read, which only costs a search.</summary>
    private RememberedFolders Remember()
    {
        try
        {
            return File.Exists(RememberedFile)
                ? JsonSerializer.Deserialize(File.ReadAllBytes(RememberedFile), DriveStoreJson.Default.RememberedFolders) ?? new RememberedFolders(null)
                : new RememberedFolders(null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Deliberately not rethrown: the remembered id only saves a search; without it the folder is found by its mark.
            logger.LogDebug(ex, "Could not read {Path}; ClassScribe's folder on Drive is looked for instead", RememberedFile);
            return new RememberedFolders(null);
        }
    }

    /// <summary>Remembers <paramref name="rootId"/> as ClassScribe's own folder; the folders remembered under another root are forgotten.</summary>
    private void KeepRoot(string rootId)
    {
        var remembered = Remember();
        Keep(remembered.RootId == rootId ? remembered : new RememberedFolders(rootId));
    }

    /// <summary>Remembers <paramref name="id"/> as the folder at <paramref name="key"/> under the root <paramref name="rootId"/>.</summary>
    private void KeepLevel(string rootId, string key, string id)
    {
        var remembered = Remember();
        var folders = remembered.RootId == rootId && remembered.Folders is { } known ? new Dictionary<string, string>(known) : [];
        folders[key] = id;
        Keep(new RememberedFolders(rootId, folders));
    }

    /// <summary>Writes <paramref name="remembered"/> whole or not at all; a failure only costs a search next time.</summary>
    private void Keep(RememberedFolders remembered)
    {
        var temporary = $"{RememberedFile}.{Guid.NewGuid():N}.tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(RememberedFile)!);
            File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(remembered, DriveStoreJson.Default.RememberedFolders));
            File.Move(temporary, RememberedFile, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Deliberately not rethrown: the folder was found or made; remembering its id only saves a search next time.
            logger.LogWarning(ex, "Could not remember the ids of ClassScribe's folders on Drive in {Path}", RememberedFile);
            TryDelete(temporary);
        }
    }

    /// <summary>Deletes a temporary file a failed write left, if it is there.</summary>
    private void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Deliberately not rethrown: a temporary file left behind holds only ids, and is never read.
            logger.LogDebug(ex, "Could not delete {Path}", path);
        }
    }
}
