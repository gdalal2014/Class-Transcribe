using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using ClassScribe.Desktop;
using ClassScribe.Desktop.Windows;

namespace ClassScribe.Tests;

/// <summary>
/// The console window's signals (design 1.13): the title in every host; the taskbar through Windows Terminal's OSC 9;4
/// sequence, ended with ESC and backslash and never with a BEL, or through the classic console's taskbar button; the
/// flash on the window that shows the console; nothing at all when the output is redirected. Windows is played by fakes:
/// these tests never touch a real window or console.
/// </summary>
[TestFixture, Platform("Win")]
[SupportedOSPlatform("windows")]
public class ConsoleWindowSignalsTests
{
    /// <summary>The classic console's own window.</summary>
    private const nint ClassicWindow = 0x10;

    /// <summary>The hidden pseudo console window of a terminal that uses ConPTY.</summary>
    private const nint PseudoWindow = 0x20;

    /// <summary>Windows Terminal's own window.</summary>
    private const nint TerminalWindow = 0x30;

    /// <summary>VS Code's window, which belongs to VS Code's main process.</summary>
    private const nint VsCodeWindow = 0x70;

    /// <summary>When the processes of the examples started: each a minute after its parent.</summary>
    private static readonly DateTimeOffset Boot = new(2026, 10, 15, 22, 0, 0, TimeSpan.Zero);

    private FakeConsoleWindowApi _api = null!;
    private FakeTaskbarProgress _taskbar = null!;
    private FakeProcessTree _processes = null!;
    private RecordingLogger<ConsoleWindowSignals> _log = null!;

    /// <summary>Creates stand-ins for Windows, a process tree laid out as in the VS Code terminal, and a log to inspect.</summary>
    [SetUp]
    public void SetUp()
    {
        _api = new FakeConsoleWindowApi { ProcessId = 100 };
        _taskbar = new FakeTaskbarProgress();
        _processes = new FakeProcessTree()
            .Add(100, 90, "ClassScribe.exe", Boot.AddMinutes(4))
            .Add(90, 80, "powershell.exe", Boot.AddMinutes(3))
            .Add(80, 70, "Code.exe", Boot.AddMinutes(2))
            .Add(70, 60, "Code.exe", Boot.AddMinutes(1), VsCodeWindow)
            .Add(60, 4, "explorer.exe", Boot, 0x60);
        _log = new RecordingLogger<ConsoleWindowSignals>();
    }

    /// <summary>The signals under test, over the stand-ins.</summary>
    private ConsoleWindowSignals Signals() => new(_api, _taskbar, new WindowFinder(_processes), _log);

    /// <summary>The OSC 9;4 sequence Windows Terminal reads, ended with ESC and backslash.</summary>
    private static string Sequence(int state, int percent) => $"\u001b]9;4;{state};{percent}\u001b\\";

    /// <summary>The title goes to the console in every host, with control characters taken out, so that none can ring.</summary>
    [Test]
    public void The_title_goes_to_the_console_without_control_characters()
    {
        _api.AsWindowsTerminal(PseudoWindow, TerminalWindow);
        using var signals = Signals();

        signals.SetTitle("REC 00:00:01 Ka\u0007lam | ClassScribe");

        Assert.That(_api.Titles, Is.EqualTo(new[] { "REC 00:00:01 Ka lam | ClassScribe" }));
    }

    /// <summary>The same title again is not set again: the console already shows it.</summary>
    [Test]
    public void The_same_title_again_is_not_set_again()
    {
        _api.AsClassicConsole(ClassicWindow);
        using var signals = Signals();

        signals.SetTitle("REC 00:00:01 Kalam | ClassScribe");
        signals.SetTitle("REC 00:00:01 Kalam | ClassScribe");
        signals.SetTitle("REC 00:00:02 Kalam | ClassScribe");

        Assert.That(_api.Titles, Is.EqualTo(new[] { "REC 00:00:01 Kalam | ClassScribe", "REC 00:00:02 Kalam | ClassScribe" }));
    }

    /// <summary>
    /// Each taskbar state has its OSC 9;4 sequence for Windows Terminal: ESC ] 9 ; 4 ; state ; percent, ended with ESC
    /// and backslash (ST), with Windows Terminal's state numbers (0 none, 1 normal, 2 error, 3 indeterminate, 4 paused).
    /// It never holds a BEL, which a host that does not read the sequence would ring. (The expected sequence is built
    /// here rather than given with each case: a test's name must not hold an escape character.)
    /// </summary>
    [TestCase(TaskbarState.None, 0, 0)]
    [TestCase(TaskbarState.Normal, 40, 1)]
    [TestCase(TaskbarState.Error, 100, 2)]
    [TestCase(TaskbarState.Indeterminate, 0, 3)]
    [TestCase(TaskbarState.Paused, 100, 4)]
    public void Each_taskbar_state_has_its_sequence_ended_with_ST(TaskbarState state, int percent, int terminalState)
    {
        var written = ConsoleWindowSignals.ProgressSequence(state, percent);

        // Ordinal checks: NUnit's Does.Contain and Does.EndWith compare with the culture, which ignores control
        // characters, so they would find a BEL in any text and an escape at the end of any text.
        Assert.That(written, Is.EqualTo($"\u001b]9;4;{terminalState};{percent}\u001b\\"));
        Assert.That(written.EndsWith("\u001b\\", StringComparison.Ordinal), Is.True, "ended with ESC and backslash");
        Assert.That(written.Contains('\a'), Is.False, "never a BEL");
    }

    /// <summary>Under Windows Terminal each new taskbar state is written as its sequence, and the same state again is not.</summary>
    [Test]
    public void Under_Windows_Terminal_each_new_taskbar_state_is_written_as_its_sequence()
    {
        _api.AsWindowsTerminal(PseudoWindow, TerminalWindow);
        using var signals = Signals();

        signals.SetTaskbar(TaskbarState.Normal, 40);
        signals.SetTaskbar(TaskbarState.Normal, 40);
        signals.SetTaskbar(TaskbarState.Error, 100);
        signals.SetTaskbar(TaskbarState.None);

        Assert.That(_api.Sequences, Is.EqualTo(new[] { Sequence(1, 40), Sequence(2, 100), Sequence(0, 0) }));
    }

    /// <summary>
    /// The sequence is written only under Windows Terminal (WT_SESSION set, or its window owning the console window):
    /// neither the classic console, nor the VS Code terminal, nor another terminal whose own window owns the console
    /// window gets one.
    /// </summary>
    [Test]
    public void The_sequence_is_written_only_under_Windows_Terminal()
    {
        var classic = _api.AsClassicConsole(ClassicWindow);
        using (var signals = Signals()) signals.SetTaskbar(TaskbarState.Error, 100);

        var vsCode = _api = new FakeConsoleWindowApi().AsVsCodeTerminal(PseudoWindow);
        using (var signals = Signals()) signals.SetTaskbar(TaskbarState.Error, 100);

        var other = _api = new FakeConsoleWindowApi().AsVsCodeTerminal(PseudoWindow);
        other.Own(PseudoWindow, VsCodeWindow);
        other.Classify(VsCodeWindow, "Chrome_WidgetWin_1");
        other.Show(VsCodeWindow);
        using (var signals = Signals()) signals.SetTaskbar(TaskbarState.Error, 100);

        Assert.That(classic.Sequences, Is.Empty);
        Assert.That(vsCode.Sequences, Is.Empty);
        Assert.That(other.Sequences, Is.Empty);
    }

    /// <summary>
    /// A console Windows handed to Windows Terminal gets the sequence too, from the first state to the clearing at the
    /// end, though WT_SESSION is not set: ClassScribe started from a shortcut or a double-click is handed over that way
    /// when Windows Terminal is the default terminal, and Windows Terminal sets the variable only for the programs it
    /// starts itself. Its own window owns the console window, and is the one flashed.
    /// </summary>
    [Test]
    public void A_console_handed_to_Windows_Terminal_gets_the_sequence_without_its_variable()
    {
        _api.AsHandedToWindowsTerminal(PseudoWindow, TerminalWindow);
        var signals = Signals();

        signals.SetTaskbar(TaskbarState.Error, 100);
        signals.Flash();
        signals.Dispose();

        Assert.That(_api.Sequences, Is.EqualTo(new[] { Sequence(2, 100), Sequence(0, 0) }));
        Assert.That(_taskbar.Shown, Is.Empty, "the pseudo console window has no taskbar button of its own");
        Assert.That(_api.Flashed, Is.EqualTo(new[] { TerminalWindow }));
    }

    /// <summary>On the classic console, the console window's own taskbar button shows the state, through the taskbar list.</summary>
    [Test]
    public void On_the_classic_console_its_taskbar_button_shows_the_state()
    {
        _api.AsClassicConsole(ClassicWindow);
        using var signals = Signals();

        signals.SetTaskbar(TaskbarState.Paused, 100);
        signals.SetTaskbar(TaskbarState.Normal, 40);

        Assert.That(_taskbar.Shown, Is.EqualTo(new[] { (ClassicWindow, TaskbarState.Paused, 100), (ClassicWindow, TaskbarState.Normal, 40) }));
    }

    /// <summary>
    /// Under Windows Terminal and VS Code, the pseudo console window has no taskbar button of its own, and is left alone,
    /// even while Windows Terminal shows it, as it does along with its own window.
    /// </summary>
    [Test]
    public void A_pseudo_console_window_is_left_alone_even_while_shown()
    {
        _api.AsWindowsTerminal(PseudoWindow, TerminalWindow);
        using (var signals = Signals()) signals.SetTaskbar(TaskbarState.Error, 100);

        _api = new FakeConsoleWindowApi().AsVsCodeTerminal(PseudoWindow);
        using (var signals = Signals()) signals.SetTaskbar(TaskbarState.Error, 100);

        Assert.That(_taskbar.Shown, Is.Empty);
    }

    /// <summary>
    /// A pseudo console window that its terminal shows without owning it is passed over for the flash: it has no taskbar
    /// button to flash, so the flash goes to the first parent with a window, as in the VS Code terminal.
    /// </summary>
    [Test]
    public void A_shown_pseudo_console_window_that_nothing_owns_is_passed_over_for_the_flash()
    {
        _api.AsVsCodeTerminal(PseudoWindow);
        _api.Show(PseudoWindow, taskbarButton: false);
        using var signals = Signals();

        signals.Flash();

        Assert.That(_api.Flashed, Is.EqualTo(new[] { VsCodeWindow }));
    }

    /// <summary>A percent outside 0 to 100 is kept inside; a state without a bar carries 0.</summary>
    [Test]
    public void A_percent_is_kept_between_0_and_100_and_a_state_without_a_bar_carries_0()
    {
        _api.AsClassicConsole(ClassicWindow);
        _api.WindowsTerminalVariableSet = true;
        using var signals = Signals();

        signals.SetTaskbar(TaskbarState.Normal, 140);
        signals.SetTaskbar(TaskbarState.Normal, -5);
        signals.SetTaskbar(TaskbarState.Indeterminate, 50);

        Assert.That(_taskbar.Shown.Select(shown => shown.Percent), Is.EqualTo(new[] { 100, 0, 0 }));
        Assert.That(_api.Sequences, Is.EqualTo(new[] { Sequence(1, 100), Sequence(1, 0), Sequence(3, 0) }));
    }

    /// <summary>
    /// A yellow or red bar given no percent, or 0, is filled: left empty it would not show its colour. A green bar keeps
    /// 0, the start of an upload.
    /// </summary>
    [Test]
    public void A_yellow_or_red_bar_given_no_percent_is_filled_so_that_its_colour_shows()
    {
        _api.AsClassicConsole(ClassicWindow);
        _api.WindowsTerminalVariableSet = true;
        using var signals = Signals();

        signals.SetTaskbar(TaskbarState.Error);
        signals.SetTaskbar(TaskbarState.Paused, -5);
        signals.SetTaskbar(TaskbarState.Normal);

        Assert.That(_taskbar.Shown, Is.EqualTo(new[]
        {
            (ClassicWindow, TaskbarState.Error, 100), (ClassicWindow, TaskbarState.Paused, 100), (ClassicWindow, TaskbarState.Normal, 0)
        }));
        Assert.That(_api.Sequences, Is.EqualTo(new[] { Sequence(2, 100), Sequence(4, 100), Sequence(1, 0) }));
    }

    /// <summary>
    /// The flash goes to the root owner of the console window: the console window itself on the classic console, and
    /// Windows Terminal's own window, which owns the hidden pseudo console window, under Windows Terminal.
    /// </summary>
    [Test]
    public void The_flash_goes_to_the_root_owner_of_the_console_window()
    {
        _api.AsClassicConsole(ClassicWindow);
        using (var signals = Signals()) signals.Flash();
        Assert.That(_api.Flashed, Is.EqualTo(new[] { ClassicWindow }));

        _api = new FakeConsoleWindowApi().AsWindowsTerminal(PseudoWindow, TerminalWindow);
        using (var signals = Signals()) signals.Flash();
        Assert.That(_api.Flashed, Is.EqualTo(new[] { TerminalWindow }));
    }

    /// <summary>
    /// In the VS Code terminal nothing owns the hidden pseudo console window, so the flash goes to the first process up
    /// the parents that shows a window: VS Code's main process, past the shell and VS Code's terminal host.
    /// </summary>
    [Test]
    public void In_the_VS_Code_terminal_the_flash_goes_to_the_first_parent_with_a_window()
    {
        _api.AsVsCodeTerminal(PseudoWindow);
        using var signals = Signals();

        signals.Flash();

        Assert.That(_api.Flashed, Is.EqualTo(new[] { VsCodeWindow }));
    }

    /// <summary>Each flash is its own: a second one flashes again, so that each new thing that needs attention is seen.</summary>
    [Test]
    public void Each_flash_flashes_again()
    {
        _api.AsClassicConsole(ClassicWindow);
        using var signals = Signals();

        signals.Flash();
        signals.Flash();

        Assert.That(_api.Flashed, Is.EqualTo(new[] { ClassicWindow, ClassicWindow }));
    }

    /// <summary>With no window to flash anywhere, nothing is flashed and nothing is thrown.</summary>
    [Test]
    public void With_no_window_to_flash_nothing_is_flashed()
    {
        _api.AsVsCodeTerminal(PseudoWindow);
        _processes = new FakeProcessTree().Add(100, 90, "ClassScribe.exe", Boot.AddMinutes(1)).Add(90, 4, "cmd.exe", Boot);
        using var signals = Signals();

        Assert.DoesNotThrow(signals.Flash);
        Assert.That(_api.Flashed, Is.Empty);
        Assert.That(_log.Problems, Is.Empty);
    }

    /// <summary>
    /// A process without a console window, as the background recorder started without one, flashes nothing: no one
    /// looks at it, and the walk up its parents would find the window of whoever started it, which shows its own view.
    /// </summary>
    [Test]
    public void Without_a_console_window_nothing_is_flashed()
    {
        _api.Window = 0;
        using var signals = Signals();

        signals.Flash();

        Assert.That(_api.Flashed, Is.Empty);
        Assert.That(_processes.Lists, Is.Zero, "the parents are not even looked at");
    }

    /// <summary>
    /// Nothing is shown when the output is redirected, as when a script captures it: no title, no sequence, no taskbar
    /// button and no flash, in any host; and so nothing is put back at the end either.
    /// </summary>
    [Test]
    public void Nothing_is_shown_when_the_output_is_redirected()
    {
        _api.AsClassicConsole(ClassicWindow);
        _api.WindowsTerminalVariableSet = true;
        _api.OutputRedirected = true;
        var signals = Signals();

        signals.SetTitle("REC 00:00:01 Kalam | ClassScribe");
        signals.SetTaskbar(TaskbarState.Error, 100);
        signals.Flash();
        signals.Dispose();

        Assert.That(_api.Titles, Is.Empty);
        Assert.That(_api.Sequences, Is.Empty);
        Assert.That(_taskbar.Shown, Is.Empty);
        Assert.That(_api.Flashed, Is.Empty);
    }

    /// <summary>
    /// A signal that fails, here the taskbar button, is logged as a warning once and never thrown, since it runs in the
    /// recording's loop; later failures are only noted for debugging. It is tried again at the next new state, and the
    /// title and the flash work all the same.
    /// </summary>
    [Test]
    public void A_signal_that_fails_is_logged_once_and_never_thrown()
    {
        _api.AsClassicConsole(ClassicWindow);
        _taskbar.Failure = new COMException("The taskbar is not there.", unchecked((int)0x80004005));
        using var signals = Signals();

        Assert.DoesNotThrow(() => signals.SetTaskbar(TaskbarState.Normal, 100));
        Assert.DoesNotThrow(() => signals.SetTaskbar(TaskbarState.Normal, 100));
        Assert.DoesNotThrow(() => signals.SetTaskbar(TaskbarState.Error, 100));
        signals.SetTitle("! CANNOT WRITE Kalam | ClassScribe");
        signals.Flash();

        Assert.That(_taskbar.Shown, Has.Count.EqualTo(2), "tried again for the new state, not for the same one");
        Assert.That(_log.Problems, Is.EqualTo(new[]
        {
            "Could not show the recording's state on the window's taskbar button; the title and the log still show it"
        }));
        Assert.That(_log.Messages, Has.Count.EqualTo(2), "the second failure noted for debugging");
        Assert.That(_api.Titles, Is.EqualTo(new[] { "! CANNOT WRITE Kalam | ClassScribe" }));
        Assert.That(_api.Flashed, Is.EqualTo(new[] { ClassicWindow }));
    }

    /// <summary>A title the console refuses is logged as a warning once and never thrown.</summary>
    [Test]
    public void A_title_the_console_refuses_is_logged_once_and_never_thrown()
    {
        _api.AsClassicConsole(ClassicWindow);
        _api.TitleFailure = new IOException("The handle is invalid.");
        using var signals = Signals();

        Assert.DoesNotThrow(() => signals.SetTitle("REC 00:00:01 Kalam | ClassScribe"));
        Assert.DoesNotThrow(() => signals.SetTitle("REC 00:00:02 Kalam | ClassScribe"));

        Assert.That(_log.Problems, Is.EqualTo(new[] { "Could not show the recording's state in the window's title; the log still shows it" }));
    }

    /// <summary>
    /// A console that cannot take the sequence, such as one without VT processing, is left without it: no warning, since
    /// nothing is wrong, and the classic taskbar button still shows the state.
    /// </summary>
    [Test]
    public void A_console_that_cannot_take_the_sequence_is_left_without_it()
    {
        _api.AsClassicConsole(ClassicWindow);
        _api.WindowsTerminalVariableSet = true;
        _api.TakesSequences = false;
        using var signals = Signals();

        Assert.DoesNotThrow(() => signals.SetTaskbar(TaskbarState.Error, 100));

        Assert.That(_api.Sequences, Is.Empty);
        Assert.That(_log.Problems, Is.Empty);
        Assert.That(_taskbar.Shown, Is.EqualTo(new[] { (ClassicWindow, TaskbarState.Error, 100) }));
    }

    /// <summary>
    /// At the end, the window is left as it was found: the taskbar state is cleared, in Windows Terminal and on the
    /// classic console's button, and the console's own title comes back.
    /// </summary>
    [Test]
    public void At_the_end_the_window_is_left_as_it_was_found()
    {
        _api.AsClassicConsole(ClassicWindow);
        _api.WindowsTerminalVariableSet = true;
        var signals = Signals();
        signals.SetTitle("REC 00:00:01 Kalam | ClassScribe");
        signals.SetTitle("! FAILED Kalam | ClassScribe");
        signals.SetTaskbar(TaskbarState.Error, 100);

        signals.Dispose();
        signals.Dispose();

        Assert.That(_api.Titles, Is.EqualTo(new[] { "REC 00:00:01 Kalam | ClassScribe", "! FAILED Kalam | ClassScribe", "Windows PowerShell" }));
        Assert.That(_api.Sequences, Is.EqualTo(new[] { Sequence(2, 100), Sequence(0, 0) }));
        Assert.That(_taskbar.Shown, Is.EqualTo(new[] { (ClassicWindow, TaskbarState.Error, 100), (ClassicWindow, TaskbarState.None, 0) }));
        Assert.That(_taskbar.Disposed, Is.True);
    }

    /// <summary>A window that showed nothing is not touched at the end, and one whose title could not be read keeps the last title.</summary>
    [Test]
    public void At_the_end_only_what_was_changed_is_put_back()
    {
        _api.AsClassicConsole(ClassicWindow);
        _api.WindowsTerminalVariableSet = true;
        Signals().Dispose();
        Assert.That(_api.Titles, Is.Empty);
        Assert.That(_api.Sequences, Is.Empty);
        Assert.That(_taskbar.Shown, Is.Empty);

        _api.OriginalTitle = null;
        var signals = Signals();
        signals.SetTitle("DONE Kalam | ClassScribe");
        signals.SetTaskbar(TaskbarState.None);
        signals.Dispose();
        Assert.That(_api.Titles, Is.EqualTo(new[] { "DONE Kalam | ClassScribe" }));
        Assert.That(_api.Sequences, Is.Empty, "the taskbar never showed anything");
    }

    /// <summary>After the end, nothing is shown any more: a state that arrives late changes nothing.</summary>
    [Test]
    public void After_the_end_nothing_is_shown()
    {
        _api.AsClassicConsole(ClassicWindow);
        _api.WindowsTerminalVariableSet = true;
        var signals = Signals();
        signals.Dispose();

        signals.SetTitle("REC 00:00:01 Kalam | ClassScribe");
        signals.SetTaskbar(TaskbarState.Error, 100);
        signals.Flash();

        Assert.That(_api.Titles, Is.Empty);
        Assert.That(_api.Sequences, Is.Empty);
        Assert.That(_taskbar.Shown, Is.Empty);
        Assert.That(_api.Flashed, Is.Empty);
    }

    /// <summary>
    /// Windows' own console functions answer without an exception for this test process, whatever its console: only
    /// facts are read, and nothing is set.
    /// </summary>
    [Test, Category("WindowsProcess")]
    public void Windows_console_facts_are_read_without_an_exception()
    {
        var api = NativeConsoleWindowApi.Instance;

        Assert.DoesNotThrow(() =>
        {
            _ = api.OutputRedirected;
            var window = api.ConsoleWindow();
            _ = api.HasTaskbarButton(window);
            var root = window == 0 ? 0 : api.RootOwner(window);
            _ = api.ClassOf(root);
        });
        Assert.That(api.ProcessId, Is.EqualTo(Environment.ProcessId));
        Assert.That(api.HasTaskbarButton(0), Is.False);
        Assert.That(api.RootOwner(0), Is.EqualTo((nint)0));
        Assert.That(api.ClassOf(0), Is.Null);
    }

    /// <summary>
    /// Windows gives the class of a window of the test's own that is never shown (a static control, "Static"), and says
    /// it has no taskbar button, since it is not visible. Only facts are read.
    /// </summary>
    [Test, Category("WindowsProcess")]
    public void Windows_gives_the_class_of_a_hidden_window_of_the_test_s_own()
    {
        var api = NativeConsoleWindowApi.Instance;
        var window = HiddenWindow.Create();
        try
        {
            Assert.That(api.ClassOf(window), Is.EqualTo("Static").IgnoreCase);
            Assert.That(api.HasTaskbarButton(window), Is.False);
        }
        finally
        {
            HiddenWindow.Destroy(window);
        }
    }

    /// <summary>The WT_SESSION variable that Windows Terminal sets for the programs it starts is read.</summary>
    [Test, Category("WindowsProcess")]
    public void Windows_Terminal_s_variable_is_read()
    {
        var api = NativeConsoleWindowApi.Instance;

        using (new TemporaryVariable("WT_SESSION", "0b5a2b1e-3c1f-4c55-9a4d-6c4cbf6f6f01"))
            Assert.That(api.WindowsTerminalVariableSet, Is.True);
        using (new TemporaryVariable("WT_SESSION", null))
            Assert.That(api.WindowsTerminalVariableSet, Is.False);
    }

    /// <summary>
    /// Windows flashes a window of the test's own that is never shown, without an exception: it has no taskbar button,
    /// so nothing is seen, and a flash never makes a sound.
    /// </summary>
    [Test, Category("WindowsProcess")]
    public void Windows_flashes_a_hidden_window_of_the_test_s_own_without_an_exception()
    {
        var window = HiddenWindow.Create();
        try
        {
            Assert.DoesNotThrow(() => NativeConsoleWindowApi.Instance.Flash(window));
        }
        finally
        {
            HiddenWindow.Destroy(window);
        }
    }
}

/// <summary>
/// Finding the window that shows the console when nothing owns the console window, as in the VS Code terminal: up the
/// parent processes to the first that shows a window with a taskbar button.
/// </summary>
[TestFixture, Platform("Win")]
[SupportedOSPlatform("windows")]
public class WindowFinderTests
{
    /// <summary>When the processes of the examples started.</summary>
    private static readonly DateTimeOffset Boot = new(2026, 10, 15, 22, 0, 0, TimeSpan.Zero);

    /// <summary>VS Code's terminal: ClassScribe, its shell, VS Code's terminal host without a window, VS Code with one, and the Windows shell.</summary>
    private static FakeProcessTree VsCode() => new FakeProcessTree()
        .Add(100, 90, "ClassScribe.exe", Boot.AddMinutes(4))
        .Add(90, 80, "pwsh.exe", Boot.AddMinutes(3))
        .Add(80, 70, "Code.exe", Boot.AddMinutes(2))
        .Add(70, 60, "Code.exe", Boot.AddMinutes(1), 0x70)
        .Add(60, 4, "explorer.exe", Boot, 0x60);

    /// <summary>The walk goes up the parents to the first that shows a window, reading the process list once.</summary>
    [Test]
    public void The_walk_goes_up_to_the_first_parent_with_a_window()
    {
        var processes = VsCode();

        Assert.That(new WindowFinder(processes).FindFor(100), Is.EqualTo((nint)0x70));
        Assert.That(processes.Lists, Is.EqualTo(1));
    }

    /// <summary>
    /// A parent that started after its child is not the child's parent but a later process that was given the same id
    /// once the parent had ended, so the walk ends there rather than flash an unrelated window.
    /// </summary>
    [Test]
    public void A_parent_that_started_after_its_child_ends_the_walk()
    {
        var processes = VsCode().Add(80, 70, "Code.exe", Boot.AddMinutes(10));

        Assert.That(new WindowFinder(processes).FindFor(100), Is.EqualTo((nint)0));
    }

    /// <summary>
    /// A parent that has ended, or whose start cannot be read, ends the walk, even when it shows a window: its id may
    /// have gone to a process ClassScribe cannot open, such as an elevated one, that has nothing to do with it.
    /// </summary>
    [Test]
    public void A_parent_that_is_gone_or_cannot_be_read_ends_the_walk()
    {
        var gone = new FakeProcessTree().Add(100, 90, "ClassScribe.exe", Boot.AddMinutes(4));
        var unreadable = VsCode().Add(80, 70, "Code.exe", started: null, window: 0x80);

        Assert.That(new WindowFinder(gone).FindFor(100), Is.EqualTo((nint)0));
        Assert.That(new WindowFinder(unreadable).FindFor(100), Is.EqualTo((nint)0));
    }

    /// <summary>
    /// The Windows shell is never chosen: a window of explorer.exe would be File Explorer's, not the one that shows
    /// ClassScribe.
    /// </summary>
    [Test]
    public void The_Windows_shell_is_never_chosen()
    {
        var processes = new FakeProcessTree()
            .Add(100, 90, "ClassScribe.exe", Boot.AddMinutes(2))
            .Add(90, 60, "cmd.exe", Boot.AddMinutes(1))
            .Add(60, 4, "EXPLORER.EXE", Boot, 0x60);

        Assert.That(new WindowFinder(processes).FindFor(100), Is.EqualTo((nint)0));
    }

    /// <summary>Only so many parents are looked at, so that a long chain or a loop of ids ends the walk.</summary>
    [Test]
    public void Only_so_many_parents_are_looked_at()
    {
        var processes = new FakeProcessTree();
        var deepest = 1000;
        for (var depth = 0; depth <= WindowFinder.MostSteps + 1; depth++)
        {
            var id = deepest - depth;
            var window = depth == WindowFinder.MostSteps + 1 ? (nint)0x99 : 0;
            processes.Add(id, id - 1, $"p{depth}.exe", Boot.AddMinutes(-depth), window);
        }

        var loop = new FakeProcessTree()
            .Add(100, 90, "ClassScribe.exe", Boot)
            .Add(90, 100, "a.exe", Boot);

        Assert.That(new WindowFinder(processes).FindFor(deepest), Is.EqualTo((nint)0), "the window lies one step too far");
        Assert.That(new WindowFinder(loop).FindFor(100), Is.EqualTo((nint)0));
    }

    /// <summary>
    /// Windows' process list is read for this test process, its start time agrees with .NET's, an id that names no
    /// process has none, and a window found, if any, belongs to a process up this one's parents. Only facts are read.
    /// </summary>
    [Test, Category("WindowsProcess")]
    public void Windows_process_tree_is_read_and_a_window_found_belongs_to_a_parent()
    {
        var tree = NativeProcessTree.Instance;
        using var self = Process.GetCurrentProcess();

        var processes = tree.List();
        var started = tree.StartedUtc(Environment.ProcessId);
        var window = new WindowFinder(tree).FindFor(Environment.ProcessId);

        Assert.That(processes, Does.ContainKey(Environment.ProcessId));
        Assert.That(started, Is.Not.Null);
        Assert.That((started!.Value - new DateTimeOffset(self.StartTime.ToUniversalTime())).Duration(), Is.LessThan(TimeSpan.FromSeconds(1)));
        Assert.That(tree.StartedUtc(int.MaxValue - 2), Is.Null, "no process has that id");
        if (window != 0)
        {
            _ = HiddenWindow.GetWindowThreadProcessId(window, out var owner);
            var parents = new List<int>();
            var id = Environment.ProcessId;
            while (parents.Count < WindowFinder.MostSteps && processes.TryGetValue(id, out var facts))
            {
                parents.Add(facts.ParentId);
                id = facts.ParentId;
            }

            Assert.That(parents, Does.Contain(owner));
        }
    }
}

/// <summary>
/// The taskbar list: the state for each kind of bar, the value set before the state so that the colour fills the bar,
/// the list made and readied once, and made again after a failure, as when Explorer has restarted.
/// </summary>
[TestFixture, Platform("Win")]
[SupportedOSPlatform("windows")]
public class TaskbarListTests
{
    /// <summary>The window the examples show their state on.</summary>
    private const nint Window = 0x10;

    /// <summary>Each state has Windows' TBPFLAG: none 0, indeterminate 1, normal 2, error 4, paused 8.</summary>
    [TestCase(TaskbarState.None, 0)]
    [TestCase(TaskbarState.Indeterminate, 1)]
    [TestCase(TaskbarState.Normal, 2)]
    [TestCase(TaskbarState.Error, 4)]
    [TestCase(TaskbarState.Paused, 8)]
    public void Each_state_has_Windows_flag(TaskbarState state, int flags)
    {
        Assert.That(TaskbarList.Flags(state), Is.EqualTo(flags));
    }

    /// <summary>
    /// A state with a bar sets its value before its state, since setting a value turns a bar green; a state without one
    /// sets the state alone. The list is made and readied once.
    /// </summary>
    [Test]
    public void A_bar_gets_its_value_before_its_state_and_the_list_is_readied_once()
    {
        var fake = new FakeTaskbarList3();
        var made = 0;
        using var list = new TaskbarList(() =>
        {
            made++;
            return fake;
        });

        list.Set(Window, TaskbarState.Error, 100);
        list.Set(Window, TaskbarState.Normal, 40);
        list.Set(Window, TaskbarState.Indeterminate, 0);
        list.Set(Window, TaskbarState.None, 0);

        Assert.That(made, Is.EqualTo(1));
        Assert.That(fake.Calls, Is.EqualTo(new[]
        {
            "HrInit",
            "SetProgressValue 0x10 100/100", "SetProgressState 0x10 4",
            "SetProgressValue 0x10 40/100", "SetProgressState 0x10 2",
            "SetProgressState 0x10 1",
            "SetProgressState 0x10 0"
        }));
    }

    /// <summary>
    /// A call Windows refuses is thrown, for the caller to log, and the list is made again for the next state, as it must
    /// be after Explorer has restarted.
    /// </summary>
    [Test]
    public void A_refused_call_is_thrown_and_the_list_is_made_again()
    {
        var first = new FakeTaskbarList3 { StateResult = unchecked((int)0x80010108) };
        var second = new FakeTaskbarList3();
        var lists = new Queue<FakeTaskbarList3>([first, second]);
        using var list = new TaskbarList(lists.Dequeue);

        Assert.Throws<COMException>(() => list.Set(Window, TaskbarState.Error, 100));
        list.Set(Window, TaskbarState.Normal, 40);

        Assert.That(second.Calls, Is.EqualTo(new[] { "HrInit", "SetProgressValue 0x10 40/100", "SetProgressState 0x10 2" }));
    }

    /// <summary>After the end, the list is let go and nothing more is set.</summary>
    [Test]
    public void After_the_end_nothing_more_is_set()
    {
        var fake = new FakeTaskbarList3();
        var list = new TaskbarList(() => fake);
        list.Set(Window, TaskbarState.Normal, 40);

        list.Dispose();

        Assert.Throws<ObjectDisposedException>(() => list.Set(Window, TaskbarState.Error, 100));
        Assert.That(fake.Calls, Has.Count.EqualTo(3));
    }

    /// <summary>
    /// Each method of the declaration sits in the slot of the interface's table that shobjidl_core.h gives it: after
    /// IUnknown's three, ITaskbarList's five, ITaskbarList2's one, then ITaskbarList3's progress calls in slots 9 and 10.
    /// COM interop gives the slots in the order the methods are declared, which their metadata keeps. Windows answers
    /// S_OK to these calls whatever they carry, so no call can show a method in the wrong slot; this pins the order
    /// against an edit that moves or drops one, as NAudio's IAudioClient2 declaration did.
    /// </summary>
    [Test]
    public void Each_method_sits_in_the_slot_shobjidl_core_h_gives_it()
    {
        var type = typeof(ITaskbarList3);
        string[] header =
        [
            "HrInit", "AddTab", "DeleteTab", "ActivateTab", "SetActiveAlt", "MarkFullscreenWindow", "SetProgressValue", "SetProgressState"
        ];

        var declared = type.GetMethods().OrderBy(method => method.MetadataToken).Select(method => method.Name);

        Assert.That(declared, Is.EqualTo(header), "the methods in the header's order, none left out before SetProgressState");
        Assert.That(Marshal.GetStartComSlot(type), Is.EqualTo(3), "HrInit right after IUnknown's three");
        Assert.That(Marshal.GetEndComSlot(type), Is.EqualTo(10), "SetProgressState in slot 10");
    }

    /// <summary>
    /// Windows' own taskbar list is made, readied and given a state for a window of the test's own that is never shown,
    /// then cleared and let go: Windows makes the list for this multithreaded caller through the proxy it registers,
    /// HrInit and the calls answer without an exception, and nothing is seen, since that window has no taskbar button.
    /// It cannot show that each method sits in its slot, since Windows answers S_OK to these calls whatever they carry;
    /// the test above pins that.
    /// </summary>
    [Test, Category("WindowsProcess")]
    public void Windows_taskbar_list_takes_a_state_for_a_hidden_window_of_the_test_s_own()
    {
        var window = HiddenWindow.Create();
        try
        {
            using var list = new TaskbarList();
            Assert.DoesNotThrow(() => list.Set(window, TaskbarState.Error, 100));
            Assert.DoesNotThrow(() => list.Set(window, TaskbarState.None, 0));
        }
        finally
        {
            HiddenWindow.Destroy(window);
        }
    }
}

/// <summary>A window of the test's own that is never shown, for the tests that call Windows: it has no taskbar button, so nothing is seen.</summary>
[SupportedOSPlatform("windows")]
internal static class HiddenWindow
{
    /// <summary>Makes a static window without WS_VISIBLE, on the calling thread, which must also destroy it.</summary>
    public static nint Create()
    {
        var window = CreateWindowExW(0, "STATIC", "ClassScribe test window", 0, 0, 0, 0, 0, 0, 0, 0, 0);
        Assert.That(window, Is.Not.EqualTo((nint)0), $"Windows did not make the test's window (error {Marshal.GetLastPInvokeError()})");
        return window;
    }

    /// <summary>Destroys a window <see cref="Create"/> made.</summary>
    public static void Destroy(nint window) => DestroyWindow(window);

    /// <summary>The process and thread that made a window.</summary>
    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint GetWindowThreadProcessId(nint window, out int processId);

    /// <summary>Makes a window.</summary>
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowExW(uint extendedStyle, string className, string windowName, uint style, int x, int y, int width, int height,
        nint parent, nint menu, nint instance, nint parameter);

    /// <summary>Destroys a window.</summary>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(nint window);
}
