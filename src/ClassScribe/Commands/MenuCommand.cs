using System.Globalization;
using ClassScribe.Storage;
using Microsoft.Extensions.Logging;

namespace ClassScribe.Commands;

/// <summary>
/// The menu shown when ClassScribe starts without arguments. One key picks a choice; when the choice
/// has run, successfully or not, the menu comes back, until Q or Ctrl+C leaves it.
/// </summary>
public sealed class MenuCommand(
    ICommandRunner commands,
    IConsoleUi ui,
    ClassFolders folders,
    IDesktop desktop,
    TimeProvider time,
    ILogger<MenuCommand> logger)
{
    /// <summary>How many recent classes choice 3 offers, so that one digit picks one.</summary>
    private const int RecentClasses = 9;

    /// <summary>
    /// The console log is written on a background thread. Waiting this long before showing the menu
    /// again lets the last lines of a choice appear above the menu rather than below it.
    /// </summary>
    private static readonly TimeSpan LogSettleTime = TimeSpan.FromMilliseconds(150);

    /// <summary>Shows the menu until it is left. Returns the exit code, which is 0.</summary>
    public async Task<int> RunAsync()
    {
        for (var first = true; ; first = false)
        {
            if (!first) await Task.Delay(LogSettleTime, time);
            ShowMenu();

            switch (await ReadChoiceAsync())
            {
                case null:
                    return 0;
                case '1':
                    await RunChoiceAsync(() => commands.RecordAsync(transcribe: false));
                    break;
                case '2':
                    await RunChoiceAsync(() => commands.RecordAsync(transcribe: true));
                    break;
                case '3':
                    await TranscribeRecentClassAsync();
                    break;
                case '4':
                    await TranscribeFileAsync();
                    break;
                case '5':
                    OpenRecordingsFolder();
                    break;
            }
        }
    }

    /// <summary>Lists the choices and where the files go.</summary>
    private void ShowMenu()
    {
        ui.WriteLine();
        ui.WriteLine("ClassScribe");
        ui.WriteLine($"Recordings and transcripts: {folders.Root}");
        ui.WriteLine();
        ui.WriteLine("  1  Record only");
        ui.WriteLine("  2  Record and transcribe");
        ui.WriteLine("  3  Transcribe a recent class");
        ui.WriteLine("  4  Transcribe a file");
        ui.WriteLine("  5  Open the recordings folder");
        ui.WriteLine("  Q  Quit");
        ui.WriteLine();
    }

    /// <summary>Waits for one of the menu's keys, ignoring any other. Returns null for Q, for Ctrl+C, or when nobody can type.</summary>
    private async Task<char?> ReadChoiceAsync()
    {
        while (true)
        {
            var key = await ui.ReadKeyAsync(CancellationToken.None);
            if (key is not { } pressed || IsQuit(pressed)) return null;
            if (pressed.KeyChar is >= '1' and <= '5') return pressed.KeyChar;
        }
    }

    /// <summary>Runs a choice. A failure is reported, and the menu carries on.</summary>
    private async Task RunChoiceAsync(Func<Task<int>> choice)
    {
        try
        {
            await choice();
        }
        catch (OperationCanceledException)
        {
            // Ctrl+C during an upload: a normal way back to the menu.
            logger.LogWarning("Cancelled");
        }
        catch (Exception ex)
        {
            // Deliberately not rethrown: the menu outlives a failed choice, and this log line is the report.
            logger.LogError(ex, "Stopped because of an error");
        }
    }

    /// <summary>Choice 3: lists the recent classes and transcribes the one picked.</summary>
    private async Task TranscribeRecentClassAsync()
    {
        var classes = folders.ListRecent(RecentClasses);
        if (classes.Count == 0)
        {
            ui.WriteLine($"No recorded classes in {folders.Root} yet.");
            return;
        }

        ui.WriteLine("Recent classes, newest first:");
        for (var i = 0; i < classes.Count; i++)
        {
            var recorded = classes[i];
            var length = recorded.Length.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);
            ui.WriteLine($"  {i + 1}  {recorded.Name}  {length}  {(recorded.HasTranscript ? "transcript ready" : "no transcript yet")}");
        }

        ui.WriteLine("Press a class's number to transcribe it, or Esc to go back.");
        if (await ReadNumberAsync(classes.Count) is not { } index) return;

        var chosen = classes[index];
        if (chosen.HasTranscript)
        {
            ui.WriteLine("This class already has a transcript, and transcribing it again is billed again. Press Y to go ahead, any other key to go back.");
            if (await ui.ReadKeyAsync(CancellationToken.None) is not { Key: ConsoleKey.Y }) return;
        }

        await RunChoiceAsync(() => commands.TranscribeAsync(chosen.Folder));
    }

    /// <summary>Waits for a digit from 1 to <paramref name="count"/> and returns its index; null for Esc, Q or Ctrl+C.</summary>
    private async Task<int?> ReadNumberAsync(int count)
    {
        while (true)
        {
            var key = await ui.ReadKeyAsync(CancellationToken.None);
            if (key is not { } pressed || pressed.Key == ConsoleKey.Escape || IsQuit(pressed)) return null;
            if (pressed.KeyChar is >= '1' and <= '9' && pressed.KeyChar - '1' < count) return pressed.KeyChar - '1';
        }
    }

    /// <summary>Choice 4: asks for a file, dragged into the window or typed, and transcribes it.</summary>
    private async Task TranscribeFileAsync()
    {
        ui.WriteLine("Drag a file into this window, or type its path, then press Enter. Press Enter alone to go back.");

        // Windows puts quotes around a dragged path that contains spaces.
        var path = ui.ReadLine()?.Trim().Trim('"').Trim();
        if (string.IsNullOrEmpty(path)) return;

        if (!File.Exists(path) && !Directory.Exists(path))
        {
            ui.WriteLine($"Nothing found at {path}");
            return;
        }

        await RunChoiceAsync(() => commands.TranscribeAsync(path));
    }

    /// <summary>Choice 5: opens the recordings folder, creating it when it is not there yet.</summary>
    private void OpenRecordingsFolder()
    {
        var root = folders.Root;
        Directory.CreateDirectory(root);
        desktop.OpenFolder(root);
    }

    /// <summary>True for the keys that leave the menu: Q and Ctrl+C.</summary>
    private static bool IsQuit(ConsoleKeyInfo key) =>
        key.Key == ConsoleKey.Q || (key.Key == ConsoleKey.C && key.Modifiers.HasFlag(ConsoleModifiers.Control));
}
