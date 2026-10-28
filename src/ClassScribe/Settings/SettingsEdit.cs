namespace ClassScribe.Settings;

/// <summary>What an edit of the settings file decided: the lines to say, and whether it refuses, with which exit code.</summary>
/// <param name="Lines">What is said: what changed, or why nothing was.</param>
/// <param name="ExitCode">0 to write the change; 2 for a mistake in what was asked, such as an unknown class; 1 for anything else that refuses it.</param>
public sealed record SettingsEdit(IReadOnlyList<string> Lines, int ExitCode = 0)
{
    /// <summary>The settings the edit changed, by their full names, so that one an environment variable overrides is pointed out.</summary>
    public IReadOnlyList<string> Keys { get; init; } = [];

    /// <summary>The saved class the edit makes or changes, by its name after the edit; null when it changes none.</summary>
    public string? ClassName { get; init; }

    /// <summary>The edit made its change, said in <paramref name="lines"/>.</summary>
    public static SettingsEdit Changed(params string[] lines) => new(lines);

    /// <summary>The edit refuses, for <paramref name="why"/>, with exit code <paramref name="exitCode"/>: nothing is written.</summary>
    public static SettingsEdit Refused(string why, int exitCode = 2) => new([why], exitCode);
}

/// <summary>What a change of the settings file came to: its exit code, and what was said.</summary>
/// <param name="ExitCode">0 when the file holds the change (or held it already); 2 for a mistake in what was asked or a value the check refuses; 1 for a file that cannot be changed.</param>
/// <param name="Lines">What was said, in the order it was said.</param>
public sealed record SettingsChange(int ExitCode, IReadOnlyList<string> Lines);
