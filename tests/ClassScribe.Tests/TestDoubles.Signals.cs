using ClassScribe.Desktop;
using ClassScribe.Desktop.Windows;

namespace ClassScribe.Tests;

/// <summary>
/// A window a test can look at: every title set, every taskbar state shown, and how often it flashed. A title holding a
/// BEL fails the test at once, since a BEL that a host does not read as part of a sequence rings, and the class
/// recording would take the sound in.
/// </summary>
public sealed class FakeWindowSignals : IWindowSignals
{
    private readonly Lock _gate = new();
    private readonly List<string> _titles = [];
    private readonly List<(TaskbarState State, int Percent)> _taskbar = [];
    private int _flashes;

    /// <summary>Every title set, in order.</summary>
    public IReadOnlyList<string> Titles
    {
        get { lock (_gate) return [.. _titles]; }
    }

    /// <summary>The title set last, or null when none was.</summary>
    public string? Title
    {
        get { lock (_gate) return _titles.Count == 0 ? null : _titles[^1]; }
    }

    /// <summary>Every state the taskbar button was given, with its percent, in order.</summary>
    public IReadOnlyList<(TaskbarState State, int Percent)> TaskbarStates
    {
        get { lock (_gate) return [.. _taskbar]; }
    }

    /// <summary>The state the taskbar button was given last, with its percent, or null when it was given none.</summary>
    public (TaskbarState State, int Percent)? Taskbar
    {
        get { lock (_gate) return _taskbar.Count == 0 ? null : _taskbar[^1]; }
    }

    /// <summary>How many times the window was flashed.</summary>
    public int Flashes => Volatile.Read(ref _flashes);

    /// <inheritdoc />
    public void SetTitle(string title)
    {
        // An ordinal check: NUnit's Does.Contain compares with the culture, which ignores control characters, and so
        // finds a BEL in any text.
        Assert.That(title.Contains('\a'), Is.False, "a title must never hold a BEL, which can ring");
        lock (_gate) _titles.Add(title);
    }

    /// <inheritdoc />
    public void SetTaskbar(TaskbarState state, int percent = 0)
    {
        lock (_gate) _taskbar.Add((state, percent));
    }

    /// <inheritdoc />
    public void Flash() => Interlocked.Increment(ref _flashes);
}

/// <summary>
/// Windows' console and window functions as a test plays them: whether the output is redirected, whether Windows
/// Terminal's variable is set, which window is the console's, which windows own it, their classes, and which are
/// visible with or without a taskbar button. It keeps the titles set, the sequences written and the windows flashed,
/// and fails the test at once on a BEL, which can ring.
/// </summary>
internal sealed class FakeConsoleWindowApi : IConsoleWindowApi
{
    /// <summary>The class of Windows Terminal's own window, written out here so that a test checks the name ClassScribe looks for.</summary>
    public const string TerminalWindowClass = "CASCADIA_HOSTING_WINDOW_CLASS";

    /// <summary>The class of the pseudo console window that ConPTY makes for a terminal's console.</summary>
    public const string PseudoWindowClass = "PseudoConsoleWindow";

    /// <summary>The class of the classic console's own window.</summary>
    public const string ClassicWindowClass = "ConsoleWindowClass";

    private readonly Lock _gate = new();
    private readonly List<string> _titles = [];
    private readonly List<string> _sequences = [];
    private readonly List<IntPtr> _flashed = [];
    private readonly Dictionary<IntPtr, IntPtr> _owners = [];
    private readonly Dictionary<IntPtr, string> _classes = [];
    private readonly HashSet<IntPtr> _visible = [];
    private readonly HashSet<IntPtr> _withoutButton = [];

    /// <summary>True when the output is meant to go to a file or a pipe.</summary>
    public bool OutputRedirected { get; set; }

    /// <summary>True when WT_SESSION is meant to be set.</summary>
    public bool WindowsTerminalVariableSet { get; set; }

    /// <summary>This process's id, for the walk up its parents; 100 unless the test says otherwise.</summary>
    public int ProcessId { get; set; } = 100;

    /// <summary>The console's title before ClassScribe set one; "Windows PowerShell" unless the test says otherwise.</summary>
    public string? OriginalTitle { get; set; } = "Windows PowerShell";

    /// <summary>The console's window, as GetConsoleWindow returns it; zero for none.</summary>
    public IntPtr Window { get; set; }

    /// <summary>False to have the console refuse a sequence, as a console without VT processing does.</summary>
    public bool TakesSequences { get; set; } = true;

    /// <summary>An exception to throw when a title is set, or null to set it.</summary>
    public Exception? TitleFailure { get; set; }

    /// <summary>Every title set, in order.</summary>
    public IReadOnlyList<string> Titles
    {
        get { lock (_gate) return [.. _titles]; }
    }

    /// <summary>The console's title now: the last one set, or the one it had.</summary>
    public string? Title
    {
        get { lock (_gate) return _titles.Count == 0 ? OriginalTitle : _titles[^1]; }
    }

    /// <summary>Every sequence written, in order.</summary>
    public IReadOnlyList<string> Sequences
    {
        get { lock (_gate) return [.. _sequences]; }
    }

    /// <summary>Every window flashed, in order.</summary>
    public IReadOnlyList<IntPtr> Flashed
    {
        get { lock (_gate) return [.. _flashed]; }
    }

    /// <summary>
    /// The classic console: a visible console window of its own, with a taskbar button, which is its own root owner,
    /// outside Windows Terminal.
    /// </summary>
    public FakeConsoleWindowApi AsClassicConsole(IntPtr window)
    {
        Window = window;
        Classify(window, ClassicWindowClass);
        Show(window);
        WindowsTerminalVariableSet = false;
        return this;
    }

    /// <summary>
    /// Windows Terminal running ClassScribe in one of its tabs, as for a program started there: WT_SESSION is set, and
    /// the terminal's own window owns the pseudo console window.
    /// </summary>
    public FakeConsoleWindowApi AsWindowsTerminal(IntPtr pseudoWindow, IntPtr terminalWindow)
    {
        AsHandedToWindowsTerminal(pseudoWindow, terminalWindow);
        WindowsTerminalVariableSet = true;
        return this;
    }

    /// <summary>
    /// A console Windows handed to Windows Terminal, as for ClassScribe started from a shortcut or a double-click while
    /// Windows Terminal is the default terminal: as for a program started in it, the terminal's own window owns the
    /// pseudo console window and shows it along with itself, without a taskbar button (Windows Terminal's GH#12515); but
    /// WT_SESSION is not set, since Windows Terminal did not start the program.
    /// </summary>
    public FakeConsoleWindowApi AsHandedToWindowsTerminal(IntPtr pseudoWindow, IntPtr terminalWindow)
    {
        Window = pseudoWindow;
        Classify(pseudoWindow, PseudoWindowClass);
        Own(pseudoWindow, terminalWindow);
        Show(pseudoWindow, taskbarButton: false);
        Classify(terminalWindow, TerminalWindowClass);
        Show(terminalWindow);
        WindowsTerminalVariableSet = false;
        return this;
    }

    /// <summary>
    /// The VS Code terminal: a hidden pseudo console window that nothing owns, and no WT_SESSION. Its window belongs to a
    /// process further up, which only <see cref="WindowFinder"/> finds.
    /// </summary>
    public FakeConsoleWindowApi AsVsCodeTerminal(IntPtr pseudoWindow)
    {
        Window = pseudoWindow;
        Classify(pseudoWindow, PseudoWindowClass);
        WindowsTerminalVariableSet = false;
        return this;
    }

    /// <summary>Makes <paramref name="owner"/> the owner of <paramref name="window"/>.</summary>
    public void Own(IntPtr window, IntPtr owner)
    {
        lock (_gate) _owners[window] = owner;
    }

    /// <summary>Gives <paramref name="window"/> the class <paramref name="className"/>.</summary>
    public void Classify(IntPtr window, string className)
    {
        lock (_gate) _classes[window] = className;
    }

    /// <summary>
    /// Makes <paramref name="window"/> visible, with a taskbar button of its own, or without one, as a tool window or an
    /// owned window has none.
    /// </summary>
    public void Show(IntPtr window, bool taskbarButton = true)
    {
        lock (_gate)
        {
            _visible.Add(window);
            if (!taskbarButton) _withoutButton.Add(window);
        }
    }

    /// <inheritdoc />
    public string? ReadTitle() => Title;

    /// <inheritdoc />
    public void WriteTitle(string title)
    {
        // Ordinal, as in FakeWindowSignals: NUnit's Does.Contain would find a BEL in any text.
        Assert.That(title.Contains('\a'), Is.False, "a title must never hold a BEL, which can ring");
        if (TitleFailure is { } failure) throw failure;
        lock (_gate) _titles.Add(title);
    }

    /// <inheritdoc />
    public bool WriteSequence(string sequence)
    {
        Assert.That(sequence.Contains('\a'), Is.False, "a sequence must never hold a BEL, which can ring");
        if (!TakesSequences) return false;
        lock (_gate) _sequences.Add(sequence);
        return true;
    }

    /// <inheritdoc />
    public IntPtr ConsoleWindow() => Window;

    /// <inheritdoc />
    public IntPtr RootOwner(IntPtr window)
    {
        lock (_gate)
        {
            // As GetAncestor with GA_ROOTOWNER: up the owners to the last; a window nothing owns is its own root owner.
            var root = window;
            while (_owners.TryGetValue(root, out var owner) && owner != 0) root = owner;
            return root;
        }
    }

    /// <inheritdoc />
    public bool HasTaskbarButton(IntPtr window)
    {
        lock (_gate) return window != 0 && _visible.Contains(window) && !_withoutButton.Contains(window);
    }

    /// <inheritdoc />
    public string? ClassOf(IntPtr window)
    {
        lock (_gate) return _classes.GetValueOrDefault(window);
    }

    /// <inheritdoc />
    public void Flash(IntPtr window)
    {
        lock (_gate) _flashed.Add(window);
    }
}

/// <summary>A taskbar button's progress as a test plays it: every state shown on a window, in order; it can be made to fail.</summary>
internal sealed class FakeTaskbarProgress : ITaskbarProgress
{
    private readonly Lock _gate = new();
    private readonly List<(IntPtr Window, TaskbarState State, int Percent)> _shown = [];

    /// <summary>An exception to throw when a state is shown, or null to show it.</summary>
    public Exception? Failure { get; set; }

    /// <summary>Every state shown, with its window and percent, in order; failed attempts included.</summary>
    public IReadOnlyList<(IntPtr Window, TaskbarState State, int Percent)> Shown
    {
        get { lock (_gate) return [.. _shown]; }
    }

    /// <summary>True once disposed.</summary>
    public bool Disposed { get; private set; }

    /// <inheritdoc />
    public void Set(IntPtr window, TaskbarState state, int percent)
    {
        lock (_gate) _shown.Add((window, state, percent));
        if (Failure is { } failure) throw failure;
    }

    /// <inheritdoc />
    public void Dispose() => Disposed = true;
}

/// <summary>
/// Windows' processes as a test lays them out: each with its parent, its program, when it started and the window it
/// shows, if any. A start time left out stands for one that cannot be read.
/// </summary>
internal sealed class FakeProcessTree : IProcessTree
{
    private readonly Dictionary<int, (ProcessFacts Facts, DateTimeOffset? Started, IntPtr Window)> _processes = [];

    /// <summary>How many times the process list was read.</summary>
    public int Lists { get; private set; }

    /// <summary>Adds process <paramref name="id"/>, started by <paramref name="parent"/>, and returns the tree.</summary>
    public FakeProcessTree Add(int id, int parent, string exe, DateTimeOffset? started, IntPtr window = 0)
    {
        _processes[id] = (new ProcessFacts(parent, exe), started, window);
        return this;
    }

    /// <inheritdoc />
    public IReadOnlyDictionary<int, ProcessFacts> List()
    {
        Lists++;
        return _processes.ToDictionary(process => process.Key, process => process.Value.Facts);
    }

    /// <inheritdoc />
    public DateTimeOffset? StartedUtc(int processId) => _processes.TryGetValue(processId, out var process) ? process.Started : null;

    /// <inheritdoc />
    public IntPtr WindowOf(int processId) => _processes.TryGetValue(processId, out var process) ? process.Window : 0;
}

/// <summary>Windows' taskbar list as a test plays it: the calls made on it, in order, each answered with S_OK unless told otherwise.</summary>
internal sealed class FakeTaskbarList3 : ITaskbarList3
{
    private readonly List<string> _calls = [];

    /// <summary>The calls made, such as "HrInit", "SetProgressValue 0x10 40/100" and "SetProgressState 0x10 2".</summary>
    public IReadOnlyList<string> Calls => [.. _calls];

    /// <summary>The result SetProgressState answers with; S_OK (0) unless the test says otherwise.</summary>
    public int StateResult { get; set; }

    /// <inheritdoc />
    public int HrInit()
    {
        _calls.Add("HrInit");
        return 0;
    }

    /// <inheritdoc />
    public int AddTab(IntPtr window) => Unexpected(nameof(AddTab));

    /// <inheritdoc />
    public int DeleteTab(IntPtr window) => Unexpected(nameof(DeleteTab));

    /// <inheritdoc />
    public int ActivateTab(IntPtr window) => Unexpected(nameof(ActivateTab));

    /// <inheritdoc />
    public int SetActiveAlt(IntPtr window) => Unexpected(nameof(SetActiveAlt));

    /// <inheritdoc />
    public int MarkFullscreenWindow(IntPtr window, bool fullscreen) => Unexpected(nameof(MarkFullscreenWindow));

    /// <inheritdoc />
    public int SetProgressValue(IntPtr window, ulong completed, ulong total)
    {
        _calls.Add($"SetProgressValue 0x{window:X} {completed}/{total}");
        return 0;
    }

    /// <inheritdoc />
    public int SetProgressState(IntPtr window, int flags)
    {
        _calls.Add($"SetProgressState 0x{window:X} {flags}");
        return StateResult;
    }

    /// <summary>Fails the test: ClassScribe only shows progress, and never touches the taskbar's tabs.</summary>
    private static int Unexpected(string call)
    {
        Assert.Fail($"ITaskbarList3.{call} was called; ClassScribe only shows progress");
        return 0;
    }
}
