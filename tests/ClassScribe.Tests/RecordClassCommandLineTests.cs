using ClassScribe.Audio;
using ClassScribe.Audio.Microphones;
using ClassScribe.Commands;
using ClassScribe.Settings;
using ClassScribe.Storage;
using Microsoft.Extensions.Configuration;
using Moq;

namespace ClassScribe.Tests;

/// <summary>
/// record and record-only with a saved class (layout-config 4.4 and 4.6, N5 and N6): --class records it as saved; options
/// given beside it replace its own in groups, each mix the class cannot take refused; a saved class's name without a source
/// is a mistake pointed at --class; and --output may be left out everywhere.
/// </summary>
[TestFixture]
public class RecordClassCommandLineTests
{
    private TempFolder _folder = null!;
    private TempFolder _settingsFolder = null!;
    private Mock<ICommandRunner> _commands = null!;
    private List<(RecordRequest Request, StartOrigin Origin)> _recorded = null!;
    private TestCommandLine _line = null!;

    /// <summary>
    /// Three saved classes: Mantiq records chrome with the array following chrome into its own folder; Kalam (in person)
    /// records the OBSBOT in person with its camera at 1080p, record only; Broken names two sources.
    /// </summary>
    private static readonly Dictionary<string, string?> Classes = new()
    {
        ["Classes:Mantiq:Course"] = "Mantiq",
        ["Classes:Mantiq:Mode"] = "ClassAndMicrophone",
        ["Classes:Mantiq:App"] = "chrome",
        ["Classes:Mantiq:Microphone"] = "Microphone Array",
        ["Classes:Mantiq:MicrophoneFollows"] = "chrome",
        ["Classes:Mantiq:OutputFolder"] = @"D:\Classes",
        ["Classes:Kalam (in person):Course"] = "Kalam",
        ["Classes:Kalam (in person):Mode"] = "Microphone",
        ["Classes:Kalam (in person):Microphone"] = "OBSBOT",
        ["Classes:Kalam (in person):Camera"] = "OBSBOT",
        ["Classes:Kalam (in person):CameraResolution"] = "1080p",
        ["Classes:Kalam (in person):Transcribe"] = "false",
        ["Classes:Fiqh:Course"] = "Fiqh",
        ["Classes:Fiqh:Device"] = "Pixel Buds",
        ["Classes:Broken:Course"] = "Broken",
        ["Classes:Broken:App"] = "chrome",
        ["Classes:Broken:Device"] = "Speakers"
    };

    /// <summary>The command line over mocked commands that note each recording, with the saved classes above.</summary>
    [SetUp]
    public void SetUp()
    {
        _folder = new TempFolder();
        _settingsFolder = new TempFolder();
        _recorded = [];
        _commands = new Mock<ICommandRunner>();
        _commands.Setup(c => c.RecordAsync(It.IsAny<RecordRequest>(), It.IsAny<StartOrigin>()))
            .Callback((RecordRequest request, StartOrigin origin) => _recorded.Add((request, origin)))
            .ReturnsAsync(0);
        var classes = new SavedClasses(new ConfigurationBuilder().AddInMemoryCollection(Classes).Build());
        _line = new TestCommandLine(_commands.Object, new FakeConsoleUi(), new TestSettings(_settingsFolder.Path), _folder.Path, classes: classes);
    }

    /// <summary>Deletes the folders.</summary>
    [TearDown]
    public void TearDown()
    {
        _folder.Dispose();
        _settingsFolder.Dispose();
    }

    /// <summary>The one recording asked for, without its run id.</summary>
    private RecordRequest Only()
    {
        Assert.That(_recorded, Has.Count.EqualTo(1));
        Assert.That(_recorded[0].Origin, Is.EqualTo(StartOrigin.CommandLine));
        return _recorded[0].Request with { RunId = "" };
    }

    /// <summary>
    /// Without --output a recording goes to Recording:OutputFolder (N6): the folder the settings name, as a full path; with
    /// it, to the folder given, as before.
    /// </summary>
    [Test]
    public async Task Output_may_be_left_out()
    {
        Assert.That(await _line.RunAsync("record", "Physics", "--app", "chrome"), Is.EqualTo(0));

        Assert.That(Only().OutputRoot, Is.EqualTo(ClassFolders.RootFor(_folder.Path)));
    }

    /// <summary>
    /// --class records the saved class as saved, its name matched in any case: its course as the recording's name, its plan,
    /// its own folder, and its name in the request, so the run says it is the saved class; record-only records only.
    /// </summary>
    [Test]
    public async Task A_saved_class_is_recorded_as_saved()
    {
        Assert.That(await _line.RunAsync("record", "--class", "mantiq"), Is.EqualTo(0));

        Assert.That(Only(), Is.EqualTo(new RecordRequest("", "Mantiq", true,
            new SourcePlan(RecordingMode.ClassAndMicrophone, new ClassSource.App("chrome"), new MicrophoneChoice("Microphone Array", new FollowRule.Named("chrome"))),
            @"D:\Classes", ContinueChoice.Ask, null, null, true, ClassName: "Mantiq")));

        _recorded.Clear();
        Assert.That(await _line.RunAsync("record-only", "--class", "Kalam (in person)", "--minutes", "90", "--no-open", "--new"), Is.EqualTo(0));
        var inPerson = Only();
        Assert.That((inPerson.Name, inPerson.Transcribe, inPerson.Minutes, inPerson.Open, inPerson.Continue, inPerson.ClassName),
            Is.EqualTo(("Kalam", false, (double?)90, false, ContinueChoice.New, "Kalam (in person)")));
        Assert.That(inPerson.Source, Is.EqualTo(new SourcePlan(RecordingMode.Microphone, null, new MicrophoneChoice("OBSBOT"), new CameraChoice("OBSBOT", "1080p"))));
        Assert.That(inPerson.OutputRoot, Is.EqualTo(ClassFolders.RootFor(_folder.Path)), "no folder of its own: the settings'");
    }

    /// <summary>
    /// Options beside --class replace the class's own in groups: a source its whole source and the app its microphone
    /// follows; --mic its microphone; --mic-follows what it follows, without --mic on a class that records the microphone;
    /// a name its course; --output its folder; --resolution its camera's size.
    /// </summary>
    [TestCase(new[] { "--app", "zoom" }, "app zoom; Microphone Array; follows the app")]
    [TestCase(new[] { "--app", "zoom", "--mic-follows", "zoom" }, "app zoom; Microphone Array; follows zoom")]
    [TestCase(new[] { "--mic", "OBSBOT" }, "app chrome; OBSBOT; follows chrome")]
    [TestCase(new[] { "--mic" }, "app chrome; default; follows chrome")]
    [TestCase(new[] { "--mic-follows", "auto" }, "app chrome; Microphone Array; follows any")]
    [TestCase(new[] { "--device", "auto" }, "device auto; Microphone Array; follows the app")]
    public async Task Options_over_a_class_replace_it_in_groups(string[] options, string expected)
    {
        Assert.That(await _line.RunAsync(["record", "--class", "Mantiq", .. options]), Is.EqualTo(0));

        var plan = Only().Source;
        var source = plan.Class switch { ClassSource.App app => $"app {app.ProcessName}", ClassSource.Device device => $"device {device.NamePart}", _ => "device auto" };
        var follows = plan.Microphone!.Follows switch { FollowRule.Named named => $"follows {named.NamePart}", FollowRule.Any => "follows any", _ => "follows the app" };
        Assert.That(plan.Mode, Is.EqualTo(RecordingMode.ClassAndMicrophone));
        Assert.That($"{source}; {plan.Microphone.NamePart ?? "default"}; {follows}", Is.EqualTo(expected));
    }

    /// <summary>
    /// --mic adds the microphone to a class without one; --in-person makes any class one in person, dropping the app it
    /// followed; a name replaces the course; --output the class's folder; --resolution the camera's size.
    /// </summary>
    [Test]
    public async Task Options_add_a_microphone_go_in_person_and_replace_the_rest()
    {
        Assert.That(await _line.RunAsync("record", "--class", "Fiqh", "--mic"), Is.EqualTo(0));
        Assert.That(Only().Source, Is.EqualTo(new SourcePlan(RecordingMode.ClassAndMicrophone, new ClassSource.Device("Pixel Buds"), new MicrophoneChoice(null))));

        _recorded.Clear();
        Assert.That(await _line.RunAsync("record", "--class", "Mantiq", "--in-person"), Is.EqualTo(0));
        Assert.That(Only().Source, Is.EqualTo(new SourcePlan(RecordingMode.Microphone, null, new MicrophoneChoice("Microphone Array"))));

        _recorded.Clear();
        Assert.That(await _line.RunAsync("record", "Mantiq Extra", "--class", "Mantiq", "--output", _folder.File("Elsewhere")), Is.EqualTo(0));
        Assert.That((Only().Name, Only().OutputRoot, Only().ClassName), Is.EqualTo(("Mantiq Extra", _folder.File("Elsewhere"), "Mantiq")));

        _recorded.Clear();
        Assert.That(await _line.RunAsync("record", "--class", "Kalam (in person)", "--resolution", "480p"), Is.EqualTo(0));
        Assert.That(Only().Source.Camera, Is.EqualTo(new CameraChoice("OBSBOT", "480p")));
    }

    /// <summary>
    /// The mixes a class cannot take are refused with exit 2, the reason and the help, and nothing records: --mic-follows over
    /// a class without the microphone, --camera over one that is not in person, --resolution without a camera, --mic over
    /// one in person; and --mic with --in-person is refused before anything is read, as it is without a class.
    /// </summary>
    [TestCase(new[] { "--class", "Fiqh", "--mic-follows", "zoom" }, "--mic-follows goes with a class that records your microphone beside the class: add --mic, over the saved class \"Fiqh\"")]
    [TestCase(new[] { "--class", "Mantiq", "--camera" }, "--camera records a lesson's picture only in person: give it with --in-person, over the saved class \"Mantiq\"")]
    [TestCase(new[] { "--class", "Mantiq", "--resolution", "720p" }, "--resolution needs --camera, or a class that records a camera, over the saved class \"Mantiq\"")]
    [TestCase(new[] { "--class", "Kalam (in person)", "--mic" }, "--mic adds your microphone beside the class; this class is in person, where the microphone is already the source: give --in-person \"<microphone>\" to change it, over the saved class \"Kalam (in person)\"")]
    [TestCase(new[] { "--class", "Mantiq", "--mic", "--in-person" }, "--mic adds the microphone to --device or --app; with --in-person the microphone is already the source")]
    public async Task Mixes_a_class_cannot_take_are_refused(string[] args, string mistake)
    {
        Assert.That(await _line.RunAsync(["record", .. args]), Is.EqualTo(2));

        Assert.That(_recorded, Is.Empty);
        Assert.That(_line.Mistakes, Is.EqualTo(new[] { mistake }));
        Assert.That(_line.Output.ToString(), Does.Contain("Usage:"));
    }

    /// <summary>An unknown class is refused with exit 2 and the saved ones; one that cannot be used, with why.</summary>
    [Test]
    public async Task An_unknown_or_unusable_class_is_refused()
    {
        Assert.That(await _line.RunAsync("record", "--class", "Mant"), Is.EqualTo(2));
        Assert.That(await _line.RunAsync("record", "--class", "Broken"), Is.EqualTo(2));

        Assert.That(_recorded, Is.Empty);
        Assert.That(_line.Mistakes[0], Is.EqualTo("There is no saved class \"Mant\". The saved classes are: \"Broken\", \"Fiqh\", \"Kalam (in person)\", \"Mantiq\"."));
        Assert.That(_line.Mistakes[1], Is.EqualTo("The saved class \"Broken\" cannot be used: App and Device are both set; a class records one app or one output device."));
    }

    /// <summary>
    /// record with a saved class's name and no source stays a mistake (N5), exit 2, and points at --class with the class's
    /// own name; a name that is no class's gets no such line.
    /// </summary>
    [Test]
    public async Task A_saved_class_name_without_a_source_is_pointed_at_class()
    {
        Assert.That(await _line.RunAsync("record", "mantiq"), Is.EqualTo(2));
        Assert.That(await _line.RunAsync("record-only", "Physics"), Is.EqualTo(2));

        Assert.That(_line.Mistakes, Is.EqualTo(new[]
        {
            "record needs a source: --device \"<part of its name>\", --device auto, --app <process> or --in-person",
            "mantiq is a saved class: ClassScribe record --class \"Mantiq\"",
            "record-only needs a source: --device \"<part of its name>\", --device auto, --app <process> or --in-person"
        }));
    }

    /// <summary>
    /// A saved class's request, printed by <see cref="CommandLine.Record(RecordRequest, string?, string)"/> for later, records the same request when run as
    /// printed (build-plan amendment 11's rule), for each field a class holds: the source, the microphone and what it
    /// follows, the camera and its size, the folder and record-only.
    /// </summary>
    [TestCase("Mantiq")]
    [TestCase("Kalam (in person)")]
    [TestCase("Fiqh")]
    public async Task A_classs_printed_line_records_the_same_request(string name)
    {
        Assert.That(await _line.RunAsync(transcribe(name), "--class", name), Is.EqualTo(0));
        var request = Only();

        var printed = CommandLine.Record(request, @"C:\Programs\ClassScribe\ClassScribe.exe", @"C:\Programs\ClassScribe");
        var words = System.Text.RegularExpressions.Regex.Matches(printed, "\"((?:`.|[^\"`])*)\"|(\\S+)")
            .Select(match => match.Groups[1].Success ? System.Text.RegularExpressions.Regex.Replace(match.Groups[1].Value, "`(.)", "$1") : match.Groups[2].Value)
            .Skip(2).Where(word => word != "--continue").ToArray();
        _recorded.Clear();
        Assert.That(await _line.RunAsync(words), Is.EqualTo(0));

        Assert.That(Only() with { ClassName = request.ClassName }, Is.EqualTo(request));

        // The word for the class: record-only for one that records only when started from menu C, as the class says.
        static string transcribe(string name) => name.StartsWith("Kalam", StringComparison.Ordinal) ? "record-only" : "record";
    }
}
