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

    /// <summary>
    /// Drops the keys typed while nothing was reading the keyboard, such as during an upload, so that the
    /// next read waits for a key pressed after its prompt.
    /// </summary>
    void DiscardPendingKeys();

    /// <summary>Shows text without ending the line, such as a prompt.</summary>
    void Write(string text);

    /// <summary>Shows a line of text, such as a line of the menu.</summary>
    void WriteLine(string text = "");
}
