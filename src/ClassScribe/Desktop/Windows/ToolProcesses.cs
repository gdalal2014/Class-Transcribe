using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using ClassScribe.YouTube;
using Microsoft.Win32.SafeHandles;

namespace ClassScribe.Desktop.Windows;

/// <summary>
/// Windows' start of a tool, such as yt-dlp (design 3.2): the same CreateProcessW as the recorder's, given what a tool needs
/// beside it. The environment block is the launch's own, without the API key; the tool inherits its three standard handles
/// and no other handle of this process, through STARTUPINFOEX's handle list; and it is made suspended, put in its job, and
/// only then resumed, so that a program that unpacks itself and starts a child of its own, as yt-dlp's does, can never start
/// that child outside the job. A process the job will not take is ended before it has run a single instruction.
/// </summary>
internal sealed partial class NativeProcessCreationApi
{
    /// <summary>STARTF_USESTDHANDLES: STARTUPINFO's standard handles count.</summary>
    private const int UseStdHandles = 0x00000100;

    /// <summary>PROC_THREAD_ATTRIBUTE_HANDLE_LIST: the handles the new process inherits, and no others.</summary>
    private static readonly IntPtr HandleListAttribute = 0x00020002;

    /// <summary>The program every test that starts a real tool runs instead: Windows' own command processor, which reaches nothing.</summary>
    private static string CommandProcessor => Path.Combine(Environment.SystemDirectory, "cmd.exe");

    /// <summary>
    /// Starts a tool as <paramref name="launch"/> says (see the class summary). Null when Windows refuses, or when the job would
    /// not take it, which ends it first, with Windows' error in <paramref name="error"/>. In a test run, any program but
    /// Windows' own command processor is refused with an <see cref="InvalidOperationException"/> before anything starts (see
    /// <see cref="YouTubeGuards"/>).
    /// </summary>
    private static StartedProcess? StartTool(ProcessLaunch launch, out int error)
    {
        if (!string.Equals(Path.GetFullPath(launch.Application), CommandProcessor, StringComparison.OrdinalIgnoreCase))
            YouTubeGuards.RefuseInTests($"Windows' start of {Path.GetFileName(launch.Application)}", "FakeToolRunner, or FakeProcessCreationApi under ProcessToolRunner");

        var added = new List<SafeHandle>();
        var environment = IntPtr.Zero;
        var handleArray = IntPtr.Zero;
        var attributeList = IntPtr.Zero;
        try
        {
            var startup = new StartupInfoEx { StartupInfo = new StartupInfo { Size = Marshal.SizeOf<StartupInfoEx>() } };
            var flags = launch.Flags | ProcessCreationFlags.UnicodeEnvironment;
            if (launch.Environment is { } variables) environment = Marshal.StringToHGlobalUni(EnvironmentBlock(variables));

            if (launch.Handles is { } handles)
            {
                IntPtr[] raw = [Held(handles.Input, added), Held(handles.Output, added), Held(handles.Error, added)];
                startup.StartupInfo.Flags = UseStdHandles;
                startup.StartupInfo.StdInput = raw[0];
                startup.StartupInfo.StdOutput = raw[1];
                startup.StartupInfo.StdError = raw[2];

                handleArray = Marshal.AllocHGlobal(IntPtr.Size * raw.Length);
                Marshal.Copy(raw, 0, handleArray, raw.Length);
                var size = IntPtr.Zero;
                InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
                attributeList = Marshal.AllocHGlobal(size);
                if (!InitializeProcThreadAttributeList(attributeList, 1, 0, ref size))
                {
                    error = Marshal.GetLastPInvokeError();
                    Marshal.FreeHGlobal(attributeList);
                    attributeList = IntPtr.Zero;
                    return null;
                }

                if (!UpdateProcThreadAttribute(attributeList, 0, HandleListAttribute, handleArray, IntPtr.Size * raw.Length, IntPtr.Zero, IntPtr.Zero))
                {
                    error = Marshal.GetLastPInvokeError();
                    return null;
                }

                startup.AttributeList = attributeList;
                flags |= ProcessCreationFlags.ExtendedStartupInfo;
            }

            var job = launch.Job is { } given ? Held(given, added) : IntPtr.Zero;
            if (job != IntPtr.Zero) flags |= ProcessCreationFlags.Suspended;

            // CreateProcessW may write into the command line, so it is given a copy of its own, ended by a null character.
            var commandLine = (launch.CommandLine + '\0').ToCharArray();
            if (!CreateToolProcessW(launch.Application, commandLine, IntPtr.Zero, IntPtr.Zero, launch.InheritHandles, (uint)flags, environment, launch.WorkingFolder,
                    ref startup, out var created))
            {
                error = Marshal.GetLastPInvokeError();
                return null;
            }

            var process = new SafeProcessHandle(created.Process, ownsHandle: true);
            if (job != IntPtr.Zero && (!AssignProcessToJobObject(job, created.Process) || ResumeThread(created.Thread) == uint.MaxValue))
            {
                // The job would not take it, or it would not run: it is ended before it has run, so nothing it would start escapes.
                error = Marshal.GetLastPInvokeError();
                TerminateProcess(created.Process, 1);
                CloseHandle(created.Thread);
                process.Dispose();
                return null;
            }

            CloseHandle(created.Thread);
            error = 0;
            return new StartedProcess(created.ProcessId, StartTime(process), () => ExitCode(process));
        }
        finally
        {
            if (attributeList != IntPtr.Zero)
            {
                DeleteProcThreadAttributeList(attributeList);
                Marshal.FreeHGlobal(attributeList);
            }

            if (handleArray != IntPtr.Zero) Marshal.FreeHGlobal(handleArray);
            if (environment != IntPtr.Zero) Marshal.FreeHGlobal(environment);
            foreach (var handle in added) handle.DangerousRelease();
        }
    }

    /// <summary>The raw value of <paramref name="handle"/>, kept from being closed until it is released with the others in <paramref name="added"/>.</summary>
    private static IntPtr Held(SafeHandle handle, List<SafeHandle> added)
    {
        var success = false;
        handle.DangerousAddRef(ref success);
        if (success) added.Add(handle);
        return handle.DangerousGetHandle();
    }

    /// <summary>
    /// An environment block of <paramref name="variables"/>: each NAME=value ended by a null character, sorted by name without
    /// regard to letter case, as Windows keeps them, and a null character more at the end.
    /// </summary>
    internal static string EnvironmentBlock(IReadOnlyDictionary<string, string> variables)
    {
        var block = new StringBuilder();
        foreach (var (name, value) in variables.OrderBy(variable => variable.Key, StringComparer.OrdinalIgnoreCase))
            block.Append(name).Append('=').Append(value).Append('\0');

        return block.Append('\0').ToString();
    }

    /// <summary>Starts a process with STARTUPINFOEX: CreateProcessW, given the extended structure.</summary>
    [DllImport("kernel32.dll", EntryPoint = "CreateProcessW", ExactSpelling = true, SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateToolProcessW(
        string applicationName,
        char[] commandLine,
        IntPtr processAttributes,
        IntPtr threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
        uint creationFlags,
        IntPtr environment,
        string currentDirectory,
        ref StartupInfoEx startupInfo,
        out ProcessInformation processInformation);

    /// <summary>Makes, or measures with no list given, a list of attributes for a process to be made.</summary>
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);

    /// <summary>Sets one attribute of the list, here the handles to inherit.</summary>
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value, IntPtr size, IntPtr previousValue, IntPtr returnSize);

    /// <summary>Lets go of what the list holds.</summary>
    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern void DeleteProcThreadAttributeList(IntPtr list);

    /// <summary>Puts a process in a job.</summary>
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    /// <summary>Lets a suspended thread run; gives 0xFFFFFFFF when it fails.</summary>
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern uint ResumeThread(IntPtr thread);

    /// <summary>Ends a process at once.</summary>
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(IntPtr process, uint exitCode);

    /// <summary>Windows' STARTUPINFOEXW: STARTUPINFOW and the list of attributes.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct StartupInfoEx
    {
        /// <summary>StartupInfo: the plain structure, its size that of this one.</summary>
        public StartupInfo StartupInfo;

        /// <summary>lpAttributeList: the attributes, here the handles to inherit.</summary>
        public IntPtr AttributeList;
    }
}

/// <summary>
/// A job that holds the processes of one tool run (design 3.2): made with JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE, so that when its
/// handle closes, as when ClassScribe's window is closed or the run ends, every process still in it ends, yt-dlp, its own
/// child, FFmpeg and Deno alike; and <see cref="Terminate"/> ends them all at once, for a cancel. Jobs nest since Windows 8, so
/// it works inside the job a terminal or dotnet run puts ClassScribe in.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class ToolJob : IDisposable
{
    /// <summary>JobObjectExtendedLimitInformation: the class of JOBOBJECT_EXTENDED_LIMIT_INFORMATION.</summary>
    private const int ExtendedLimitInformationClass = 9;

    /// <summary>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE.</summary>
    private const uint KillOnJobClose = 0x00002000;

    /// <summary>The job's handle.</summary>
    private readonly SafeJobHandle _handle;

    /// <summary>A job over <paramref name="handle"/>.</summary>
    private ToolJob(SafeJobHandle handle) => _handle = handle;

    /// <summary>The job's handle, which a tool's launch names (see <see cref="ProcessLaunch.Job"/>).</summary>
    public SafeHandle Handle => _handle;

    /// <summary>Makes a job that ends its processes when its handle closes. Throws a <see cref="Win32Exception"/> when Windows will not.</summary>
    public static ToolJob Create()
    {
        var handle = CreateJobObjectW(IntPtr.Zero, null);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            throw new Win32Exception(error, $"Windows did not make a job for a tool (error {error})");
        }

        var limits = new JobLimits { BasicLimits = new BasicJobLimits { LimitFlags = KillOnJobClose } };
        if (!SetInformationJobObject(handle, ExtendedLimitInformationClass, ref limits, (uint)Marshal.SizeOf<JobLimits>()))
        {
            var error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            throw new Win32Exception(error, $"Windows did not set a tool's job to end its processes when it closes (error {error})");
        }

        return new ToolJob(handle);
    }

    /// <summary>Ends every process in the job at once, for a cancel. A job already closed, or empty, is left as it is.</summary>
    public void Terminate()
    {
        if (!_handle.IsClosed) TerminateJobObject(_handle, 130);
    }

    /// <summary>Closes the job's handle, which ends any process still in it.</summary>
    public void Dispose() => _handle.Dispose();

    /// <summary>Makes a job.</summary>
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeJobHandle CreateJobObjectW(IntPtr attributes, string? name);

    /// <summary>Sets a job's limits.</summary>
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(SafeJobHandle job, int informationClass, ref JobLimits information, uint length);

    /// <summary>Ends every process in a job.</summary>
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateJobObject(SafeJobHandle job, uint exitCode);

    /// <summary>Closes a handle Windows gave out.</summary>
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    /// <summary>A job's handle, closed when it is let go.</summary>
    private sealed class SafeJobHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        /// <summary>A handle Windows fills in.</summary>
        public SafeJobHandle()
            : base(ownsHandle: true)
        {
        }

        /// <inheritdoc />
        protected override bool ReleaseHandle() => CloseHandle(handle);
    }

    /// <summary>Windows' JOBOBJECT_BASIC_LIMIT_INFORMATION.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct BasicJobLimits
    {
        /// <summary>Each process's limit on time in user mode.</summary>
        public long PerProcessUserTimeLimit;

        /// <summary>The job's limit on time in user mode.</summary>
        public long PerJobUserTimeLimit;

        /// <summary>The limits in effect: here only JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE.</summary>
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

    /// <summary>Windows' IO_COUNTERS.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct JobIoCounters
    {
        /// <summary>Read, write and other operations, then the bytes each moved.</summary>
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    /// <summary>Windows' JOBOBJECT_EXTENDED_LIMIT_INFORMATION.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct JobLimits
    {
        /// <summary>The basic limits, with the flags.</summary>
        public BasicJobLimits BasicLimits;

        /// <summary>The job's input and output so far.</summary>
        public JobIoCounters IoInfo;

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
