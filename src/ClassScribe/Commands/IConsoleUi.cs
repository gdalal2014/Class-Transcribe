using System.Runtime.InteropServices;
using System.Text;

namespace ClassScribe.Commands;

/// <summary>The keyboard and screen of the console window, for the menu, its prompts and the keys used while recording.</summary>
public interface IConsoleUi
{
    /// <summary>True when a person can type into this console: its input is not redirected.</summary>
    bool IsInteractive { get; }

    /// <summary>
    /// Waits for the next key press, without showing it. Ctrl+C pressed meanwhile comes back as a Ctrl+C
    /// key press. Returns null once <paramref name="cancellationToken"/> is cancelled, and at once when
    /// nobody can type.
    /// </summary>
    Task<ConsoleKeyInfo?> ReadKeyAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Reads a line of text, such as a name or a path dragged into the window, showing it as it is
    /// typed. Returns null when Esc or Ctrl+C is pressed instead, when the read is cancelled, or when
    /// nobody can type.
    /// </summary>
    Task<string?> ReadLineAsync(CancellationToken cancellationToken);

    /// <summary>Shows text without ending the line, such as a prompt.</summary>
    void Write(string text);

    /// <summary>Shows a line of text, such as a line of the menu.</summary>
    void WriteLine(string text = "");
}

/// <summary>
/// The real console. Keys are polled rather than waited for, so a read can be abandoned the moment the
/// recording stops. Ctrl+C never ends the process from here: during a read it becomes a key press, and
/// otherwise the running command's own handler decides what it means.
/// </summary>
public sealed class ConsoleUi : IConsoleUi, IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(50);
    private static readonly ConsoleKeyInfo ControlC = new('\u0003', ConsoleKey.C, shift: false, alt: false, control: true);

    private volatile bool _interrupted;

    /// <summary>Hooks Ctrl+C.</summary>
    public ConsoleUi() => Console.CancelKeyPress += OnCancelKeyPress;

    /// <inheritdoc />
    public bool IsInteractive => !Console.IsInputRedirected;

    /// <inheritdoc />
    public async Task<ConsoleKeyInfo?> ReadKeyAsync(CancellationToken cancellationToken)
    {
        if (!IsInteractive) return null;

        // A Ctrl+C from before this read belonged to whatever was running then.
        _interrupted = false;
        while (!cancellationToken.IsCancellationRequested)
        {
            if (_interrupted)
            {
                _interrupted = false;
                return ControlC;
            }

            if (Console.KeyAvailable) return Console.ReadKey(intercept: true);
            await Task.Delay(PollInterval, CancellationToken.None);
        }

        return null;
    }

    /// <inheritdoc />
    public async Task<string?> ReadLineAsync(CancellationToken cancellationToken)
    {
        // Read key by key rather than with Console.ReadLine, so that Esc can mean "go back".
        var text = new StringBuilder();
        while (true)
        {
            var key = await ReadKeyAsync(cancellationToken);
            if (key is not { } pressed || pressed.Key == ConsoleKey.Escape || (pressed.Key == ConsoleKey.C && pressed.Modifiers.HasFlag(ConsoleModifiers.Control)))
            {
                Console.WriteLine();
                return null;
            }

            switch (pressed.Key)
            {
                case ConsoleKey.Enter:
                    Console.WriteLine();
                    return text.ToString();
                case ConsoleKey.Backspace:
                    if (text.Length > 0)
                    {
                        text.Length--;
                        Console.Write("\b \b");
                    }

                    break;
                default:
                    if (pressed.KeyChar != '\0' && !char.IsControl(pressed.KeyChar))
                    {
                        text.Append(pressed.KeyChar);
                        Console.Write(pressed.KeyChar);
                    }

                    break;
            }
        }
    }

    /// <inheritdoc />
    public void Write(string text) => Console.Write(text);

    /// <inheritdoc />
    public void WriteLine(string text = "") => Console.WriteLine(text);

    /// <summary>
    /// True when this process is the only one attached to its console. The window was then opened for
    /// it, by a double-click or a file dropped on the exe, and disappears as soon as the process ends.
    /// </summary>
    public static bool OwnsConsoleWindow()
    {
        if (!OperatingSystem.IsWindows()) return false;

        var processes = new uint[2];
        return GetConsoleProcessList(processes, (uint)processes.Length) == 1;
    }

    /// <summary>Unhooks Ctrl+C.</summary>
    public void Dispose() => Console.CancelKeyPress -= OnCancelKeyPress;

    /// <summary>Keeps the process alive on Ctrl+C; a key read in progress returns it as a key press.</summary>
    private void OnCancelKeyPress(object? sender, ConsoleCancelEventArgs e)
    {
        e.Cancel = true;
        _interrupted = true;
    }

    /// <summary>Lists the processes attached to this console; returns how many there are.</summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetConsoleProcessList(uint[] processList, uint processCount);
}
