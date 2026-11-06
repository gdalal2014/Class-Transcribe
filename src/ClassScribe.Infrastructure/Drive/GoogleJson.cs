using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClassScribe.Drive;

/// <summary>
/// The answer of Google's token endpoint, to a code exchange or a refresh: the tokens on success, or Google's error and its
/// description. As text it shows only which fields are there, never a token.
/// </summary>
/// <param name="AccessToken">The access token, which Drive takes for about an hour.</param>
/// <param name="ExpiresIn">How many seconds the access token lasts.</param>
/// <param name="RefreshToken">The long-lived refresh token; only the code exchange gives one.</param>
/// <param name="Scope">The scopes granted, separated by spaces.</param>
/// <param name="TokenType">Bearer.</param>
/// <param name="Error">Google's error code on failure, such as invalid_grant.</param>
/// <param name="ErrorDescription">Google's words about the error.</param>
public sealed record GoogleTokenAnswer(string? AccessToken, int? ExpiresIn, string? RefreshToken, string? Scope, string? TokenType, string? Error, string? ErrorDescription)
{
    /// <summary>The answer as text: which tokens it holds and Google's error, never the tokens themselves.</summary>
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("HasAccessToken = ").Append(AccessToken is not null)
            .Append(", HasRefreshToken = ").Append(RefreshToken is not null)
            .Append(", ExpiresIn = ").Append(ExpiresIn)
            .Append(", Scope = ").Append(Scope)
            .Append(", Error = ").Append(Error);
        return true;
    }
}

/// <summary>
/// JSON for Google's OAuth endpoints, generated at compile time: snake_case names, read as leniently as System.Text.Json's
/// web defaults allow. Refit's generated client for <see cref="IGoogleOAuthApi"/> is built with it.
/// </summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(GoogleTokenAnswer))]
public sealed partial class GoogleOAuthJson : JsonSerializerContext;

/// <summary>
/// A file's or folder's metadata, as ClassScribe sends it to Drive to make one, or with nothing in it to start a new
/// revision of a file. What is null is left out, by an attribute on each property, which holds however the client's
/// serializer is set up: a null sent to a revision would ask Drive to clear that field.
/// </summary>
/// <param name="Name">Its name.</param>
/// <param name="MimeType">Its type; a folder's is application/vnd.google-apps.folder.</param>
/// <param name="Parents">The folder it goes in; none for My Drive itself.</param>
/// <param name="AppProperties">Properties private to ClassScribe, such as the mark of its own folder.</param>
public sealed record DriveFileMetadata(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Name,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? MimeType,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? Parents,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyDictionary<string, string>? AppProperties);

/// <summary>A file or folder as Drive describes it; Drive writes the size as text, which the web defaults read as a number.</summary>
/// <param name="Id">Drive's id for it.</param>
/// <param name="Name">Its name.</param>
/// <param name="MimeType">Its type.</param>
/// <param name="Size">Its size in bytes, for a file with content.</param>
/// <param name="Md5Checksum">The MD5 of its content, for a file with content.</param>
/// <param name="Trashed">True when it is in the trash.</param>
/// <param name="Parents">The folders it is in.</param>
public sealed record DriveFile(string? Id, string? Name, string? MimeType, long? Size, string? Md5Checksum, bool? Trashed, IReadOnlyList<string>? Parents);

/// <summary>One page of a search of Drive.</summary>
/// <param name="NextPageToken">What asks for the next page; null on the last.</param>
/// <param name="Files">The files and folders on this page.</param>
public sealed record DriveFileList(string? NextPageToken, IReadOnlyList<DriveFile>? Files);

/// <summary>Drive's about: the account and its storage.</summary>
/// <param name="User">The account.</param>
/// <param name="StorageQuota">Its storage.</param>
public sealed record DriveAbout(DriveUser? User, DriveStorageQuota? StorageQuota);

/// <summary>The account Drive belongs to.</summary>
/// <param name="DisplayName">The account's name.</param>
/// <param name="EmailAddress">Its email address.</param>
public sealed record DriveUser(string? DisplayName, string? EmailAddress);

/// <summary>The account's storage, in bytes, written by Drive as text.</summary>
/// <param name="Limit">How much it may hold; null for an account without a limit.</param>
/// <param name="Usage">How much it holds, across Drive, Gmail and Photos.</param>
public sealed record DriveStorageQuota(long? Limit, long? Usage);

/// <summary>Google's error body, as Drive writes it.</summary>
/// <param name="Error">The error.</param>
public sealed record GoogleErrorAnswer(GoogleError? Error);

/// <summary>One of Google's errors.</summary>
/// <param name="Code">The HTTP status.</param>
/// <param name="Message">Google's words.</param>
/// <param name="Errors">The reasons, such as storageQuotaExceeded.</param>
/// <param name="Status">The status name, such as PERMISSION_DENIED.</param>
public sealed record GoogleError(int? Code, string? Message, IReadOnlyList<GoogleErrorItem>? Errors, string? Status);

/// <summary>One reason in Google's error.</summary>
/// <param name="Reason">The reason, such as accessNotConfigured.</param>
/// <param name="Message">Google's words.</param>
/// <param name="Domain">The domain of the reason.</param>
public sealed record GoogleErrorItem(string? Reason, string? Message, string? Domain);

/// <summary>
/// JSON for Drive's API, generated at compile time: camelCase names, the web defaults (which read numbers written as text,
/// as Drive writes sizes), and nothing written for a null. Refit's generated client for <see cref="IDriveApi"/> is built with it.
/// </summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(DriveFileMetadata))]
[JsonSerializable(typeof(DriveFile))]
[JsonSerializable(typeof(DriveFileList))]
[JsonSerializable(typeof(DriveAbout))]
[JsonSerializable(typeof(GoogleErrorAnswer))]
public sealed partial class DriveJson : JsonSerializerContext;

/// <summary>
/// The ids of ClassScribe's folders on Drive, remembered in %LOCALAPPDATA%\ClassScribe\drive\folders.json, so that each is
/// found with one request rather than a search.
/// </summary>
/// <param name="RootId">The id of ClassScribe's own folder in My Drive; null until it is known.</param>
/// <param name="Folders">
/// The ids of the folders under it, by their path under it, as "Mantiq" and "Mantiq/2026-10-17"; null or empty until one is
/// known. They belong to the folder <paramref name="RootId"/> names, and are forgotten when that changes.
/// </param>
internal sealed record RememberedFolders(string? RootId, IReadOnlyDictionary<string, string>? Folders = null);

/// <summary>
/// JSON for ClassScribe's own files about Drive: the client and sign-in (encrypted before it is written; see
/// <see cref="IGoogleCredentialStore"/>) and the remembered folders. camelCase, the web defaults, nothing written for a null.
/// </summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, WriteIndented = true)]
[JsonSerializable(typeof(GoogleCredentials))]
[JsonSerializable(typeof(RememberedFolders))]
internal sealed partial class DriveStoreJson : JsonSerializerContext;
