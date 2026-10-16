using System.Collections.Concurrent;
using System.Text;
using ClassScribe.Storage;
using ClassScribe.Transcription;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClassScribe.Tests;

/// <summary>
/// The numbered copies of a recording's transcript and of ElevenLabs' answer beside it: each new one takes the next
/// free number, and nothing already there is ever overwritten, not even by another ClassScribe saving at the same moment.
/// </summary>
[TestFixture]
public class TranscriptCopiesTests
{
    private static readonly DateTimeOffset Started = new(2026, 10, 15, 20, 55, 27, TimeSpan.Zero);

    private TempFolder _folder = null!;
    private RecordingFiles _files = null!;

    /// <summary>Creates a temporary folder holding the folder of one recording.</summary>
    [SetUp]
    public void SetUp()
    {
        _folder = new TempFolder();
        _files = RecordingFiles.ForNew(_folder.File("Kalam"), "Kalam", Started);
        Directory.CreateDirectory(_files.Folder);
    }

    /// <summary>Deletes the temporary folder. That fails if a test left a file open, which is the point.</summary>
    [TearDown]
    public void TearDown() => _folder.Dispose();

    /// <summary>Saves a transcript of <paramref name="markdown"/> with the answer <paramref name="json"/>, as the workflow does.</summary>
    private TranscriptFiles Save(string json, string markdown) => TranscriptCopies.Save(_files, json, () => markdown, NullLogger.Instance);

    /// <summary>The bytes of <paramref name="text"/> in UTF-8, without a byte order mark.</summary>
    private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);

    /// <summary>The first transcript is copy 1, beside its answer, both in UTF-8 without a byte order mark, transliterated Arabic included.</summary>
    [Test]
    public void The_first_transcript_and_its_answer_are_copy_1()
    {
        const string json = """{"text":"Kalām of the Ashʿarī."}""";
        const string markdown = "# Kalam\n\nKalām of the Ashʿarī.\n";

        var saved = Save(json, markdown);

        Assert.That(saved, Is.EqualTo(new TranscriptFiles(_files.Transcript(1), _files.Json(1))));
        Assert.That(File.ReadAllBytes(saved.JsonPath), Is.EqualTo(Utf8(json)));
        Assert.That(File.ReadAllBytes(saved.TranscriptPath), Is.EqualTo(Utf8(markdown)));
    }

    /// <summary>A later transcript takes the next copy, and the earlier one stays as it was.</summary>
    [Test]
    public void A_later_transcript_takes_the_next_copy_and_leaves_the_earlier_one_as_it_was()
    {
        Save("""{"first":1}""", "first");

        var second = Save("""{"second":2}""", "second");

        Assert.That(second, Is.EqualTo(new TranscriptFiles(_files.Transcript(2), _files.Json(2))));
        Assert.That(File.ReadAllText(_files.Json(1)), Is.EqualTo("""{"first":1}"""));
        Assert.That(File.ReadAllText(_files.Transcript(1)), Is.EqualTo("first"));
    }

    /// <summary>
    /// A file already at the next number, made by hand or left without its partner, is never overwritten, and is never
    /// given a partner that does not belong to it: the transcript takes the copy after it.
    /// </summary>
    [TestCase(".json")]
    [TestCase(".md")]
    public void A_file_already_at_the_next_number_is_never_overwritten(string extension)
    {
        Save("""{"first":1}""", "first");
        var there = extension == ".json" ? _files.Json(2) : _files.Transcript(2);
        File.WriteAllText(there, "made by hand");

        var saved = Save("""{"third":3}""", "third");

        Assert.That(saved, Is.EqualTo(new TranscriptFiles(_files.Transcript(3), _files.Json(3))));
        Assert.That(File.ReadAllText(there), Is.EqualTo("made by hand"));
        Assert.That(File.Exists(_files.Json(2)) && File.Exists(_files.Transcript(2)), Is.False, "the file at copy 2 got no partner");
    }

    /// <summary>
    /// When another ClassScribe takes the transcript's name at the moment this one is about to write it, the answer
    /// moves on with its transcript to the next copy: the other's transcript is left as it is, and no answer of this
    /// one is left beside it. The transcript is made once.
    /// </summary>
    [Test]
    public void A_transcript_name_taken_at_that_moment_moves_the_answer_on_with_its_transcript()
    {
        var made = 0;

        var saved = TranscriptCopies.Save(_files, """{"ours":1}""", () =>
        {
            made++;
            File.WriteAllText(_files.Transcript(1), "theirs");   // another ClassScribe, just now
            return "ours";
        }, NullLogger.Instance);

        Assert.That(saved, Is.EqualTo(new TranscriptFiles(_files.Transcript(2), _files.Json(2))));
        Assert.That(File.ReadAllText(_files.Transcript(2)), Is.EqualTo("ours"));
        Assert.That(File.ReadAllText(_files.Json(2)), Is.EqualTo("""{"ours":1}"""));
        Assert.That(File.ReadAllText(_files.Transcript(1)), Is.EqualTo("theirs"));
        Assert.That(File.Exists(_files.Json(1)), Is.False, "no answer of ours is left beside their transcript");
        Assert.That(made, Is.EqualTo(1));
    }

    /// <summary>
    /// The answer is saved before the transcript is made from it, so when the transcript cannot be made, the answer is
    /// on disk all the same, and the error names it.
    /// </summary>
    [Test]
    public void When_the_transcript_cannot_be_made_the_answer_is_saved_and_the_error_names_it()
    {
        var error = Assert.Throws<OverflowException>(() =>
            TranscriptCopies.Save(_files, """{"text":"Hello"}""", () => throw new OverflowException("A word's time is out of range."), NullLogger.Instance));

        Assert.That(error!.Data[TranscriptCopies.SavedAnswerKey], Is.EqualTo(_files.Json(1)));
        Assert.That(File.ReadAllText(_files.Json(1)), Is.EqualTo("""{"text":"Hello"}"""));
        Assert.That(File.Exists(_files.Transcript(1)), Is.False);
    }

    /// <summary>When the transcript cannot be written (a folder is in its way here), the answer stays saved, and the error names it.</summary>
    [Test]
    public void When_the_transcript_cannot_be_written_the_answer_stays_saved_and_the_error_names_it()
    {
        Directory.CreateDirectory(_files.Transcript(1));

        var error = Assert.Throws<UnauthorizedAccessException>(() => Save("""{"text":"Hello"}""", "Hello"));

        Assert.That(error!.Data[TranscriptCopies.SavedAnswerKey], Is.EqualTo(_files.Json(1)));
        Assert.That(File.ReadAllText(_files.Json(1)), Is.EqualTo("""{"text":"Hello"}"""));
    }

    /// <summary>When the answer itself cannot be written (a folder is in its way here), the error names no saved answer, and no transcript is made.</summary>
    [Test]
    public void When_the_answer_cannot_be_written_the_error_names_no_saved_answer()
    {
        Directory.CreateDirectory(_files.Json(1));
        var made = false;

        var error = Assert.Throws<UnauthorizedAccessException>(() => TranscriptCopies.Save(_files, "{}", () =>
        {
            made = true;
            return "Hello";
        }, NullLogger.Instance));

        Assert.That(error!.Data.Contains(TranscriptCopies.SavedAnswerKey), Is.False);
        Assert.That(made, Is.False);
        Assert.That(File.Exists(_files.Transcript(1)), Is.False);
    }

    /// <summary>
    /// A write that stops half-way with <paramref name="failure"/>, as when the disk fills up, in each file whose name ends
    /// in <paramref name="extension"/>, once the file has been created; any other file is written in full.
    /// </summary>
    private static Action<FileStream, byte[]> FailingHalfWayIn(string extension, Exception failure) => (file, bytes) =>
    {
        if (!file.Name.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
        {
            file.Write(bytes);
            return;
        }

        file.Write(bytes, 0, bytes.Length / 2);
        throw failure;
    };

    /// <summary>The error Windows gives when the disk is full.</summary>
    private static IOException DiskFull() => new("There is not enough space on the disk.");

    /// <summary>
    /// An answer that cannot be written in full once its file is made, as when the disk fills up, is removed again, so
    /// that no half-written answer passes for a whole one. The error that stopped the write is the one reported, it
    /// names no saved answer, and no transcript is made.
    /// </summary>
    [Test]
    public void An_answer_that_cannot_be_written_in_full_is_removed_and_its_error_reported()
    {
        var full = DiskFull();
        var made = false;

        var error = Assert.Throws<IOException>(() => TranscriptCopies.Save(_files, """{"text":"Hello world."}""", () =>
        {
            made = true;
            return "Hello world.";
        }, NullLogger.Instance, FailingHalfWayIn(".json", full)));

        Assert.That(error, Is.SameAs(full));
        Assert.That(error!.Data.Contains(TranscriptCopies.SavedAnswerKey), Is.False);
        Assert.That(File.Exists(_files.Json(1)), Is.False, "the half-written answer is removed");
        Assert.That(made, Is.False);
        Assert.That(File.Exists(_files.Transcript(1)), Is.False);
    }

    /// <summary>
    /// A transcript that cannot be written in full once its file is made is removed again, while the answer saved
    /// before it stays whole; the error that stopped the write is the one reported, and it names the saved answer.
    /// </summary>
    [Test]
    public void A_transcript_that_cannot_be_written_in_full_is_removed_and_the_answer_stays_saved()
    {
        var full = DiskFull();

        var error = Assert.Throws<IOException>(() =>
            TranscriptCopies.Save(_files, """{"text":"Hello world."}""", () => "# Kalam\n\nHello world.\n", NullLogger.Instance, FailingHalfWayIn(".md", full)));

        Assert.That(error, Is.SameAs(full));
        Assert.That(error!.Data[TranscriptCopies.SavedAnswerKey], Is.EqualTo(_files.Json(1)));
        Assert.That(File.Exists(_files.Transcript(1)), Is.False, "the half-written transcript is removed");
        Assert.That(File.ReadAllText(_files.Json(1)), Is.EqualTo("""{"text":"Hello world."}"""));
    }

    /// <summary>A rebuilt transcript that cannot be written in full once its file is made is removed again, and the error that stopped the write is the one reported.</summary>
    [Test]
    public void A_rebuilt_transcript_that_cannot_be_written_in_full_is_removed_and_its_error_reported()
    {
        var full = DiskFull();

        var error = Assert.Throws<IOException>(() => TranscriptCopies.SaveRebuilt(_files, "# Kalam\n\nHello world.\n", NullLogger.Instance, FailingHalfWayIn(".md", full)));

        Assert.That(error, Is.SameAs(full));
        Assert.That(File.Exists(_files.Transcript(1)), Is.False, "the half-written transcript is removed");
    }

    /// <summary>
    /// A half-written file that cannot be removed, because another program holds it open at that moment, is left with a
    /// warning that names it, and the error that stopped the write is still the one reported.
    /// </summary>
    [Test]
    public void A_half_written_file_that_cannot_be_removed_is_named_in_a_warning_and_the_write_error_reported()
    {
        var full = DiskFull();
        var log = new RecordingLogger<TranscriptCopiesTests>();
        FileStream? holder = null;
        try
        {
            var error = Assert.Throws<IOException>(() => TranscriptCopies.SaveRebuilt(_files, "# Kalam\n\nHello world.\n", log, (file, bytes) =>
            {
                holder = new FileStream(file.Name, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);   // open, and not willing to let it be deleted
                file.Write(bytes, 0, bytes.Length / 2);
                throw full;
            }));

            Assert.That(error, Is.SameAs(full));
            Assert.That(log.Problems, Has.Some.Contains(_files.Transcript(1)).And.Contains("could not be written in full"));
        }
        finally
        {
            holder?.Dispose();
        }
    }

    /// <summary>
    /// A rebuilt transcript is written alone, without an answer of its own, at the first copy that has neither file: an
    /// answer kept without its transcript, or a transcript made by hand, keeps its number to itself.
    /// </summary>
    [TestCase(".json")]
    [TestCase(".md")]
    public void A_rebuilt_transcript_takes_the_first_copy_that_has_neither_file(string extension)
    {
        Save("""{"first":1}""", "first");
        var there = extension == ".json" ? _files.Json(2) : _files.Transcript(2);
        File.WriteAllText(there, "kept");

        var rebuilt = TranscriptCopies.SaveRebuilt(_files, "rebuilt", NullLogger.Instance);

        Assert.That(rebuilt, Is.EqualTo(_files.Transcript(3)));
        Assert.That(File.ReadAllBytes(rebuilt), Is.EqualTo(Utf8("rebuilt")));
        Assert.That(File.Exists(_files.Json(3)), Is.False, "a rebuilt transcript has no answer of its own");
        Assert.That(File.ReadAllText(there), Is.EqualTo("kept"));
        Assert.That(File.ReadAllText(_files.Transcript(1)), Is.EqualTo("first"));
    }

    /// <summary>
    /// Transcripts saved and rebuilt by several writers at the same moment, as by two ClassScribe processes, never take
    /// one another's place: every one of them is where it was said to be saved, with what it was saved with.
    /// </summary>
    [Test]
    public async Task Transcripts_saved_and_rebuilt_at_the_same_moment_never_overwrite_one_another()
    {
        const int writers = 6;
        const int each = 10;
        using var start = new Barrier(writers);
        var saved = new ConcurrentBag<(TranscriptFiles Files, string Json, string Markdown)>();
        var rebuilt = new ConcurrentBag<(string Path, string Markdown)>();

        var running = Enumerable.Range(0, writers).Select(writer => Task.Factory.StartNew(() =>
        {
            start.SignalAndWait();
            for (var i = 0; i < each; i++)
            {
                var id = $"{writer}-{i}";
                if (writer % 3 == 2)
                {
                    rebuilt.Add((TranscriptCopies.SaveRebuilt(_files, $"rebuilt {id}", NullLogger.Instance), $"rebuilt {id}"));
                }
                else
                {
                    var json = $$"""{"id":"{{id}}"}""";
                    saved.Add((TranscriptCopies.Save(_files, json, () => $"transcript {id}", NullLogger.Instance), json, $"transcript {id}"));
                }
            }
        }, TaskCreationOptions.LongRunning)).ToArray();
        await Task.WhenAll(running).WaitAsync(TimeSpan.FromSeconds(60));

        Assert.That(saved.Count + rebuilt.Count, Is.EqualTo(writers * each));
        Assert.That(saved.SelectMany(s => new[] { s.Files.TranscriptPath, s.Files.JsonPath }).Concat(rebuilt.Select(r => r.Path)), Is.Unique);
        using (Assert.EnterMultipleScope())
        {
            foreach (var (files, json, markdown) in saved)
            {
                Assert.That(File.ReadAllText(files.JsonPath), Is.EqualTo(json));
                Assert.That(File.ReadAllText(files.TranscriptPath), Is.EqualTo(markdown));
            }

            foreach (var (path, markdown) in rebuilt)
                Assert.That(File.ReadAllText(path), Is.EqualTo(markdown));
        }
    }

    /// <summary>
    /// The newest answer is the one of the highest copy, counted as a number (copy 10 after copy 9), whatever copies are
    /// missing below it; a rebuilt transcript has no answer, and an unread answer is never one.
    /// </summary>
    [Test]
    public void The_newest_answer_is_the_one_of_the_highest_copy()
    {
        foreach (var path in new[] { _files.Json(1), _files.Json(9), _files.Json(10), _files.Transcript(11), _files.UnreadAnswer(1), _files.UnreadAnswer(12) })
            File.WriteAllText(path, "{}");

        Assert.That(TranscriptCopies.NewestAnswer(_files), Is.EqualTo(_files.Json(10)));
    }

    /// <summary>An answer kept as unread is never taken as the newest answer, even when it is the only one.</summary>
    [Test]
    public void An_unread_answer_is_never_the_newest_answer()
    {
        File.WriteAllText(_files.UnreadAnswer(1), "{}");

        Assert.That(TranscriptCopies.NewestAnswer(_files), Is.Null);
    }

    /// <summary>A recording without a saved answer has none, whether its folder is empty or gone.</summary>
    [Test]
    public void A_recording_without_a_saved_answer_has_none()
    {
        Assert.That(TranscriptCopies.NewestAnswer(_files), Is.Null);

        Directory.Delete(_files.Folder);

        Assert.That(TranscriptCopies.NewestAnswer(_files), Is.Null);
    }

    /// <summary>The answers of other recordings in the same folder, and other files whose names start the same way, are not the recording's.</summary>
    [Test]
    public void The_answers_of_other_recordings_in_the_folder_are_not_the_recordings()
    {
        var later = RecordingFiles.ForNew(_files.Folder, "Kalam", Started.AddDays(1));
        File.WriteAllText(_files.Json(1), "{}");
        File.WriteAllText(later.Json(3), "{}");
        File.WriteAllText(Path.Combine(_files.Folder, $"{_files.BaseName} notes.json"), "{}");

        Assert.That(TranscriptCopies.NewestAnswer(_files), Is.EqualTo(_files.Json(1)));
    }

    /// <summary>
    /// The transcripts of a recording are every copy of its transcript, in the order of their numbers (copy 10 after
    /// copy 9), whatever copies are missing between them; answers, another recording's transcripts and other files whose
    /// names start the same way are not among them.
    /// </summary>
    [Test]
    public void The_transcripts_of_a_recording_are_its_copies_in_the_order_of_their_numbers()
    {
        var later = RecordingFiles.ForNew(_files.Folder, "Kalam", Started.AddDays(1));
        foreach (var path in new[] { _files.Transcript(10), _files.Transcript(1), _files.Transcript(9), _files.Json(2), later.Transcript(1), Path.Combine(_files.Folder, $"{_files.BaseName} notes.md") })
            File.WriteAllText(path, "");

        Assert.That(TranscriptCopies.Transcripts(_files), Is.EqualTo(new[] { _files.Transcript(1), _files.Transcript(9), _files.Transcript(10) }));
    }

    /// <summary>A recording that was never transcribed has no transcripts, whether its folder is empty or gone.</summary>
    [Test]
    public void A_recording_that_was_never_transcribed_has_no_transcripts()
    {
        Assert.That(TranscriptCopies.Transcripts(_files), Is.Empty);

        Directory.Delete(_files.Folder);

        Assert.That(TranscriptCopies.Transcripts(_files), Is.Empty);
    }
}
