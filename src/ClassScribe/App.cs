using ClassScribe.Commands;
using Microsoft.Extensions.Logging;

namespace ClassScribe;

/// <summary>Reads the command line and runs the matching command, or the menu when there is none.</summary>
public sealed class App(ICommandRunner commands, MenuCommand menu, RecordingNamePrompt prompt, IConsoleUi ui, ILogger<App> logger)
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

        Without a name, record and record-only ask for one.
        While recording, Q twice (or Ctrl+C) stops, P pauses or resumes, and M marks a moment.
        The API key is read from the ELEVENLABS_API_KEY environment variable.
        """;

    /// <summary>The command names. They are taken in any case.</summary>
    private static readonly string[] CommandNames = ["record", "record-only", "transcribe", "help", "--help", "-h", "/?"];

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
                    return await prompt.AskAsync() is { } asked ? await commands.RecordAsync(asked, transcribe: arguments[0] == "record") : 0;

                case ["record", var given]:
                    return await commands.RecordAsync(given, transcribe: true);

                case ["record-only", var given]:
                    return await commands.RecordAsync(given, transcribe: false);

                case ["transcribe", var path]:
                    return await commands.TranscribeAsync(path);

                case ["help" or "--help" or "-h" or "/?"]:
                    ui.WriteLine(Usage);
                    return 0;

                case [var path] when File.Exists(path) || Directory.Exists(path):
                    return await commands.TranscribeAsync(path);

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
    /// Tidies the command line: a command name is taken in any case, and quotes around an argument go.
    /// Windows PowerShell 5.1 hands a quoted path that ends in a backslash over with a stray quote at
    /// the end, and its tab completion writes a folder whose path has a space just that way; a quote
    /// cannot be part of a Windows path anyway.
    /// </summary>
    private static string[] Tidy(string[] args)
    {
        string[] tidy = [.. args.Select(argument => argument.Trim().Trim('"').Trim())];
        if (tidy is [var command, ..] && CommandNames.Contains(command, StringComparer.OrdinalIgnoreCase))
            tidy[0] = command.ToLowerInvariant();

        return tidy;
    }
}
