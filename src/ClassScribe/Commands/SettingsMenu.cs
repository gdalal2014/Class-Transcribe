using ClassScribe.Camera;
using ClassScribe.Settings;
using ClassScribe.Storage;
using Microsoft.Extensions.Options;

namespace ClassScribe.Commands;

/// <summary>
/// Menu choice S (layout-config 1.8 and 2, camera-first's picture size line), run through
/// <see cref="ICommandRunner.SettingsMenuAsync"/>: the recordings folder (1), typed or dragged into the window, with where
/// the next class would go; the camera's picture size (P); and the settings file opened in Notepad (O). Each change goes
/// through the one writer (<see cref="SettingsWriter"/>), which checks it first and makes the user's file, a full copy, at
/// the first change. When the recordings folder changes, nothing moves, and the menu says where the recordings made before
/// stay and how to list them. Esc, Q or Ctrl+C go back.
/// </summary>
/// <param name="writer">The one writer of the user's file.</param>
/// <param name="places">Where the settings are.</param>
/// <param name="folders">The recordings folder in force and the recent recordings, for the preview.</param>
/// <param name="recording">The run's recording settings: the picture size in force.</param>
/// <param name="reloader">The settings read last, for the check's warnings about a folder.</param>
/// <param name="commands">Runs settings open.</param>
/// <param name="ui">The window the questions are asked in.</param>
/// <param name="time">The clock, for the date of the next class in the preview.</param>
public sealed class SettingsMenu(SettingsWriter writer, SettingsPlaces places, IClassFolders folders, IOptionsSnapshot<RecordingOptions> recording, SettingsReloader reloader,
    ICommandRunner commands, IConsoleUi ui, TimeProvider time)
{
    /// <summary>Shows the screen and does what is picked. Returns the exit code, 0.</summary>
    public async Task<int> RunAsync()
    {
        var size = CameraSize.TryParse(recording.Value.CameraResolution, out var set) ? set : CameraSize.Default;
        ui.WriteLine($"Settings ({places.SettingsPath}):");
        ui.WriteLine($"  1  Recordings folder: {folders.Root}");
        ui.WriteLine($"  P  Camera picture size, for a lesson video: {size.Text}");
        ui.WriteLine("  O  Open the settings file in Notepad");
        ui.WriteLine("Press 1, P or O, or Esc to go back.");

        while (true)
        {
            var key = await ui.ReadKeyAsync(CancellationToken.None);
            if (key is not { } pressed || IsBack(pressed)) return 0;

            switch (pressed.Key)
            {
                case ConsoleKey.D1 or ConsoleKey.NumPad1:
                    await ChangeFolderAsync();
                    return 0;
                case ConsoleKey.P:
                    await ChangeSizeAsync(size);
                    return 0;
                case ConsoleKey.O:
                    await commands.SettingsAsync(new SettingsRequest.Open());
                    return 0;
            }
        }
    }

    /// <summary>
    /// 1: asks for the recordings folder, dragged into the window or typed (a relative path taken from the user's folder, as
    /// Recording:OutputFolder takes one), shows where the next class would go, saves it,
    /// gives the check's warnings about it (inside OneDrive, a drive's root, another program's sync folder), and says where
    /// the recordings made before stay, since nothing moves. Enter alone or Esc goes back.
    /// </summary>
    private async Task ChangeFolderAsync()
    {
        var before = folders.Root;
        ui.WriteLine("Drag a folder into this window, or type its path (a relative one is taken from your user folder), then press Enter. Esc, or Enter alone, goes back.");
        ui.Write("> ");

        // Windows puts quotes around a dragged path that contains spaces.
        var typed = (await ui.ReadLineAsync(CancellationToken.None))?.Trim().Trim('"').Trim();
        if (string.IsNullOrEmpty(typed)) return;

        if (typed.IndexOfAny(Path.GetInvalidPathChars()) >= 0 || typed.IndexOfAny(['<', '>', '|', '?', '*']) >= 0)
        {
            ui.WriteLine($"\"{typed}\" is not a folder's path: it has a character Windows does not allow in a path.");
            return;
        }

        // Taken as Recording:OutputFolder takes a path: a relative one from the user's folder, not the window's current one.
        var folder = folders.RootFor(typed);
        var course = folders.ListRecent(1).FirstOrDefault()?.Files.Name ?? "Mantiq";
        ui.WriteLine($"The next class of {course} would go to {Path.Combine(folder, course, FolderNames.DateFolderName(DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(time.GetUtcNow(), time.LocalTimeZone).DateTime)))}.");

        var change = writer.Set(RecordingOptions.SectionName + ":" + nameof(RecordingOptions.OutputFolder), folder);
        if (change.ExitCode != 0) return;

        foreach (var warning in reloader.Last?.Warnings ?? []) ui.WriteLine($"WARNING  {warning}.");
        if (!string.Equals(Path.TrimEndingDirectorySeparator(before), Path.TrimEndingDirectorySeparator(folders.Root), StringComparison.OrdinalIgnoreCase) && Directory.Exists(before))
            ui.WriteLine($"Recordings made before stay in {before}; nothing was moved. ClassScribe list --output \"{before}\" lists them.");
    }

    /// <summary>P: the camera's picture size, as menu choice 7 asks it, saved as Recording:CameraResolution. Esc goes back.</summary>
    private async Task ChangeSizeAsync(CameraSize current)
    {
        while (true)
        {
            ui.WriteLine($"Picture size: Enter keeps {current.Text}, 1 480p, 2 720p, 3 1080p, T to type a size");
            var key = await ui.ReadKeyAsync(CancellationToken.None);
            if (key is not { } pressed || IsBack(pressed) || pressed.Key == ConsoleKey.Enter) return;

            CameraSize? size = null;
            if (pressed.KeyChar is >= '1' and <= '3') size = CameraSize.Presets[pressed.KeyChar - '1'].Size;
            else if (pressed.Key == ConsoleKey.T)
            {
                ui.Write("Width and height, such as 1024x576: ");
                if (await ui.ReadLineAsync(CancellationToken.None) is not { } typed) return;
                if (!CameraSize.TryParse(typed, out size))
                {
                    ui.WriteLine($"\"{typed.Trim()}\" is not a size ClassScribe takes: {CameraSize.Rule}.");
                    continue;
                }
            }

            if (size is null) continue;
            writer.Set(RecordingOptions.SectionName + ":" + nameof(RecordingOptions.CameraResolution), size.Text);
            return;
        }
    }

    /// <summary>True for the keys that go back to the menu: Esc, Q and Ctrl+C.</summary>
    private static bool IsBack(ConsoleKeyInfo key) =>
        key.Key is ConsoleKey.Escape or ConsoleKey.Q || (key.Key == ConsoleKey.C && key.Modifiers.HasFlag(ConsoleModifiers.Control));
}
