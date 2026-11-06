using ClassScribe.Storage;

namespace ClassScribe.Commands;

/// <summary>Asks at the keyboard for the name of a recording about to start, until one is given or Esc is pressed.</summary>
public sealed class RecordingNamePrompt(IConsoleUi ui)
{
    /// <summary>Returns the name, cleaned for use as a folder and file name, or null when Esc was pressed.</summary>
    public async Task<string?> AskAsync()
    {
        while (true)
        {
            ui.WriteLine("Name for this recording (Esc to go back):");
            ui.Write("> ");
            var typed = await ui.ReadLineAsync(CancellationToken.None);
            if (typed is null) return null;

            var name = FolderNames.SafeName(typed);
            if (name.Length > 0) return name;

            ui.WriteLine("A name is needed.");
        }
    }
}
