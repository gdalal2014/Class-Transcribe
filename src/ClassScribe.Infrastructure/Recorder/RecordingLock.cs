using Microsoft.Extensions.Logging;

namespace ClassScribe.Recorder;

/// <summary>
/// One recording at a time, for this user: the lock file in ClassScribe's own folder, held open without sharing from just
/// before a run records until its capture stops, before the upload, so that the next class can start while the last one
/// uploads. Windows closes the file when the process that holds it ends, however it ends, so a crash never leaves the lock
/// taken. Unlike a named mutex, which belongs to the thread that took it, the file can be let go from any thread, as after
/// an await. The file itself stays in place; nothing is written into it.
/// </summary>
public sealed class RecordingLock(RuntimeFolder folder, ILogger<RecordingLock> logger) : IRecordingLock
{
    /// <summary>How many times a held lock is tried again before it counts as held.</summary>
    private const int Retries = 3;

    /// <summary>ERROR_SHARING_VIOLATION as an HRESULT: the file is open in a way that does not allow this open.</summary>
    private const int SharingViolation = unchecked((int)0x80070020);

    /// <summary>The pause before a held lock is tried again.</summary>
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(100);

    /// <summary>How a take waits before it tries again: the thread sleeps, unless a test plays the wait.</summary>
    private readonly Action<TimeSpan> _pause = Thread.Sleep;

    /// <summary>
    /// A lock over <paramref name="folder"/> that waits with <paramref name="pause"/> before each new try instead of
    /// sleeping, so that a test can act at that moment, as the other holder would.
    /// </summary>
    internal RecordingLock(RuntimeFolder folder, ILogger<RecordingLock> logger, Action<TimeSpan> pause) : this(folder, logger) => _pause = pause;

    /// <summary>
    /// Takes the lock, making ClassScribe's folder if needed, and returns what lets it go; or null when another recording
    /// holds it, in this process or another. A lock found held is tried three more times, 100 ms apart, first: a window
    /// looking whether it is held (<see cref="IsHeld"/>), a virus scanner, or Windows finishing with a holder that has just
    /// ended keeps the file for a moment only. Any other failure to open the file, such as a folder that cannot be written,
    /// is logged and thrown: it says nothing about another recording.
    /// </summary>
    public IDisposable? TryTake()
    {
        for (var retry = 0; ; retry++)
        {
            try
            {
                Directory.CreateDirectory(folder.Root);
                var file = new FileStream(folder.LockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, bufferSize: 1);
                logger.LogDebug("Took the lock {Path}: no other recording runs", folder.LockFile);
                return new Held(file, logger);
            }
            catch (IOException ex) when (ex.HResult == SharingViolation && retry < Retries)
            {
                // Deliberately not rethrown: another program has the file open, which may be for a moment only, so the
                // lock is tried again shortly. The last try's answer is the one given.
                logger.LogDebug("The lock {Path} is held; trying again in {Milliseconds} ms", folder.LockFile, RetryDelay.TotalMilliseconds);
                _pause(RetryDelay);
            }
            catch (IOException ex) when (ex.HResult == SharingViolation)
            {
                // Deliberately not rethrown: a lock held after every try is the answer this method gives, that another
                // recording runs.
                logger.LogDebug(ex, "The lock {Path} is held by another recording", folder.LockFile);
                return null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogDebug(ex, "Could not open the lock file {Path}", folder.LockFile);
                throw;
            }
        }
    }

    /// <summary>
    /// True when a recording holds the lock now, in this process or another. The look opens the file for a moment while
    /// sharing everything, so it never takes the lock; a take at that very moment tries again (see <see cref="TryTake"/>).
    /// False when there is no lock file, as before the first recording. Any other failure to look, such as a folder where
    /// the file should be, is logged and thrown: it says nothing about a recording.
    /// </summary>
    public bool IsHeld()
    {
        try
        {
            using var look = new FileStream(folder.LockFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize: 1);
            return false;
        }
        catch (IOException ex) when (ex.HResult == SharingViolation)
        {
            // Deliberately not rethrown: a sharing violation is the answer this look is for, that a recording holds the
            // lock. Any other failure is a real one, and goes to the caller.
            return true;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            // Deliberately not rethrown: without a lock file, no recording has ever held the lock here.
            logger.LogDebug(ex, "There is no lock file {Path}, so no recording holds it", folder.LockFile);
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug(ex, "Could not look at the lock file {Path}", folder.LockFile);
            throw;
        }
    }

    /// <summary>The lock, held: the lock file open without sharing. Letting it go closes the file; a second time does nothing.</summary>
    private sealed class Held(FileStream file, ILogger logger) : IDisposable
    {
        private int _released;

        /// <summary>Closes the lock file, which lets the next recording take the lock.</summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0) return;

            file.Dispose();
            logger.LogDebug("Let go of the lock {Path}", file.Name);
        }
    }
}
