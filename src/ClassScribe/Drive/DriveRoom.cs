using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClassScribe.Drive;

/// <summary>How much Google Drive holds and may hold, as its about call says.</summary>
/// <param name="Usage">How much it holds, across Drive, Gmail and Photos.</param>
/// <param name="Limit">How much it may hold; null for an account without a limit.</param>
public sealed record DriveSpace(long Usage, long? Limit)
{
    /// <summary>How much more it may hold; null for an account without a limit.</summary>
    public long? Free => Limit is { } limit ? Math.Max(0, limit - Usage) : null;
}

/// <summary>
/// Google Drive's room, for a YouTube batch's plan when the videos' audio goes up (design 2.10), behind an interface so that
/// the batch's tests never reach Google. The real one asks Drive's about call, which drive status makes too.
/// </summary>
public interface IDriveRoom
{
    /// <summary>
    /// Drive's room; null when Drive is not set up or not signed in, when uploads after each class are off, or when Drive
    /// could not be asked, which is said. Never throws but the caller's own cancel.
    /// </summary>
    Task<DriveSpace?> ReadAsync(CancellationToken cancellationToken);
}

/// <summary>Asks Google Drive's about call for its room, once Drive is set up and signed in (see <see cref="IDriveRoom"/>).</summary>
public sealed class DriveRoom(IGoogleCredentialStore store, Lazy<DriveClient> drive, IOptionsSnapshot<DriveOptions> options, ILogger<DriveRoom> logger) : IDriveRoom
{
    /// <inheritdoc />
    public async Task<DriveSpace?> ReadAsync(CancellationToken cancellationToken)
    {
        if (!options.Value.Enabled || store.Read() is not { Credentials.IsSignedIn: true }) return null;

        try
        {
            var about = await drive.Value.GetAboutAsync(cancellationToken);
            return about.StorageQuota is { } quota ? new DriveSpace(quota.Usage ?? 0, quota.Limit) : null;
        }
        catch (GoogleDriveException ex)
        {
            // Deliberately not rethrown: Drive's room only adds a line to the plan; Drive out of reach is said, and the audio
            // that does not fit waits in the queue as always.
            logger.LogInformation("Google Drive's room could not be read ({Reason}); what does not fit waits in the queue", ex.Message);
            return null;
        }
    }
}
