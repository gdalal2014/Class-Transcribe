using System.Text;
using ClassScribe.Storage;

namespace ClassScribe.Tests;

/// <summary>The marks set with M while recording, kept in the recording's marks file.</summary>
[TestFixture]
public class ClassMarksTests
{
    private TempFolder _folder = null!;
    private RecordingFiles _files = null!;
    private RecordingLogger<ClassMarks> _log = null!;
    private ClassMarks _marks = null!;

    /// <summary>Creates a temporary recording folder and the marks under test.</summary>
    [SetUp]
    public void SetUp()
    {
        _folder = new TempFolder();
        _files = new RecordingFiles(_folder.Path, "Kalam_2026-10-15_20-55-27Z");
        _log = new RecordingLogger<ClassMarks>();
        _marks = new ClassMarks(_log);
    }

    /// <summary>Deletes the temporary folder.</summary>
    [TearDown]
    public void TearDown() => _folder.Dispose();

    /// <summary>Each mark is written at once with its position, and numbered in the order it was set.</summary>
    [Test]
    public void Marks_are_added_with_their_position_and_numbered()
    {
        var first = _marks.Add(_files, TimeSpan.FromSeconds(5.25));
        var second = _marks.Add(_files, new TimeSpan(1, 2, 3));

        Assert.That((first, second), Is.EqualTo((1, 2)));
        Assert.That(File.ReadAllLines(_files.Marks), Is.EqualTo(new[] { "00:00:05.250", "01:02:03.000" }));
    }

    /// <summary>A mark more than a day into a recording keeps all its hours, and reads back as it was set.</summary>
    [Test]
    public void A_mark_after_a_day_keeps_all_its_hours()
    {
        _marks.Add(_files, new TimeSpan(1, 1, 0, 0, 500));

        Assert.That(File.ReadAllLines(_files.Marks), Is.EqualTo(new[] { "25:00:00.500" }));
        Assert.That(_marks.Read(_files).Single().Position, Is.EqualTo(new TimeSpan(1, 1, 0, 0, 500)));
    }

    /// <summary>A marks file that exists but is empty gets the mark on its first line.</summary>
    [Test]
    public void A_mark_in_an_empty_file_goes_on_its_first_line()
    {
        File.WriteAllBytes(_files.Marks, []);

        Assert.That(_marks.Add(_files, TimeSpan.FromSeconds(5.25)), Is.EqualTo(1));
        Assert.That(File.ReadAllText(_files.Marks), Is.EqualTo("00:00:05.250" + Environment.NewLine));
    }

    /// <summary>
    /// A mark set after the file was edited by hand goes on a line of its own, even when the editor left
    /// no line break at the end or saved the file as UTF-16, as Notepad can. The mark is written in the
    /// file's own encoding, so the label typed in and the new mark both survive.
    /// </summary>
    [TestCase("UTF-8", false)]
    [TestCase("UTF-8", true)]
    [TestCase("UTF-8 with BOM", false)]
    [TestCase("UTF-16 LE", false)]
    [TestCase("UTF-16 LE", true)]
    [TestCase("UTF-16 BE", false)]
    public void A_mark_after_a_hand_edit_goes_on_a_line_of_its_own(string savedAs, bool endsWithLineBreak)
    {
        Encoding encoding = savedAs switch
        {
            "UTF-8" => new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            "UTF-8 with BOM" => new UTF8Encoding(encoderShouldEmitUTF8Identifier: true),
            "UTF-16 LE" => new UnicodeEncoding(bigEndian: false, byteOrderMark: true),
            _ => new UnicodeEncoding(bigEndian: true, byteOrderMark: true)
        };
        File.WriteAllText(_files.Marks, "00:42:13.250 Definition of jawhar" + (endsWithLineBreak ? "\r\n" : ""), encoding);

        var number = _marks.Add(_files, TimeSpan.FromMinutes(50));

        Assert.That(number, Is.EqualTo(2));
        Assert.That(_marks.Read(_files), Is.EqualTo(new[]
        {
            new ClassMark(1, new TimeSpan(0, 0, 42, 13, 250), "Definition of jawhar"),
            new ClassMark(2, TimeSpan.FromMinutes(50), null)
        }));
        Assert.That(_log.Problems, Is.Empty);
        Assert.That(File.ReadAllLines(_files.Marks), Is.EqualTo(new[] { "00:42:13.250 Definition of jawhar", "00:50:00.000" }));
    }

    /// <summary>A label typed after the time by hand is read with the mark.</summary>
    [Test]
    public void Marks_are_read_with_their_labels()
    {
        File.WriteAllLines(_files.Marks, ["00:42:13.250 Definition of jawhar", "01:00:00"]);

        var marks = _marks.Read(_files);

        Assert.That(marks, Is.EqualTo(new[]
        {
            new ClassMark(1, new TimeSpan(0, 0, 42, 13, 250), "Definition of jawhar"),
            new ClassMark(2, TimeSpan.FromHours(1), null)
        }));
        Assert.That(_log.Problems, Is.Empty);
    }

    /// <summary>Blank lines and comments are ignored; anything else that is not a mark is skipped with a warning.</summary>
    [Test]
    public void Lines_that_are_not_marks_are_skipped()
    {
        File.WriteAllLines(_files.Marks, ["", "# set during the break", "around the middle", "00:01:00"]);

        var marks = _marks.Read(_files);

        Assert.That(marks, Is.EqualTo(new[] { new ClassMark(1, TimeSpan.FromMinutes(1), null) }));
        Assert.That(_log.Problems, Has.Count.EqualTo(1));
        Assert.That(_log.Problems[0], Does.Contain("around the middle"));
    }

    /// <summary>A recording made without marks simply has none.</summary>
    [Test]
    public void A_recording_without_marks_has_none() =>
        Assert.That(_marks.Read(_files), Is.Empty);
}
