using ClassScribe.Settings;
using Microsoft.Extensions.Logging;

namespace ClassScribe.Commands;

/// <summary>What the gate is opened for, which decides what it says when the settings cannot be used.</summary>
public enum GateUse
{
    /// <summary>A choice in the menu, which comes back afterwards.</summary>
    MenuChoice,

    /// <summary>A command given on the command line, which then ends.</summary>
    Command
}

/// <summary>
/// The settings check before each menu choice and each command. It reads appsettings.json again, so an edit made since
/// the last choice counts, and lets the choice or command run only once the file has passed the check. When it has not,
/// it says what is wrong, and the settings stay as they were.
/// </summary>
public sealed class RunGate(SettingsReloader reloader, SettingsFile file, ILogger<RunGate> logger)
{
    private readonly Lock _gate = new();

    /// <summary>The warnings given so far: each is given once per process, however often the settings are read.</summary>
    private readonly HashSet<string> _warned = new(StringComparer.Ordinal);

    /// <summary>
    /// What kept the settings from being used when they were last read, at start or for the last choice or command, one
    /// sentence per problem; empty while they can be used. The menu shows it in its header, so a file that cannot be used
    /// is plain before any choice is made, at start too, when no gate has said anything yet.
    /// </summary>
    public IReadOnlyList<string> Problems => reloader.Last is { Usable: false } last ? last.Problems : [];

    /// <summary>
    /// While the user's own settings file is missing (layout-config 1.4), what is said about it; null otherwise. The menu
    /// shows it in its header as an error.
    /// </summary>
    public string? Missing => reloader.Last?.Missing;

    /// <summary>The settings the user's own file holds at a default a later build changed, which the menu's header counts (see <see cref="DefaultChanges"/>).</summary>
    public IReadOnlyList<DefaultChange> OlderDefaults => reloader.OlderDefaultsHeld();

    /// <summary>
    /// Reads the settings again and returns true when the choice or command may run with them. Otherwise it logs each
    /// problem as an error, then what to do, and returns false. A command run from the development layout also hears,
    /// once, that the copy of the settings it reads is older than the project's own. The menu says that itself, in its
    /// header and, when it comes about while the menu is shown, before the choice runs (see <see cref="MenuCommand"/>).
    /// While the user's own file is missing, the defaults are used and that is said first, once.
    /// </summary>
    public bool TryOpen(GateUse use)
    {
        if (use == GateUse.Command && file.DescribeStaleCopy() is { } stale) WarnOnce(stale);

        var verdict = reloader.TryReload();
        if (verdict.Missing is { } missing) WarnOnce(missing);
        foreach (var warning in verdict.Warnings) WarnOnce(warning);
        if (verdict.Usable) return true;

        foreach (var problem in verdict.Problems) logger.LogError("{Problem}", problem);
        if (use == GateUse.MenuChoice)
            logger.LogError("Nothing was started. Fix the settings above in {Path}, then choose again", verdict.Path);
        else
            logger.LogError("Nothing was started. Fix the settings above in {Path}, then run the command again", verdict.Path);

        return false;
    }

    /// <summary>Logs <paramref name="warning"/> unless it has been given before.</summary>
    private void WarnOnce(string warning)
    {
        lock (_gate)
        {
            if (!_warned.Add(warning)) return;
        }

        logger.LogWarning("{Warning}", warning);
    }
}
