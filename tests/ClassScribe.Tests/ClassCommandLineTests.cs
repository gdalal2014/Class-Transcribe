using ClassScribe.Commands;
using ClassScribe.Settings;
using Moq;

namespace ClassScribe.Tests;

/// <summary>
/// The class command on the command line (layout-config 4.4): its commands and the choices they carry, the mistakes named
/// before anything runs, and its own kind, without the gate and the report.
/// </summary>
[TestFixture]
public class ClassCommandLineTests
{
    private TempFolder _folder = null!;
    private TempFolder _settingsFolder = null!;
    private Mock<ICommandRunner> _commands = null!;
    private List<ClassRequest> _asked = null!;
    private TestCommandLine _line = null!;

    /// <summary>The command line over mocked commands, which note each class request and succeed.</summary>
    [SetUp]
    public void SetUp()
    {
        _folder = new TempFolder();
        _settingsFolder = new TempFolder();
        _asked = [];
        _commands = new Mock<ICommandRunner>();
        _commands.Setup(c => c.ClassAsync(It.IsAny<ClassRequest>())).Callback((ClassRequest request) => _asked.Add(request)).ReturnsAsync(0);
        _line = new TestCommandLine(_commands.Object, new FakeConsoleUi(), new TestSettings(_settingsFolder.Path), _folder.Path);
    }

    /// <summary>Deletes the folders.</summary>
    [TearDown]
    public void TearDown()
    {
        _folder.Dispose();
        _settingsFolder.Dispose();
    }

    /// <summary>Each command asks for what it names, with the choices given: a source, the microphone, the camera, the folder, record-only and the clears.</summary>
    [Test]
    public async Task Each_command_asks_for_what_it_names()
    {
        Assert.That(await _line.RunAsync("class", "list", "--json"), Is.EqualTo(0));
        Assert.That(await _line.RunAsync("Class", "Show", "Mantiq"), Is.EqualTo(0));
        Assert.That(await _line.RunAsync("class", "add", "Mantiq", "--app", "chrome.exe", "--mic", "Microphone Array", "--mic-follows", "chrome"), Is.EqualTo(0));
        Assert.That(await _line.RunAsync("class", "add", "Kalam (in person)", "--course", "Kalam", "--in-person", "Microphone Array", "--record-only", "--camera", "--resolution", "1080p"), Is.EqualTo(0));
        Assert.That(await _line.RunAsync("class", "set", "Mantiq", "--output", _folder.File("Classes"), "--clear", "follows", "--clear", "microphone"), Is.EqualTo(0));
        Assert.That(await _line.RunAsync("class", "rename", "Mantiq", "Mantiq (Chrome)"), Is.EqualTo(0));
        Assert.That(await _line.RunAsync("class", "remove", "Mantiq (Chrome)"), Is.EqualTo(0));

        Assert.That(_asked[0], Is.EqualTo(new ClassRequest.List(true)));
        Assert.That(_asked[1], Is.EqualTo(new ClassRequest.Show("Mantiq", false)));
        Assert.That(_asked[2], Is.EqualTo(new ClassRequest.Add("Mantiq", new ClassChoices
        {
            Source = new SourceGiven(SourceOption.App, "chrome"), Mic = true, MicName = "Microphone Array", Follows = "chrome", Clear = []
        })).Using<ClassRequest>(SameRequest));
        var inPerson = ((ClassRequest.Add)_asked[3]).Choices;
        Assert.That((inPerson.Course, inPerson.Source, inPerson.Transcribe, inPerson.Camera, inPerson.CameraName, inPerson.Resolution),
            Is.EqualTo(("Kalam", new SourceGiven(SourceOption.InPerson, "Microphone Array"), (bool?)false, true, (string?)null, "1080p")));
        var set = ((ClassRequest.Set)_asked[4]).Choices;
        Assert.That((set.Output, set.Clear), Is.EqualTo((_folder.File("Classes"), new[] { "follows", "microphone" })).Using<(string?, IReadOnlyList<string>)>((a, b) => a.Item1 == b.Item1 && a.Item2.SequenceEqual(b.Item2)));
        Assert.That(_asked[5], Is.EqualTo(new ClassRequest.Rename("Mantiq", "Mantiq (Chrome)")));
        Assert.That(_asked[6], Is.EqualTo(new ClassRequest.Remove("Mantiq (Chrome)")));
    }

    /// <summary>
    /// --keyterms gives a class its own glossary, as written (quotes and spaces around it taken off, a relative path left
    /// relative, for the settings folder), whether or not it is there yet; none for no glossary; --clear keyterms empties it.
    /// </summary>
    [Test]
    public async Task Keyterms_gives_a_class_its_own_glossary_and_clear_keyterms_empties_it()
    {
        Assert.That(await _line.RunAsync("class", "add", "Mantiq", "--app", "chrome", "--keyterms", " \"mantiq.txt\" "), Is.EqualTo(0));
        Assert.That(await _line.RunAsync("class", "set", "Mantiq", "--KEYTERMS", "none"), Is.EqualTo(0));
        Assert.That(await _line.RunAsync("class", "set", "Mantiq", "--keyterms", @"%APPDATA%\ClassScribe\mantiq.txt"), Is.EqualTo(0));
        Assert.That(await _line.RunAsync("class", "set", "Mantiq", "--clear", "keyterms"), Is.EqualTo(0));

        Assert.That(((ClassRequest.Add)_asked[0]).Choices.Keyterms, Is.EqualTo("mantiq.txt"));
        Assert.That(((ClassRequest.Set)_asked[1]).Choices.Keyterms, Is.EqualTo("none"));
        Assert.That(((ClassRequest.Set)_asked[2]).Choices.Keyterms, Is.EqualTo(@"%APPDATA%\ClassScribe\mantiq.txt"));
        Assert.That((((ClassRequest.Set)_asked[3]).Choices.Keyterms, ((ClassRequest.Set)_asked[3]).Choices.Clear), Is.EqualTo(((string?)null, new[] { "keyterms" }))
            .Using<(string?, IReadOnlyList<string>)>((a, b) => a.Item1 == b.Item1 && a.Item2.SequenceEqual(b.Item2)));
    }

    /// <summary>The mixes record refuses, and set without anything to change, are refused before anything runs, with the help, exit 2.</summary>
    [TestCase(new[] { "class", "add", "Mantiq", "--app", "chrome", "--device", "auto" }, "class add takes one source; you gave --device and --app")]
    [TestCase(new[] { "class", "add", "Mantiq", "--in-person", "--mic" }, "--mic adds the microphone to --device or --app; with --in-person the microphone is already the source")]
    [TestCase(new[] { "class", "add", "Mantiq", "--in-person", "--mic-follows", "zoom" }, "--mic-follows cannot go with --in-person: in person there is no class app")]
    [TestCase(new[] { "class", "add", "Mantiq", "--record-only", "--transcribe" }, "--record-only and --transcribe cannot go together")]
    [TestCase(new[] { "class", "add", "Mantiq", "--resolution", "999p" }, "--resolution takes 480p, 720p or 1080p, or a width and a height such as 1024x576: the width from 320 to 1920 and the height from 240 to 1080, both even, in a shape from 4:3 to 16:9; \"999p\" is not one")]
    [TestCase(new[] { "class", "set", "Mantiq", "--clear", "layout" }, "--clear takes output, microphone, follows, camera, resolution or keyterms, not \"layout\"")]
    [TestCase(new[] { "class", "set", "Mantiq", "--clear", "output", "--output", "D:\\Classes" }, "--clear output and --output cannot go together")]
    [TestCase(new[] { "class", "set", "Mantiq", "--clear", "keyterms", "--keyterms", "mantiq.txt" }, "--clear keyterms and --keyterms cannot go together")]
    [TestCase(new[] { "class", "set", "Mantiq", "--keyterms" }, "--keyterms needs a glossary file after it, or none, such as --keyterms \"%APPDATA%\\ClassScribe\\mantiq.txt\"")]
    [TestCase(new[] { "class", "add", "Mantiq", "--keyterms", "--app", "chrome" }, "--keyterms needs a glossary file after it, or none, such as --keyterms \"%APPDATA%\\ClassScribe\\mantiq.txt\"; it took the option --app instead")]
    [TestCase(new[] { "class", "set", "Mantiq", "--keyterms", "terms|old.txt" }, "--keyterms takes a file; \"terms|old.txt\" has a character Windows does not allow in a path: < > \" | ? *")]
    [TestCase(new[] { "class", "set", "Mantiq" }, "class set needs something to change, such as ClassScribe class set \"Mantiq\" --app zoom --mic-follows zoom")]
    [TestCase(new[] { "class" }, "class needs one of list, show, add, set, rename or remove, such as ClassScribe class list")]
    public async Task Mistakes_are_named_before_anything_runs(string[] args, string mistake)
    {
        Assert.That(await _line.RunAsync(args), Is.EqualTo(2));

        Assert.That(_asked, Is.Empty);
        Assert.That(_line.Mistakes, Has.Member(mistake));
        Assert.That(_line.Output.ToString(), Does.Contain("ClassScribe class list"), "the help, with its examples");
    }

    /// <summary>class show's exit code 2, a class that cannot be used or is not there, is the answer, without the help; class is of its own kind.</summary>
    [Test]
    public async Task Show_answers_without_the_help_and_class_is_its_own_kind()
    {
        _commands.Setup(c => c.ClassAsync(It.IsAny<ClassRequest.Show>())).ReturnsAsync(2);

        Assert.That(await _line.RunAsync("class", "show", "Kalam"), Is.EqualTo(2));

        Assert.That(_line.Output.ToString(), Is.Empty);
        Assert.That(_line.KindOf("class", "list"), Is.EqualTo(CommandKind.Settings));
        Assert.That(_line.KindOf("class", "add", "Mantiq", "--app", "chrome"), Is.EqualTo(CommandKind.Settings));
    }

    /// <summary>Two class requests compared by their names and choices, the lists in the choices by their items.</summary>
    private static bool SameRequest(ClassRequest first, ClassRequest second) =>
        (first, second) switch
        {
            (ClassRequest.Add a, ClassRequest.Add b) => a.Name == b.Name && a.Choices with { Clear = [] } == b.Choices with { Clear = [] } && a.Choices.Clear.SequenceEqual(b.Choices.Clear),
            _ => first == second
        };
}
