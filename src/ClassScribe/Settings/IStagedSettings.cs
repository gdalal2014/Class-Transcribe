namespace ClassScribe.Settings;

/// <summary>
/// A settings file's bytes as they would be used, staged over the live sources (environment variables and the key
/// included) and checked, without any of it being accepted (see <see cref="ISettingsStore.Stage"/>): what the settings
/// writer checks a change with before writing it, and what settings list, settings get and the class command read the
/// values in force from. Disposing it lets the staged settings go.
/// </summary>
public interface IStagedSettings : IDisposable
{
    /// <summary>Why the bytes cannot be read as a settings file, as the reloader says it; empty when they can.</summary>
    IReadOnlyList<string> Unreadable { get; }

    /// <summary>True when the bytes can be read as a settings file.</summary>
    bool Readable { get; }

    /// <summary>The file's own values, by setting name; none when it cannot be read.</summary>
    IReadOnlyDictionary<string, string?> Values { get; }

    /// <summary>What the check finds wrong with them, each with the settings it names.</summary>
    IReadOnlyList<SettingsProblem> Problems { get; }

    /// <summary>What the check would warn about.</summary>
    IReadOnlyList<string> Warnings { get; }

    /// <summary>The saved classes the file holds, each checked on its own (see <see cref="SavedClassEntry"/>); none when it cannot be read.</summary>
    IReadOnlyList<SavedClassEntry> Classes { get; }

    /// <summary>Why the saved class <paramref name="name"/> cannot be used, in any case; empty when it can, or is not there.</summary>
    IReadOnlyList<string> ProblemsOf(string name);

    /// <summary>
    /// The value in force at <paramref name="key"/>, a setting's full name such as Recording:FlushSeconds, in any case, as
    /// the staged settings hold it with the live sources over the file; null for a setting that is not set, for a section,
    /// and when the bytes cannot be read.
    /// </summary>
    string? this[string key] { get; }

    /// <summary>
    /// The children of the section at <paramref name="key"/>, such as Classes or Logging:LogLevel, each by its own key
    /// and its value (null for a child that is a section itself), in the order the settings give them; none when there
    /// are none, or the bytes cannot be read.
    /// </summary>
    IReadOnlyList<KeyValuePair<string, string?>> Children(string key);
}
