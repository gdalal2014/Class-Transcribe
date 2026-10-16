using System.Globalization;
using ClassScribe.Audio;
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
    ILoopbackCaptureProvider devices,
    SourceChoice choice,
    RecordingNamePrompt prompt,
    IDesktop desktop,
    TimeProvider time,
    ILogger<MenuCommand> logger)
{
    /// <summary>How many recent recordings choice 3 offers, so that one digit picks one.</summary>
    private const int RecentRecordings = 9;

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
                    await RecordAsync(transcribe: false);
                    break;
                case '2':
                    await RecordAsync(transcribe: true);
                    break;
                case '3':
                    await TranscribeRecentRecordingAsync();
                    break;
                case '4':
                    await TranscribeFileAsync();
                    break;
                case '5':
                    OpenRecordingsFolder();
                    break;
                case '6':
                    await ChooseSourceAsync();
                    break;
            }
        }
    }

    /// <summary>Lists the choices, where the files go and which device is recorded.</summary>
    private void ShowMenu()
    {
        ui.WriteLine();
        ui.WriteLine("ClassScribe");
        ui.WriteLine($"Recordings and transcripts: {folders.Root}");
        ui.WriteLine($"Recording from: {choice.Description}");
        ui.WriteLine();
        ui.WriteLine("  1  Record only");
        ui.WriteLine("  2  Record and transcribe");
        ui.WriteLine("  3  Transcribe a recent recording");
        ui.WriteLine("  4  Transcribe a file");
        ui.WriteLine("  5  Open the recordings folder");
        ui.WriteLine("  6  Choose what to record: a device or an app");
        ui.WriteLine("  Q  Quit");
        ui.WriteLine();
    }

    /// <summary>
    /// Choice 6: lists the connected output devices and the apps with sound open, and pins the one
    /// picked for the rest of the run, or goes back to automatic.
    /// </summary>
    private async Task ChooseSourceAsync()
    {
        var connected = devices.GetOutputDevices();
        if (connected.Count == 0)
        {
            ui.WriteLine("No output device is connected.");
            return;
        }

        // One entry per app, at its loudest: a browser can have several processes with sound open.
        var apps = devices.GetPlayingApps()
            .GroupBy(app => app.Name, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(app => app.Level).First())
            .OrderBy(app => app.Name, StringComparer.OrdinalIgnoreCase);
        var entries = connected
            .Select(device => new SourceEntry(Describe(device), device.Name, "Recording:Device", () => choice.Device = device.Name))
            .Concat(apps.Select(app => new SourceEntry(Describe(app), app.Name, "Recording:App", () => choice.App = app.Name)))
            .Take(9)
            .ToList();

        ui.WriteLine("Output devices, then apps with sound:");
        for (var i = 0; i < entries.Count; i++)
            ui.WriteLine($"  {i + 1}  {entries[i].Line}");

        ui.WriteLine("  A  Automatic: whichever device the class plays on");
        ui.WriteLine("Press a number, A for automatic, or Esc to go back.");

        while (true)
        {
            var key = await ui.ReadKeyAsync(CancellationToken.None);
            if (key is not { } pressed || pressed.Key == ConsoleKey.Escape || IsQuit(pressed)) return;

            if (pressed.Key == ConsoleKey.A)
            {
                choice.Device = null;
                choice.App = null;
                return;
            }

            if (pressed.KeyChar is >= '1' and <= '9' && pressed.KeyChar - '1' < entries.Count)
            {
                var entry = entries[pressed.KeyChar - '1'];
                entry.Pick();
                ui.WriteLine($"Recording from {choice.Description} until ClassScribe is closed. To keep that, set {entry.Setting} to \"{entry.Name}\" in appsettings.json.");
                return;
            }
        }
    }

    /// <summary>A device's line in the list: its name, and whether it is the default one or playing.</summary>
    private static string Describe(OutputDevice device)
    {
        var notes = string.Join(", ", new[] { device.IsDefault ? "default" : null, device.Level >= CaptureChooser.SoundLevel ? "playing" : null }.OfType<string>());
        return notes.Length > 0 ? $"{device.Name}  ({notes})" : device.Name;
    }

    /// <summary>An app's line in the list: its name, the device it plays through, and whether it is playing now.</summary>
    private static string Describe(PlayingApp app) =>
        app.Level >= CaptureChooser.SoundLevel ? $"{app.Name}  (app on {app.DeviceName}, playing)" : $"{app.Name}  (app on {app.DeviceName})";

    /// <summary>One line of the list of sources: what it shows, the name to pin, the setting that keeps it, and how to pin it.</summary>
    private sealed record SourceEntry(string Line, string Name, string Setting, Action Pick);

    /// <summary>Waits for one of the menu's keys, ignoring any other. Returns null for Q, for Ctrl+C, or when nobody can type.</summary>
    private async Task<char?> ReadChoiceAsync()
    {
        while (true)
        {
            var key = await ui.ReadKeyAsync(CancellationToken.None);
            if (key is not { } pressed || IsQuit(pressed)) return null;
            if (pressed.KeyChar is >= '1' and <= '6') return pressed.KeyChar;
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

    /// <summary>Choices 1 and 2: ask for the recording's name, then record, and transcribe when asked to.</summary>
    private async Task RecordAsync(bool transcribe)
    {
        if (await prompt.AskAsync() is not { } name) return;

        await RunChoiceAsync(() => commands.RecordAsync(name, transcribe));
    }

    /// <summary>Choice 3: lists the recent recordings and transcribes the one picked.</summary>
    private async Task TranscribeRecentRecordingAsync()
    {
        var recordings = folders.ListRecent(RecentRecordings);
        if (recordings.Count == 0)
        {
            ui.WriteLine($"No recordings in {folders.Root} yet.");
            return;
        }

        ui.WriteLine("Recent recordings, newest first:");
        for (var i = 0; i < recordings.Count; i++)
        {
            var recorded = recordings[i];
            var length = recorded.Length.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);
            ui.WriteLine($"  {i + 1}  {recorded.Files.BaseName}  {length}  {(recorded.HasTranscript ? "transcript ready" : "no transcript yet")}");
        }

        ui.WriteLine("Press a recording's number to transcribe it, or Esc to go back.");
        if (await ReadNumberAsync(recordings.Count) is not { } index) return;

        var chosen = recordings[index];
        if (chosen.HasTranscript)
        {
            ui.WriteLine("This recording already has a transcript, and transcribing it again is billed again. Press Y to go ahead, any other key to go back.");
            if (await ui.ReadKeyAsync(CancellationToken.None) is not { Key: ConsoleKey.Y }) return;
        }

        await RunChoiceAsync(() => commands.TranscribeAsync(chosen.Files.Segment(1)));
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
        ui.WriteLine("Drag a file into this window, or type its path, then press Enter. Esc, or Enter alone, goes back.");
        ui.Write("> ");

        // Windows puts quotes around a dragged path that contains spaces.
        var path = (await ui.ReadLineAsync(CancellationToken.None))?.Trim().Trim('"').Trim();
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
