using Microsoft.Extensions.Logging;

namespace ClassScribe.Storage;

/// <summary>How much room is left on the drive that holds a folder, for the warnings about a disk filling up during a class.</summary>
public interface IFreeSpace
{
    /// <summary>
    /// The bytes this user may still write on the drive that holds <paramref name="folder"/>, a disk quota included; null
    /// when that cannot be read, as for a folder on a network share.
    /// </summary>
    long? AvailableBytes(string folder);
}

/// <summary>
/// The free space of a folder's drive, as Windows reports it for the drive's root (<see cref="DriveInfo"/>). A folder whose
/// drive cannot be read gives none, with a line for diagnosis: a network share, which has no drive letter, a path without
/// a drive, or a drive that is not ready.
/// </summary>
public sealed class FreeSpace(ILogger<FreeSpace> logger) : IFreeSpace
{
    /// <inheritdoc />
    public long? AvailableBytes(string folder)
    {
        try
        {
            return new DriveInfo(folder).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Deliberately not rethrown: the free space only feeds a warning that the disk is filling up, and a drive that
            // cannot be read must not end a class; the recording itself reports a disk that cannot be written.
            logger.LogDebug(ex, "Could not read the free space of the drive that holds {Folder}", folder);
            return null;
        }
    }
}
