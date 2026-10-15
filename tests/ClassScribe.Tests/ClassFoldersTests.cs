using System.Globalization;
using ClassScribe.Settings;
using ClassScribe.Storage;
using Microsoft.Extensions.Options;

namespace ClassScribe.Tests;

/// <summary>Where recordings and transcripts go, and how the menu finds recorded classes.</summary>
[TestFixture]
public class ClassFoldersTests
{
    private TempFolder _folder = null!;

    /// <summary>Creates a temporary folder to hold the class folders.</summary>
    [SetUp]
    public void SetUp() => _folder = new TempFolder();

    /// <summary>Deletes the temporary folder.</summary>
    [TearDown]
    public void TearDown() => _folder.Dispose();

    /// <summary>Creates the folders under test, rooted in the temporary folder unless another root is given.</summary>
    private ClassFolders Folders(string? outputFolder = null, TimeProvider? time = null) =>
        new(Options.Create(new RecordingOptions { OutputFolder = outputFolder ?? _folder.Path }), time ?? TimeProvider.System);

    /// <summary>Writes a recording segment of the given length, last written at the given time.</summary>
    private static void WriteSegment(string path, double seconds, DateTime writtenUtc)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        TestAudio.WriteWav(path, 16000, new short[(int)(16000 * seconds)]);
        File.SetLastWriteTimeUtc(path, writtenUtc);
    }

    /// <summary>An empty setting keeps the old default, a ClassScribe folder in the user profile.</summary>
    [Test]
    public void An_empty_setting_means_ClassScribe_in_the_user_profile()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        Assert.That(Folders(outputFolder: "").Root, Is.EqualTo(Path.Combine(profile, "ClassScribe")));
    }

    /// <summary>The setting may use environment variables such as %USERPROFILE%.</summary>
    [Test]
    public void Environment_variables_in_the_setting_are_expanded()
    {
        Environment.SetEnvironmentVariable("CLASSSCRIBE_TEST_ROOT", _folder.Path);
        try
        {
            Assert.That(Folders(outputFolder: @"%CLASSSCRIBE_TEST_ROOT%\Classes").Root, Is.EqualTo(Path.Combine(_folder.Path, "Classes")));
        }
        finally
        {
            Environment.SetEnvironmentVariable("CLASSSCRIBE_TEST_ROOT", null);
        }
    }

    /// <summary>A class folder is named after the local date and time, and an existing one is never reused.</summary>
    [Test, SetCulture("ar-SA")]   // a region whose own calendar would give a Hijri year
    public void A_class_folder_is_named_after_the_time_and_never_reused()
    {
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 10, 14, 18, 30, 0, TimeSpan.Zero));
        var folders = Folders(time: time);
        var name = time.GetLocalNow().ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture);

        var first = folders.CreateForClass();
        var second = folders.CreateForClass();

        Assert.That(first, Is.EqualTo(Path.Combine(_folder.Path, name)));
        Assert.That(second, Is.EqualTo(Path.Combine(_folder.Path, name + "_2")));
        Assert.That(Directory.Exists(first) && Directory.Exists(second), Is.True);
    }

    /// <summary>A file's transcript gets a folder named after the file, and an existing one is never reused.</summary>
    [Test]
    public void A_file_folder_is_named_after_the_file_and_never_reused()
    {
        var folders = Folders();

        var first = folders.CreateForFile(@"D:\Downloads\Lecture 5.mp4");
        var second = folders.CreateForFile(@"E:\Other\Lecture 5.mp3");

        Assert.That(first, Is.EqualTo(_folder.File("Lecture 5")));
        Assert.That(second, Is.EqualTo(_folder.File("Lecture 5_2")));
    }

    /// <summary>Only the recorder's own segment files count, in the order they were recorded.</summary>
    [Test]
    public void Segments_are_found_in_recording_order()
    {
        foreach (var name in new[] { "recording-010.wav", "recording-002.wav", "recording-001.wav", "recording-001.upload.wav", "upload.wav", "notes.txt" })
            File.WriteAllBytes(_folder.File(name), [0]);

        var segments = ClassFolders.FindSegments(_folder.Path);

        Assert.That(segments, Is.EqualTo(new[] { _folder.File("recording-001.wav"), _folder.File("recording-002.wav"), _folder.File("recording-010.wav") }));
    }

    /// <summary>The menu's list: folders with a recording, newest first, with their length and whether a transcript exists.</summary>
    [Test]
    public void Recent_classes_are_listed_newest_first()
    {
        var older = _folder.File("2026-10-11_14-05-00");
        var newer = _folder.File("2026-10-14_19-30-00");
        WriteSegment(Path.Combine(older, "recording-001.wav"), 2.0, new DateTime(2026, 10, 11, 16, 0, 0, DateTimeKind.Utc));
        File.WriteAllText(Path.Combine(older, "transcript.txt"), "[00:00:00] Hello.");
        WriteSegment(Path.Combine(newer, "recording-001.wav"), 1.0, new DateTime(2026, 10, 14, 20, 0, 0, DateTimeKind.Utc));
        WriteSegment(Path.Combine(newer, "recording-002.wav"), 0.5, new DateTime(2026, 10, 14, 21, 0, 0, DateTimeKind.Utc));
        Directory.CreateDirectory(_folder.File("Lecture 5"));   // a transcribed file: nothing recorded in it

        var classes = Folders().ListRecent(9);

        Assert.That(classes.Select(c => c.Name), Is.EqualTo(new[] { "2026-10-14_19-30-00", "2026-10-11_14-05-00" }));
        Assert.That(classes[0].Length.TotalSeconds, Is.EqualTo(1.5).Within(0.001));
        Assert.That(classes[0].HasTranscript, Is.False);
        Assert.That(classes[1].Length.TotalSeconds, Is.EqualTo(2.0).Within(0.001));
        Assert.That(classes[1].HasTranscript, Is.True);
    }

    /// <summary>The list stops at the number asked for, keeping the newest.</summary>
    [Test]
    public void At_most_the_requested_number_of_classes_is_listed()
    {
        for (var day = 1; day <= 3; day++)
            WriteSegment(_folder.File($"2026-10-0{day}_10-00-00/recording-001.wav"), 0.1, new DateTime(2026, 10, day, 12, 0, 0, DateTimeKind.Utc));

        var classes = Folders().ListRecent(2);

        Assert.That(classes.Select(c => c.Name), Is.EqualTo(new[] { "2026-10-03_10-00-00", "2026-10-02_10-00-00" }));
    }

    /// <summary>A recordings folder that does not exist yet simply has no classes.</summary>
    [Test]
    public void A_missing_recordings_folder_has_no_classes() =>
        Assert.That(Folders(outputFolder: _folder.File("not-there")).ListRecent(9), Is.Empty);
}
