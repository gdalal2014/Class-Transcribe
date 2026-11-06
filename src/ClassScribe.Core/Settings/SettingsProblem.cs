namespace ClassScribe.Settings;

/// <summary>
/// One problem the check found: the sentence that says it, starting with the setting's name, and the settings it names,
/// which a recording drops from a file that fails the check while it keeps the rest (build-plan amendment 4). A name may be
/// a section, such as Logging:Console, which stands for every setting under it.
/// </summary>
/// <param name="Text">The sentence.</param>
/// <param name="Keys">The settings it names, the first being the one the sentence starts with.</param>
public sealed record SettingsProblem(string Text, IReadOnlyList<string> Keys);
