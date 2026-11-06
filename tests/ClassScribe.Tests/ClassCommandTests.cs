using System.Text.Json.Nodes;
using ClassScribe.Application.Commands;
using ClassScribe.Application.Settings;
using ClassScribe.Cli.Commands;
using ClassScribe.Infrastructure.Desktop;
using ClassScribe.Infrastructure.Settings;
using ClassScribe.Infrastructure.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClassScribe.Tests;

/// <summary>
/// The class command (layout-config 4.4, build-plan amendments 4 and 11): add, set, rename and remove through the one
/// writer, list and show with --json, and class show's exit codes, which a launcher reads. Every folder is a temporary one.
/// </summary>
[TestFixture]
public class ClassCommandTests
{
    private SettingsFileFixtures _files = null!;
    private TempFolder _recordings = null!;
    private StringWriter _out = null!;
    private StringWriter _error = null!;

    /// <summary>The settings of the program's folder, an empty user folder, a recordings folder, and both outputs kept.</summary>
    [SetUp]
    public void SetUp()
    {
        _files = new SettingsFileFixtures();
        _recordings = new TempFolder();
        _out = new StringWriter();
        _error = new StringWriter();
    }

    /// <summary>Deletes the folders.</summary>
    [TearDown]
    public void TearDown()
    {
        _recordings.Dispose();
        _files.Dispose();
    }

    /// <summary>Runs <paramref name="request"/> through the command and returns the exit code.</summary>
    private int Run(ClassRequest request) =>
        new ClassOutput(new ClassCommand(_files.Store, new FileProbe(), TestOptions.Create(new RecordingOptions { OutputFolder = _recordings.Path }),
            Folders(_recordings.Path), Printed.Commands, NullLogger<ClassCommand>.Instance), new CommandOutput(_out, _error)).RunAsync(request).GetAwaiter().GetResult();

    /// <summary>The recordings' library over <paramref name="recordings"/>, the real class folders.</summary>
    private static ClassFolders Folders(string recordings) =>
        new(TestOptions.Create(new RecordingOptions { OutputFolder = recordings }), TimeProvider.System, NullLogger<ClassFolders>.Instance);

    /// <summary>The lines written to standard output.</summary>
    private string[] Lines => _out.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

    /// <summary>The choices of Gulzar's Mantiq: chrome, the array, following chrome.</summary>
    private static ClassChoices Mantiq => new() { Source = new SourceGiven(SourceOption.App, "chrome"), Mic = true, MicName = "Microphone Array", Follows = "chrome" };

    /// <summary>
    /// class add writes the class under Classes in the user's file, made at this first change as a full copy: its course
    /// copied from its name, its source, microphone and the app it follows; and says the launcher's line.
    /// </summary>
    [Test]
    public void Add_saves_the_class_with_its_course()
    {
        Assert.That(Run(new ClassRequest.Add("Mantiq", Mantiq)), Is.EqualTo(0));

        var fields = _files.UserValue("Classes:Mantiq")!.AsObject();
        Assert.That(fields.Select(field => (field.Key, field.Value!.ToJsonString())), Is.EqualTo(new[]
        {
            ("Course", "\"Mantiq\""), ("Mode", "\"ClassAndMicrophone\""), ("App", "\"chrome\""), ("Microphone", "\"Microphone Array\""), ("MicrophoneFollows", "\"chrome\"")
        }));
        Assert.That(_files.Log.Messages, Has.Some.StartsWith("Saved the class \"Mantiq\" in ").And.Some.EndsWith(": chrome, with your microphone (Microphone Array, following chrome)."));
        Assert.That(_files.Log.Messages, Has.Some.Matches<string>(line => line.StartsWith("From PowerShell: ", StringComparison.Ordinal) && line.EndsWith("record --class \"Mantiq\"", StringComparison.Ordinal)));
        Assert.That(_files.UserValue("Recording:FlushSeconds"), Is.Not.Null, "the file is a full copy");
    }

    /// <summary>
    /// class add with --course, in person with a camera and record-only: the course as given, the camera auto for any, its
    /// size, Transcribe false written as false.
    /// </summary>
    [Test]
    public void Add_in_person_with_a_camera_and_record_only()
    {
        var choices = new ClassChoices { Course = "Kalam", Source = new SourceGiven(SourceOption.InPerson, "OBSBOT"), Camera = true, Resolution = "1080p", Transcribe = false };

        Assert.That(Run(new ClassRequest.Add("Kalam (in person)", choices)), Is.EqualTo(0));

        Assert.That(_files.UserValue("Classes:Kalam (in person)")!.ToJsonString(),
            Is.EqualTo("""{"Course":"Kalam","Mode":"Microphone","Microphone":"OBSBOT","Transcribe":false,"Camera":"auto","CameraResolution":"1080p"}"""));
    }

    /// <summary>
    /// class set --clear camera on a class in person with a camera and its size empties the size too, since a size without
    /// a camera would make the class unusable; the class goes on recording your microphone alone.
    /// </summary>
    [Test]
    public void Clear_camera_empties_its_size_too()
    {
        Run(new ClassRequest.Add("Kalam (in person)", new ClassChoices { Source = new SourceGiven(SourceOption.InPerson, "OBSBOT"), Camera = true, Resolution = "1080p" }));

        Assert.That(Run(new ClassRequest.Set("Kalam (in person)", new ClassChoices { Clear = ["camera"] })), Is.EqualTo(0));

        Assert.That(_files.UserValue("Classes:Kalam (in person)")!.ToJsonString(), Is.EqualTo("""{"Course":"Kalam (in person)","Mode":"Microphone","Microphone":"OBSBOT"}"""));
        Assert.That(_files.Log.Problems, Is.Empty);
    }

    /// <summary>
    /// class add and class set --keyterms save the class's own glossary as given, or none; a glossary that is not there is
    /// said, and saved all the same, since the file may come later; --clear keyterms empties the field, so that the class
    /// takes ElevenLabs:KeytermsFile again. class show --json gives the glossary as a full path, "" for none, null for the
    /// setting's.
    /// </summary>
    [Test]
    public void A_class_names_its_own_glossary_and_clear_keyterms_empties_it()
    {
        Assert.That(Run(new ClassRequest.Add("Mantiq", Mantiq with { Keyterms = "mantiq.txt" })), Is.EqualTo(0));

        var missing = Path.Combine(_files.User.Path, "mantiq.txt");
        Assert.That(_files.UserValue("Classes:Mantiq:KeytermsFile")!.GetValue<string>(), Is.EqualTo("mantiq.txt"));
        Assert.That(_files.Log.Messages, Has.Some.EndsWith(": chrome, with your microphone (Microphone Array, following chrome), glossary mantiq.txt."));
        Assert.That(_files.Log.Messages, Has.Member($"The glossary {missing} is not there yet: the class's transcripts are made without its terms until it is."));
        Assert.That(Run(new ClassRequest.Show("Mantiq", true)), Is.EqualTo(0));
        Assert.That(JsonNode.Parse(_out.ToString())!["glossary"]!.GetValue<string>(), Is.EqualTo(missing));

        File.WriteAllText(missing, "kalām");
        _out.GetStringBuilder().Clear();
        Assert.That(Run(new ClassRequest.Set("Mantiq", new ClassChoices { Keyterms = "none" })), Is.EqualTo(0));
        Assert.That(_files.UserValue("Classes:Mantiq:KeytermsFile")!.GetValue<string>(), Is.EqualTo("none"));
        Assert.That(Run(new ClassRequest.Show("Mantiq", true)), Is.EqualTo(0));
        Assert.That(JsonNode.Parse(_out.ToString())!["glossary"]!.GetValue<string>(), Is.Empty);

        _out.GetStringBuilder().Clear();
        Assert.That(Run(new ClassRequest.Set("Mantiq", new ClassChoices { Clear = ["keyterms"] })), Is.EqualTo(0));
        Assert.That(_files.UserValue("Classes:Mantiq:KeytermsFile"), Is.Null);
        Assert.That(Run(new ClassRequest.Show("Mantiq", true)), Is.EqualTo(0));
        Assert.That(JsonNode.Parse(_out.ToString())!["glossary"], Is.Null);
        Assert.That(_files.Log.Messages.Count(message => message.Contains("is not there yet", StringComparison.Ordinal)), Is.EqualTo(1), "only the missing file is said");
    }

    /// <summary>
    /// A saved class whose glossary value is longer than any path can be (a hand edit; the check puts no limit on it) is
    /// listed and shown all the same, as text and as JSON, with exit 0, which the launcher reads, and nothing on standard
    /// error: the JSON forms give the class, its field as the file holds it, and its glossary as "", the full path of a value
    /// no path can hold, as for none, since it names no file; the run's start and the menu's header say what is wrong with it.
    /// </summary>
    [Test]
    public void A_glossary_longer_than_any_path_still_lists_and_shows_the_class_as_text_and_json()
    {
        var longValue = new string('a', 40_000);
        _files.WriteUser($$"""{ "Classes": { "Mantiq": { "Course": "Mantiq", "App": "chrome", "KeytermsFile": "{{longValue}}" } } }""");

        var listed = Run(new ClassRequest.List(false));
        var listLines = Lines;
        _out.GetStringBuilder().Clear();
        var shown = Run(new ClassRequest.Show("Mantiq", false));
        var showLines = Lines;
        _out.GetStringBuilder().Clear();
        var listedJson = Run(new ClassRequest.List(true));
        var listJson = JsonNode.Parse(_out.ToString())!.AsArray();
        _out.GetStringBuilder().Clear();
        var shownJson = Run(new ClassRequest.Show("Mantiq", true));
        var showJson = JsonNode.Parse(_out.ToString())!;

        Assert.Multiple(() =>
        {
            Assert.That(listed, Is.EqualTo(0));
            Assert.That(listLines, Has.Length.EqualTo(2));
            Assert.That(listLines[0], Is.EqualTo($"Saved classes in {_files.UserFile}:"));
            Assert.That(listLines[1], Does.StartWith("  Mantiq  chrome").And.Contain(longValue));
            Assert.That(shown, Is.EqualTo(0), "the class can be used");
            Assert.That(showLines[0], Does.StartWith("Mantiq: chrome"));
            Assert.That(showLines, Has.Member($"  KeytermsFile = {longValue}"));
            Assert.That(showLines[^1], Does.StartWith("From PowerShell: "));
            Assert.That(listedJson, Is.EqualTo(listed), "the JSON form exits as the text form does");
            Assert.That(listJson, Has.Count.EqualTo(1));
            Assert.That(shownJson, Is.EqualTo(shown));
            foreach (var json in new[] { listJson[0]!, showJson })
            {
                Assert.That(json["name"]!.GetValue<string>(), Is.EqualTo("Mantiq"));
                Assert.That(json["usable"]!.GetValue<bool>(), Is.True);
                Assert.That(json["fields"]!["KeytermsFile"]!.GetValue<string>(), Is.EqualTo(longValue), "the field as the file holds it");
                Assert.That(json["glossary"]!.GetValue<string>(), Is.Empty, "a value no path can hold names no file");
                Assert.That(json["command"]!.GetValue<string>(), Does.EndWith("record --class \"Mantiq\""));
            }

            Assert.That(_error.ToString(), Is.Empty);
        });
    }

    /// <summary>
    /// class add and class set --keyterms with a glossary value longer than any path can be (as menu C's question or an
    /// environment variable in --keyterms could give it) save the class as written, with exit 0, as for any glossary: no
    /// path can hold the value, so no line says that its file is not there yet, and the run's start and the menu's header
    /// say what is wrong with it.
    /// </summary>
    [Test]
    public void Add_and_set_save_a_glossary_longer_than_any_path()
    {
        var longValue = new string('a', 40_000);

        var added = Run(new ClassRequest.Add("Mantiq", Mantiq with { Keyterms = longValue }));
        Run(new ClassRequest.Add("Kalam", new ClassChoices { Source = new SourceGiven(SourceOption.App, "chrome") }));
        var set = Run(new ClassRequest.Set("Kalam", new ClassChoices { Keyterms = longValue }));

        Assert.Multiple(() =>
        {
            Assert.That((added, set), Is.EqualTo((0, 0)));
            Assert.That(_files.UserValue("Classes:Mantiq:KeytermsFile")!.GetValue<string>(), Is.EqualTo(longValue));
            Assert.That(_files.UserValue("Classes:Kalam:KeytermsFile")!.GetValue<string>(), Is.EqualTo(longValue));
            Assert.That(_files.Log.Messages, Has.Some.StartsWith("Changed the class \"Kalam\" in "));
            Assert.That(_files.Log.Messages.Count(message => message.Contains("is not there yet", StringComparison.Ordinal)), Is.Zero);
            Assert.That(_files.Log.Problems, Is.Empty);
            Assert.That(_error.ToString(), Is.Empty);
        });
    }

    /// <summary>
    /// A class that exists already, in any letter case, is refused with exit 2, since names that differ only in case are one
    /// name to the settings; so is a name that cannot be a folder's, and a mix the class cannot take.
    /// </summary>
    [Test]
    public void Add_refuses_a_name_taken_one_that_is_no_folder_name_and_a_mix()
    {
        Run(new ClassRequest.Add("Mantiq", Mantiq));

        Assert.That(Run(new ClassRequest.Add("MANTIQ", Mantiq)), Is.EqualTo(2));
        Assert.That(Run(new ClassRequest.Add("Man:tiq", Mantiq)), Is.EqualTo(2));
        Assert.That(Run(new ClassRequest.Add("Fiqh", new ClassChoices { Follows = "zoom" })), Is.EqualTo(2));
        Assert.That(Run(new ClassRequest.Add("Fiqh", new ClassChoices { Follows = "ClassScribe", Mic = true })), Is.EqualTo(2));

        Assert.That(_files.Log.Problems, Has.Member("There is a saved class \"Mantiq\" already; ClassScribe class set changes it, and class rename renames it."));
        Assert.That(_error.ToString(), Does.Contain("\"Man:tiq\" cannot be a class's name"));
        Assert.That(_files.Log.Problems, Has.Member("--mic-follows goes with a class that records your microphone beside the class: add --mic"));
        Assert.That(_files.Log.Problems, Has.Some.StartsWith("The saved class \"Fiqh\" would not be usable: MicrophoneFollows is \"ClassScribe\""));
        Assert.That(_files.UserValue("Classes:Fiqh"), Is.Null);
    }

    /// <summary>
    /// class add and class set --output with a recordings folder longer than any path can be (as menu C's question or an
    /// environment variable could give it) are refused before anything is written, with exit 2 and the check's words, as a
    /// folder with a character no path may hold is; the class set keeps its folder.
    /// </summary>
    [Test]
    public void Add_and_set_refuse_a_folder_longer_than_any_path()
    {
        var longValue = @"C:\" + new string('a', 39_997);
        Run(new ClassRequest.Add("Kalam", new ClassChoices { Source = new SourceGiven(SourceOption.App, "chrome"), Output = @"D:\Classes" }));

        var added = Run(new ClassRequest.Add("Mantiq", Mantiq with { Output = longValue }));
        var set = Run(new ClassRequest.Set("Kalam", new ClassChoices { Output = longValue }));
        var tab = Run(new ClassRequest.Set("Kalam", new ClassChoices { Output = "D:\\\tClasses" }));

        Assert.Multiple(() =>
        {
            Assert.That((added, set, tab), Is.EqualTo((2, 2, 2)));
            Assert.That(_files.UserValue("Classes:Mantiq"), Is.Null);
            Assert.That(_files.UserValue("Classes:Kalam:OutputFolder")!.GetValue<string>(), Is.EqualTo(@"D:\Classes"));
            Assert.That(_files.Log.Problems, Has.Member(
                $"The saved class \"Mantiq\" would not be usable: OutputFolder names a folder whose full path is 40,000 characters long, starting \"C:\\{new string('a', 37)}...\"; no path on Windows can be longer than 32,766 characters. Give it the folder's own path, such as D:\\Classes, or leave it empty for Recording:OutputFolder."));
            Assert.That(_files.Log.Problems, Has.Some.StartsWith("The saved class \"Kalam\" would not be usable: OutputFolder names a folder whose full path is 40,000 characters long"));
            Assert.That(_files.Log.Problems, Has.Member("The saved class \"Kalam\" would not be usable: OutputFolder holds a tab, a line break or another character that cannot be part of a path."));
            Assert.That(_files.Log.Problems.Count(problem => problem.Contains(longValue, StringComparison.Ordinal)), Is.Zero, "never the whole value");
        });
    }

    /// <summary>
    /// class set changes a class by its options in groups: --app replaces the source and the app followed; --clear empties a
    /// field; fields a later build wrote stay; an unknown class is exit 2 with the saved ones.
    /// </summary>
    [Test]
    public void Set_changes_a_class_in_groups_and_keeps_fields_of_a_later_build()
    {
        Run(new ClassRequest.Add("Mantiq", Mantiq with { Output = @"D:\Classes" }));
        var tree = _files.UserTree;
        tree["Classes"]!["Mantiq"]!["MicrophoneSpeaker"] = "Me";
        _files.WriteUser(tree.ToJsonString());

        Assert.That(Run(new ClassRequest.Set("mantiq", new ClassChoices { Source = new SourceGiven(SourceOption.App, "zoom"), Clear = ["output"] })), Is.EqualTo(0));

        var fields = _files.UserValue("Classes:Mantiq")!.AsObject();
        Assert.That(fields["App"]!.GetValue<string>(), Is.EqualTo("zoom"));
        Assert.That(fields["Microphone"]!.GetValue<string>(), Is.EqualTo("Microphone Array"), "the microphone stays");
        Assert.That(fields.ContainsKey("MicrophoneFollows"), Is.False, "it follows the app recorded now");
        Assert.That(fields.ContainsKey("OutputFolder"), Is.False);
        Assert.That(fields["MicrophoneSpeaker"]!.GetValue<string>(), Is.EqualTo("Me"), "a later build's field stays");
        Assert.That(Run(new ClassRequest.Set("Kalam", new ClassChoices { Output = @"D:\Classes" })), Is.EqualTo(2));
        Assert.That(_files.Log.Problems, Has.Member("There is no saved class \"Kalam\". The saved classes are: \"Mantiq\"."));
    }

    /// <summary>
    /// A changed course says that the next class goes to a new folder with new file names and a new Drive folder, that
    /// continue will not offer the old course's recordings, and where those are when its folder is there.
    /// </summary>
    [Test]
    public void A_changed_course_says_what_follows()
    {
        Run(new ClassRequest.Add("Mantiq (Chrome)", Mantiq with { Course = "Mantiq" }));
        Directory.CreateDirectory(_recordings.File("Mantiq"));

        Assert.That(Run(new ClassRequest.Set("Mantiq (Chrome)", new ClassChoices { Course = "Mantiq Chrome" })), Is.EqualTo(0));

        Assert.That(_files.Log.Messages, Has.Member(
            "The next class of Mantiq (Chrome) goes to a new folder, Mantiq Chrome\\..., with new file names and a new folder on Google Drive; continue will not offer recordings of Mantiq."));
        Assert.That(_files.Log.Messages, Has.Member($"The recordings of Mantiq stay where they are, in {_recordings.File("Mantiq")}."));
    }

    /// <summary>
    /// A saved class whose own recordings folder is longer than any path can be (a hand edit) cannot be used, as one with a
    /// character no path may hold cannot: it is listed with exit 0 and shown with exit 2, which the launcher reads, as text
    /// and as JSON, with the check's words and nothing on standard error, rather than "could not be read"; class set
    /// --course on it changes the course all the same, since the problem was there before, and says what follows without the
    /// line about where the old course's recordings are, since the folder names no place.
    /// </summary>
    [Test]
    public void A_class_folder_longer_than_any_path_lists_shows_unusable_and_changes_its_course()
    {
        var longValue = new string('a', 40_000);
        _files.WriteUser($$"""{ "Classes": { "Mantiq": { "Course": "Mantiq", "App": "chrome", "OutputFolder": "{{longValue}}" } } }""");

        var listed = Run(new ClassRequest.List(false));
        var shown = Run(new ClassRequest.Show("Mantiq", false));
        var showLines = Lines;
        _out.GetStringBuilder().Clear();
        var listedJson = Run(new ClassRequest.List(true));
        var listJson = JsonNode.Parse(_out.ToString())!.AsArray();
        _out.GetStringBuilder().Clear();
        var shownJson = Run(new ClassRequest.Show("Mantiq", true));
        var showJson = JsonNode.Parse(_out.ToString())!;
        var changed = Run(new ClassRequest.Set("Mantiq", new ClassChoices { Course = "Mantiq Chrome" }));

        Assert.Multiple(() =>
        {
            Assert.That((listed, shown, listedJson, shownJson, changed), Is.EqualTo((0, 2, 0, 2, 0)));
            Assert.That(showLines, Has.Member($"  OutputFolder = {longValue}"));
            Assert.That(string.Join("\n", showLines), Does.Contain("OutputFolder names a folder whose full path is "));
            Assert.That(listJson, Has.Count.EqualTo(1));
            foreach (var json in new[] { listJson[0]!, showJson })
            {
                Assert.That(json["name"]!.GetValue<string>(), Is.EqualTo("Mantiq"));
                Assert.That(json["usable"]!.GetValue<bool>(), Is.False);
                Assert.That(json["fields"]!["OutputFolder"]!.GetValue<string>(), Is.EqualTo(longValue));
            }

            Assert.That(_files.UserValue("Classes:Mantiq:Course")!.GetValue<string>(), Is.EqualTo("Mantiq Chrome"));
            Assert.That(_files.Log.Messages, Has.Some.StartsWith("The next class of Mantiq goes to a new folder, Mantiq Chrome\\..."));
            Assert.That(_files.Log.Messages.Count(message => message.Contains("stay where they are", StringComparison.Ordinal)), Is.Zero);
            Assert.That(_files.Log.Problems, Is.Empty);
            Assert.That(_error.ToString(), Is.Empty);
        });
    }

    /// <summary>
    /// class rename changes only the name, keeps the course, and says the launcher's line with the new name; a name another
    /// class has, in any case, is refused; class remove takes the class away, and the empty section with it.
    /// </summary>
    [Test]
    public void Rename_keeps_the_course_and_remove_takes_the_class_away()
    {
        Run(new ClassRequest.Add("Mantiq", Mantiq));
        Run(new ClassRequest.Add("Fiqh", new ClassChoices { Source = new SourceGiven(SourceOption.App, "chrome") }));

        Assert.That(Run(new ClassRequest.Rename("Mantiq", "Mantiq (Chrome)")), Is.EqualTo(0));
        Assert.That(_files.UserValue("Classes:Mantiq (Chrome):Course")!.GetValue<string>(), Is.EqualTo("Mantiq"));
        Assert.That(_files.UserValue("Classes:Mantiq"), Is.Null);
        Assert.That(_files.Log.Messages, Has.Some.EndsWith("record --class \"Mantiq (Chrome)\""));
        Assert.That(Run(new ClassRequest.Rename("Fiqh", "mantiq (chrome)")), Is.EqualTo(2));

        Assert.That(Run(new ClassRequest.Remove("FIQH")), Is.EqualTo(0));
        Assert.That(Run(new ClassRequest.Remove("Mantiq (Chrome)")), Is.EqualTo(0));
        Assert.That(_files.UserValue("Classes"), Is.Null, "the empty section goes");
        Assert.That(Run(new ClassRequest.Remove("Mantiq")), Is.EqualTo(2));
    }

    /// <summary>
    /// class list writes a line per class, what it records or why it cannot be used; with --json, JSON alone with each
    /// class's name, whether it can be used, its problems, its fields, its words and its command.
    /// </summary>
    [Test]
    public void List_writes_each_class_and_json()
    {
        Run(new ClassRequest.Add("Mantiq", Mantiq));
        var tree = _files.UserTree;
        tree["Classes"]!["Broken"] = new JsonObject { ["Course"] = "Broken", ["App"] = "chrome", ["Device"] = "Speakers" };
        _files.WriteUser(tree.ToJsonString());

        Assert.That(Run(new ClassRequest.List(false)), Is.EqualTo(0));
        Assert.That(Lines, Is.EqualTo(new[]
        {
            $"Saved classes in {_files.UserFile}:",
            "  Broken  cannot be used: App and Device are both set; a class records one app or one output device.",
            "  Mantiq  chrome, with your microphone (Microphone Array, following chrome)"
        }));

        _out.GetStringBuilder().Clear();
        Assert.That(Run(new ClassRequest.List(true)), Is.EqualTo(0));
        var listed = JsonNode.Parse(_out.ToString())!.AsArray();
        Assert.That(listed.Select(entry => (entry!["name"]!.GetValue<string>(), entry["usable"]!.GetValue<bool>())), Is.EqualTo(new[] { ("Broken", false), ("Mantiq", true) }));
        Assert.That(listed[1]!["fields"]!["App"]!.GetValue<string>(), Is.EqualTo("chrome"));
        Assert.That(listed[1]!["command"]!.GetValue<string>(), Does.EndWith("record --class \"Mantiq\""));
        Assert.That(listed[0]!["command"], Is.Null);
    }

    /// <summary>
    /// class show answers the launcher (amendment 4): 0 for a class that can be used, whatever the problems outside Classes,
    /// which a recording drops; 2 for one that cannot, or that is not there, or while the file is missing; 1 for a file that
    /// is not valid JSON, which nothing can be read from.
    /// </summary>
    [Test]
    public void Show_answers_the_launcher_with_its_exit_code()
    {
        Run(new ClassRequest.Add("Mantiq", Mantiq));
        Assert.That(Run(new ClassRequest.Show("mantiq", false)), Is.EqualTo(0));
        Assert.That(Lines[0], Is.EqualTo("Mantiq: chrome, with your microphone (Microphone Array, following chrome)"));
        Assert.That(Lines, Has.Member("  App = chrome"));

        _files.WriteUser(_files.UserText.Replace("\"FlushSeconds\": 5", "\"FlushSeconds\": 0", StringComparison.Ordinal));
        Assert.That(Run(new ClassRequest.Show("Mantiq", true)), Is.EqualTo(0), "a problem outside Classes: the recording would drop it");
        Assert.That(Run(new ClassRequest.Show("Kalam", false)), Is.EqualTo(2));

        var tree = _files.UserTree;
        tree["Classes"]!["Mantiq"]!["Device"] = "Speakers";
        _files.WriteUser(tree.ToJsonString());
        Assert.That(Run(new ClassRequest.Show("Mantiq", false)), Is.EqualTo(2), "the class itself cannot be used");

        _files.WriteUser("{ not json");
        Assert.That(Run(new ClassRequest.Show("Mantiq", false)), Is.EqualTo(1));

        File.Delete(_files.UserFile);
        var places = new SettingsPlaces(_files.Program.Path, _files.User.Path, _files.State.Path);
        var store = new SettingsStore(places, new SettingsFile(places, NullLogger<SettingsFile>.Instance), _files.Reloader, _files.Writer);
        var missing = new ClassOutput(new ClassCommand(store, new FileProbe(), TestOptions.Create(new RecordingOptions()), Folders(_recordings.Path), Printed.Commands, NullLogger<ClassCommand>.Instance),
            new CommandOutput(_out, _error));
        Assert.That(missing.RunAsync(new ClassRequest.Show("Mantiq", false)).Result, Is.EqualTo(2), "missing, with the marker");
        Assert.That(_error.ToString(), Does.Contain("is missing"));
    }
}
