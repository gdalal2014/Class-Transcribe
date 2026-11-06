namespace ClassScribe.Settings;

/// <summary>A setting a recording left out of a settings file that failed the check (build-plan amendment 4).</summary>
/// <param name="Key">The setting, such as Recording:FlushSeconds.</param>
/// <param name="Refused">The value the file gave it, which the check refused.</param>
/// <param name="InPlace">The value the recording uses in its place: an environment variable's, or the default.</param>
public sealed record DroppedSetting(string Key, string? Refused, string InPlace)
{
    /// <summary>What a recording's first lines say of it, after the file's path.</summary>
    public string Describe(string path) => $"{Key} is \"{Refused}\" in {path}, which the check refuses; this recording uses {InPlace} in its place.";
}

/// <summary>What reading the settings again found: whether they can be used, what is wrong if not, and which file was read.</summary>
public sealed record SettingsVerdict(bool Usable, IReadOnlyList<string> Problems, string Path)
{
    /// <summary>What is worth saying about the file without refusing it, such as an API key written into it.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>
    /// While the user's own file is missing (see <see cref="SettingsOrigin.Missing"/>), what is said about it: the defaults
    /// beside the program were read for now, and no change is written. Null otherwise.
    /// </summary>
    public string? Missing { get; init; }

    /// <summary>
    /// For a recording, the settings left out of a file that failed the check, each with the value it gave and the one used
    /// instead (build-plan amendment 4); the file's problems stay in <see cref="Problems"/>, for the menu's header. Empty
    /// when nothing was left out.
    /// </summary>
    public IReadOnlyList<DroppedSetting> Dropped { get; init; } = [];

    /// <summary>
    /// For a recording, why nothing of the file could be taken (not valid JSON, unreadable, gone), and that the settings last
    /// read, or the defaults, and the command line are used instead; empty otherwise.
    /// </summary>
    public IReadOnlyList<string> Kept { get; init; } = [];
}
