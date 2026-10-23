using System.Text;
using System.Text.Json.Nodes;
using ClassScribe.Settings;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClassScribe.Tests;

/// <summary>
/// The settings files of one test, in temporary folders: the program's folder with its appsettings.json, the user's folder
/// (empty until the first change), and ClassScribe's own folder for the marker and the lock; with the places over them, the
/// settings file, the reloader with a real check and the writer, as the app puts them together. Never the real %APPDATA% or
/// %LOCALAPPDATA%.
/// </summary>
public sealed class SettingsFileFixtures : IDisposable
{
    /// <summary>The settings beside the program unless a test gives others: the shipped file's own, with a Recording section.</summary>
    public const string ShippedJson = """{ "Recording": { "OutputFolder": "%USERPROFILE%\\ClassScribe", "FlushSeconds": 5 } }""";

    /// <summary>Writes <paramref name="programJson"/> beside the program and puts the settings together over the three folders, then reads them once, as the app does at start.</summary>
    public SettingsFileFixtures(string programJson = ShippedJson, bool overridden = false)
    {
        File.WriteAllText(Program.File("appsettings.json"), programJson);
        Places = new SettingsPlaces(Program.Path, User.Path, overridden ? null : State.Path, overridden);
        SettingsFile = new SettingsFile(Places, NullLogger<SettingsFile>.Instance);
        var accepted = new CheckedSettingsSource();
        Configuration = new ConfigurationManager();
        ((IConfigurationBuilder)Configuration).AddInMemoryCollection(DefaultSettings.Values);
        ((IConfigurationBuilder)Configuration).Add(accepted);
        Reloader = new SettingsReloader(Configuration, accepted, SettingsFile, new SettingsCheck(), NullLogger<SettingsReloader>.Instance);
        Reloader.TryReload();
        Writer = new SettingsWriter(Places, SettingsFile, Reloader, Log);
    }

    /// <summary>The program's folder.</summary>
    public TempFolder Program { get; } = new();

    /// <summary>The user's folder, %APPDATA%\ClassScribe's stand-in.</summary>
    public TempFolder User { get; } = new();

    /// <summary>ClassScribe's own folder, %LOCALAPPDATA%\ClassScribe's stand-in, for the marker and the lock.</summary>
    public TempFolder State { get; } = new();

    /// <summary>The places over the three folders.</summary>
    public SettingsPlaces Places { get; }

    /// <summary>The settings file the places name.</summary>
    public SettingsFile SettingsFile { get; }

    /// <summary>The settings as the app holds them: the Logging defaults, then what last passed the check.</summary>
    public ConfigurationManager Configuration { get; }

    /// <summary>What reads the file again and checks it.</summary>
    public SettingsReloader Reloader { get; }

    /// <summary>The writer, logging to <see cref="Log"/>.</summary>
    public SettingsWriter Writer { get; }

    /// <summary>What the writer logged.</summary>
    public RecordingLogger<SettingsWriter> Log { get; } = new();

    /// <summary>The user's own file.</summary>
    public string UserFile => User.File("appsettings.json");

    /// <summary>The file beside the program.</summary>
    public string ProgramFile => Program.File("appsettings.json");

    /// <summary>The user's file before the last change.</summary>
    public string PreviousFile => User.File("appsettings.previous.json");

    /// <summary>The user's file's text.</summary>
    public string UserText => File.ReadAllText(UserFile, Encoding.UTF8);

    /// <summary>The user's file as a tree, its header comment passed over.</summary>
    public JsonObject UserTree => JsonNode.Parse(UserText, documentOptions: new System.Text.Json.JsonDocumentOptions { CommentHandling = System.Text.Json.JsonCommentHandling.Skip })!.AsObject();

    /// <summary>The value at <paramref name="name"/> in the user's file, such as Recording:OutputFolder; null when it is not there.</summary>
    public JsonNode? UserValue(string name) => new SettingsDocument(UserTree).Get(name);

    /// <summary>Writes the user's own file as <paramref name="text"/>, as a hand edit or another program would.</summary>
    public void WriteUser(string text) => File.WriteAllText(UserFile, text);

    /// <summary>Deletes the folders.</summary>
    public void Dispose()
    {
        State.Dispose();
        User.Dispose();
        Program.Dispose();
    }
}

/// <summary>
/// What the check is told about folders on the computer, set by the test: the folders that exist, the drives' labels and
/// the folders Dropbox keeps in sync. Nothing on this computer is looked at.
/// </summary>
public sealed class FakeOutputFolderFacts : IOutputFolderFacts
{
    /// <summary>The full paths of the folders that exist.</summary>
    public HashSet<string> Folders { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Each drive's label by its root, such as G:\.</summary>
    public Dictionary<string, string> Labels { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The folders Dropbox's info.json names.</summary>
    public List<string> Dropbox { get; } = [];

    /// <inheritdoc />
    public bool HasFolder(string folder, string name) => Folders.Contains(Path.Combine(folder, name));

    /// <inheritdoc />
    public string? VolumeLabel(string root) => Labels.GetValueOrDefault(root);

    /// <inheritdoc />
    public IReadOnlyList<string> DropboxFolders() => Dropbox;
}
