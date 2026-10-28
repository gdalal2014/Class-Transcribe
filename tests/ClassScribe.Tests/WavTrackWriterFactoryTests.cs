using ClassScribe.Audio;
using ClassScribe.Storage;

namespace ClassScribe.Tests;

/// <summary>
/// The contract of <see cref="ITrackWriterFactory"/> over its real adapter, in a temporary folder: the recording's folder
/// made, the next free part opened as a valid WAV that is never overwritten, the writer's audio counted as it reaches the
/// file, an empty segment deleted, and a segment's length on disk.
/// </summary>
[TestFixture]
public class WavTrackWriterFactoryTests
{
    private TempFolder _folder = null!;
    private RecordingFiles _files = null!;
    private ITrackWriterFactory _writers = null!;

    /// <summary>A recording's names in a temporary folder, and the adapter.</summary>
    [SetUp]
    public void SetUp()
    {
        _folder = new TempFolder();
        _files = new RecordingFiles(_folder.File("Kalam"), "Kalam_2026-10-15_20-55-27Z");
        _writers = new WavTrackWriterFactory();
    }

    /// <summary>Deletes the folder, which fails when a writer was left open.</summary>
    [TearDown]
    public void TearDown() => _folder.Dispose();

    /// <summary>The recording's folder is made when it is not there, and left as it is when it is.</summary>
    [Test]
    public void Makes_the_recordings_folder()
    {
        _writers.CreateFolder(_files.Folder);
        Assert.That(Directory.Exists(_files.Folder), Is.True);

        File.WriteAllText(_files.Marks, "00:00:01\n");
        _writers.CreateFolder(_files.Folder);
        Assert.That(File.Exists(_files.Marks), Is.True, "a folder that is there keeps its files");
    }

    /// <summary>The next free part from the number asked for is opened on its track, a part whose file is there being skipped, as a valid, empty WAV at the rate given.</summary>
    [Test]
    public void Opens_the_next_free_part_of_a_track_as_a_valid_wav()
    {
        _writers.CreateFolder(_files.Folder);
        File.WriteAllBytes(_files.Segment(2), new byte[10]);

        using (var first = _writers.Open(_files, Track.Main, 1, 48_000))
        using (var third = _writers.Open(_files, Track.Main, 2, 16_000))
        using (var microphone = _writers.Open(_files, Track.Microphone, 1, 48_000))
        {
            Assert.Multiple(() =>
            {
                Assert.That(first.Path, Is.EqualTo(_files.Segment(1)));
                Assert.That(third.Path, Is.EqualTo(_files.Segment(3)), "part 2 is there already, so it is skipped");
                Assert.That(microphone.Path, Is.EqualTo(_files.Segment(1, Track.Microphone)));
                Assert.That(first.SampleRate, Is.EqualTo(48_000));
                Assert.That(third.SampleRate, Is.EqualTo(16_000));
                Assert.That(first.DataBytes, Is.EqualTo(0));
            });
        }

        Assert.That(ClassFolders.MeasureSegment(_files.Segment(3)), Is.EqualTo(TimeSpan.Zero), "a valid WAV with no audio");
        Assert.That(new FileInfo(_files.Segment(1)).Length, Is.EqualTo(WavHeader.Size));
    }

    /// <summary>What is written reaches the file and is counted, and after the flush and the close the file is a WAV ClassScribe reads back at its length.</summary>
    [Test]
    public void Counts_what_reaches_the_file_and_closes_a_readable_wav()
    {
        _writers.CreateFolder(_files.Folder);
        using (var writer = _writers.Open(_files, Track.Main, 1, 10))
        {
            writer.Write(new byte[600]);
            writer.Flush();
            writer.Write(new byte[600]);
            Assert.That(writer.DataBytes, Is.EqualTo(1200));
        }

        Assert.Multiple(() =>
        {
            Assert.That(_writers.LengthOf(_files.Segment(1)), Is.EqualTo(WavHeader.Size + 1200));
            Assert.That(ClassFolders.MeasureSegment(_files.Segment(1)), Is.EqualTo(TimeSpan.FromSeconds(60)));
        });
    }

    /// <summary>An empty segment is deleted, and the length of a segment that is not there is an error, as the session's own catch expects.</summary>
    [Test]
    public void Deletes_a_segment_and_reports_a_missing_one_as_an_error()
    {
        _writers.CreateFolder(_files.Folder);
        using (_writers.Open(_files, Track.Main, 1, 16_000))
        {
        }

        _writers.Delete(_files.Segment(1));

        Assert.Multiple(() =>
        {
            Assert.That(File.Exists(_files.Segment(1)), Is.False);
            Assert.Throws<FileNotFoundException>(() => _writers.LengthOf(_files.Segment(1)));
            Assert.DoesNotThrow(() => _writers.Delete(_files.Segment(1)), "a file that is gone already is no error");
        });
    }
}
