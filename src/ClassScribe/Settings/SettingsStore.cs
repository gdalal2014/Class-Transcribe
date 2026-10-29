namespace ClassScribe.Settings;

/// <summary>
/// The window's settings store (see <see cref="ISettingsStore"/>): where the settings are, from the places; the file's
/// bytes, from the settings file; the staged settings and the snapshot for a recorder, from the reloader; and every change,
/// through the one writer. It holds no code of its own: each member forwards to the class that has always held it, so a
/// change to any of them reaches the use cases unchanged.
/// </summary>
/// <param name="places">Where the settings are.</param>
/// <param name="file">The settings file the places name.</param>
/// <param name="reloader">Stages a file's values as they would be used, and gives the recorder's snapshot.</param>
/// <param name="writer">The one writer of the user's file.</param>
public sealed class SettingsStore(SettingsPlaces places, SettingsFile file, SettingsReloader reloader, SettingsWriter writer) : ISettingsStore
{
    /// <inheritdoc />
    public SettingsSource Current() => places.Current();

    /// <inheritdoc />
    public string SettingsPath => places.SettingsPath;

    /// <inheritdoc />
    public SettingsOrigin Origin => places.Origin;

    /// <inheritdoc />
    public string ProgramFile => places.ProgramFile;

    /// <inheritdoc />
    public string? UserFile => places.UserFile;

    /// <inheritdoc />
    public string? UserFolder => places.UserFolder;

    /// <inheritdoc />
    public string Folder => places.Folder;

    /// <inheritdoc />
    public string? DescribeMissing() => places.DescribeMissing();

    /// <inheritdoc />
    public string ResolveFile(string configured, string folder) => SettingsPlaces.ResolveFile(configured, folder);

    /// <inheritdoc />
    public SettingsText Read(SettingsSource source, bool expectFile = false) => file.Read(source, expectFile);

    /// <inheritdoc />
    public IStagedSettings Stage(byte[]? bytes) => reloader.Stage(bytes);

    /// <inheritdoc />
    public string FilePath => writer.FilePath;

    /// <inheritdoc />
    public SettingsChange Change(string what, Func<SettingsDocument, SettingsEdit> edit, bool freshCopy = false) => writer.Change(what, edit, freshCopy);

    /// <inheritdoc />
    public SettingsChange Set(string name, params string[] values) => writer.Set(name, values);

    /// <inheritdoc />
    public SettingsChange SetAll(IReadOnlyList<(string Name, string[] Values)> settings) => writer.SetAll(settings);

    /// <inheritdoc />
    public SettingsChange Reset(string name) => writer.Reset(name);

    /// <inheritdoc />
    public SettingsChange ResetAll() => writer.ResetAll();

    /// <inheritdoc />
    public SettingsChange Fill() => writer.Fill();

    /// <inheritdoc />
    public string? EnvironmentOverride(string name) => SettingsWriter.EnvironmentOverride(name);

    /// <inheritdoc />
    public IReadOnlyDictionary<string, string?> Snapshot() => reloader.Snapshot();
}
