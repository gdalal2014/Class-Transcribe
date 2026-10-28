using System.Runtime.InteropServices;
using System.Text;

namespace ClassScribe.Commands;

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
    public void DiscardPendingKeys()
    {
        if (!IsInteractive) return;

        while (Console.KeyAvailable) Console.ReadKey(intercept: true);
    }

    /// <inheritdoc />
    public void Write(string text) => Console.Write(text);

    /// <inheritdoc />
    public void WriteLine(string text = "") => Console.WriteLine(text);

    /// <summary>
    /// Lets the console show every character the app prints: Arabic and IJMES letters in names and
    /// paths, and device names such as "Intel® Smart Sound". A console opened on its own (a double-click,
    /// the published exe) otherwise keeps its old code page, such as 437, and prints those as ? or
    /// without their marks. UTF-16 goes through the console's own wide-character calls and, unlike
    /// UTF-8, leaves the window's code page, which it shares with the shell it was started from, as it
    /// was. Keys are already read as UTF-16. Call it before anything writes to the console: the logger
    /// keeps the writer it finds when it starts. Output sent to a file or a pipe is left alone, because
    /// the setting covers both output and errors, and a file should not receive UTF-16.
    /// </summary>
    public static void UseUnicode()
    {
        if (OperatingSystem.IsWindows() && !Console.IsOutputRedirected && !Console.IsErrorRedirected)
            Console.OutputEncoding = Encoding.Unicode;
    }

    /// <summary>
    /// For settings and class, whose output a script reads through a pipe (layout-config 1.8): when standard output is
    /// redirected, it is written in UTF-8 without a byte-order mark rather than in the console's code page, in which
    /// Ṣughrā would arrive as ?. Only the writer changes, never the console's code page, which the window shares with the
    /// shell. Call it before anything writes to the console, as <see cref="UseUnicode"/>.
    /// </summary>
    public static void UseUtf8WhenRedirected()
    {
        if (Console.IsOutputRedirected) Console.SetOut(Utf8Writer(Console.OpenStandardOutput()));
    }

    /// <summary>A writer of <paramref name="stream"/> in UTF-8 without a byte-order mark, which flushes each write.</summary>
    public static TextWriter Utf8Writer(Stream stream) => new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)) { AutoFlush = true };

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
