using System.CommandLine;
using System.CommandLine.Parsing;
using ClassScribe.Audio.Microphones;
using ClassScribe.Camera;
using ClassScribe.Settings;
using ClassScribe.Storage;

namespace ClassScribe.Commands;

/// <summary>The class command of the command line (layout-config 4.4): list, show, add, set, rename and remove.</summary>
public sealed partial class CommandLineTree
{
    /// <summary>
    /// class: list and show, each with --json; add and set with record's source and microphone options, --camera and
    /// --resolution, --course, --output, --record-only or --transcribe, --keyterms, and for set --clear; rename with the new name;
    /// remove. class alone is a mistake that names them all. The mixes record refuses are refused here too, before anything
    /// runs; what depends on the class is said as the command runs, exit 2 with the help.
    /// </summary>
    private Command ClassCommandTree(HashSet<string> optionNames, Dictionary<Command, string> examples)
    {
        var listJson = JsonOption();
        var list = new Command("list", "List the saved classes: what each records, or why it cannot be used.") { listJson };
        list.SetAction(parsed => RunClassAsync(parsed, new ClassRequest.List(parsed.GetValue(listJson))));
        examples[list] = ClassExamples;

        var showName = ClassName();
        var showJson = JsonOption();
        var show = new Command("show", "Show one saved class: its fields, what it records and its command. Exit 0 when it can be used, 2 when it cannot or is not there.") { showName, showJson };
        show.SetAction(parsed => RunClassAsync(parsed, new ClassRequest.Show(parsed.GetValue(showName)!, parsed.GetValue(showJson))));
        examples[show] = ClassExamples;

        var add = ClassChangeCommand("add", "Save a new class: its name, what it records, your microphone, its folder; its course is its name unless --course gives one.", withClear: false,
            optionNames, examples, (name, choices) => new ClassRequest.Add(name, choices));
        var set = ClassChangeCommand("set", "Change a saved class: a source replaces its whole source, --mic its microphone, --clear empties a field.", withClear: true,
            optionNames, examples, (name, choices) => new ClassRequest.Set(name, choices));

        var renameName = ClassName();
        var newName = new Argument<string>("new name") { Description = "Its new name. Its course, folders, files and Google Drive stay as they are." };
        var rename = new Command("rename", "Rename a saved class; its course, folders, files and Google Drive stay as they are.") { renameName, newName };
        rename.SetAction(parsed => RunClassAsync(parsed, new ClassRequest.Rename(parsed.GetValue(renameName)!, parsed.GetValue(newName)!)));
        examples[rename] = ClassExamples;

        var removeName = ClassName();
        var remove = new Command("remove", "Remove a saved class. No recording is touched.") { removeName };
        remove.SetAction(parsed => RunClassAsync(parsed, new ClassRequest.Remove(parsed.GetValue(removeName)!)));
        examples[remove] = ClassExamples;

        var command = new Command("class", "Saved classes from PowerShell: list, show, add, set, rename, remove. record --class records one.") { list, show, add, set, rename, remove };
        command.Validators.Add(result =>
        {
            if (!result.Children.OfType<CommandResult>().Any())
                result.AddError("class needs one of list, show, add, set, rename or remove, such as ClassScribe class list");
        });

        // Never run: the validator above refuses class alone.
        command.SetAction(_ => Mistake);
        examples[command] = ClassExamples;
        return command;
    }

    /// <summary>The argument that names a saved class.</summary>
    private static Argument<string> ClassName() => new("name") { Description = "The saved class's name, in any case, such as \"Mantiq\"." };

    /// <summary>
    /// class add or class set, named <paramref name="word"/>: the class's name and its options (see <see cref="ClassSymbols"/>);
    /// <paramref name="request"/> makes the request from the name and the choices.
    /// </summary>
    private Command ClassChangeCommand(string word, string description, bool withClear, HashSet<string> optionNames, Dictionary<Command, string> examples,
        Func<string, ClassChoices, ClassRequest> request)
    {
        var symbols = new ClassSymbols(
            ClassName(),
            new Option<string>("--course") { HelpName = "course", Description = "The recording's name, which names its folder and files; class add copies the class's name." },
            new Option<string>("--app") { HelpName = "process", Description = "Record one app's sound, by its process name, such as chrome or zoom." },
            new Option<string>("--device") { HelpName = "part of its name", Description = "Record that output device, by part of its name, or auto for the device the class plays on." },
            new Option<string>("--in-person") { HelpName = "microphone", Arity = ArgumentArity.ZeroOrOne, Description = "Record your microphone alone, in the room: part of its name, or nothing." },
            new Option<string>("--mic") { HelpName = "microphone", Arity = ArgumentArity.ZeroOrOne, Description = "Record your microphone beside the class: part of its name, or nothing for the settings' and Windows' default." },
            new Option<string>("--mic-follows") { HelpName = "app", Description = $"The app whose microphone use your microphone follows: part of its name, {FollowRule.AnyWord} or {FollowRule.NoneWord}." },
            new Option<string>("--camera") { HelpName = "camera", Arity = ArgumentArity.ZeroOrOne, Description = "In person: a lesson's picture too, from the camera whose name holds this part, or nothing for any (auto)." },
            new Option<string>("--resolution") { HelpName = "size", Description = $"With a camera: the picture's size, {CameraSize.Rule}." },
            new Option<string>("--output") { HelpName = "folder", Description = "The class's own recordings folder; without it, Recording:OutputFolder." },
            new Option<bool>("--record-only") { Description = "When started from menu C, record only; transcribe it later." },
            new Option<bool>("--transcribe") { Description = "When started from menu C, record and transcribe (the default)." },
            new Option<string>("--keyterms")
            {
                HelpName = "file",
                Description = $"The class's own glossary, one term per line, such as \"%APPDATA%\\ClassScribe\\mantiq.txt\" (a relative path is taken from your settings folder), or {SavedClass.NoGlossary} for no glossary. Without it, ElevenLabs:KeytermsFile."
            },
            withClear
                ? new Option<string[]>("--clear") { HelpName = "field", Arity = ArgumentArity.OneOrMore, AllowMultipleArgumentsPerToken = false, Description = $"Empty a field: {SettingsCatalog.Listed([.. ClassChoices.ClearWords.Keys])}; give --clear once for each." }
                : null);

        var command = new Command(word, description)
        {
            symbols.Name, symbols.Course, symbols.App, symbols.Device, symbols.InPerson, symbols.Mic, symbols.MicFollows, symbols.Camera, symbols.Resolution, symbols.Output,
            symbols.RecordOnly, symbols.Transcribe, symbols.Keyterms
        };
        if (symbols.Clear is { } clear) command.Add(clear);
        command.Validators.Add(result =>
        {
            foreach (var problem in ClassProblems(word, result, symbols, optionNames)) result.AddError(problem);
        });
        command.SetAction(parsed => RunClassAsync(parsed, request(parsed.GetValue(symbols.Name)!, ChoicesOf(parsed, symbols))));
        examples[command] = ClassExamples;
        return command;
    }

    /// <summary>
    /// What keeps class add or class set from running, one sentence each: two sources, --mic with --in-person, --mic-follows
    /// with --in-person, --record-only with --transcribe, an option without its value, a value that cannot be used, an unknown
    /// --clear word or one given with its own option, and class set without anything to change.
    /// </summary>
    private static IEnumerable<string> ClassProblems(string word, CommandResult result, ClassSymbols symbols, HashSet<string> optionNames)
    {
        var device = result.GetResult(symbols.Device);
        var app = result.GetResult(symbols.App);
        var inPerson = result.GetResult(symbols.InPerson);
        var sources = new (OptionResult? Given, string Name)[] { (device, symbols.Device.Name), (app, symbols.App.Name), (inPerson, symbols.InPerson.Name) }
            .Where(source => source.Given is not null)
            .Select(source => source.Name)
            .ToList();
        if (sources.Count > 1) yield return $"class {word} takes one source; you gave {Listed(sources)}";
        if (device is not null && ValueProblem(TokenOf(device), DeviceNeedsValue, optionNames) is { } deviceProblem) yield return deviceProblem;
        if (app is not null && ValueProblem(TokenOf(app), AppNeedsValue, optionNames) is { } appProblem) yield return appProblem;
        if (result.GetResult(symbols.Course) is { } course && ValueProblem(TokenOf(course), "--course needs the recording's name after it, such as --course \"Mantiq\"", optionNames) is { } courseProblem)
            yield return courseProblem;

        if (result.GetResult(symbols.Mic) is not null && inPerson is not null) yield return "--mic adds the microphone to --device or --app; with --in-person the microphone is already the source";
        if (result.GetResult(symbols.MicFollows) is { } follows)
        {
            if (inPerson is not null) yield return "--mic-follows cannot go with --in-person: in person there is no class app";
            if (FollowProblem(TokenOf(follows), optionNames) is { } followProblem) yield return followProblem;
        }

        if (result.GetResult(symbols.Resolution) is { } resolution && ResolutionProblem(TokenOf(resolution), optionNames) is { } resolutionProblem) yield return resolutionProblem;
        if (result.GetResult(symbols.Output) is { } output && FolderProblem(symbols.Output.Name, TokenOf(output), optionNames) is { } outputProblem) yield return outputProblem;
        if (result.GetValue(symbols.RecordOnly) && result.GetValue(symbols.Transcribe)) yield return "--record-only and --transcribe cannot go together";
        if (result.GetResult(symbols.Keyterms) is { } keyterms && ClassGlossaryProblem(TokenOf(keyterms), optionNames) is { } keytermsProblem) yield return keytermsProblem;

        var clears = symbols.Clear is { } clear ? result.GetValue(clear) ?? [] : [];
        foreach (var unknown in clears.Where(given => !ClassChoices.ClearWords.ContainsKey(given)))
            yield return $"--clear takes {SettingsCatalog.Listed([.. ClassChoices.ClearWords.Keys])}, not \"{unknown}\"";
        foreach (var (cleared, option) in new[]
                 {
                     ("output", symbols.Output), ("microphone", symbols.Mic), ("follows", symbols.MicFollows), ("camera", symbols.Camera), ("resolution", symbols.Resolution),
                     ("keyterms", symbols.Keyterms)
                 })
        {
            if (clears.Contains(cleared, StringComparer.OrdinalIgnoreCase) && result.GetResult(option) is not null)
                yield return $"--clear {cleared} and {option.Name} cannot go together";
        }

        var anything = result.Children.OfType<OptionResult>().Any();
        if (word == "set" && !anything) yield return "class set needs something to change, such as ClassScribe class set \"Mantiq\" --app zoom --mic-follows zoom";
    }

    /// <summary>The choices class add or class set was given, from <paramref name="parsed"/>.</summary>
    private static ClassChoices ChoicesOf(ParseResult parsed, ClassSymbols symbols) => new()
    {
        Course = parsed.GetValue(symbols.Course) is { } course && !string.IsNullOrWhiteSpace(course) ? course.Trim() : null,
        Source = parsed.GetResult(symbols.InPerson) is not null ? new SourceGiven(SourceOption.InPerson, NamePart(parsed.GetValue(symbols.InPerson)))
            : parsed.GetValue(symbols.App) is { } app ? new SourceGiven(SourceOption.App, ProcessName(app.Trim()))
            : parsed.GetValue(symbols.Device) is { } device ? new SourceGiven(SourceOption.Device, device.Trim())
            : null,
        Mic = parsed.GetResult(symbols.Mic) is not null,
        MicName = NamePart(parsed.GetValue(symbols.Mic)),
        Follows = parsed.GetValue(symbols.MicFollows)?.Trim(),
        Camera = parsed.GetResult(symbols.Camera) is not null,
        CameraName = NamePart(parsed.GetValue(symbols.Camera)),
        Resolution = ResolutionOf(parsed.GetValue(symbols.Resolution)),
        Output = parsed.GetValue(symbols.Output) is { } output ? Path.GetFullPath(output) : null,
        Transcribe = parsed.GetValue(symbols.RecordOnly) ? false : parsed.GetValue(symbols.Transcribe) ? true : null,
        Keyterms = parsed.GetValue(symbols.Keyterms) is { } keyterms ? keyterms.Trim().Trim('"').Trim() : null,
        Clear = symbols.Clear is { } clear ? parsed.GetValue(clear) ?? [] : []
    };

    /// <summary>
    /// What is wrong with <paramref name="value"/> as a saved class's glossary, --keyterms of class add and class set: no value,
    /// or a character Windows does not allow in a path. Null for none, and for a file, which is saved as written, whether or
    /// not it is there yet: the class command says when it is not.
    /// </summary>
    private static string? ClassGlossaryProblem(string? value, HashSet<string> optionNames)
    {
        if (ValueProblem(value, KeytermsNeedsValue, optionNames) is { } missing) return missing;

        var file = value!.Trim().Trim('"').Trim();
        return file.IndexOfAny(NotInPaths) >= 0 || file.Any(char.IsControl)
            ? $"--keyterms takes a file; \"{file}\" has a character Windows does not allow in a path: < > \" | ? *"
            : null;
    }

    /// <summary>Runs <paramref name="request"/> through the runner; a mistake it finds, exit 2, is followed by the command's help.</summary>
    private async Task<int> RunClassAsync(ParseResult parsed, ClassRequest request)
    {
        var exitCode = await commands.ClassAsync(request);
        if (exitCode == Mistake && request is not ClassRequest.Show) WriteHelp(parsed);
        return exitCode;
    }

    /// <summary>The symbols of class add and class set, which their check and their action share.</summary>
    /// <param name="Name">The class's name.</param>
    /// <param name="Course">--course: the recording's name.</param>
    /// <param name="App">--app: an app's process name.</param>
    /// <param name="Device">--device: part of a device's name, or auto.</param>
    /// <param name="InPerson">--in-person: the microphone alone, with part of its name or nothing.</param>
    /// <param name="Mic">--mic: the microphone beside the class, with part of its name or nothing.</param>
    /// <param name="MicFollows">--mic-follows: the app the microphone follows, or auto or none.</param>
    /// <param name="Camera">--camera: a lesson's picture too, in person, with part of a camera's name or nothing.</param>
    /// <param name="Resolution">--resolution: the picture's size.</param>
    /// <param name="Output">--output: the class's own recordings folder.</param>
    /// <param name="RecordOnly">--record-only: menu C records only.</param>
    /// <param name="Transcribe">--transcribe: menu C records and transcribes.</param>
    /// <param name="Keyterms">--keyterms: the class's own glossary, or none.</param>
    /// <param name="Clear">--clear: fields to empty; null for class add, which has none.</param>
    private sealed record ClassSymbols(Argument<string> Name, Option<string> Course, Option<string> App, Option<string> Device, Option<string> InPerson, Option<string> Mic,
        Option<string> MicFollows, Option<string> Camera, Option<string> Resolution, Option<string> Output, Option<bool> RecordOnly, Option<bool> Transcribe,
        Option<string> Keyterms, Option<string[]>? Clear);

    /// <summary>The examples and notes under the help of class and its commands.</summary>
    private const string ClassExamples = """
        Examples:
          ClassScribe class list
          ClassScribe class show "Mantiq" --json
          ClassScribe class add "Mantiq" --app chrome --mic "Microphone Array" --mic-follows chrome
          ClassScribe class add "Kalam (in person)" --course "Kalam" --in-person "Microphone Array" --record-only
          ClassScribe class add "Lesson" --in-person --camera "OBSBOT" --resolution 720p
          ClassScribe class set "Mantiq" --output "D:\Classes"
          ClassScribe class set "Mantiq" --keyterms "%APPDATA%\ClassScribe\mantiq.txt"
          ClassScribe class set "Mantiq" --clear output --clear follows --clear keyterms
          ClassScribe class rename "Mantiq" "Mantiq (Chrome)"
          ClassScribe class remove "Mantiq"
          ClassScribe record --class "Mantiq"

        A class is saved in your settings file under Classes, by its name, which is what it is started by; its course names the recording's folder and files.
        Names are matched exactly, in any case. class rename changes only the name; class set --course starts the next class in a new folder.
        Exit codes: 0 done (class show: the class can be used); 1 a settings file that is not valid JSON, has comments of your own, cannot be written, keeps changing or is missing (class show: only one that is not valid JSON or cannot be read); 2 an unknown or unusable class, a value the check refuses, or another mistake (class show: also while the settings file is missing, so that a launcher records with its own values).
        """;
}
