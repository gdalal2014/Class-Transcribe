using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;

namespace ClassScribe.YouTube;

/// <summary>
/// One YouTube batch at a time, across ClassScribe's windows (design 2.1): &lt;RuntimeFolder&gt;\youtube\youtube.lock, held
/// open from the start of a batch to its end, as recording.lock is while a class records, so that two batches never fetch
/// the same video or bill it twice. The holder writes its process id into it and lets others read that, so that a second
/// batch can name the window that holds it. Windows closes the file when the process that holds it ends, however it ends,
/// so a closed window frees it. It is never recording.lock: no class ever waits for it.
/// </summary>
public sealed class YouTubeLock(YouTubeFolders folders, ILogger<YouTubeLock> logger)
{
    /// <summary>How many times a held lock is tried again before it counts as held.</summary>
    private const int Retries = 3;

    /// <summary>ERROR_SHARING_VIOLATION as an HRESULT: the file is open in a way that does not allow this open.</summary>
    private const int SharingViolation = unchecked((int)0x80070020);

    /// <summary>The pause before a held lock is tried again.</summary>
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Takes the lock, making the folder if needed, and returns what lets it go; or null when another batch holds it, with
    /// that batch's process id in <paramref name="holder"/> when it can be read. A lock found held is tried three more times,
    /// 100 ms apart, as a virus scanner may hold the file for a moment. Any other failure to open the file is logged and thrown.
    /// </summary>
    public IDisposable? TryTake(out int? holder)
    {
        holder = null;
        for (var retry = 0; ; retry++)
        {
            try
            {
                Directory.CreateDirectory(folders.Root);
                var file = new FileStream(folders.LockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read, bufferSize: 1);
                try
                {
                    file.SetLength(0);
                    file.Write(Encoding.ASCII.GetBytes(Environment.ProcessId.ToString(CultureInfo.InvariantCulture)));
                    file.Flush();
                }
                catch
                {
                    file.Dispose();
                    throw;
                }

                logger.LogDebug("Took the lock {Path}: no other YouTube batch runs", folders.LockFile);
                return new Held(file, logger);
            }
            catch (IOException ex) when (ex.HResult == SharingViolation && retry < Retries)
            {
                // Deliberately not rethrown: another program has the file open, which may be for a moment only, so the lock is
                // tried again shortly. The last try's answer is the one given.
                logger.LogDebug("The lock {Path} is held; trying again in {Milliseconds} ms", folders.LockFile, RetryDelay.TotalMilliseconds);
                Thread.Sleep(RetryDelay);
            }
            catch (IOException ex) when (ex.HResult == SharingViolation)
            {
                // Deliberately not rethrown: a lock held after every try is the answer this method gives, that another batch runs.
                logger.LogDebug(ex, "The lock {Path} is held by another YouTube batch", folders.LockFile);
                holder = ReadHolder();
                return null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogDebug(ex, "Could not open the lock file {Path}", folders.LockFile);
                throw;
            }
        }
    }

    /// <summary>The process id the holder wrote into the lock file; null when it cannot be read.</summary>
    private int? ReadHolder()
    {
        try
        {
            using var file = new FileStream(folders.LockFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, bufferSize: 1);
            using var reader = new StreamReader(file, Encoding.ASCII);
            return int.TryParse(reader.ReadToEnd().Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Deliberately not rethrown: the holder's id only names the window that holds the lock; without it, the refusal
            // still says that another batch runs.
            logger.LogDebug(ex, "Could not read who holds the lock {Path}", folders.LockFile);
            return null;
        }
    }

    /// <summary>The lock, held: the lock file open, others reading only. Letting it go closes the file; a second time does nothing.</summary>
    private sealed class Held(FileStream file, ILogger logger) : IDisposable
    {
        private int _released;

        /// <summary>Closes the lock file, which lets the next batch take the lock.</summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0) return;

            file.Dispose();
            logger.LogDebug("Let go of the lock {Path}", file.Name);
        }
    }
}
