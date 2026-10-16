namespace ClassScribe.Commands;

/// <summary>
/// Keeps a console window that was opened for ClassScribe alone open at the end, until Enter is
/// pressed, so that its last lines can still be read: the window of a double-click, or of a file
/// dropped on the exe, closes the moment the process ends.
/// </summary>
public sealed class ClosePrompt(IConsoleUi ui)
{
    /// <summary>
    /// True when the end of the run should wait: someone can type, the window is ClassScribe's own, and
    /// there is something left to read. That is after a command given on the command line, such as a
    /// dropped file, and after any failure, including settings that kept the menu from starting. The
    /// menu, once left with Q, has nothing more to show.
    /// </summary>
    public static bool IsNeeded(string[] args, int exitCode, bool interactive, bool ownsWindow) =>
        interactive && ownsWindow && (args.Length > 0 || exitCode != 0);

    /// <summary>
    /// Asks for Enter and waits for it; Esc and Ctrl+C close the window too. Keys typed before the
    /// question appeared are dropped first: an Enter pressed during the upload, to check that the
    /// window was alive, would otherwise close it over its result before it could be read.
    /// </summary>
    public async Task WaitAsync()
    {
        ui.DiscardPendingKeys();
        ui.WriteLine();
        ui.WriteLine("Press Enter to close.");
        while (await ui.ReadKeyAsync(CancellationToken.None) is { } key && !Closes(key))
        {
            // Any other key leaves the window open.
        }
    }

    /// <summary>True for the keys that close the window: Enter, Esc and Ctrl+C.</summary>
    private static bool Closes(ConsoleKeyInfo key) =>
        key.Key is ConsoleKey.Enter or ConsoleKey.Escape || (key.Key == ConsoleKey.C && key.Modifiers.HasFlag(ConsoleModifiers.Control));
}
