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
