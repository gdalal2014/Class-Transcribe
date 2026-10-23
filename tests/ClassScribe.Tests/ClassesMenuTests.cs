using ClassScribe.Audio;
using ClassScribe.Audio.Microphones;
using ClassScribe.Commands;
using ClassScribe.Settings;
using ClassScribe.Storage;
using Microsoft.Extensions.Configuration;
using Moq;

namespace ClassScribe.Tests;

/// <summary>
/// Menu choice C (layout-config 4.3, build-plan amendment 12): the saved classes listed, a number recording one as saved
/// through the same path as choices 1 and 2, N adding one (from the Lesson video preset or question by question), E changing
/// one with its own answers for Enter, D removing one after Y. The class command is mocked, and its requests kept.
/// </summary>
[TestFixture]
public class ClassesMenuTests
{
    private TempFolder _folder = null!;
    private FakeConsoleUi _ui = null!;
    private Mock<ICommandRunner> _commands = null!;
    private List<(RecordRequest Request, StartOrigin Origin)> _recorded = null!;
    private List<ClassRequest> _changed = null!;

    /// <summary>Two classes: Mantiq records chrome with the array, into its own folder; Broken names two sources.</summary>
    private static readonly Dictionary<string, string?> Saved = new()
    {
        ["Classes:Mantiq:Course"] = "Mantiq",
        ["Classes:Mantiq:Mode"] = "ClassAndMicrophone",
        ["Classes:Mantiq:App"] = "chrome",
        ["Classes:Mantiq:Microphone"] = "Microphone Array",
        ["Classes:Mantiq:OutputFolder"] = @"D:\Classes",
        ["Classes:Mantiq:Transcribe"] = "false",
        ["Classes:Broken:Course"] = "Broken",
        ["Classes:Broken:App"] = "chrome",
        ["Classes:Broken:Device"] = "Speakers"
    };

    /// <summary>A recordings folder, a scripted keyboard, and the runner mocked, keeping each recording and each class change.</summary>
    [SetUp]
    public void SetUp()
    {
        _folder = new TempFolder();
        _ui = new FakeConsoleUi();
        _recorded = [];
        _changed = [];
        _commands = new Mock<ICommandRunner>();
        _commands.Setup(c => c.RecordAsync(It.IsAny<RecordRequest>(), It.IsAny<StartOrigin>()))
            .Callback((RecordRequest request, StartOrigin origin) => _recorded.Add((request, origin))).ReturnsAsync(0);
        _commands.Setup(c => c.ClassAsync(It.IsAny<ClassRequest>())).Callback((ClassRequest request) => _changed.Add(request)).ReturnsAsync(0);
    }

    /// <summary>Deletes the recordings folder.</summary>
    [TearDown]
    public void TearDown() => _folder.Dispose();

    /// <summary>
    /// Runs menu C over <paramref name="classes"/>, or the two above, with <paramref name="cameras"/> for the camera question
    /// in person (none asked without), failing the test rather than hanging.
    /// </summary>
    private Task<int> RunAsync(Dictionary<string, string?>? classes = null, FakeCameraProvider? cameras = null)
    {
        var saved = new SavedClasses(new ConfigurationBuilder().AddInMemoryCollection(classes ?? Saved).Build());
        var folders = new ClassFolders(TestOptions.Create(new RecordingOptions { OutputFolder = _folder.Path }), TimeProvider.System, Microsoft.Extensions.Logging.Abstractions.NullLogger<ClassFolders>.Instance);
        var microphones = new FakeMicrophoneProvider().Connect(LaptopMicrophones.Array, LaptopMicrophones.Obsbot);
        var menu = new ClassesMenu(saved, new SettingsPlaces(_folder.Path, userFolder: null), _commands.Object, folders, new FakeCaptureProvider(), microphones, _ui, TimeProvider.System,
            cameras);
        return menu.RunAsync().WaitAsync(TimeSpan.FromSeconds(15));
    }

    /// <summary>
    /// The classes are listed with what each records, or why it cannot be used; a usable one's number records it as saved,
    /// from the menu: its course, its plan, its own folder, its name in the request, record only as it says.
    /// </summary>
    [Test]
    public async Task A_number_records_the_class_as_saved()
    {
        _ui.Type('2');

        Assert.That(await RunAsync(), Is.EqualTo(0));

        Assert.That(_ui.Screen, Does.Contain("  1  Broken  cannot be used: App and Device are both set; a class records one app or one output device."));
        Assert.That(_ui.Screen, Does.Contain("  2  Mantiq  chrome, with your microphone (Microphone Array, following chrome), recorded only"));
        var (request, origin) = _recorded.Single();
        Assert.That(origin, Is.EqualTo(StartOrigin.Menu));
        Assert.That(request with { RunId = "" }, Is.EqualTo(new RecordRequest("", "Mantiq", false,
            new SourcePlan(RecordingMode.ClassAndMicrophone, new ClassSource.App("chrome"), new MicrophoneChoice("Microphone Array")), @"D:\Classes", ContinueChoice.Ask,
            null, null, true, ClassName: "Mantiq")));
        Assert.That(_ui.Screen, Does.Contain($"Recording Mantiq (saved class): chrome, with your microphone (Microphone Array, following chrome), recorded only, into {Path.Combine(@"D:\Classes", "Mantiq")}."));
    }

    /// <summary>A class that cannot be used says why when its number is pressed, and nothing records.</summary>
    [Test]
    public async Task An_unusable_class_says_why_and_records_nothing()
    {
        _ui.Type('1');

        await RunAsync();

        Assert.That(_recorded, Is.Empty);
        Assert.That(_ui.Screen, Does.Contain("The saved class \"Broken\" cannot be used: App and Device are both set"));
    }

    /// <summary>
    /// N with the Lesson video preset: in person, with any camera (a USB camera, else the built-in one), the microphone and
    /// the rest by Enter; saved through class add.
    /// </summary>
    [Test]
    public async Task N_with_the_lesson_video_preset_saves_a_class_in_person_with_a_camera()
    {
        _ui.Type('n', 'l');
        _ui.TypeLine("Lesson");
        _ui.TypeLine("");
        _ui.Type('\r');
        _ui.TypeLine("");
        _ui.Type('\r');

        Assert.That(await RunAsync(), Is.EqualTo(0));

        var add = (ClassRequest.Add)_changed.Single();
        Assert.That(add.Name, Is.EqualTo("Lesson"));
        Assert.That((add.Choices.Course, add.Choices.Source, add.Choices.Camera, add.Choices.CameraName, add.Choices.Transcribe),
            Is.EqualTo(("Lesson", new SourceGiven(SourceOption.InPerson, null), true, (string?)null, (bool?)true)));
    }

    /// <summary>
    /// N question by question: the name, the course, an app typed, the microphone picked by its number, the app it follows
    /// typed, Enter for the settings' folder, and N to record only.
    /// </summary>
    [Test]
    public async Task N_asks_each_question_and_saves_the_answers()
    {
        _ui.Type('n', '\r');
        _ui.TypeLine("Kalam (Zoom app)");
        _ui.TypeLine("Kalam");
        _ui.Type('a');
        _ui.TypeLine("zoom");
        _ui.Type('y', '1');
        _ui.TypeLine("zoom");
        _ui.TypeLine("");
        _ui.Type('n');

        Assert.That(await RunAsync(new()), Is.EqualTo(0));

        var add = (ClassRequest.Add)_changed.Single();
        Assert.That(add.Name, Is.EqualTo("Kalam (Zoom app)"));
        Assert.That((add.Choices.Course, add.Choices.Source, add.Choices.Mic, add.Choices.MicName, add.Choices.Follows, add.Choices.Transcribe, add.Choices.Output),
            Is.EqualTo(("Kalam", new SourceGiven(SourceOption.App, "zoom"), true, LaptopMicrophones.Array.Name, "zoom", (bool?)false, (string?)null)));
        Assert.That(_ui.Screen, Does.Contain("None yet."));
    }

    /// <summary>A name taken already, or one that cannot be a folder's name, is said, and nothing is saved.</summary>
    [Test]
    public async Task N_refuses_a_name_taken_and_one_that_is_no_folder_name()
    {
        _ui.Type('n', '\r');
        _ui.TypeLine("mantiq");
        await RunAsync();
        _ui.Type('n', '\r');
        _ui.TypeLine("Man:tiq");
        await RunAsync();

        Assert.That(_changed, Is.Empty);
        Assert.That(_ui.Screen, Does.Contain("There is a saved class \"Mantiq\" already; E changes it."));
        Assert.That(_ui.Screen, Does.Contain("\"Man:tiq\" cannot be a class's name"));
    }

    /// <summary>E and a number asks the same questions with the class's own answers for Enter, and saves them through class set.</summary>
    [Test]
    public async Task E_keeps_the_classs_answers_for_Enter()
    {
        _ui.Type('e', '2');
        _ui.TypeLine("");
        _ui.Type('\r', '\r', '\r');
        _ui.TypeLine("");
        _ui.TypeLine("");
        _ui.Type('\r');

        Assert.That(await RunAsync(), Is.EqualTo(0));

        var set = (ClassRequest.Set)_changed.Single();
        Assert.That(set.Name, Is.EqualTo("Mantiq"));
        Assert.That((set.Choices.Course, set.Choices.Source, set.Choices.Mic, set.Choices.MicName, set.Choices.Transcribe, set.Choices.Output),
            Is.EqualTo(("Mantiq", new SourceGiven(SourceOption.App, "chrome"), true, "Microphone Array", (bool?)false, @"D:\Classes")));
        Assert.That(set.Choices.Clear, Is.Empty, "Enter keeps the folder, written again as it was");
    }

    /// <summary>
    /// A relative recordings folder typed for a class is taken from the user's folder, as Recording:OutputFolder takes one,
    /// not from the window's current folder.
    /// </summary>
    [Test]
    public async Task A_relative_folder_typed_for_a_class_is_taken_from_the_user_folder()
    {
        _ui.Type('e', '2');
        _ui.TypeLine("");
        _ui.Type('\r', '\r', '\r');
        _ui.TypeLine("");
        _ui.TypeLine("Classes\\Mantiq");
        _ui.Type('\r');

        await RunAsync();

        var set = (ClassRequest.Set)_changed.Single();
        Assert.That(set.Choices.Output, Is.EqualTo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Classes", "Mantiq")));
    }

    /// <summary>
    /// E on a class in person with a camera: Enter keeps it in person with its microphone, and N at the camera question
    /// takes the camera and its size away, so the class records your microphone alone; the rest by Enter is kept.
    /// </summary>
    [Test]
    public async Task E_with_N_at_the_camera_takes_the_camera_away()
    {
        var fields = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            [ClassFields.Course] = "Kalam",
            [ClassFields.Mode] = "Microphone",
            [ClassFields.Microphone] = "OBSBOT",
            [ClassFields.Camera] = "OBSBOT",
            [ClassFields.CameraResolution] = "1080p"
        };
        _ui.Type('e', '1');
        _ui.TypeLine("");
        _ui.Type('\r', '\r', 'n');
        _ui.TypeLine("");
        _ui.Type('\r');

        await RunAsync(fields.ToDictionary(field => $"Classes:Kalam (in person):{field.Key}", field => field.Value), new FakeCameraProvider());

        Assert.That(_ui.Screen, Does.Contain("Press a camera's number, Enter keeps \"OBSBOT\", N for none, or Esc to go back."));
        var set = (ClassRequest.Set)_changed.Single();
        var after = new Dictionary<string, string?>(fields, StringComparer.OrdinalIgnoreCase);
        Assert.That(set.Choices.ApplyTo(after), Is.Null);
        Assert.That(after, Is.EquivalentTo(new Dictionary<string, string?>
        {
            [ClassFields.Course] = "Kalam", [ClassFields.Mode] = "Microphone", [ClassFields.Microphone] = "OBSBOT", [ClassFields.Transcribe] = "true"
        }));
    }

    /// <summary>
    /// E on a class that cannot be used names its problem, then asks the same questions with the class's own fields, as the
    /// file holds them, for Enter: pressed through, every field is kept, its course, its microphone, the app it follows and
    /// record only among them, and the stray field that makes it unusable is left for class set --clear or the file.
    /// </summary>
    [Test]
    public async Task E_on_a_class_that_cannot_be_used_keeps_its_own_fields_for_Enter()
    {
        var fields = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            [ClassFields.Course] = "Aqidah - Sughra As-Sughra",
            [ClassFields.Mode] = "ClassAndMicrophone",
            [ClassFields.App] = "zoom",
            [ClassFields.Microphone] = "Microphone Array",
            [ClassFields.MicrophoneFollows] = "zoom",
            [ClassFields.OutputFolder] = @"D:\Classes",
            [ClassFields.Transcribe] = "false",
            [ClassFields.CameraResolution] = "720p"
        };
        _ui.Type('e', '1');
        _ui.TypeLine("");
        _ui.Type('\r', '\r', '\r');
        _ui.TypeLine("");
        _ui.TypeLine("");
        _ui.Type('\r');

        Assert.That(await RunAsync(fields.ToDictionary(field => $"Classes:Sughra (Zoom app):{field.Key}", field => field.Value)), Is.EqualTo(0));

        Assert.That(_ui.Screen, Does.Contain("The saved class \"Sughra (Zoom app)\" cannot be used: CameraResolution is set, but the class records no camera."));
        Assert.That(_ui.Screen, Does.Contain("ClassScribe class set \"Sughra (Zoom app)\" --clear <field>"));
        Assert.That(_ui.Screen, Does.Contain("Course, which names its folder and files (Enter: Aqidah - Sughra As-Sughra)"));
        var set = (ClassRequest.Set)_changed.Single();
        var after = new Dictionary<string, string?>(fields, StringComparer.OrdinalIgnoreCase);
        Assert.That(set.Choices.ApplyTo(after), Is.Null);
        Assert.That(after, Is.EquivalentTo(fields), "Enter at every question keeps every field");
    }

    /// <summary>
    /// E on a class whose two sources make it unusable offers no Enter answer for the source, since it has no one source to
    /// keep; the source picked replaces both, and the rest is kept.
    /// </summary>
    [Test]
    public async Task E_on_a_class_with_two_sources_asks_for_one()
    {
        _ui.Type('e', '1');
        _ui.TypeLine("");
        _ui.Type('\r', 'w', '\r');
        _ui.TypeLine("");
        _ui.Type('\r');

        await RunAsync();

        Assert.That(_ui.Screen, Does.Contain("P in person (your microphone alone)."));
        var set = (ClassRequest.Set)_changed.Single();
        var fields = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase) { [ClassFields.Course] = "Broken", [ClassFields.App] = "chrome", [ClassFields.Device] = "Speakers" };
        Assert.That(set.Choices.ApplyTo(fields), Is.Null);
        Assert.That(SavedClasses.Check("Broken", fields).Class, Is.EqualTo(new SavedClass("Broken", "Broken", RecordingMode.Class, new ClassSource.Automatic(), null, null, null, true, null, null)));
    }

    /// <summary>D and a number removes the class after Y, and keeps it for any other key.</summary>
    [Test]
    public async Task D_removes_a_class_after_Y()
    {
        _ui.Type('d', '2', 'x');
        await RunAsync();
        _ui.Type('d', '2', 'y');
        await RunAsync();

        Assert.That(_changed, Is.EqualTo(new[] { new ClassRequest.Remove("Mantiq") }));
    }
}
