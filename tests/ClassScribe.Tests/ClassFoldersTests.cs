using ClassScribe.Settings;
using ClassScribe.Storage;
using Microsoft.Extensions.Options;

namespace ClassScribe.Tests;

/// <summary>Where recordings and transcripts go, and how the menu finds recent recordings.</summary>
[TestFixture]
public class ClassFoldersTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 18, 30, 0, TimeSpan.Zero);

    private TempFolder _folder = null!;
    private ManualTimeProvider _time = null!;

    /// <summary>Creates a temporary folder to hold the recording folders, and a clock moved by hand.</summary>
    [SetUp]
    public void SetUp()
    {
        _folder = new TempFolder();
        _time = new ManualTimeProvider(Now);
    }

    /// <summary>Deletes the temporary folder.</summary>
    [TearDown]
    public void TearDown() => _folder.Dispose();

    /// <summary>Creates the folders under test, rooted in the temporary folder unless another root is given.</summary>
    private ClassFolders Folders(string? outputFolder = null) =>
        new(Options.Create(new RecordingOptions { OutputFolder = outputFolder ?? _folder.Path }), _time);

    /// <summary>Writes a recording of the given length into its folder, last written at the given time.</summary>
    private RecordingFiles WriteRecording(string name, DateTimeOffset started, double seconds = 0.1, bool withTranscript = false)
    {
        var files = RecordingFiles.ForNew(_folder.File(name), name, started);
        Directory.CreateDirectory(files.Folder);
        TestAudio.WriteWav(files.Segment(1), 16000, new short[(int)(16000 * seconds)]);
        File.SetLastWriteTimeUtc(files.Segment(1), started.UtcDateTime);
        if (withTranscript) File.WriteAllText(files.Transcript(), "# Kalam");
        return files;
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

    /// <summary>A recording's folder is its name; its files carry the name and the UTC start time. The folder is shared by later recordings of the same name.</summary>
    [Test]
    public void A_recording_gets_its_name_as_folder_and_its_start_time_in_its_files()
    {
        var folders = Folders();

        var first = folders.CreateRecording("Kalam");
        TestAudio.WriteWav(first.Segment(1), 16000, [1]);
        var second = folders.CreateRecording("Kalam");

        Assert.That(first.Folder, Is.EqualTo(_folder.File("Kalam")));
        Assert.That(Directory.Exists(first.Folder), Is.True);
        Assert.That(first.BaseName, Is.EqualTo("Kalam_2026-10-14_18-30-00Z"));
        Assert.That(second.Folder, Is.EqualTo(first.Folder), "recordings of one class share its folder");
        Assert.That(second.BaseName, Is.EqualTo("Kalam_2026-10-14_18-30-01Z"), "a recording started within the same second gets the next one, so nothing is overwritten");
    }

    /// <summary>The start time is UTC whatever the machine's time zone and calendar.</summary>
    [Test, SetCulture("ar-SA")]
    public void The_start_time_is_utc_under_any_regional_settings()
    {
        var files = Folders().CreateRecording("Kalam");

        Assert.That(files.BaseName, Is.EqualTo("Kalam_2026-10-14_18-30-00Z"));
        Assert.That(files.StartedUtc, Is.EqualTo(Now));
    }

    /// <summary>A name is cleaned for use as a folder and file name; one with nothing left is empty.</summary>
    [Test]
    public void A_recording_name_is_made_safe_for_a_file_name()
    {
        Assert.That(ClassFolders.SafeName("  Kalam: lesson  <12>?  "), Is.EqualTo("Kalam lesson 12"));
        Assert.That(ClassFolders.SafeName("Uṣūl al-dīn, part 1"), Is.EqualTo("Uṣūl al-dīn, part 1"), "letters outside ASCII and commas are fine");
        Assert.That(ClassFolders.SafeName(" ... "), Is.Empty, "a name of only dots would not be a valid folder");
        Assert.That(ClassFolders.SafeName(""), Is.Empty);
    }

    /// <summary>A transcribed file gets a folder named after the file, and files stamped with the transcription time.</summary>
    [Test]
    public void A_file_gets_a_folder_named_after_it()
    {
        var files = Folders().CreateForFile(@"D:\Downloads\Lecture 5.mp4");

        Assert.That(files.Folder, Is.EqualTo(_folder.File("Lecture 5")));
        Assert.That(files.BaseName, Is.EqualTo("Lecture 5_2026-10-14_18-30-00Z"));
        Assert.That(Directory.Exists(files.Folder), Is.True);
    }

    /// <summary>The menu's list: every recording under the root, newest first, with its length and whether a transcript exists.</summary>
    [Test]
    public void Recent_recordings_are_listed_newest_first()
    {
        var older = WriteRecording("Kalam", new DateTimeOffset(2026, 10, 11, 14, 5, 0, TimeSpan.Zero), seconds: 2.0, withTranscript: true);
        var newer = WriteRecording("Kalam", new DateTimeOffset(2026, 10, 14, 19, 30, 0, TimeSpan.Zero), seconds: 1.0);
        TestAudio.WriteWav(newer.Segment(2), 16000, new short[8000]);
        File.SetLastWriteTimeUtc(newer.Segment(2), new DateTime(2026, 10, 14, 21, 0, 0, DateTimeKind.Utc));
        var other = WriteRecording("Fiqh", new DateTimeOffset(2026, 10, 12, 10, 0, 0, TimeSpan.Zero));
        Directory.CreateDirectory(_folder.File("Lecture 5"));   // a transcribed file: nothing recorded in it

        var recordings = Folders().ListRecent(9);

        Assert.That(recordings.Select(r => r.Files), Is.EqualTo(new[] { newer, other, older }));
        Assert.That(recordings[0].Length.TotalSeconds, Is.EqualTo(1.5).Within(0.001));
        Assert.That(recordings[0].HasTranscript, Is.False);
        Assert.That(recordings[2].Length.TotalSeconds, Is.EqualTo(2.0).Within(0.001));
        Assert.That(recordings[2].HasTranscript, Is.True);
    }

    /// <summary>The list stops at the number asked for, keeping the newest.</summary>
    [Test]
    public void At_most_the_requested_number_of_recordings_is_listed()
    {
        for (var day = 1; day <= 3; day++)
            WriteRecording("Kalam", new DateTimeOffset(2026, 10, day, 10, 0, 0, TimeSpan.Zero));

        var recordings = Folders().ListRecent(2);

        Assert.That(recordings.Select(r => r.Files.BaseName), Is.EqualTo(new[] { "Kalam_2026-10-03_10-00-00Z", "Kalam_2026-10-02_10-00-00Z" }));
    }

    /// <summary>A recordings folder that does not exist yet simply has no recordings.</summary>
    [Test]
    public void A_missing_recordings_folder_has_no_recordings() =>
        Assert.That(Folders(outputFolder: _folder.File("not-there")).ListRecent(9), Is.Empty);

    /// <summary>Given a folder, the recording started last in it is the one meant.</summary>
    [Test]
    public void The_newest_recording_in_a_folder_is_found()
    {
        WriteRecording("Kalam", new DateTimeOffset(2026, 10, 11, 14, 5, 0, TimeSpan.Zero));
        var newest = WriteRecording("Kalam", new DateTimeOffset(2026, 10, 14, 19, 30, 0, TimeSpan.Zero));
        File.WriteAllBytes(_folder.File("Kalam/notes.wav"), [0]);

        Assert.That(ClassFolders.FindNewest(_folder.File("Kalam")), Is.EqualTo(newest));
        Assert.That(ClassFolders.FindNewest(_folder.Path), Is.Null, "the root holds folders, not recordings");
    }
}
