using ClassScribe.Audio;
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

    /// <summary>A segment file's path says which recording it belongs to, in any letter case, and keeps the case of its name.</summary>
    [Test]
    public void A_segment_path_is_recognised_as_its_recording()
    {
        var fromPart = RecordingFiles.TryParse(@"C:\Classes\Kalam\Kalam_2026-10-15_20-55-27Z_part2.wav");
        var fromFirst = RecordingFiles.TryParse(@"C:\Classes\Kalam\kalam_2026-10-15_20-55-27z.WAV");

        Assert.That(fromPart, Is.EqualTo(new RecordingFiles(@"C:\Classes\Kalam", "Kalam_2026-10-15_20-55-27Z")));
        Assert.That(fromFirst, Is.EqualTo(new RecordingFiles(@"C:\Classes\Kalam", "kalam_2026-10-15_20-55-27z")));
    }

    /// <summary>
    /// Any file of a recording says which recording it belongs to: a segment of either track, the marks, the log, the
    /// upload copy, and every copy of the transcript and of ElevenLabs' answer, an answer kept as unread included, in
    /// any letter case.
    /// </summary>
    [TestCase("Kalam_2026-10-15_20-55-27Z.wav")]
    [TestCase("Kalam_2026-10-15_20-55-27Z_part12.wav")]
    [TestCase("Kalam_2026-10-15_20-55-27Z.mic.wav")]
    [TestCase("Kalam_2026-10-15_20-55-27Z_part3.mic.wav")]
    [TestCase("Kalam_2026-10-15_20-55-27Z.marks.txt")]
    [TestCase("Kalam_2026-10-15_20-55-27Z.MARKS.TXT")]
    [TestCase("Kalam_2026-10-15_20-55-27Z.log")]
    [TestCase("Kalam_2026-10-15_20-55-27Z.upload.wav")]
    [TestCase("Kalam_2026-10-15_20-55-27Z.md")]
    [TestCase("Kalam_2026-10-15_20-55-27Z (2).md")]
    [TestCase("Kalam_2026-10-15_20-55-27Z.json")]
    [TestCase("Kalam_2026-10-15_20-55-27Z (2).json")]
    [TestCase("Kalam_2026-10-15_20-55-27Z.unread.json")]
    [TestCase("Kalam_2026-10-15_20-55-27Z (3).unread.json")]
    public void Any_file_of_a_recording_is_recognised_as_its_recording(string fileName) =>
        Assert.That(RecordingFiles.TryParse(Path.Combine(@"C:\Classes\Kalam", fileName)), Is.EqualTo(new RecordingFiles(@"C:\Classes\Kalam", "Kalam_2026-10-15_20-55-27Z")));

    /// <summary>
    /// A file that is no recording's does not pass for one, even beside a recording and named after it: a file without a
    /// time stamp, a note of one's own, and a file of a kind ClassScribe never writes.
    /// </summary>
    [TestCase(@"C:\Downloads\Lecture 5.wav")]
    [TestCase(@"C:\Downloads\Lecture 5.md")]
    [TestCase(@"C:\Classes\Kalam\Kalam_2026-10-15_20-55-27Z notes.md")]
    [TestCase(@"C:\Classes\Kalam\Kalam_2026-10-15_20-55-27Z.mp3")]
    [TestCase(@"C:\Classes\Kalam\Kalam_2026-10-15_20-55-27Z.txt")]
    [TestCase(@"C:\Classes\Kalam\Kalam_2026-10-15_20-55-27Z.wav.bak")]
    [TestCase(@"C:\Classes\Kalam\Kalam_2026-10-15_20-55-27Z_part2.md")]
    [TestCase(@"C:\Classes\Kalam\Kalam_2026-10-15_20-55-27Z (2).wav")]
    [TestCase(@"C:\Classes\Kalam\Kalam_2026-10-15_20-55-27Z.mic.json")]
    public void Other_files_do_not_pass_for_a_recordings(string path) =>
        Assert.That(RecordingFiles.TryParse(path), Is.Null);

    /// <summary>The log of everything the recording's runs logged is named after the recording, beside its other files.</summary>
    [Test]
    public void The_log_is_named_after_the_recording() =>
        Assert.That(RecordingFiles.ForNew(@"C:\Classes\Kalam", "Kalam", Started).Log, Is.EqualTo(@"C:\Classes\Kalam\Kalam_2026-10-15_20-55-27Z.log"));

    /// <summary>
    /// The microphone's segments, whose names end in .mic.wav, are found apart from the main track's, each track in the
    /// order of its parts, and neither track takes the other's files.
    /// </summary>
    [Test]
    public void The_microphones_segments_are_found_apart_from_the_main_tracks()
    {
        using var folder = new TempFolder();
        var files = RecordingFiles.ForNew(folder.Path, "Kalam", Started);
        var micFirst = folder.File("Kalam_2026-10-15_20-55-27Z.mic.wav");
        var micThird = folder.File("Kalam_2026-10-15_20-55-27Z_part3.mic.wav");
        foreach (var path in new[] { micThird, files.Segment(2), micFirst, files.Segment(1), files.Upload })
            File.WriteAllBytes(path, [0]);

        Assert.That(files.FindSegments(), Is.EqualTo(new[] { files.Segment(1), files.Segment(2) }));
        Assert.That(files.FindSegments(Track.Main), Is.EqualTo(new[] { files.Segment(1), files.Segment(2) }));
        Assert.That(files.FindSegments(Track.Microphone), Is.EqualTo(new[] { micFirst, micThird }));
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

    /// <summary>
    /// An answer that was paid for but did not become a transcript is named apart from the transcripts' answers,
    /// with (2), (3) and so on for later ones, so that it neither counts as a transcript nor takes the next one's place.
    /// </summary>
    [Test]
    public void An_answer_that_did_not_become_a_transcript_is_named_apart()
    {
        var files = RecordingFiles.ForNew(@"C:\Classes\Kalam", "Kalam", Started);

        Assert.That(files.UnreadAnswer(), Is.EqualTo(@"C:\Classes\Kalam\Kalam_2026-10-15_20-55-27Z.unread.json"));
        Assert.That(files.UnreadAnswer(2), Is.EqualTo(@"C:\Classes\Kalam\Kalam_2026-10-15_20-55-27Z (2).unread.json"));
    }

    /// <summary>The next free name for such an answer passes over the ones already kept, so none of them is ever overwritten.</summary>
    [Test]
    public void The_next_free_name_for_an_unread_answer_passes_over_those_already_kept()
    {
        using var folder = new TempFolder();
        var files = RecordingFiles.ForNew(folder.Path, "Kalam", Started);
        Assert.That(files.NextFreeUnreadAnswer(), Is.EqualTo(files.UnreadAnswer(1)));

        File.WriteAllText(files.UnreadAnswer(1), "{}");
        Assert.That(files.NextFreeUnreadAnswer(), Is.EqualTo(files.UnreadAnswer(2)));

        File.WriteAllText(files.UnreadAnswer(2), "{}");
        Assert.That(files.NextFreeUnreadAnswer(), Is.EqualTo(files.UnreadAnswer(3)));
    }

    /// <summary>
    /// A recording counts as transcribed when any copy of its transcript is there, as when the first was deleted and
    /// a later one kept, so that the menu still warns that transcribing it again is billed again.
    /// </summary>
    [Test]
    public void Any_copy_of_the_transcript_counts_as_a_transcript()
    {
        using var folder = new TempFolder();
        var files = RecordingFiles.ForNew(folder.Path, "Kalam", Started);
        File.WriteAllText(files.Transcript(2), "# Kalam");

        Assert.That(files.HasTranscript, Is.True);
    }

    /// <summary>
    /// Only a transcript of this recording counts: not ElevenLabs' answer on its own, not an answer that could not be
    /// read, not another recording's transcript, and not another Markdown file whose name starts the same way.
    /// </summary>
    [Test]
    public void Other_files_do_not_count_as_a_transcript()
    {
        using var folder = new TempFolder();
        var files = RecordingFiles.ForNew(folder.Path, "Kalam", Started);
        var later = RecordingFiles.ForNew(folder.Path, "Kalam", Started.AddHours(1));
        foreach (var path in new[] { files.Segment(1), files.Json(), files.Json(2), files.UnreadAnswer(), later.Transcript(), folder.File("Kalam_2026-10-15_20-55-27Z notes.md") })
            File.WriteAllText(path, "");

        Assert.That(files.HasTranscript, Is.False);
    }

    /// <summary>A recording whose folder is not there has no transcript, and asking does not fail.</summary>
    [Test]
    public void A_recording_whose_folder_is_gone_has_no_transcript()
    {
        using var folder = new TempFolder();
        var files = RecordingFiles.ForNew(folder.File("gone"), "Kalam", Started);

        Assert.That(files.HasTranscript, Is.False);
    }

    /// <summary>
    /// The answers that were paid for but did not become a transcript are found among the recording's files: every
    /// unread answer, and a copy's answer whose transcript is missing, as when the transcript could not be written.
    /// </summary>
    [Test]
    public void Answers_kept_without_a_transcript_are_found()
    {
        using var folder = new TempFolder();
        var files = RecordingFiles.ForNew(folder.Path, "Kalam", Started);
        foreach (var path in new[] { files.UnreadAnswer(), files.UnreadAnswer(2), files.Json(2) })
            File.WriteAllText(path, "{}");

        Assert.That(files.FindKeptAnswers(), Is.EquivalentTo(new[] { files.UnreadAnswer(), files.UnreadAnswer(2), files.Json(2) }));
    }

    /// <summary>
    /// A transcript's answer with its transcript beside it is not a kept answer, and neither is an answer of another
    /// recording whose name starts the same way, nor another file. A folder that is not there holds none.
    /// </summary>
    [Test]
    public void Other_files_are_not_taken_for_kept_answers()
    {
        using var folder = new TempFolder();
        var files = RecordingFiles.ForNew(folder.Path, "Kalam", Started);
        var longerName = RecordingFiles.ForNew(folder.Path, "Kalam_2026-10-15_20-55-27Z extra", Started.AddDays(1));
        File.WriteAllText(files.Json(), "{}");
        File.WriteAllText(files.Transcript(), "# Kalam");
        foreach (var path in new[] { files.Segment(1), longerName.UnreadAnswer(), longerName.Json(), folder.File("Kalam_2026-10-15_20-55-27Z notes.json") })
            File.WriteAllText(path, "");

        Assert.That(files.FindKeptAnswers(), Is.Empty);
        Assert.That(RecordingFiles.ForNew(folder.File("gone"), "Kalam", Started).FindKeptAnswers(), Is.Empty);
    }
}
