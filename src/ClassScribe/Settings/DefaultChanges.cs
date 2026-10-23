using ClassScribe.Audio.Microphones;

namespace ClassScribe.Settings;

/// <summary>A default a build changed: the setting, the default before, the default since, and the version that changed it.</summary>
/// <param name="Key">The setting, such as Recording:MicrophoneFollows.</param>
/// <param name="OldDefault">The default before, as the settings file writes it.</param>
/// <param name="NewDefault">The default since, as the settings file writes it.</param>
/// <param name="Version">
/// The version that changed it, such as 0.7.0; empty on a feature branch, since a version is given only when a release branch
/// is cut, whose version commit fills the empty rows (build-plan amendment 48). An empty one is shown without a version.
/// </param>
public sealed record DefaultChange(string Key, string OldDefault, string NewDefault, string Version);

/// <summary>
/// The defaults later builds changed (build-plan amendment 38). The user's settings file is a full copy of every setting, so a
/// default a later build changes does not reach it: a value equal to an old default may be a choice, and ClassScribe never
/// changes it by itself. Instead the menu's header says how many settings hold an older default, settings list marks each
/// with the newer one, and settings reset takes it.
/// </summary>
public static class DefaultChanges
{
    /// <summary>
    /// Every default changed, oldest first. The first: Recording:MicrophoneFollows went from chrome, which fits one laptop
    /// only, to auto (N9). The second: ElevenLabs:KeytermsFile went from keyterms.txt, the glossary shipped beside the
    /// program, to empty, no glossary, since none is shipped now (architecture 8.2, item 2); a glossary of one's own is named
    /// by its own path, such as %APPDATA%\ClassScribe\keyterms-islamic-sciences.txt, which is never an old default.
    /// </summary>
    public static IReadOnlyList<DefaultChange> Rows { get; } =
    [
        new("Recording:MicrophoneFollows", "chrome", FollowRule.AnyWord, Version: ""),
        new("ElevenLabs:KeytermsFile", "keyterms.txt", "", Version: "")
    ];

    /// <summary>
    /// The settings among <paramref name="values"/>, a settings file's values by setting name, that hold an older default
    /// of <paramref name="rows"/>, or of <see cref="Rows"/>: equal, in any case and trimmed, to a row's old default and not
    /// to that setting's latest default. Each comes with its latest row, which says the newer default and since when.
    /// </summary>
    public static IReadOnlyList<DefaultChange> HeldIn(IReadOnlyDictionary<string, string?> values, IReadOnlyList<DefaultChange>? rows = null)
    {
        rows ??= Rows;
        var held = new List<DefaultChange>();
        foreach (var key in rows.Select(row => row.Key).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var latest = rows.Last(row => row.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
            var value = values.FirstOrDefault(pair => pair.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Value?.Trim();
            if (value is null || Same(value, latest.NewDefault)) continue;

            if (rows.Any(row => row.Key.Equals(key, StringComparison.OrdinalIgnoreCase) && Same(value, row.OldDefault))) held.Add(latest);
        }

        return held;
    }

    /// <summary>
    /// What settings list says after such a setting's value: the newer default, an empty one written as "", and since which
    /// version when the row has one.
    /// </summary>
    public static string Describe(DefaultChange change)
    {
        var newer = change.NewDefault.Length == 0 ? "\"\"" : change.NewDefault;
        return change.Version.Length == 0 ? $"newer default: {newer}" : $"newer default: {newer} (since {change.Version})";
    }

    /// <summary>The menu header's line for <paramref name="count"/> settings that hold an older default; null for none.</summary>
    public static string? HeaderLine(int count) => count switch
    {
        0 => null,
        1 => "1 setting holds an older default; settings list shows it",
        _ => $"{count} settings hold an older default; settings list shows them"
    };

    /// <summary>Two values as a settings file writes them, compared as a setting reads them: in any case.</summary>
    private static bool Same(string value, string other) => value.Equals(other.Trim(), StringComparison.OrdinalIgnoreCase);
}
