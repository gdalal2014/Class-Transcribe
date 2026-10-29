namespace ClassScribe.YouTube;

/// <summary>
/// One YouTube batch at a time, across ClassScribe's windows (architecture 4.3; design 2.1): held from the start of a batch
/// to its end, so that two batches never fetch the same video or bill it twice, and let go when the process that holds it
/// ends, however it ends. Never recording.lock: no class ever waits for it.
/// </summary>
public interface IYouTubeLock
{
    /// <summary>
    /// Takes the lock and returns what lets it go; or null when another batch holds it, with that batch's process id in
    /// <paramref name="holder"/> when it can be read. Any other failure to take it is logged and thrown.
    /// </summary>
    IDisposable? TryTake(out int? holder);
}
