using System.Text;
using ClassScribe.Settings;
using ClassScribe.Storage;
using ClassScribe.Transcription;
using Microsoft.Extensions.Configuration;

namespace ClassScribe.Tests;

/// <summary>
/// The one resolver of the glossary (architecture 8.2, item 5), for every run and every later transcription: the command's
/// glossary first, the recording's own record next, then the run's class, a saved class of the recording's course,
/// ElevenLabs:KeytermsFile, and none; with the line that says which, and a glossary that is not there never stopping
/// anything. The settings folder and the recording are temporary folders; the glossaries are real files there.
/// </summary>
[TestFixture]
public class GlossaryChoiceTests
{
    private static readonly DateTimeOffset Started = new(2026, 11, 1, 14, 4, 42, TimeSpan.Zero);

    private TempFolder _folder = null!;
    private string _settings = null!;
    private RecordingFiles _files = null!;
    private RecordingLogger<GlossaryChoice> _log = null!;

    /// <summary>Creates the settings folder, a recording of Mantiq with no record yet, and the log to read.</summary>
    [SetUp]
    public void SetUp()
    {
        _folder = new TempFolder();
        _settings = _folder.File("settings");
        Directory.CreateDirectory(_settings);
        _files = RecordingFiles.ForNew(_folder.File("Mantiq"), "Mantiq", Started);
        Directory.CreateDirectory(_files.Folder);
        _log = new RecordingLogger<GlossaryChoice>();
    }

    /// <summary>Deletes the temporary folder.</summary>
    [TearDown]
    public void TearDown() => _folder.Dispose();

    /// <summary>The resolver over the settings folder, with <paramref name="keytermsFile"/> as ElevenLabs:KeytermsFile and the saved classes given, by setting name.</summary>
    private GlossaryChoice Choice(string keytermsFile = "", Dictionary<string, string?>? classes = null) =>
        new(new GlossaryFiles(new SettingsPlaces(_settings, userFolder: null)),
            new SavedClasses(new ConfigurationBuilder().AddInMemoryCollection(classes ?? []).Build()),
            TestOptions.Create(new ElevenLabsOptions { KeytermsFile = keytermsFile }), new ManualTimeProvider(Started), _log);

    /// <summary>Writes a glossary of <paramref name="terms"/> named <paramref name="name"/> into the settings folder and returns its full path.</summary>
    private string Glossary(string name, params string[] terms)
    {
        var path = Path.Combine(_settings, name);
        File.WriteAllLines(path, terms, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }

    /// <summary>The saved classes Mantiq (Chrome) and Mantiq (Zoom app), both of the course Mantiq, each with the glossary given; null leaves its field out.</summary>
    private static Dictionary<string, string?> TwoClasses(string? chrome, string? zoom)
    {
        var classes = new Dictionary<string, string?>
        {
            ["Classes:Mantiq (Chrome):Course"] = "Mantiq",
            ["Classes:Mantiq (Chrome):App"] = "chrome",
            ["Classes:Mantiq (Zoom app):Course"] = "Mantiq",
            ["Classes:Mantiq (Zoom app):App"] = "zoom"
        };
        if (chrome is not null) classes["Classes:Mantiq (Chrome):KeytermsFile"] = chrome;
        if (zoom is not null) classes["Classes:Mantiq (Zoom app):KeytermsFile"] = zoom;
        return classes;
    }

    /// <summary>ElevenLabs:KeytermsFile decides when no class does: its file, a relative name from the settings folder, with the line that names it and its terms.</summary>
    [Test]
    public void The_setting_decides_when_no_class_does()
    {
        var file = Glossary("keyterms-islamic-sciences.txt", "kalām", "Ashʿarī");

        var use = Choice("keyterms-islamic-sciences.txt").ForTranscript(_files, given: null);

        Assert.That((use.Path, use.From, use.Terms), Is.EqualTo((file, "ElevenLabs:KeytermsFile", new[] { "kalām", "Ashʿarī" })));
        Assert.That(_log.Messages, Is.EqualTo(new[] { "Glossary: keyterms-islamic-sciences.txt (ElevenLabs:KeytermsFile), 2 terms" }));
    }

    /// <summary>The command's glossary comes first, over the recording's record, the class of its course and the setting.</summary>
    [Test]
    public void The_commands_glossary_comes_first()
    {
        var other = Glossary("other.txt", "tawḥīd");
        Glossary("class.txt", "a");
        Glossary("setting.txt", "b");
        var choice = Choice("setting.txt", TwoClasses("class.txt", null));
        choice.ForTranscript(_files, given: null);
        _log.Clear();

        var use = choice.ForTranscript(_files, given: other);

        Assert.That((use.Path, use.From, use.Terms), Is.EqualTo((other, "transcribe --keyterms", new[] { "tawḥīd" })));
        Assert.That(_log.Messages, Is.EqualTo(new[] { "Glossary: other.txt (transcribe --keyterms), 1 term" }));
    }

    /// <summary>The command's none sends no glossary, whatever else would; the line says so.</summary>
    [Test]
    public void The_commands_none_sends_no_glossary()
    {
        Glossary("setting.txt", "b");

        var use = Choice("setting.txt").ForTranscript(_files, given: "NONE");

        Assert.That((use.Path, use.Terms), Is.EqualTo(("", Array.Empty<string>())));
        Assert.That(_log.Messages, Is.EqualTo(new[] { "No glossary: transcribe --keyterms says none" }));
    }

    /// <summary>
    /// The run's class decides at the start, over a class of the same course and the setting: its own glossary, named with the
    /// class; a class without one gives way to the next step.
    /// </summary>
    [Test]
    public void The_runs_class_decides_at_the_start()
    {
        var zoom = Glossary("zoom.txt", "a");
        var classes = TwoClasses("chrome.txt", "zoom.txt");

        var pick = Choice("setting.txt", classes).ForRun("Mantiq", className: "mantiq (zoom app)");
        var withoutOwn = Choice("setting.txt", TwoClasses(null, null)).ForRun("Mantiq", className: "Mantiq (Zoom app)");

        Assert.That(pick, Is.EqualTo(new GlossaryPick("zoom.txt", zoom, "the saved class \"Mantiq (Zoom app)\"", "Mantiq (Zoom app)")));
        Assert.That(withoutOwn, Is.EqualTo(new GlossaryPick("setting.txt", Path.Combine(_settings, "setting.txt"), "ElevenLabs:KeytermsFile")));
    }

    /// <summary>
    /// A saved class whose course is the recording's name counts for a start by name and for a later transcription alike, as
    /// Gulzar's launchers start by name; the two give the same answer.
    /// </summary>
    [Test]
    public void A_class_of_the_recordings_course_counts_for_a_start_by_name_and_a_later_transcription_alike()
    {
        var chrome = Glossary("chrome.txt", "kalām");
        var choice = Choice("setting.txt", TwoClasses("chrome.txt", null));

        var atStart = choice.ForRun("mantiq", className: null);
        var later = choice.ForTranscript(_files, given: null);

        Assert.That(atStart, Is.EqualTo(new GlossaryPick("chrome.txt", chrome, "the saved class \"Mantiq (Chrome)\"", "Mantiq (Chrome)")));
        Assert.That((later.Path, later.From), Is.EqualTo((atStart.Path, atStart.From)));
        Assert.That(_log.Messages, Has.Member("Glossary: chrome.txt (the saved class \"Mantiq (Chrome)\"), 1 term"));
    }

    /// <summary>Two classes of one course that name the same glossary give it; two that name different ones give the setting's, with a warning that names both.</summary>
    [Test]
    public void Two_classes_of_one_course_with_different_glossaries_give_the_settings_with_a_warning()
    {
        var setting = Glossary("setting.txt", "b");

        var same = Choice("setting.txt", TwoClasses("a.txt", Path.Combine(_settings, "A.TXT"))).ForRun("Mantiq", className: null);
        var different = Choice("setting.txt", TwoClasses("a.txt", "none")).ForRun("Mantiq", className: null);

        Assert.That(same.Path, Is.EqualTo(Path.Combine(_settings, "a.txt")));
        Assert.That(different, Is.EqualTo(new GlossaryPick("setting.txt", setting, "ElevenLabs:KeytermsFile",
            Warning: "The saved classes \"Mantiq (Chrome)\" and \"Mantiq (Zoom app)\" record Mantiq with different glossaries (a.txt, none), so ElevenLabs:KeytermsFile decides")));
    }

    /// <summary>A class's none sends no glossary, and the line names the class; an empty setting and the setting's none say so too.</summary>
    [Test]
    public void None_from_a_class_or_the_setting_sends_no_glossary_and_says_where_it_came_from()
    {
        Choice("setting.txt", TwoClasses("NONE", null)).ForTranscript(_files, given: null);
        Choice("").ForTranscript(_files with { BaseName = "Fiqh_2026-11-01_14-04-42Z" }, given: null);
        Choice("none").ForTranscript(_files with { BaseName = "Kalam_2026-11-01_14-04-42Z" }, given: null);

        Assert.That(_log.Messages, Is.EqualTo(new[]
        {
            "No glossary: the saved class \"Mantiq (Chrome)\" says none",
            "No glossary: ElevenLabs:KeytermsFile is empty",
            "No glossary: ElevenLabs:KeytermsFile says none"
        }));
        Assert.That(_log.Problems, Is.Empty);
    }

    /// <summary>
    /// A glossary that is not there gives a warning and no terms, and the transcript goes on: the warning names the file, where
    /// the value came from, and how to fix it, for the setting and for a class.
    /// </summary>
    [Test]
    public void A_missing_file_gives_a_warning_and_no_terms_and_the_transcript_goes_on()
    {
        var setting = Choice("gone.txt").ForTranscript(_files, given: null);
        var ofClass = Choice("setting.txt", TwoClasses("mantiq.txt", null)).ForTranscript(_files with { BaseName = "Mantiq_2026-11-01_15-00-00Z" }, given: null);

        Assert.That((setting.Terms, ofClass.Terms), Is.EqualTo((Array.Empty<string>(), Array.Empty<string>())));
        Assert.That(_log.Problems, Is.EqualTo(new[]
        {
            $"No glossary at {Path.Combine(_settings, "gone.txt")} (ElevenLabs:KeytermsFile), so the transcript is made without its terms. Put the file there, or point ElevenLabs:KeytermsFile at another: ClassScribe settings set ElevenLabs:KeytermsFile \"<file>\"",
            $"No glossary at {Path.Combine(_settings, "mantiq.txt")} (the saved class \"Mantiq (Chrome)\"), so the transcript is made without its terms. Put the file there, or point the class at another: ClassScribe class set \"Mantiq (Chrome)\" --keyterms \"<file>\""
        }));
    }

    /// <summary>A glossary that is there but cannot be read, as one held by another program, gives a warning with the reason and no terms; the transcript goes on.</summary>
    [Test]
    public void A_glossary_that_cannot_be_read_gives_a_warning_and_no_terms()
    {
        var file = Glossary("held.txt", "kalām");
        using var held = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None);

        var use = Choice("held.txt").ForTranscript(_files, given: null);

        Assert.That(use.Terms, Is.Empty);
        Assert.That(_log.Problems.Single(), Does.StartWith($"The glossary {file} (ElevenLabs:KeytermsFile) could not be read (").And.EndWith("), so the transcript is made without its terms"));
    }

    /// <summary>A term ElevenLabs would refuse is left out with the warning it always had, naming its line and why; the others go up.</summary>
    [Test]
    public void A_term_that_breaks_a_rule_is_left_out_with_its_line()
    {
        Glossary("keyterms.txt", "# terms", "kalām", "bad[term]");

        var use = Choice("keyterms.txt").ForTranscript(_files, given: null);

        Assert.That(use.Terms, Is.EqualTo(new[] { "kalām" }));
        Assert.That(_log.Problems, Is.EqualTo(new[] { @"Skipping keyterm on line 3 (bad[term]): it contains one of the characters < > { } [ ] \" }));
        Assert.That(_log.Messages, Has.Member("Glossary: keyterms.txt (ElevenLabs:KeytermsFile), 1 term"));
    }

    /// <summary>
    /// A YouTube plan counts the terms a course's recording would be sent with, by the same order (a class of the course, then
    /// the setting), quietly: no line, since nothing is sent yet.
    /// </summary>
    [Test]
    public void A_plan_counts_the_terms_of_the_course_quietly()
    {
        Glossary("risala.txt", "a", "b", "c");
        Glossary("setting.txt", "d");
        var classes = new Dictionary<string, string?> { ["Classes:Risala:Course"] = "Risala", ["Classes:Risala:App"] = "chrome", ["Classes:Risala:KeytermsFile"] = "risala.txt" };

        var choice = Choice("setting.txt", classes);

        Assert.That((choice.TermsFor("Risala"), choice.TermsFor("Kalam"), Choice("").TermsFor("Kalam")), Is.EqualTo((3, 1, 0)));
        Assert.That(_log.Entries.Where(entry => entry.Level > Microsoft.Extensions.Logging.LogLevel.Debug), Is.Empty);
    }
}
