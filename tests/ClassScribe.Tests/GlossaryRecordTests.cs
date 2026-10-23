using System.Text.Json.Nodes;
using ClassScribe.Settings;
using ClassScribe.Storage;
using ClassScribe.Transcription;

namespace ClassScribe.Tests;

/// <summary>
/// The recording's glossary record, &lt;base&gt;.glossary.json (architecture 8.2, item 6): what was decided when the recording
/// started and what each transcript sent, written whole and read leniently, as run files are. Every folder is a temporary one.
/// </summary>
[TestFixture]
public partial class GlossaryRecordTests
{
    private static readonly DateTimeOffset Started = new(2026, 11, 1, 14, 4, 42, TimeSpan.Zero);

    private TempFolder _folder = null!;
    private RecordingFiles _files = null!;
    private GlossaryFiles _glossaries = null!;

    /// <summary>Creates a temporary folder, a recording's files in it, and the glossary files over a settings folder of its own.</summary>
    [SetUp]
    public void SetUp()
    {
        _folder = new TempFolder();
        _files = RecordingFiles.ForNew(_folder.File("Mantiq"), "Mantiq", Started);
        Directory.CreateDirectory(_files.Folder);
        _glossaries = new GlossaryFiles(new SettingsPlaces(_folder.File("settings"), userFolder: null));
    }

    /// <summary>Deletes the temporary folder.</summary>
    [TearDown]
    public void TearDown() => _folder.Dispose();

    /// <summary>A record as a recording's start writes it, and one transcript's entry.</summary>
    private static GlossaryRecord Example() => new(GlossaryRecord.CurrentVersion,
        new GlossaryDecision(@"%APPDATA%\ClassScribe\keyterms-islamic-sciences.txt", @"C:\Users\x\AppData\Roaming\ClassScribe\keyterms-islamic-sciences.txt",
            "ElevenLabs:KeytermsFile", 94, "58659e75", Started),
        [new GlossarySent(1, @"C:\Users\x\AppData\Roaming\ClassScribe\keyterms-islamic-sciences.txt", 94, "58659e75", Started.AddHours(2), "ElevenLabs:KeytermsFile")]);

    /// <summary>A record written is read back as it was, beside the recording, under its name.</summary>
    [Test]
    public void A_record_is_read_back_as_it_was_written()
    {
        var record = Example();

        _glossaries.WriteRecord(_files, record);
        var read = _glossaries.ReadRecord(_files);

        Assert.That(File.Exists(_files.Glossary), Is.True);
        Assert.That(read, Is.Not.Null);
        Assert.That(read!.Decided, Is.EqualTo(record.Decided));
        Assert.That(read.Sent, Is.EqualTo(record.Sent));
        Assert.That(read.Version, Is.EqualTo(1));
    }

    /// <summary>
    /// The file reads as architecture 8.2 shows it: indented, its fields in camelCase, a path with Arabic in it written as it
    /// is, so that a person can read it, and an entry of a transcript that sent nothing without a hash.
    /// </summary>
    [Test]
    public void The_file_reads_as_the_design_shows_it()
    {
        var arabic = _folder.File("المنطق.txt");
        var record = Example() with { Sent = [new GlossarySent(2, arabic, 0, null, Started.AddHours(3))] };

        _glossaries.WriteRecord(_files, record);
        var text = File.ReadAllText(_files.Glossary);
        var json = JsonNode.Parse(text)!;

        Assert.That(text, Does.Contain(Environment.NewLine + "  \"decided\""), "indented");
        Assert.That(text.Contains("المنطق", StringComparison.Ordinal), Is.True, "Arabic as it is");
        Assert.That(json["version"]!.GetValue<int>(), Is.EqualTo(1));
        Assert.That(json["decided"]!.AsObject().Select(field => field.Key), Is.EqualTo(new[] { "setting", "path", "from", "terms", "sha256", "utc" }));
        Assert.That(json["sent"]![0]!["copy"]!.GetValue<int>(), Is.EqualTo(2));
        Assert.That(json["sent"]![0]!["sha256"], Is.Null);
    }

    /// <summary>
    /// Fields a later build adds are passed over when the record is read, and kept when it is written back, as when a
    /// transcript adds its entry: at the top, in what was decided and in each transcript's entry.
    /// </summary>
    [Test]
    public void Unknown_fields_are_passed_over_and_kept()
    {
        File.WriteAllText(_files.Glossary, """
            {
              "version": 2,
              "later": "kept",
              "decided": { "setting": "", "path": "", "from": "ElevenLabs:KeytermsFile", "terms": 0, "sha256": null, "utc": "2026-11-01T14:04:42Z", "why": "kept too" },
              "sent": [ { "copy": 1, "path": "", "terms": 0, "sha256": null, "utc": "2026-11-01T16:31:07Z", "model": "scribe_v3" } ]
            }
            """);

        var read = _glossaries.ReadRecord(_files)!;
        _glossaries.WriteRecord(_files, read with { Sent = [.. read.Sent, new GlossarySent(2, "", 0, null, Started)] });
        var json = JsonNode.Parse(File.ReadAllText(_files.Glossary))!;

        Assert.That(read.Version, Is.EqualTo(2));
        Assert.That(read.Decided.From, Is.EqualTo("ElevenLabs:KeytermsFile"));
        Assert.That((string?)json["later"], Is.EqualTo("kept"));
        Assert.That((string?)json["decided"]!["why"], Is.EqualTo("kept too"));
        Assert.That((string?)json["sent"]![0]!["model"], Is.EqualTo("scribe_v3"));
        Assert.That(json["sent"]!.AsArray(), Has.Count.EqualTo(2));
    }

    /// <summary>A recording without a record has none, and asking does not fail, even when its folder is gone.</summary>
    [Test]
    public void A_recording_without_a_record_has_none()
    {
        Assert.That(_glossaries.ReadRecord(_files), Is.Null);
        Assert.That(_glossaries.ReadRecord(_files with { Folder = _folder.File("gone") }), Is.Null);
    }

    /// <summary>A file that is no record, one that is not JSON or one without what was decided, is refused, naming the file.</summary>
    [TestCase("not json {")]
    [TestCase("{\"version\":1}")]
    [TestCase("null")]
    [TestCase("[1, 2]")]
    public void A_file_that_is_no_record_is_refused_with_its_name(string text)
    {
        File.WriteAllText(_files.Glossary, text);

        var refused = Assert.Throws<InvalidDataException>(() => _glossaries.ReadRecord(_files));

        Assert.That(refused!.Message, Does.Contain(_files.Glossary));
    }

    /// <summary>A record is written whole, replacing the one before, and nothing of the write is left beside it.</summary>
    [Test]
    public void A_record_is_written_whole_and_leaves_nothing_beside_it()
    {
        _glossaries.WriteRecord(_files, Example());
        _glossaries.WriteRecord(_files, Example() with { Sent = [] });

        Assert.That(_glossaries.ReadRecord(_files)!.Sent, Is.Empty);
        Assert.That(Directory.GetFiles(_files.Folder).Select(Path.GetFileName), Is.EqualTo(new[] { Path.GetFileName(_files.Glossary) }));
    }
}
