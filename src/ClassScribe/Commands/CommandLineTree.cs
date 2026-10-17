using System.CommandLine;
using System.CommandLine.Help;
using System.CommandLine.Invocation;
using System.CommandLine.Parsing;
using System.Globalization;
using System.Text.RegularExpressions;
using ClassScribe.Audio;
using ClassScribe.Desktop;
using ClassScribe.Storage;

namespace ClassScribe.Commands;

/// <summary>A parsed command line, or the exit code of one whose mistakes have been written with that command's help.</summary>
/// <param name="Parsed">The parse result to run; null when the command line held a mistake.</param>
/// <param name="ExitCode">2 when the mistakes and the help were written; null when there is something to run.</param>
public sealed record ParseOutcome(ParseResult? Parsed, int? ExitCode);

/// <summary>What a command line asks for, which decides what App does before it runs it.</summary>
public enum CommandKind
{
    /// <summary>Help, which needs no settings.</summary>
    Help,

    /// <summary>The menu, which reads the settings before each choice and shows the report at the start itself.</summary>
    Menu,

    /// <summary>Showing a recording in progress, which the settings never hold up.</summary>
    Attach,

    /// <summary>Any other command: the settings are read and checked first, then the report at the start is printed.</summary>
    Command
}

/// <summary>
/// ClassScribe's command line, on System.CommandLine, behind ClassScribe's own way of reporting mistakes:
/// <code>
/// ClassScribe                                     the menu
/// ClassScribe &lt;path&gt;                              as transcribe &lt;path&gt;; a file dropped on the exe
/// ClassScribe record "&lt;name&gt;" SOURCE --output "&lt;folder&gt;" [--minutes &lt;n&gt;] [--no-open] [--continue | --new]
/// ClassScribe record-only "&lt;name&gt;" ...          the same options
///     SOURCE is --device "&lt;part of its name&gt;", --device auto or --app &lt;process&gt;
/// ClassScribe transcribe &lt;path&gt; [--output "&lt;folder&gt;"] [--no-open] [--again]
/// ClassScribe rebuild &lt;path&gt; [--no-open]
/// ClassScribe list [--output "&lt;folder&gt;"]
/// ClassScribe devices
/// ClassScribe &lt;command&gt; --help                    also -h, -?, /? and help &lt;command&gt;
/// hidden: ClassScribe attach [--alert &lt;run id&gt;]
/// </code>
/// A mistake is never asked about: every one is written to standard error, all of them in one run, then that command's
/// help to standard output, and the exit code is 2 (System.CommandLine's own would be 1). For record and record-only one
/// validator names each missing piece, because once a validator has found a mistake, System.CommandLine reports neither
/// its own missing pieces nor the values it cannot read. --minutes is read with a dot and the invariant culture, so 1,5
/// is refused rather than read as 15. An argument that starts with @ stays a path, and Ctrl+C stays with ClassScribe.
/// </summary>
public sealed partial class CommandLineTree(ICommandRunner commands, MenuCommand menu, IConsoleUi ui, TimeProvider time, TextWriter output, TextWriter errors)
{
    /// <summary>The exit code of a command line with a mistake in it.</summary>
    public const int Mistake = 2;

    /// <summary>The most minutes --minutes takes: a day.</summary>
    private const double MostMinutes = 1440;

    /// <summary>The word that asks for help, before a command or alone, as the earlier command line took it.</summary>
    private const string HelpWord = "help";

    /// <summary>The value of --device that means the device the class plays on.</summary>
    private const string Automatic = "auto";

    /// <summary>
    /// The name of the hidden command that shows a recording in progress: the word the process starter writes on the alert
    /// window's command line, so that the two agree.
    /// </summary>
    private const string AttachName = ProcessArguments.Attach;

    /// <summary>What --minutes says about a value not written as a number with a dot.</summary>
    private const string MinutesFormat = "--minutes takes a number of minutes written with a dot, such as 1.5";

    /// <summary>What --minutes says about a number outside its range.</summary>
    private const string MinutesRange = "--minutes takes more than 0 and at most 1440 minutes (24 hours)";

    /// <summary>What --device says without a value.</summary>
    private const string DeviceNeedsValue = "--device needs part of a device's name after it, or auto";

    /// <summary>What --app says without a value.</summary>
    private const string AppNeedsValue = "--app needs an app's process name after it, such as chrome";

    /// <summary>What --output says without a value.</summary>
    private const string OutputNeedsValue = "--output needs a folder after it, such as \"D:\\Classes\"";

    /// <summary>The characters, besides control characters, that Windows does not allow anywhere in a path.</summary>
    private static readonly char[] NotInPaths = ['<', '>', '"', '|', '?', '*'];

    /// <summary>
    /// How the arguments are parsed: an argument that starts with @ stays a path rather than naming a file of arguments, and
    /// short options are never bundled, so a word such as -?h is not read as two options.
    /// </summary>
    private static readonly ParserConfiguration Parsing = new() { ResponseFileTokenReplacer = null, EnablePosixBundling = false };

    /// <summary>The tree that <see cref="Tidy"/>, <see cref="Parse"/> and <see cref="KindOf"/> use, built when first needed.</summary>
    private BuiltTree? _tree;

    /// <summary>
    /// How a parsed command line is run. Ctrl+C stays with ClassScribe: with a process termination timeout,
    /// System.CommandLine would end the process 2 seconds after a Ctrl+C, in the middle of a recording or an upload. A
    /// failure reaches App, which turns it into the exit code, rather than being printed by System.CommandLine.
    /// </summary>
    public InvocationConfiguration Invocation { get; } = new()
    {
        ProcessTerminationTimeout = null,
        EnableDefaultExceptionHandler = false,
        Output = output,
        Error = errors
    };

    /// <summary>The tree, built once.</summary>
    private BuiltTree Tree => _tree ??= BuildTree();

    /// <summary>Builds the command line's tree of commands, options and arguments, each command with its action and its help.</summary>
    public RootCommand Build() => BuildTree().Root;

    /// <summary>
    /// Tidies the arguments before they are parsed. Command and option names are taken in any case, as typed by habit; the
    /// word help before a command, or alone, asks for its help. Quotes around an argument go: Windows PowerShell 5.1 hands a
    /// quoted path that ends in a backslash over with a stray quote at the end, and joins what follows it into the same
    /// argument when that path has a space, as in C:\My Classes" --again. A quote cannot be part of a Windows path, so when
    /// an option follows the quote, the argument is split there again. A first word in square brackets is a path written
    /// from the current folder, so that the parser never takes it for a directive of its own.
    /// </summary>
    public string[] Tidy(string[] args)
    {
        var tree = Tree;
        string[] words = [.. args.SelectMany(argument => SplitOffJoinedOptions(argument, tree.OptionNames))];
        if (words is [var first, .. var rest] && first.Equals(HelpWord, StringComparison.OrdinalIgnoreCase)) words = [.. rest, "--help"];
        if (words is [var command, ..] && tree.CommandNames.Contains(command)) words[0] = command.ToLowerInvariant();

        for (var i = 0; i < words.Length; i++)
        {
            if (tree.OptionNames.Contains(words[i])) words[i] = words[i].ToLowerInvariant();
        }

        if (words is [var lone, ..] && lone.StartsWith('[') && lone.EndsWith(']')) words[0] = Path.Combine(".", lone);
        return words;
    }

    /// <summary>
    /// Parses the tidied arguments (see <see cref="Tidy"/>). A command line with mistakes has each written to standard error,
    /// then that command's help to standard output, and gives exit code 2; so does no command at all when nobody can type
    /// into the menu. Otherwise the parse result comes back, ready to run.
    /// </summary>
    public ParseOutcome Parse(string[] tidied)
    {
        var parsed = Tree.Root.Parse(tidied, Parsing);
        if (parsed.Errors.Count > 0)
        {
            foreach (var error in parsed.Errors) Invocation.Error.WriteLine(error.Message);
            WriteHelp(parsed);
            return new(null, Mistake);
        }

        if (KindOf(parsed) == CommandKind.Menu && !ui.IsInteractive)
        {
            Invocation.Error.WriteLine("ClassScribe shows its menu only when someone can type into its window; give it a command instead, such as one of those below.");
            WriteHelp(parsed);
            return new(null, Mistake);
        }

        return new(parsed, null);
    }

    /// <summary>What <paramref name="parsed"/> asks for: help, the menu, showing a recording in progress, or any other command.</summary>
    public CommandKind KindOf(ParseResult parsed)
    {
        var tree = Tree;
        if (parsed.Action is HelpWithExamples) return CommandKind.Help;
        if (parsed.CommandResult.Command == tree.Root) return parsed.GetValue(tree.LonePath) is null ? CommandKind.Menu : CommandKind.Command;

        return parsed.CommandResult.Command.Name == AttachName ? CommandKind.Attach : CommandKind.Command;
    }

    /// <summary>Runs what <paramref name="parsed"/> names (see <see cref="Invocation"/>) and returns its exit code.</summary>
    public Task<int> InvokeAsync(ParseResult parsed) => parsed.InvokeAsync(Invocation);

    /// <summary>Builds the tree, with the names of its commands and options, which tidying and the checks of values need.</summary>
    private BuiltTree BuildTree()
    {
        var optionNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var examples = new Dictionary<Command, string>();

        var root = new RootCommand("ClassScribe records the sound your computer plays during a class and gets a transcript from ElevenLabs.");
        root.Directives.Clear();
        foreach (var version in root.Options.OfType<VersionOption>().ToList()) root.Options.Remove(version);

        var lonePath = new Argument<string?>("path")
        {
            Arity = ArgumentArity.ZeroOrOne,
            Hidden = true,
            Description = "A recording's folder or file, or any audio or video file, to transcribe."
        };
        lonePath.Validators.Add(result =>
        {
            if (TokenOf(result) is { } given && !File.Exists(given) && !Directory.Exists(given))
                result.AddError(given.Length > 0 ? $"Nothing found at {given}" : "Nothing found at an empty path");
        });
        root.Add(lonePath);
        root.SetAction(parsed => parsed.GetValue(lonePath) is { } given
            ? commands.TranscribeAsync(new TranscribeRequest(given, FromCommandLine: true))
            : menu.RunAsync());
        examples[root] = RootExamples;

        root.Add(RecordCommand("record", transcribe: true, optionNames, examples));
        root.Add(RecordCommand("record-only", transcribe: false, optionNames, examples));
        root.Add(TranscribeCommand(optionNames, examples));
        root.Add(RebuildCommand(examples));
        root.Add(ListCommand(optionNames, examples));
        root.Add(DevicesCommand(examples));
        root.Add(AttachCommand(examples));

        var help = new HelpWithExamples(examples);
        var helpOption = root.Options.OfType<HelpOption>().Single();
        helpOption.Action = help;

        foreach (var option in root.Subcommands.SelectMany(command => command.Options).Append(helpOption))
        {
            optionNames.Add(option.Name);
            optionNames.UnionWith(option.Aliases);
        }

        var commandNames = new HashSet<string>(root.Subcommands.Select(command => command.Name), StringComparer.OrdinalIgnoreCase);
        return new BuiltTree(root, lonePath, help, commandNames, optionNames);
    }

    /// <summary>
    /// record, which records and then transcribes, or record-only, which records only: a name, one source, --output, and
    /// the options that shape the run. Every missing piece and every value that cannot be used is named in one run.
    /// </summary>
    private Command RecordCommand(string word, bool transcribe, HashSet<string> optionNames, Dictionary<Command, string> examples)
    {
        var symbols = new RecordSymbols(
            new Argument<string>("name") { Description = "The recording's name, such as Kalam. Its folder and files are named after it." },
            new Option<string>("--device")
            {
                HelpName = "part of its name",
                Description = "Record that output device and no other, found by part of its name, such as \"Pixel Buds\". --device auto records the device the class plays on."
            },
            new Option<string>("--app")
            {
                HelpName = "process",
                Description = "Record one app's sound, whatever device it plays on, by the app's process name, such as chrome."
            },
            new Option<string>("--output")
            {
                HelpName = "folder",
                Required = true,
                Description = "The folder the recording goes to, such as \"D:\\Classes\": the recording gets a folder of its name inside it."
            },
            new Option<double?>("--minutes")
            {
                HelpName = "n",
                Description = "Stop by itself after this many minutes, such as 90 or 1.5, counted from the start with pauses included. Q twice still stops it earlier.",
                CustomParser = ParseMinutes
            },
            new Option<bool>("--no-open") { Description = "Open nothing at the end." },
            new Option<bool>("--continue")
            {
                Description = "Continue, as its next part, the recording of this name that stopped recently (Recording:ContinueWithinMinutes) without a transcript. Without one, a new recording starts."
            },
            new Option<bool>("--new") { Description = "Start a new recording, without a word about one that could be continued." });

        var command = new Command(word, transcribe
            ? "Record now under that name, then transcribe the recording."
            : "Record now under that name; transcribe it later with transcribe, or from the menu.")
        {
            symbols.Name, symbols.Device, symbols.App, symbols.Output, symbols.Minutes, symbols.NoOpen, symbols.Continue, symbols.New
        };
        command.Validators.Add(result =>
        {
            foreach (var problem in RecordProblems(word, result, symbols, optionNames)) result.AddError(problem);
        });
        command.SetAction(parsed => RecordAsync(parsed, symbols, transcribe));
        examples[command] = RecordExamples(word);
        return command;
    }

    /// <summary>
    /// What keeps a record or record-only command line from running, one sentence each, in the order its pieces go: the
    /// name, the source, --output, --minutes, then --continue with --new.
    /// </summary>
    private static IEnumerable<string> RecordProblems(string word, CommandResult result, RecordSymbols symbols, HashSet<string> optionNames)
    {
        var name = TokenOf(result.GetResult(symbols.Name));
        if (string.IsNullOrWhiteSpace(name))
            yield return $"{word} needs a name, right after the word {word}: ClassScribe {word} \"Kalam\" ...{NameTakenBy(result)}";
        else if (ClassFolders.SafeName(name).Length == 0)
            yield return $"{word} cannot use \"{name}\" as a name: nothing is left of it once the characters Windows does not allow in a folder's name are taken out";

        var device = result.GetResult(symbols.Device);
        var app = result.GetResult(symbols.App);
        if (device is null && app is null) yield return $"{word} needs a source: --device \"<part of its name>\", --device auto or --app <process>";
        if (device is not null && app is not null) yield return $"{word} takes one source; you gave --device and --app";
        if (device is not null && ValueProblem(TokenOf(device), DeviceNeedsValue, optionNames) is { } deviceProblem) yield return deviceProblem;
        if (app is not null && ValueProblem(TokenOf(app) is { } process ? ProcessName(process) : null, AppNeedsValue, optionNames) is { } appProblem) yield return appProblem;

        if (result.GetResult(symbols.Output) is not { } output) yield return $"{word} needs --output \"<folder>\", the folder the recording goes to";
        else if (FolderProblem(TokenOf(output), optionNames) is { } outputProblem) yield return outputProblem;

        if (result.GetResult(symbols.Minutes) is { } minutes && ReadMinutes(TokenOf(minutes), out _) is { } minutesProblem) yield return minutesProblem;

        if (result.GetValue(symbols.Continue) && result.GetValue(symbols.New)) yield return "--continue and --new cannot go together";
    }

    /// <summary>
    /// When an option whose value may be left out took the word meant as the recording's name, says so, after a space, in
    /// brackets: record --new True gives (--new took "True" as its value). Empty otherwise.
    /// </summary>
    private static string NameTakenBy(CommandResult result)
    {
        var taken = result.Command.Options
            .Where(option => option.Arity is { MinimumNumberOfValues: 0, MaximumNumberOfValues: > 0 })
            .Select(option => (option.Name, Token: TokenOf(result.GetResult(option))))
            .Where(taker => taker.Token is not null)
            .Select(taker => $"{taker.Name} took \"{taker.Token}\" as its value")
            .ToList();
        return taken.Count > 0 ? $" ({string.Join("; ", taken)})" : "";
    }

    /// <summary>Starts the recording a record or record-only command line describes, as a request from the command line.</summary>
    private Task<int> RecordAsync(ParseResult parsed, RecordSymbols symbols, bool transcribe)
    {
        ClassSource source = parsed.GetValue(symbols.App) is { } app
            ? new ClassSource.App(ProcessName(app))
            : parsed.GetValue(symbols.Device) is { } device && !device.Equals(Automatic, StringComparison.OrdinalIgnoreCase)
                ? new ClassSource.Device(device)
                : new ClassSource.Automatic();
        var continueChoice = parsed.GetValue(symbols.Continue) ? ContinueChoice.Continue
            : parsed.GetValue(symbols.New) ? ContinueChoice.New
            : ContinueChoice.Ask;

        var request = new RecordRequest(
            RunIds.New(time.GetUtcNow()),
            ClassFolders.SafeName(parsed.GetValue(symbols.Name)!),
            transcribe,
            new SourcePlan(RecordingMode.Class, source, Microphone: null),
            Path.GetFullPath(parsed.GetValue(symbols.Output)!),
            continueChoice,
            ContinueBaseName: null,
            parsed.GetValue(symbols.Minutes),
            Open: !parsed.GetValue(symbols.NoOpen));
        return commands.RecordAsync(request, StartOrigin.CommandLine);
    }

    /// <summary>
    /// transcribe: a recording, by its folder or one of its files, or any audio or video file, with --output, --no-open and
    /// --again. --output is passed on as a full path, for the transcript of a file that is not a recording's; a recording's
    /// transcript stays beside the recording, and the transcribe command says that --output was not used.
    /// </summary>
    private Command TranscribeCommand(HashSet<string> optionNames, Dictionary<Command, string> examples)
    {
        var path = new Argument<string>("path") { Description = "A recording's folder (its newest recording is meant), one of a recording's files, or any audio or video file." };
        var outputFolder = new Option<string>("--output")
        {
            HelpName = "folder",
            Description = "The folder for the transcript of a file that is not a recording's, such as \"D:\\Classes\": it gets a folder of its name inside it. A recording's transcript goes beside the recording, and this is not used."
        };
        var noOpen = new Option<bool>("--no-open") { Description = "Open nothing at the end." };
        var again = new Option<bool>("--again") { Description = "Transcribe a recording that already has a transcript. This is billed again; rebuild lays it out again for free." };

        var command = new Command("transcribe", "Transcribe a recording, named by its folder or one of its files, or any audio or video file.") { path, outputFolder, noOpen, again };
        command.Validators.Add(result =>
        {
            if (string.IsNullOrWhiteSpace(TokenOf(result.GetResult(path))))
                result.AddError("transcribe needs a path: a recording's folder, one of its files, or any audio or video file, such as ClassScribe transcribe \"D:\\Classes\\Kalam\"");
            if (result.GetResult(outputFolder) is { } given && FolderProblem(TokenOf(given), optionNames) is { } problem) result.AddError(problem);
        });
        command.SetAction(parsed => commands.TranscribeAsync(new TranscribeRequest(
            parsed.GetValue(path)!,
            parsed.GetValue(outputFolder) is { } folder ? Path.GetFullPath(folder) : null,
            Open: !parsed.GetValue(noOpen),
            Again: parsed.GetValue(again),
            FromCommandLine: true)));
        examples[command] = TranscribeExamples;
        return command;
    }

    /// <summary>rebuild: a recording, by its folder or one of its files, or a saved answer, with --no-open.</summary>
    private Command RebuildCommand(Dictionary<Command, string> examples)
    {
        var path = new Argument<string>("path") { Description = "A recording's folder, one of its files, or an answer from ElevenLabs saved as .json." };
        var noOpen = new Option<bool>("--no-open") { Description = "Open nothing at the end." };

        var command = new Command("rebuild", "Make a recording's transcript again, for free, from ElevenLabs' saved answer, with the marks and Transcript settings as they are now.") { path, noOpen };
        command.Validators.Add(result =>
        {
            if (string.IsNullOrWhiteSpace(TokenOf(result.GetResult(path))))
                result.AddError("rebuild needs a path: a recording's folder, one of its files, or an answer from ElevenLabs saved as .json, such as ClassScribe rebuild \"D:\\Classes\\Kalam\"");
        });
        command.SetAction(parsed => commands.RebuildAsync(parsed.GetValue(path)!, open: !parsed.GetValue(noOpen)));
        examples[command] = RebuildExamples;
        return command;
    }

    /// <summary>list: the recordings under the folder --output names, or under Recording:OutputFolder.</summary>
    private Command ListCommand(HashSet<string> optionNames, Dictionary<Command, string> examples)
    {
        var outputFolder = new Option<string>("--output") { HelpName = "folder", Description = "The recordings folder to list, instead of the one Recording:OutputFolder names." };

        var command = new Command("list", "List the recordings, newest first, with the ones being recorded or transcribed now.") { outputFolder };
        command.Validators.Add(result =>
        {
            if (result.GetResult(outputFolder) is { } given && FolderProblem(TokenOf(given), optionNames) is { } problem) result.AddError(problem);
        });
        command.SetAction(parsed => commands.ListAsync(parsed.GetValue(outputFolder) is { } folder ? Path.GetFullPath(folder) : null));
        examples[command] = ListExamples;
        return command;
    }

    /// <summary>devices: the output devices and the apps with sound.</summary>
    private Command DevicesCommand(Dictionary<Command, string> examples)
    {
        var command = new Command("devices", "List the output devices and the apps with sound, each with the option that records it.");
        command.SetAction(_ => commands.DevicesAsync());
        examples[command] = DevicesExamples;
        return command;
    }

    /// <summary>
    /// The hidden attach: shows the recording in progress, or, with --alert, the run named, in a window the recorder opened
    /// by itself. A run id names a file, a pipe and an event, so one that is not well formed is refused.
    /// </summary>
    private Command AttachCommand(Dictionary<Command, string> examples)
    {
        var alert = new Option<string>(ProcessArguments.Alert)
        {
            HelpName = "run id",
            Description = "The run to show, in a window the recorder opened by itself: keys do nothing there until Enter."
        };

        var command = new Command(AttachName, "Show the recording in progress in this window.") { alert };
        command.Hidden = true;
        command.Validators.Add(result =>
        {
            if (result.GetResult(alert) is { } given && !RunIds.IsWellFormed(TokenOf(given)))
                result.AddError("--alert takes a run id, such as 20261015-230002Z-1f3a9c07");
        });
        command.SetAction(parsed => parsed.GetValue(alert) is { } runId ? commands.AttachAsync(runId, alert: true) : commands.AttachAsync(null, alert: false));
        examples[command] = AttachExamples;
        return command;
    }

    /// <summary>Writes the help of the command <paramref name="parsed"/> stopped at, with its examples, to standard output.</summary>
    private void WriteHelp(ParseResult parsed)
    {
        parsed.InvocationConfiguration.Output = Invocation.Output;
        Tree.Help.Invoke(parsed);
    }

    /// <summary>The value given to an option or an argument: its first word, or null when it was given none.</summary>
    private static string? TokenOf(SymbolResult? result) => result is { Tokens: [var first, ..] } ? first.Value : null;

    /// <summary>
    /// What is wrong with <paramref name="value"/>, the value of an option that needs one: none at all, or an option it
    /// took instead, because the option it belongs to was left without its own. Null when there is a value.
    /// </summary>
    private static string? ValueProblem(string? value, string need, HashSet<string> optionNames) =>
        string.IsNullOrWhiteSpace(value) ? need
        : optionNames.Contains(value) ? $"{need}; it took the option {value} instead"
        : null;

    /// <summary>
    /// What is wrong with <paramref name="value"/> as the folder --output names: no value, a character Windows does not allow
    /// in a path, or a file where the folder should be. Null for a folder that can be used, or made.
    /// </summary>
    private static string? FolderProblem(string? value, HashSet<string> optionNames)
    {
        if (ValueProblem(value, OutputNeedsValue, optionNames) is { } missing) return missing;
        if (value!.IndexOfAny(NotInPaths) >= 0 || value.Any(char.IsControl))
            return $"--output takes a folder, such as \"D:\\Classes\"; \"{value}\" has a character Windows does not allow in a path: < > \" | ? *";

        var folder = Path.GetFullPath(value);
        return File.Exists(folder) ? $"--output names a file, not a folder: {folder}" : null;
    }

    /// <summary>An app's process name as Windows gives it: chrome.exe, as Task Manager shows it, is chrome.</summary>
    private static string ProcessName(string app) =>
        app.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? app[..^".exe".Length] : app;

    /// <summary>Reads --minutes with <see cref="ReadMinutes"/>; a value it cannot take is a mistake of the command line.</summary>
    private static double? ParseMinutes(ArgumentResult result)
    {
        if (ReadMinutes(TokenOf(result), out var minutes) is not { } problem) return minutes;

        result.AddError(problem);
        return null;
    }

    /// <summary>
    /// Reads a number of minutes: digits with at most one dot and an optional sign, the same whatever the computer's
    /// language, more than 0 and at most a day. Returns what is wrong with <paramref name="text"/>, or null when
    /// <paramref name="minutes"/> holds the number.
    /// </summary>
    private static string? ReadMinutes(string? text, out double minutes)
    {
        minutes = 0;
        if (text is null || !MinutesShape().IsMatch(text)) return MinutesFormat;

        minutes = double.Parse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);
        return minutes is > 0 and <= MostMinutes ? null : MinutesRange;
    }

    /// <summary>
    /// The shape of a number of minutes: an optional sign, then digits with at most one dot, at least one digit among them.
    /// Digits are 0 to 9 only, and \z ends the match at the very end.
    /// </summary>
    [GeneratedRegex(@"^[+-]?(?:[0-9]+\.?[0-9]*|\.[0-9]+)\z", RegexOptions.CultureInvariant)]
    private static partial Regex MinutesShape();

    /// <summary>
    /// An argument without the quotes and spaces around it. When a quote is left inside it and an option follows that quote,
    /// a stray quote joined what follows to a path (see <see cref="Tidy"/>): the argument is split at the quote, and the
    /// words after it become arguments of their own. Any other quote inside, such as in a recording's name, stays.
    /// </summary>
    private static string[] SplitOffJoinedOptions(string argument, HashSet<string> optionNames)
    {
        var tidy = argument.Trim().Trim('"').Trim();
        var quote = tidy.IndexOf('"');
        if (quote < 0) return [tidy];

        var after = tidy[(quote + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return after.Any(optionNames.Contains) ? [tidy[..quote].TrimEnd(), .. after] : [tidy];
    }

    /// <summary>The examples and notes under the help of the app as a whole.</summary>
    private const string RootExamples = """
        Examples:
          ClassScribe record "Kalam" --device auto --output "D:\Classes"
          ClassScribe record-only "Kalam" --app chrome --output "D:\Classes" --minutes 90
          ClassScribe transcribe "D:\Classes\Kalam"
          ClassScribe "D:\Lectures\Lecture 5.mp4"

        Without a command, ClassScribe shows its menu. A path alone is transcribed, as when a file is dropped on ClassScribe.exe.
        Add --help after a command for its options, such as: ClassScribe record --help
        While recording, Q twice or Ctrl+C twice stops, P pauses or resumes, and M marks a moment.
        The API key is read from the ELEVENLABS_API_KEY environment variable.
        Exit codes: 0 done; 1 failed or refused; 2 a mistake on the command line; 130 cancelled with Ctrl+C.
        """;

    /// <summary>The examples and notes under the help of record or record-only, named by <paramref name="word"/>.</summary>
    private static string RecordExamples(string word) => $"""
        Examples:
          ClassScribe {word} "Kalam" --device auto --output "D:\Classes"
          ClassScribe {word} "Kalam" --device "Pixel Buds" --output "D:\Classes" --minutes 90
          ClassScribe {word} "Kalam" --app chrome --output "D:\Classes" --continue

        The name, one source (--device or --app) and --output are needed, and nothing is asked.
        While recording, Q twice or Ctrl+C twice stops, P pauses or resumes, and M marks a moment; Esc leaves the view of a recording in the background, which carries on.
        """;

    /// <summary>The examples and notes under the help of transcribe.</summary>
    private const string TranscribeExamples = """
        Examples:
          ClassScribe transcribe "D:\Classes\Kalam"
          ClassScribe transcribe "D:\Classes\Kalam\Kalam_2026-10-15_23-00-02Z.wav" --again
          ClassScribe "D:\Lectures\Lecture 5.mp4"

        A folder means the recording started last in it. A path alone does the same as transcribe, and so does dropping a file on ClassScribe.exe.
        """;

    /// <summary>The examples and notes under the help of rebuild.</summary>
    private const string RebuildExamples = """
        Examples:
          ClassScribe rebuild "D:\Classes\Kalam"
          ClassScribe rebuild "D:\Classes\Kalam\Kalam_2026-10-15_23-00-02Z.json" --no-open

        Nothing is uploaded and nothing is billed; the new transcript is saved beside the others, which are kept.
        """;

    /// <summary>The examples under the help of list.</summary>
    private const string ListExamples = """
        Examples:
          ClassScribe list
          ClassScribe list --output "D:\Classes"
        """;

    /// <summary>The example under the help of devices.</summary>
    private const string DevicesExamples = """
        Examples:
          ClassScribe devices
        """;

    /// <summary>The examples under the help of the hidden attach.</summary>
    private const string AttachExamples = """
        Examples:
          ClassScribe attach
          ClassScribe attach --alert 20261015-230002Z-1f3a9c07
        """;

    /// <summary>The symbols of record and record-only, which their check and their action share.</summary>
    /// <param name="Name">The recording's name.</param>
    /// <param name="Device">--device: part of a device's name, or auto.</param>
    /// <param name="App">--app: an app's process name.</param>
    /// <param name="Output">--output: the recordings folder for this run.</param>
    /// <param name="Minutes">--minutes: stop by itself after this many minutes.</param>
    /// <param name="NoOpen">--no-open: open nothing at the end.</param>
    /// <param name="Continue">--continue: continue a recent recording of this name.</param>
    /// <param name="New">--new: start a new recording without a word.</param>
    private sealed record RecordSymbols(Argument<string> Name, Option<string> Device, Option<string> App, Option<string> Output,
        Option<double?> Minutes, Option<bool> NoOpen, Option<bool> Continue, Option<bool> New);

    /// <summary>The tree as built, with what tidying, parsing and the kind of a command line need from it.</summary>
    /// <param name="Root">The root command.</param>
    /// <param name="LonePath">The root's hidden argument: a path alone on the command line.</param>
    /// <param name="Help">The help, with examples, of every command.</param>
    /// <param name="CommandNames">The commands' names, in any case.</param>
    /// <param name="OptionNames">Every option's names and aliases, in any case.</param>
    private sealed record BuiltTree(RootCommand Root, Argument<string?> LonePath, HelpWithExamples Help, HashSet<string> CommandNames, HashSet<string> OptionNames);

    /// <summary>
    /// The help of a command: System.CommandLine's, which lists its arguments and options, then ClassScribe's examples and
    /// notes. Like System.CommandLine's own help, it clears the command line's mistakes, so that help is shown, with exit
    /// code 0, however incomplete the rest is.
    /// </summary>
    private sealed class HelpWithExamples(IReadOnlyDictionary<Command, string> examples) : SynchronousCommandLineAction
    {
        /// <inheritdoc />
        public override bool ClearsParseErrors => true;

        /// <summary>
        /// Writes the help of the command <paramref name="parseResult"/> names, then its examples. System.CommandLine writes
        /// nothing at all for a hidden command, such as attach, so the command is shown while its own help is written: it
        /// stays out of the list of commands in the help of the app as a whole.
        /// </summary>
        public override int Invoke(ParseResult parseResult)
        {
            var command = parseResult.CommandResult.Command;
            var hidden = command.Hidden;
            command.Hidden = false;
            try
            {
                new HelpAction().Invoke(parseResult);
            }
            finally
            {
                command.Hidden = hidden;
            }

            if (examples.TryGetValue(command, out var text)) parseResult.InvocationConfiguration.Output.WriteLine(text);
            return 0;
        }
    }
}
