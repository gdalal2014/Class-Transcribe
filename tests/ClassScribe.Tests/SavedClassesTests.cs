using ClassScribe.Audio;
using ClassScribe.Audio.Microphones;
using ClassScribe.Commands;
using ClassScribe.Settings;
using Microsoft.Extensions.Configuration;

namespace ClassScribe.Tests;

/// <summary>
/// The saved classes (layout-config 4.1 and 4.2, build-plan amendments 3 and 11): each checked on its own, so that a
/// problem in one stops only that one; their names matched exactly in any case; what each records, as a plan and in words.
/// </summary>
[TestFixture]
public class SavedClassesTests
{
    /// <summary>The saved classes of <paramref name="settings"/>, as the app reads them from the settings in force.</summary>
    private static SavedClasses Classes(Dictionary<string, string?> settings) => new(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());

    /// <summary>A class checked from <paramref name="fields"/> under the name Mantiq.</summary>
    private static SavedClassEntry Check(params (string Field, string Value)[] fields) =>
        SavedClasses.Check("Mantiq", fields.ToDictionary(field => field.Field, field => (string?)field.Value, StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// A class of the class alone from an app, of the class and the microphone following the app, and in person with a
    /// camera: each one's plan, which a run takes as it is, and its words for the menu.
    /// </summary>
    [Test]
    public void A_usable_class_gives_its_plan_and_its_words()
    {
        var withMicrophone = Check(("Course", "Mantiq"), ("Mode", "classandmicrophone"), ("App", "chrome.exe"), ("Microphone", "Microphone Array"), ("MicrophoneFollows", "chrome"));
        var inPerson = Check(("Course", "Kalam"), ("Mode", "Microphone"), ("Microphone", "OBSBOT"), ("Camera", "auto"), ("CameraResolution", "1080P"), ("Transcribe", "False"));
        var alone = Check(("Course", "Fiqh"), ("Device", "auto"));

        Assert.That(withMicrophone.Class!.Plan, Is.EqualTo(new SourcePlan(RecordingMode.ClassAndMicrophone, new ClassSource.App("chrome"),
            new MicrophoneChoice("Microphone Array", new FollowRule.Named("chrome")))));
        Assert.That(withMicrophone.Class.Describe(), Is.EqualTo("chrome, with your microphone (Microphone Array, following chrome)"));
        Assert.That(inPerson.Class!.Plan, Is.EqualTo(new SourcePlan(RecordingMode.Microphone, null, new MicrophoneChoice("OBSBOT"), new CameraChoice(null, "1080p"))));
        Assert.That(inPerson.Class.Describe(), Is.EqualTo("in person, your microphone (OBSBOT), with a camera (1080p), recorded only"));
        Assert.That((inPerson.Class.Transcribe, inPerson.Class.Course), Is.EqualTo((false, "Kalam")));
        Assert.That(alone.Class!.Plan, Is.EqualTo(new SourcePlan(RecordingMode.Class, new ClassSource.Automatic(), null)));
        Assert.That(alone.Class.Describe(), Is.EqualTo("the device the class plays on"));
    }

    /// <summary>Each rule a class breaks is named, and makes the class unusable; fields this build does not know pass.</summary>
    [TestCase("Mode=Lecture", "Mode is \"Lecture\"; it must be Class, ClassAndMicrophone or Microphone (in person).")]
    [TestCase("App=chrome;Device=Speakers", "App and Device are both set; a class records one app or one output device.")]
    [TestCase("Mode=Microphone;App=chrome", "App or Device is set, but the class is in person (Mode Microphone), which records no class.")]
    [TestCase("Microphone=OBSBOT", "Microphone is set, but the class records the class alone (Mode Class); set Mode to ClassAndMicrophone, or clear it.")]
    [TestCase("MicrophoneFollows=zoom", "MicrophoneFollows is set, but only a class recorded with your microphone beside it follows an app.")]
    [TestCase("Mode=ClassAndMicrophone;MicrophoneFollows=classscribe", "MicrophoneFollows is \"classscribe\"; it must be part of the class app's name, such as chrome, or auto or none, and never ClassScribe.")]
    [TestCase("Transcribe=maybe", "Transcribe is \"maybe\"; it must be true or false.")]
    [TestCase("Camera=OBSBOT", "Camera is set, but a lesson's picture is recorded only in person (Mode Microphone).")]
    [TestCase("Mode=Microphone;CameraResolution=720p", "CameraResolution is set, but the class records no camera.")]
    [TestCase("Course=Man/tiq", "Course, \"Man/tiq\", holds characters Windows does not allow in a folder's name, or spaces or dots at its end.")]
    public void Each_rule_a_class_breaks_is_named(string given, string problem)
    {
        var fields = given.Split(';').Select(pair => pair.Split('=')).ToDictionary(pair => pair[0], pair => (string?)pair[1], StringComparer.OrdinalIgnoreCase);
        fields.TryAdd("Course", "Mantiq");

        var entry = SavedClasses.Check("Mantiq", fields);

        Assert.That(entry.Usable, Is.False);
        Assert.That(entry.Problems, Has.Member(problem));
        Assert.That(entry.CannotBeUsed, Does.StartWith("The saved class \"Mantiq\" cannot be used: "));
    }

    /// <summary>A class without a course, or whose name or course would change as a folder's name, is unusable.</summary>
    [Test]
    public void A_course_is_required_and_names_must_be_folder_names()
    {
        Assert.That(SavedClasses.Check("Mantiq", new Dictionary<string, string?>()).Problems, Is.EqualTo(new[] { "Course is empty; it names the recording's folder and files, such as Mantiq." }));
        Assert.That(SavedClasses.Check("Mantiq.", new Dictionary<string, string?> { ["Course"] = "Mantiq" }).Usable, Is.False);
        Assert.That(SavedClasses.Check("Mantiq", new Dictionary<string, string?> { ["Course"] = new string('a', 81) }).Problems.Single(),
            Is.EqualTo("Course is 81 characters long; it may be at most 80."));
    }

    /// <summary>
    /// A class may name its own glossary (architecture 8.2, item 4), in any mode: a file, read trimmed as written, which its
    /// words name by the file's name; or none, which its words say. Without the field the class takes ElevenLabs:KeytermsFile,
    /// and its words are as before.
    /// </summary>
    [Test]
    public void A_class_may_name_its_own_glossary()
    {
        var own = Check(("Course", "Mantiq"), ("App", "chrome"), ("KeytermsFile", @"  %APPDATA%\ClassScribe\mantiq.txt  "));
        var none = Check(("Course", "Kalam"), ("Mode", "Microphone"), ("KeytermsFile", "NONE"), ("Transcribe", "false"));
        var setting = Check(("Course", "Fiqh"), ("Device", "auto"));

        Assert.That(own.Class!.KeytermsFile, Is.EqualTo(@"%APPDATA%\ClassScribe\mantiq.txt"));
        Assert.That(own.Class.Describe(), Is.EqualTo("chrome, glossary mantiq.txt"));
        Assert.That(none.Class!.KeytermsFile, Is.EqualTo("NONE"));
        Assert.That(none.Class.Describe(), Is.EqualTo("in person, your microphone (Windows' default), no glossary, recorded only"));
        Assert.That(setting.Class!.KeytermsFile, Is.Null);
        Assert.That(setting.Class.Describe(), Is.EqualTo("the device the class plays on"));
        Assert.That(ClassFields.All, Has.Member(ClassFields.KeytermsFile));
        Assert.That(ClassFields.KeytermsFile, Is.EqualTo("KeytermsFile"));
    }

    /// <summary>
    /// A glossary never makes a class unusable, since a glossary must never stop a recording: neither a file that is not there
    /// nor a value no path can hold; the run's start and the menu's header say what is wrong with it instead.
    /// </summary>
    [TestCase(@"C:\nowhere\mantiq.txt")]
    [TestCase("a\tb.txt")]
    [TestCase("mantiq|terms.txt")]
    public void A_glossary_never_makes_a_class_unusable(string glossary)
    {
        var entry = Check(("Course", "Mantiq"), ("App", "chrome"), ("KeytermsFile", glossary));

        Assert.That(entry.Usable, Is.True);
        Assert.That(entry.Class!.KeytermsFile, Is.EqualTo(glossary));
    }

    /// <summary>A field this build does not know, such as a later build's MicrophoneSpeaker, passes the check (amendment 3).</summary>
    [Test]
    public void A_field_of_a_later_build_passes()
    {
        Assert.That(Check(("Course", "Mantiq"), ("MicrophoneSpeaker", "Me"), ("FolderLayout", "{course}")).Usable, Is.True);
    }

    /// <summary>
    /// A problem in one class stops only that class: the others can still be used, and found by their names in any case,
    /// exactly, never by a prefix; a typed name may mean a class by its name, or else those whose course it is.
    /// </summary>
    [Test]
    public void A_problem_in_one_class_stops_only_that_class()
    {
        var classes = Classes(new()
        {
            ["Classes:Mantiq:Course"] = "Mantiq",
            ["Classes:Mantiq:App"] = "chrome",
            ["Classes:Sughra (Chrome):Course"] = "Aqidah - Sughra As-Sughra",
            ["Classes:Sughra (Chrome):App"] = "chrome",
            ["Classes:Sughra (Zoom app):Course"] = "Aqidah - Sughra As-Sughra",
            ["Classes:Sughra (Zoom app):App"] = "zoom",
            ["Classes:Sughra (Zoom app):Device"] = "Speakers"
        });

        Assert.That(classes.All().Select(entry => (entry.Name, entry.Usable)), Is.EqualTo(new[] { ("Mantiq", true), ("Sughra (Chrome)", true), ("Sughra (Zoom app)", false) }));
        Assert.That(classes.Find(" MANTIQ ")!.Name, Is.EqualTo("Mantiq"));
        Assert.That(classes.Find("Mant"), Is.Null, "never by a prefix");
        Assert.That(classes.Matching("mantiq").Select(entry => entry.Name), Is.EqualTo(new[] { "Mantiq" }));
        Assert.That(classes.Matching("Aqidah - Sughra As-Sughra").Select(entry => entry.Name), Is.EqualTo(new[] { "Sughra (Chrome)" }), "by course, among those that can be used");
        Assert.That(classes.FieldsOf("sughra (zoom app)")!["App"], Is.EqualTo("zoom"));
        Assert.That(classes.FieldsOf("Nothing"), Is.Null);
    }

    /// <summary>The saved classes are read from the settings in force at each use: a reload that adds a class shows it at once.</summary>
    [Test]
    public void The_classes_are_read_at_each_use()
    {
        var source = new Dictionary<string, string?>();
        var settings = new ConfigurationBuilder().AddInMemoryCollection(source).Build();
        var classes = new SavedClasses(settings);
        Assert.That(classes.All(), Is.Empty);

        settings["Classes:Mantiq:Course"] = "Mantiq";

        Assert.That(classes.All().Single().Usable, Is.True);
    }
}
