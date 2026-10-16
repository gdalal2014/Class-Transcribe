using ClassScribe.Commands;
using Microsoft.Extensions.Logging;

namespace ClassScribe;

/// <summary>
/// Reads the command line and runs the matching command, or the menu when there is none. Before a command runs, the
/// settings are read again and checked (see <see cref="RunGate"/>); a mistake on the command line is reported first.
/// </summary>
public sealed class App(ICommandRunner commands, MenuCommand menu, RecordingNamePrompt prompt, IConsoleUi ui, RunGate gate, ILogger<App> logger)
{
    /// <summary>Help text shown on request and for an unknown command.</summary>
    public const string Usage = """
        ClassScribe records the sound your computer plays and gets a transcript from ElevenLabs.

          ClassScribe                          Show the menu.
          ClassScribe record "<name>"          Record now under that name, then transcribe.
          ClassScribe record-only "<name>"     Record now; transcribe later from the menu.
          ClassScribe transcribe <path>        Transcribe a recording (its folder or one of its files),
                                               or any audio or video file.
          ClassScribe <path>                   The same. Dropping a file on ClassScribe.exe does this.
          ClassScribe rebuild <path>           Make a recording's transcript again, for free, from
                                               ElevenLabs' saved answer, with the marks and Transcript
                                               settings as they are now: <path> is the recording's
                                               folder, one of its files, or the answer (.json).

          --again                              With transcribe: transcribe a recording that already
                                               has a transcript. This is billed again.
          --no-open                            With transcribe or rebuild: open nothing at the end.

        Without a name, record and record-only ask for one.
        While recording, Q twice (or Ctrl+C) stops, P pauses or resumes, and M marks a moment.
        The API key is read from the ELEVENLABS_API_KEY environment variable.
        """;

    /// <summary>The exit code of a command that did not run because the settings cannot be used.</summary>
    private const int RefusedBySettings = 1;

    /// <summary>The option that transcribes a recording that already has a transcript, which is billed again.</summary>
    private const string Again = "--again";

    /// <summary>The option that opens nothing when the command is done.</summary>
    private const string NoOpen = "--no-open";

    /// <summary>The command names. They are taken in any case.</summary>
    private static readonly string[] CommandNames = ["record", "record-only", "transcribe", "rebuild", "help", "--help", "-h", "/?"];

    /// <summary>The options transcribe takes.</summary>
    private static readonly string[] TranscribeOptions = [Again, NoOpen];

    /// <summary>The options rebuild takes.</summary>
    private static readonly string[] RebuildOptions = [NoOpen];

    /// <summary>Every option a command takes. They are taken in any case.</summary>
    private static readonly string[] OptionNames = [Again, NoOpen];

    /// <summary>Runs the command named by <paramref name="args"/> and returns the process exit code.</summary>
    public async Task<int> RunAsync(string[] args)
    {
        var arguments = Tidy(args);
        try
        {
            switch (arguments)
            {
                case [] when ui.IsInteractive:
                    return await menu.RunAsync();

                case ["record" or "record-only"] when !ui.IsInteractive:
                    ui.WriteLine(Usage);
                    return 2;

                case ["record" or "record-only"]:
                    return await WhenSettingsAllowAsync(async () =>
                        await prompt.AskAsync() is { } asked ? await commands.RecordAsync(asked, transcribe: arguments[0] == "record") : 0);

                case ["record", var given]:
                    return await WhenSettingsAllowAsync(() => commands.RecordAsync(given, transcribe: true));

                case ["record-only", var given]:
                    return await WhenSettingsAllowAsync(() => commands.RecordAsync(given, transcribe: false));

                case ["transcribe", .. var words] when TryRead(words, TranscribeOptions, out var path, out var options):
                    var request = new TranscribeRequest(path, Open: !options.Contains(NoOpen), Again: options.Contains(Again), FromCommandLine: true);
                    return await WhenSettingsAllowAsync(() => commands.TranscribeAsync(request));

                case ["rebuild", .. var words] when TryRead(words, RebuildOptions, out var path, out var options):
                    return await WhenSettingsAllowAsync(() => commands.RebuildAsync(path, open: !options.Contains(NoOpen)));

                case ["help" or "--help" or "-h" or "/?"]:
                    ui.WriteLine(Usage);
                    return 0;

                case [var path] when File.Exists(path) || Directory.Exists(path):
                    return await WhenSettingsAllowAsync(() => commands.TranscribeAsync(new TranscribeRequest(path, FromCommandLine: true)));

                default:
                    ui.WriteLine(Usage);
                    return 2;
            }
        }
        catch (OperationCanceledException)
        {
            // Ctrl+C during the upload: a normal way to leave, reported as such.
            logger.LogWarning("Cancelled");
            return 130;
        }
        catch (Exception ex)
        {
            // Last stop before the process exits: the failure becomes the log line and the exit code.
            logger.LogError(ex, "ClassScribe stopped because of an error");
            return 1;
        }
    }

    /// <summary>
    /// Reads the settings again and runs <paramref name="command"/> once they have passed the check, so that the
    /// command works with the file as it is now. When they cannot be used, the gate has said why, and the command
    /// does not run.
    /// </summary>
    private async Task<int> WhenSettingsAllowAsync(Func<Task<int>> command) =>
        gate.TryOpen(GateUse.Command) ? await command() : RefusedBySettings;

    /// <summary>
    /// Reads the words after a command: exactly one path, and options, in any order, from those in
    /// <paramref name="allowed"/>. False for anything else, such as no path, a second one, or an option the command does
    /// not take, which the caller reports as a mistake on the command line.
    /// </summary>
    private static bool TryRead(string[] words, string[] allowed, out string path, out string[] options)
    {
        options = [.. words.Where(IsOption)];
        string[] paths = [.. words.Where(word => !IsOption(word))];
        path = paths is [var only] ? only : "";
        return paths.Length == 1 && options.All(allowed.Contains);
    }

    /// <summary>True for a word that is an option, such as --again, rather than a path.</summary>
    private static bool IsOption(string word) => word.StartsWith("--", StringComparison.Ordinal);

    /// <summary>
    /// Tidies the command line: a command name and the options are taken in any case, and quotes around an argument go.
    /// Windows PowerShell 5.1 hands a quoted path that ends in a backslash over with a stray quote at the end, and its
    /// tab completion writes a folder whose path has a space just that way; a quote cannot be part of a Windows path
    /// anyway. When options follow such a path, Windows reads its backslash and quote as a quote inside the argument, so
    /// they arrive joined to it, as in C:\Classes\Kalam" --again: they are taken out of it again.
    /// </summary>
    private static string[] Tidy(string[] args)
    {
        string[] tidy = [.. args.SelectMany(SplitOffJoinedOptions)];
        if (tidy is [var command, ..] && CommandNames.Contains(command, StringComparer.OrdinalIgnoreCase))
            tidy[0] = command.ToLowerInvariant();

        for (var i = 1; i < tidy.Length; i++)
        {
            if (OptionNames.Contains(tidy[i], StringComparer.OrdinalIgnoreCase)) tidy[i] = tidy[i].ToLowerInvariant();
        }

        return tidy;
    }

    /// <summary>
    /// An argument without the quotes and spaces around it. When a quote is left inside it and only options follow that
    /// quote, the options were joined to a path by a stray quote (see <see cref="Tidy"/>), and come back as arguments of
    /// their own after the path. Any other quote inside, such as in a recording's name, stays where it is.
    /// </summary>
    private static string[] SplitOffJoinedOptions(string argument)
    {
        var tidy = argument.Trim().Trim('"').Trim();
        var quote = tidy.IndexOf('"');
        if (quote < 0) return [tidy];

        var after = tidy[(quote + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return after.Length > 0 && after.All(IsOption) ? [tidy[..quote].TrimEnd(), .. after] : [tidy];
    }
}
