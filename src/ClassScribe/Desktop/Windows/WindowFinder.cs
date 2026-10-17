using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace ClassScribe.Desktop.Windows;

/// <summary>One process as a snapshot of Windows' process list shows it.</summary>
/// <param name="ParentId">The id of the process that started it. That process may have ended since, and its id gone to another.</param>
/// <param name="ExeName">Its program's file name, such as Code.exe.</param>
internal sealed record ProcessFacts(int ParentId, string ExeName);

/// <summary>Windows' processes and their windows, as <see cref="WindowFinder"/> reads them, behind an interface so that tests can lay out their own.</summary>
internal interface IProcessTree
{
    /// <summary>Every running process, by its id, from one snapshot of Windows' process list.</summary>
    IReadOnlyDictionary<int, ProcessFacts> List();

    /// <summary>When <paramref name="processId"/> started, in UTC, or null when it has ended or its start cannot be read.</summary>
    DateTimeOffset? StartedUtc(int processId);

    /// <summary>The topmost visible window of <paramref name="processId"/> that has a taskbar button, or zero when it shows none.</summary>
    IntPtr WindowOf(int processId);
}

/// <summary>
/// Finds the window that shows a console when nothing owns the console's window, as in the VS Code terminal: it walks
/// up the parent processes to the first that shows a window with a taskbar button (VS Code's main process, past the
/// shell and VS Code's terminal host, which show none).
/// </summary>
/// <remarks>
/// A parent that started after its child is not its parent but a later process that was given the same id once the
/// parent had ended, so the walk ends there, as it does at a parent that has ended or whose start cannot be read, and
/// after <see cref="MostSteps"/> parents. The Windows shell (explorer.exe) ends it too: a window there would be File
/// Explorer's, not the one that shows ClassScribe.
/// </remarks>
internal sealed class WindowFinder(IProcessTree tree)
{
    /// <summary>How many parents are looked at, at most.</summary>
    public const int MostSteps = 8;

    /// <summary>The program of the Windows shell, which the walk never goes past.</summary>
    private const string Shell = "explorer.exe";

    /// <summary>The window of the first parent of <paramref name="processId"/> that shows one, or zero when none is found.</summary>
    public IntPtr FindFor(int processId)
    {
        var processes = tree.List();
        var child = processId;
        var childStarted = tree.StartedUtc(child);

        for (var step = 0; step < MostSteps; step++)
        {
            if (childStarted is null || !processes.TryGetValue(child, out var facts) || facts.ParentId <= 0 || facts.ParentId == child) return 0;

            var parent = facts.ParentId;
            if (!processes.TryGetValue(parent, out var parentFacts) || string.Equals(parentFacts.ExeName, Shell, StringComparison.OrdinalIgnoreCase)) return 0;

            var parentStarted = tree.StartedUtc(parent);
            if (parentStarted is null || parentStarted > childStarted) return 0;

            var window = tree.WindowOf(parent);
            if (window != 0) return window;

            child = parent;
            childStarted = parentStarted;
        }

        return 0;
    }
}

/// <summary>
/// Windows' processes and their windows: the process list from a Toolhelp snapshot, start times from GetProcessTimes,
/// and windows from EnumWindows. Everything here only reads.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class NativeProcessTree : IProcessTree
{
    /// <summary>TH32CS_SNAPPROCESS: the processes, without their threads, heaps or modules.</summary>
    private const uint SnapProcesses = 0x2;

    /// <summary>PROCESS_QUERY_LIMITED_INFORMATION: enough to read a process's times, granted for most processes of the same user.</summary>
    private const uint QueryLimitedInformation = 0x1000;

    /// <summary>GWL_EXSTYLE.</summary>
    private const int ExtendedStyle = -20;

    /// <summary>GW_OWNER.</summary>
    private const uint Owner = 4;

    /// <summary>WS_EX_TOOLWINDOW: a tool window, which has no taskbar button.</summary>
    private const long ToolWindow = 0x80;

    /// <summary>WS_EX_APPWINDOW: a window that has a taskbar button even when something owns it.</summary>
    private const long AppWindow = 0x40000;

    /// <summary>ERROR_NO_MORE_FILES: the end of the process list.</summary>
    private const int NoMoreFiles = 18;

    /// <summary>The one instance; it holds nothing.</summary>
    public static NativeProcessTree Instance { get; } = new();

    /// <inheritdoc />
    public IReadOnlyDictionary<int, ProcessFacts> List()
    {
        using var snapshot = CreateToolhelp32Snapshot(SnapProcesses, 0);
        if (snapshot.IsInvalid) throw new Win32Exception(Marshal.GetLastPInvokeError(), "Windows did not list the running processes.");

        var processes = new Dictionary<int, ProcessFacts>();
        var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>() };
        if (!Process32FirstW(snapshot, ref entry)) return End(processes);

        do
        {
            processes[(int)entry.ProcessId] = new ProcessFacts((int)entry.ParentProcessId, entry.ExeFile);
        }
        while (Process32NextW(snapshot, ref entry));

        return End(processes);
    }

    /// <inheritdoc />
    public DateTimeOffset? StartedUtc(int processId)
    {
        using var process = OpenProcess(QueryLimitedInformation, inheritHandle: false, (uint)processId);
        if (process.IsInvalid || !GetProcessTimes(process, out var created, out _, out _, out _)) return null;

        return DateTimeOffset.FromFileTime(created).ToUniversalTime();
    }

    /// <inheritdoc />
    public IntPtr WindowOf(int processId)
    {
        IntPtr found = 0;

        // Windows lists top-level windows from the front to the back, so the first that fits is the topmost.
        EnumWindows((window, _) =>
        {
            GetWindowThreadProcessId(window, out var owner);
            if (owner != processId || !HasTaskbarButton(window)) return true;

            found = window;
            return false;
        }, 0);

        return found;
    }

    /// <summary>The process list, after checking that the snapshot was read to its end.</summary>
    private static Dictionary<int, ProcessFacts> End(Dictionary<int, ProcessFacts> processes)
    {
        var error = Marshal.GetLastPInvokeError();
        if (error != NoMoreFiles) throw new Win32Exception(error, "Windows did not list every running process.");
        return processes;
    }

    /// <summary>
    /// True when <paramref name="window"/> is visible and has a taskbar button: an app window, or one that nothing owns
    /// and that is not a tool window. <see cref="NativeConsoleWindowApi"/> asks the same of the console's windows.
    /// </summary>
    internal static bool HasTaskbarButton(IntPtr window)
    {
        if (!IsWindowVisible(window)) return false;

        var style = (long)(IntPtr.Size == 8 ? GetWindowLongPtrW(window, ExtendedStyle) : GetWindowLongW(window, ExtendedStyle));
        if ((style & AppWindow) != 0) return true;
        return GetWindow(window, Owner) == 0 && (style & ToolWindow) == 0;
    }

    /// <summary>Windows' PROCESSENTRY32W.</summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        /// <summary>The size of the structure, in bytes.</summary>
        public uint Size;

        /// <summary>No longer used by Windows.</summary>
        public uint Usage;

        /// <summary>The process's id.</summary>
        public uint ProcessId;

        /// <summary>No longer used by Windows.</summary>
        public IntPtr DefaultHeapId;

        /// <summary>No longer used by Windows.</summary>
        public uint ModuleId;

        /// <summary>How many threads the process runs.</summary>
        public uint Threads;

        /// <summary>The id of the process that started it.</summary>
        public uint ParentProcessId;

        /// <summary>The base priority of its threads.</summary>
        public int PriorityBase;

        /// <summary>No longer used by Windows.</summary>
        public uint Flags;

        /// <summary>Its program's file name.</summary>
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string ExeFile;
    }

    /// <summary>Called for each top-level window; returns false to stop.</summary>
    private delegate bool WindowCallback(IntPtr window, IntPtr parameter);

    /// <summary>Takes a snapshot of the running processes.</summary>
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern SafeFileHandle CreateToolhelp32Snapshot(uint flags, uint processId);

    /// <summary>The first process of a snapshot.</summary>
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32FirstW(SafeFileHandle snapshot, ref ProcessEntry entry);

    /// <summary>The next process of a snapshot.</summary>
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32NextW(SafeFileHandle snapshot, ref ProcessEntry entry);

    /// <summary>Opens a process to read about it.</summary>
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    /// <summary>When a process started and ended, and the time it ran.</summary>
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(SafeProcessHandle process, out long creation, out long exit, out long kernel, out long user);

    /// <summary>Calls <paramref name="callback"/> for each top-level window.</summary>
    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(WindowCallback callback, IntPtr parameter);

    /// <summary>The thread and process that made a window.</summary>
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out int processId);

    /// <summary>Whether a window is visible.</summary>
    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);

    /// <summary>A window related to another, such as its owner.</summary>
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern IntPtr GetWindow(IntPtr window, uint relation);

    /// <summary>A window's value at an offset, such as its extended style, in a 64-bit process.</summary>
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern IntPtr GetWindowLongPtrW(IntPtr window, int index);

    /// <summary>A window's value at an offset, such as its extended style, in a 32-bit process.</summary>
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern int GetWindowLongW(IntPtr window, int index);
}
