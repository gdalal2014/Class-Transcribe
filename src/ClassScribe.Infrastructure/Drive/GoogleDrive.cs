namespace ClassScribe.Drive;

/// <summary>
/// The adapter of <see cref="IGoogleDrive"/>: the calls to Drive (<see cref="DriveClient"/>), ClassScribe's folders there
/// (<see cref="DriveFolders"/>) and the resumable upload of one file (<see cref="ResumableUpload"/>), with Google's records
/// mapped to the use cases' at the border. One per run, as the three are.
/// </summary>
public sealed class GoogleDrive(DriveClient drive, DriveFolders folders, ResumableUpload upload) : IGoogleDrive
{
    /// <inheritdoc />
    public async Task<DriveAccount> GetAccountAsync(CancellationToken cancellationToken) => AccountOf(await drive.GetAboutAsync(cancellationToken));

    /// <inheritdoc />
    public async Task<DriveItem?> GetItemAsync(string id, CancellationToken cancellationToken) =>
        await drive.GetFileAsync(id, cancellationToken) is { } file ? ItemOf(file) : null;

    /// <inheritdoc />
    public async Task<IReadOnlyList<DriveItem>> ListChildrenAsync(string folderId, CancellationToken cancellationToken) =>
        [.. (await drive.ListAsync(DriveFolders.ChildrenQuery(folderId), cancellationToken)).Select(ItemOf)];

    /// <inheritdoc />
    public Task<DriveFolder?> FindRootAsync(CancellationToken cancellationToken) => folders.FindRootAsync(cancellationToken);

    /// <inheritdoc />
    public Task<DriveFolder> FindOrCreateRootAsync(CancellationToken cancellationToken) => folders.FindOrCreateRootAsync(cancellationToken);

    /// <inheritdoc />
    public Task<DriveFolderPath> FindOrCreatePathAsync(IReadOnlyList<string> levels, CancellationToken cancellationToken) =>
        folders.FindOrCreatePathAsync(levels, cancellationToken);

    /// <inheritdoc />
    public async Task<DriveItem> SendAsync(UploadSource source, UploadTarget target, UploadSession? kept, IUploadWatch watch, CancellationToken cancellationToken) =>
        ItemOf(await upload.SendAsync(source, target, kept, watch, cancellationToken));

    /// <summary><paramref name="file"/> as the use cases read it: a folder by its MIME type, in the trash when Drive says so.</summary>
    internal static DriveItem ItemOf(DriveFile file) =>
        new(file.Id, file.Name, file.MimeType == DriveFolders.FolderType, file.Size, file.Md5Checksum, file.Trashed == true);

    /// <summary><paramref name="about"/> as the use cases read it: the account, and its storage as Drive's room is read.</summary>
    internal static DriveAccount AccountOf(DriveAbout about) =>
        new(about.User?.EmailAddress, about.User?.DisplayName, about.StorageQuota is { } quota ? new DriveSpace(quota.Usage ?? 0, quota.Limit) : null);
}
