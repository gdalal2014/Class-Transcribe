using ClassScribe.Audio;
using ClassScribe.Settings;
using ClassScribe.Storage;
using Microsoft.Extensions.Logging;

namespace ClassScribe.Commands;

/// <summary>
/// The menu shown when ClassScribe starts without arguments. One key picks a choice; when the choice
/// has run, successfully or not, the menu comes back, until Q or Ctrl+C leaves it. Keys typed while a
/// choice ran are dropped, so the menu only acts on a key pressed after it is shown. Each choice reads
/// the settings again first, and runs only once they have passed the check (see <see cref="RunGate"/>).
/// </summary>
public sealed class MenuCommand(
    ICommandRunner commands,
    IConsoleUi ui,
    RunGate gate,
    SettingsFile settings,
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

    /// <summary>The warning about an out-of-date copy of the settings that the menu's header gave when it was last shown; null when it gave none.</summary>
    private string? _staleCopyShown;

    /// <summary>Shows the menu until it is left. Returns the exit code, which is 0.</summary>
    public async Task<int> RunAsync()
    {
        for (var first = true; ; first = false)
        {
            if (!first)
            {
                await Task.Delay(LogSettleTime, time);

                // A key pressed while the choice ran, such as a Q or an Enter during the upload, was not
                // meant for this menu: taken as a choice, it could quit or start something unasked.
                ui.DiscardPendingKeys();
            }

            ShowMenu();
            if (await ReadChoiceAsync() is not { } picked) return 0;

            await RunChoiceAsync(picked);
        }
    }

    /// <summary>
    /// Runs the choice picked, once the settings, read again, have passed the check: an edit made since the last choice
    /// counts, and one that cannot be used stops the choice before anything starts. Under dotnet run, an edit made to the
    /// project's own appsettings.json while the menu was shown does not count until the next build, so that is said
    /// first, unless the menu's header has said it already. A failure is reported, and the menu carries on.
    /// </summary>
    private async Task RunChoiceAsync(char picked)
    {
        try
        {
            // Written here rather than logged, so that it shows before whatever the choice shows first, such as the
            // question for the recording's name; the log is written on a thread of its own.
            if (_staleCopyShown is null && settings.DescribeStaleCopy() is { } stale) ui.WriteLine($"WARNING  {stale}.");

            if (!gate.TryOpen(GateUse.MenuChoice)) return;

            switch (picked)
            {
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
        catch (OperationCanceledException)
        {
            // Ctrl+C during an upload: a normal way back to the menu.
            logger.LogWarning("Cancelled");
        }
        catch (Exception ex)
        {
            // Deliberately not rethrown: the menu outlives a failed choice, whether the recording, the
            // upload, the list of recordings, the recordings folder or the list of devices failed, and
            // this log line is the report.
            logger.LogError(ex, "Stopped because of an error");
        }
    }

    /// <summary>
    /// Lists the choices, the settings file read, where the files go and which device is recorded. Under the file's name
    /// it points out a copy of the settings that a build has not brought up to date, and settings that could not be used
    /// when they were last read, at start or for the last choice: each problem, and that no choice runs until the file is
    /// fixed, so the folder and the source shown below are not taken for the file's. A recordings folder that cannot be
    /// used, such as one on a drive that is not there, is pointed out right under its own, before anything is recorded.
    /// </summary>
    private void ShowMenu()
    {
        ui.WriteLine();
        ui.WriteLine("ClassScribe");
        ui.WriteLine($"Settings: {settings.Path}");
        _staleCopyShown = settings.DescribeStaleCopy();
        if (_staleCopyShown is { } stale) ui.WriteLine($"WARNING  {stale}.");
        if (gate.Problems is [_, ..] settingsProblems)
        {
            foreach (var settingsProblem in settingsProblems) ui.WriteLine($"ERROR  {settingsProblem}");
            ui.WriteLine("ERROR  No choice runs until the file is fixed. Until then ClassScribe keeps its last usable settings, or the defaults.");
        }

        ui.WriteLine($"Recordings and transcripts: {folders.Root}");
        if (folders.DescribeRootProblem() is { } problem) ui.WriteLine($"WARNING  {problem}.");
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
    /// Choice 6: lists the connected output devices and the apps with sound open, and pins the one picked, or automatic,
    /// until ClassScribe is closed, whatever the settings say (see <see cref="SourceChoice"/>). It says so, and which
    /// setting keeps the pick.
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
                ui.WriteLine($"Recording from {choice.Description} until ClassScribe is closed, whatever the settings say. To keep that, leave Recording:Device and Recording:App empty in {settings.Path}.");
                return;
            }

            if (pressed.KeyChar is >= '1' and <= '9' && pressed.KeyChar - '1' < entries.Count)
            {
                var entry = entries[pressed.KeyChar - '1'];
                entry.Pick();
                ui.WriteLine($"Recording from {choice.Description} until ClassScribe is closed, whatever the settings say. To keep that, set {entry.Setting} to \"{entry.Name}\" in {settings.Path}.");
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

    /// <summary>Choices 1 and 2: ask for the recording's name, then record, and transcribe when asked to.</summary>
    private async Task RecordAsync(bool transcribe)
    {
        if (await prompt.AskAsync() is not { } name) return;

        await commands.RecordAsync(name, transcribe);
    }

    /// <summary>
    /// Choice 3: lists the recent recordings and transcribes the one picked (see <see cref="TranscribeRecordingAsync"/>).
    /// When there are none because the recordings folder cannot be used, it says why. Otherwise it only says there is
    /// nothing to pick from: a recording left out because it cannot be read now still exists, and the warning above names it.
    /// </summary>
    private async Task TranscribeRecentRecordingAsync()
    {
        var recordings = folders.ListRecent(RecentRecordings);
        if (recordings.Count == 0)
        {
            ui.WriteLine(folders.DescribeRootProblem() is { } problem ? $"No recordings found. {problem}." : $"No recordings to pick from in {folders.Root}.");
            return;
        }

        ui.WriteLine("Recent recordings, newest first:");
        for (var i = 0; i < recordings.Count; i++)
        {
            var recorded = recordings[i];
            ui.WriteLine($"  {i + 1}  {recorded.Files.BaseName}  {Durations.Format(recorded.Length)}  {TranscriptState(recorded)}");
        }

        ui.WriteLine("Press a recording's number to transcribe it, or Esc to go back.");
        if (await ReadNumberAsync(recordings.Count) is not { } index) return;

        var chosen = recordings[index];
        await TranscribeRecordingAsync(chosen, chosen.Files.Segment(1));
    }

    /// <summary>
    /// Transcribes a recording picked in choice 3 or given in choice 4 by sending <paramref name="path"/>, which names it,
    /// with the care such a recording needs. One being recorded now is not sent: what was sent would stop short. One that
    /// would be billed again offers the free rebuild first, with R, when there is an answer to rebuild from (see
    /// <see cref="WhyBilledAgain"/>), and the new try with Y; any other key goes back without either.
    /// </summary>
    private async Task TranscribeRecordingAsync(RecordedClass recorded, string path)
    {
        if (recorded.BeingWritten)
        {
            ui.WriteLine($"{recorded.Files.BaseName} is being recorded now; transcribe it once it has stopped.");
            return;
        }

        if (WhyBilledAgain(recorded) is { } offer)
        {
            ui.WriteLine(offer.Question);
            switch ((await ui.ReadKeyAsync(CancellationToken.None))?.Key)
            {
                case ConsoleKey.R when offer.RebuildFrom is { } rebuildFrom:
                    await commands.RebuildAsync(rebuildFrom, open: true);
                    return;
                case ConsoleKey.Y:
                    break;
                default:
                    return;
            }
        }

        await commands.TranscribeAsync(new TranscribeRequest(path));
    }

    /// <summary>What the list says about a recording: being recorded now, its transcript ready, an answer paid for and kept without one, or none yet.</summary>
    private static string TranscriptState(RecordedClass recorded) =>
        recorded.BeingWritten ? "being recorded now"
        : recorded.HasTranscript ? "transcript ready"
        : recorded.KeptAnswer is not null ? "answer kept"
        : "no transcript yet";

    /// <summary>
    /// The question asked before a recording is transcribed again, which is billed again, and what R rebuilds from for
    /// free instead. For a recording with a transcript, that is the recording, from its newest saved answer; when that
    /// answer is no longer there, an answer to another try that was paid for and kept apart, as one is after R laid it out;
    /// and when there is neither, nothing, so only Y is offered. For a recording without a transcript whose answer to an
    /// earlier try was paid for and kept, it is that answer. Null when the recording has neither a transcript nor a kept answer.
    /// </summary>
    private static BilledAgain? WhyBilledAgain(RecordedClass recorded)
    {
        if (!recorded.HasTranscript)
        {
            return recorded.KeptAnswer is { } answer
                ? new($"ElevenLabs' answer to an earlier try, which has been paid for, is kept in {answer}: look there for the transcript's text first, because transcribing it again is billed again. R lays that answer out as a transcript (free, nothing is uploaded). Y transcribes it again (billed again). Any other key goes back.",
                    answer)
                : null;
        }

        if (TranscriptCopies.NewestAnswer(recorded.Files) is not null)
        {
            return new("This recording has a transcript. R rebuilds it from ElevenLabs' saved answer, with the current marks and Transcript settings (free, nothing is uploaded). Y transcribes it again (billed again). Any other key goes back.",
                recorded.Files.Segment(1));
        }

        return recorded.KeptAnswer is { } kept
            ? new($"This recording has a transcript. R makes a new transcript from ElevenLabs' answer kept in {kept}, with the current marks and Transcript settings (free, nothing is uploaded). Y transcribes it again (billed again). Any other key goes back.",
                kept)
            : new("This recording has a transcript, but its saved answer from ElevenLabs is no longer there, so it cannot be rebuilt for free. Y transcribes it again (billed again). Any other key goes back.",
                RebuildFrom: null);
    }

    /// <summary>
    /// The question choices 3 and 4 ask before they send a recording that would be billed again, and the path R rebuilds
    /// from for free; null when there is nothing to rebuild from, and R then goes back as any other key does.
    /// </summary>
    private sealed record BilledAgain(string Question, string? RebuildFrom);

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

    /// <summary>
    /// Choice 4: asks for a file, dragged into the window or typed, and transcribes it. A recording, given by one of its
    /// segment files or by its folder (see <see cref="RecordingAt"/>), gets the same care as in choice 3 (see
    /// <see cref="TranscribeRecordingAsync"/>), and the path is sent as given; any other file is sent at once.
    /// </summary>
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

        if (RecordingAt(path) is { } recording)
        {
            await TranscribeRecordingAsync(folders.Describe(recording), path);
            return;
        }

        await commands.TranscribeAsync(new TranscribeRequest(path));
    }

    /// <summary>
    /// The recording <paramref name="path"/> names, as transcribe takes it (see <see cref="TranscribeCommand"/>): for a
    /// folder, the recording started last in it; for a segment file of a recording with audio on disk, that recording.
    /// Null for any other file, which is sent on its own, and for a folder without a recording, which transcribe reports.
    /// </summary>
    private static RecordingFiles? RecordingAt(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (Directory.Exists(fullPath)) return ClassFolders.FindNewest(fullPath);

        return RecordingFiles.TryParse(fullPath) is { } files && files.FindSegments().Count > 0 ? files : null;
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
