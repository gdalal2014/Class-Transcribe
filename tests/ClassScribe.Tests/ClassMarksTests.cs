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

    /// <summary>
    /// A time typed by hand in the digits of another script, such as Arabic-Indic ones, is no mark: the line is skipped
    /// with a warning, as any other that is not a mark, rather than stopping the transcript with an error.
    /// </summary>
    [Test]
    public void A_time_in_the_digits_of_another_script_is_skipped_with_a_warning()
    {
        File.WriteAllLines(_files.Marks, ["٠٠:42:13 Definition of jawhar", "00:42:13.٢٥٠ Definition of ʿaraḍ", "00:50:00"]);

        Assert.That(_marks.Read(_files), Is.EqualTo(new[] { new ClassMark(1, TimeSpan.FromMinutes(50), null) }));
        Assert.That(_log.Problems, Has.Count.EqualTo(2));
    }

    /// <summary>
    /// A note goes into the marks file as its position, a tilde and its words, so that it survives a crash as a mark does,
    /// and an older ClassScribe reads it as a mark with a label rather than as a mistake.
    /// </summary>
    [Test]
    public void A_note_is_written_as_its_position_a_tilde_and_its_words()
    {
        _marks.AddNote(_files, new TimeSpan(0, 0, 42, 10, 0), "Not recorded: the laptop slept from 19:42 to 19:50");

        Assert.That(File.ReadAllLines(_files.Marks), Is.EqualTo(new[] { "00:42:10.000 ~ Not recorded: the laptop slept from 19:42 to 19:50" }));
    }

    /// <summary>
    /// Notes are read back in their place among the marks, without a number, and are no marks: the marks around them keep
    /// their numbers, and a mark set after a note gets the next mark's number, not one that counts the note.
    /// </summary>
    [Test]
    public void Notes_are_read_back_unnumbered_and_leave_the_numbering_of_marks_alone()
    {
        var first = _marks.Add(_files, TimeSpan.FromMinutes(1));
        _marks.AddNote(_files, TimeSpan.FromMinutes(2), "Not recorded: the laptop slept from 19:42 to 19:50");
        var second = _marks.Add(_files, TimeSpan.FromMinutes(3));

        Assert.That((first, second), Is.EqualTo((1, 2)));
        Assert.That(_marks.Read(_files), Is.EqualTo(new[]
        {
            new ClassMark(1, TimeSpan.FromMinutes(1), null),
            new ClassMark(0, TimeSpan.FromMinutes(2), "Not recorded: the laptop slept from 19:42 to 19:50", IsNote: true),
            new ClassMark(2, TimeSpan.FromMinutes(3), null)
        }));
        Assert.That(_log.Problems, Is.Empty);
    }

    /// <summary>
    /// A position before the start, which no recording has, is written as the start, for a mark and a note alike, so that
    /// both read back rather than being skipped as lines that are no marks.
    /// </summary>
    [Test]
    public void A_position_before_the_start_is_written_as_the_start()
    {
        _marks.Add(_files, TimeSpan.FromMilliseconds(-500));
        _marks.AddNote(_files, TimeSpan.FromMilliseconds(-500), "Not recorded: the laptop slept from 19:42 to 19:50");

        Assert.That(File.ReadAllLines(_files.Marks), Is.EqualTo(new[] { "00:00:00.000", "00:00:00.000 ~ Not recorded: the laptop slept from 19:42 to 19:50" }));
        Assert.That(_marks.Read(_files), Is.EqualTo(new[]
        {
            new ClassMark(1, TimeSpan.Zero, null),
            new ClassMark(0, TimeSpan.Zero, "Not recorded: the laptop slept from 19:42 to 19:50", IsNote: true)
        }));
        Assert.That(_log.Problems, Is.Empty);
    }

    /// <summary>A note keeps to one line of the marks file, whatever line breaks its words hold, so that the lines after it stay marks.</summary>
    [Test]
    public void A_note_keeps_to_one_line()
    {
        _marks.AddNote(_files, TimeSpan.FromSeconds(5), " Two\r\nlines \n");
        _marks.Add(_files, TimeSpan.FromSeconds(6));

        Assert.That(File.ReadAllLines(_files.Marks), Is.EqualTo(new[] { "00:00:05.000 ~ Two lines", "00:00:06.000" }));
    }

    /// <summary>A note needs words: one without any is refused, and nothing is written.</summary>
    [TestCase("")]
    [TestCase("  \r\n ")]
    public void A_note_without_words_is_refused(string text)
    {
        Assert.Throws<ArgumentException>(() => _marks.AddNote(_files, TimeSpan.FromSeconds(5), text));

        Assert.That(File.Exists(_files.Marks), Is.False);
    }

    /// <summary>
    /// A note written after the file was saved by hand as UTF-16, without a line break at its end, goes on a line of its
    /// own in the file's encoding, as a mark does.
    /// </summary>
    [Test]
    public void A_note_after_a_hand_edit_goes_on_a_line_of_its_own()
    {
        File.WriteAllText(_files.Marks, "00:42:13.250 Definition of jawhar", new UnicodeEncoding(bigEndian: false, byteOrderMark: true));

        _marks.AddNote(_files, TimeSpan.FromMinutes(50), "Not recorded: the laptop slept from 19:42 to 19:50");

        Assert.That(File.ReadAllLines(_files.Marks), Is.EqualTo(new[] { "00:42:13.250 Definition of jawhar", "00:50:00.000 ~ Not recorded: the laptop slept from 19:42 to 19:50" }));
    }

    /// <summary>
    /// Only a tilde, a space and words after the time make a note. A label typed by hand that starts with a tilde and no
    /// space, or is a tilde alone, stays a mark's label.
    /// </summary>
    [Test]
    public void Only_a_tilde_a_space_and_words_make_a_note()
    {
        File.WriteAllLines(_files.Marks, ["00:01:00 ~approximately here", "00:02:00 ~", "00:03:00 ~  A note typed by hand "]);

        Assert.That(_marks.Read(_files), Is.EqualTo(new[]
        {
            new ClassMark(1, TimeSpan.FromMinutes(1), "~approximately here"),
            new ClassMark(2, TimeSpan.FromMinutes(2), "~"),
            new ClassMark(0, TimeSpan.FromMinutes(3), "A note typed by hand", IsNote: true)
        }));
    }

    /// <summary>
    /// The note that marks where a recording was taken up again says when, in UTC as the time under the transcript's
    /// title is, and about how long was not recorded in between; it is the note that counts as a resume.
    /// </summary>
    [TestCase(-5, "less than a minute not recorded")]
    [TestCase(30, "less than a minute not recorded")]
    [TestCase(60, "about 1 minute not recorded")]
    [TestCase(89, "about 1 minute not recorded")]
    [TestCase(4 * 60 + 10, "about 4 minutes not recorded")]
    [TestCase(61 * 60, "about 1 hour 1 minute not recorded")]
    [TestCase(65 * 60, "about 1 hour 5 minutes not recorded")]
    [TestCase(120 * 60 + 20, "about 2 hours not recorded")]
    public void A_resume_note_says_when_and_about_how_long_was_not_recorded(int secondsNotRecorded, string gap)
    {
        _marks.AddResume(_files, new TimeSpan(0, 47, 10), new DateTimeOffset(2026, 10, 15, 23, 51, 30, TimeSpan.Zero), TimeSpan.FromSeconds(secondsNotRecorded));

        var note = _marks.Read(_files).Single();

        Assert.That(note, Is.EqualTo(new ClassMark(0, new TimeSpan(0, 47, 10), $"Resumed at 23:51 UTC, {gap}", IsNote: true)));
        Assert.That(note.IsResume, Is.True);
    }

    /// <summary>A recording taken up again on a later day, in UTC, than the one it started on says which day.</summary>
    [Test]
    public void A_resume_on_a_later_day_says_which_day()
    {
        _marks.AddResume(_files, TimeSpan.FromMinutes(47), new DateTimeOffset(2026, 10, 16, 0, 5, 0, TimeSpan.Zero), TimeSpan.FromMinutes(10));

        var note = _marks.Read(_files).Single();

        Assert.That(note.Label, Is.EqualTo("Resumed on 16 October at 00:05 UTC, about 10 minutes not recorded"));
        Assert.That(note.IsResume, Is.True);
    }

    /// <summary>
    /// A recording is made in one part, and one more for each time it was taken up again, as its resume notes say. Marks
    /// and other notes do not count, nor does a mark whose label was typed by hand to start the same way, nor a note typed
    /// by hand in other words than those ClassScribe writes, even when they start the same way.
    /// </summary>
    [Test]
    public void The_parts_of_a_recording_are_counted_from_its_resume_notes()
    {
        var resumed = new DateTimeOffset(2026, 10, 15, 23, 51, 0, TimeSpan.Zero);
        Assert.That(ClassMarks.PartsOf(_files), Is.EqualTo(1), "a recording without a marks file");

        _marks.Add(_files, TimeSpan.FromMinutes(1));
        _marks.AddNote(_files, TimeSpan.FromMinutes(2), "Not recorded: the laptop slept from 19:42 to 19:50");
        File.AppendAllLines(_files.Marks,
        [
            "00:02:30 Resumed at the board",
            "01:02:00 ~ Resumed after the break",
            "01:02:10 ~ Resumed at 23:51 UTC, after the break",
            "01:02:20 ~ Resumed at 23:51 UTC, about 4 minutes not recorded, said the teacher"
        ]);
        Assert.That(ClassMarks.PartsOf(_files), Is.EqualTo(1));
        Assert.That(_marks.Read(_files).Where(mark => mark.IsNote).Select(note => note.IsResume), Is.EqualTo(new[] { false, false, false, false }), "the notes are read, but none is a resume");

        _marks.AddResume(_files, TimeSpan.FromMinutes(3), resumed, TimeSpan.FromMinutes(4));
        _marks.AddResume(_files, TimeSpan.FromMinutes(5), resumed.AddMinutes(30), TimeSpan.FromMinutes(1));
        Assert.That(ClassMarks.PartsOf(_files), Is.EqualTo(3));
        Assert.That(ClassMarks.PartsIn(_marks.Read(_files)), Is.EqualTo(3));
    }

    /// <summary>
    /// The marks can be read, and the parts counted, while another ClassScribe holds the file open to add a mark, as a
    /// recorder does for a moment at every M.
    /// </summary>
    [Test]
    public void The_marks_can_be_read_while_a_mark_is_being_added()
    {
        _marks.AddResume(_files, TimeSpan.FromMinutes(3), new DateTimeOffset(2026, 10, 15, 23, 51, 0, TimeSpan.Zero), TimeSpan.FromMinutes(4));
        using var adding = new FileStream(_files.Marks, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);

        Assert.That(ClassMarks.PartsOf(_files), Is.EqualTo(2));
        Assert.That(_marks.Read(_files), Has.Count.EqualTo(1));
    }
}
