using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClassScribe.Storage;
using Microsoft.Extensions.Logging;

namespace ClassScribe.Drive;

/// <summary>
/// What ClassScribe keeps of Google: the client it signs in with, and, while it is signed in, the refresh token, the account's
/// email, when it signed in and the scope Google granted. Kept encrypted for the Windows account (see
/// <see cref="DpapiGoogleCredentialStore"/>), never in the settings, a log, a run file or a test. As text it shows the end of
/// the client id, whether it is signed in and as whom, never the secret or the token.
/// </summary>
/// <param name="ClientId">The client id.</param>
/// <param name="ClientSecret">The client secret.</param>
public sealed record GoogleCredentials(string ClientId, string ClientSecret)
{
    /// <summary>The refresh token of the sign-in; null when not signed in.</summary>
    public string? RefreshToken { get; init; }

    /// <summary>The email address of the Google account signed in; null when not signed in.</summary>
    public string? Email { get; init; }

    /// <summary>When the sign-in was made, in UTC; null when not signed in.</summary>
    public DateTimeOffset? SignedInUtc { get; init; }

    /// <summary>The scopes Google granted, separated by spaces; null when not signed in.</summary>
    public string? Scope { get; init; }

    /// <summary>True while there is a sign-in: a refresh token.</summary>
    [JsonIgnore]
    public bool IsSignedIn => !string.IsNullOrEmpty(RefreshToken);

    /// <summary>The client these credentials sign in with.</summary>
    [JsonIgnore]
    public GoogleClient Client => new(ClientId, ClientSecret);

    /// <summary>The same client without the sign-in, as after a sign-out or once Google has ended the sign-in.</summary>
    public GoogleCredentials SignedOut() => this with { RefreshToken = null, Email = null, SignedInUtc = null, Scope = null };

    /// <summary>The credentials as text, which a log line might hold by mistake: the end of the client id, whether signed in, and as whom.</summary>
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("ClientId = ").Append(GoogleClient.Describe(ClientId))
            .Append(", IsSignedIn = ").Append(IsSignedIn)
            .Append(", Email = ").Append(Email);
        return true;
    }
}

/// <summary>What reading the store found: the credentials, nothing (not set up), or a store that cannot be read, with why.</summary>
/// <param name="Credentials">What is stored; null when nothing is, or when it cannot be read.</param>
/// <param name="Problem">Why the store cannot be read, with what to do, in plain words; null when it can.</param>
public sealed record StoredCredentials(GoogleCredentials? Credentials, string? Problem)
{
    /// <summary>Nothing stored: ClassScribe has never been given a Google client.</summary>
    public static StoredCredentials None { get; } = new(null, null);

    /// <summary>A client is kept, so Google Drive is set up, signed in or not.</summary>
    public bool IsSetUp => Credentials is not null;

    /// <summary>What was read: <paramref name="credentials"/>.</summary>
    public static StoredCredentials Of(GoogleCredentials credentials) => new(credentials, null);

    /// <summary>A store that cannot be read, for <paramref name="problem"/>; it counts as no sign-in.</summary>
    public static StoredCredentials Unreadable(string problem) => new(null, problem);
}

/// <summary>
/// Where the Google client and sign-in are kept. One file, written whole or not at all. Tests use an in-memory store, and
/// never the real %APPDATA%\ClassScribe.
/// </summary>
public interface IGoogleCredentialStore
{
    /// <summary>The file that holds them, for messages.</summary>
    string Location { get; }

    /// <summary>
    /// Reads what is kept: the credentials, nothing, or, for a store that cannot be read, the reason, which counts as no
    /// sign-in. Never throws for the store's own state.
    /// </summary>
    StoredCredentials Read();

    /// <summary>Keeps <paramref name="credentials"/> in place of whatever was kept, whole or not at all.</summary>
    void Write(GoogleCredentials credentials);
}

/// <summary>
/// The store in %APPDATA%\ClassScribe\google-drive.dat: the credentials as UTF-8 JSON, protected with Windows' data protection
/// for the current user (DPAPI, with an entropy of ClassScribe's own, so the blob is never mistaken for another), so only this
/// Windows account on this computer can open it. Written atomically: a temporary file in the same folder, then moved over the
/// old one, so a crash never leaves half a file. Nothing it holds is ever logged.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiGoogleCredentialStore(RoamingFolder folder, ILogger<DpapiGoogleCredentialStore> logger) : IGoogleCredentialStore
{
    /// <summary>The entropy every blob is protected with: data protected without it, or with another, does not open.</summary>
    private static readonly byte[] Entropy = "ClassScribe Google Drive 1"u8.ToArray();

    /// <summary>One read or write at a time in this process.</summary>
    private readonly Lock _gate = new();

    /// <inheritdoc />
    public string Location => folder.GoogleDriveFile;

    /// <inheritdoc />
    public StoredCredentials Read()
    {
        lock (_gate)
        {
            byte[] protectedBytes;
            try
            {
                if (!File.Exists(Location)) return StoredCredentials.None;
                protectedBytes = File.ReadAllBytes(Location);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Deliberately not rethrown: a store that cannot be read counts as no sign-in, and the reason goes with it to
                // whoever reads it, to be shown and logged there.
                logger.LogDebug(ex, "Could not read {Path}", Location);
                return StoredCredentials.Unreadable($"{Location} could not be read: {ex.Message}");
            }

            byte[] json;
            try
            {
                json = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);
            }
            catch (CryptographicException ex)
            {
                // Deliberately not rethrown: a blob this account cannot open (made by another Windows account or computer, or
                // after an administrator reset the password) counts as no sign-in, with the reason and what to do.
                logger.LogDebug("{Path} cannot be opened by this Windows account: {Reason}", Location, ex.Message);
                return StoredCredentials.Unreadable($"{Location} cannot be opened by this Windows account: it was made by another Windows user or on another computer, or Windows' protection keys changed. {SignInAgain}");
            }

            try
            {
                var credentials = JsonSerializer.Deserialize(json, DriveStoreJson.Default.GoogleCredentials);
                if (credentials is null || string.IsNullOrWhiteSpace(credentials.ClientId) || string.IsNullOrWhiteSpace(credentials.ClientSecret))
                    return Damaged();

                return StoredCredentials.Of(credentials);
            }
            catch (JsonException)
            {
                // Deliberately not rethrown: a blob that opens but is not the store's JSON counts as no sign-in. The parser's
                // message is not logged, since it may quote what the blob holds.
                return Damaged();
            }
            finally
            {
                CryptographicOperations.ZeroMemory(json);
            }
        }
    }

    /// <inheritdoc />
    public void Write(GoogleCredentials credentials)
    {
        lock (_gate)
        {
            var json = JsonSerializer.SerializeToUtf8Bytes(credentials, DriveStoreJson.Default.GoogleCredentials);
            byte[] protectedBytes;
            try
            {
                protectedBytes = ProtectedData.Protect(json, Entropy, DataProtectionScope.CurrentUser);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(json);
            }

            var temporary = Path.Combine(folder.Root, $"{RoamingFolder.GoogleDriveFileName}.{Guid.NewGuid():N}.tmp");
            try
            {
                Directory.CreateDirectory(folder.Root);
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.Write(protectedBytes);
                    stream.Flush(flushToDisk: true);
                }

                File.Move(temporary, Location, overwrite: true);
                logger.LogDebug("Saved ClassScribe's Google client and sign-in in {Path}", Location);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Could not save ClassScribe's Google client and sign-in in {Path}", Location);
                DeleteTemporary(temporary);
                throw;
            }
        }
    }

    /// <summary>What to do when the store cannot be used: sign in again with the client file.</summary>
    private const string SignInAgain =
        "Sign in again with the client file you downloaded from Google: ClassScribe drive signin --client \"<the file>\", or menu choice 9 > S. If you deleted that file, click Add secret on the client's page in Google Auth platform > Clients and download the JSON again.";

    /// <summary>The verdict on a blob that opens but holds no usable credentials.</summary>
    private StoredCredentials Damaged()
    {
        logger.LogDebug("{Path} is damaged: it holds no usable Google client", Location);
        return StoredCredentials.Unreadable($"{Location} is damaged, so ClassScribe has no Google sign-in. {SignInAgain}");
    }

    /// <summary>Deletes the temporary file of a write that failed, if it is there.</summary>
    private void DeleteTemporary(string temporary)
    {
        try
        {
            File.Delete(temporary);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Deliberately not rethrown: the write's own failure is the one to report, and is rethrown by the caller; a
            // temporary file left behind holds only encrypted bytes and is never read.
            logger.LogDebug(ex, "Could not delete {Path}", temporary);
        }
    }
}
