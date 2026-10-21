using Microsoft.Extensions.Configuration;

namespace ClassScribe.Settings;

/// <summary>
/// A settings file's bytes as they would be used, staged over the live sources (environment variables and the key
/// included) and checked, without any of it being accepted (see <see cref="SettingsReloader.Stage(byte[])"/>). Disposing it lets
/// the staged settings go.
/// </summary>
public sealed class StagedSettings : IDisposable
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

    /// <summary>A file that cannot be read as a settings file, for <paramref name="why"/>.</summary>
    internal static StagedSettings Unusable(IReadOnlyList<string> why) =>
        new(why, new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase), configuration: null, [], []);

    /// <summary>Lets the staged settings go.</summary>
    public void Dispose() => (Configuration as IDisposable)?.Dispose();
}
