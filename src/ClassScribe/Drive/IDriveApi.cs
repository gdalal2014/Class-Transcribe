using Refit;
using ClassScribe.Transcription;

namespace ClassScribe.Drive;

/// <summary>
/// The Drive calls ClassScribe makes (base https://www.googleapis.com/, Drive:ApiBaseUrl), declared for Refit. Each takes
/// the access token as its Authorization header, "Bearer" and the token, from <see cref="GoogleTokens"/>, as
/// <see cref="IElevenLabsApi"/> takes its key, so the caller can renew a refused token and ask once more, and
/// the version of the API in the middle of its path, from Drive:ApiVersion (architecture 7.3), such as v3 in /drive/v3/about.
/// Every answer comes back whatever its status: a 308 of a resumable upload is no error, and its Location and Range headers
/// are read. Every call here accepts the drive.file scope. Nothing here deletes, trashes or shares anything on Drive.
/// </summary>
public interface IDriveApi
{
    /// <summary>GET /drive/{apiVersion}/about: the account's name and email, and its storage limit and usage.</summary>
    [Get("/drive/{apiVersion}/about?fields=user(displayName,emailAddress),storageQuota(limit,usage)")]
    Task<HttpResponseMessage> GetAboutAsync([Header("Authorization")] string authorization, string apiVersion, CancellationToken cancellationToken);

    /// <summary>
    /// GET /drive/{apiVersion}/files: one page of the files and folders <paramref name="query"/> finds, among those ClassScribe
    /// made (drive.file sees no others); <paramref name="pageToken"/> asks for the next page.
    /// </summary>
    [Get("/drive/{apiVersion}/files?spaces=drive&pageSize=100&fields=nextPageToken,files(id,name,mimeType,size,md5Checksum,trashed)")]
    Task<HttpResponseMessage> ListAsync([Header("Authorization")] string authorization, string apiVersion, [AliasAs("q")] string query, [AliasAs("pageToken")] string? pageToken,
        CancellationToken cancellationToken);

    /// <summary>GET /drive/{apiVersion}/files/{fileId}: one file or folder, with its size and MD5, whether it is in the trash, and where it is.</summary>
    [Get("/drive/{apiVersion}/files/{fileId}?fields=id,name,mimeType,size,md5Checksum,trashed,parents")]
    Task<HttpResponseMessage> GetAsync([Header("Authorization")] string authorization, string apiVersion, string fileId, CancellationToken cancellationToken);

    /// <summary>POST /drive/{apiVersion}/files: makes a folder from <paramref name="folder"/>, its name, type, parent and ClassScribe's mark.</summary>
    [Post("/drive/{apiVersion}/files?fields=id,name")]
    Task<HttpResponseMessage> CreateFolderAsync([Header("Authorization")] string authorization, string apiVersion, [Body] DriveFileMetadata folder,
        CancellationToken cancellationToken);

    /// <summary>
    /// POST /upload/drive/{apiVersion}/files?uploadType=resumable: starts the upload of a new file, described by
    /// <paramref name="metadata"/>, of <paramref name="length"/> bytes of <paramref name="contentType"/>. The answer's Location
    /// header is the session's address, from which the pieces are sent; no file exists until the last piece is in.
    /// </summary>
    [Post("/upload/drive/{apiVersion}/files?uploadType=resumable")]
    Task<HttpResponseMessage> StartUploadAsync([Header("Authorization")] string authorization, string apiVersion, [Header("X-Upload-Content-Type")] string contentType,
        [Header("X-Upload-Content-Length")] long length, [Body] DriveFileMetadata metadata, CancellationToken cancellationToken);

    /// <summary>
    /// PATCH /upload/drive/{apiVersion}/files/{fileId}?uploadType=resumable: starts the upload of a new revision of
    /// ClassScribe's own file <paramref name="fileId"/>, which keeps its id, its name and its earlier revisions; as
    /// <see cref="StartUploadAsync"/> otherwise.
    /// </summary>
    [Patch("/upload/drive/{apiVersion}/files/{fileId}?uploadType=resumable")]
    Task<HttpResponseMessage> StartRevisionAsync([Header("Authorization")] string authorization, string apiVersion, string fileId,
        [Header("X-Upload-Content-Type")] string contentType, [Header("X-Upload-Content-Length")] long length, [Body] DriveFileMetadata metadata,
        CancellationToken cancellationToken);

    /// <summary>
    /// PUT /upload/drive/{apiVersion}/files?uploadType=resumable&amp;upload_id=...: sends <paramref name="piece"/>, which carries
    /// its own Content-Range, to the session <paramref name="uploadId"/> of a new file; an empty piece with Content-Range bytes
    /// */size asks how much Drive has. 308 asks for more, from the end of its Range header; 200 or 201 is the file, complete.
    /// </summary>
    [Put("/upload/drive/{apiVersion}/files?uploadType=resumable")]
    Task<HttpResponseMessage> SendAsync([Header("Authorization")] string authorization, string apiVersion, [AliasAs("upload_id")] string uploadId, [Body] HttpContent piece,
        CancellationToken cancellationToken);

    /// <summary>The same as <see cref="SendAsync"/>, to the session <paramref name="uploadId"/> of a new revision of <paramref name="fileId"/>.</summary>
    [Put("/upload/drive/{apiVersion}/files/{fileId}?uploadType=resumable")]
    Task<HttpResponseMessage> SendRevisionAsync([Header("Authorization")] string authorization, string apiVersion, string fileId, [AliasAs("upload_id")] string uploadId,
        [Body] HttpContent piece, CancellationToken cancellationToken);
}

/// <summary>What this build's Drive calls were written for, beside the routes that make them.</summary>
public static class DriveApi
{
    /// <summary>
    /// The version of the Drive API this build's requests and answers were written for (architecture 7.3): the default of
    /// Drive:ApiVersion, and the one any other is warned against, since a new version brings new shapes, which are compiled
    /// into <see cref="DriveJson"/>.
    /// </summary>
    public const string WrittenFor = "v3";
}

/// <summary>The Refit settings <see cref="IGoogleOAuthApi"/> and <see cref="IDriveApi"/> are used with, in the app and in the tests.</summary>
public static class DriveApiSettings
{
    /// <summary>
    /// Refit's defaults, except that a request that cannot be sent (a broken connection, the caller's own time limit) fails
    /// with the original exception instead of Refit's wrapper, so the caller can tell Google out of reach from its own limit.
    /// </summary>
    public static RefitSettings Create() => new() { TransportExceptionFactory = (_, error, _) => error };
}
