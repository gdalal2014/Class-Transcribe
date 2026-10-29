using Microsoft.Extensions.Configuration;

namespace ClassScribe.Settings;

/// <summary>
/// A settings file's bytes as they would be used, staged over the live sources (environment variables and the key
/// included) and checked, without any of it being accepted (see <see cref="SettingsReloader.Stage(byte[])"/>). Disposing it lets
/// the staged settings go.
/// </summary>
public sealed class StagedSettings : IStagedSettings
{
    /// <summary>Holds what the reloader staged and found.</summary>
    internal StagedSettings(IReadOnlyList<string> unreadable, IReadOnlyDictionary<string, string?> values, IConfigurationRoot? configuration,
        IReadOnlyList<SettingsProblem> problems, IReadOnlyList<string> warnings)
    {
        Unreadable = unreadable;
        Values = values;
        Configuration = configuration;
        Problems = problems;
        Warnings = warnings;
    }

    /// <summary>Why the bytes cannot be read as a settings file, as the reloader says it; empty when they can.</summary>
    public IReadOnlyList<string> Unreadable { get; }

    /// <summary>True when the bytes can be read as a settings file.</summary>
    public bool Readable => Unreadable.Count == 0;

    /// <summary>The file's own values, by setting name; none when it cannot be read.</summary>
    public IReadOnlyDictionary<string, string?> Values { get; }

    /// <summary>The settings as they would be with this file; null when it cannot be read.</summary>
    public IConfigurationRoot? Configuration { get; }

    /// <summary>What the check finds wrong with them, each with the settings it names.</summary>
    public IReadOnlyList<SettingsProblem> Problems { get; }

    /// <summary>What the check would warn about.</summary>
    public IReadOnlyList<string> Warnings { get; }

    /// <summary>The saved classes the file holds, each checked on its own (see <see cref="SavedClasses"/>); none when it cannot be read.</summary>
    public IReadOnlyList<SavedClassEntry> Classes => Configuration is { } settings ? SavedClasses.Read(settings) : [];

    /// <summary>Why the saved class <paramref name="name"/> cannot be used, in any case; empty when it can, or is not there.</summary>
    public IReadOnlyList<string> ProblemsOf(string name) =>
        Classes.FirstOrDefault(entry => entry.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase))?.Problems ?? [];

    /// <inheritdoc />
    public string? this[string key] => Configuration?[key];

    /// <inheritdoc />
    public IReadOnlyList<KeyValuePair<string, string?>> Children(string key) =>
        Configuration is { } settings ? [.. settings.GetSection(key).GetChildren().Select(child => KeyValuePair.Create(child.Key, child.Value))] : [];

    /// <summary>A file that cannot be read as a settings file, for <paramref name="why"/>.</summary>
    internal static StagedSettings Unusable(IReadOnlyList<string> why) =>
        new(why, new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase), configuration: null, [], []);

    /// <summary>Lets the staged settings go.</summary>
    public void Dispose() => (Configuration as IDisposable)?.Dispose();
}
