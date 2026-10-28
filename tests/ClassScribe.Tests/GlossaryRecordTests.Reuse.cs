using System.Text;
using ClassScribe.Audio;
using ClassScribe.Commands;
using ClassScribe.Runs;
using ClassScribe.Settings;
using ClassScribe.Transcription;
using Microsoft.Extensions.Configuration;

namespace ClassScribe.Tests;

/// <summary>
/// The record decided once, when a recording starts, and kept with it (architecture 8.2, items 5 and 6, Gulzar's answer to
/// question 11): written at the start before any transcript, reused by every later transcription with a line that says so,
/// a changed file noted, a missing one decided again with a warning, transcribe --keyterms over it and recorded, a
/// continued recording keeping it, and each transcript's entry added.
/// </summary>
public partial class GlossaryRecordTests
{
    /// <summary>The settings folder, where relative glossaries are.</summary>
    private string Settings => _folder.File("settings");

    /// <summary>
    /// The resolver over the settings folder, with <paramref name="keytermsFile"/> as ElevenLabs:KeytermsFile, the saved
    /// classes given, and its log in <paramref name="log"/>.
    /// </summary>
    private GlossaryChoice Choice(RecordingLogger<GlossaryChoice> log, string keytermsFile = "", Dictionary<string, string?>? classes = null, TimeProvider? time = null) =>
        new(_glossaries, new SavedClasses(new ConfigurationBuilder().AddInMemoryCollection(classes ?? []).Build()),
            TestOptions.Create(new ElevenLabsOptions { KeytermsFile = keytermsFile }), time ?? new ManualTimeProvider(Started), log);

    /// <summary>Writes a glossary of <paramref name="terms"/> named <paramref name="name"/> into the settings folder and returns its full path.</summary>
    private string Glossary(string name, params string[] terms)
    {
        Directory.CreateDirectory(Settings);
        var path = Path.Combine(Settings, name);
        File.WriteAllLines(path, terms, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }

    /// <summary>A run's request for the recording of Mantiq, started as <paramref name="className"/> when given, with the glossary the window decided.</summary>
    private static RecordRequest Request(GlossaryPick? glossary = null, string? className = null) =>
        new(RunIds.New(Started), "Mantiq", true, new SourcePlan(RecordingMode.Class, new ClassSource.App("chrome"), null), @"C:\Classes", ContinueChoice.New, null, null, true,
            ClassName: className, Glossary: glossary);

    /// <summary>
    /// The record is written as the run starts, before any transcript, so that a recording made with record-only has it: the
    /// window's decision as the request carries it, with the file's terms and hash then; and the start line names it.
    /// </summary>
    [Test]
    public void The_record_is_written_at_the_start_before_any_transcript()
    {
        var file = Glossary("mantiq.txt", "kalām", "Ashʿarī");
        var log = new RecordingLogger<GlossaryChoice>();

        Choice(log).AtStart(_files, Request(new GlossaryPick("mantiq.txt", file, "the saved class \"Mantiq\"", "Mantiq")));

        var record = _glossaries.ReadRecord(_files)!;
        Assert.That(record.Decided with { Sha256 = null }, Is.EqualTo(new GlossaryDecision("mantiq.txt", file, "the saved class \"Mantiq\"", 2, null, Started)));
        Assert.That(record.Decided.Sha256, Has.Length.EqualTo(64));
        Assert.That(record.Sent, Is.Empty);
        Assert.That(log.Messages, Is.EqualTo(new[] { "Glossary: mantiq.txt (the saved class \"Mantiq\"), 2 terms" }));
    }

    /// <summary>
    /// A request without a glossary, from a window of an older build, is decided where the run is: steps 3 to 6 over the
    /// settings it has, here ElevenLabs:KeytermsFile; and the window's warning, carried in the request, is said at the start.
    /// </summary>
    [Test]
    public void A_request_without_a_glossary_is_decided_at_the_start_and_a_carried_warning_is_said()
    {
        var file = Glossary("setting.txt", "kalām");
        var log = new RecordingLogger<GlossaryChoice>();

        Choice(log, "setting.txt").AtStart(_files, Request());
        Choice(log).AtStart(_files with { BaseName = "Mantiq_2026-11-01_15-00-00Z" }, Request(new GlossaryPick("", "", "ElevenLabs:KeytermsFile", Warning: "Two classes disagree")));

        Assert.That(_glossaries.ReadRecord(_files)!.Decided.Path, Is.EqualTo(file));
        Assert.That(log.Messages, Is.EqualTo(new[] { "Glossary: setting.txt (ElevenLabs:KeytermsFile), 1 term", "Two classes disagree", "No glossary: ElevenLabs:KeytermsFile is empty" }));
    }

    /// <summary>
    /// A transcript adds its entry: the transcript's copy, the file sent, its terms and hash, when, and where it came from;
    /// the next transcript adds the next.
    /// </summary>
    [Test]
    public void A_transcript_adds_its_entry()
    {
        var file = Glossary("mantiq.txt", "kalām");
        var log = new RecordingLogger<GlossaryChoice>();
        var time = new ManualTimeProvider(Started);
        var choice = Choice(log, time: time);
        choice.AtStart(_files, Request(new GlossaryPick("mantiq.txt", file, "the saved class \"Mantiq\"", "Mantiq")));
        time.Advance(TimeSpan.FromHours(2));

        choice.Sent(_files, choice.ForTranscript(_files, given: null), _files.Transcript());
        choice.Sent(_files, choice.ForTranscript(_files, given: null), _files.Transcript(2));

        var sent = _glossaries.ReadRecord(_files)!.Sent;
        Assert.That(sent.Select(entry => (entry.Copy, entry.Path, entry.Terms, entry.Utc, entry.From)), Is.EqualTo(new[]
        {
            (1, file, 1, Started.AddHours(2), "as decided when this recording started"), (2, file, 1, Started.AddHours(2), "as decided when this recording started")
        }));
        Assert.That(sent[0].Sha256, Is.EqualTo(_glossaries.ReadRecord(_files)!.Decided.Sha256));
    }

    /// <summary>
    /// Every later transcription, transcribe --again among them, uses the glossary decided at the start, whatever the settings
    /// say now, with the line that says so and both counts.
    /// </summary>
    [Test]
    public void A_later_transcription_reuses_the_decided_glossary_and_says_so()
    {
        var first = Glossary("first.txt", "kalām", "Ashʿarī");
        Glossary("second.txt", "tawḥīd");
        var log = new RecordingLogger<GlossaryChoice>();
        Choice(log, "first.txt").AtStart(_files, Request());
        log.Clear();

        var use = Choice(log, "second.txt").ForTranscript(_files, given: null);

        Assert.That((use.Path, use.Terms), Is.EqualTo((first, new[] { "kalām", "Ashʿarī" })));
        Assert.That(log.Messages, Is.EqualTo(new[] { "Glossary: first.txt, as decided when this recording started (2 terms then, 2 now)" }));
    }

    /// <summary>A glossary edited since the start is used as it is now, and the line says it changed, with both counts.</summary>
    [Test]
    public void A_changed_file_is_used_as_it_is_now_and_noted_with_both_counts()
    {
        var file = Glossary("mantiq.txt", "kalām", "Ashʿarī");
        var log = new RecordingLogger<GlossaryChoice>();
        Choice(log, "mantiq.txt").AtStart(_files, Request());
        File.AppendAllLines(file, ["tawḥīd"]);
        log.Clear();

        var use = Choice(log, "mantiq.txt").ForTranscript(_files, given: null);

        Assert.That(use.Terms, Has.Count.EqualTo(3));
        Assert.That(log.Messages, Is.EqualTo(new[] { "Glossary: mantiq.txt, as decided when this recording started; changed since: 2 terms then, 3 now" }));
    }

    /// <summary>A recording decided without a glossary keeps none, whatever the settings say now, and the line says why.</summary>
    [Test]
    public void A_recording_decided_without_a_glossary_keeps_none()
    {
        Glossary("later.txt", "kalām");
        var log = new RecordingLogger<GlossaryChoice>();
        Choice(log, "").AtStart(_files, Request());
        log.Clear();

        var use = Choice(log, "later.txt").ForTranscript(_files, given: null);

        Assert.That(use.Terms, Is.Empty);
        Assert.That(log.Messages, Is.EqualTo(new[] { "No glossary, as decided when this recording started (ElevenLabs:KeytermsFile is empty)" }));
    }

    /// <summary>
    /// When the decided glossary is gone, the glossary is decided again by steps 3 to 6, with a warning that names the missing
    /// file; the record keeps what was decided at the start.
    /// </summary>
    [Test]
    public void A_missing_decided_file_is_decided_again_with_a_warning()
    {
        var gone = Glossary("gone.txt", "kalām");
        var now = Glossary("now.txt", "tawḥīd");
        var log = new RecordingLogger<GlossaryChoice>();
        Choice(log, "gone.txt").AtStart(_files, Request());
        File.Delete(gone);
        log.Clear();

        var use = Choice(log, "now.txt").ForTranscript(_files, given: null);

        Assert.That((use.Path, use.Terms), Is.EqualTo((now, new[] { "tawḥīd" })));
        Assert.That(log.Messages, Is.EqualTo(new[]
        {
            $"The glossary decided when this recording started, {gone}, is not there now, so the glossary is decided again",
            "Glossary: now.txt (ElevenLabs:KeytermsFile), 1 term"
        }));
        Assert.That(_glossaries.ReadRecord(_files)!.Decided.Path, Is.EqualTo(gone));
    }

    /// <summary>transcribe --keyterms sends another glossary over the decided one, and its entry says so; what was decided stays.</summary>
    [Test]
    public void Keyterms_overrides_and_is_recorded()
    {
        var decided = Glossary("decided.txt", "kalām");
        var other = Glossary("other.txt", "tawḥīd", "fiqh");
        var log = new RecordingLogger<GlossaryChoice>();
        var choice = Choice(log, "decided.txt");
        choice.AtStart(_files, Request());

        choice.Sent(_files, choice.ForTranscript(_files, given: other), _files.Transcript());

        var record = _glossaries.ReadRecord(_files)!;
        Assert.That(record.Decided.Path, Is.EqualTo(decided));
        Assert.That(record.Sent.Single() with { Sha256 = null, Utc = default }, Is.EqualTo(new GlossarySent(1, other, 2, null, default, "transcribe --keyterms")));
    }

    /// <summary>
    /// A continued recording keeps its base name, so it keeps its record and its glossary: its start says the decided glossary
    /// again, and writes nothing over it, whatever the request decided this time.
    /// </summary>
    [Test]
    public void A_continued_recording_keeps_its_record()
    {
        var first = Glossary("first.txt", "kalām");
        var second = Glossary("second.txt", "tawḥīd");
        var log = new RecordingLogger<GlossaryChoice>();
        Choice(log, "first.txt").AtStart(_files, Request());
        var before = File.ReadAllText(_files.Glossary);
        log.Clear();

        Choice(log, "second.txt").AtStart(_files, Request(new GlossaryPick("second.txt", second, "ElevenLabs:KeytermsFile")));

        Assert.That(File.ReadAllText(_files.Glossary), Is.EqualTo(before));
        Assert.That(log.Messages, Is.EqualTo(new[] { "Glossary: first.txt, as decided when this recording started (1 term then, 1 now)" }));
        Assert.That(_glossaries.ReadRecord(_files)!.Decided.Path, Is.EqualTo(first));
    }

    /// <summary>
    /// A recording made before this version, or a file from elsewhere, has no record: its first transcription decides by steps
    /// 4 to 6 and writes the record then, so that the next one reuses it.
    /// </summary>
    [Test]
    public void A_recording_without_a_record_gets_one_at_its_first_transcription()
    {
        var file = Glossary("setting.txt", "kalām");
        var log = new RecordingLogger<GlossaryChoice>();

        var use = Choice(log, "setting.txt").ForTranscript(_files, given: null);

        Assert.That(use.Path, Is.EqualTo(file));
        Assert.That(_glossaries.ReadRecord(_files)!.Decided with { Sha256 = null }, Is.EqualTo(new GlossaryDecision("setting.txt", file, "ElevenLabs:KeytermsFile", 1, null, Started)));
    }

    /// <summary>A record that cannot be read is said, the glossary is decided again, and the record is left as it is, never written over.</summary>
    [Test]
    public void An_unreadable_record_is_left_as_it_is_and_the_glossary_decided_again()
    {
        var file = Glossary("setting.txt", "kalām");
        File.WriteAllText(_files.Glossary, "not json {");
        var log = new RecordingLogger<GlossaryChoice>();
        var choice = Choice(log, "setting.txt");

        var use = choice.ForTranscript(_files, given: null);
        choice.Sent(_files, use, _files.Transcript());

        Assert.That(use.Path, Is.EqualTo(file));
        Assert.That(File.ReadAllText(_files.Glossary), Is.EqualTo("not json {"));
        Assert.That(log.Problems.Single(), Does.StartWith($"The glossary record {_files.Glossary} could not be read (").And.EndWith("); the glossary is decided again, and the record is left as it is"));
    }

    /// <summary>A record that cannot be written is said, and nothing else is affected: the start goes on, and the transcript gets its glossary.</summary>
    [Test]
    public void A_record_that_cannot_be_written_is_said_and_nothing_else_is_affected()
    {
        var file = Glossary("setting.txt", "kalām");
        var log = new RecordingLogger<GlossaryChoice>();
        var gone = _files with { Folder = _folder.File("no such folder") };

        Choice(log, "setting.txt").AtStart(gone, Request());
        var use = Choice(log, "setting.txt").ForTranscript(gone, given: null);

        Assert.That(use.Terms, Is.EqualTo(new[] { "kalām" }));
        Assert.That(use.Path, Is.EqualTo(file));
        Assert.That(log.Problems, Has.Count.EqualTo(2));
        Assert.That(log.Problems, Has.All.StartWith($"Could not write the glossary record {gone.Glossary} ("));
    }
}
