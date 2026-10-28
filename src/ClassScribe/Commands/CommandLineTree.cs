using System.CommandLine;
using System.CommandLine.Help;
using System.CommandLine.Invocation;
using System.CommandLine.Parsing;
using System.Globalization;
using System.Text.RegularExpressions;
using ClassScribe.Audio;
using ClassScribe.Audio.Microphones;
using ClassScribe.Camera;
using ClassScribe.Desktop;
using ClassScribe.Runs;
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

    /// <summary>The version, which needs no settings either.</summary>
    Version,

    /// <summary>The menu, which reads the settings before each choice and shows the report at the start itself.</summary>
    Menu,

    /// <summary>Showing a recording in progress, which the settings never hold up.</summary>
    Attach,

    /// <summary>
    /// settings and class, commands of their own kind: neither the gate nor the report at the start runs before them, since
    /// they must be able to fix a file the gate refuses; the settings writer checks each change itself.
    /// </summary>
    Settings,

    /// <summary>
    /// record and record-only: the settings are read and checked first, keeping every value that passes and dropping the
    /// rest, since a refused settings file never stops a class (build-plan amendment 4); then the report at the start.
    /// </summary>
    Recording,

    /// <summary>Any other command: the settings are read and checked first, then the report at the start is printed.</summary>
    Command
}

/// <summary>
/// ClassScribe's command line, on System.CommandLine, behind ClassScribe's own way of reporting mistakes:
/// <code>
/// ClassScribe                                     the menu
/// ClassScribe &lt;path&gt;                              as transcribe &lt;path&gt;; a file dropped on the exe
/// ClassScribe record "&lt;name&gt;" SOURCE [--output "&lt;folder&gt;"] [--mic ["&lt;microphone&gt;"] [--mic-follows &lt;app&gt;|auto|none]]
///                                                     [--camera ["&lt;camera&gt;"] [--resolution &lt;size&gt;]]
///                                                     [--minutes &lt;n&gt;] [--no-open] [--continue | --new]
/// ClassScribe record --class "&lt;saved class&gt;" ["&lt;name&gt;"] [the options above, over the class's own, in groups]
/// ClassScribe record-only "&lt;name&gt;" ...          the same options
///     SOURCE is --device "&lt;part of its name&gt;", --device auto, --app &lt;process&gt; or --in-person ["&lt;microphone&gt;"]
///     --mic adds your microphone to --device or --app; --in-person records it alone; --camera, only with --in-person,
///     records a lesson's picture too, beside the sound (refused before anything records only by a camera set-up problem);
///     without --output, Recording:OutputFolder, or the saved class's own folder
/// ClassScribe settings path | list [--json] | get &lt;setting&gt; [--json] | set &lt;setting&gt; &lt;value&gt;... | reset (&lt;setting&gt; | --all) | open | fill
/// ClassScribe class list [--json] | show "&lt;name&gt;" [--json] | add "&lt;name&gt;" ... | set "&lt;name&gt;" ... | rename "&lt;name&gt;" "&lt;new&gt;" | remove "&lt;name&gt;"
/// ClassScribe transcribe &lt;path&gt; [--output "&lt;folder&gt;"] [--no-open] [--again] [--keyterms "&lt;glossary&gt;" | --keyterms none]
/// ClassScribe rebuild &lt;path&gt; [--no-open]
/// ClassScribe finish-video &lt;path&gt; [--no-open]       a lesson's video made again, free, from its pieces and its sound
/// ClassScribe list [--output "&lt;folder&gt;"]
/// ClassScribe devices
/// ClassScribe mic-check [--seconds &lt;n&gt;] [--log-folder "&lt;folder&gt;"]
/// ClassScribe camera-check ["&lt;camera&gt;"] [--resolution &lt;size&gt;]    opens the camera, shared, only after a typed Y
/// ClassScribe drive signin [--client "&lt;downloaded client file&gt;"]    the first time needs --client
/// ClassScribe drive signout
/// ClassScribe drive status
/// ClassScribe drive upload ["&lt;recording file, a folder of recordings&gt;"]    without a path: what waits to go up
/// ClassScribe drive free-space "&lt;recording file or folder&gt;" --yes      deletes local audio once Drive has it, checked
/// ClassScribe youtube &lt;link&gt;... [--list "&lt;file&gt;"] --name "&lt;course&gt;" --output "&lt;folder&gt;" [--download] (--yes | --plan)
///                                                     [--no-transcribe] [--no-open] [--again] [--allow-unlisted]
/// ClassScribe youtube check                         yt-dlp and its helpers, their versions, what to install
/// ClassScribe &lt;command&gt; --help                    also -h, -?, /? and help &lt;command&gt;
/// ClassScribe --version                           the version and the commit, as the build was stamped
/// hidden: ClassScribe attach [--alert &lt;run id&gt;]
/// </code>
/// A mistake is never asked about: every one is written to standard error, all of them in one run, then that command's
/// help to standard output, and the exit code is 2 (System.CommandLine's own would be 1). For record and record-only one
/// validator names each missing piece, because once a validator has found a mistake, System.CommandLine reports neither
/// its own missing pieces nor the values it cannot read. --minutes is read with a dot and the invariant culture, so 1,5
/// is refused rather than read as 15. --mic and --in-person may take a microphone's name, and --camera a camera's, so a
/// recording's name right after them is taken as the microphone's or the camera's, which the message points out. An
/// argument that starts with @ stays a path, and Ctrl+C stays with ClassScribe. <paramref name="classes"/>, the saved
/// classes, and <paramref name="recording"/>, the recording settings, are read as a command runs: record --class starts a
/// saved class, a saved class's name without a source is pointed at --class (N5), and a run without --output records into
/// Recording:OutputFolder (N6); without them, as in tests that do not need them, there are no saved classes and the
/// recordings folder is the default.
/// </summary>
public sealed partial class CommandLineTree(ICommandRunner commands, MenuCommand menu, IConsoleUi ui, TimeProvider time, TextWriter output, TextWriter errors,
    Settings.SavedClasses? classes = null, Microsoft.Extensions.Options.IOptionsMonitor<Settings.RecordingOptions>? recording = null)
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

    /// <summary>What --keyterms says without a value.</summary>
    private const string KeytermsNeedsValue = "--keyterms needs a glossary file after it, or none, such as --keyterms \"%APPDATA%\\ClassScribe\\mantiq.txt\"";

    /// <summary>What --mic-follows says without a value, or with one that names no app, or ClassScribe itself.</summary>
    private const string FollowsNeedsValue = $"--mic-follows needs part of an app's name, such as chrome, or {FollowRule.AnyWord} or {FollowRule.NoneWord}";

    /// <summary>The name of ClassScribe's own process, which the microphone must never follow: its own recording holds the microphone for the whole class.</summary>
    private const string OwnProcessName = "ClassScribe";

    /// <summary>The most seconds --seconds takes: a day.</summary>
    private const int MostSeconds = 86400;

    /// <summary>What --seconds says about a value not written as a whole number.</summary>
    private const string SecondsFormat = "--seconds takes a whole number of seconds, such as 600";

    /// <summary>What --seconds says about a number outside its range.</summary>
    private const string SecondsRange = "--seconds takes from 1 to 86400 seconds (a day)";

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
        if (words is [var parent, var child, ..] && tree.SubcommandNames.TryGetValue(parent, out var children) && children.Contains(child)) words[1] = child.ToLowerInvariant();

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

    /// <summary>
    /// What <paramref name="parsed"/> asks for: help, the version, the menu, showing a recording in progress, a command of
    /// its own kind (settings or class, or one of their commands), or any other command.
    /// </summary>
    public CommandKind KindOf(ParseResult parsed)
    {
        var tree = Tree;
        if (parsed.Action is HelpWithExamples) return CommandKind.Help;
        if (parsed.Action is VersionLine) return CommandKind.Version;
        if (parsed.CommandResult.Command == tree.Root) return parsed.GetValue(tree.LonePath) is null ? CommandKind.Menu : CommandKind.Command;

        for (SymbolResult? result = parsed.CommandResult; result is CommandResult command; result = command.Parent)
        {
            if (OwnKindWords.Contains(command.Command.Name)) return CommandKind.Settings;
        }

        return parsed.CommandResult.Command.Name switch
        {
            AttachName => CommandKind.Attach,
            "record" or "record-only" => CommandKind.Recording,
            _ => CommandKind.Command
        };
    }

    /// <summary>Runs what <paramref name="parsed"/> names (see <see cref="Invocation"/>) and returns its exit code.</summary>
    public Task<int> InvokeAsync(ParseResult parsed) => parsed.InvokeAsync(Invocation);

    /// <summary>Builds the tree, with the names of its commands and options, which tidying and the checks of values need.</summary>
    private BuiltTree BuildTree()
    {
        var optionNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var examples = new Dictionary<Command, string>();

        var root = new RootCommand($"ClassScribe {BuildInfo.Describe()} records the sound your computer plays during a class and gets a transcript from ElevenLabs.");
        root.Directives.Clear();
        var versionOption = root.Options.OfType<VersionOption>().Single();
        versionOption.Action = new VersionLine();

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
        root.Add(FinishVideoCommand(examples));
        root.Add(ListCommand(optionNames, examples));
        root.Add(DevicesCommand(examples));
        root.Add(MicCheckCommand(optionNames, examples));
        root.Add(CameraCheckCommand(optionNames, examples));
        root.Add(DriveCommand(optionNames, examples));
        root.Add(YouTubeCommand(optionNames, examples));
        root.Add(SettingsCommandTree(examples));
        root.Add(ClassCommandTree(optionNames, examples));
        root.Add(AttachCommand(examples));

        var help = new HelpWithExamples(examples);
        var helpOption = root.Options.OfType<HelpOption>().Single();
        helpOption.Action = help;

        var allCommands = root.Subcommands.SelectMany(command => command.Subcommands.Prepend(command)).ToList();
        foreach (var option in allCommands.SelectMany(command => command.Options).Append(helpOption).Append(versionOption))
        {
            optionNames.Add(option.Name);
            optionNames.UnionWith(option.Aliases);
        }

        var commandNames = new HashSet<string>(root.Subcommands.Select(command => command.Name), StringComparer.OrdinalIgnoreCase);
        var subcommandNames = root.Subcommands
            .Where(command => command.Subcommands.Count > 0)
            .ToDictionary(command => command.Name, command => new HashSet<string>(command.Subcommands.Select(child => child.Name), StringComparer.OrdinalIgnoreCase),
                StringComparer.OrdinalIgnoreCase);
        return new BuiltTree(root, lonePath, help, commandNames, subcommandNames, optionNames);
    }

    /// <summary>
    /// drive: signin, with --client the first time, the client file downloaded from Google, passed on as a full path;
    /// signout; status; upload, with a recording's file or a folder of recordings, or nothing for the queue; and free-space,
    /// with a recording's file or a folder, and --yes, without which it is a mistake. drive alone is a mistake that names
    /// them all.
    /// </summary>
    private Command DriveCommand(HashSet<string> optionNames, Dictionary<Command, string> examples)
    {
        var client = new Option<string>("--client")
        {
            HelpName = "file",
            Description = "The client file you downloaded from Google (Google Auth platform > Clients > Download JSON), such as \"$env:USERPROFILE\\Downloads\\client_secret_....json\". Needed the first time; ClassScribe keeps the client, encrypted for your Windows account, and the file can then be deleted."
        };
        var signIn = new Command("signin", "Sign in to Google Drive in your browser. The first time, --client names the client file you downloaded from Google.") { client };
        signIn.Validators.Add(result =>
        {
            if (result.GetResult(client) is { } given && ClientFileProblem(TokenOf(given), optionNames) is { } problem) result.AddError(problem);
        });
        signIn.SetAction(parsed => commands.DriveSignInAsync(parsed.GetValue(client) is { } file ? Path.GetFullPath(file) : null));
        examples[signIn] = DriveSignInExamples;

        var signOut = new Command("signout", "Sign out of Google Drive: Google is asked to end ClassScribe's access, and the sign-in is forgotten. The client is kept, and nothing on Drive is touched.");
        signOut.SetAction(_ => commands.DriveSignOutAsync());
        examples[signOut] = DriveSignOutExamples;

        var status = new Command("status", "Show the Google account ClassScribe is signed in as, since when, the storage used and ClassScribe's folder on Drive.");
        status.SetAction(_ => commands.DriveStatusAsync());
        examples[status] = DriveStatusExamples;

        var uploadPath = new Argument<string?>("path")
        {
            Arity = ArgumentArity.ZeroOrOne,
            Description = "A recording's file, a course's or a class's folder, or the recordings folder: each recording in it goes up. Without one, every recording waiting to go up."
        };
        var upload = new Command("upload", "Upload recordings to Google Drive: the audio, the transcripts, ElevenLabs' answers, the marks and the log, each checked against Drive's MD5.") { uploadPath };
        upload.Validators.Add(result =>
        {
            if (TokenOf(result.GetResult(uploadPath)) is { } given && PathProblem(given) is { } problem) result.AddError(problem);
        });
        upload.SetAction(parsed => commands.DriveUploadAsync(parsed.GetValue(uploadPath) is { } given ? Path.GetFullPath(given) : null));
        examples[upload] = DriveUploadExamples;

        var freePath = new Argument<string>("path") { Description = "A recording's file, a course's or a class's folder, or the recordings folder: the audio of each recording in it is deleted from this laptop once Google Drive has it." };
        var yes = new Option<bool>("--yes") { Description = "Go ahead: delete the audio files from this laptop. Needed, as nothing is asked." };
        var freeSpace = new Command("free-space", "Free room on this laptop: delete recordings' .wav files once each one's copy on Google Drive is checked again. Transcripts, answers, marks and logs stay, and nothing on Drive is touched.") { freePath, yes };
        freeSpace.Validators.Add(result =>
        {
            var given = TokenOf(result.GetResult(freePath));
            if (string.IsNullOrWhiteSpace(given))
                result.AddError("drive free-space needs a path: a recording's file or a folder of recordings, such as ClassScribe drive free-space \"D:\\Classes\\Kalam\" --yes");
            else if (PathProblem(given) is { } problem)
                result.AddError(problem);
            if (!result.GetValue(yes))
                result.AddError("drive free-space deletes audio from this laptop once Google Drive has a checked copy of it; add --yes to go ahead, such as ClassScribe drive free-space \"D:\\Classes\\Kalam\" --yes");
        });
        freeSpace.SetAction(parsed => commands.DriveFreeSpaceAsync(Path.GetFullPath(parsed.GetValue(freePath)!)));
        examples[freeSpace] = DriveFreeSpaceExamples;

        var drive = new Command("drive", "Google Drive: upload recordings, free room on this laptop, sign in, sign out, or see the sign-in.") { signIn, signOut, status, upload, freeSpace };
        drive.Validators.Add(result =>
        {
            if (!result.Children.OfType<CommandResult>().Any())
                result.AddError("drive needs one of signin, signout, status, upload or free-space, such as ClassScribe drive status");
        });

        // Never run: the validator above refuses drive alone. A command without an action of its own would also get
        // System.CommandLine's "Required command was not provided." beside ClassScribe's words.
        drive.SetAction(_ => Mistake);
        examples[drive] = DriveExamples;
        return drive;
    }

    /// <summary>
    /// youtube: links, a --list file of links, or both, joined in order; --name, the course; --output, the recordings folder,
    /// Recording:OutputFolder when it is not given (N6); without --download, each video recorded as it plays in a Chrome window of ClassScribe's own, with --download its audio
    /// downloaded with yt-dlp; exactly one of --yes and --plan; and --no-transcribe, --no-open, --again and --allow-unlisted,
    /// which is for --download alone. Each word that is no link the batch takes is named by where it stands, a line of the file
    /// by its number; a playlist's or a live stream's link, and more than 10 videos, need --download. youtube check takes
    /// nothing else.
    /// </summary>
    private Command YouTubeCommand(HashSet<string> optionNames, Dictionary<Command, string> examples)
    {
        var links = new Argument<string[]>("links")
        {
            Arity = ArgumentArity.ZeroOrMore, Description = "One or more YouTube links, separated by spaces: videos, or with --download playlists too."
        };
        var list = new Option<string>("--list") { HelpName = "file", Description = "A text file of links: several on a line, separated by spaces; # starts a comment." };
        var name = new Option<string>("--name") { HelpName = "course", Description = "The course the videos belong to, such as Risala: the folder they go to, as a class's name names it." };
        var output = new Option<string>("--output") { HelpName = "folder", Description = "The folder the recordings go to, such as \"D:\\Classes\": the course gets a folder inside it. Without it, Recording:OutputFolder." };
        var download = new Option<bool>("--download")
        {
            Description = "Download the audio with yt-dlp instead of recording it as it plays: only for videos you uploaded or whose owner allows it, never your course's."
        };
        var yes = new Option<bool>("--yes")
        {
            Description = "Go ahead after the plan is printed. Recording as they play, you are still asked for Enter before each video, and play it yourself."
        };
        var plan = new Option<bool>("--plan") { Description = "Print the plan and stop: nothing is fetched, recorded or sent." };
        var noTranscribe = new Option<bool>("--no-transcribe") { Description = "Only fetch the audio; the summary gives each recording's transcribe command." };
        var noOpen = new Option<bool>("--no-open") { Description = "Open nothing at the end; otherwise the course's folder opens once." };
        var again = new Option<bool>("--again") { Description = "Fetch videos fetched before as new recordings; transcribing them is billed again." };
        var allowUnlisted = new Option<bool>("--allow-unlisted")
        {
            Description = "Download every unlisted video of the batch on your word, given once here, that you uploaded it or its owner allows downloading. Needs YouTube:CourseChannels."
        };

        var check = new Command("check", "Show where yt-dlp, Deno, FFmpeg and FFprobe are, their versions, and what to install or update. Asks nothing of YouTube.");
        check.SetAction(_ => commands.YouTubeCheckAsync());
        examples[check] = YouTubeCheckExamples;

        var command = new Command("youtube",
            "Record YouTube videos as they play, in a Chrome window of ClassScribe's own, or download their audio with yt-dlp (--download), each into a recording of the course, then transcribe them.")
        {
            links, list, name, output, download, yes, plan, noTranscribe, noOpen, again, allowUnlisted, check
        };
        command.Validators.Add(result =>
        {
            if (result.Children.OfType<CommandResult>().Any()) return;
            foreach (var problem in YouTubeProblems(result, links, list, name, output, download, yes, plan, allowUnlisted, optionNames)) result.AddError(problem);
        });
        command.SetAction(parsed =>
        {
            var words = Words(parsed.GetValue(links) ?? [], parsed.GetValue(list));
            var reading = YouTube.LinkList.Read(words);
            foreach (var note in reading.Notes) Invocation.Output.WriteLine(note);
            return commands.YouTubeAsync(new YouTube.YouTubeRequest(
                reading.Links,
                FolderNames.SafeName(parsed.GetValue(name)!),
                OutputRoot(parsed.GetValue(output), classFolder: null),
                parsed.GetValue(download),
                parsed.GetValue(plan) ? YouTube.BatchConfirmation.PlanOnly : YouTube.BatchConfirmation.Yes,
                Transcribe: !parsed.GetValue(noTranscribe),
                Open: !parsed.GetValue(noOpen),
                Again: parsed.GetValue(again),
                AllowUnlisted: parsed.GetValue(allowUnlisted)));
        });
        examples[command] = YouTubeExamples;
        return command;
    }

    /// <summary>
    /// What keeps a youtube command line from running, one sentence each: --name, --output when given, the links and the --list file,
    /// each word that is no link the batch takes, --yes and --plan, and more videos than a batch takes; and without
    /// --download, a playlist's or a live stream's link, which only a download's read can take, and --allow-unlisted.
    /// </summary>
    private static IEnumerable<string> YouTubeProblems(CommandResult result, Argument<string[]> links, Option<string> list, Option<string> name, Option<string> output,
        Option<bool> download, Option<bool> yes, Option<bool> plan, Option<bool> allowUnlisted, HashSet<string> optionNames)
    {
        var course = TokenOf(result.GetResult(name));
        if (result.GetResult(name) is null || string.IsNullOrWhiteSpace(course))
            yield return "youtube needs --name \"<course>\", the course the videos belong to, such as --name \"Risala\"";
        else if (optionNames.Contains(course))
            yield return $"--name needs the course's name after it; it took the option {course} instead";
        else if (FolderNames.SafeName(course).Length == 0)
            yield return $"youtube cannot use \"{course}\" as a course's name: nothing is left of it once the characters Windows does not allow in a folder's name are taken out";

        if (result.GetResult(output) is { } folder && FolderProblem(output.Name, TokenOf(folder), optionNames) is { } outputProblem) yield return outputProblem;

        var given = result.GetResult(links)?.Tokens.Select(token => token.Value).ToList() ?? [];
        string? listFile = null;
        if (result.GetResult(list) is { } listResult)
        {
            var value = TokenOf(listResult);
            if (ValueProblem(value, "--list needs a text file of links after it, such as --list \"risala-links.txt\"", optionNames) is { } missing) yield return missing;
            else if (value!.IndexOfAny(NotInPaths) >= 0 || value.Any(char.IsControl)) yield return $"--list takes a file; \"{value}\" has a character Windows does not allow in a path: < > \" | ? *";
            else if (!File.Exists(Path.GetFullPath(value))) yield return $"Nothing found at {Path.GetFullPath(value)}";
            else listFile = value;
        }

        if (given.Count == 0 && result.GetResult(list) is null) yield return "youtube needs at least one YouTube link, or --list \"<file>\" of links";

        IReadOnlyList<YouTube.LinkWord> words;
        string? unreadable = null;
        try
        {
            words = Words(given, listFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Deliberately not rethrown: a list file that cannot be read is a mistake of the command line, said with the others.
            words = Words(given, null);
            unreadable = $"--list cannot be read: {ex.Message}";
        }

        if (unreadable is not null) yield return unreadable;
        var reading = YouTube.LinkList.Read(words);
        foreach (var problem in reading.Problems) yield return problem;
        if (listFile is not null && given.Count == 0 && words.Count == 0) yield return $"{Path.GetFileName(listFile)} holds no link";

        var isYes = result.GetValue(yes);
        var isPlan = result.GetValue(plan);
        if (isYes == isPlan)
            yield return isYes ? "--yes and --plan cannot go together" : "youtube needs --yes to go ahead after the plan, or --plan to print the plan alone";

        var videos = reading.Links.Count(link => link.Kind != YouTube.YouTubeLinkKind.Playlist);
        if (result.GetValue(download))
        {
            if (videos > YouTube.DownloadBatch.MostVideos)
                yield return $"youtube takes at most {YouTube.DownloadBatch.MostVideos} videos at a time; these links name {videos}";
            yield break;
        }

        // Recording as they play: one video's own link at a time, each for as long as it plays.
        foreach (var playlist in reading.Links.Where(link => link.Kind == YouTube.YouTubeLinkKind.Playlist))
            yield return $"{playlist.Url} is a playlist's link, which only --download takes; give each video's own link to record it as it plays";
        foreach (var live in reading.Links.Where(link => link.Kind == YouTube.YouTubeLinkKind.Live))
            yield return $"The live link of {live.Id} is taken only with --download, whose read tells a live stream from a video; give the video's watch link to record it as it plays";
        if (videos > YouTube.VideoBatch.MostVideos)
            yield return $"youtube records at most {YouTube.VideoBatch.MostVideos} videos at a time as they play, each for as long as it plays; these links name {videos}. Give fewer, or add --download";
        if (result.GetValue(allowUnlisted)) yield return ClassScribe.Commands.YouTubeCommand.AllowUnlistedNeedsDownload;
    }

    /// <summary>The words of the links on the command line, then of the list file, each with where it stands.</summary>
    private static IReadOnlyList<YouTube.LinkWord> Words(IEnumerable<string> given, string? listFile) =>
    [
        .. given.Select(word => new YouTube.LinkWord(word, "the command line")),
        .. listFile is null ? [] : YouTube.LinkList.ReadFile(Path.GetFullPath(listFile))
    ];

    /// <summary>What is wrong with <paramref name="value"/> as a path to upload or free: an empty one, or nothing there. Null for a file or a folder.</summary>
    private static string? PathProblem(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "Nothing found at an empty path";

        var full = Path.GetFullPath(value);
        return File.Exists(full) || Directory.Exists(full) ? null : $"Nothing found at {full}";
    }

    /// <summary>
    /// What is wrong with <paramref name="value"/> as the client file --client names: no value, a character Windows does not
    /// allow in a path, nothing there, or a folder. Null for a file, which the sign-in then reads.
    /// </summary>
    private static string? ClientFileProblem(string? value, HashSet<string> optionNames)
    {
        if (ValueProblem(value, "--client needs the client file you downloaded from Google after it, such as \"$env:USERPROFILE\\Downloads\\client_secret_....json\"", optionNames) is { } missing)
            return missing;
        if (value!.IndexOfAny(NotInPaths) >= 0 || value.Any(char.IsControl))
            return $"--client takes a file; \"{value}\" has a character Windows does not allow in a path: < > \" | ? *";

        var file = Path.GetFullPath(value);
        return Directory.Exists(file) ? $"--client names a folder, not the client file: {file}"
            : !File.Exists(file) ? $"Nothing found at {file}"
            : null;
    }

    /// <summary>
    /// record, which records and then transcribes, or record-only, which records only: a name, one source, --output, the
    /// microphone, and the options that shape the run. Every missing piece and every value that cannot be used is named in
    /// one run.
    /// </summary>
    private Command RecordCommand(string word, bool transcribe, HashSet<string> optionNames, Dictionary<Command, string> examples)
    {
        var symbols = new RecordSymbols(
            new Argument<string?>("name")
            {
                Arity = ArgumentArity.ZeroOrOne,
                Description = "The recording's name, such as Kalam. Its folder and files are named after it. With --class, it replaces the saved class's course."
            },
            new Option<string>("--class")
            {
                HelpName = "saved class",
                Description = "Record the saved class of this name as it is saved, its name matched in any case (ClassScribe class list shows them); options given beside it replace its own, in groups: a source replaces its whole source, --mic its microphone."
            },
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
            new Option<string>("--in-person")
            {
                HelpName = "microphone",
                Arity = ArgumentArity.ZeroOrOne,
                Description = "Record your microphone alone, for a class in the room: part of its name, such as \"OBSBOT\", or nothing for Recording:Microphone, else Windows' default microphone (never a Bluetooth one)."
            },
            new Option<string>("--mic")
            {
                HelpName = "microphone",
                Arity = ArgumentArity.ZeroOrOne,
                Description = "Record your microphone too, beside --device or --app, as a second track mixed into one transcript: part of its name, or nothing for Recording:Microphone, else Windows' default microphone (never a Bluetooth one)."
            },
            new Option<string>("--mic-follows")
            {
                HelpName = "app",
                Description = $"With --mic: the app whose microphone use your microphone follows, by part of its process name, such as chrome; {FollowRule.AnyWord} follows any app that holds the microphone; {FollowRule.NoneWord} records whenever V is on. Without it, the app recorded with --app, else Recording:MicrophoneFollows."
            },
            new Option<string>("--camera")
            {
                HelpName = "camera",
                Arity = ArgumentArity.ZeroOrOne,
                Description = "With --in-person: record a lesson's picture too, from the camera whose name holds this part, such as \"OBSBOT\", or nothing for a USB camera, else the built-in front camera. The picture is recorded beside the sound, on the same clock; only a set-up problem that keeps the camera from use (Windows older than 10 version 2004, its privacy switches, no H.264 encoder) refuses such a recording, before anything records."
            },
            new Option<string>("--resolution")
            {
                HelpName = "size",
                Description = $"With --camera: the picture's size, {CameraSize.Rule}. Without it, Recording:CameraResolution (720p)."
            },
            new Option<string>("--output")
            {
                HelpName = "folder",
                Description = "The folder the recording goes to, such as \"D:\\Classes\": the recording gets a folder of its name inside it. Without it, a saved class's own folder, else Recording:OutputFolder."
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
            symbols.Name, symbols.Class, symbols.Device, symbols.App, symbols.InPerson, symbols.Mic, symbols.MicFollows, symbols.Camera, symbols.Resolution, symbols.Output, symbols.Minutes,
            symbols.NoOpen, symbols.Continue, symbols.New
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
    /// name, the source, the microphone and the app it follows, the camera and its size, --output, --minutes, then
    /// --continue with --new. With --class the name and the source may be left out, and what depends on the class (--mic-follows
    /// without --mic, --camera and --resolution) is checked as the run starts, over the class; without it, a name that is a
    /// saved class's, given without a source, is pointed at --class (N5). --output may always be left out (N6).
    /// </summary>
    private IEnumerable<string> RecordProblems(string word, CommandResult result, RecordSymbols symbols, HashSet<string> optionNames)
    {
        var withClass = result.GetResult(symbols.Class) is not null;
        if (withClass && ValueProblem(TokenOf(result.GetResult(symbols.Class)), "--class needs a saved class's name after it, such as --class \"Mantiq\"", optionNames) is { } classProblem)
            yield return classProblem;

        var name = TokenOf(result.GetResult(symbols.Name));
        if (string.IsNullOrWhiteSpace(name))
        {
            if (!withClass) yield return $"{word} needs a name, right after the word {word}: ClassScribe {word} \"Kalam\" ...{NameTakenBy(result, symbols)}";
        }
        else if (FolderNames.SafeName(name).Length == 0)
        {
            yield return $"{word} cannot use \"{name}\" as a name: nothing is left of it once the characters Windows does not allow in a folder's name are taken out";
        }

        var device = result.GetResult(symbols.Device);
        var app = result.GetResult(symbols.App);
        var inPerson = result.GetResult(symbols.InPerson);
        var sources = new (OptionResult? Given, string Name)[] { (device, symbols.Device.Name), (app, symbols.App.Name), (inPerson, symbols.InPerson.Name) }
            .Where(source => source.Given is not null)
            .Select(source => source.Name)
            .ToList();
        if (sources.Count == 0 && !withClass)
        {
            yield return $"{word} needs a source: --device \"<part of its name>\", --device auto, --app <process> or --in-person";
            if (!string.IsNullOrWhiteSpace(name) && (classes?.Find(name) ?? classes?.FindInFile(name)) is { } saved)
                yield return $"{name} is a saved class: ClassScribe {word} --class \"{saved.Name}\"";
        }

        if (sources.Count > 1) yield return $"{word} takes one source; you gave {Listed(sources)}";
        if (device is not null && ValueProblem(TokenOf(device), DeviceNeedsValue, optionNames) is { } deviceProblem) yield return deviceProblem;
        if (app is not null && ValueProblem(TokenOf(app) is { } process ? ProcessName(process) : null, AppNeedsValue, optionNames) is { } appProblem) yield return appProblem;

        var mic = result.GetResult(symbols.Mic);
        if (mic is not null && inPerson is not null) yield return "--mic adds the microphone to --device or --app; with --in-person the microphone is already the source";
        if (result.GetResult(symbols.MicFollows) is { } follows)
        {
            if (inPerson is not null) yield return "--mic-follows cannot go with --in-person: in person there is no class app";
            else if (mic is null && !withClass) yield return "--mic-follows goes with --mic: it names the app whose microphone use your microphone follows";
            if (FollowProblem(TokenOf(follows), optionNames) is { } followProblem) yield return followProblem;
        }

        var camera = result.GetResult(symbols.Camera);
        if (camera is not null && inPerson is null && !withClass) yield return "--camera records only in person in this build: give it with --in-person";
        if (result.GetResult(symbols.Resolution) is { } resolution)
        {
            if (camera is null && !withClass) yield return "--resolution needs --camera";
            if (ResolutionProblem(TokenOf(resolution), optionNames) is { } resolutionProblem) yield return resolutionProblem;
        }

        if (result.GetResult(symbols.Output) is { } output && FolderProblem(symbols.Output.Name, TokenOf(output), optionNames) is { } outputProblem) yield return outputProblem;

        if (result.GetResult(symbols.Minutes) is { } minutes && ReadMinutes(TokenOf(minutes), out _) is { } minutesProblem) yield return minutesProblem;

        if (result.GetValue(symbols.Continue) && result.GetValue(symbols.New)) yield return "--continue and --new cannot go together";
    }

    /// <summary>
    /// When an option whose value may be left out took the word meant as the recording's name, says so, after a space, in
    /// brackets: record --new True gives (--new took "True" as its value), record --in-person Kalam gives (--in-person
    /// took "Kalam" as the microphone's name), and --camera Kalam (--camera took "Kalam" as the camera's name). Empty otherwise.
    /// </summary>
    private static string NameTakenBy(CommandResult result, RecordSymbols symbols)
    {
        var taken = result.Command.Options
            .Where(option => option.Arity is { MinimumNumberOfValues: 0, MaximumNumberOfValues: > 0 })
            .Select(option => (option.Name, Token: TokenOf(result.GetResult(option)),
                As: option == symbols.Mic || option == symbols.InPerson ? "the microphone's name" : option == symbols.Camera ? "the camera's name" : "its value"))
            .Where(taker => taker.Token is not null)
            .Select(taker => $"{taker.Name} took \"{taker.Token}\" as {taker.As}")
            .ToList();
        return taken.Count > 0 ? $" ({string.Join("; ", taken)})" : "";
    }

    /// <summary>The options given, in words: "--device and --app", or "--device, --app and --in-person".</summary>
    private static string Listed(IReadOnlyList<string> options) =>
        options.Count == 2 ? $"{options[0]} and {options[1]}" : $"{string.Join(", ", options.Take(options.Count - 1))} and {options[^1]}";

    /// <summary>
    /// Starts the recording a record or record-only command line describes, as a request from the command line: the class
    /// alone, the class and the microphone, or the microphone alone in person, with the camera when --camera asks for it. A
    /// microphone without a name, and the app it follows without --mic-follows, are left to the run, which reads
    /// Recording:Microphone and Recording:MicrophoneFollows, or follows the app it records; a picture's size without
    /// --resolution is left to the starter, which resolves it before the run starts. Without --output the recording goes
    /// to Recording:OutputFolder (N6). With --class, the saved class is started instead (see <see cref="RecordClassAsync"/>).
    /// </summary>
    private Task<int> RecordAsync(ParseResult parsed, RecordSymbols symbols, bool transcribe)
    {
        if (parsed.GetResult(symbols.Class) is not null) return RecordClassAsync(parsed, symbols, transcribe);

        var inPerson = parsed.GetResult(symbols.InPerson) is not null;
        var mic = parsed.GetResult(symbols.Mic) is not null;
        ClassSource? source = inPerson ? null
            : parsed.GetValue(symbols.App) is { } app ? new ClassSource.App(ProcessName(app))
            : parsed.GetValue(symbols.Device) is { } device && !device.Equals(Automatic, StringComparison.OrdinalIgnoreCase) ? new ClassSource.Device(device)
            : new ClassSource.Automatic();
        var camera = parsed.GetResult(symbols.Camera) is null ? null : new CameraChoice(NamePart(parsed.GetValue(symbols.Camera)), ResolutionOf(parsed.GetValue(symbols.Resolution)));
        var plan = inPerson ? new SourcePlan(RecordingMode.Microphone, null, new MicrophoneChoice(NamePart(parsed.GetValue(symbols.InPerson))), camera)
            : mic ? new SourcePlan(RecordingMode.ClassAndMicrophone, source, new MicrophoneChoice(NamePart(parsed.GetValue(symbols.Mic)), FollowRuleOf(parsed.GetValue(symbols.MicFollows))))
            : new SourcePlan(RecordingMode.Class, source, Microphone: null);

        var request = new RecordRequest(
            RunIds.New(time.GetUtcNow()),
            FolderNames.SafeName(parsed.GetValue(symbols.Name)!),
            transcribe,
            plan,
            OutputRoot(parsed.GetValue(symbols.Output), classFolder: null),
            ContinueOf(parsed, symbols),
            ContinueBaseName: null,
            parsed.GetValue(symbols.Minutes),
            Open: !parsed.GetValue(symbols.NoOpen));
        return commands.RecordAsync(request, StartOrigin.CommandLine);
    }

    /// <summary>
    /// record --class: the saved class named, matched exactly in any case, with the options given over it in groups (see
    /// <see cref="Settings.ClassChoices"/>): a source replaces its whole source, --mic its microphone, --mic-follows what it
    /// follows, --camera and --resolution its camera; a name given replaces its course; --output, --minutes, --no-open,
    /// --continue and --new apply as to any run. An unknown class, a mix the class refuses, and a class that cannot be used
    /// are mistakes, exit 2, said on standard error with the help. The run is the class's own, and says so (ClassName).
    /// </summary>
    private async Task<int> RecordClassAsync(ParseResult parsed, RecordSymbols symbols, bool transcribe)
    {
        var name = parsed.GetValue(symbols.Class)!.Trim();
        var saved = classes?.All() ?? [];
        if (classes?.FieldsOf(name) is not { } fields || saved.FirstOrDefault(entry => entry.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) is not { } entry)
            return Refuse(parsed, ClassCommand.NoSuchClass(name, saved.Select(known => known.Name)));

        var source = parsed.GetResult(symbols.InPerson) is not null ? new Settings.SourceGiven(Settings.SourceOption.InPerson, NamePart(parsed.GetValue(symbols.InPerson)))
            : parsed.GetValue(symbols.App) is { } app ? new Settings.SourceGiven(Settings.SourceOption.App, ProcessName(app))
            : parsed.GetValue(symbols.Device) is { } device ? new Settings.SourceGiven(Settings.SourceOption.Device, device)
            : null;
        var choices = new Settings.ClassChoices
        {
            Course = parsed.GetValue(symbols.Name) is { } course && !string.IsNullOrWhiteSpace(course) ? FolderNames.SafeName(course) : null,
            Source = source,
            Mic = parsed.GetResult(symbols.Mic) is not null,
            MicName = NamePart(parsed.GetValue(symbols.Mic)),
            Follows = parsed.GetValue(symbols.MicFollows),
            Camera = parsed.GetResult(symbols.Camera) is not null,
            CameraName = NamePart(parsed.GetValue(symbols.Camera)),
            Resolution = ResolutionOf(parsed.GetValue(symbols.Resolution))
        };
        if (choices.ApplyTo(fields) is { } mix) return Refuse(parsed, $"{mix}, over the saved class \"{entry.Name}\"");

        var chosen = Settings.SavedClasses.Check(entry.Name, fields);
        if (chosen.Class is not { } ready)
            return Refuse(parsed, $"The saved class \"{entry.Name}\" cannot be used{(entry.Usable ? " with these options" : "")}: {string.Join(" ", chosen.Problems)}");

        var request = new RecordRequest(
            RunIds.New(time.GetUtcNow()),
            ready.Course,
            transcribe,
            ready.Plan,
            OutputRoot(parsed.GetValue(symbols.Output), ready.OutputFolder),
            ContinueOf(parsed, symbols),
            ContinueBaseName: null,
            parsed.GetValue(symbols.Minutes),
            Open: !parsed.GetValue(symbols.NoOpen),
            ClassName: ready.Name);
        return await commands.RecordAsync(request, StartOrigin.CommandLine);
    }

    /// <summary>
    /// The recordings folder for a run, as a full path: --output when given; else a saved class's own folder,
    /// <paramref name="classFolder"/>; else Recording:OutputFolder, as the settings in force say it (N6).
    /// </summary>
    private string OutputRoot(string? given, string? classFolder) =>
        given is not null ? Path.GetFullPath(given) : ClassFolders.RootFor(classFolder ?? recording?.CurrentValue.OutputFolder);

    /// <summary>What --continue or --new say about a recent recording of the same name; neither asks, which the command line never does.</summary>
    private static ContinueChoice ContinueOf(ParseResult parsed, RecordSymbols symbols) =>
        parsed.GetValue(symbols.Continue) ? ContinueChoice.Continue
        : parsed.GetValue(symbols.New) ? ContinueChoice.New
        : ContinueChoice.Ask;

    /// <summary>A mistake found as a command runs: said on standard error, followed by the command's help; exit 2.</summary>
    private int Refuse(ParseResult parsed, string why)
    {
        Invocation.Error.WriteLine(why);
        WriteHelp(parsed);
        return Mistake;
    }

    /// <summary>Part of a microphone's name as --mic or --in-person gave it, trimmed; null when none was given, which leaves the microphone to the settings.</summary>
    private static string? NamePart(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>The rule --mic-follows spells, already checked (see <see cref="FollowProblem"/>); null when it was not given.</summary>
    private static FollowRule? FollowRuleOf(string? value) => FollowRule.TryParse(value, out var rule) ? rule : null;

    /// <summary>
    /// What is wrong with <paramref name="value"/> as the app --mic-follows names: no value, or an option taken as the
    /// value, or a value that names no app, or part of ClassScribe's own name. Null when it spells a rule.
    /// </summary>
    private static string? FollowProblem(string? value, HashSet<string> optionNames)
    {
        if (ValueProblem(value, FollowsNeedsValue, optionNames) is { } missing) return missing;
        if (!FollowRule.TryParse(value, out var rule)) return FollowsNeedsValue;

        return rule is FollowRule.Named named && named.Matches(OwnProcessName) ? FollowsNeedsValue : null;
    }

    /// <summary>
    /// transcribe: a recording, by its folder or one of its files, or any audio or video file, with --output, --no-open,
    /// --again and --keyterms. --output is passed on as a full path, for the transcript of a file that is not a recording's; a
    /// recording's transcript stays beside the recording, and the transcribe command says that --output was not used.
    /// --keyterms, a glossary for this transcript instead of the one decided when the recording started, is passed on as a
    /// full path, or as none; one that is not there is a mistake, before anything is sent.
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
        var keyterms = new Option<string>("--keyterms")
        {
            HelpName = "file",
            Description = $"Send this glossary, one term per line, instead of the one decided when the recording started, or {Settings.SavedClass.NoGlossary} for no glossary; this transcript only, and its entry in the recording's .glossary.json says so."
        };

        var command = new Command("transcribe", "Transcribe a recording, named by its folder or one of its files, or any audio or video file.") { path, outputFolder, noOpen, again, keyterms };
        command.Validators.Add(result =>
        {
            if (string.IsNullOrWhiteSpace(TokenOf(result.GetResult(path))))
                result.AddError("transcribe needs a path: a recording's folder, one of its files, or any audio or video file, such as ClassScribe transcribe \"D:\\Classes\\Kalam\"");
            if (result.GetResult(outputFolder) is { } given && FolderProblem(outputFolder.Name, TokenOf(given), optionNames) is { } problem) result.AddError(problem);
            if (result.GetResult(keyterms) is { } glossary && GlossaryGivenProblem(TokenOf(glossary), optionNames) is { } glossaryProblem) result.AddError(glossaryProblem);
        });
        command.SetAction(parsed => commands.TranscribeAsync(new TranscribeRequest(
            parsed.GetValue(path)!,
            parsed.GetValue(outputFolder) is { } folder ? Path.GetFullPath(folder) : null,
            Open: !parsed.GetValue(noOpen),
            Again: parsed.GetValue(again),
            FromCommandLine: true,
            Keyterms: parsed.GetValue(keyterms) is { } given ? GlossaryGiven(given) : null)));
        examples[command] = TranscribeExamples;
        return command;
    }

    /// <summary>
    /// The glossary --keyterms of transcribe names, as passed on: none, in any case, as none; a file as a full path, with
    /// environment variables expanded and a relative one taken from where the command was typed.
    /// </summary>
    private static string GlossaryGiven(string value)
    {
        var given = value.Trim().Trim('"').Trim();
        return Settings.SavedClass.MeansNoGlossary(given) ? Settings.SavedClass.NoGlossary : Path.GetFullPath(Environment.ExpandEnvironmentVariables(given));
    }

    /// <summary>
    /// What is wrong with <paramref name="value"/> as the glossary of transcribe --keyterms: no value, a character Windows
    /// does not allow in a path, a folder, or nothing there, since a transcript is paid for and a mistyped glossary would send
    /// none. Null for none, and for a file that is there.
    /// </summary>
    private static string? GlossaryGivenProblem(string? value, HashSet<string> optionNames)
    {
        if (ValueProblem(value, KeytermsNeedsValue, optionNames) is { } missing) return missing;

        var given = value!.Trim().Trim('"').Trim();
        if (Settings.SavedClass.MeansNoGlossary(given)) return null;
        if (given.IndexOfAny(NotInPaths) >= 0 || given.Any(char.IsControl))
            return $"--keyterms takes a file; \"{given}\" has a character Windows does not allow in a path: < > \" | ? *";

        var file = GlossaryGiven(given);
        return Directory.Exists(file) ? $"--keyterms names a folder, not a glossary file: {file}"
            : !File.Exists(file) ? $"--keyterms names no file: nothing found at {file}"
            : null;
    }

    /// <summary>rebuild: a recording, by its folder or one of its files, or a saved answer, with --no-open.</summary>
    private Command RebuildCommand(Dictionary<Command, string> examples)
    {
        var path = new Argument<string>("path") { Description = "A recording's folder, one of its files, or an answer from ElevenLabs saved as .json." };
        var noOpen = new Option<bool>("--no-open") { Description = "Open nothing at the end." };

        // Not one of rebuild's options: it is there to say why rebuild takes no glossary, rather than that the word is unknown.
        var keyterms = new Option<string>("--keyterms") { Hidden = true, Arity = ArgumentArity.ZeroOrOne };

        var command = new Command("rebuild", "Make a recording's transcript again, for free, from ElevenLabs' saved answer, with the marks and Transcript settings as they are now.") { path, noOpen, keyterms };
        command.Validators.Add(result =>
        {
            if (string.IsNullOrWhiteSpace(TokenOf(result.GetResult(path))))
                result.AddError("rebuild needs a path: a recording's folder, one of its files, or an answer from ElevenLabs saved as .json, such as ClassScribe rebuild \"D:\\Classes\\Kalam\"");
            if (result.GetResult(keyterms) is not null)
                result.AddError("rebuild sends nothing to ElevenLabs and uses no glossary; --keyterms goes with transcribe, which is charged");
        });
        command.SetAction(parsed => commands.RebuildAsync(parsed.GetValue(path)!, open: !parsed.GetValue(noOpen)));
        examples[command] = RebuildExamples;
        return command;
    }

    /// <summary>finish-video: a lesson, by its folder or one of its files, made into its video again, for free, with --no-open; a path that is not there is a mistake.</summary>
    private Command FinishVideoCommand(Dictionary<Command, string> examples)
    {
        var path = new Argument<string>("path") { Description = "A lesson's folder (its newest lesson with the camera is meant) or one of its files." };
        var noOpen = new Option<bool>("--no-open") { Description = "Open nothing at the end." };

        var command = new Command("finish-video", "Make a lesson's video again, for free, from its camera pieces and its sound; nothing is uploaded or billed.") { path, noOpen };
        command.Validators.Add(result =>
        {
            if (TokenOf(result.GetResult(path)) is not { } given || string.IsNullOrWhiteSpace(given))
                result.AddError("finish-video needs a path: a lesson's folder or one of its files, such as ClassScribe finish-video \"D:\\Classes\\Tajwid\"");
            else if (PathProblem(given) is { } problem)
                result.AddError(problem);
        });
        command.SetAction(parsed => commands.FinishVideoAsync(new FinishVideoRequest(Path.GetFullPath(parsed.GetValue(path)!), Open: !parsed.GetValue(noOpen))));
        examples[command] = FinishVideoExamples;
        return command;
    }

    /// <summary>list: the recordings under the folder --output names, or under Recording:OutputFolder.</summary>
    private Command ListCommand(HashSet<string> optionNames, Dictionary<Command, string> examples)
    {
        var outputFolder = new Option<string>("--output") { HelpName = "folder", Description = "The recordings folder to list, instead of the one Recording:OutputFolder names." };

        var command = new Command("list", "List the recordings, newest first, with the ones being recorded or transcribed now.") { outputFolder };
        command.Validators.Add(result =>
        {
            if (result.GetResult(outputFolder) is { } given && FolderProblem(outputFolder.Name, TokenOf(given), optionNames) is { } problem) result.AddError(problem);
        });
        command.SetAction(parsed => commands.ListAsync(parsed.GetValue(outputFolder) is { } folder ? Path.GetFullPath(folder) : null));
        examples[command] = ListExamples;
        return command;
    }

    /// <summary>devices: the output devices, the apps with sound, the microphones and the cameras.</summary>
    private Command DevicesCommand(Dictionary<Command, string> examples)
    {
        var command = new Command("devices", "List the output devices, the apps with sound, the microphones and the cameras, each with the option that records it. Opens nothing.");
        command.SetAction(_ => commands.DevicesAsync());
        examples[command] = DevicesExamples;
        return command;
    }

    /// <summary>
    /// mic-check: watches what the programs on this computer do with the microphone and writes a line for each change,
    /// until Q, Ctrl+C or --seconds, to a log under the recordings folder or under --log-folder, as a full path. It opens
    /// no microphone and records nothing.
    /// </summary>
    private Command MicCheckCommand(HashSet<string> optionNames, Dictionary<Command, string> examples)
    {
        var seconds = new Option<int?>("--seconds")
        {
            HelpName = "n",
            Description = "Stop by itself after this many seconds, from 1 to 86400 (a day), such as 600. Without it, Q or Ctrl+C stops the check.",
            CustomParser = ParseSeconds
        };
        var logFolder = new Option<string>("--log-folder")
        {
            HelpName = "folder",
            Description = "The folder the log goes to, such as \"D:\\Classes\", instead of the mic-check folder under Recording:OutputFolder."
        };

        var command = new Command("mic-check",
            "Watch what the programs on this computer do with the microphone and write a line for each change, until Q, Ctrl+C or --seconds. Opens no microphone. M writes a numbered mark.")
        {
            seconds, logFolder
        };
        command.Validators.Add(result =>
        {
            if (result.GetResult(seconds) is { } given && ReadSeconds(TokenOf(given), out _) is { } problem) result.AddError(problem);
            if (result.GetResult(logFolder) is { } folder && FolderProblem(logFolder.Name, TokenOf(folder), optionNames) is { } folderProblem) result.AddError(folderProblem);
        });
        command.SetAction(parsed => commands.MicCheckAsync(new MicCheckRequest(
            parsed.GetValue(seconds) is { } after ? TimeSpan.FromSeconds(after) : null,
            parsed.GetValue(logFolder) is { } folder ? Path.GetFullPath(folder) : null)));
        examples[command] = MicCheckExamples;
        return command;
    }

    /// <summary>
    /// camera-check: the cameras from Windows' own lists, which apps use one, the camera and the picture's size a lesson
    /// recorded with --camera would take, and the camera's set-up lines; it opens the camera for 5 seconds, shared, only after a typed Y. Part of a camera's
    /// name and --resolution, checked as record's are, show what a lesson with them would take.
    /// </summary>
    private Command CameraCheckCommand(HashSet<string> optionNames, Dictionary<Command, string> examples)
    {
        var camera = new Argument<string?>("camera")
        {
            Arity = ArgumentArity.ZeroOrOne,
            Description = "Part of a camera's name, such as OBSBOT: the camera a lesson recorded with --camera \"OBSBOT\" would take."
        };
        var resolution = new Option<string>("--resolution")
        {
            HelpName = "size",
            Description = $"The picture's size to show: {CameraSize.Rule}. Without it, Recording:CameraResolution."
        };

        var command = new Command("camera-check",
            "List the cameras, which apps use one now, the camera and the size a lesson would take, and what the camera needs on this computer. Opens the camera for 5 seconds, shared, only after you type Y.")
        {
            camera, resolution
        };
        command.Validators.Add(result =>
        {
            if (result.GetResult(resolution) is { } given && ResolutionProblem(TokenOf(given), optionNames) is { } problem) result.AddError(problem);
        });
        command.SetAction(parsed => commands.CameraCheckAsync(new CameraCheckRequest(NamePart(parsed.GetValue(camera)), ResolutionOf(parsed.GetValue(resolution)))));
        examples[command] = CameraCheckExamples;
        return command;
    }

    /// <summary>
    /// What is wrong with <paramref name="value"/> as the size --resolution names: no value, an option taken as the value, or
    /// a size <see cref="CameraSize.TryParse"/> does not take, with its rule. Null for a size.
    /// </summary>
    private static string? ResolutionProblem(string? value, HashSet<string> optionNames)
    {
        if (ValueProblem(value, "--resolution needs a size after it, such as 720p", optionNames) is { } missing) return missing;

        return CameraSize.TryParse(value, out _) ? null : $"--resolution takes {CameraSize.Rule}; \"{value}\" is not one";
    }

    /// <summary>The size --resolution names, already checked, as it is written (720p, 1024x576); null when it was not given.</summary>
    private static string? ResolutionOf(string? value) => CameraSize.TryParse(value, out var size) ? size.Text : null;

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
    /// What is wrong with <paramref name="value"/> as the folder the option <paramref name="option"/>, --output or
    /// --log-folder, names: no value, a character Windows does not allow in a path, or a file where the folder should be.
    /// Null for a folder that can be used, or made.
    /// </summary>
    private static string? FolderProblem(string option, string? value, HashSet<string> optionNames)
    {
        if (ValueProblem(value, $"{option} needs a folder after it, such as \"D:\\Classes\"", optionNames) is { } missing) return missing;
        if (value!.IndexOfAny(NotInPaths) >= 0 || value.Any(char.IsControl))
            return $"{option} takes a folder, such as \"D:\\Classes\"; \"{value}\" has a character Windows does not allow in a path: < > \" | ? *";

        var folder = Path.GetFullPath(value);
        return File.Exists(folder) ? $"{option} names a file, not a folder: {folder}" : null;
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

    /// <summary>Reads --seconds with <see cref="ReadSeconds"/>; a value it cannot take is a mistake of the command line.</summary>
    private static int? ParseSeconds(ArgumentResult result)
    {
        if (ReadSeconds(TokenOf(result), out var seconds) is not { } problem) return seconds;

        result.AddError(problem);
        return null;
    }

    /// <summary>
    /// Reads a number of seconds: digits only, from 1 to a day. Returns what is wrong with <paramref name="text"/>, or null
    /// when <paramref name="seconds"/> holds the number.
    /// </summary>
    private static string? ReadSeconds(string? text, out int seconds)
    {
        seconds = 0;
        if (text is null || !SecondsShape().IsMatch(text)) return SecondsFormat;

        return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out seconds) && seconds is >= 1 and <= MostSeconds ? null : SecondsRange;
    }

    /// <summary>The shape of a number of seconds: digits, 0 to 9 only, and \z ends the match at the very end.</summary>
    [GeneratedRegex(@"^[0-9]+\z", RegexOptions.CultureInvariant)]
    private static partial Regex SecondsShape();

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
          ClassScribe record "Kalam" --app chrome --mic --output "D:\Classes"
          ClassScribe record "Kalam" --in-person --output "D:\Classes"
          ClassScribe transcribe "D:\Classes\Kalam"
          ClassScribe "D:\Lectures\Lecture 5.mp4"
          ClassScribe mic-check
          ClassScribe drive status
          ClassScribe youtube "https://youtu.be/abcdefghijk" --name "Risala" --output "D:\Classes" --download --plan

        Without a command, ClassScribe shows its menu. A path alone is transcribed, as when a file is dropped on ClassScribe.exe.
        Add --help after a command for its options, such as: ClassScribe record --help
        While recording, Q twice or Ctrl+C twice stops, P pauses or resumes, M marks a moment, and V switches your microphone off and on.
        The API key is read from the ELEVENLABS_API_KEY environment variable.
        Exit codes: 0 done; 1 failed or refused; 2 a mistake on the command line; 130 cancelled with Ctrl+C.
        """;

    /// <summary>The examples and notes under the help of record or record-only, named by <paramref name="word"/>.</summary>
    private static string RecordExamples(string word) => $"""
        Examples:
          ClassScribe {word} "Kalam" --device auto --output "D:\Classes"
          ClassScribe {word} "Kalam" --device "Pixel Buds" --output "D:\Classes" --minutes 90
          ClassScribe {word} "Kalam" --app chrome --output "D:\Classes" --continue
          ClassScribe {word} "Kalam" --app chrome --mic --mic-follows chrome --output "D:\Classes"
          ClassScribe {word} "Kalam" --in-person --output "D:\Classes"
          ClassScribe {word} "Kalam" --app chrome
          ClassScribe {word} --class "Mantiq"
          ClassScribe {word} --class "Mantiq" --app zoom --mic-follows zoom

        The name and one source (--device, --app or --in-person) are needed, or --class and a saved class's name; nothing is asked.
        Without --output the recording goes to the saved class's own folder, else Recording:OutputFolder.
        Options beside --class replace the class's own in groups: a source replaces its whole source and the app its microphone follows, --mic its microphone, a name its course.
        --mic adds your microphone to --device or --app, as a second track mixed into one transcript; --in-person records it alone. Either takes part of a microphone's name, or nothing for Recording:Microphone.
        --camera, with --in-person, records a lesson's picture too, beside the sound; only a set-up problem that keeps the camera from use refuses such a recording, before anything records. ClassScribe camera-check shows the cameras and those problems.
        While recording, Q twice or Ctrl+C twice stops, P pauses or resumes, M marks a moment, and V switches your microphone off and on; Esc leaves the view of a recording in the background, which carries on.
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

    /// <summary>The examples and notes under the help of finish-video.</summary>
    private const string FinishVideoExamples = """
        Examples:
          ClassScribe finish-video "D:\Classes\Tajwid"
          ClassScribe finish-video "D:\Classes\Tajwid\2026-10-19\Tajwid_2026-10-19_14-00-02Z.wav" --no-open

        Nothing is uploaded and nothing is billed. The lesson's camera pieces and its .wav files must be on this laptop; the
        older video is replaced only once the new one has passed its check.
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

    /// <summary>The examples and notes under the help of mic-check.</summary>
    private const string MicCheckExamples = """
        Examples:
          ClassScribe mic-check
          ClassScribe mic-check --seconds 600 --log-folder "D:\Classes"

        Nothing is recorded and no microphone is opened: it only reads which programs hold the microphone, and whether it is muted in Windows, and writes a line for each change.
        The log's path is printed at the start and at the end. Q stops the check; M writes a numbered mark.
        """;

    /// <summary>The examples and notes under the help of camera-check.</summary>
    private const string CameraCheckExamples = """
        Examples:
          ClassScribe camera-check
          ClassScribe camera-check "OBSBOT" --resolution 1080p

        The cameras come from Windows' own lists, and the set-up lines from Windows' settings and its Security Center, which open nothing. Then, only after you type Y and Enter, the chosen camera is opened for 5 seconds, shared, as a lesson will: its light comes on, each call is shown with its outcome, and a security program such as Bitdefender may ask about ClassScribe; allow it. Nothing of the picture is kept.
        Exit code 0 when the camera can be used on this computer; 1 when a set-up line says what keeps it from being used, or the camera opened and sent no picture.
        """;

    /// <summary>The examples and notes under the help of drive.</summary>
    private const string DriveExamples = """
        Examples:
          ClassScribe drive signin --client "$env:USERPROFILE\Downloads\client_secret_....json"
          ClassScribe drive status
          ClassScribe drive upload "$env:USERPROFILE\ClassScribe\Mantiq\2026-10-17"
          ClassScribe drive upload
          ClassScribe drive free-space "$env:USERPROFILE\ClassScribe\Mantiq\2026-10-17" --yes
          ClassScribe drive signout

        The first sign-in needs the client file you made in your own Google Cloud project; the README's "Google Drive (set up once)" has the steps.
        ClassScribe asks Google only for its own files and folders in your Drive, nothing else, and never deletes, trashes or shares anything there.
        """;

    /// <summary>The examples and notes under the help of drive upload.</summary>
    private const string DriveUploadExamples = """
        Examples:
          ClassScribe drive upload "$env:USERPROFILE\ClassScribe\Mantiq\2026-10-17"
          ClassScribe drive upload "$env:USERPROFILE\ClassScribe\Mantiq"
          ClassScribe drive upload "$env:USERPROFILE\ClassScribe"
          ClassScribe drive upload

        Each recording goes to My Drive > ClassScribe > its course > its date folder, as on this laptop; one of the old layout, to My Drive > ClassScribe > its name.
        Without a path, every recording waiting to go up goes up. An upload that stops goes on from where it stopped next time.
        While a class records, the upload waits between pieces. Esc or Ctrl+C stops it, and what is left waits.
        """;

    /// <summary>The examples and notes under the help of drive free-space.</summary>
    private const string DriveFreeSpaceExamples = """
        Examples:
          ClassScribe drive free-space "$env:USERPROFILE\ClassScribe\Mantiq\2026-10-17" --yes
          ClassScribe drive free-space "$env:USERPROFILE\ClassScribe\Mantiq" --yes

        Only a recording whose every .wav file has a copy on Google Drive with the same size and MD5, asked of Drive again now, loses its audio here.
        Its transcripts, ElevenLabs' answers, its marks and its log stay on this laptop, and so do the other files in its folder. Nothing on Drive is touched.
        """;

    /// <summary>The examples and notes under the help of drive signin.</summary>
    private const string DriveSignInExamples = """
        Examples:
          ClassScribe drive signin --client "$env:USERPROFILE\Downloads\client_secret_....json"
          ClassScribe drive signin

        Your browser opens Google's sign-in: choose your account and allow ClassScribe. Esc in this window gives up.
        The client is kept, encrypted for your Windows account, so a later sign-in needs no --client.
        """;

    /// <summary>The examples and notes under the help of drive signout.</summary>
    private const string DriveSignOutExamples = """
        Examples:
          ClassScribe drive signout

        The files on Drive stay as they are. Signing in again needs no client file.
        """;

    /// <summary>The examples under the help of drive status.</summary>
    private const string DriveStatusExamples = """
        Examples:
          ClassScribe drive status
        """;

    /// <summary>The examples and notes under the help of youtube.</summary>
    private const string YouTubeExamples = """
        Examples:
          ClassScribe youtube "https://youtu.be/abcdefghijk" --name "Risala" --output "$env:USERPROFILE\ClassScribe" --yes
          ClassScribe youtube "https://youtu.be/abcdefghijk" --name "Risala" --output "$env:USERPROFILE\ClassScribe" --download --plan
          ClassScribe youtube --list "risala-links.txt" --name "Risala" --output "$env:USERPROFILE\ClassScribe" --download --yes
          ClassScribe youtube "https://www.youtube.com/playlist?list=PL..." --name "Lectures" --output "D:\Classes" --download --no-transcribe --yes
          ClassScribe youtube check

        Without --download, each video opens in a Chrome window of ClassScribe's own when you press Enter; you press play there, and it is recorded until it ends: at most 10 videos, each at most YouTube:LongestVideoHours.
        Q twice stops the video in hand; Ctrl+C twice stops the batch. Leave YouTube's Autoplay on: its move to the next video is how ClassScribe knows one has ended.
        --download: only videos you uploaded, or whose owner allows you to download them, and never your course's: YouTube:CourseChannels names its channels, which are never downloaded, nor is a link copied from a player embedded in a web page.
        An unlisted video, or one YouTube does not say is public, needs your word: in the menu (Y), or its channel in YouTube:MayDownloadChannels. --allow-unlisted gives it for every video YouTube says is unlisted, and needs YouTube:CourseChannels.
        The plan comes first, with the cost, the time and the room; --yes goes ahead, --plan stops there. The same command run again skips what is done.
        Downloading, Q stops after the video in hand; Q twice, or Ctrl+C, stops at once. yt-dlp is installed with: winget install --source winget --id yt-dlp.yt-dlp
        """;

    /// <summary>The examples under the help of youtube check.</summary>
    private const string YouTubeCheckExamples = """
        Examples:
          ClassScribe youtube check

        Exit code 0 when yt-dlp, Deno, FFmpeg and FFprobe are all in place and yt-dlp is less than 90 days old; 1 otherwise, with what to do.
        """;

    /// <summary>The examples under the help of the hidden attach.</summary>
    private const string AttachExamples = """
        Examples:
          ClassScribe attach
          ClassScribe attach --alert 20261015-230002Z-1f3a9c07
        """;

    /// <summary>The symbols of record and record-only, which their check and their action share.</summary>
    /// <param name="Name">The recording's name; with --class, the course in place of the class's.</param>
    /// <param name="Class">--class: the saved class to record.</param>
    /// <param name="Device">--device: part of a device's name, or auto.</param>
    /// <param name="App">--app: an app's process name.</param>
    /// <param name="InPerson">--in-person: the microphone alone, with part of its name or nothing.</param>
    /// <param name="Mic">--mic: the microphone beside the class, with part of its name or nothing.</param>
    /// <param name="MicFollows">--mic-follows: the app the microphone follows, or auto or none.</param>
    /// <param name="Camera">--camera: the lesson's picture too, in person, with part of a camera's name or nothing.</param>
    /// <param name="Resolution">--resolution: the picture's size, with --camera.</param>
    /// <param name="Output">--output: the recordings folder for this run.</param>
    /// <param name="Minutes">--minutes: stop by itself after this many minutes.</param>
    /// <param name="NoOpen">--no-open: open nothing at the end.</param>
    /// <param name="Continue">--continue: continue a recent recording of this name.</param>
    /// <param name="New">--new: start a new recording without a word.</param>
    private sealed record RecordSymbols(Argument<string?> Name, Option<string> Class, Option<string> Device, Option<string> App, Option<string> InPerson, Option<string> Mic,
        Option<string> MicFollows, Option<string> Camera, Option<string> Resolution, Option<string> Output, Option<double?> Minutes, Option<bool> NoOpen,
        Option<bool> Continue, Option<bool> New);

    /// <summary>The tree as built, with what tidying, parsing and the kind of a command line need from it.</summary>
    /// <param name="Root">The root command.</param>
    /// <param name="LonePath">The root's hidden argument: a path alone on the command line.</param>
    /// <param name="Help">The help, with examples, of every command.</param>
    /// <param name="CommandNames">The commands' names, in any case.</param>
    /// <param name="SubcommandNames">The names of the commands under a command, such as drive's signin, by that command's name, in any case.</param>
    /// <param name="OptionNames">Every option's names and aliases, in any case.</param>
    private sealed record BuiltTree(RootCommand Root, Argument<string?> LonePath, HelpWithExamples Help, HashSet<string> CommandNames,
        Dictionary<string, HashSet<string>> SubcommandNames, HashSet<string> OptionNames);

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

    /// <summary>
    /// --version: the informational version as the project stamped it, such as 0.4.0+f5f2fb04b43e65a58888ecd217914f200705cb9d
    /// (see <see cref="BuildInfo.InformationalVersion"/>), on one line of standard output, with exit code 0. System.CommandLine's
    /// own action would name the program that started the process, which under the tests is the test host.
    /// </summary>
    private sealed class VersionLine : SynchronousCommandLineAction
    {
        /// <inheritdoc />
        public override int Invoke(ParseResult parseResult)
        {
            parseResult.InvocationConfiguration.Output.WriteLine(BuildInfo.InformationalVersion);
            return 0;
        }
    }
}
