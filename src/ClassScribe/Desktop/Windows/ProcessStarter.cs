using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;

namespace ClassScribe.Desktop.Windows;

/// <summary>The creation flags ClassScribe uses, with the values Windows documents for them.</summary>
[Flags]
internal enum ProcessCreationFlags : uint
{
    /// <summary>None.</summary>
    None = 0,

    /// <summary>CREATE_SUSPENDED: the first thread waits until it is resumed, so that the process can be put in a job before it runs.</summary>
    Suspended = 0x00000004,

    /// <summary>CREATE_NEW_CONSOLE: a console window of its own.</summary>
    NewConsole = 0x00000010,

    /// <summary>
    /// CREATE_NEW_PROCESS_GROUP: the root of a process group of its own. Windows disables Ctrl+C in it, and the processes
    /// it starts inherit that unless they turn Ctrl+C back on. Windows ignores the flag beside <see cref="NewConsole"/>.
    /// </summary>
    NewProcessGroup = 0x00000200,

    /// <summary>CREATE_UNICODE_ENVIRONMENT: an environment block, if one were given, in UTF-16.</summary>
    UnicodeEnvironment = 0x00000400,

    /// <summary>EXTENDED_STARTUPINFO_PRESENT: STARTUPINFOEX, with its list of attributes, such as the handles to inherit.</summary>
    ExtendedStartupInfo = 0x00080000,

    /// <summary>CREATE_BREAKAWAY_FROM_JOB: outside the job of the process that starts it, where that job allows it.</summary>
    BreakawayFromJob = 0x01000000,

    /// <summary>CREATE_NO_WINDOW: a console of its own that has no window.</summary>
    NoWindow = 0x08000000
}

/// <summary>How the window of a process that gets a console of its own is first shown (STARTUPINFO's wShowWindow).</summary>
internal enum ProcessWindowShow : short
{
    /// <summary>SW_SHOWMINNOACTIVE: minimized, as a taskbar button, without taking the focus from the window that has it.</summary>
    MinimizedNotActive = 7
}

/// <summary>The program ClassScribe runs as, and the folder its files are in.</summary>
/// <param name="ProcessPath">
/// The program's full path (<see cref="Environment.ProcessPath"/>): ClassScribe.exe, or dotnet.exe running ClassScribe.dll;
/// null when Windows does not say.
/// </param>
/// <param name="AppFolder">The folder of ClassScribe's files (<see cref="AppContext.BaseDirectory"/>), where ClassScribe.dll is.</param>
internal sealed record ThisProgram(string? ProcessPath, string AppFolder)
{
    /// <summary>The program this process runs.</summary>
    public static ThisProgram Current => new(Environment.ProcessPath, AppContext.BaseDirectory);
}

/// <summary>Everything CreateProcessW is given to start a process, as <see cref="ProcessStarter"/> fills it in.</summary>
/// <param name="Application">The program's full path (lpApplicationName), so that Windows looks for it nowhere else.</param>
/// <param name="CommandLine">The whole command line (lpCommandLine), the program quoted first.</param>
/// <param name="InheritHandles">Whether the process inherits this process's inheritable handles (bInheritHandles).</param>
/// <param name="Flags">The creation flags (dwCreationFlags).</param>
/// <param name="WorkingFolder">The process's working folder (lpCurrentDirectory).</param>
/// <param name="Show">
/// How the window of a process with a console of its own is first shown (STARTF_USESHOWWINDOW with wShowWindow), or null
/// for Windows' own choice. No other part of STARTUPINFO is given, so the process gets no standard handles from here,
/// unless <see cref="Handles"/> gives them, as a tool's launch does.
/// </param>
/// <remarks>
/// A launch of the recorder or the alert window gives none of the three optional parts, and is started as it always was: the
/// environment (lpEnvironment) NULL, so the process inherits this process's environment block as it is, and no handles. A
/// tool's launch, such as yt-dlp's (design 3.2), gives all three: an environment block of its own, the standard handles it
/// inherits and nothing else, and a job it runs in from its first instruction.
/// </remarks>
internal sealed record ProcessLaunch(string Application, string CommandLine, bool InheritHandles, ProcessCreationFlags Flags, string WorkingFolder, ProcessWindowShow? Show)
{
    /// <summary>The process's environment, by variable name; null to inherit this process's as it is.</summary>
    public IReadOnlyDictionary<string, string>? Environment { get; init; }

    /// <summary>The standard handles the process gets, the only handles it inherits (a handle list); null for none.</summary>
    public StandardHandles? Handles { get; init; }

    /// <summary>
    /// The job the process runs in: it is made suspended, put in the job, and only then resumed, so that nothing it starts can
    /// leave the job before; null for no job of its own.
    /// </summary>
    public SafeHandle? Job { get; init; }

    /// <summary>True when the launch gives any of the optional parts, as a tool's launch does.</summary>
    public bool IsToolLaunch => Environment is not null || Handles is not null || Job is not null;
}

/// <summary>A tool's standard handles: the ends of the pipes it reads its input from and writes its output and errors to, each inheritable.</summary>
/// <param name="Input">Standard input.</param>
/// <param name="Output">Standard output.</param>
/// <param name="Error">Standard error.</param>
internal sealed record StandardHandles(SafeHandle Input, SafeHandle Output, SafeHandle Error);

/// <summary>
/// How the starter has a process started, behind an interface so that its tests can see the launch it asks for and hand
/// back a process of their own. <see cref="NativeProcessCreationApi"/> turns a launch into the call of CreateProcessW.
/// </summary>
internal interface IProcessCreationApi
{
    /// <summary>
    /// Starts a process as <paramref name="launch"/> says (CreateProcessW). Null when Windows refuses, with its error code in
    /// <paramref name="error"/>; otherwise <paramref name="error"/> is 0.
    /// </summary>
    StartedProcess? Start(ProcessLaunch launch, out int error);
}

/// <summary>
/// Starts the recorder and the alert window through CreateProcessW: the same program from the same path, with a plain
/// argument; no window for the recorder; no inherited handle; this process's environment as it is, so that the key and a
/// stub's address reach the recorder that way and no other; and ClassScribe's own folder under %LOCALAPPDATA% as the
/// working folder, so that nothing holds the repo's bin folder, the user's current folder or anything under OneDrive open.
/// Nothing else is used to start a process: no shell, no scheduled task, no WMI. A start Windows refuses is logged with
/// what was asked and thrown as a <see cref="Win32Exception"/>; what it means for the recording is the caller's to say.
/// </summary>
/// <param name="workingFolder">The folder the processes run in: ClassScribe's own folder under %LOCALAPPDATA%, which must exist.</param>
/// <param name="logger">Where the starts and Windows' refusals are logged.</param>
[SupportedOSPlatform("windows")]
public sealed class ProcessStarter(string workingFolder, ILogger<ProcessStarter> logger) : IProcessStarter
{
    /// <summary>The recorder's flags, outside the job or not: a console with no window, in a process group of its own.</summary>
    private const ProcessCreationFlags RecorderFlags = ProcessCreationFlags.NoWindow | ProcessCreationFlags.NewProcessGroup | ProcessCreationFlags.UnicodeEnvironment;

    /// <summary>
    /// The alert window's flags: a console window of its own, and not outside the job, since it ends when Gulzar closes it.
    /// No process group of its own: Windows ignores that flag beside a new console, and a console of its own already keeps
    /// every other console's Ctrl+C away from it. Ctrl+C typed in the alert window most likely does nothing at first,
    /// whatever these flags: the recorder starts it, the recorder ignores Ctrl+C as the root of a process group of its own,
    /// and Microsoft documents that the processes it starts inherit that. If Ctrl+C is to work there, the attach command
    /// turns it back on when it starts (SetConsoleCtrlHandler with no handler and FALSE).
    /// </summary>
    private const ProcessCreationFlags AlertWindowFlags = ProcessCreationFlags.NewConsole | ProcessCreationFlags.UnicodeEnvironment;

    /// <summary>Windows' error ERROR_FILE_NOT_FOUND, given when this process does not know which program it runs.</summary>
    private const int FileNotFound = 2;

    /// <summary>Windows' function, or a test's stand-in for it.</summary>
    private readonly IProcessCreationApi _api = NativeProcessCreationApi.Instance;

    /// <summary>The program to start, which is this one.</summary>
    private readonly ThisProgram _program = ThisProgram.Current;

    /// <summary>
    /// Starts the processes through <paramref name="api"/> instead of Windows' own function, as if this process ran
    /// <paramref name="program"/>. Tests pass a fake and a program of their own.
    /// </summary>
    internal ProcessStarter(IProcessCreationApi api, ThisProgram program, string workingFolder, ILogger<ProcessStarter> logger)
        : this(workingFolder, logger)
    {
        _api = api;
        _program = program;
    }

    /// <inheritdoc />
    public StartedProcess StartRecorder(string runId, bool breakaway) =>
        Start(
            StartRefusal.Recorder,
            $"{ProcessArguments.Recorder} {Checked(runId)}",
            breakaway ? RecorderFlags | ProcessCreationFlags.BreakawayFromJob : RecorderFlags,
            show: null);

    /// <inheritdoc />
    public StartedProcess StartAlertWindow(string runId) =>
        Start(
            StartRefusal.AlertWindow,
            $"{ProcessArguments.Attach} {ProcessArguments.Alert} {Checked(runId)}",
            AlertWindowFlags,
            ProcessWindowShow.MinimizedNotActive);

    /// <summary>
    /// <paramref name="runId"/>, when it is a run id (<see cref="ProcessArguments.IsRunId"/>), since it goes on a command
    /// line, where anything else could add arguments of its own, and names the run's files, pipe and stop event. Anything
    /// else is refused with an <see cref="ArgumentException"/>, before anything starts.
    /// </summary>
    private static string Checked(string runId) =>
        ProcessArguments.IsRunId(runId)
            ? runId
            : throw new ArgumentException(
                $"\"{runId}\" is not a run id, such as 20261016-230002Z-0a1b2c3d. A run id goes on a command line and names the run's files, pipe and stop event, so nothing else is taken.",
                nameof(runId));

    /// <summary>
    /// Starts this program with <paramref name="arguments"/> and <paramref name="flags"/>: <paramref name="what"/> names it
    /// in the log and in Windows' refusal. Under dotnet, the program is dotnet running ClassScribe's dll from the app's
    /// folder, as <see cref="Commands.CommandLine"/> prints it.
    /// </summary>
    private StartedProcess Start(string what, string arguments, ProcessCreationFlags flags, ProcessWindowShow? show)
    {
        if (_program.ProcessPath is not { } program)
        {
            logger.LogDebug("Did not start {What}: Windows did not say which program ClassScribe runs as", what);
            throw new Win32Exception(FileNotFound, $"Windows did not start {what}: it did not say which program ClassScribe runs as (error {FileNotFound})");
        }

        var commandLine = Path.GetFileNameWithoutExtension(program).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            ? $"\"{program}\" \"{Path.Combine(_program.AppFolder, AppDll)}\" {arguments}"
            : $"\"{program}\" {arguments}";
        var launch = new ProcessLaunch(program, commandLine, InheritHandles: false, flags, workingFolder, show);

        if (_api.Start(launch, out var error) is { } started)
        {
            logger.LogDebug(
                "Started {What}: process {ProcessId}, {CommandLine}, flags {Flags}, in {Folder}",
                what, started.ProcessId, launch.CommandLine, launch.Flags, launch.WorkingFolder);
            return started;
        }

        logger.LogDebug(
            "Windows did not start {What} ({Error}): {CommandLine}, flags {Flags}, in {Folder}",
            what, Describe(error), launch.CommandLine, launch.Flags, launch.WorkingFolder);
        throw StartRefusal.For(what, error);
    }

    /// <summary>The file name of ClassScribe's dll, which dotnet runs: ClassScribe.dll.</summary>
    private static string AppDll => typeof(ProcessStarter).Assembly.GetName().Name + ".dll";

    /// <summary>Windows' words for an error code, and the code, as in "Access is denied, error 5".</summary>
    private static string Describe(int error) => $"{new Win32Exception(error).Message.TrimEnd('.')}, error {error}";
}

/// <summary>
/// Windows' CreateProcessW with the arguments <see cref="NativeProcessCreationApi"/> gives it, so that a test can take the
/// call in Windows' place and see every one of them as Windows would get them, without a process being started.
/// </summary>
internal delegate bool CreateProcessFunction(
    string applicationName,
    char[] commandLine,
    IntPtr processAttributes,
    IntPtr threadAttributes,
    bool inheritHandles,
    uint creationFlags,
    IntPtr environment,
    string currentDirectory,
    ref NativeProcessCreationApi.StartupInfo startupInfo,
    out NativeProcessCreationApi.ProcessInformation processInformation);

/// <summary>
/// Windows' own CreateProcessW, from kernel32, and the calls on the process it starts. A launch becomes the call's
/// arguments here and nowhere else, and they go to <paramref name="createProcess"/> as they are.
/// </summary>
/// <param name="createProcess">
/// Windows' CreateProcessW itself, as <see cref="Instance"/> has it; in tests, a stand-in that keeps the arguments and
/// refuses the call.
/// </param>
[SupportedOSPlatform("windows")]
internal sealed partial class NativeProcessCreationApi(CreateProcessFunction createProcess) : IProcessCreationApi
{
    /// <summary>STARTF_USESHOWWINDOW: STARTUPINFO's wShowWindow counts.</summary>
    private const int UseShowWindow = 0x00000001;

    /// <summary>WAIT_OBJECT_0: the process has ended.</summary>
    private const uint WaitObject0 = 0x00000000;

    /// <summary>WAIT_TIMEOUT: the process is still running.</summary>
    private const uint WaitTimeout = 0x00000102;

    /// <summary>The one instance that starts processes through Windows' own CreateProcessW; it holds nothing else.</summary>
    public static NativeProcessCreationApi Instance { get; } = new(CreateProcessW);

    /// <inheritdoc />
    public StartedProcess? Start(ProcessLaunch launch, out int error)
    {
        if (launch.IsToolLaunch) return StartTool(launch, out error);

        var startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>() };
        if (launch.Show is { } show)
        {
            startup.Flags = UseShowWindow;
            startup.ShowWindow = (short)show;
        }

        // CreateProcessW may write into the command line, so it is given a copy of its own, ended by a null character.
        // No environment block is given: the process inherits this process's environment as it is.
        var commandLine = (launch.CommandLine + '\0').ToCharArray();
        if (!createProcess(launch.Application, commandLine, IntPtr.Zero, IntPtr.Zero, launch.InheritHandles, (uint)launch.Flags, IntPtr.Zero,
                launch.WorkingFolder, ref startup, out var created))
        {
            error = Marshal.GetLastPInvokeError();
            return null;
        }

        // The first thread's handle is never needed. The process's handle is kept for as long as the record is, so that
        // its exit code can still be read after it ended; the handle's finalizer closes it once the record is let go.
        CloseHandle(created.Thread);
        var process = new SafeProcessHandle(created.Process, ownsHandle: true);
        error = 0;
        return new StartedProcess(created.ProcessId, StartTime(process), () => ExitCode(process));
    }

    /// <summary>
    /// When Windows made <paramref name="process"/>, as it records it, which is what tells it from a later process given
    /// the same id. Should Windows not say, the time now stands in, a moment after it made the process.
    /// </summary>
    private static DateTimeOffset StartTime(SafeProcessHandle process) =>
        GetProcessTimes(process, out var creation, out _, out _, out _)
            ? new DateTimeOffset(DateTime.FromFileTimeUtc(creation), TimeSpan.Zero)
            : DateTimeOffset.UtcNow;

    /// <summary>The exit code of <paramref name="process"/> once it has ended; null while it runs.</summary>
    private static int? ExitCode(SafeProcessHandle process)
    {
        var wait = WaitForSingleObject(process, 0);
        if (wait == WaitTimeout) return null;
        if (wait == WaitObject0 && GetExitCodeProcess(process, out var exitCode)) return unchecked((int)exitCode);

        throw new Win32Exception(Marshal.GetLastPInvokeError(), "Windows did not say whether a process ClassScribe started has ended");
    }

    /// <summary>Starts a process.</summary>
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessW(
        string applicationName,
        char[] commandLine,
        IntPtr processAttributes,
        IntPtr threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
        uint creationFlags,
        IntPtr environment,
        string currentDirectory,
        ref StartupInfo startupInfo,
        out ProcessInformation processInformation);

    /// <summary>Waits for a process to end, here not at all: whether it has ended.</summary>
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);

    /// <summary>Reads a process's exit code.</summary>
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(SafeProcessHandle process, out uint exitCode);

    /// <summary>Reads when a process was made, and the times it has run, as FILETIMEs.</summary>
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(SafeProcessHandle process, out long creationTime, out long exitTime, out long kernelTime, out long userTime);

    /// <summary>Closes a handle Windows gave out.</summary>
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    /// <summary>Windows' STARTUPINFOW, with nothing given but its size and, for a console window of its own, how it is shown.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct StartupInfo
    {
        /// <summary>cb: the size of the structure.</summary>
        public int Size;

        /// <summary>lpReserved.</summary>
        public IntPtr Reserved;

        /// <summary>lpDesktop: none, so the desktop of the process that starts it.</summary>
        public IntPtr Desktop;

        /// <summary>lpTitle: none, so the program's path as a new console's title.</summary>
        public IntPtr Title;

        /// <summary>dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars and dwFillAttribute: not given.</summary>
        public int X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute;

        /// <summary>dwFlags: STARTF_USESHOWWINDOW when <see cref="ShowWindow"/> counts, and never STARTF_USESTDHANDLES.</summary>
        public int Flags;

        /// <summary>wShowWindow: how a console window of its own is first shown.</summary>
        public short ShowWindow;

        /// <summary>cbReserved2.</summary>
        public short Reserved2Size;

        /// <summary>lpReserved2.</summary>
        public IntPtr Reserved2;

        /// <summary>hStdInput, hStdOutput and hStdError: none given.</summary>
        public IntPtr StdInput, StdOutput, StdError;
    }

    /// <summary>Windows' PROCESS_INFORMATION: the new process and its first thread.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct ProcessInformation
    {
        /// <summary>hProcess: the new process's handle, which the caller closes.</summary>
        public IntPtr Process;

        /// <summary>hThread: its first thread's handle, which the caller closes.</summary>
        public IntPtr Thread;

        /// <summary>dwProcessId.</summary>
        public int ProcessId;

        /// <summary>dwThreadId.</summary>
        public int ThreadId;
    }
}
