using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;

namespace ClassScribe.Desktop.Windows;

/// <summary>Windows' console and window functions that <see cref="ConsoleWindowSignals"/> uses, behind an interface so that tests can stand in for Windows.</summary>
internal interface IConsoleWindowApi
{
    /// <summary>True when the output goes to a file or a pipe rather than to a console, as when a script captures it.</summary>
    bool OutputRedirected { get; }

    /// <summary>
    /// True when Windows Terminal's WT_SESSION variable is set: Windows Terminal started ClassScribe, or started a program
    /// that passed the variable on. A console Windows hands to Windows Terminal, as from a shortcut or a double-click,
    /// never has it.
    /// </summary>
    bool WindowsTerminalVariableSet { get; }

    /// <summary>This process's id, where the walk up the parent processes starts.</summary>
    int ProcessId { get; }

    /// <summary>The console's title now, or null when it cannot be read.</summary>
    string? ReadTitle();

    /// <summary>Sets the console's title (SetConsoleTitle).</summary>
    void WriteTitle(string title);

    /// <summary>
    /// Writes a control sequence to the console, with VT processing on for the write. Returns false, writing nothing,
    /// when the output is not a console or will not take sequences.
    /// </summary>
    bool WriteSequence(string sequence);

    /// <summary>
    /// The console's window (GetConsoleWindow): the classic console's own window; or, for a terminal that uses ConPTY, a
    /// pseudo window without a taskbar button, which Windows Terminal owns and shows along with its own window; zero for
    /// none.
    /// </summary>
    IntPtr ConsoleWindow();

    /// <summary>The window at the end of <paramref name="window"/>'s chain of parents and owners (GetAncestor with GA_ROOTOWNER).</summary>
    IntPtr RootOwner(IntPtr window);

    /// <summary>
    /// True when <paramref name="window"/> has a taskbar button of its own: it is visible (a minimized window counts), and
    /// it is an app window, or one that nothing owns and that is not a tool window.
    /// </summary>
    bool HasTaskbarButton(IntPtr window);

    /// <summary>The name of <paramref name="window"/>'s class (GetClassName), such as ConsoleWindowClass; null for none.</summary>
    string? ClassOf(IntPtr window);

    /// <summary>Flashes <paramref name="window"/>'s taskbar button until it comes to the front (FlashWindowEx with FLASHW_TRAY and FLASHW_TIMERNOFG).</summary>
    void Flash(IntPtr window);
}

/// <summary>
/// Shows a run's state on the console window ClassScribe runs in (design 1.13), never with a sound:
/// <list type="bullet">
/// <item>the title through SetConsoleTitle, in every host;</item>
/// <item>the taskbar in Windows Terminal through the OSC 9;4 sequence, ended with ESC and backslash (ST) and never with
/// a BEL, since a BEL that a host does not read as the end of a sequence rings, Windows Terminal's bell is audible by
/// default, and any sound would be recorded. Windows Terminal is known by WT_SESSION, which it sets for the programs it
/// starts, or by its own window owning the console window, as it does for a console Windows hands to it when
/// ClassScribe starts from a shortcut or a double-click. On the classic console, whose window has a taskbar button of
/// its own, the taskbar list sets that button;</item>
/// <item>the flash on the root owner of the console window: the console window itself in the classic console, and
/// Windows Terminal's own window under ConPTY. When that has no taskbar button, as in the VS Code terminal, on the
/// window of the first parent process that shows one (<see cref="WindowFinder"/>). A process without a console window
/// flashes nothing.</item>
/// </list>
/// Nothing at all is shown when the output is redirected. The same title or taskbar state twice is set once. A signal
/// that fails is logged as a warning once, then only for debugging, and never thrown: it runs in the recording's loop,
/// and the log says the same in words. At the end the window is left as it was found: the taskbar state cleared and
/// the console's own title back.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ConsoleWindowSignals(ILogger<ConsoleWindowSignals> logger) : IWindowSignals, IDisposable
{
    /// <summary>The signals, each logged as a warning on its first failure only.</summary>
    private enum Signal
    {
        /// <summary>The title.</summary>
        Title,

        /// <summary>Windows Terminal's sequence.</summary>
        Terminal,

        /// <summary>The classic console's taskbar button.</summary>
        Taskbar,

        /// <summary>The flash.</summary>
        Flash
    }

    /// <summary>The class of Windows Terminal's own window, which owns the pseudo console window of each of its tabs.</summary>
    private const string TerminalWindowClass = "CASCADIA_HOSTING_WINDOW_CLASS";

    private readonly IConsoleWindowApi _api = NativeConsoleWindowApi.Instance;
    private readonly ITaskbarProgress _taskbar = new TaskbarList();
    private readonly WindowFinder _finder = new(NativeProcessTree.Instance);
    private readonly Lock _gate = new();

    /// <summary>The signals whose failure was logged as a warning already.</summary>
    private readonly HashSet<Signal> _failed = [];

    /// <summary>The title set last, or null while ClassScribe has set none.</summary>
    private string? _title;

    /// <summary>The console's own title, read before ClassScribe set one, to put back at the end; null when it could not be read.</summary>
    private string? _originalTitle;

    /// <summary>True once the console's own title was read, or tried.</summary>
    private bool _originalTitleRead;

    /// <summary>The taskbar state shown last, with its percent; nothing, until ClassScribe shows one.</summary>
    private (TaskbarState State, int Percent) _shown = (TaskbarState.None, 0);

    private bool _disposed;

    /// <summary>Shows the signals through <paramref name="api"/>, <paramref name="taskbar"/> and <paramref name="finder"/> instead of Windows' own. Tests pass fakes.</summary>
    internal ConsoleWindowSignals(IConsoleWindowApi api, ITaskbarProgress taskbar, WindowFinder finder, ILogger<ConsoleWindowSignals> logger)
        : this(logger)
    {
        _api = api;
        _taskbar = taskbar;
        _finder = finder;
    }

    /// <summary>
    /// Windows Terminal's OSC 9;4 sequence for <paramref name="state"/>: ESC ] 9 ; 4 ; state ; percent, ended with ESC and
    /// backslash (ST), never with a BEL. Windows Terminal's states: 0 none, 1 normal, 2 error, 3 indeterminate, 4 paused.
    /// </summary>
    internal static string ProgressSequence(TaskbarState state, int percent)
    {
        var terminalState = state switch
        {
            TaskbarState.None => 0,
            TaskbarState.Normal => 1,
            TaskbarState.Error => 2,
            TaskbarState.Indeterminate => 3,
            TaskbarState.Paused => 4,
            _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Not a taskbar state.")
        };

        return string.Create(CultureInfo.InvariantCulture, $"\u001b]9;4;{terminalState};{percent}\u001b\\");
    }

    /// <inheritdoc />
    public void SetTitle(string title)
    {
        if (_api.OutputRedirected) return;

        var clean = TitleText.Clean(title);
        lock (_gate)
        {
            if (_disposed || clean == _title) return;

            if (!_originalTitleRead)
            {
                // Read once, before ClassScribe sets a title of its own; one that cannot be read is not put back.
                _originalTitleRead = true;
                TryShow(Signal.Title, () => _originalTitle = _api.ReadTitle());
            }

            TryShow(Signal.Title, () =>
            {
                _api.WriteTitle(clean);
                _title = clean;
            });
        }
    }

    /// <inheritdoc />
    public void SetTaskbar(TaskbarState state, int percent = 0)
    {
        if (_api.OutputRedirected) return;

        (TaskbarState State, int Percent) shown = (state, ShownPercent(state, percent));
        lock (_gate)
        {
            // Noted as shown even when showing it fails, so that a signal that fails is tried again at the next new
            // state, not once a second.
            if (_disposed || shown == _shown) return;
            _shown = shown;
            ShowTaskbar(shown.State, shown.Percent);
        }
    }

    /// <inheritdoc />
    public void Flash()
    {
        if (_api.OutputRedirected) return;

        lock (_gate)
        {
            if (_disposed) return;

            TryShow(Signal.Flash, () =>
            {
                var target = FlashTarget();
                if (target != 0) _api.Flash(target);
            });
        }
    }

    /// <summary>
    /// Leaves the window as it was found: the taskbar state cleared, when ClassScribe showed one, and the console's own
    /// title back, when ClassScribe changed it and could read it first. A flash goes on until the window comes forward.
    /// Disposing again does nothing.
    /// </summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;

            if (_shown.State != TaskbarState.None) ShowTaskbar(TaskbarState.None, 0);
            if (_title is not null && _originalTitle is { } original) TryShow(Signal.Title, () => _api.WriteTitle(original));
            _taskbar.Dispose();
        }
    }

    /// <summary>
    /// How far a bar of <paramref name="state"/> is filled: <paramref name="percent"/> kept between 0 and 100; 0 for a
    /// state without a bar; and 100 for a yellow or red bar given 0, which would not show its colour.
    /// </summary>
    private static int ShownPercent(TaskbarState state, int percent) => state switch
    {
        TaskbarState.None or TaskbarState.Indeterminate => 0,
        TaskbarState.Paused or TaskbarState.Error when percent <= 0 => 100,
        _ => Math.Clamp(percent, 0, 100)
    };

    /// <summary>Shows a taskbar state: as Windows Terminal's sequence when Windows Terminal shows the console, and on the classic console's own button. Call with the lock held.</summary>
    private void ShowTaskbar(TaskbarState state, int percent)
    {
        var console = _api.ConsoleWindow();
        if (InWindowsTerminal(console))
        {
            TryShow(Signal.Terminal, () =>
            {
                if (!_api.WriteSequence(ProgressSequence(state, percent)))
                    logger.LogDebug("The console did not take Windows Terminal's taskbar sequence; the title and the log still show the recording's state");
            });
        }

        // Only the classic console's own window has a taskbar button; under ConPTY the console window is a pseudo window
        // without one, even while Windows Terminal shows it.
        if (console != 0 && _api.HasTaskbarButton(console)) TryShow(Signal.Taskbar, () => _taskbar.Set(console, state, percent));
    }

    /// <summary>
    /// True when Windows Terminal shows this console: WT_SESSION is set, as for the programs Windows Terminal starts, or
    /// Windows Terminal's own window is the root owner of the console window. The second is how a console Windows handed
    /// to Windows Terminal is known, as when ClassScribe starts from a shortcut or a double-click: Windows Terminal never
    /// sets the variable for a program it did not start, but it owns the pseudo console window of each of its tabs.
    /// </summary>
    private bool InWindowsTerminal(IntPtr console) =>
        _api.WindowsTerminalVariableSet
        || (console != 0 && string.Equals(_api.ClassOf(_api.RootOwner(console)), TerminalWindowClass, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The window to flash: the root owner of the console window when it has a taskbar button (the classic console's own
    /// window, or Windows Terminal's), otherwise the window of the first parent process that shows one, as VS Code does;
    /// zero when there is none. A process without a console window, such as the background recorder, flashes nothing: no
    /// one looks at it, and its parents' window belongs to whoever started it.
    /// </summary>
    private IntPtr FlashTarget()
    {
        var console = _api.ConsoleWindow();
        if (console == 0) return 0;

        var root = _api.RootOwner(console);
        return root != 0 && _api.HasTaskbarButton(root) ? root : _finder.FindFor(_api.ProcessId);
    }

    /// <summary>Shows one signal; a failure is logged as a warning the first time for that signal, and only for debugging after that. Call with the lock held.</summary>
    private void TryShow(Signal signal, Action show)
    {
        try
        {
            show();
        }
        catch (Exception ex)
        {
            // Deliberately not rethrown: the title, the taskbar and the flash only repeat at a glance what the log says
            // in words, and they are shown from the recording's loop, which a failure here must never stop. A failure
            // is logged as a warning once, so that a dry run shows it, and only for debugging after that, as it would
            // otherwise come back with every new state.
            if (_failed.Add(signal))
                logger.LogWarning(ex, "{Failure}", Describe(signal));
            else
                logger.LogDebug(ex, "{Failure} (again)", Describe(signal));
        }
    }

    /// <summary>What failed, in words, with what still shows the state.</summary>
    private static string Describe(Signal signal) => signal switch
    {
        Signal.Title => "Could not show the recording's state in the window's title; the log still shows it",
        Signal.Terminal => "Could not show the recording's state on Windows Terminal's tab and taskbar button; the title and the log still show it",
        Signal.Taskbar => "Could not show the recording's state on the window's taskbar button; the title and the log still show it",
        _ => "Could not flash the window's taskbar button; the title and the log still show what needs attention"
    };
}

/// <summary>Windows' own console and window functions, from kernel32 and user32.</summary>
[SupportedOSPlatform("windows")]
internal sealed class NativeConsoleWindowApi : IConsoleWindowApi
{
    /// <summary>The variable Windows Terminal sets for the programs it starts.</summary>
    private const string TerminalVariable = "WT_SESSION";

    /// <summary>STD_OUTPUT_HANDLE.</summary>
    private const int StandardOutput = -11;

    /// <summary>ENABLE_VIRTUAL_TERMINAL_PROCESSING: the console reads control sequences rather than printing them.</summary>
    private const uint VirtualTerminalProcessing = 0x0004;

    /// <summary>GA_ROOTOWNER: up the chain of parents and owners to its end.</summary>
    private const uint RootOwnerFlag = 3;

    /// <summary>FLASHW_TRAY: flash the taskbar button.</summary>
    private const uint FlashTray = 0x2;

    /// <summary>FLASHW_TIMERNOFG: flash until the window comes to the front.</summary>
    private const uint FlashUntilForeground = 0xC;

    /// <summary>The one instance; it holds nothing.</summary>
    public static NativeConsoleWindowApi Instance { get; } = new();

    /// <inheritdoc />
    public bool OutputRedirected => Console.IsOutputRedirected;

    /// <inheritdoc />
    public bool WindowsTerminalVariableSet => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(TerminalVariable));

    /// <inheritdoc />
    public int ProcessId => Environment.ProcessId;

    /// <inheritdoc />
    public string? ReadTitle() => Console.Title;

    /// <inheritdoc />
    public void WriteTitle(string title) => Console.Title = title;

    /// <inheritdoc />
    public bool WriteSequence(string sequence)
    {
        var output = GetStdHandle(StandardOutput);
        if (output == 0 || output == -1 || !GetConsoleMode(output, out var mode)) return false;

        // A console reads sequences only with VT processing on. It is turned on for this write alone and put back
        // after, since the console's mode is shared with the shell ClassScribe was started from.
        var turnedOn = (mode & VirtualTerminalProcessing) == 0;
        if (turnedOn && !SetConsoleMode(output, mode | VirtualTerminalProcessing)) return false;
        try
        {
            // Through Console.Out, so that the sequence lands between the log's lines, never inside one.
            Console.Out.Write(sequence);
            Console.Out.Flush();
            return true;
        }
        finally
        {
            if (turnedOn) SetConsoleMode(output, mode);
        }
    }

    /// <inheritdoc />
    public IntPtr ConsoleWindow() => GetConsoleWindow();

    /// <inheritdoc />
    public IntPtr RootOwner(IntPtr window) => window == 0 ? 0 : GetAncestor(window, RootOwnerFlag);

    /// <inheritdoc />
    public bool HasTaskbarButton(IntPtr window) => window != 0 && NativeProcessTree.HasTaskbarButton(window);

    /// <inheritdoc />
    public string? ClassOf(IntPtr window)
    {
        if (window == 0) return null;

        // A class name has at most 256 characters; GetClassName counts the end of the string in the buffer's size.
        var name = new char[257];
        var length = GetClassNameW(window, name, name.Length);
        return length > 0 ? new string(name, 0, length) : null;
    }

    /// <inheritdoc />
    public void Flash(IntPtr window)
    {
        var flash = new FlashInfo
        {
            Size = (uint)Marshal.SizeOf<FlashInfo>(),
            Window = window,
            Flags = FlashTray | FlashUntilForeground,
            Count = 0,
            Timeout = 0
        };

        // The result says whether the window was active before, not whether the flash worked; there is nothing to check.
        FlashWindowEx(ref flash);
    }

    /// <summary>Windows' FLASHWINFO.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct FlashInfo
    {
        /// <summary>The size of the structure, in bytes.</summary>
        public uint Size;

        /// <summary>The window to flash.</summary>
        public IntPtr Window;

        /// <summary>What to flash and until when.</summary>
        public uint Flags;

        /// <summary>How many times to flash; 0 with FLASHW_TIMERNOFG flashes until the window comes to the front.</summary>
        public uint Count;

        /// <summary>The time between flashes in milliseconds; 0 for the cursor's blink rate.</summary>
        public uint Timeout;
    }

    /// <summary>A standard handle of this process.</summary>
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern IntPtr GetStdHandle(int handle);

    /// <summary>A console handle's mode.</summary>
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetConsoleMode(IntPtr console, out uint mode);

    /// <summary>Sets a console handle's mode.</summary>
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetConsoleMode(IntPtr console, uint mode);

    /// <summary>The window of this process's console, or under ConPTY its hidden pseudo window; zero for none.</summary>
    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern IntPtr GetConsoleWindow();

    /// <summary>A window's ancestor of the kind asked for.</summary>
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern IntPtr GetAncestor(IntPtr window, uint flags);

    /// <summary>The name of a window's class, written into <paramref name="name"/>; the length written, or 0 when it fails.</summary>
    [DllImport("user32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(IntPtr window, [Out] char[] name, int size);

    /// <summary>Flashes a window's caption or taskbar button.</summary>
    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlashWindowEx(ref FlashInfo flash);
}
