namespace ClassScribe.Recorder;

/// <summary>
/// One recording at a time, for this user (architecture 4.3): a lock in ClassScribe's own folder, taken from just before a
/// run records and let go once its capture stops, before the upload, so that the next class can start while the last one
/// uploads. It can be let go from any thread, as after an await, and the process that holds it ending, however it ends,
/// lets it go, so a crash never leaves it taken.
/// </summary>
public interface IRecordingLock
{
    /// <summary>
    /// Takes the lock, making ClassScribe's folder if needed, and returns what lets it go; or null when another recording
    /// holds it, in this process or another. A lock found held is tried a few more times first: a window looking whether it
    /// is held (<see cref="IsHeld"/>), a virus scanner, or Windows finishing with a holder that has just ended keeps it for a
    /// moment only. Any other failure to take it, such as a folder that cannot be written, is thrown: it says nothing about
    /// another recording.
    /// </summary>
    IDisposable? TryTake();

    /// <summary>
    /// True when a recording holds the lock now, in this process or another; the look never takes the lock. False when no
    /// recording has ever held it here. Any other failure to look is thrown: it says nothing about a recording.
    /// </summary>
    bool IsHeld();
}
