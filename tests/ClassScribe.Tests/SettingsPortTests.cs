using ClassScribe.Recorder;
using ClassScribe.Settings;
using ClassScribe.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ClassScribe.Tests;

/// <summary>
/// The contracts of the ports of settings and classes (architecture 4.3, slice 3b3) over their real adapters, in temporary
/// folders that stand for the program's folder, %APPDATA%\ClassScribe and %LOCALAPPDATA%\ClassScribe: where the settings
/// are, the file's bytes, a file's bytes staged and checked, the changes through the one writer, the environment override
/// and the recorder's snapshot (<see cref="ISettingsStore"/> over <see cref="SettingsStore"/>, <see cref="IStagedSettings"/>
/// over <see cref="StagedSettings"/>); and that the window's host resolves the port to a store over the very adapters it
/// holds, while the recorder's has none.
/// </summary>
[TestFixture]
public class SettingsPortTests
{
    private SettingsFileFixtures _files = null!;

    /// <summary>The settings of the program's folder, an empty user folder and ClassScribe's own folder, all temporary.</summary>
    [SetUp]
    public void SetUp() => _files = new SettingsFileFixtures();

    /// <summary>Deletes the folders.</summary>
    [TearDown]
    public void TearDown() => _files.Dispose();

    /// <summary>The store under test: the real adapter over the fixture's places, file, reloader and writer.</summary>
    private ISettingsStore Store => _files.Store;

    /// <summary>
    /// Where the settings are follows the places: the file beside the program until the first change, then the user's own,
    /// with the source read now, the paths and the folder as the places give them, and nothing missing.
    /// </summary>
    [Test]
    public void Where_the_settings_are_follows_the_places()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Store.Current(), Is.EqualTo(new SettingsSource(SettingsOrigin.BesideTheProgram, _files.ProgramFile, _files.ProgramFile)));
            Assert.That(Store.Origin, Is.EqualTo(SettingsOrigin.BesideTheProgram));
            Assert.That(Store.SettingsPath, Is.EqualTo(_files.ProgramFile));
            Assert.That(Store.ProgramFile, Is.EqualTo(_files.ProgramFile));
            Assert.That(Store.UserFile, Is.EqualTo(_files.UserFile));
            Assert.That(Store.UserFolder, Is.EqualTo(_files.Places.UserFolder));
            Assert.That(Store.Folder, Is.EqualTo(_files.Places.ProgramFolder));
            Assert.That(Store.DescribeMissing(), Is.Null);
            Assert.That(Store.FilePath, Is.EqualTo(_files.UserFile));
        });

        Assert.That(Store.Set("Recording:FlushSeconds", "7").ExitCode, Is.EqualTo(0));

        Assert.Multiple(() =>
        {
            Assert.That(Store.Current(), Is.EqualTo(new SettingsSource(SettingsOrigin.UserFolder, _files.UserFile, _files.UserFile)));
            Assert.That(Store.Origin, Is.EqualTo(SettingsOrigin.UserFolder));
            Assert.That(Store.SettingsPath, Is.EqualTo(_files.UserFile));
            Assert.That(Store.Folder, Is.EqualTo(_files.Places.UserFolder));
            Assert.That(_files.UserValue("Recording:FlushSeconds")!.GetValue<int>(), Is.EqualTo(7));
        });
    }

    /// <summary>
    /// A user's file that is gone after ClassScribe made it is the missing-file state to a store of a new process: said in
    /// the places' words, the defaults beside the program read in its stead, and every change refused with it.
    /// </summary>
    [Test]
    public void A_missing_user_file_is_said_in_the_places_words()
    {
        Store.Set("Recording:FlushSeconds", "7");
        File.Delete(_files.UserFile);
        var places = new SettingsPlaces(_files.Program.Path, _files.User.Path, _files.State.Path);
        var file = new SettingsFile(places, NullLogger<SettingsFile>.Instance);
        ISettingsStore store = new SettingsStore(places, file, _files.Reloader, new SettingsWriter(places, file, _files.Reloader, _files.Log));

        Assert.That(store.Origin, Is.EqualTo(SettingsOrigin.Missing));
        Assert.That(store.DescribeMissing(), Is.EqualTo(places.DescribeMissing()).And.StartWith($"Your settings file {_files.UserFile} is missing."));
        Assert.That(store.Current().ReadPath, Is.EqualTo(_files.ProgramFile), "the defaults beside the program stand in");
        var fill = store.Fill();
        Assert.That((fill.ExitCode, fill.Lines.Single()), Is.EqualTo((1, places.DescribeMissing()!)));
    }

    /// <summary>
    /// The file the source names is read with its origin, and its bytes staged over the live sources give the values in
    /// force by name in any case, the file's own values, a section's children in the settings' order (sorted by key, as
    /// the host's configuration gives them), and the saved classes checked.
    /// </summary>
    [Test]
    public void The_file_is_read_and_its_bytes_staged_with_the_values_in_force()
    {
        Store.Set("Recording:FlushSeconds", "7");
        Store.Change("a saved class", document =>
        {
            document.Set("Classes:Mantiq:Course", "Mantiq");
            document.Set("Classes:Mantiq:App", "chrome");
            return SettingsEdit.Changed("saved");
        });

        var text = Store.Read(Store.Current());
        Assert.That((text.Path, text.Origin), Is.EqualTo((_files.UserFile, SettingsOrigin.UserFolder)));
        Assert.That(text.Bytes, Is.EqualTo(File.ReadAllBytes(_files.UserFile)));

        using var staged = Store.Stage(text.Bytes);
        Assert.Multiple(() =>
        {
            Assert.That(staged.Readable, Is.True);
            Assert.That(staged.Unreadable, Is.Empty);
            Assert.That(staged.Problems, Is.Empty);
            Assert.That(staged.Warnings, Is.Empty);
            Assert.That(staged["Recording:FlushSeconds"], Is.EqualTo("7"), "the file's value");
            Assert.That(staged["recording:flushseconds"], Is.EqualTo("7"), "in any case");
            Assert.That(staged["Logging:LogLevel:Default"], Is.EqualTo("Information"));
            Assert.That(staged["Recording"], Is.Null, "a section has no value");
            Assert.That(staged["Recording:NoSuchSetting"], Is.Null);
            Assert.That(staged.Values["Recording:FlushSeconds"], Is.EqualTo("7"));
            Assert.That(staged.Values["Classes:Mantiq:App"], Is.EqualTo("chrome"));
            Assert.That(staged.Children("Classes").Select(child => child.Key), Is.EqualTo(new[] { "Mantiq" }));
            Assert.That(staged.Children("classes:mantiq"), Is.EqualTo(new[] { KeyValuePair.Create("App", (string?)"chrome"), KeyValuePair.Create("Course", (string?)"Mantiq") }));
            Assert.That(staged.Children("Logging:LogLevel").Select(child => child.Key), Is.EqualTo(new[] { "Default", "Microsoft", "System" }));
            Assert.That(staged.Children("Recording").Select(child => child.Key), Has.Member("FlushSeconds"));
            Assert.That(staged.Children("Logging").Select(child => child.Value), Has.All.Null, "children that are sections");
            Assert.That(staged.Children("Nowhere"), Is.Empty);
            Assert.That(staged.Classes.Select(entry => (entry.Name, entry.Usable)), Is.EqualTo(new[] { ("Mantiq", true) }));
            Assert.That(staged.ProblemsOf("mantiq"), Is.Empty);
        });
    }

    /// <summary>
    /// Bytes that are not a settings file stage as unreadable, with the reloader's words and nothing in force; no bytes at
    /// all stage as no file, readable, with the defaults.
    /// </summary>
    [Test]
    public void Bytes_that_are_not_a_settings_file_stage_as_unreadable()
    {
        using var staged = Store.Stage("{ not json"u8.ToArray());
        Assert.Multiple(() =>
        {
            Assert.That(staged.Readable, Is.False);
            Assert.That(staged.Unreadable, Has.Count.EqualTo(2));
            Assert.That(staged.Unreadable[0], Does.StartWith("The mistake is on line 1:"));
            Assert.That(staged.Unreadable[1], Is.EqualTo(SettingsReloader.JsonPathAdvice));
            Assert.That(staged["Recording:FlushSeconds"], Is.Null);
            Assert.That(staged.Children("Recording"), Is.Empty);
            Assert.That(staged.Classes, Is.Empty);
            Assert.That(staged.Values, Is.Empty);
            Assert.That(staged.Problems, Is.Empty);
        });

        using var none = Store.Stage(null);
        Assert.That(none.Readable, Is.True, "no file: the defaults");
        Assert.That(none["Logging:LogLevel:Default"], Is.EqualTo("Information"));
        Assert.That(none["Recording:FlushSeconds"], Is.Null);

        using var empty = Store.Stage(" \n"u8.ToArray());
        Assert.That(empty.Unreadable, Is.EqualTo(new[] { "appsettings.json is empty. Write { } in it at least; the settings it leaves out then keep their defaults." }));
    }

    /// <summary>
    /// Every change goes through the one writer: an edit of the document, set, reset, several at once, fill and reset
    /// --all reach the user's file, with the writer's lines and exit codes, and the key is refused in its words.
    /// </summary>
    [Test]
    public void Changes_go_through_the_one_writer()
    {
        var change = Store.Change("a test's change", document =>
        {
            document.Set("Recording:FlushSeconds", 9);
            return SettingsEdit.Changed("set to 9");
        });
        Assert.That(change.ExitCode, Is.EqualTo(0));
        Assert.That(change.Lines, Has.Member("set to 9").And.Some.StartsWith("Your settings are now kept in "));
        Assert.That(_files.UserValue("Recording:FlushSeconds")!.GetValue<int>(), Is.EqualTo(9));
        Assert.That(_files.Log.Messages, Has.Member("set to 9"), "logged by the writer");

        Assert.That(Store.Reset("Recording:FlushSeconds").ExitCode, Is.EqualTo(0));
        Assert.That(_files.UserValue("Recording:FlushSeconds")!.GetValue<int>(), Is.EqualTo(5));

        Assert.That(Store.SetAll([("Recording:FlushSeconds", ["8"]), ("Recording:StatusIntervalSeconds", ["90"])]).ExitCode, Is.EqualTo(0));
        Assert.That((_files.UserValue("Recording:FlushSeconds")!.GetValue<int>(), _files.UserValue("Recording:StatusIntervalSeconds")!.GetValue<int>()), Is.EqualTo((8, 90)));

        var fill = Store.Fill();
        Assert.That((fill.ExitCode, fill.Lines.Count), Is.EqualTo((0, 0)), "a full copy lacks nothing");

        Assert.That(Store.ResetAll().ExitCode, Is.EqualTo(0));
        Assert.That(_files.UserValue("Recording:FlushSeconds")!.GetValue<int>(), Is.EqualTo(5));

        var key = Store.Set("ElevenLabs:ApiKey", "sk-1");
        Assert.That((key.ExitCode, key.Lines.Single()), Is.EqualTo((2, SettingsCatalog.ApiKeyRefused)));
        var refused = Store.Change("a refused change", document =>
        {
            document.Set("Recording:FlushSeconds", 0);
            return SettingsEdit.Changed("set to 0");
        });
        Assert.That(refused.ExitCode, Is.EqualTo(2), "a change that adds a problem is refused in the check's words");
        Assert.That(refused.Lines, Is.EqualTo(new[] { "Recording:FlushSeconds is 0; it must be 1 or more.", "Nothing was changed." }));
        Assert.That(_files.UserValue("Recording:FlushSeconds")!.GetValue<int>(), Is.EqualTo(5));
    }

    /// <summary>
    /// The environment variable that overrides a setting is named when one is set; the snapshot a recorder is given holds
    /// the sections as they are now, never the key or the saved classes; and a file a setting names is resolved from the
    /// folder given, environment variables expanded and quotes ignored, empty for an empty setting.
    /// </summary>
    [Test]
    public void The_environment_override_the_snapshot_and_a_file_a_setting_names()
    {
        using var variable = new TemporaryVariable("Recording__SilenceWarningSeconds", "30");
        Assert.That(Store.EnvironmentOverride("Recording:SilenceWarningSeconds"), Is.EqualTo("Recording__SilenceWarningSeconds"));
        Assert.That(Store.EnvironmentOverride("recording:silencewarningseconds"), Is.EqualTo("Recording__SilenceWarningSeconds"), "in any case");
        Assert.That(Store.EnvironmentOverride("Recording:FlushSeconds"), Is.Null);

        Store.Set("Recording:FlushSeconds", "7");
        Store.Change("a saved class", document =>
        {
            document.Set("Classes:Mantiq:Course", "Mantiq");
            return SettingsEdit.Changed("saved");
        });
        var snapshot = Store.Snapshot();
        Assert.That(snapshot["Recording:FlushSeconds"], Is.EqualTo("7"));
        Assert.That(snapshot["Logging:LogLevel:Default"], Is.EqualTo("Information"));
        Assert.That(snapshot.Keys, Has.None.StartsWith("ElevenLabs:ApiKey").And.None.StartsWith("Classes"));

        using var folder = new TemporaryVariable("CLASSSCRIBE_TEST_GLOSSARIES", _files.User.Path);
        Assert.That(Store.ResolveFile("mantiq.txt", _files.User.Path), Is.EqualTo(Path.Combine(_files.User.Path, "mantiq.txt")));
        Assert.That(Store.ResolveFile(" \"%CLASSSCRIBE_TEST_GLOSSARIES%\\terms.txt\" ", _files.Program.Path), Is.EqualTo(Path.Combine(_files.User.Path, "terms.txt")));
        Assert.That(Store.ResolveFile("", _files.User.Path), Is.EqualTo(""));
    }

    /// <summary>
    /// The window's host resolves the port to one store for the process, over the writer and the reloader the host itself
    /// holds: a change through it reaches the settings every part reads. The recorder's host has no store, as it has no
    /// writer: a recorder records with the snapshot its window handed over.
    /// </summary>
    [Test]
    public void The_window_resolves_the_store_over_its_own_adapters_and_the_recorder_has_none()
    {
        using var temp = new TempFolder();
        using var runtime = new TestRuntime();
        File.WriteAllText(temp.File(SettingsFile.FileName), "{}");
        var places = new SettingsPlaces(temp.Path, temp.File("User"), temp.File("State"));
        using var window = AppHost.Build(places, "test-key", services => services.AddSingleton(runtime.Folder).AddSingleton(new RoamingFolder(temp.File("Roaming"))));
        var runId = TestRuns.Id();
        using var log = new RecorderLog(runtime.Time);
        using var recorder = AppHost.BuildRecorder(new RecorderStart(runId, TestRuns.Request(runId), new Dictionary<string, string?>()), "test-key", log, runtime.Folder, places, runtime.Time);

        var store = window.Services.GetRequiredService<ISettingsStore>();
        Assert.That(store, Is.TypeOf<SettingsStore>().And.SameAs(window.Services.GetRequiredService<ISettingsStore>()));
        Assert.That(store.SettingsPath, Is.EqualTo(places.SettingsPath));
        Assert.That(store.Set("Recording:FlushSeconds", "7").ExitCode, Is.EqualTo(0));
        Assert.That(window.Services.GetRequiredService<IOptionsMonitor<RecordingOptions>>().CurrentValue.FlushSeconds, Is.EqualTo(7), "written and reloaded through the host's own writer and reloader");
        Assert.That(window.Services.GetRequiredService<SettingsReloader>().Last!.Path, Is.EqualTo(places.UserFile));
        Assert.That(recorder.Services.GetService<ISettingsStore>(), Is.Null, "a recorder never writes the settings");
    }
}
