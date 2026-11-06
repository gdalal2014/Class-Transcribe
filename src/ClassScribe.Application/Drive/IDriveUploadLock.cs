namespace ClassScribe.Drive;

/// <summary>
/// One upload pass to Google Drive at a time, across ClassScribe's processes (architecture 4.3): held while an upload runs,
/// let go when the process that holds it ends, however it ends. Never recording.lock, which an upload must not touch even
/// for a moment.
/// </summary>
public interface IDriveUploadLock
{
    /// <summary>
    /// Takes the lock and returns what lets it go; or null when another upload holds it, in this process or another. Any
    /// other failure to take it is logged and thrown: it says nothing about another upload.
    /// </summary>
    IDisposable? TryTake();
}
