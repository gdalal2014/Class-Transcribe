using ClassScribe.Audio;
using ClassScribe.Settings;
using ClassScribe.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClassScribe.Tests;

/// <summary>
/// The contract of <see cref="IClassFolders"/> over its real adapter, in a temporary folder: a track's length on disk, the
/// strict listings of a folder's files and folders, and that the port's members over the class folders' statics answer as
/// the statics do.
/// </summary>
[TestFixture]
public class ClassFoldersPortTests
{
    private TempFolder _root = null!;
    private ClassFolders _folders = null!;

    /// <summary>A recordings folder of the test's own, and the class folders over it.</summary>
    [SetUp]
    public void SetUp()
    {
        _root = new TempFolder();
        _folders = new ClassFolders(TestOptions.Create(new RecordingOptions { OutputFolder = _root.Path }), TimeProvider.System, NullLogger<ClassFolders>.Instance);
    }

    /// <summary>Deletes the folder.</summary>
    [TearDown]
    public void TearDown() => _root.Dispose();

    /// <summary>The port the adapter implements.</summary>
    private IClassFolders Port => _folders;

    /// <summary>Writes <paramref name="seconds"/> of audio at ten samples a second with the recorder's own writer, so a long track stays small.</summary>
    private static void WriteAudio(string path, double seconds)
    {
        using var writer = new WavSegmentWriter(path, 10);
        writer.Write(new byte[(int)Math.Round(seconds * 10) * 2]);
    }

    /// <summary>A track's length is the sum of its own segments read to their ends, in recording order; the other track's files do not count, and a track with none is zero.</summary>
    [Test]
    public void Measures_one_track_by_its_segments()
    {
        var folder = Directory.CreateDirectory(_root.File(Path.Combine("Kalam", "2026-10-15"))).FullName;
        var files = new RecordingFiles(folder, "Kalam_2026-10-15_20-55-27Z");
        WriteAudio(files.Segment(1), 90);
        WriteAudio(files.Segment(2), 30);
        WriteAudio(files.Segment(1, Track.Microphone), 45);

        Assert.Multiple(() =>
        {
            Assert.That(Port.TrackLength(files, Track.Main), Is.EqualTo(TimeSpan.FromSeconds(120)));
            Assert.That(Port.TrackLength(files, Track.Microphone), Is.EqualTo(TimeSpan.FromSeconds(45)));
            Assert.That(Port.TrackLength(new RecordingFiles(folder, "Kalam_2026-10-15_21-55-27Z"), Track.Main), Is.EqualTo(TimeSpan.Zero));
            Assert.That(Port.LengthOnDisk(files), Is.EqualTo(TimeSpan.FromSeconds(120)), "the recording's length is its longest track's");
        });
    }

    /// <summary>The files and the folders of a folder are listed as they are, itself alone, and a folder that is not there is an error rather than empty.</summary>
    [Test]
    public void Lists_the_files_and_the_folders_of_a_folder_strictly()
    {
        var course = Directory.CreateDirectory(_root.File("Kalam")).FullName;
        Directory.CreateDirectory(Path.Combine(course, "2026-10-15"));
        Directory.CreateDirectory(Path.Combine(course, "Slides"));
        File.WriteAllText(Path.Combine(course, "notes.md"), "notes");
        File.WriteAllText(Path.Combine(course, "2026-10-15", "Kalam_2026-10-15_20-55-27Z.log"), "a line");

        Assert.Multiple(() =>
        {
            Assert.That(Port.FilesIn(course), Is.EqualTo(new[] { Path.Combine(course, "notes.md") }));
            Assert.That(Port.FoldersIn(course), Is.EquivalentTo(new[] { Path.Combine(course, "2026-10-15"), Path.Combine(course, "Slides") }));
            Assert.That(Port.FilesIn(Path.Combine(course, "Slides")), Is.Empty);
            Assert.Throws<DirectoryNotFoundException>(() => Port.FilesIn(_root.File("missing")));
            Assert.Throws<DirectoryNotFoundException>(() => Port.FoldersIn(_root.File("missing")));
        });
    }

    /// <summary>Each of the port's members over a static of the class folders gives the static's own answer, on the same files.</summary>
    [Test]
    public void The_members_over_the_statics_answer_as_the_statics_do()
    {
        var course = Directory.CreateDirectory(_root.File("Mantiq")).FullName;
        var dated = Directory.CreateDirectory(Path.Combine(course, "2026-10-17")).FullName;
        var files = new RecordingFiles(dated, "Mantiq_2026-10-17_14-04-42Z");
        WriteAudio(files.Segment(1), 60);
        File.WriteAllText(files.Marks, "00:00:30\n");
        var video = new RecordingFiles(dated, "Mantiq_2026-10-17_16-04-42Z");
        WriteAudio(video.Segment(1), 10);
        File.WriteAllText(video.Source, "{}");

        Assert.Multiple(() =>
        {
            Assert.That(Port.RootFor("%USERPROFILE%\\Classes"), Is.EqualTo(ClassFolders.RootFor("%USERPROFILE%\\Classes")));
            Assert.That(Port.RootFor(null), Is.EqualTo(ClassFolders.RootFor(null)));
            Assert.That(Port.IsVideo(video), Is.True.And.EqualTo(ClassFolders.IsVideo(video)));
            Assert.That(Port.IsVideo(files), Is.False.And.EqualTo(ClassFolders.IsVideo(files)));
            Assert.That(Port.FindNewest(course), Is.EqualTo(video).And.EqualTo(ClassFolders.FindNewest(course)));
            Assert.That(Port.RecordingFoldersIn(course), Is.EqualTo(ClassFolders.RecordingFoldersIn(course)).And.EqualTo(new[] { course, dated }));
            Assert.That(Port.DriveLevelsOf(dated), Is.EqualTo(ClassFolders.DriveLevelsOf(dated)).And.EqualTo(new[] { "Mantiq", "2026-10-17" }));
            Assert.That(Port.AsOnDisk(dated.ToLowerInvariant()), Is.EqualTo(ClassFolders.AsOnDisk(dated.ToLowerInvariant())));
            Assert.That(Port.RecordingsByAnyFileIn(course), Is.EqualTo(ClassFolders.RecordingsByAnyFileIn(course)).And.EqualTo(new[] { video, files }));
            Assert.That(Port.IsBeingWritten(files), Is.False.And.EqualTo(ClassFolders.IsBeingWritten(files)));
            Assert.That(Port.IsLessonVideoBeingMade(files), Is.False.And.EqualTo(ClassFolders.IsLessonVideoBeingMade(files)));
            Assert.That(Port.LessonVideoStateOf(files), Is.EqualTo(LessonVideoState.None).And.EqualTo(ClassFolders.LessonVideoStateOf(files)));
            Assert.That(Port.MeasureSegment(files.Segment(1)), Is.EqualTo(TimeSpan.FromSeconds(60)).And.EqualTo(ClassFolders.MeasureSegment(files.Segment(1))));
        });
    }

    /// <summary>A segment held open for writing by a recorder, which lets others read it, shows the recording as being written through the port as through the static.</summary>
    [Test]
    public void A_segment_held_for_writing_is_seen_through_the_port()
    {
        var folder = Directory.CreateDirectory(_root.File("Kalam")).FullName;
        var files = new RecordingFiles(folder, "Kalam_2026-10-15_20-55-27Z");
        using (var writer = new WavSegmentWriter(files.Segment(1), 10))
        {
            writer.Write(new byte[20]);
            Assert.That(Port.IsBeingWritten(files), Is.True.And.EqualTo(ClassFolders.IsBeingWritten(files)));
        }

        Assert.That(Port.IsBeingWritten(files), Is.False);
    }
}
