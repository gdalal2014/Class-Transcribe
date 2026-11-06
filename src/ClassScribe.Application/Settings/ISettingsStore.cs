namespace ClassScribe.Settings;

/// <summary>
/// The settings as the window keeps them (architecture 4.3): where they are read from, decided at every read (the user's
/// own appsettings.json, the file beside the program, or the missing-file state); the file's bytes; the settings a file's
/// bytes would give, staged over the live sources and checked, which settings list, settings get and the class command
/// read the values in force from; every change of the user's file, through the one writer, which refuses a change that
/// adds a problem; the environment variable that overrides a setting; and the snapshot a separate recorder is given. What
/// it takes and gives are the settings' records (<see cref="SettingsSource"/>, <see cref="SettingsText"/>,
/// <see cref="SettingsDocument"/>, <see cref="SettingsEdit"/>, <see cref="SettingsChange"/>) and .NET's own types. A
/// recorder has none: it never writes the settings, and records with the snapshot its window handed over.
/// </summary>
public interface ISettingsStore
{
    /// <summary>
    /// Which file is read now: the user's own when it exists, or when this process has read it before (it is then gone,
    /// which the reloader refuses rather than going back to the file beside the program); the missing-file state when
    /// ClassScribe made the user's file before and it is not there; else the file beside the program.
    /// </summary>
    SettingsSource Current();

    /// <summary>The settings file the person knows as theirs now (see <see cref="SettingsSource.Path"/>).</summary>
    string SettingsPath { get; }

    /// <summary>Which file is read now (see <see cref="Current"/>).</summary>
    SettingsOrigin Origin { get; }

    /// <summary>The appsettings.json beside the program.</summary>
    string ProgramFile { get; }

    /// <summary>The user's own appsettings.json, whether or not it is there; null without a user folder.</summary>
    string? UserFile { get; }

    /// <summary>The user's folder, whether or not it holds a settings file; null when there is none, as in most tests.</summary>
    string? UserFolder { get; }

    /// <summary>The folder whose appsettings.json is read now: the user's, or the program's, which also stands in for a missing user file.</summary>
    string Folder { get; }

    /// <summary>
    /// What is said while the user's file is missing (layout-config 1.4): the menu's header shows it as an error, every
    /// command's first line as a warning, and every change is refused with it. Null while it is not missing.
    /// </summary>
    string? DescribeMissing();

    /// <summary>
    /// Where a file a setting names is, such as the glossary of ElevenLabs:KeytermsFile or of a saved class: environment
    /// variables such as %APPDATA% expanded, quotes around the path ignored, and a relative path taken from
    /// <paramref name="folder"/> and from nowhere else: the user's folder for a value the settings writer is about to write
    /// there, at its first change too, before the user's file is read. An empty setting gives an empty path, which means no
    /// file was wanted. Throws as a path that cannot be made does.
    /// </summary>
    string ResolveFile(string configured, string folder);

    /// <summary>
    /// Reads the file <paramref name="source"/> names, as <see cref="Current"/> decided it a moment before, sharing it with
    /// any program that has it open; a file another program holds for a moment is tried again, and the last try's
    /// <see cref="IOException"/> is handed on. Without a file there are no settings of the file's own, and
    /// <see cref="SettingsText.Bytes"/> is null; when <paramref name="expectFile"/> is true, because the file was read
    /// before, a missing file is looked for again the same way first.
    /// </summary>
    SettingsText Read(SettingsSource source, bool expectFile = false);

    /// <summary>
    /// What a settings file holding <paramref name="bytes"/> would give, through the reloader's own parse, staging and
    /// check, without accepting any of it (layout-config 1.6 step 5). Null bytes stand for no file. The caller disposes
    /// the result.
    /// </summary>
    IStagedSettings Stage(byte[]? bytes);

    /// <summary>The file the writer changes: the user's own, made at the first change; the file beside the program when there is no user folder.</summary>
    string FilePath { get; }

    /// <summary>
    /// Changes the user's file by <paramref name="edit"/>, described as <paramref name="what"/> in the log, through the one
    /// writer: one change at a time, the result checked before anything is written, a change that adds a problem refused
    /// in the check's words, the file written whole with the bytes before kept as one undo, the settings reloaded, and the
    /// change logged. <paramref name="freshCopy"/> starts from the defaults instead of the file, for settings reset --all,
    /// which alone may make the file while it is missing or not valid JSON. Returns what came of it.
    /// </summary>
    SettingsChange Change(string what, Func<SettingsDocument, SettingsEdit> edit, bool freshCopy = false);

    /// <summary>
    /// settings set: sets <paramref name="name"/> to <paramref name="values"/>, written as the setting's kind (a number,
    /// true or false, an enum's own name, text, or a list's items). An entry of a map is set by its key path. Refused with
    /// exit 2: ElevenLabs:ApiKey, an unknown name (with the nearest known one), a value of the wrong kind, and a value the
    /// check refuses, in its words.
    /// </summary>
    SettingsChange Set(string name, params string[] values);

    /// <summary>Sets several settings in one change, each as <see cref="Set"/> sets it, all written together or none.</summary>
    SettingsChange SetAll(IReadOnlyList<(string Name, string[] Values)> settings);

    /// <summary>
    /// settings reset: puts <paramref name="name"/> back at this build's default, written into the file; an entry of a map
    /// without a default of its own is removed. Refused with exit 2 for ElevenLabs:ApiKey and an unknown name.
    /// </summary>
    SettingsChange Reset(string name);

    /// <summary>
    /// settings reset --all: a fresh full copy of every setting at this build's default, the saved classes kept, the file
    /// before kept as appsettings.previous.json. It alone may replace a file that is missing or not valid JSON.
    /// </summary>
    SettingsChange ResetAll();

    /// <summary>
    /// Adds to the user's file every setting this build knows and the file lacks, at its default, saying so a line each
    /// (settings fill). With no user file yet it writes nothing, exit 0; while the user's file is missing it is refused with
    /// exit 1, as every change is.
    /// </summary>
    SettingsChange Fill();

    /// <summary>The settings environment variable that overrides <paramref name="name"/>, such as Recording__OutputFolder, when one is set; null otherwise.</summary>
    string? EnvironmentOverride(string name);

    /// <summary>
    /// The settings a separate recorder is given: the Recording, ElevenLabs, Transcript, Drive, YouTube and Logging
    /// sections as they are now, overrides from environment variables included, by setting name, such as
    /// Recording:FlushSeconds. Never the whole of the settings, which hold every environment variable, and never the key:
    /// ElevenLabs:ApiKey is left out, and so is any value that holds the key.
    /// </summary>
    IReadOnlyDictionary<string, string?> Snapshot();
}
