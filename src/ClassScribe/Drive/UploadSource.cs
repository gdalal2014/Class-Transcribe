namespace ClassScribe.Drive;

/// <summary>What goes up: an open stream of the file's bytes, how many there are, their MD5 as Drive writes it, and their type.</summary>
/// <param name="Content">The bytes, from the start of the stream; it must be able to seek.</param>
/// <param name="Length">How many bytes there are.</param>
/// <param name="Md5">Their MD5: 32 lowercase hexadecimal digits.</param>
/// <param name="ContentType">Their type, such as audio/wav; never a Google Docs type, so Drive keeps the bytes as they are.</param>
public sealed record UploadSource(Stream Content, long Length, string Md5, string ContentType);

/// <summary>Where a file goes up to: a new file named <paramref name="Name"/> in the folder <paramref name="FolderId"/>, or a new revision of <paramref name="RevisionOf"/>.</summary>
/// <param name="Name">The file's name on Drive.</param>
/// <param name="FolderId">The folder a new file goes into.</param>
/// <param name="RevisionOf">The id of ClassScribe's own file that gets a new revision; null for a new file.</param>
public sealed record UploadTarget(string Name, string? FolderId, string? RevisionOf);

/// <summary>What an upload tells as it goes, and what may hold it up between pieces.</summary>
public interface IUploadWatch
{
    /// <summary>Runs before each piece is sent; it may wait, as while a class records, until the upload may go on.</summary>
    Task BeforePieceAsync(CancellationToken cancellationToken);

    /// <summary>The session was started, Drive confirmed more of it, or it was dropped (null), for the manifest.</summary>
    void SessionChanged(UploadSession? session);
}
