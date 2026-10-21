using ClassScribe.Audio;
using ClassScribe.Audio.Microphones;
using ClassScribe.Camera;
using ClassScribe.Settings;
using ClassScribe.Storage;
using Microsoft.Extensions.Options;

namespace ClassScribe.Commands;

/// <summary>
/// Menu choice C, the saved classes (layout-config 4.3, build-plan amendment 12, camera-first's Lesson video preset), run
/// through <see cref="ICommandRunner.ClassesMenuAsync"/>: a class's number records it now, as saved, through the same path
/// as choices 1 and 2 (the camera's question and the continue question included); N adds a class, asking first whether to
/// start from the Lesson video preset, then its name, its course, what it records, the microphone, in person the camera and
/// its size, its recordings folder and whether to transcribe it; E and a number asks the same with the class's own answers
/// for Enter; D and a number removes one, after Y. Every question goes back with Esc. Changes go through the class command,
/// and so through the one writer, which checks them first; nothing a class recorded is ever touched. With more than nine
/// classes, a number is typed and Enter pressed.
/// </summary>
/// <param name="classes">The saved classes.</param>
/// <param name="places">Where the settings are, named in the list's heading.</param>
/// <param name="commands">Records a class, and adds, changes or removes one through the class command.</param>
/// <param name="folders">The recordings folder in force.</param>
/// <param name="devices">The output devices and the apps with sound now, for the source question.</param>
/// <param name="microphones">The microphones, for the microphone question.</param>
/// <param name="ui">The window the questions are asked in.</param>
/// <param name="time">The clock a new run's id is stamped with.</param>
/// <param name="cameras">The cameras, for the camera question in person; null asks none.</param>
public sealed class ClassesMenu(SavedClasses classes, SettingsPlaces places, ICommandRunner commands, ClassFolders folders, ILoopbackCaptureProvider devices,
    IMicrophoneProvider microphones, IConsoleUi ui, TimeProvider time, ICameraProvider? cameras = null)
{
    /// <summary>How many entries a list offers, so that one digit picks one.</summary>
    private const int MostListed = 9;

    /// <summary>Lists the saved classes and does what is picked. Returns the exit code of a recording started, else 0.</summary>
    public async Task<int> RunAsync()
    {
        var all = classes.All();
        var width = all.Count == 0 ? 0 : all.Max(entry => entry.Name.Length);
        ui.WriteLine($"Saved classes ({places.SettingsPath}):");
        if (all.Count == 0) ui.WriteLine("  None yet.");
        for (var i = 0; i < all.Count; i++)
        {
            var entry = all[i];
            var what = entry.Class is { } usable ? usable.Describe() : $"cannot be used: {string.Join(" ", entry.Problems)}";
            ui.WriteLine($"  {i + 1}  {entry.Name.PadRight(width)}  {what}");
        }

        ui.WriteLine(all.Count == 0
            ? "Press N for a new class, or Esc to go back."
            : "Press a class's number to record it now, N for a new class, E and a number to change one, D and a number to remove one, or Esc to go back.");

        while (true)
        {
            var key = await ui.ReadKeyAsync(CancellationToken.None);
            if (key is not { } pressed || IsBack(pressed)) return 0;

            if (pressed.Key == ConsoleKey.N) return await AddAsync();
            if (pressed.Key is ConsoleKey.E or ConsoleKey.D && all.Count > 0)
            {
                ui.WriteLine(pressed.Key == ConsoleKey.E ? "Which class to change? Its number:" : "Which class to remove? Its number:");
                if (await ReadNumberAsync(all.Count, first: null) is not { } index) return 0;
                return pressed.Key == ConsoleKey.E ? await EditAsync(all[index]) : await RemoveAsync(all[index]);
            }

            if (pressed.KeyChar is >= '1' and <= '9' && await ReadNumberAsync(all.Count, pressed.KeyChar) is { } picked) return await StartAsync(all[picked]);
        }
    }

    /// <summary>
    /// Records <paramref name="entry"/> as saved, through the same path as choices 1 and 2 (<see cref="StartOrigin.Menu"/>):
    /// its course as the recording's name, its plan, its folder or the settings', its name in the request; transcribed
    /// unless the class says to record only. One that cannot be used says why, and nothing starts.
    /// </summary>
    private async Task<int> StartAsync(SavedClassEntry entry)
    {
        if (entry.Class is not { } saved)
        {
            ui.WriteLine(entry.CannotBeUsed);
            return 0;
        }

        var root = saved.OutputFolder is { } own ? ClassFolders.RootFor(own) : folders.Root;
        ui.WriteLine($"Recording {saved.Course} ({(saved.Name == saved.Course ? "saved class" : $"the saved class \"{saved.Name}\"")}): {saved.Describe()}, into {Path.Combine(root, saved.Course)}{(saved.Transcribe ? ", then transcribed" : "")}.");
        var request = new RecordRequest(RunIds.New(time.GetUtcNow()), saved.Course, saved.Transcribe, saved.Plan, root, ContinueChoice.Ask,
            ContinueBaseName: null, Minutes: null, Open: true, ClassName: saved.Name);
        return await commands.RecordAsync(request, StartOrigin.Menu);
    }

    /// <summary>
    /// N: a new class, asked step by step: the preset (L for a lesson video, in person with a camera; Enter for none), the
    /// name, the course, what it records, the microphone, the camera, the folder and whether to transcribe it; saved through
    /// class add, which says how to start it from PowerShell.
    /// </summary>
    private async Task<int> AddAsync()
    {
        ui.WriteLine("A new class. L starts from the Lesson video preset (in person, with a camera), Enter answers each question; Esc goes back at any of them.");
        var key = await ui.ReadKeyAsync(CancellationToken.None);
        if (key is not { } pressed || IsBack(pressed)) return 0;

        var lesson = pressed.Key == ConsoleKey.L;
        ui.Write("Name of the class, such as Mantiq or Kalam (Zoom app): ");
        if (Trimmed(await ui.ReadLineAsync(CancellationToken.None)) is not { } name) return 0;
        if (ClassCommand.NameProblem(name) is { } problem)
        {
            ui.WriteLine(problem);
            return 0;
        }

        if (classes.Find(name) is { } existing)
        {
            ui.WriteLine($"There is a saved class \"{existing.Name}\" already; E changes it.");
            return 0;
        }

        var answers = await AskAsync(name, current: null, lesson);
        return answers is null ? 0 : await commands.ClassAsync(new ClassRequest.Add(name, answers));
    }

    /// <summary>
    /// E: the same questions as N, with the class's own answers for Enter; saved through class set. A class that cannot be
    /// used says why first, and its Enter answers are its fields as the file holds them (see <see cref="Kept.FromFields"/>),
    /// so that Enter through every question keeps its fields (where they disagree, as a microphone on a class recorded
    /// alone does, its mode decides); a field the questions do not ask about is left for class set --clear or the file.
    /// </summary>
    private async Task<int> EditAsync(SavedClassEntry entry)
    {
        Kept kept;
        if (entry.Class is { } usable)
        {
            kept = Kept.FromClass(usable);
        }
        else
        {
            ui.WriteLine(entry.CannotBeUsed);
            ui.WriteLine($"Enter keeps each of its values as the file holds them. A field these questions do not ask about can be emptied with ClassScribe class set \"{entry.Name}\" --clear <field>, or changed in the file (S, then O).");
            kept = Kept.FromFields(entry.Name, classes.FieldsOf(entry.Name) ?? []);
        }

        var answers = await AskAsync(entry.Name, kept, lesson: false);
        return answers is null ? 0 : await commands.ClassAsync(new ClassRequest.Set(entry.Name, answers));
    }

    /// <summary>D: asks Y, then removes the class from the settings; no recording is touched.</summary>
    private async Task<int> RemoveAsync(SavedClassEntry entry)
    {
        ui.WriteLine($"Remove the saved class \"{entry.Name}\"? Y removes it (no recording is touched); any other key keeps it.");
        var key = await ui.ReadKeyAsync(CancellationToken.None);
        return key?.Key == ConsoleKey.Y ? await commands.ClassAsync(new ClassRequest.Remove(entry.Name)) : 0;
    }

    /// <summary>
    /// The questions of N and E, with <paramref name="current"/>'s answers for Enter when it is given, and the Lesson video
    /// preset's when <paramref name="lesson"/>: the course; what it records; the microphone, and with the class the app it
    /// follows; in person the camera and its size; the folder; and whether to transcribe it. Null for Esc at any of them.
    /// </summary>
    private async Task<ClassChoices?> AskAsync(string name, Kept? current, bool lesson)
    {
        var course = current?.Course ?? name;
        ui.Write($"Course, which names its folder and files (Enter: {course}): ");
        if (await ui.ReadLineAsync(CancellationToken.None) is not { } typedCourse) return null;
        course = Trimmed(typedCourse) ?? course;

        var source = lesson ? new SourceGiven(SourceOption.InPerson, null) : await AskSourceAsync(current);
        if (source is null) return null;

        var inPerson = source.Option == SourceOption.InPerson;
        string? microphone = current?.Microphone;
        var withMicrophone = inPerson || current?.Mode == RecordingMode.ClassAndMicrophone;
        string? follows = current?.Follows;
        if (!inPerson)
        {
            ui.WriteLine($"Your microphone beside the class? Y records it, N records the class alone, Enter keeps {(withMicrophone ? "it" : "the class alone")}.");
            var key = await ui.ReadKeyAsync(CancellationToken.None);
            if (key is not { } answer || IsBack(answer)) return null;
            if (answer.Key == ConsoleKey.Y) withMicrophone = true;
            else if (answer.Key == ConsoleKey.N) withMicrophone = false;
        }

        if (withMicrophone)
        {
            var (back, picked) = await AskMicrophoneAsync(microphone);
            if (back) return null;
            microphone = picked;
        }

        if (withMicrophone && !inPerson)
        {
            ui.Write($"Follow the class app: Enter keeps {follows ?? "the app recorded, else Recording:MicrophoneFollows"}, type another app's name, A any app that holds the microphone, N none (V only): ");
            if (await ui.ReadLineAsync(CancellationToken.None) is not { } typed) return null;
            var answer = Trimmed(typed);
            follows = answer is null ? follows
                : answer.Equals("A", StringComparison.OrdinalIgnoreCase) ? FollowRule.AnyWord
                : answer.Equals("N", StringComparison.OrdinalIgnoreCase) ? FollowRule.NoneWord
                : answer;
        }

        string? camera = current?.Camera ?? (lesson ? ClassFields.AnyCamera : null);
        var size = current?.CameraResolution;
        if (inPerson && cameras is not null)
        {
            var asked = await AskCameraAsync(camera, size);
            if (asked.Back) return null;
            (camera, size) = (asked.Camera, asked.Size);
        }

        var folder = current?.OutputFolder;
        ui.Write($"Recordings folder (Enter: {(folder is null ? $"the settings', {folders.Root}" : folder)}; - for the settings'): ");
        if (await ui.ReadLineAsync(CancellationToken.None) is not { } typedFolder) return null;
        var clear = new List<string>();
        if (Trimmed(typedFolder) is { } answeredFolder)
        {
            if (answeredFolder == "-")
            {
                folder = null;
                clear.Add("output");
            }
            else
            {
                folder = Path.GetFullPath(Environment.ExpandEnvironmentVariables(answeredFolder.Trim('"')));
            }
        }

        // Null keeps the class's Transcribe as the file holds it, for one whose value is neither true nor false.
        bool? transcribe = current is null ? true : current.Transcribe;
        ui.WriteLine($"Transcribe it when it stops? Y transcribes, N records only, Enter keeps {transcribe switch { true => "transcribing", false => "record only", null => "it as the file holds it" }}.");
        var transcribeKey = await ui.ReadKeyAsync(CancellationToken.None);
        if (transcribeKey is not { } transcribeAnswer || IsBack(transcribeAnswer)) return null;
        if (transcribeAnswer.Key == ConsoleKey.Y) transcribe = true;
        else if (transcribeAnswer.Key == ConsoleKey.N) transcribe = false;

        if (camera is null && current?.Camera is not null) clear.Add("camera");
        return new ClassChoices
        {
            Course = course,
            Source = inPerson ? source with { Value = microphone } : source,
            ClassAlone = !inPerson && !withMicrophone,
            Mic = !inPerson && withMicrophone,
            MicName = inPerson ? null : microphone,
            Follows = !inPerson && withMicrophone ? follows : null,
            Camera = inPerson && camera is not null,
            CameraName = camera is null || camera.Equals(ClassFields.AnyCamera, StringComparison.OrdinalIgnoreCase) ? null : camera,
            Resolution = inPerson && camera is not null ? size : null,
            Output = folder is not null && !clear.Contains("output") ? folder : null,
            Transcribe = transcribe,
            Clear = clear
        };
    }

    /// <summary>
    /// What the class records: A an app, from the apps with sound now or typed; D an output device, from those connected or
    /// typed; W whatever device the class plays on; P in person, your microphone alone; Enter keeps <paramref name="current"/>'s,
    /// when it has one source. Null for Esc.
    /// </summary>
    private async Task<SourceGiven?> AskSourceAsync(Kept? current)
    {
        var kept = current?.Source;
        ui.WriteLine($"What it records: A an app, D an output device, W whatever device the class plays on, P in person (your microphone alone){(kept is null ? "" : ", Enter keeps it")}.");
        while (true)
        {
            var key = await ui.ReadKeyAsync(CancellationToken.None);
            if (key is not { } pressed || IsBack(pressed)) return null;

            switch (pressed.Key)
            {
                case ConsoleKey.Enter when kept is not null:
                    return kept;
                case ConsoleKey.W:
                    return new SourceGiven(SourceOption.Device, "auto");
                case ConsoleKey.P:
                    return new SourceGiven(SourceOption.InPerson, null);
                case ConsoleKey.A:
                    var apps = DevicesCommand.AppsByName(devices.GetPlayingApps()).Select(app => app.Name).Take(MostListed).ToList();
                    return await PickAsync("App", apps, "an app's process name, such as chrome or zoom") is { } app ? new SourceGiven(SourceOption.App, app) : null;
                case ConsoleKey.D:
                    var outputs = devices.GetOutputDevices().Select(device => device.Name).Take(MostListed).ToList();
                    return await PickAsync("Output device", outputs, "part of a device's name, such as Pixel Buds") is { } device ? new SourceGiven(SourceOption.Device, device) : null;
            }
        }
    }

    /// <summary>
    /// Lists <paramref name="listed"/>, numbered, and reads a line: a number picks one, anything else is taken as typed, as
    /// <paramref name="what"/> says. Null for Esc or an empty line.
    /// </summary>
    private async Task<string?> PickAsync(string heading, IReadOnlyList<string> listed, string what)
    {
        ui.WriteLine($"{heading}s now:");
        if (listed.Count == 0) ui.WriteLine("  None now.");
        for (var i = 0; i < listed.Count; i++) ui.WriteLine($"  {i + 1}  {listed[i]}");
        ui.Write($"Type a number, or {what}, then press Enter: ");
        if (Trimmed(await ui.ReadLineAsync(CancellationToken.None)) is not { } typed) return null;

        return int.TryParse(typed, out var number) && number >= 1 && number <= listed.Count ? listed[number - 1] : typed;
    }

    /// <summary>The microphone, as menu choice 7 asks it: a number, or Enter for <paramref name="current"/>, or the settings' and Windows' default. Back for Esc.</summary>
    private async Task<(bool Back, string? Picked)> AskMicrophoneAsync(string? current)
    {
        var listed = microphones.GetMicrophones().Where(microphone => !microphone.IsLoopbackInput).Take(MostListed).ToList();
        ui.WriteLine("Microphones:");
        if (listed.Count == 0) ui.WriteLine("  None is connected now; the recording waits for one.");
        for (var i = 0; i < listed.Count; i++) ui.WriteLine($"  {i + 1}  {MicrophoneMenu.Describe(listed[i])}");
        ui.WriteLine(current is null
            ? "Press a number, Enter for Recording:Microphone or Windows' default microphone (never a Bluetooth one), or Esc to go back."
            : $"Press a number, Enter keeps \"{current}\", or Esc to go back.");
        while (true)
        {
            var key = await ui.ReadKeyAsync(CancellationToken.None);
            if (key is not { } pressed || IsBack(pressed)) return (true, null);
            if (pressed.Key == ConsoleKey.Enter) return (false, current);
            if (pressed.KeyChar is >= '1' and <= '9' && pressed.KeyChar - '1' < listed.Count) return (false, listed[pressed.KeyChar - '1'].Name);
        }
    }

    /// <summary>
    /// In person: the camera that records a lesson's picture too (a number; Enter keeps <paramref name="current"/>, or none;
    /// N none) and its size (Enter keeps <paramref name="size"/>, or the setting; 1, 2 or 3 for 480p, 720p or 1080p). Back
    /// for Esc.
    /// </summary>
    private async Task<(bool Back, string? Camera, string? Size)> AskCameraAsync(string? current, string? size)
    {
        var list = await cameras!.GetCamerasAsync(CancellationToken.None);
        var listed = list.Cameras.Take(MostListed).ToList();
        ui.WriteLine("Camera, for a lesson video:");
        if (list.Problem is { } problem) ui.WriteLine($"  The cameras could not be listed: {problem}.");
        for (var i = 0; i < listed.Count; i++) ui.WriteLine($"  {i + 1}  {listed[i].Described}");
        ui.WriteLine($"Press a camera's number, Enter keeps {(current is null ? "no camera" : current.Equals(ClassFields.AnyCamera, StringComparison.OrdinalIgnoreCase) ? "any camera (a USB camera, else the built-in one)" : $"\"{current}\"")}, N for none, or Esc to go back.");
        string? camera;
        while (true)
        {
            var key = await ui.ReadKeyAsync(CancellationToken.None);
            if (key is not { } pressed || IsBack(pressed)) return (true, null, null);
            if (pressed.Key == ConsoleKey.N) return (false, null, null);
            if (pressed.Key == ConsoleKey.Enter)
            {
                camera = current;
                break;
            }

            if (pressed.KeyChar is >= '1' and <= '9' && pressed.KeyChar - '1' < listed.Count)
            {
                camera = listed[pressed.KeyChar - '1'].Name;
                break;
            }
        }

        if (camera is null) return (false, null, null);
        ui.WriteLine($"Picture size: Enter keeps {size ?? "the setting"}, 1 480p, 2 720p, 3 1080p");
        while (true)
        {
            var key = await ui.ReadKeyAsync(CancellationToken.None);
            if (key is not { } pressed || IsBack(pressed)) return (true, null, null);
            if (pressed.Key == ConsoleKey.Enter) return (false, camera, size);
            if (pressed.KeyChar is >= '1' and <= '3') return (false, camera, CameraSize.Presets[pressed.KeyChar - '1'].Size.Text);
        }
    }

    /// <summary>
    /// A class's number: with up to nine, the digit <paramref name="first"/> or the next one pressed; with more, the number
    /// typed and Enter, starting with <paramref name="first"/> when given. Null for Esc or a number that is no class's.
    /// </summary>
    private async Task<int?> ReadNumberAsync(int count, char? first)
    {
        if (count <= MostListed)
        {
            var digit = first;
            while (digit is null)
            {
                var key = await ui.ReadKeyAsync(CancellationToken.None);
                if (key is not { } pressed || IsBack(pressed)) return null;
                if (pressed.KeyChar is >= '1' and <= '9') digit = pressed.KeyChar;
            }

            return digit - '1' < count ? digit - '1' : null;
        }

        ui.Write(first is null ? "Type the number and press Enter: " : $"{first}");
        var rest = await ui.ReadLineAsync(CancellationToken.None);
        return int.TryParse($"{first}{rest?.Trim()}", out var number) && number >= 1 && number <= count ? number - 1 : null;
    }

    /// <summary>A typed answer trimmed of spaces and the quotes Windows puts around a dragged path; null for nothing.</summary>
    private static string? Trimmed(string? answer) => string.IsNullOrWhiteSpace(answer) ? null : answer.Trim().Trim('"').Trim();

    /// <summary>True for the keys that go back to the menu: Esc, Q and Ctrl+C.</summary>
    private static bool IsBack(ConsoleKeyInfo key) =>
        key.Key is ConsoleKey.Escape or ConsoleKey.Q || (key.Key == ConsoleKey.C && key.Modifiers.HasFlag(ConsoleModifiers.Control));

    /// <summary>The Enter answers E offers: a class's own values, from a usable class, or from the fields of one that cannot be used.</summary>
    /// <param name="Course">The course.</param>
    /// <param name="Mode">The class alone, with your microphone, or in person.</param>
    /// <param name="Source">The source, as class set takes it; null when the class has no one source to keep, so Enter is not offered.</param>
    /// <param name="Microphone">Part of the microphone's name; null for the settings and Windows' default.</param>
    /// <param name="Follows">The app the microphone follows, as the file writes it; null for none written.</param>
    /// <param name="Camera">The camera, in person: part of its name, or any; null for none.</param>
    /// <param name="CameraResolution">The camera's picture size; null for the setting.</param>
    /// <param name="OutputFolder">The recordings folder; null for the settings'.</param>
    /// <param name="Transcribe">True or false; null for a value that is neither, which Enter keeps as the file holds it.</param>
    private sealed record Kept(string Course, RecordingMode Mode, SourceGiven? Source, string? Microphone, string? Follows, string? Camera, string? CameraResolution,
        string? OutputFolder, bool? Transcribe)
    {
        /// <summary>The Enter answers of a usable class: its own values.</summary>
        public static Kept FromClass(SavedClass saved) => new(saved.Course, saved.Mode,
            saved.Mode == RecordingMode.Microphone ? new SourceGiven(SourceOption.InPerson, saved.Microphone)
            : saved.Source switch
            {
                ClassSource.App app => new SourceGiven(SourceOption.App, app.ProcessName),
                ClassSource.Device device => new SourceGiven(SourceOption.Device, device.NamePart),
                _ => new SourceGiven(SourceOption.Device, "auto")
            },
            saved.Microphone, saved.Follows?.Word, saved.Camera, saved.CameraResolution, saved.OutputFolder, saved.Transcribe);

        /// <summary>
        /// The Enter answers of the class <paramref name="name"/> that cannot be used, from its <paramref name="fields"/> as the
        /// file holds them, so that Enter at every question writes back what is there: the course, else the name; the mode in
        /// any case, and one that is none of the three taken as with your microphone when a microphone or the app it follows
        /// is set, else the class alone; the source in person, or the one app or device set, none when both are; and the rest
        /// as written.
        /// </summary>
        public static Kept FromFields(string name, IReadOnlyDictionary<string, string?> fields)
        {
            // A field's text, trimmed; null when it is not there or holds only spaces.
            string? Field(string field) => fields.TryGetValue(field, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;

            var microphone = Field(ClassFields.Microphone);
            var follows = Field(ClassFields.MicrophoneFollows);
            var mode = Field(ClassFields.Mode) is not { } modeText ? RecordingMode.Class
                : Enum.GetNames<RecordingMode>().FirstOrDefault(known => known.Equals(modeText, StringComparison.OrdinalIgnoreCase)) is { } known ? Enum.Parse<RecordingMode>(known)
                : microphone is not null || follows is not null ? RecordingMode.ClassAndMicrophone
                : RecordingMode.Class;
            var (app, device) = (Field(ClassFields.App), Field(ClassFields.Device));
            var source = mode == RecordingMode.Microphone ? new SourceGiven(SourceOption.InPerson, microphone)
                : app is not null && device is not null ? null
                : app is not null ? new SourceGiven(SourceOption.App, app)
                : new SourceGiven(SourceOption.Device, device ?? "auto");
            bool? transcribe = Field(ClassFields.Transcribe) is not { } text ? true : bool.TryParse(text, out var yes) ? yes : null;
            return new Kept(Field(ClassFields.Course) ?? name, mode, source, microphone, follows, Field(ClassFields.Camera), Field(ClassFields.CameraResolution),
                Field(ClassFields.OutputFolder), transcribe);
        }
    }
}
