using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;

namespace ClassScribe.Desktop.Windows;

/// <summary>Windows' functions for the job a process runs in, behind an interface so that tests can stand in for Windows.</summary>
internal interface IJobApi
{
    /// <summary>
    /// Whether this process runs in any job (IsProcessInJob): 0 with the answer in <paramref name="inJob"/>, or Windows'
    /// error code.
    /// </summary>
    int IsInJob(out bool inJob);

    /// <summary>
    /// The limits of the job this process is directly in (QueryInformationJobObject for JobObjectExtendedLimitInformation):
    /// 0 with its LimitFlags in <paramref name="limitFlags"/>, or Windows' error code.
    /// </summary>
    int ReadLimitFlags(out uint limitFlags);
}

/// <summary>
/// Reads, from Windows, whether ClassScribe runs in a job, and the limits of the job it is directly in that decide whether
/// a recorder it starts can outlive it. When Windows does not answer, the facts are the cautious ones
/// (<see cref="JobFacts.Unknown"/>), and the reason is logged as a warning: a recording then runs in its own window, where
/// closing the window is said to end it.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class JobInspector(ILogger<JobInspector> logger) : IJobInspector
{
    /// <summary>What the warnings say a refusal means for the next recording.</summary>
    private const string Consequence =
        "ClassScribe takes it that the job would end a recorder started from this window when the window closes, so a recording runs in this window.";

    /// <summary>Windows' functions, or a test's stand-in for them.</summary>
    private readonly IJobApi _api = NativeJobApi.Instance;

    /// <summary>Reads the job through <paramref name="api"/> instead of Windows' own functions. Tests pass a fake.</summary>
    internal JobInspector(IJobApi api, ILogger<JobInspector> logger)
        : this(logger) => _api = api;

    /// <inheritdoc />
    public JobFacts Inspect()
    {
        var error = _api.IsInJob(out var inJob);
        if (error != 0)
        {
            logger.LogWarning("Windows did not say whether ClassScribe runs in a job ({Error}). " + Consequence, Describe(error));
            return JobFacts.Unknown;
        }

        if (!inJob)
        {
            logger.LogDebug("ClassScribe runs in no job, so a recorder it starts outlives its window");
            return JobFacts.NoJob;
        }

        error = _api.ReadLimitFlags(out var limitFlags);
        if (error != 0)
        {
            logger.LogWarning("Windows did not give the limits of the job ClassScribe runs in ({Error}). " + Consequence, Describe(error));
            return JobFacts.Unknown;
        }

        var facts = JobFacts.FromLimitFlags(limitFlags);
        logger.LogDebug(
            "ClassScribe runs in a job with the limits 0x{LimitFlags:X}: it ends its processes when it closes: {KillOnClose}; " +
            "breakaway allowed: {BreakawayAllowed}; silent breakaway: {SilentBreakaway}",
            limitFlags, facts.KillOnClose, facts.BreakawayAllowed, facts.SilentBreakaway);
        return facts;
    }

    /// <summary>Windows' words for an error code, and the code, as in "Access is denied, error 5".</summary>
    private static string Describe(int error) => $"{new Win32Exception(error).Message.TrimEnd('.')}, error {error}";
}

/// <summary>Windows' own job functions, from kernel32.</summary>
[SupportedOSPlatform("windows")]
internal sealed class NativeJobApi : IJobApi
{
    /// <summary>JOBOBJECTINFOCLASS JobObjectExtendedLimitInformation.</summary>
    private const int JobObjectExtendedLimitInformation = 9;

    /// <summary>The one instance; it holds nothing.</summary>
    public static NativeJobApi Instance { get; } = new();

    /// <inheritdoc />
    public int IsInJob(out bool inJob)
    {
        // With no job named, Windows answers whether the process runs in any job at all.
        if (IsProcessInJob(GetCurrentProcess(), IntPtr.Zero, out inJob)) return 0;

        inJob = false;
        return Marshal.GetLastPInvokeError();
    }

    /// <inheritdoc />
    public int ReadLimitFlags(out uint limitFlags)
    {
        // With no job named, Windows reads the job this process is directly in, the innermost when jobs are nested.
        var information = new ExtendedLimitInformation();
        if (QueryInformationJobObject(IntPtr.Zero, JobObjectExtendedLimitInformation, ref information, (uint)Marshal.SizeOf<ExtendedLimitInformation>(), out _))
        {
            limitFlags = information.BasicLimitInformation.LimitFlags;
            return 0;
        }

        limitFlags = 0;
        return Marshal.GetLastPInvokeError();
    }

    /// <summary>The pseudo handle that stands for this process; it needs no closing.</summary>
    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern IntPtr GetCurrentProcess();

    /// <summary>Whether a process runs in a job: in the one named, or with none named, in any.</summary>
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsProcessInJob(IntPtr process, IntPtr job, [MarshalAs(UnmanagedType.Bool)] out bool result);

    /// <summary>Reads a job's information of one class; with no job named, of the job this process is directly in.</summary>
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryInformationJobObject(IntPtr job, int informationClass, ref ExtendedLimitInformation information, uint length, out uint returnedLength);

    /// <summary>Windows' JOBOBJECT_BASIC_LIMIT_INFORMATION.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimitInformation
    {
        /// <summary>Each process's limit on time in user mode, in units of 100 nanoseconds.</summary>
        public long PerProcessUserTimeLimit;

        /// <summary>The job's limit on time in user mode, in units of 100 nanoseconds.</summary>
        public long PerJobUserTimeLimit;

        /// <summary>The limits in effect: the JOB_OBJECT_LIMIT_ flags.</summary>
        public uint LimitFlags;

        /// <summary>The smallest working set of each process.</summary>
        public UIntPtr MinimumWorkingSetSize;

        /// <summary>The largest working set of each process.</summary>
        public UIntPtr MaximumWorkingSetSize;

        /// <summary>How many processes the job may hold at once.</summary>
        public uint ActiveProcessLimit;

        /// <summary>The processors the job's processes may run on.</summary>
        public UIntPtr Affinity;

        /// <summary>The priority class of the job's processes.</summary>
        public uint PriorityClass;

        /// <summary>The scheduling class of the job's processes.</summary>
        public uint SchedulingClass;
    }

    /// <summary>Windows' IO_COUNTERS: the job's input and output so far.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        /// <summary>Read, write and other operations, then the bytes each moved.</summary>
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    /// <summary>Windows' JOBOBJECT_EXTENDED_LIMIT_INFORMATION, of which only the basic limits are read.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimitInformation
    {
        /// <summary>The basic limits, with the flags.</summary>
        public BasicLimitInformation BasicLimitInformation;

        /// <summary>The job's input and output so far.</summary>
        public IoCounters IoInfo;

        /// <summary>Each process's limit on committed memory.</summary>
        public UIntPtr ProcessMemoryLimit;

        /// <summary>The job's limit on committed memory.</summary>
        public UIntPtr JobMemoryLimit;

        /// <summary>The most memory one process of the job has committed.</summary>
        public UIntPtr PeakProcessMemoryUsed;

        /// <summary>The most memory the job's processes have committed together.</summary>
        public UIntPtr PeakJobMemoryUsed;
    }
}
