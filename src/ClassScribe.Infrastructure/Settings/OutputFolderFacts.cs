using System.Text.Json;

namespace ClassScribe.Settings;

/// <summary>
/// The folders' facts on this computer: Directory.Exists for a folder held, DriveInfo for a drive's label, and Dropbox's
/// info.json files for its folders, read at each check, so that a Dropbox installed or moved while ClassScribe runs counts.
/// Dropbox says where it keeps those files and what they hold: "How to programmatically find the Dropbox folder paths",
/// https://help.dropbox.com/installs/locate-dropbox-folder (a JSON object with "personal" and "business", each with its
/// "path").
/// </summary>
/// <param name="dropboxInfoFiles">The info.json files to read; null for those of this user (see <see cref="DropboxInfoFiles"/>).</param>
public sealed class OutputFolderFacts(IReadOnlyList<string>? dropboxInfoFiles = null) : IOutputFolderFacts
{
    /// <summary>The label Google Drive for desktop gives the drive it adds.</summary>
    public const string GoogleDriveLabel = "Google Drive";

    /// <summary>Where Dropbox keeps its info.json for this user: under the roaming, then the local, application data folder.</summary>
    public static IReadOnlyList<string> DropboxInfoFiles() =>
    [
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Dropbox", "info.json"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Dropbox", "info.json")
    ];

    /// <inheritdoc />
    public bool HasFolder(string folder, string name) => Directory.Exists(Path.Combine(folder, name));

    /// <inheritdoc />
    public string? VolumeLabel(string root)
    {
        if (Path.GetPathRoot(root) is not { Length: > 0 } driveRoot || !string.Equals(Path.TrimEndingDirectorySeparator(driveRoot), Path.TrimEndingDirectorySeparator(root), StringComparison.OrdinalIgnoreCase)
            || driveRoot.StartsWith(@"\\", StringComparison.Ordinal))
            return null;

        try
        {
            return new DriveInfo(driveRoot).VolumeLabel is { Length: > 0 } label ? label : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException)
        {
            // Deliberately not rethrown: a drive that is not there, not ready, or not readable has no label to warn about,
            // and the warning is only advice; the check must never fail the settings over another drive.
            return null;
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<string> DropboxFolders()
    {
        List<string> folders = [];
        foreach (var file in dropboxInfoFiles ?? DropboxInfoFiles())
        {
            try
            {
                if (!File.Exists(file)) continue;

                using var document = JsonDocument.Parse(File.ReadAllBytes(file));
                if (document.RootElement.ValueKind != JsonValueKind.Object) continue;

                foreach (var account in document.RootElement.EnumerateObject())
                {
                    if (account.Value.ValueKind == JsonValueKind.Object && account.Value.TryGetProperty("path", out var path) && path.ValueKind == JsonValueKind.String
                        && path.GetString() is { Length: > 0 } folder && !folders.Contains(folder, StringComparer.OrdinalIgnoreCase))
                        folders.Add(folder);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                // Deliberately not rethrown: Dropbox's own file that cannot be read, or is not what Dropbox writes, names no
                // folder to warn about; the warning is only advice, and the check must never fail the settings over it.
            }
        }

        return folders;
    }
}
