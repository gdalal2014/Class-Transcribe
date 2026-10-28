using ClassScribe.Audio.Microphones;
using ClassScribe.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClassScribe.Tests;

/// <summary>
/// The contracts of <see cref="IClassMarks"/>, <see cref="ITranscriptCopies"/> and <see cref="IMicCheckLog"/> over their
/// real adapters, in a temporary folder, for what each port adds to or takes over from its adapter: the marks' parts, the
/// transcript's line under its title and the key of a saved answer, and the check's log opened through the port.
/// </summary>
[TestFixture]
public class StoragePortTests
{
    private TempFolder _folder = null!;
    private RecordingFiles _files = null!;

    /// <summary>A recording's names in a temporary folder.</summary>
    [SetUp]
    public void SetUp()
    {
        _folder = new TempFolder();
        _files = new RecordingFiles(_folder.Path, "Kalam_2026-10-15_20-55-27Z");
    }

    /// <summary>Deletes the folder.</summary>
    [TearDown]
    public void TearDown() => _folder.Dispose();

    /// <summary>The lines of a log file that the check still holds open for writing, read as the app's readers read it, with others allowed to write.</summary>
    private static string[] LinesOf(string path)
    {
        using var reader = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
        return reader.ReadToEnd().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
    }

    /// <summary>The port counts a recording's parts from the resume notes its own adapter wrote: one, and one more for each resume.</summary>
    [Test]
    public void The_marks_port_counts_the_parts_its_adapter_wrote()
    {
        IClassMarks marks = new ClassMarks(NullLogger<ClassMarks>.Instance);

        Assert.That(marks.PartsOf(_files), Is.EqualTo(1), "no marks file: one part");

        marks.Add(_files, TimeSpan.FromSeconds(30));
        marks.AddResume(_files, TimeSpan.FromMinutes(10), new DateTimeOffset(2026, 10, 15, 21, 10, 0, TimeSpan.Zero), TimeSpan.FromMinutes(4));
        marks.AddNote(_files, TimeSpan.FromMinutes(11), "a note of its own");

        Assert.Multiple(() =>
        {
            Assert.That(marks.PartsOf(_files), Is.EqualTo(2).And.EqualTo(ClassMarks.PartsOf(_files)));
            Assert.That(marks.Read(_files).Count, Is.EqualTo(3));
            Assert.That(ClassMark.PartsIn(marks.Read(_files)), Is.EqualTo(2));
        });
    }

    /// <summary>The line under a transcript's title is the first non-blank line after it, read while the file is open for writing elsewhere; a transcript without one gives null.</summary>
    [Test]
    public void The_copies_port_reads_the_line_under_a_transcripts_title()
    {
        ITranscriptCopies copies = new TranscriptCopies();
        File.WriteAllText(_files.Transcript(1), "# Kalam\n\n\nRecorded 15 October 2026, 20:55 UTC.\n\nThe text.\n");
        File.WriteAllText(_files.Transcript(2), "# Kalam\n");
        File.WriteAllText(_files.Transcript(3), "");

        Assert.Multiple(() =>
        {
            Assert.That(copies.LineUnderTitle(_files.Transcript(1)), Is.EqualTo("Recorded 15 October 2026, 20:55 UTC."));
            Assert.That(copies.LineUnderTitle(_files.Transcript(2)), Is.Null);
            Assert.That(copies.LineUnderTitle(_files.Transcript(3)), Is.Null);
            using (new FileStream(_files.Transcript(1), FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
                Assert.That(copies.LineUnderTitle(_files.Transcript(1)), Is.EqualTo("Recorded 15 October 2026, 20:55 UTC."), "readable while another ClassScribe writes it");
            Assert.That(ITranscriptCopies.SavedAnswerKey, Is.EqualTo("ClassScribe.SavedAnswer"), "the key whoever reports a failed save reads");
        });
    }

    /// <summary>The check's log opens through the port in the folder given, which is made, and the file takes the lines as they are written and reports its count.</summary>
    [Test]
    public void The_mic_check_port_opens_a_log_and_writes_it_at_once()
    {
        IMicCheckLog logs = new MicCheckLog();
        var started = new DateTimeOffset(2026, 10, 17, 19, 0, 12, TimeSpan.FromHours(-4));
        var folder = _folder.File(FolderNames.MicCheck);

        using var log = logs.Create(folder, started);
        var text = log.Write(new MicCheckLine(started.AddSeconds(1), "MIC", "the array appeared"));
        log.Plain("a plain line");

        Assert.Multiple(() =>
        {
            Assert.That(log.Path, Is.EqualTo(Path.Combine(folder, "mic-check_2026-10-17_19-00-12.log")));
            Assert.That(log.TimedLines, Is.EqualTo(1));
            Assert.That(text, Is.EqualTo(MicCheckLog.Format(new MicCheckLine(started.AddSeconds(1), "MIC", "the array appeared"))));
            Assert.That(LinesOf(log.Path), Is.EqualTo(new[] { text, "a plain line" }), "each line reaches the file as it is written");
            Assert.That(logs.HeaderLines(started, 1234, log.Path, TimeSpan.FromMilliseconds(250), null), Has.Count.EqualTo(8).And.Some.Contains("ClassScribe's own process id 1234"));
            Assert.That(logs.CostLine(), Does.StartWith("The check's own cost: "));
        });
    }
}
