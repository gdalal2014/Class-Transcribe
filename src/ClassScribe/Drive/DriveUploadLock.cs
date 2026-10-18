using ClassScribe.Recorder;
using Microsoft.Extensions.Logging;

namespace ClassScribe.Drive;

/// <summary>
/// One upload to Google Drive at a time, across ClassScribe's processes: %LOCALAPPDATA%\ClassScribe\drive\upload.lock, held
/// open without sharing while an upload runs, as recording.lock is while a class records. Windows closes the file when the
/// process that holds it ends, however it ends, so a crash never leaves it taken. An upload that finds it held puts its
/// recordings in the queue, which the holder takes before it lets go. It is never recording.lock, which an upload must not
/// touch even for a moment, or a class starting at that instant would be refused.
/// </summary>
public sealed class DriveUploadLock(RuntimeFolder folder, ILogger<DriveUploadLock> logger)
{
    /// <summary>The name of the lock file, in ClassScribe's folder for Drive.</summary>
    public const string FileName = "upload.lock";

    /// <summary>How many times a held lock is tried again before it counts as held.</summary>
    private const int Retries = 3;

    /// <summary>ERROR_SHARING_VIOLATION as an HRESULT: the file is open in a way that does not allow this open.</summary>
    private const int SharingViolation = unchecked((int)0x80070020);

    /// <summary>The pause before a held lock is tried again.</summary>
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(100);

    /// <summary>The lock file.</summary>
    public string LockFile => Path.Combine(folder.Root, DriveFolders.DriveFolderName, FileName);

    /// <summary>
    /// Takes the lock, making the folder if needed, and returns what lets it go; or null when another upload holds it, in
    /// this process or another. A lock found held is tried three more times, 100 ms apart, as a virus scanner may hold the
    /// file for a moment. Any other failure to open the file is logged and thrown: it says nothing about another upload.
    /// </summary>
    public IDisposable? TryTake()
    {
        for (var retry = 0; ; retry++)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LockFile)!);
                var file = new FileStream(LockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, bufferSize: 1);
                logger.LogDebug("Took the lock {Path}: no other upload to Google Drive runs", LockFile);
                return new Held(file, logger);
            }
            catch (IOException ex) when (ex.HResult == SharingViolation && retry < Retries)
            {
                // Deliberately not rethrown: another program has the file open, which may be for a moment only, so the lock
                // is tried again shortly. The last try's answer is the one given.
                logger.LogDebug("The lock {Path} is held; trying again in {Milliseconds} ms", LockFile, RetryDelay.TotalMilliseconds);
                Thread.Sleep(RetryDelay);
            }
            catch (IOException ex) when (ex.HResult == SharingViolation)
            {
                // Deliberately not rethrown: a lock held after every try is the answer this method gives, that another upload
                // runs.
                logger.LogDebug(ex, "The lock {Path} is held by another upload", LockFile);
                return null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogDebug(ex, "Could not open the lock file {Path}", LockFile);
                throw;
            }
        }
    }

    /// <summary>The lock, held: the lock file open without sharing. Letting it go closes the file; a second time does nothing.</summary>
    private sealed class Held(FileStream file, ILogger logger) : IDisposable
    {
        private int _released;

        /// <summary>Closes the lock file, which lets the next upload take the lock.</summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0) return;

            file.Dispose();
            logger.LogDebug("Let go of the lock {Path}", file.Name);
        }
    }
}
