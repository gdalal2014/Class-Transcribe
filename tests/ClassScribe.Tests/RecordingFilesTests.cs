using ClassScribe.Storage;

namespace ClassScribe.Tests;

/// <summary>The files of one recording: named after it and the UTC time it started, all in its folder.</summary>
[TestFixture]
public class RecordingFilesTests
{
    private static readonly DateTimeOffset Started = new(2026, 10, 15, 20, 55, 27, TimeSpan.Zero);

    /// <summary>Every file carries the name and the start time; the first segment has no part number.</summary>
    [Test]
    public void Files_are_named_after_the_recording_and_its_start_time()
    {
        var files = RecordingFiles.ForNew(@"C:\Classes\Kalam", "Kalam", Started);

        Assert.That(files.BaseName, Is.EqualTo("Kalam_2026-10-15_20-55-27Z"));
        Assert.That(files.Name, Is.EqualTo("Kalam"));
        Assert.That(files.StartedUtc, Is.EqualTo(Started));
        Assert.That(files.Segment(1), Is.EqualTo(@"C:\Classes\Kalam\Kalam_2026-10-15_20-55-27Z.wav"));
        Assert.That(files.Segment(2), Is.EqualTo(@"C:\Classes\Kalam\Kalam_2026-10-15_20-55-27Z_part2.wav"));
        Assert.That(files.Marks, Is.EqualTo(@"C:\Classes\Kalam\Kalam_2026-10-15_20-55-27Z.marks.txt"));
        Assert.That(files.Upload, Is.EqualTo(@"C:\Classes\Kalam\Kalam_2026-10-15_20-55-27Z.upload.wav"));
        Assert.That(files.Transcript(), Is.EqualTo(@"C:\Classes\Kalam\Kalam_2026-10-15_20-55-27Z.md"));
        Assert.That(files.Json(), Is.EqualTo(@"C:\Classes\Kalam\Kalam_2026-10-15_20-55-27Z.json"));
        Assert.That(files.Transcript(2), Is.EqualTo(@"C:\Classes\Kalam\Kalam_2026-10-15_20-55-27Z (2).md"));
        Assert.That(files.Json(2), Is.EqualTo(@"C:\Classes\Kalam\Kalam_2026-10-15_20-55-27Z (2).json"));
    }

    /// <summary>A segment file's path says which recording it belongs to; other files do not pass for one.</summary>
    [Test]
    public void A_segment_path_is_recognised_as_its_recording()
    {
        var fromPart = RecordingFiles.TryParse(@"C:\Classes\Kalam\Kalam_2026-10-15_20-55-27Z_part2.wav");
        var fromFirst = RecordingFiles.TryParse(@"C:\Classes\Kalam\kalam_2026-10-15_20-55-27z.WAV");

        Assert.That(fromPart, Is.EqualTo(new RecordingFiles(@"C:\Classes\Kalam", "Kalam_2026-10-15_20-55-27Z")));
        Assert.That(fromFirst, Is.EqualTo(new RecordingFiles(@"C:\Classes\Kalam", "kalam_2026-10-15_20-55-27z")));
        Assert.That(RecordingFiles.TryParse(@"C:\Classes\Kalam\Kalam_2026-10-15_20-55-27Z.upload.wav"), Is.Null);
        Assert.That(RecordingFiles.TryParse(@"C:\Downloads\Lecture 5.wav"), Is.Null);
        Assert.That(RecordingFiles.TryParse(@"C:\Classes\Kalam\Kalam_2026-10-15_20-55-27Z.md"), Is.Null);
    }

    /// <summary>A recording without a time stamp in its name, such as one named by hand, has no start time.</summary>
    [Test]
    public void A_name_without_a_time_stamp_has_no_start_time()
    {
        var files = new RecordingFiles(@"C:\Classes\Old", "lesson-notes");

        Assert.That(files.Name, Is.EqualTo("lesson-notes"));
        Assert.That(files.StartedUtc, Is.Null);
    }

    /// <summary>The recording's segments are found in order, and nothing else in the folder passes for one.</summary>
    [Test]
    public void Segments_are_listed_in_order()
    {
        using var folder = new TempFolder();
        var files = RecordingFiles.ForNew(folder.Path, "Kalam", Started);
        foreach (var name in new[] { files.Segment(3), files.Segment(1), files.Segment(2), files.Upload, folder.File("Kalam_2026-10-15_20-55-27Z.marks.txt"), folder.File("Kalam_2026-10-15_21-00-00Z.wav") })
            File.WriteAllBytes(name, [0]);

        Assert.That(files.FindSegments(), Is.EqualTo(new[] { files.Segment(1), files.Segment(2), files.Segment(3) }));
        Assert.That(files.HasTranscript, Is.False);
    }
}
