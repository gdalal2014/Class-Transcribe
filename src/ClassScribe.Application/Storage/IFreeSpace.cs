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
