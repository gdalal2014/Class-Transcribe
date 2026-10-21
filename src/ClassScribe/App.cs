using ClassScribe.Commands;
using Microsoft.Extensions.Logging;

namespace ClassScribe;

/// <summary>
/// Runs ClassScribe's command line. The arguments are tidied and parsed first, so a mistake is written with that command's
/// help and ends with exit code 2 before anything else happens. A command then runs only once the settings, read again,
/// have passed the check (see <see cref="RunGate"/>), and after the report at the start has been printed (see
/// <see cref="IStartupReport"/>). Help needs neither; the menu reads the settings before each choice and shows the report
/// itself; showing a recording in progress is never held up by the settings; and settings and class, commands of their own
/// kind (<see cref="CommandKind.Settings"/>), run without either, since they must be able to fix a file the gate refuses.
/// </summary>
public sealed class App(CommandLineTree commandLine, RunGate gate, IStartupReport report, ILogger<App> logger)
{
    /// <summary>The exit code of a command that did not run because the settings cannot be used.</summary>
    private const int RefusedBySettings = 1;

    /// <summary>The exit code of a command that failed.</summary>
    private const int Failed = 1;

    /// <summary>The exit code of a command cancelled with Ctrl+C.</summary>
    private const int Cancelled = 130;

    /// <summary>Runs the command line <paramref name="args"/> and returns the process exit code.</summary>
    public async Task<int> RunAsync(string[] args)
    {
        try
        {
            var outcome = commandLine.Parse(commandLine.Tidy(args));
            if (outcome is not { Parsed: { } parsed, ExitCode: null }) return outcome.ExitCode ?? CommandLineTree.Mistake;

            if (commandLine.KindOf(parsed) == CommandKind.Command)
            {
                if (!gate.TryOpen(GateUse.Command)) return RefusedBySettings;

                await report.ShowAsync(interactive: false);
            }

            return await commandLine.InvokeAsync(parsed);
        }
        catch (OperationCanceledException)
        {
            // Deliberately not rethrown: Ctrl+C during the upload is a normal way to leave, and it is reported as such,
            // with this warning and exit code 130.
            logger.LogWarning("Cancelled");
            return Cancelled;
        }
        catch (Exception ex)
        {
            // Deliberately not rethrown: this is the last stop before the process exits, where a failure becomes this log
            // line and exit code 1.
            logger.LogError(ex, "ClassScribe stopped because of an error");
            return Failed;
        }
    }
}
