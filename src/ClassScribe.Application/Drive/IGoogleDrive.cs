namespace ClassScribe.Drive;

/// <summary>A file or folder on Drive, as the use cases read it: what Drive's own description says, without Drive's record.</summary>
/// <param name="Id">Drive's id for it; null when Drive did not say.</param>
/// <param name="Name">Its name.</param>
/// <param name="IsFolder">True for a folder.</param>
/// <param name="Size">Its size in bytes, for a file with content.</param>
/// <param name="Md5">The MD5 of its content, for a file with content, as Drive writes it.</param>
/// <param name="Trashed">True when it is in the trash.</param>
public sealed record DriveItem(string? Id, string? Name, bool IsFolder, long? Size, string? Md5, bool Trashed);

/// <summary>The Google account Drive belongs to, and its storage, as Drive's about call gives them.</summary>
/// <param name="EmailAddress">The account's email address; null when Drive did not say.</param>
/// <param name="DisplayName">The account's name; null when Drive did not say.</param>
/// <param name="Storage">How much it holds and may hold; null when Drive gave no storage.</param>
public sealed record DriveAccount(string? EmailAddress, string? DisplayName, DriveSpace? Storage);

/// <summary>
/// Google Drive as the use cases use it (architecture 4.3): the account, a file or folder, a folder's children, ClassScribe's
/// own folder in My Drive and the folders under it, and the upload of one file with Drive's resumable protocol. Each answer
/// is an Application record, never Google's own; Google's refusals come as a <see cref="GoogleDriveException"/> whose message
/// says what to do, and the caller's own cancel is thrown as it is. Nothing here deletes, trashes or shares anything on
/// Drive. One per run, as the parts behind it are.
/// </summary>
public interface IGoogleDrive
{
    /// <summary>The account and its storage (about.get).</summary>
    Task<DriveAccount> GetAccountAsync(CancellationToken cancellationToken);

    /// <summary>The file or folder <paramref name="id"/>, with its size and MD5 and whether it is in the trash; null when Drive does not have it.</summary>
    Task<DriveItem?> GetItemAsync(string id, CancellationToken cancellationToken);

    /// <summary>Everything in the folder <paramref name="folderId"/> that is not in the trash: with drive.file, only what ClassScribe made.</summary>
    Task<IReadOnlyList<DriveItem>> ListChildrenAsync(string folderId, CancellationToken cancellationToken);

    /// <summary>ClassScribe's folder in My Drive, found by its remembered id or by its mark; null when there is none (in the trash counts as none). Makes nothing.</summary>
    Task<DriveFolder?> FindRootAsync(CancellationToken cancellationToken);

    /// <summary>ClassScribe's folder in My Drive, found as <see cref="FindRootAsync"/> finds it, or else made, named after Drive:FolderName, with ClassScribe's mark.</summary>
    Task<DriveFolder> FindOrCreateRootAsync(CancellationToken cancellationToken);

    /// <summary>
    /// The folder <paramref name="levels"/> name under ClassScribe's own folder, from the top down, such as Mantiq and
    /// 2026-10-17, found or made level by level; ClassScribe's own folder is found or made first. Call it only while holding
    /// the upload lock, so that two processes never make the same folder.
    /// </summary>
    Task<DriveFolderPath> FindOrCreatePathAsync(IReadOnlyList<string> levels, CancellationToken cancellationToken);

    /// <summary>
    /// Sends <paramref name="source"/> to <paramref name="target"/> with Drive's resumable upload (a session started, or a new
    /// revision's; the file in pieces; a kept session asked how much it has and gone on with), going on with
    /// <paramref name="kept"/> when it is a usable session for this very content, and returns the file as Drive made it (at
    /// least its id). <paramref name="watch"/> is told of every change of the session, for the manifest, and may hold the
    /// upload up before each piece. A failure that asking again did not cure is thrown as Drive gave it.
    /// </summary>
    Task<DriveItem> SendAsync(UploadSource source, UploadTarget target, UploadSession? kept, IUploadWatch watch, CancellationToken cancellationToken);
}
