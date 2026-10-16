using System.Diagnostics;
using System.Runtime.Versioning;
using ClassScribe.Settings;
using ClassScribe.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClassScribe.Tests;

/// <summary>Where recordings and transcripts go, and how the menu finds recent recordings.</summary>
[TestFixture]
public class ClassFoldersTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 18, 30, 0, TimeSpan.Zero);

    private TempFolder _folder = null!;
    private ManualTimeProvider _time = null!;
    private RecordingLogger<ClassFolders> _log = null!;

    /// <summary>Creates a temporary folder to hold the recording folders, a clock moved by hand and a log to inspect.</summary>
    [SetUp]
    public void SetUp()
    {
        _folder = new TempFolder();
        _time = new ManualTimeProvider(Now);
        _log = new RecordingLogger<ClassFolders>();
    }

    /// <summary>Deletes the temporary folder.</summary>
    [TearDown]
    public void TearDown() => _folder.Dispose();

    /// <summary>Creates the folders under test, rooted in the temporary folder unless another root is given.</summary>
    private ClassFolders Folders(string? outputFolder = null) =>
        new(Options.Create(new RecordingOptions { OutputFolder = outputFolder ?? _folder.Path }), _time, _log);

    /// <summary>Holds a recording's first file open without sharing, as another program might.</summary>
    private static FileStream HoldOpen(RecordingFiles files) =>
        new(files.Segment(1), FileMode.Open, FileAccess.ReadWrite, FileShare.None);

    /// <summary>
    /// Makes <paramref name="link"/> a junction to <paramref name="target"/>, as mklink /J does. Unlike a
    /// symbolic link, a junction needs no administrator rights.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static void MakeJunction(string link, string target)
    {
        var start = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
        foreach (var argument in new[] { "/c", "mklink", "/J", link, target }) start.ArgumentList.Add(argument);

        using var mklink = Process.Start(start)!;
        mklink.StandardOutput.ReadToEnd();
        mklink.WaitForExit();
    }

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

    /// <summary>Saves a transcript and ElevenLabs' answer where the files say, as the workflow does after the upload, and checks they arrived.</summary>
    private static void WriteTranscript(RecordingFiles files)
    {
        File.WriteAllText(files.Json(), "{}");
        File.WriteAllText(files.Transcript(), "# Lecture");

        Assert.That(File.Exists(files.Transcript()) && File.Exists(files.Json()), Is.True);
    }

    /// <summary>A drive letter with no drive behind it on this machine, such as "Q:", or null when every letter is in use.</summary>
    private static string? MissingDrive() =>
        Enumerable.Range('D', 'Z' - 'D' + 1)
            .Select(letter => $"{(char)letter}:")
            .Reverse()
            .FirstOrDefault(drive => !Directory.Exists(drive + @"\"));

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

    /// <summary>A relative setting is taken from the user profile, so it means the same folder whatever folder ClassScribe was started from.</summary>
    [Test]
    public void A_relative_setting_is_resolved_in_the_user_profile_whatever_the_current_folder()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var startedIn = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = _folder.Path;

            Assert.That(Folders(outputFolder: "Classes").Root, Is.EqualTo(Path.Combine(profile, "Classes")));
        }
        finally
        {
            Environment.CurrentDirectory = startedIn;
        }
    }

    /// <summary>Quotes around the setting, as Explorer's "Copy as path" adds them, are not part of the folder's name.</summary>
    [Test]
    public void Quotes_around_the_setting_are_ignored() =>
        Assert.That(Folders(outputFolder: $" \"{_folder.File("Classes")}\" ").Root, Is.EqualTo(_folder.File("Classes")));

    /// <summary>A usable root has nothing to report, even before its folder has been made.</summary>
    [Test]
    public void A_usable_root_has_no_problem()
    {
        Assert.That(Folders().DescribeRootProblem(), Is.Null);
        Assert.That(Folders(outputFolder: _folder.File(@"not-yet\Classes")).DescribeRootProblem(), Is.Null);
    }

    /// <summary>An environment variable in the setting that is not set is reported, instead of quietly becoming part of a folder's name.</summary>
    [Test]
    public void An_unset_environment_variable_in_the_setting_is_reported()
    {
        var folders = Folders(outputFolder: @"%CLASSSCRIBE_UNSET_TEST_VARIABLE%\Classes");

        Assert.That(folders.DescribeRootProblem(), Does.Contain("%CLASSSCRIBE_UNSET_TEST_VARIABLE%").And.Contain("not set"));
    }

    /// <summary>A root on a drive that is not there, such as an unplugged USB drive, is reported, so it is not mistaken for a root without recordings.</summary>
    [Test]
    public void A_root_on_a_missing_drive_is_reported()
    {
        var drive = MissingDrive();
        Assume.That(drive, Is.Not.Null, "every drive letter is in use on this machine");

        var problem = Folders(outputFolder: $@"{drive}\ClassScribe").DescribeRootProblem();

        Assert.That(problem, Does.Contain(drive).And.Contain("not available"));
        Assert.That(Folders(outputFolder: $@"{drive}\ClassScribe").ListRecent(9), Is.Empty, "the list itself stays empty, so the menu still opens");
    }

    /// <summary>A root that is a file is reported, because no recording can be made there.</summary>
    [Test]
    public void A_root_that_is_a_file_is_reported()
    {
        File.WriteAllText(_folder.File("Classes"), "");

        Assert.That(Folders(outputFolder: _folder.File("Classes")).DescribeRootProblem(), Does.Contain("is a file"));
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

    /// <summary>
    /// A name Windows keeps for a device, such as NUL or COM1, alone or before an extension, gets an
    /// underscore after it, because no folder of that name can be made. Longer names are left alone.
    /// </summary>
    [TestCase("nul", "nul_")]
    [TestCase("NUL.", "NUL_")]
    [TestCase("con", "con_")]
    [TestCase("Aux", "Aux_")]
    [TestCase("prn", "prn_")]
    [TestCase("Com1", "Com1_")]
    [TestCase("LPT9", "LPT9_")]
    [TestCase("COM¹", "COM¹_")]
    [TestCase("aux.backup", "aux_.backup")]
    [TestCase("nul .backup", "nul_ .backup")]
    [TestCase("Console", "Console")]
    [TestCase("nullify", "nullify")]
    [TestCase("COM10", "COM10")]
    [TestCase("Lecture nul", "Lecture nul")]
    public void A_name_windows_keeps_for_a_device_gets_an_underscore(string name, string safe) =>
        Assert.That(ClassFolders.SafeName(name), Is.EqualTo(safe));

    /// <summary>A transcribed file gets a folder named after the file, and files stamped with the transcription time.</summary>
    [Test]
    public void A_file_gets_a_folder_named_after_it()
    {
        var files = Folders().CreateForFile(@"D:\Downloads\Lecture 5.mp4");

        Assert.That(files.Folder, Is.EqualTo(_folder.File("Lecture 5")));
        Assert.That(files.BaseName, Is.EqualTo("Lecture 5_2026-10-14_18-30-00Z"));
        Assert.That(Directory.Exists(files.Folder), Is.True);
    }

    /// <summary>
    /// A file whose name ends in a space or in dots gets a folder that can be written to. Windows drops
    /// those characters when it makes a folder, so a path that kept them would lead nowhere, and the
    /// transcript, already paid for, could not be saved.
    /// </summary>
    [TestCase(@"D:\Downloads\Lecture 5 .mp3", "Lecture 5")]
    [TestCase(@"D:\Downloads\Lecture 5. .mp4", "Lecture 5")]
    [TestCase(@"D:\Downloads\Lecture 5 ..mp4", "Lecture 5")]
    [TestCase(@"D:\Downloads\And then....mp4", "And then")]
    public void A_file_whose_name_ends_in_spaces_or_dots_gets_a_folder_that_can_be_written_to(string source, string folder)
    {
        var files = Folders().CreateForFile(source);

        Assert.That(files.Folder, Is.EqualTo(_folder.File(folder)));
        WriteTranscript(files);
    }

    /// <summary>A file named like a device, such as nul.wav, gets a folder that can be written to.</summary>
    [Test]
    public void A_file_named_like_a_device_gets_a_folder_that_can_be_written_to()
    {
        var files = Folders().CreateForFile(@"D:\Downloads\nul.wav");

        Assert.That(files.Folder, Is.EqualTo(_folder.File("nul_")));
        WriteTranscript(files);
    }

    /// <summary>A file with nothing usable in its name, such as ".mp3", still gets a folder of its own rather than the root or an error.</summary>
    [TestCase(@"D:\Downloads\.mp3")]
    [TestCase(@"D:\Downloads\....mp3")]
    [TestCase(@"D:\Downloads\ .mp3")]
    public void A_file_without_a_usable_name_gets_an_untitled_folder(string source)
    {
        var files = Folders().CreateForFile(source);

        Assert.That(files.Folder, Is.EqualTo(_folder.File("Untitled")));
        Assert.That(files.BaseName, Is.EqualTo("Untitled_2026-10-14_18-30-00Z"));
        WriteTranscript(files);
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

    /// <summary>
    /// The list gives a recording's answer that was paid for but did not become a transcript, kept beside it or, when
    /// its own folder would not take it, in the recordings folder. Of several, the newest is given; a recording
    /// without one, even with a transcript, has none.
    /// </summary>
    [Test]
    public void A_kept_answer_is_listed_with_its_recording()
    {
        var beside = WriteRecording("Kalam", new DateTimeOffset(2026, 10, 14, 19, 30, 0, TimeSpan.Zero));
        var inTheRoot = WriteRecording("Fiqh", new DateTimeOffset(2026, 10, 12, 10, 0, 0, TimeSpan.Zero));
        WriteRecording("Tafsir", new DateTimeOffset(2026, 10, 11, 10, 0, 0, TimeSpan.Zero), withTranscript: true);
        File.WriteAllText(beside.UnreadAnswer(), "{}");
        File.SetLastWriteTimeUtc(beside.UnreadAnswer(), new DateTime(2026, 10, 14, 21, 0, 0, DateTimeKind.Utc));
        File.WriteAllText(beside.Json(2), "{}");   // a later try whose transcript could not be written
        File.SetLastWriteTimeUtc(beside.Json(2), new DateTime(2026, 10, 15, 9, 0, 0, DateTimeKind.Utc));
        var keptInTheRoot = (inTheRoot with { Folder = _folder.Path }).UnreadAnswer();
        File.WriteAllText(keptInTheRoot, "{}");

        var recordings = Folders().ListRecent(9);

        Assert.That(recordings.Select(r => r.KeptAnswer), Is.EqualTo(new[] { beside.Json(2), keptInTheRoot, null }));
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

    /// <summary>
    /// A recording's length is worked out from the size of its file, read to the end as the upload reads
    /// it, so a header left behind by a crash still gives the whole length. A file the recorder did not
    /// write (empty, a header alone, a sample rate of zero, or not a WAV at all) counts as empty.
    /// </summary>
    [TestCase("stale header", 1.5)]
    [TestCase("header only", 0.0)]
    [TestCase("empty file", 0.0)]
    [TestCase("zero sample rate", 0.0)]
    [TestCase("not a recording", 0.0)]
    public void A_recordings_length_is_read_from_its_file(string segment, double seconds)
    {
        var path = WriteRecording("Kalam", Now, seconds: 1.0).Segment(1);
        switch (segment)
        {
            case "stale header":
                using (var stream = new FileStream(path, FileMode.Append)) stream.Write(new byte[16000]);
                break;
            case "header only":
                File.Delete(path);
                TestAudio.WriteWav(path, 16000, []);
                break;
            case "empty file":
                File.WriteAllBytes(path, []);
                break;
            case "zero sample rate":
                var bytes = File.ReadAllBytes(path);
                bytes.AsSpan(24, 4).Clear();
                File.WriteAllBytes(path, bytes);
                break;
            case "not a recording":
                File.WriteAllBytes(path, Enumerable.Repeat((byte)7, 200).ToArray());
                break;
        }

        var recorded = Folders().ListRecent(9).Single();

        Assert.That(recorded.Length.TotalSeconds, Is.EqualTo(seconds).Within(0.001));
    }

    /// <summary>
    /// A recording with a file another program holds open without sharing is left out of the list, rather
    /// than shown with a wrong length, and a warning names it. The other recordings, even of the same name,
    /// are still listed, so one locked file does not keep every recording from being picked.
    /// </summary>
    [Test]
    public void A_recording_held_open_by_another_program_is_left_out_with_a_warning()
    {
        var held = WriteRecording("Kalam", new DateTimeOffset(2026, 10, 14, 19, 30, 0, TimeSpan.Zero));
        var sameName = WriteRecording("Kalam", new DateTimeOffset(2026, 10, 11, 14, 5, 0, TimeSpan.Zero));
        var other = WriteRecording("Fiqh", new DateTimeOffset(2026, 10, 12, 10, 0, 0, TimeSpan.Zero));
        using var stream = HoldOpen(held);

        var recordings = Folders().ListRecent(9);

        Assert.That(recordings.Select(r => r.Files), Is.EqualTo(new[] { other, sameName }));
        Assert.That(_log.Problems, Has.Count.EqualTo(1));
        Assert.That(_log.Problems[0], Does.Contain(held.BaseName));
    }

    /// <summary>
    /// A recording with a file ClassScribe may not read, as when the file's permissions shut this user out,
    /// is left out of the list with a warning that names it, as one held open by another program is. The
    /// other recordings, even of the same name, are still listed, so the menu can still offer them.
    /// </summary>
    [Test, Platform("Win")]
    [SupportedOSPlatform("windows")]
    public void A_recording_whose_file_may_not_be_read_is_left_out_with_a_warning()
    {
        var shut = WriteRecording("Kalam", new DateTimeOffset(2026, 10, 14, 19, 30, 0, TimeSpan.Zero));
        var sameName = WriteRecording("Kalam", new DateTimeOffset(2026, 10, 11, 14, 5, 0, TimeSpan.Zero));
        var other = WriteRecording("Fiqh", new DateTimeOffset(2026, 10, 12, 10, 0, 0, TimeSpan.Zero));
        using var denied = Denied.ReadingOf(shut.Segment(1));
        Assume.That(() => File.ReadAllBytes(shut.Segment(1)), Throws.InstanceOf<UnauthorizedAccessException>(), "the file could not be shut to this user");

        var recordings = Folders().ListRecent(9);

        Assert.That(recordings.Select(r => r.Files), Is.EqualTo(new[] { other, sameName }));
        Assert.That(_log.Problems, Has.Count.EqualTo(1));
        Assert.That(_log.Problems[0], Does.Contain(shut.BaseName));
    }

    /// <summary>A recording whose file is hidden is still listed, as transcribing it would still find it.</summary>
    [Test]
    public void A_hidden_recording_is_still_listed()
    {
        var hidden = WriteRecording("Kalam", Now);
        File.SetAttributes(hidden.Segment(1), FileAttributes.Hidden);

        Assert.That(Folders().ListRecent(9).Select(r => r.Files), Is.EqualTo(new[] { hidden }));
    }

    /// <summary>
    /// The app's own folders, built by the service container, report a recording they leave out: the
    /// container gives them the app's logger, so the warning reaches the console.
    /// </summary>
    [Test]
    public void The_apps_folders_report_a_recording_they_leave_out()
    {
        var held = WriteRecording("Kalam", Now);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Recording:OutputFolder"] = _folder.Path })
            .Build();
        using var services = new ServiceCollection()
            .AddLogging()
            .AddClassScribe(configuration)
            .AddSingleton<ILogger<ClassFolders>>(_log)
            .BuildServiceProvider();
        using var stream = HoldOpen(held);

        Assert.That(services.GetRequiredService<ClassFolders>().ListRecent(9), Is.Empty);
        Assert.That(_log.Problems, Has.Count.EqualTo(1));
    }

    /// <summary>
    /// A folder under the root that ClassScribe may not look into, such as one copied from another account
    /// or System Volume Information when the root is a drive, is passed over without a warning, and the
    /// recordings in the other folders are still listed. A drive's own system folders are no news, so
    /// they are not reported every time the list is shown.
    /// </summary>
    [Test, Platform("Win")]
    [SupportedOSPlatform("windows")]
    public void A_folder_that_cannot_be_looked_into_is_passed_over_quietly()
    {
        var listed = WriteRecording("Kalam", Now);
        var shut = WriteRecording("Fiqh", Now.AddDays(-1)).Folder;
        using var denied = Denied.ListingOf(shut);
        Assume.That(() => Directory.GetFiles(shut), Throws.InstanceOf<UnauthorizedAccessException>(), "the folder could not be shut to this user");

        Assert.That(Folders().ListRecent(9).Select(r => r.Files), Is.EqualTo(new[] { listed }));
        Assert.That(_log.Problems, Is.Empty);
    }

    /// <summary>
    /// A folder under the root that cannot be read for another reason, such as a link to a drive that is
    /// not plugged in, is left out of the list with a warning that names it, and the recordings in the
    /// other folders are still listed.
    /// </summary>
    [Test, Platform("Win")]
    [SupportedOSPlatform("windows")]
    public void A_folder_that_leads_nowhere_is_left_out_with_a_warning()
    {
        var listed = WriteRecording("Kalam", Now);
        var link = _folder.File("Fiqh");
        MakeJunction(link, _folder.File("unplugged"));
        try
        {
            Assume.That(new DirectoryInfo(link).LinkTarget, Is.Not.Null, "mklink could not make the junction");

            var recordings = Folders().ListRecent(9);

            Assert.That(recordings.Select(r => r.Files), Is.EqualTo(new[] { listed }));
            Assert.That(_log.Problems, Has.Count.EqualTo(1));
            Assert.That(_log.Problems[0], Does.Contain(link));
        }
        finally
        {
            // Removes the junction itself; what it points to does not exist.
            if (new DirectoryInfo(link).LinkTarget is not null) Directory.Delete(link);
        }
    }

    /// <summary>
    /// A root that ClassScribe may not look into is an error that names it, not a root without recordings:
    /// the setting needs fixing, and an empty list would hide that.
    /// </summary>
    [Test, Platform("Win")]
    [SupportedOSPlatform("windows")]
    public void A_root_that_cannot_be_looked_into_is_an_error()
    {
        WriteRecording("Kalam", Now);
        using var denied = Denied.ListingOf(_folder.Path);

        var error = Assert.Throws<UnauthorizedAccessException>(() => Folders().ListRecent(9));

        Assert.That(error!.Message, Does.Contain(_folder.Path));
    }

    /// <summary>
    /// Transcribing a folder looks into it strictly: a folder ClassScribe may not look into is an error,
    /// not a folder without recordings. Only the menu's list passes such folders over.
    /// </summary>
    [Test, Platform("Win")]
    [SupportedOSPlatform("windows")]
    public void The_newest_recording_of_a_folder_that_cannot_be_looked_into_is_an_error()
    {
        var folder = WriteRecording("Kalam", Now).Folder;
        using var denied = Denied.ListingOf(folder);

        Assert.That(() => ClassFolders.FindNewest(folder), Throws.InstanceOf<UnauthorizedAccessException>());
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
