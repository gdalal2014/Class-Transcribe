using System.Text.Json;
using ClassScribe.Commands;
using ClassScribe.Recorder;
using ClassScribe.Runs;
using ClassScribe.Transcription;

namespace ClassScribe.Tests;

/// <summary>
/// The glossary as a recorder decides it: its settings hold no saved classes (the window leaves them out of the snapshot it
/// hands over), so the run's glossary is the one the window decided and the request carries. When the recording's record
/// gives no glossary at the upload, because it could not be read or written, or names a file that is gone, the transcript
/// sends the run's glossary, the one its start line named, never ElevenLabs:KeytermsFile's in its place; a continued
/// recording whose decided file is gone keeps the glossary decided again at its start; and a request whose glossary lacks a
/// field never stops the class.
/// </summary>
public partial class GlossaryRecordTests
{
    /// <summary>The window's decision for a run of the saved class Mantiq, whose glossary is <paramref name="file"/>.</summary>
    private static GlossaryPick OfTheClass(string file) => new("mantiq.txt", file, "the saved class \"Mantiq\"", "Mantiq");

    /// <summary>
    /// A record that cannot be read, as a continued recording's damaged one: the start says the class's glossary, and the
    /// transcript sends it too, with the same line, rather than the setting's; the damaged record is left as it is.
    /// </summary>
    [Test]
    public void In_a_recorder_an_unreadable_record_sends_the_glossary_the_start_named()
    {
        var mantiq = Glossary("mantiq.txt", "kalām", "manṭiq");
        Glossary("setting.txt", "other");
        File.WriteAllText(_files.Glossary, "not json {");
        var log = new RecordingLogger<GlossaryChoice>();
        var choice = Choice(log, "setting.txt");

        var run = choice.AtStart(_files, Request(OfTheClass(mantiq), "Mantiq"));
        var use = choice.ForTranscript(_files, given: null, run);

        Assert.That((use.Path, use.From, use.Terms), Is.EqualTo((mantiq, "the saved class \"Mantiq\"", new[] { "kalām", "manṭiq" })));
        Assert.That(log.Messages.Except(log.Problems), Is.EqualTo(new[] { "Glossary: mantiq.txt (the saved class \"Mantiq\"), 2 terms" }));
        Assert.That(log.Messages.Count(message => message.StartsWith("Glossary: ", StringComparison.Ordinal)), Is.EqualTo(2), "said at the start and before the upload");
        Assert.That(File.ReadAllText(_files.Glossary), Is.EqualTo("not json {"));
    }

    /// <summary>A record that could not be written at the start: the transcript sends the class's glossary the start named, and its record is written then.</summary>
    [Test]
    public void In_a_recorder_a_record_that_could_not_be_written_sends_the_glossary_the_start_named()
    {
        var mantiq = Glossary("mantiq.txt", "kalām");
        Glossary("setting.txt", "other");
        var log = new RecordingLogger<GlossaryChoice>();
        var choice = Choice(log, "setting.txt");
        var later = _files with { Folder = _folder.File("made later") };

        var run = choice.AtStart(later, Request(OfTheClass(mantiq), "Mantiq"));
        Directory.CreateDirectory(later.Folder);
        var use = choice.ForTranscript(later, given: null, run);

        Assert.That((use.Path, use.Terms), Is.EqualTo((mantiq, new[] { "kalām" })));
        Assert.That(_glossaries.ReadRecord(later)!.Decided.From, Is.EqualTo("the saved class \"Mantiq\""));
    }

    /// <summary>
    /// A continued recording whose record named another glossary, there at the start and gone at the upload: the glossary is
    /// decided again as this run decided it, the class's, not by the setting.
    /// </summary>
    [Test]
    public void In_a_recorder_a_decided_file_gone_at_the_upload_gives_the_runs_glossary()
    {
        var first = Glossary("first.txt", "kalām");
        var mantiq = Glossary("mantiq.txt", "manṭiq");
        Glossary("setting.txt", "other");
        var log = new RecordingLogger<GlossaryChoice>();
        Choice(log, "first.txt").AtStart(_files, Request());
        var choice = Choice(log, "setting.txt");
        var run = choice.AtStart(_files, Request(OfTheClass(mantiq), "Mantiq"));
        File.Delete(first);
        log.Clear();

        var use = choice.ForTranscript(_files, given: null, run);

        Assert.That((use.Path, use.Terms), Is.EqualTo((mantiq, new[] { "manṭiq" })));
        Assert.That(log.Messages, Is.EqualTo(new[]
        {
            $"The glossary decided when this recording started, {first}, is not there now, so the glossary is decided again",
            "Glossary: mantiq.txt (the saved class \"Mantiq\"), 1 term"
        }));
    }

    /// <summary>
    /// A continued recording whose decided file is gone at its start: the glossary decided again there is written into the
    /// record, so that its transcripts, this run's and later ones, send what the start said; the earlier transcripts' entries stay.
    /// </summary>
    [Test]
    public void A_continued_start_whose_decided_file_is_gone_keeps_the_new_decision_in_the_record()
    {
        var first = Glossary("first.txt", "kalām");
        var mantiq = Glossary("mantiq.txt", "manṭiq", "kalām");
        var log = new RecordingLogger<GlossaryChoice>();
        var time = new ManualTimeProvider(Started);
        var choice = Choice(log, "first.txt", time: time);
        choice.AtStart(_files, Request());
        choice.Sent(_files, choice.ForTranscript(_files, given: null), _files.Transcript());
        File.Delete(first);
        time.Advance(TimeSpan.FromHours(1));
        log.Clear();

        Choice(log, "setting.txt", time: time).AtStart(_files, Request(OfTheClass(mantiq), "Mantiq"));
        var later = Choice(log, "setting.txt").ForTranscript(_files, given: null);

        var record = _glossaries.ReadRecord(_files)!;
        Assert.That(record.Decided with { Sha256 = null }, Is.EqualTo(new GlossaryDecision("mantiq.txt", mantiq, "the saved class \"Mantiq\"", 2, null, Started.AddHours(1))));
        Assert.That(record.Sent.Single().Path, Is.EqualTo(first), "the first transcript's entry stays");
        Assert.That(later.Path, Is.EqualTo(mantiq));
        Assert.That(log.Messages, Is.EqualTo(new[]
        {
            $"The glossary decided when this recording started, {first}, is not there now, so the glossary is decided again",
            "Glossary: mantiq.txt (the saved class \"Mantiq\"), 2 terms",
            "Glossary: mantiq.txt, as decided when this recording started (2 terms then, 2 now)"
        }));
    }

    /// <summary>
    /// The class's glossary missing at the start: the warning says that putting the file there lets this recording's next
    /// transcript send it, how to send another, and that the class's own glossary changes from its next recording, since a
    /// change made during the class cannot reach this one.
    /// </summary>
    [Test]
    public void The_advice_for_a_missing_class_glossary_says_what_reaches_this_recording()
    {
        var log = new RecordingLogger<GlossaryChoice>();

        Choice(log, "setting.txt").AtStart(_files, Request(OfTheClass(Path.Combine(Settings, "mantiq.txt")), "Mantiq"));

        Assert.That(log.Problems, Is.EqualTo(new[]
        {
            $"No glossary at {Path.Combine(Settings, "mantiq.txt")} (the saved class \"Mantiq\"), so the transcript is made without its terms. Put the file there, and this recording's next transcript sends it, or send another: ClassScribe transcribe \"<recording>\" --again --keyterms \"<file>\". To point the class at another from its next recording: ClassScribe class set \"Mantiq\" --keyterms \"<file>\""
        }));
    }

    /// <summary>
    /// A request whose glossary lacks a field, as a request or run file of another shape or a damaged one reads, never throws
    /// at the start, where the class is recording already: the warning says so, and the glossary is decided here, as for a
    /// request that carries none, and written into the record. Handed to a transcript of a recording without a record, it is
    /// passed over the same way.
    /// </summary>
    [TestCase("""{ "from": "ElevenLabs:KeytermsFile" }""")]
    [TestCase("""{ "setting": "mantiq.txt", "from": "the saved class \"Mantiq\"" }""")]
    [TestCase("""{ "setting": "mantiq.txt", "path": "C:\\mantiq.txt" }""")]
    public void A_request_whose_glossary_lacks_a_field_never_throws_and_is_decided_here(string glossary)
    {
        var setting = Glossary("setting.txt", "kalām");
        var json = $$"""
            {
              "runId": "{{RunIds.New(Started)}}", "name": "Mantiq", "transcribe": false,
              "source": { "mode": "Class", "class": { "kind": "automatic" }, "microphone": null },
              "outputRoot": "D:\\Classes", "continue": "New", "continueBaseName": null, "minutes": null, "open": true,
              "glossary": {{glossary}}
            }
            """;
        var request = JsonSerializer.Deserialize(json, RecorderJson.Default.RecordRequest)!;
        var log = new RecordingLogger<GlossaryChoice>();
        var choice = Choice(log, "setting.txt");

        GlossaryPick run = null!;
        Assert.DoesNotThrow(() => run = choice.AtStart(_files, request));
        var use = choice.ForTranscript(_files with { BaseName = "Mantiq_2026-11-01_15-00-00Z" }, given: null, request.Glossary);

        Assert.That((run.Path, use.Path), Is.EqualTo((setting, setting)));
        Assert.That(_glossaries.ReadRecord(_files)!.Decided.Path, Is.EqualTo(setting));
        Assert.That(log.Problems, Is.EqualTo(Enumerable.Repeat("The glossary this run's request carries is incomplete, so the glossary is decided here instead", 2)));
        Assert.That(log.Messages, Has.Member("Glossary: setting.txt (ElevenLabs:KeytermsFile), 1 term"));
    }
}
