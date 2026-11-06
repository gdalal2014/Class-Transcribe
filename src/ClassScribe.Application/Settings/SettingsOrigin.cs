namespace ClassScribe.Settings;

/// <summary>Which settings file ClassScribe reads now: the user's own, the one beside the program, or none while the user's own is missing.</summary>
public enum SettingsOrigin
{
    /// <summary>The user's own file, in the user's folder: %APPDATA%\ClassScribe, unless CLASSSCRIBE_SETTINGS_FOLDER names another.</summary>
    UserFolder,

    /// <summary>The file beside the program: the shipped defaults once installed, the build's copy under dotnet run, or a portable copy's own.</summary>
    BesideTheProgram,

    /// <summary>
    /// ClassScribe made the user's own file once (the marker says so), and it is not there now: deleted or renamed by
    /// mistake, or on a redirected %APPDATA% that is offline. The defaults beside the program are read for what is done now,
    /// with a warning, and no change is written, since a new file would hide the loss (layout-config 1.4).
    /// </summary>
    Missing
}

/// <summary>The settings file read now, as <see cref="ISettingsStore.Current"/> decides it.</summary>
/// <param name="Origin">Which file it is.</param>
/// <param name="Path">The file the person knows as theirs: the user's own file, even while it is missing, or the one beside the program.</param>
/// <param name="ReadPath">The file whose values are read: the user's own, or the one beside the program, which stands in for a missing user file.</param>
public sealed record SettingsSource(SettingsOrigin Origin, string Path, string ReadPath);
