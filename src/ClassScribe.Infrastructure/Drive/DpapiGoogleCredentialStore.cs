using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using ClassScribe.Storage;
using Microsoft.Extensions.Logging;

namespace ClassScribe.Drive;

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
