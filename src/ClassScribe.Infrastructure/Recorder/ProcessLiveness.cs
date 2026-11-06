using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;

namespace ClassScribe.Recorder;

/// <summary>
/// Asks Windows whether a process runs: it opens the process with the least access there is, sees whether it has ended,
/// and reads when Windows made it. A process that cannot be opened is taken as gone: the ids of ended processes cannot be
/// opened, and a process this user may not look at, such as another user's, is no recorder of this user's. ClassScribe's
/// recorder runs as the same user as the window that starts it. Whether a window may look at a recorder started with Run as
/// administrator while the window was not, or the reverse, is Windows' to decide; if it may not, that recorder looks gone
/// to it.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ProcessLiveness(TimeProvider time, ILogger<ProcessLiveness> logger) : IProcessLiveness
{
    /// <summary>PROCESS_QUERY_LIMITED_INFORMATION: enough to read the process's times.</summary>
    private const uint QueryLimitedInformation = 0x1000;

    /// <summary>SYNCHRONIZE: enough to see whether the process has ended.</summary>
    private const uint Synchronize = 0x00100000;

    /// <summary>WAIT_TIMEOUT: a wait of no time on a process that has not ended.</summary>
    private const uint WaitTimeout = 0x102;

    /// <summary>ERROR_INVALID_PARAMETER: what opening an id that no process has gives.</summary>
    private const int InvalidParameter = 87;

    /// <inheritdoc />
    public bool IsAlive(int processId, DateTimeOffset startedUtc)
    {
        if (processId <= 0) return false;

        using var process = OpenProcess(QueryLimitedInformation | Synchronize, inheritHandle: false, processId);
        if (process.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            if (error != InvalidParameter)
                logger.LogDebug("Could not look at process {ProcessId} ({Reason}); it is taken as no recorder of ClassScribe's", processId, new Win32Exception(error).Message);
            return false;
        }

        // A process that has ended but whose handle another program still holds keeps its id taken; it has ended all the same.
        if (WaitForSingleObject(process, 0) != WaitTimeout) return false;

        if (!GetProcessTimes(process, out var created, out _, out _, out _))
        {
            logger.LogDebug("Could not read when process {ProcessId} started ({Reason}); it is taken as no recorder of ClassScribe's", processId,
                new Win32Exception(Marshal.GetLastPInvokeError()).Message);
            return false;
        }

        return IProcessLiveness.StartedTogether(DateTimeOffset.FromFileTime(created), startedUtc);
    }

    /// <summary>
    /// When the computer last started: now less the time Windows has counted since it started, which includes the time the
    /// laptop slept or hibernated, so a sleep is never taken for a restart. With Fast Startup, shutting down and starting
    /// again resumes Windows rather than starting it, so only a restart counts; a shutdown ends a recording's process
    /// through the session's end, which the run file notes as its own cause.
    /// </summary>
    public DateTimeOffset LastBootUtc() => time.GetUtcNow() - TimeSpan.FromMilliseconds(Environment.TickCount64);

    /// <summary>Opens a process for the access asked for; an invalid handle when Windows refuses.</summary>
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);

    /// <summary>Waits for a process to end, here for no time: WAIT_TIMEOUT while it runs.</summary>
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);

    /// <summary>Reads when Windows made the process, and how long it has run, each as a FILETIME in UTC.</summary>
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(SafeProcessHandle process, out long creationTime, out long exitTime, out long kernelTime, out long userTime);
}
