using System.Globalization;
using ClassScribe.Audio;
using ClassScribe.Recorder;
using ClassScribe.Settings;
using ClassScribe.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClassScribe.Commands;

/// <summary>
/// The menu shown when ClassScribe starts without arguments. It opens with the report at the start (see
/// <see cref="IStartupReport"/>), with its offers, which the report carries out itself, and, when a recording is in
/// progress, shows that recording at once; A shows it again later. One key picks a choice; when the choice has run,
/// successfully or not, the menu comes back, until Q or Ctrl+C leaves it. Keys typed while something ran are dropped, so
/// the menu only acts on a key pressed after it is shown. Each choice reads the settings again first, and runs only once
/// they have passed the check (see <see cref="RunGate"/>); showing the recording in progress never waits for the settings.
/// The menu reaches every command through the runner (<see cref="ICommandRunner"/>), which runs each in a scope of its
/// own, the microphone menu of choice 7, the microphone check of choice 8 and Google Drive's menu of choice 9 among them,
/// and the runs only through the report, so that it is made before the services of the background recorder are registered.
/// <paramref name="classes"/>, the saved classes, are named in the header when one cannot be used and offered by choices 1
/// and 2 (C lists them); <paramref name="writer"/> saves choice 6's pick as the default with S. Without them, as in tests
/// that do not need them, there are neither.
/// </summary>
public sealed class MenuCommand(
    ICommandRunner commands,
    IStartupReport report,
    IConsoleUi ui,
    RunGate gate,
    SettingsFile settings,
    IOptionsMonitor<ElevenLabsOptions> elevenLabs,
    ClassFolders folders,
    ILoopbackCaptureProvider devices,
    SourceChoice choice,
    RecordingNamePrompt prompt,
    IDesktop desktop,
    TimeProvider time,
    ILogger<MenuCommand> logger,
    SavedClasses? classes = null,
    SettingsWriter? writer = null)
{
    /// <summary>The key of the saved classes, menu C.</summary>
    private const char ClassesKey = 'C';

    /// <summary>The key of the settings, menu S.</summary>
    private const char SettingsKey = 'S';

    /// <summary>How many recent recordings choice 3 offers, so that one digit picks one.</summary>
    private const int RecentRecordings = 9;

    /// <summary>The key that shows the recording in progress, as the menu returns it.</summary>
    private const char ShowKey = 'A';

    /// <summary>The key of the YouTube videos, after 9, the digits being all taken.</summary>
    private const char YouTubeKey = 'Y';

    /// <summary>
    /// The console log is written on a background thread. Waiting this long before showing the menu
    /// again lets the last lines of a choice appear above the menu rather than below it.
    /// </summary>
    private static readonly TimeSpan LogSettleTime = TimeSpan.FromMilliseconds(150);

    /// <summary>The warning about an out-of-date copy of the settings that the menu's header gave when it was last shown; null when it gave none.</summary>
    private string? _staleCopyShown;

    /// <summary>
    /// A menu without the report at the start, for the callers that do not pass one yet: it reports nothing and never
    /// finds a recording in progress (see <see cref="NoStartupReport"/>). The container uses the other constructor.
    /// </summary>
    internal MenuCommand(ICommandRunner commands, IConsoleUi ui, RunGate gate, SettingsFile settings, IOptionsMonitor<ElevenLabsOptions> elevenLabs, ClassFolders folders,
        ILoopbackCaptureProvider devices, SourceChoice choice, RecordingNamePrompt prompt, IDesktop desktop, TimeProvider time, ILogger<MenuCommand> logger)
        : this(commands, new NoStartupReport(), ui, gate, settings, elevenLabs, folders, devices, choice, prompt, desktop, time, logger)
    {
    }

    /// <summary>Shows the report at the start, then the menu until it is left. Returns the exit code, which is 0.</summary>
    public async Task<int> RunAsync()
    {
        for (var settle = await StartAsync(); ; settle = true)
        {
            if (settle)
            {
                await Task.Delay(LogSettleTime, time);

                // A key pressed while something ran, such as a Q or an Enter during the upload, was not meant for this
                // menu: taken as a choice, it could quit or start something unasked.
                ui.DiscardPendingKeys();
            }

            ShowMenu();
            if (await ReadChoiceAsync() is not { } picked) return 0;

            await RunChoiceAsync(picked);
        }
    }

    /// <summary>
    /// What comes before the menu is first shown: the report at the start, with its offers; then the recording in progress,
    /// shown at once, unless C in the report has just continued one, which the run that continued it showed. The report
    /// carries out what is chosen there itself, with the check of the settings that C and T need, so the choice it returns
    /// only says what was done, and the menu does not do it again. Returns true when anything was chosen or shown, so that
    /// the menu lets the log settle and drops the keys typed meanwhile. A failure is reported, and the menu opens all the
    /// same.
    /// </summary>
    private async Task<bool> StartAsync()
    {
        try
        {
            var chosen = await report.ShowAsync(interactive: true);
            if (chosen?.Action == StartupAction.Continue || report.RecordingNow() is not { } inProgress) return chosen is not null;

            await ShowRecordingAsync(inProgress);
            return true;
        }
        catch (OperationCanceledException)
        {
            // Deliberately not rethrown: Ctrl+C during a transcription, such as that of the recording shown at the start
            // once the view has gone on with it in this window, is a normal way on to the menu, as during one chosen in
            // the menu, and this warning reports it.
            logger.LogWarning("Cancelled");
            return true;
        }
        catch (Exception ex)
        {
            // Deliberately not rethrown: the menu opens all the same, so that a report or a recording that cannot be
            // shown never keeps anyone from the other choices, and this log line is the report.
            logger.LogError(ex, "Stopped because of an error");
            return true;
        }
    }

    /// <summary>
    /// Runs the choice picked. A shows the recording in progress whatever the settings say: a broken file must not keep
    /// anyone from seeing or stopping a recording. While a recording is in progress, 1 and 2 explain that there is one
    /// recording at a time, and start nothing. Any other choice runs once the settings, read again, have passed the check:
    /// an edit made since the last choice counts, and one that cannot be used stops the choice before anything starts.
    /// Under dotnet run, an edit made to the project's own appsettings.json while the menu was shown does not count until
    /// the next build, so that is said first, unless the menu's header has said it already. A failure is reported, and
    /// the menu carries on.
    /// </summary>
    private async Task RunChoiceAsync(char picked)
    {
        try
        {
            if (picked == ShowKey)
            {
                await ShowRecordingAsync(report.RecordingNow());
                return;
            }

            if (picked is '1' or '2' && report.RecordingNow() is { } inProgress)
            {
                ui.WriteLine(OneRecordingAtATime(inProgress));
                return;
            }

            // Written here rather than logged, so that it shows before whatever the choice shows first, such as the
            // question for the recording's name; the log is written on a thread of its own.
            if (_staleCopyShown is null && settings.DescribeStaleCopy() is { } stale) ui.WriteLine($"WARNING  {stale}.");

            // S changes the settings through the writer, which checks each change itself, so a file the gate refuses can be
            // fixed from it; it opens without the gate.
            if (picked == SettingsKey)
            {
                await commands.SettingsMenuAsync();
                return;
            }

            // A recording runs whatever the file says, without the settings the check refuses (amendment 4), and C starts a
            // saved class, or changes one through the writer; the rest wait for a file that passes.
            if (!gate.TryOpen(picked is '1' or '2' or ClassesKey ? GateUse.Recording : GateUse.MenuChoice)) return;

            switch (picked)
            {
                case ClassesKey:
                    await commands.ClassesMenuAsync();
                    break;
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
                case '7':
                    await commands.ChooseMicrophoneAsync();
                    break;
                case '8':
                    await CheckAsync();
                    break;
                case '9':
                    await commands.DriveMenuAsync();
                    break;
                case YouTubeKey:
                    await commands.YouTubeMenuAsync();
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
    /// Lists the choices under a header: the build (see <see cref="BuildInfo"/>), the settings file read, where the files
    /// go, what is recorded, the microphone included, and the runs going on now. When the file read is the shipped one
    /// beside the installed program because the user has no file of their own yet, the file's line says so and where
    /// ClassScribe makes it (see <see cref="SettingsPlaces.DescribeTemplateInUse"/>); a folder that CLASSSCRIBE_SETTINGS_FOLDER
    /// names is named, so that a stray one is seen; and while the user's own file is missing, an error says so and how to
    /// put it back (see <see cref="SettingsPlaces.DescribeMissing"/>).
    /// Under the file's name it points out a copy of the settings that a build has not brought up to date, and settings
    /// that could not be used when they were last read, at start or for the last choice: each problem, and that no choice
    /// runs until the file is fixed, so the folder and the source shown below are not taken for the file's. Then, when
    /// no ElevenLabs key is set, that recording works and transcribing will not, before a class rather than at its end;
    /// the record and transcribe commands say it again when it matters. A recordings folder that cannot be used, such as
    /// one on a drive that is not there, is pointed out right under its own, before anything is recorded. The report's
    /// lines about the runs now follow what is recorded, and A is offered while a recording is in progress in the background.
    /// </summary>
    private void ShowMenu()
    {
        ui.WriteLine();
        ui.WriteLine($"ClassScribe {BuildInfo.Describe()}");
        ui.WriteLine(settings.Places.DescribeTemplateInUse() is { } template ? $"Settings: {settings.Path} ({template})" : $"Settings: {settings.Path}");
        if (settings.Places.IsOverridden)
            ui.WriteLine($"Settings folder: {settings.Places.UserFolder}, from {SettingsPlaces.FolderVariable}, as in a dry run; your own settings are not read in this window.");
        if (settings.Places.DescribeMissing() is { } missing) ui.WriteLine($"ERROR  {missing}");
        if (DefaultChanges.HeaderLine(gate.OlderDefaults.Count) is { } older) ui.WriteLine(older);
        foreach (var unusable in classes?.All().Where(entry => !entry.Usable) ?? []) ui.WriteLine($"WARNING  {unusable.CannotBeUsed}");
        _staleCopyShown = settings.DescribeStaleCopy();
        if (_staleCopyShown is { } stale) ui.WriteLine($"WARNING  {stale}.");
        if (gate.Problems is [_, ..] settingsProblems)
        {
            foreach (var settingsProblem in settingsProblems) ui.WriteLine($"ERROR  {settingsProblem}");
            ui.WriteLine("ERROR  Until the file is fixed, only recordings run (1, 2 and C), without the settings above; the other choices wait. ClassScribe keeps its last usable settings, or the defaults.");
        }

        if (string.IsNullOrWhiteSpace(elevenLabs.CurrentValue.ApiKey))
            ui.WriteLine($"WARNING  ElevenLabs key: not set ({ElevenLabsOptions.ApiKeyVariable}); recording works, transcribing will not.");

        ui.WriteLine($"Recordings and transcripts: {folders.Root}");
        if (folders.DescribeRootProblem() is { } problem) ui.WriteLine($"WARNING  {problem}.");
        ui.WriteLine($"Recording from: {choice.Description}");
        foreach (var line in report.HeaderLines()) ui.WriteLine(line);
        ui.WriteLine();
        ui.WriteLine("  1  Record only");
        ui.WriteLine("  2  Record and transcribe");
        ui.WriteLine("  3  Transcribe a recent recording");
        ui.WriteLine("  4  Transcribe a file");
        ui.WriteLine("  5  Open the recordings folder");
        ui.WriteLine("  6  Choose what to record: a device or an app");
        ui.WriteLine("  7  Microphone and camera: none, with the class, or in person");
        ui.WriteLine("  8  Check the microphone, or a camera (a camera opens only after you type Y)");
        ui.WriteLine("  9  Google Drive: upload, free room on this laptop, sign in");
        ui.WriteLine($"  {YouTubeKey}  YouTube: record videos as they play, or download them, then transcribe");
        ui.WriteLine($"  {ClassesKey}  Saved classes: start, add, change or remove one");
        ui.WriteLine($"  {SettingsKey}  Settings: recordings folder, camera picture size, the settings file");
        if (report.RecordingNow() is { Home: RunHome.Separate }) ui.WriteLine($"  {ShowKey}  Show the recording in progress");
        ui.WriteLine("  Q  Quit");
        ui.WriteLine();
    }

    /// <summary>
    /// Shows <paramref name="inProgress"/> through the runner, as a window that did not start it: what it logged so far,
    /// then what happens, with the recording keys passed on to it; Esc brings the menu back, and the recording carries on.
    /// A recording in another ClassScribe window has no link to show it by, so that window is named instead. Null: there
    /// is none, which is said.
    /// </summary>
    private async Task ShowRecordingAsync(RunRecord? inProgress)
    {
        if (inProgress is null)
        {
            ui.WriteLine("No recording is in progress.");
            return;
        }

        if (inProgress.Home == RunHome.InWindow)
        {
            ui.WriteLine($"{CommandRunner.InAnotherWindow(inProgress)}.");
            return;
        }

        await commands.AttachAsync(inProgress.RunId, alert: false);
    }

    /// <summary>
    /// What 1 and 2 say while <paramref name="inProgress"/> records: one recording at a time, with since when it records in
    /// local time and that A shows it, or, for one in another ClassScribe window, which window shows it.
    /// </summary>
    private string OneRecordingAtATime(RunRecord inProgress) =>
        inProgress.Home == RunHome.InWindow
            ? $"Only one recording at a time: {CommandRunner.InAnotherWindow(inProgress)}."
            : $"Only one recording at a time: \"{inProgress.Request.Name}\" has been recording since {TimeZoneInfo.ConvertTime(inProgress.StartedUtc, time.LocalTimeZone).ToString("HH:mm", CultureInfo.InvariantCulture)}. {ShowKey} shows it.";

    /// <summary>
    /// Choice 6: lists the connected output devices and the apps with sound open, and pins the one picked, or automatic,
    /// until ClassScribe is closed, whatever the settings say (see <see cref="SourceChoice"/>). It says so, and which
    /// setting keeps the pick. ClassScribe's own processes are not offered (see <see cref="DevicesCommand.AppsByName"/>).
    /// </summary>
    private async Task ChooseSourceAsync()
    {
        var connected = devices.GetOutputDevices();
        if (connected.Count == 0)
        {
            ui.WriteLine("No output device is connected.");
            return;
        }

        var entries = connected
            .Select(device => new SourceEntry(DevicesCommand.Describe(device), device.Name, "Recording:Device", () => choice.Device = device.Name))
            .Concat(DevicesCommand.AppsByName(devices.GetPlayingApps()).Select(app => new SourceEntry(DevicesCommand.Describe(app), app.Name, "Recording:App", () => choice.App = app.Name)))
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
                if (writer is null)
                    ui.WriteLine($"Recording from {choice.Description} until ClassScribe is closed, whatever the settings say. To keep that, leave Recording:Device and Recording:App empty in {settings.Path}.");
                else
                    await OfferToSaveAsync([("Recording:Device", [""]), ("Recording:App", [""])]);
                return;
            }

            if (pressed.KeyChar is >= '1' and <= '9' && pressed.KeyChar - '1' < entries.Count)
            {
                var entry = entries[pressed.KeyChar - '1'];
                entry.Pick();
                if (writer is null)
                    ui.WriteLine($"Recording from {choice.Description} until ClassScribe is closed, whatever the settings say. To keep that, set {entry.Setting} to \"{entry.Name}\" in {settings.Path}.");
                else
                    await OfferToSaveAsync([(entry.Setting, [entry.Name]), (entry.Setting == "Recording:App" ? "Recording:Device" : "Recording:App", [""])]);
                return;
            }
        }
    }

    /// <summary>
    /// After a pick in choice 6 (layout-config 1.8): says what is recorded now, and S keeps it as the default, saving
    /// <paramref name="settingsToSave"/> through the writer; any other key uses it until ClassScribe is closed.
    /// </summary>
    private async Task OfferToSaveAsync(IReadOnlyList<(string Name, string[] Values)> settingsToSave)
    {
        ui.WriteLine($"Recording from {choice.Description} until ClassScribe is closed, whatever the settings say.");
        ui.WriteLine($"Press S to keep this as the default (saved in {writer!.FilePath}), or any other key to use it until ClassScribe is closed.");
        if ((await ui.ReadKeyAsync(CancellationToken.None))?.Key == ConsoleKey.S) writer.SetAll(settingsToSave);
    }

    /// <summary>
    /// Choice 8: W watches what apps do with the microphone (the microphone check, until it is stopped in this window); C
    /// checks the cameras (the camera check, which opens the chosen camera, shared, only after a typed Y). Esc, Q or Ctrl+C
    /// go back; other keys are ignored. W opens nothing; C opens a camera only after a typed Y and Enter.
    /// </summary>
    private async Task CheckAsync()
    {
        ui.WriteLine("Check:");
        ui.WriteLine("  W  Watch what apps do with the microphone (opens nothing)");
        ui.WriteLine("  C  The cameras: list them, what the camera needs on this computer, and open one for 5 seconds after you type Y");
        ui.WriteLine("Press W or C, or Esc to go back.");

        while (true)
        {
            var key = await ui.ReadKeyAsync(CancellationToken.None);
            if (key is not { } pressed || pressed.Key == ConsoleKey.Escape || IsQuit(pressed)) return;

            switch (pressed.Key)
            {
                case ConsoleKey.W:
                    await commands.MicCheckAsync(new MicCheckRequest(StopAfter: null, LogFolder: null));
                    return;
                case ConsoleKey.C when !pressed.Modifiers.HasFlag(ConsoleModifiers.Control):
                    await commands.CameraCheckAsync(new CameraCheckRequest());
                    return;
            }
        }
    }

    /// <summary>One line of the list of sources: what it shows, the name to pin, the setting that keeps it, and how to pin it.</summary>
    private sealed record SourceEntry(string Line, string Name, string Setting, Action Pick);

    /// <summary>
    /// Waits for one of the menu's keys, ignoring any other: a digit from 1 to 9; C or S; A, which comes back as
    /// <see cref="ShowKey"/>, or Y, as <see cref="YouTubeKey"/>. Returns null for Q, for Ctrl+C, or when nobody can type.
    /// </summary>
    private async Task<char?> ReadChoiceAsync()
    {
        while (true)
        {
            var key = await ui.ReadKeyAsync(CancellationToken.None);
            if (key is not { } pressed || IsQuit(pressed)) return null;
            if (pressed.KeyChar is >= '1' and <= '9') return pressed.KeyChar;
            if (pressed.Key == ConsoleKey.A && !pressed.Modifiers.HasFlag(ConsoleModifiers.Control)) return ShowKey;
            if (pressed.Key == ConsoleKey.Y && !pressed.Modifiers.HasFlag(ConsoleModifiers.Control)) return YouTubeKey;
            if (pressed.Key == ConsoleKey.C && !pressed.Modifiers.HasFlag(ConsoleModifiers.Control)) return ClassesKey;
            if (pressed.Key == ConsoleKey.S && !pressed.Modifiers.HasFlag(ConsoleModifiers.Control)) return SettingsKey;
        }
    }

    /// <summary>
    /// Choices 1 and 2: ask for the recording's name, then hand the runner a request to record it, started from the menu:
    /// a new run id, what menu choice 6 or the settings say to record, the recordings folder of the settings, no time
    /// limit, the results opened at the end, and the question whether to continue a recent recording of the same name
    /// left to the starter, which asks it. 2 also transcribes it. A name that is a saved class's, or its course, is offered
    /// first (layout-config 4.3, amendment 17): Enter records the saved class as saved, O records with choice 6's source
    /// instead; this comes before the camera's question and the continue question, which the starter asks.
    /// </summary>
    private async Task RecordAsync(bool transcribe)
    {
        if (await prompt.AskAsync() is not { } name) return;

        if (await OfferSavedClassAsync(name) is { } offer)
        {
            if (offer.Back) return;
            if (offer.Class is { } saved)
            {
                var root = saved.OutputFolder is { } own ? ClassFolders.RootFor(own) : folders.Root;
                await commands.RecordAsync(new RecordRequest(RunIds.New(time.GetUtcNow()), saved.Course, transcribe, saved.Plan, root, ContinueChoice.Ask,
                    ContinueBaseName: null, Minutes: null, Open: true, ClassName: saved.Name), StartOrigin.Menu);
                return;
            }
        }

        var request = new RecordRequest(RunIds.New(time.GetUtcNow()), name, transcribe, choice.Current, folders.Root, ContinueChoice.Ask,
            ContinueBaseName: null, Minutes: null, Open: true);
        await commands.RecordAsync(request, StartOrigin.Menu);
    }

    /// <summary>
    /// When <paramref name="name"/> is a usable saved class's name or course, asks whether to record it as saved: "Mantiq is
    /// a saved class (chrome, with your microphone (...)). Enter records it as saved; O records with choice 6's source
    /// instead." With several classes of that course, a number picks one. Null when no saved class is meant; Back for Esc;
    /// no class for O.
    /// </summary>
    private async Task<(bool Back, SavedClass? Class)?> OfferSavedClassAsync(string name)
    {
        var matching = classes?.Matching(name).Select(entry => entry.Class).OfType<SavedClass>().ToList() ?? [];
        if (matching.Count == 0) return null;

        if (matching.Count == 1)
            ui.WriteLine($"{name} is a saved class ({matching[0].Describe()}). Enter records it as saved; O records with choice 6's source instead.");
        else
        {
            ui.WriteLine($"{name} is the course of saved classes:");
            for (var i = 0; i < matching.Count && i < 9; i++) ui.WriteLine($"  {i + 1}  {matching[i].Name}  {matching[i].Describe()}");
            ui.WriteLine("Press a class's number to record it as saved; O records with choice 6's source instead.");
        }

        while (true)
        {
            var key = await ui.ReadKeyAsync(CancellationToken.None);
            if (key is not { } pressed || pressed.Key == ConsoleKey.Escape || IsQuit(pressed)) return (true, null);
            if (pressed.Key == ConsoleKey.O) return (false, null);
            if (matching.Count == 1 && pressed.Key == ConsoleKey.Enter) return (false, matching[0]);
            if (matching.Count > 1 && pressed.KeyChar is >= '1' and <= '9' && pressed.KeyChar - '1' < matching.Count) return (false, matching[pressed.KeyChar - '1']);
        }
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
            var notes = string.Concat(ListCommand.NotesOf(recorded).Select(note => $"  {note}"));
            ui.WriteLine($"  {i + 1}  {recorded.Files.BaseName}  {Durations.Format(recorded.Length)}{notes}  {TranscriptState(recorded)}");
        }

        // V, beside the numbers, makes a lesson's video (teach-video 1.4 and 6.9), free, when a recording listed is a lesson's.
        var lessons = recordings.Any(recorded => recorded.LessonVideo != LessonVideoState.None);
        ui.WriteLine(lessons
            ? "Press a recording's number to transcribe it, V then a lesson's number to make its video (free), or Esc to go back."
            : "Press a recording's number to transcribe it, or Esc to go back.");
        if (await ReadNumberAsync(recordings.Count, lessons) is not { } picked) return;

        if (picked.Video)
        {
            await MakeTheLessonVideoAsync(recordings[picked.Index]);
            return;
        }

        var chosen = recordings[picked.Index];
        await TranscribeRecordingAsync(chosen, chosen.Files.Segment(1));
    }

    /// <summary>
    /// Choice 3's V: makes the lesson's video of <paramref name="recorded"/> through the runner, as finish-video does, by one of
    /// its files; a recording without the camera has none to make, which is said.
    /// </summary>
    private async Task MakeTheLessonVideoAsync(RecordedClass recorded)
    {
        if (recorded.LessonVideo == LessonVideoState.None)
        {
            ui.WriteLine($"{recorded.Files.BaseName} was recorded without the camera, so it has no video to make.");
            return;
        }

        await commands.FinishVideoAsync(new FinishVideoRequest(recorded.Files.FindSegments().FirstOrDefault() ?? recorded.Files.FindSegments(Track.Microphone).FirstOrDefault() ?? recorded.Files.Folder));
    }

    /// <summary>
    /// Transcribes a recording picked in choice 3 or given in choice 4 by sending <paramref name="path"/>, which names it,
    /// with the care such a recording needs. One being recorded now is not sent: what was sent would stop short. One whose
    /// audio is on Google Drive alone is not sent either: where the audio is is said, with the free rebuild (see
    /// <see cref="OfferRebuildOfAudioOnDriveAsync"/>). One that
    /// would be billed again offers the free rebuild first, with R, when there is an answer to rebuild from (see
    /// <see cref="WhyBilledAgain"/>), and the new try with Y; any other key goes back without either. The runner refuses
    /// one that a recorder transcribes in the background.
    /// </summary>
    private async Task TranscribeRecordingAsync(RecordedClass recorded, string path)
    {
        if (recorded.BeingWritten)
        {
            ui.WriteLine($"{recorded.Files.BaseName} is being recorded now; transcribe it once it has stopped.");
            return;
        }

        if (recorded.OnDrive is { } onDrive)
        {
            await OfferRebuildOfAudioOnDriveAsync(recorded, onDrive);
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

    /// <summary>
    /// What choice 3 does with a recording whose audio is on Google Drive alone: it says so, and where, and that the audio
    /// must be back in its folder before it can be transcribed again; R rebuilds its transcript for free from ElevenLabs'
    /// saved answer, or from an answer paid for and kept, when there is one. Any other key goes back.
    /// </summary>
    private async Task OfferRebuildOfAudioOnDriveAsync(RecordedClass recorded, AudioOnDrive onDrive)
    {
        var rebuildFrom = TranscriptCopies.NewestAnswer(recorded.Files) ?? recorded.KeptAnswer;
        var audio = $"The audio of {recorded.Files.BaseName} is on Google Drive, not on this laptop ({onDrive.Location}). To transcribe it again, download its .wav files from there into {recorded.Files.Folder} first.";
        if (rebuildFrom is null)
        {
            ui.WriteLine(audio);
            return;
        }

        ui.WriteLine($"{audio} R rebuilds its transcript from ElevenLabs' saved answer, with the current marks and Transcript settings (free, nothing is uploaded). Any other key goes back.");
        if ((await ui.ReadKeyAsync(CancellationToken.None))?.Key == ConsoleKey.R) await commands.RebuildAsync(rebuildFrom, open: true);
    }

    /// <summary>What choice 3 says about a recording after its notes: RECORDING NOW while it is being recorded, its transcript ready, an answer paid for and kept without one, or none yet.</summary>
    private static string TranscriptState(RecordedClass recorded) =>
        recorded.BeingWritten ? "RECORDING NOW"
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
    private async Task<int?> ReadNumberAsync(int count) => await ReadNumberAsync(count, video: false) is { } picked ? picked.Index : null;

    /// <summary>
    /// Waits for a digit from 1 to <paramref name="count"/>, or, when <paramref name="video"/>, for V and then a digit, and
    /// returns the index and whether V came first; null for Esc, Q or Ctrl+C.
    /// </summary>
    private async Task<(int Index, bool Video)?> ReadNumberAsync(int count, bool video)
    {
        var withV = false;
        while (true)
        {
            var key = await ui.ReadKeyAsync(CancellationToken.None);
            if (key is not { } pressed || pressed.Key == ConsoleKey.Escape || IsQuit(pressed)) return null;
            if (pressed.KeyChar is >= '1' and <= '9' && pressed.KeyChar - '1' < count) return (pressed.KeyChar - '1', withV);
            if (video && !withV && pressed.Key == ConsoleKey.V)
            {
                withV = true;
                ui.WriteLine("Press the lesson's number to make its video, or Esc to go back.");
            }
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
    /// folder, the recording started last in it, one whose audio is on Google Drive alone included, so that what is asked
    /// about is the recording transcribe would take; for a segment file of a recording with audio on disk, that recording.
    /// Null for any other file, which is sent on its own, and for a folder without a recording, which transcribe reports.
    /// </summary>
    private RecordingFiles? RecordingAt(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (Directory.Exists(fullPath))
        {
            var newest = ClassFolders.FindNewest(fullPath);
            return folders.NewestWithAudioOnDriveIn(fullPath) is { } onDrive && (newest is null || onDrive.Files.StartedUtc > newest.StartedUtc) ? onDrive.Files : newest;
        }

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
