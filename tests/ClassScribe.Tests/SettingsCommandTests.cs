using System.Text.Json.Nodes;
using ClassScribe.Commands;
using ClassScribe.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ClassScribe.Tests;

/// <summary>
/// The settings command itself (layout-config 1.8, build-plan amendments 25 and 38): path, list and get, with --json; set,
/// reset, fill and open through the writer; what goes to standard output and what to standard error; and the key never
/// shown. Every folder is a temporary one.
/// </summary>
[TestFixture]
public class SettingsCommandTests
{
    private SettingsFileFixtures _files = null!;
    private Mock<IDesktop> _desktop = null!;
    private StringWriter _out = null!;
    private StringWriter _error = null!;

    /// <summary>The settings of the program's folder and an empty user folder, the desktop mocked, and both outputs kept.</summary>
    [SetUp]
    public void SetUp()
    {
        _files = new SettingsFileFixtures();
        _desktop = new Mock<IDesktop>();
        _out = new StringWriter();
        _error = new StringWriter();
    }

    /// <summary>Deletes the folders.</summary>
    [TearDown]
    public void TearDown() => _files.Dispose();

    /// <summary>The command under test, over the fixture's settings.</summary>
    private SettingsCommand Command() =>
        new(_files.Writer, _files.Places, _files.SettingsFile, _files.Reloader, _desktop.Object, new CommandOutput(_out, _error), NullLogger<SettingsCommand>.Instance);

    /// <summary>Runs <paramref name="request"/> and returns the exit code.</summary>
    private int Run(SettingsRequest request) => Command().RunAsync(request).GetAwaiter().GetResult();

    /// <summary>The lines written to standard output.</summary>
    private string[] Lines => _out.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

    /// <summary>path writes the file read, alone on its line: beside the program until the first change, then the user's own.</summary>
    [Test]
    public void Path_writes_the_file_alone()
    {
        Assert.That(Run(new SettingsRequest.ShowPath()), Is.EqualTo(0));
        _files.Writer.Set("Recording:FlushSeconds", "7");
        Assert.That(Run(new SettingsRequest.ShowPath()), Is.EqualTo(0));

        Assert.That(Lines, Is.EqualTo(new[] { _files.ProgramFile, _files.UserFile }));
        Assert.That(_error.ToString(), Is.Empty);
    }

    /// <summary>
    /// list writes a line per setting, a map's entries one by one: its value, and in brackets the default when the value
    /// differs, or the environment variable that sets it; never the key.
    /// </summary>
    [Test]
    public void List_writes_a_line_per_setting()
    {
        _files.Writer.Set("Recording:FlushSeconds", "7");
        using var variable = new TemporaryVariable("Recording__SilenceWarningSeconds", "30");

        Assert.That(Run(new SettingsRequest.List(false)), Is.EqualTo(0));

        Assert.That(Lines[0], Is.EqualTo($"Settings in {_files.UserFile}:"));
        Assert.That(Lines, Has.Member("Recording:FlushSeconds = 7  (default 5)"));
        Assert.That(Lines, Has.Member("Recording:StatusIntervalSeconds = 60"));
        Assert.That(Lines, Has.Member("Logging:LogLevel:Default = Information"));
        Assert.That(Lines, Has.Member("YouTube:CourseChannels = "));
        Assert.That(Lines, Has.None.StartsWith("ElevenLabs:ApiKey"));
        Assert.That(Lines.Length, Is.EqualTo(SettingsCatalog.Keys.Sum(key => key.Kind == SettingKind.Map ? ((JsonObject)key.Default!).Count : 1) + 1));
    }

    /// <summary>
    /// A user's file that holds a default a later build changed shows the newer one beside it, in words and in the JSON's
    /// newerDefault (amendment 38); ClassScribe changes nothing by itself.
    /// </summary>
    [Test]
    public void An_older_default_is_marked_with_the_newer()
    {
        _files.WriteUser("""{ "Recording": { "MicrophoneFollows": "chrome" } }""");

        Run(new SettingsRequest.List(false));
        Run(new SettingsRequest.List(true));

        Assert.That(Lines, Has.Member("Recording:MicrophoneFollows = chrome  (default auto; newer default: auto)"));
        var row = JsonNode.Parse(Lines[^1])!.AsArray().Single(item => item!["key"]!.GetValue<string>() == "Recording:MicrophoneFollows")!;
        Assert.That(row["newerDefault"]!.GetValue<string>(), Is.EqualTo("auto"));
        Assert.That(row["value"]!.GetValue<string>(), Is.EqualTo("chrome"), "never changed by itself");
    }

    /// <summary>
    /// list --json writes the JSON alone on standard output, one object per setting (key, value, default, source,
    /// newerDefault), every character beyond ASCII escaped so that ConvertFrom-Json reads it whatever the code page.
    /// </summary>
    [Test]
    public void List_json_is_JSON_alone_in_ASCII()
    {
        _files.Writer.Set("YouTube:MayDownloadChannels", "@Ṣughrā");
        _files.Writer.Set("Recording:FlushSeconds", "7");
        using var variable = new TemporaryVariable("Recording__SilenceWarningSeconds", "30");

        Assert.That(Run(new SettingsRequest.List(true)), Is.EqualTo(0));

        var text = _out.ToString();
        Assert.That(text.All(character => character < 128), Is.True, "ASCII only");
        var rows = JsonNode.Parse(text)!.AsArray().Select(row => row!.AsObject()).ToList();
        var flush = rows.Single(row => row["key"]!.GetValue<string>() == "Recording:FlushSeconds");
        Assert.That(flush.ToJsonString(), Is.EqualTo("""{"key":"Recording:FlushSeconds","value":7,"default":5,"source":"file","newerDefault":null}"""));
        Assert.That(rows.Single(row => row["key"]!.GetValue<string>() == "Recording:SilenceWarningSeconds")["source"]!.GetValue<string>(), Is.EqualTo("environment"));
        Assert.That(rows.Single(row => row["key"]!.GetValue<string>() == "YouTube:MayDownloadChannels")["value"]![0]!.GetValue<string>(), Is.EqualTo("@Ṣughrā"));
        Assert.That(rows.Single(row => row["key"]!.GetValue<string>() == "YouTube:MayDownloadChannels")["source"]!.GetValue<string>(), Is.EqualTo("file"),
            "a list the file holds comes from the file, though the settings hold only its items, by number");
        Assert.That(_error.ToString(), Is.Empty);
    }

    /// <summary>
    /// get writes one setting's value alone: text as it is, a list's items a line each, a map's entries as name=value; with
    /// --json as a JSON value. A name in any case works.
    /// </summary>
    [Test]
    public void Get_writes_the_value_alone()
    {
        _files.Writer.Set("YouTube:CourseChannels", "@one", "@two");

        Run(new SettingsRequest.Get("recording:outputfolder", false));
        Run(new SettingsRequest.Get("YouTube:CourseChannels", false));
        Run(new SettingsRequest.Get("Logging:LogLevel", false));
        Run(new SettingsRequest.Get("Recording:FlushSeconds", true));
        Run(new SettingsRequest.Get("YouTube:CourseChannels", true));

        Assert.That(Lines, Is.EqualTo(new[]
        {
            "%USERPROFILE%\\ClassScribe", "@one", "@two", "Default=Information", "Microsoft=Warning", "System=Warning", "5", """["@one","@two"]"""
        }));
    }

    /// <summary>An unknown setting and the key are mistakes said on standard error, exit 2; nothing is written to standard output.</summary>
    [Test]
    public void Get_refuses_an_unknown_setting_and_the_key()
    {
        Assert.That(Run(new SettingsRequest.Get("Recording:OutputFolders", false)), Is.EqualTo(2));
        Assert.That(Run(new SettingsRequest.Get("ElevenLabs:ApiKey", true)), Is.EqualTo(2));

        Assert.That(_out.ToString(), Is.Empty);
        Assert.That(_error.ToString(), Does.Contain("Recording:OutputFolders is not a setting; did you mean Recording:OutputFolder?"));
        Assert.That(_error.ToString(), Does.Contain(SettingsWriter.ApiKeyRefused));
    }

    /// <summary>A file that is not valid JSON lists nothing: what is wrong goes to standard error, exit 1.</summary>
    [Test]
    public void A_file_that_is_not_JSON_lists_nothing()
    {
        _files.WriteUser("{ not json");

        Assert.That(Run(new SettingsRequest.List(true)), Is.EqualTo(1));

        Assert.That(_out.ToString(), Is.Empty);
        Assert.That(_error.ToString(), Does.Contain($"Nothing can be read from {_files.UserFile} until it is fixed."));
    }

    /// <summary>set, reset, reset --all and fill go through the writer and give its exit codes.</summary>
    [Test]
    public void Changes_go_through_the_writer()
    {
        Assert.That(Run(new SettingsRequest.Set("Recording:FlushSeconds", ["7"])), Is.EqualTo(0));
        Assert.That(_files.UserValue("Recording:FlushSeconds")!.GetValue<int>(), Is.EqualTo(7));
        Assert.That(Run(new SettingsRequest.Set("Recording:FlushSeconds", ["0"])), Is.EqualTo(2));
        Assert.That(Run(new SettingsRequest.Reset("Recording:FlushSeconds")), Is.EqualTo(0));
        Assert.That(_files.UserValue("Recording:FlushSeconds")!.GetValue<int>(), Is.EqualTo(5));
        Assert.That(Run(new SettingsRequest.Fill()), Is.EqualTo(0));
        Assert.That(Run(new SettingsRequest.Reset(null)), Is.EqualTo(0));
        Assert.That(Run(new SettingsRequest.Set("ElevenLabs:ApiKey", ["sk-x"])), Is.EqualTo(2));
    }

    /// <summary>open makes the user's file first, as a full copy, when there is none yet, then opens it in Notepad.</summary>
    [Test]
    public void Open_makes_the_file_first_then_opens_it_in_Notepad()
    {
        Assert.That(Run(new SettingsRequest.Open()), Is.EqualTo(0));

        Assert.That(File.Exists(_files.UserFile), Is.True);
        _desktop.Verify(desktop => desktop.OpenInNotepad(_files.UserFile), Times.Once);
        Assert.That(Run(new SettingsRequest.Open()), Is.EqualTo(0), "made already: opened as it is");
    }

    /// <summary>
    /// While the user's file is missing, open is refused, exit 1, since a new file would hide the loss; list says so first
    /// on standard error, and lists the defaults.
    /// </summary>
    [Test]
    public void While_the_file_is_missing_open_is_refused_and_list_warns()
    {
        _files.Writer.Set("Recording:FlushSeconds", "7");
        File.Delete(_files.UserFile);
        var places = new SettingsPlaces(_files.Program.Path, _files.User.Path, _files.State.Path);
        var command = new SettingsCommand(new SettingsWriter(places, new SettingsFile(places, NullLogger<SettingsFile>.Instance), _files.Reloader, _files.Log), places,
            new SettingsFile(places, NullLogger<SettingsFile>.Instance), _files.Reloader, _desktop.Object, new CommandOutput(_out, _error), NullLogger<SettingsCommand>.Instance);

        Assert.That(command.RunAsync(new SettingsRequest.Open()).Result, Is.EqualTo(1));
        Assert.That(command.RunAsync(new SettingsRequest.List(false)).Result, Is.EqualTo(0));

        _desktop.VerifyNoOtherCalls();
        Assert.That(_error.ToString(), Does.Contain(places.DescribeMissing()!));
        Assert.That(Lines, Has.Member("Recording:FlushSeconds = 5"), "the defaults beside the program");
    }
}
