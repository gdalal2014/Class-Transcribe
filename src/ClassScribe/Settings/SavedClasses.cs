using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace ClassScribe.Settings;

/// <summary>
/// The saved classes under Classes in the settings (layout-config 4.1 and 4.2), read from the settings in force at each use,
/// as the gate last accepted them. Each class is checked on its own: a problem in one class makes only that class unusable,
/// and is named in the menu's header and by class list, while the other classes and the rest of the file go on. Names are
/// looked up ignoring case, exactly, never by a prefix, since .NET binds them case-sensitively while Windows' users do not
/// type them so.
/// </summary>
/// <param name="configuration">The settings in force.</param>
/// <param name="file">The settings file, read by <see cref="FindInFile"/>; null for none, as in most tests.</param>
/// <param name="reloader">Stages the file's bytes for <see cref="FindInFile"/>; null for none.</param>
/// <param name="logger">Where a file that cannot be read for <see cref="FindInFile"/> is noted; null for nowhere.</param>
public sealed class SavedClasses(IConfiguration configuration, SettingsFile? file = null, SettingsReloader? reloader = null, ILogger<SavedClasses>? logger = null) : ISavedClasses
{
    /// <inheritdoc />
    public IReadOnlyList<SavedClassEntry> All() => Read(configuration);

    /// <inheritdoc />
    public SavedClassEntry? Find(string name) => All().FirstOrDefault(entry => entry.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The saved class named <paramref name="name"/>, ignoring case and spaces around it, as the settings file holds it now,
    /// staged and checked as the reloader does, each class on its own. For N5's hint on the command line, which is given
    /// before the gate reads the file for a recording: until then the settings in force are what the first read accepted,
    /// none of the file's values when a setting outside Classes is refused, while record --class would record the class,
    /// since a recording drops only that setting. Null when there is none, when there is no file to read (as in most
    /// tests), and when the file cannot be read now or is not valid JSON.
    /// </summary>
    public SavedClassEntry? FindInFile(string name)
    {
        if (file is null || reloader is null) return null;

        try
        {
            using var staged = reloader.Stage(file.Read().Bytes);
            return staged.Classes.FirstOrDefault(entry => entry.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Deliberately not rethrown: the class is looked for only to add a hint to a mistake, which is said without it
            // while the file cannot be read.
            logger?.LogDebug(ex, "Could not read {Path} for the saved classes", file.Path);
            return null;
        }
    }

    /// <inheritdoc />
    public Dictionary<string, string?>? FieldsOf(string name) =>
        configuration.GetSection(SettingsCatalog.ClassesSection).GetChildren().FirstOrDefault(child => child.Key.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase)) is { } section
            ? new Dictionary<string, string?>(Fields(section), StringComparer.OrdinalIgnoreCase)
            : null;

    /// <inheritdoc />
    public IReadOnlyList<SavedClassEntry> Matching(string typed)
    {
        if (Find(typed) is { } named) return [named];

        return [.. All().Where(entry => entry.Class?.Course.Equals(typed.Trim(), StringComparison.OrdinalIgnoreCase) == true)];
    }

    /// <summary>The saved classes in <paramref name="settings"/>, each checked on its own (see <see cref="SavedClass.Check"/>).</summary>
    public static IReadOnlyList<SavedClassEntry> Read(IConfiguration settings) =>
        [.. settings.GetSection(SettingsCatalog.ClassesSection).GetChildren().Select(child => SavedClass.Check(child.Key, Fields(child)))];

    /// <summary>The fields of one class's section, by name, ignoring case; a field holding a list or an object is passed over.</summary>
    private static IReadOnlyDictionary<string, string?> Fields(IConfigurationSection section) =>
        section.GetChildren().ToDictionary(child => child.Key, child => child.Value, StringComparer.OrdinalIgnoreCase);
}
