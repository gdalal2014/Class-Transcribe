using ClassScribe.Storage;

namespace ClassScribe.Tests;

/// <summary>
/// What the class folders give Google Drive's side: a path with each folder named as it is on disk, whatever was typed;
/// the recordings of a folder known by any of their files, so that one whose audio is on Drive alone, or a file transcribed
/// on its own, counts, while PDFs and other folders such as Slides are passed over.
/// </summary>
[TestFixture]
public class ClassFoldersDriveTests
{
    private TempFolder _folder = null!;

    /// <summary>A temporary recordings folder.</summary>
    [SetUp]
    public void SetUp() => _folder = new TempFolder();

    /// <summary>Deletes it.</summary>
    [TearDown]
    public void TearDown() => _folder.Dispose();

    /// <summary>A folder typed in another letter case comes back named as on disk; the part that is not there keeps what was typed.</summary>
    [Test]
    public void A_path_comes_back_named_as_on_disk()
    {
        var course = Directory.CreateDirectory(Path.Combine(_folder.Path, "Mantiq", "2026-10-17 (2)")).FullName;

        Assert.That(ClassFolders.AsOnDisk(course.ToLowerInvariant()).ToLowerInvariant(), Is.EqualTo(course.ToLowerInvariant()));
        Assert.That(ClassFolders.DriveLevelsOf(ClassFolders.AsOnDisk(course.ToLowerInvariant())), Is.EqualTo(new[] { "Mantiq", "2026-10-17 (2)" }));
        Assert.That(Path.GetFileName(ClassFolders.AsOnDisk(Path.Combine(course.ToUpperInvariant(), "not there yet"))), Is.EqualTo("not there yet"));
        Assert.That(ClassFolders.AsOnDisk(course + Path.DirectorySeparatorChar), Is.EqualTo(course));
    }

    /// <summary>
    /// A course's recordings are known by any of their files, flat in it and in its date folders, newest first: one with only
    /// its transcript and answer left counts; the PDFs beside them and the Slides folder do not.
    /// </summary>
    [Test]
    public void A_folders_recordings_are_known_by_any_of_their_files()
    {
        var course = Path.Combine(_folder.Path, "Mantiq");
        var dated = Directory.CreateDirectory(Path.Combine(course, "2026-10-17")).FullName;
        Directory.CreateDirectory(Path.Combine(course, "Slides"));
        File.WriteAllText(Path.Combine(course, "Slides", "Mantiq_2026-10-01_10-00-00Z.md"), "not a class: in Slides");
        File.WriteAllText(Path.Combine(dated, "Mantiq_2026-10-17_14-04-42Z.md"), "#");
        File.WriteAllText(Path.Combine(dated, "Mantiq_2026-10-17_14-04-42Z.json"), "{}");
        File.WriteAllText(Path.Combine(dated, "Class_1_slides.pdf"), "%PDF");
        TestAudio.WriteWav(Path.Combine(course, "Mantiq_2026-10-10_14-00-00Z.wav"), 10, [1, 2, 3]);

        var found = ClassFolders.RecordingsByAnyFileIn(course);

        Assert.That(found, Is.EqualTo(new[]
        {
            new RecordingFiles(dated, "Mantiq_2026-10-17_14-04-42Z"),
            new RecordingFiles(course, "Mantiq_2026-10-10_14-00-00Z")
        }));
    }

    /// <summary>Without what is known of Drive, a recording with no audio here is no recording in the lists, as before.</summary>
    [Test]
    public void Without_Drive_a_recording_without_audio_is_not_listed()
    {
        var dated = Directory.CreateDirectory(Path.Combine(_folder.Path, "Mantiq", "2026-10-17")).FullName;
        File.WriteAllText(Path.Combine(dated, "Mantiq_2026-10-17_14-04-42Z.md"), "#");
        var folders = new ClassFolders(TestOptions.Create(new Settings.RecordingOptions { OutputFolder = _folder.Path }), TimeProvider.System,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ClassFolders>.Instance);

        Assert.That(folders.ListAll(), Is.Empty);
        Assert.That(folders.NewestWithAudioOnDriveIn(dated), Is.Null);
    }
}
