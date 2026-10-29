using ClassScribe.Audio;
using ClassScribe.Audio.Microphones;
using ClassScribe.Commands;
using ClassScribe.Runs;
using ClassScribe.Settings;
using ClassScribe.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace ClassScribe.Tests;

/// <summary>
/// The menu's side of the settings and the saved classes (layout-config 1.8, 4.3; amendment 17): C and S in the menu, S
/// without the gate; the header naming a class that cannot be used; choices 1 and 2 offering a saved class (Enter as
/// saved, O with choice 6's source); S after a pick in choices 6 and 7 keeping it as the default; and the S screen itself.
/// The settings are real, in temporary folders; the runner is mocked.
/// </summary>
[TestFixture]
public class MenuSettingsTests
{
    private SettingsFileFixtures _files = null!;
    private TempFolder _recordings = null!;
    private FakeConsoleUi _ui = null!;
    private Mock<ICommandRunner> _commands = null!;
    private List<(RecordRequest Request, StartOrigin Origin)> _recorded = null!;
    private FakeCaptureProvider _devices = null!;
    private SourceChoice _choice = null!;
    private ServiceProvider _options = null!;

    /// <summary>The settings with two classes of one course and a third that cannot be used, a keyboard, and the runner mocked.</summary>
    [SetUp]
    public void SetUp()
    {
        _files = new SettingsFileFixtures();
        _files.WriteUser("""
            {
              "Classes": {
                "Sughra (Chrome)": { "Course": "Aqidah - Sughra As-Sughra", "App": "chrome" },
                "Sughra (Zoom app)": { "Course": "Aqidah - Sughra As-Sughra", "App": "zoom" },
                "Mantiq": { "Course": "Mantiq", "App": "chrome", "OutputFolder": "E:\\Mantiq" },
                "Broken": { "Course": "Broken", "App": "chrome", "Device": "Speakers" }
              }
            }
            """);
        _files.Reloader.TryReload();
        _recordings = new TempFolder();
        _ui = new FakeConsoleUi();
        _recorded = [];
        _commands = new Mock<ICommandRunner>();
        _commands.Setup(c => c.RecordAsync(It.IsAny<RecordRequest>(), It.IsAny<StartOrigin>()))
            .Callback((RecordRequest request, StartOrigin origin) => _recorded.Add((request, origin))).ReturnsAsync(0);
        _devices = new FakeCaptureProvider();
        _choice = new SourceChoice(TestOptions.Create(new RecordingOptions()));
        _options = new ServiceCollection().AddOptions<RecordingOptions>().Bind(_files.Configuration.GetSection(RecordingOptions.SectionName)).Services.BuildServiceProvider();
    }

    /// <summary>Deletes the folders.</summary>
    [TearDown]
    public void TearDown()
    {
        _options.Dispose();
        _recordings.Dispose();
        _files.Dispose();
    }

    /// <summary>The menu as the container makes it, with the saved classes and the writer, until it is left.</summary>
    private Task<int> RunMenuAsync()
    {
        var report = new Mock<IStartupReport>();
        report.Setup(r => r.ShowAsync(It.IsAny<bool>())).ReturnsAsync((StartupChoice?)null);
        report.Setup(r => r.HeaderLines()).Returns([]);
        var folders = new ClassFolders(TestOptions.Create(new RecordingOptions { OutputFolder = _recordings.Path }), TimeProvider.System, NullLogger<ClassFolders>.Instance);
        var menu = new MenuCommand(_commands.Object, report.Object, _ui, new RunGate(_files.Reloader, _files.SettingsFile, NullLogger<RunGate>.Instance), _files.SettingsFile,
            TestOptions.Create(new ElevenLabsOptions { ApiKey = "test-key" }), folders, new RecordingFolder(), new TranscriptCopies(), _devices, Printed.Program, _choice, new RecordingNamePrompt(_ui), Mock.Of<IDesktop>(), TimeProvider.System,
            NullLogger<MenuCommand>.Instance, new SavedClasses(_files.Configuration), _files.Writer, new Transcription.GlossaryFiles(_files.Places));
        return menu.RunAsync().WaitAsync(TimeSpan.FromSeconds(15));
    }

    /// <summary>The menu offers C and S, and its header names a saved class that cannot be used, with why.</summary>
    [Test]
    public async Task The_menu_offers_C_and_S_and_names_an_unusable_class()
    {
        _ui.Type('q');

        await RunMenuAsync();

        var lines = _ui.Screen.Split(Environment.NewLine);
        Assert.That(lines, Has.Member("  C  Saved classes: start, add, change or remove one"));
        Assert.That(lines, Has.Member("  S  Settings: recordings folder, camera picture size, the settings file"));
        Assert.That(lines, Has.Member("WARNING  The saved class \"Broken\" cannot be used: App and Device are both set; a class records one app or one output device."));
    }

    /// <summary>
    /// The header names a saved class whose own glossary is not there (architecture 8.2, item 4): it can still be used, and
    /// its transcripts are made without its terms until the file is there. A class whose glossary is there, or that has none,
    /// is not named.
    /// </summary>
    [Test]
    public async Task The_header_names_a_class_whose_glossary_is_not_there()
    {
        _files.WriteUser("""
            {
              "Classes": {
                "Mantiq": { "Course": "Mantiq", "App": "chrome", "KeytermsFile": "mantiq.txt" },
                "Fiqh": { "Course": "Fiqh", "App": "chrome", "KeytermsFile": "fiqh.txt" },
                "Kalam": { "Course": "Kalam", "App": "chrome", "KeytermsFile": "none" }
              }
            }
            """);
        File.WriteAllText(Path.Combine(_files.User.Path, "fiqh.txt"), "fiqh");
        _files.Reloader.TryReload();
        _ui.Type('q');

        await RunMenuAsync();

        var warnings = _ui.Screen.Split(Environment.NewLine).Where(line => line.StartsWith("WARNING  The saved class", StringComparison.Ordinal)).ToList();
        Assert.That(warnings, Is.EqualTo(new[]
        {
            $"WARNING  The saved class \"Mantiq\" names the glossary {Path.Combine(_files.User.Path, "mantiq.txt")}, which is not there; its transcripts are made without its terms until it is."
        }));
    }

    /// <summary>C runs the saved classes' menu; S the settings' menu, without the gate, so that a file it refuses can be fixed there.</summary>
    [Test]
    public async Task C_and_S_open_their_menus_and_S_needs_no_usable_file()
    {
        _files.WriteUser("{ not json");
        _ui.Type('s', 'c', 'q');

        await RunMenuAsync();

        _commands.Verify(c => c.SettingsMenuAsync(), Times.Once);
        _commands.Verify(c => c.ClassesMenuAsync(), Times.Once, "C starts or changes classes, which a refused file does not stop");
    }

    /// <summary>
    /// Choice 2 with a saved class's name asks first (amendment 17): Enter records it as saved, its own folder and its name in
    /// the request; O records under the typed name with choice 6's source and the settings' folder.
    /// </summary>
    [Test]
    public async Task Choices_1_and_2_offer_a_saved_class_Enter_as_saved_O_as_choice_6()
    {
        _ui.Type('2');
        _ui.TypeLine("mantiq");
        _ui.Type('\r', '1');
        _ui.TypeLine("Mantiq");
        _ui.Type('o', 'q');

        await RunMenuAsync();

        Assert.That(_ui.Screen, Does.Contain("mantiq is a saved class (chrome). Enter records it as saved; O records with choice 6's source instead."));
        Assert.That(_recorded, Has.Count.EqualTo(2));
        Assert.That((_recorded[0].Request.Name, _recorded[0].Request.ClassName, _recorded[0].Request.OutputRoot, _recorded[0].Request.Transcribe, _recorded[0].Origin),
            Is.EqualTo(("Mantiq", "Mantiq", @"E:\Mantiq", true, StartOrigin.Menu)));
        Assert.That((_recorded[1].Request.Name, _recorded[1].Request.ClassName, _recorded[1].Request.OutputRoot, _recorded[1].Request.Transcribe),
            Is.EqualTo(("Mantiq", (string?)null, _recordings.Path, false)));
        Assert.That(_recorded[1].Request.Source, Is.EqualTo(_choice.Current));
    }

    /// <summary>
    /// Esc at the saved-class question of choices 1 and 2 goes back to the menu and records nothing, neither the class as
    /// saved nor the typed name with choice 6's source.
    /// </summary>
    [Test]
    public async Task Esc_at_the_saved_class_question_records_nothing()
    {
        _ui.Type('1');
        _ui.TypeLine("Mantiq");
        _ui.Type('\u001b', 'q');

        await RunMenuAsync();

        Assert.That(_ui.Screen, Does.Contain("Mantiq is a saved class (chrome). Enter records it as saved; O records with choice 6's source instead."));
        Assert.That(_recorded, Is.Empty);
    }

    /// <summary>A course shared by two saved classes lists both, and a number picks one.</summary>
    [Test]
    public async Task A_course_of_two_classes_lists_both()
    {
        _ui.Type('1');
        _ui.TypeLine("Aqidah - Sughra As-Sughra");
        _ui.Type('2', 'q');

        await RunMenuAsync();

        Assert.That(_ui.Screen, Does.Contain("Aqidah - Sughra As-Sughra is the course of saved classes:"));
        Assert.That(_recorded.Single().Request.ClassName, Is.EqualTo("Sughra (Zoom app)"));
        Assert.That(_recorded.Single().Request.Source.Class, Is.EqualTo(new ClassSource.App("zoom")));
    }

    /// <summary>After a pick in choice 6, S keeps it as the default: the app is saved and the device emptied, through the writer.</summary>
    [Test]
    public async Task S_after_choice_6_keeps_the_pick_as_the_default()
    {
        _devices.Enqueue(new FakeAudioCapture(new AudioFormat(16000, 1, 16, false), "Speakers"));
        _devices.AddApp(4242, "chrome", "Speakers", level: 0.4f);
        _ui.Type('6', '2', 's', 'q');

        await RunMenuAsync();

        Assert.That(_ui.Screen, Does.Contain($"Press S to keep this as the default (saved in {_files.UserFile}), or any other key to use it until ClassScribe is closed."));
        Assert.That(_files.UserValue("Recording:App")!.GetValue<string>(), Is.EqualTo("chrome"));
        Assert.That(_files.UserValue("Recording:Device")!.GetValue<string>(), Is.Empty);
    }

    /// <summary>
    /// The S screen: 1 changes the recordings folder through the writer, shows where the next class would go, and says the
    /// recordings made before stay where they were; P changes the camera's picture size; O opens the file.
    /// </summary>
    [Test]
    public async Task The_settings_screen_changes_the_folder_and_the_picture_size()
    {
        var before = new TempFolder();
        _files.Writer.Set("Recording:OutputFolder", before.Path);
        var menu = SettingsMenu();
        _ui.Type('1');
        _ui.TypeLine($"\"{_recordings.File("Classes")}\"");

        await menu.RunAsync();

        Assert.That(_files.UserValue("Recording:OutputFolder")!.GetValue<string>(), Is.EqualTo(_recordings.File("Classes")));
        Assert.That(_ui.Screen, Does.Contain($"The next class of Mantiq would go to {Path.Combine(_recordings.File("Classes"), "Mantiq")}"));
        Assert.That(_ui.Screen, Does.Contain($"Recordings made before stay in {before.Path}; nothing was moved. ClassScribe list --output \"{before.Path}\" lists them."));

        _ui.Type('p', '3');
        await SettingsMenu().RunAsync();
        Assert.That(_files.UserValue("Recording:CameraResolution")!.GetValue<string>(), Is.EqualTo("1080p"));

        _ui.Type('o');
        await SettingsMenu().RunAsync();
        _commands.Verify(c => c.SettingsAsync(It.IsAny<SettingsRequest.Open>()), Times.Once);
        before.Dispose();
    }

    /// <summary>
    /// A relative folder typed at the S screen is taken from the user's folder, as Recording:OutputFolder itself takes one,
    /// not from the window's current folder, and saved as that full path. Nothing is made there.
    /// </summary>
    [Test]
    public async Task A_relative_folder_typed_in_S_is_taken_from_the_user_folder()
    {
        var relative = $"ClassScribe test {Guid.NewGuid():N}";
        var expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), relative);
        _files.Writer.Set("Recording:OutputFolder", _recordings.Path);
        _ui.Type('1');
        _ui.TypeLine(relative);

        await SettingsMenu().RunAsync();

        Assert.That(_files.UserValue("Recording:OutputFolder")!.GetValue<string>(), Is.EqualTo(expected));
        Assert.That(_ui.Screen, Does.Contain($"would go to {Path.Combine(expected, "Mantiq")}"));
        Assert.That(Directory.Exists(expected), Is.False, "nothing is made");
    }

    /// <summary>
    /// After a pick in choice 7, S keeps it as the default: the mode, the microphone picked and the app it follows, through
    /// the writer; any other key keeps it until ClassScribe is closed and writes nothing.
    /// </summary>
    [Test]
    public async Task S_after_choice_7_keeps_the_pick_as_the_default()
    {
        var microphones = new FakeMicrophoneProvider().Connect(LaptopMicrophones.Array, LaptopMicrophones.Obsbot);
        var menu = new MicrophoneMenu(microphones, _choice, _files.SettingsFile, TestOptions.Create(new RecordingOptions()), _ui, writer: _files.Writer);
        _ui.Type('w', '2');
        _ui.TypeLine("zoom");
        _ui.Type('s');

        await menu.RunAsync();

        Assert.That(_files.UserValue("Recording:Mode")!.GetValue<string>(), Is.EqualTo("ClassAndMicrophone"));
        Assert.That(_files.UserValue("Recording:Microphone")!.GetValue<string>(), Is.EqualTo(LaptopMicrophones.Obsbot.Name));
        Assert.That(_files.UserValue("Recording:MicrophoneFollows")!.GetValue<string>(), Is.EqualTo("zoom"));

        var saved = _files.UserText;
        _ui.Type('n', 'x');
        await menu.RunAsync();
        Assert.That(_choice.Mode, Is.EqualTo(RecordingMode.Class), "the pick holds until ClassScribe is closed");
        Assert.That(_files.UserText, Is.EqualTo(saved), "any key but S writes nothing");
    }

    /// <summary>
    /// In person with a camera and its size picked in choice 7, S keeps the size as Recording:CameraResolution (amendment 11),
    /// with the mode and the microphone; the camera itself is no setting, so nothing names it.
    /// </summary>
    [Test]
    public async Task S_after_choice_7_in_person_keeps_the_picture_size()
    {
        var microphones = new FakeMicrophoneProvider().Connect(LaptopMicrophones.Array, LaptopMicrophones.Obsbot);
        var menu = new MicrophoneMenu(microphones, _choice, _files.SettingsFile, TestOptions.Create(new RecordingOptions()), _ui, new FakeCameraProvider(), _files.Writer);
        _ui.Type('i', '2', '2', '3', 's');

        await menu.RunAsync();

        Assert.That(_files.UserValue("Recording:Mode")!.GetValue<string>(), Is.EqualTo("Microphone"));
        Assert.That(_files.UserValue("Recording:Microphone")!.GetValue<string>(), Is.EqualTo(LaptopMicrophones.Obsbot.Name));
        Assert.That(_files.UserValue("Recording:CameraResolution")!.GetValue<string>(), Is.EqualTo("1080p"));
        Assert.That(_files.UserText, Does.Not.Contain(LaptopCameras.Obsbot.Name), "the camera is no setting");
    }

    /// <summary>
    /// The S screen over the fixture's settings and the mocked runner, its recordings folder read from the settings as they
    /// are now, as the container's options do, so that a change the writer makes reaches it.
    /// </summary>
    private SettingsMenu SettingsMenu()
    {
        var recording = _options.GetRequiredService<IOptionsMonitor<RecordingOptions>>();
        return new(_files.Writer, _files.Places, new ClassFolders(recording, TimeProvider.System, NullLogger<ClassFolders>.Instance),
            TestOptions.Create(recording.CurrentValue), _files.Reloader, _commands.Object, _ui, TimeProvider.System);
    }
}
