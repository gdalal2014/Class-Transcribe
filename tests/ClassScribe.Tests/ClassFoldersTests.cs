using System.Diagnostics;
using System.Runtime.Versioning;
using ClassScribe.Audio;
using ClassScribe.Settings;
using ClassScribe.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

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

    /// <summary>
    /// Creates the folders under test, rooted in the temporary folder unless another root is given, on the test's clock
    /// read in US Eastern time, where now is 14:30 on 14 October: a new recording's date folder is 2026-10-14 on every machine.
    /// </summary>
    private ClassFolders Folders(string? outputFolder = null, double longestFileHours = 10) =>
        new(TestOptions.Create(new RecordingOptions { OutputFolder = outputFolder ?? _folder.Path }), new ZonedClock(_time, ZonedTimeProvider.EasternSummer.LocalTimeZone), _log,
            elevenLabs: TestOptions.Create(new ElevenLabsOptions { LongestFileHours = longestFileHours }));

    /// <summary>Holds a recording's first file open without sharing, as another program might.</summary>
    private static FileStream HoldOpen(RecordingFiles files) =>
        new(files.Segment(1), FileMode.Open, FileAccess.ReadWrite, FileShare.None);

    /// <summary>Holds a segment open for writing while letting others read it, as the recorder holds the segment it writes.</summary>
    private static FileStream HoldForWriting(string segment) =>
        new(segment, FileMode.Open, FileAccess.Write, FileShare.Read);

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

    /// <summary>
    /// Writes a recording of the given length into the folder of its name, under <paramref name="root"/> or else the
    /// temporary folder, last written at the given time.
    /// </summary>
    private RecordingFiles WriteRecording(string name, DateTimeOffset started, double seconds = 0.1, bool withTranscript = false, string? root = null)
    {
        var files = RecordingFiles.ForNew(Path.Combine(root ?? _folder.Path, name), name, started);
        Directory.CreateDirectory(files.Folder);
        TestAudio.WriteWav(files.Segment(1), 16000, new short[(int)(16000 * seconds)]);
        File.SetLastWriteTimeUtc(files.Segment(1), started.UtcDateTime);
        if (withTranscript) File.WriteAllText(files.Transcript(), "# Kalam");
        return files;
    }

    /// <summary>
    /// Writes a recording of <paramref name="name"/> that started an hour before now and stopped
    /// <paramref name="stoppedAgo"/> before now, as one stopped by a crash or with Q leaves its audio.
    /// </summary>
    private RecordingFiles WriteStopped(string name, TimeSpan stoppedAgo, double seconds = 1.0, string? root = null)
    {
        var files = WriteRecording(name, Now.AddHours(-1), seconds, root: root);
        File.SetLastWriteTimeUtc(files.Segment(1), (Now - stoppedAgo).UtcDateTime);
        return files;
    }

    /// <summary>
    /// Writes segment <paramref name="number"/> of the recording's microphone track, which ends in .mic.wav, holding
    /// <paramref name="seconds"/> of audio and last written <paramref name="stoppedAgo"/> before now, and returns its path.
    /// </summary>
    private static string WriteMicrophone(RecordingFiles files, int number, double seconds = 1.0, TimeSpan? stoppedAgo = null)
    {
        var path = Path.Combine(files.Folder, number == 1 ? $"{files.BaseName}.mic.wav" : $"{files.BaseName}_part{number}.mic.wav");
        TestAudio.WriteWav(path, 16000, new short[(int)(16000 * seconds)]);
        File.SetLastWriteTimeUtc(path, (Now - (stoppedAgo ?? TimeSpan.FromMinutes(5))).UtcDateTime);
        return path;
    }

    /// <summary>
    /// Writes a segment of <paramref name="length"/> with the recorder's own writer at ten samples a second, so that hours
    /// of it take less than a megabyte.
    /// </summary>
    private static void WriteLong(string segment, TimeSpan length)
    {
        File.Delete(segment);
        using var writer = new WavSegmentWriter(segment, 10);
        writer.Write(new byte[(int)Math.Round(length.TotalSeconds * 10) * 2]);
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

    /// <summary>The folders live as long as the app; the root, and what is said about it, follow the setting as it is read again.</summary>
    [Test]
    public void The_root_follows_the_setting_as_it_is_read_again()
    {
        var settings = TestOptions.Create(new RecordingOptions { OutputFolder = _folder.File("First") });
        var folders = new ClassFolders(settings, _time, _log);
        var inTheWay = _folder.File("Second");
        File.WriteAllText(inTheWay, "not a folder");

        settings.Set(new RecordingOptions { OutputFolder = inTheWay });

        Assert.That(folders.Root, Is.EqualTo(inTheWay));
        Assert.That(folders.DescribeRootProblem(), Does.StartWith($"{inTheWay} is a file"));
    }

    /// <summary>The root a setting stands for is worked out the same way everywhere: here for an empty setting, a relative one and one with quotes.</summary>
    [Test]
    public void The_root_for_a_setting_is_worked_out_without_the_folders()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        Assert.That(ClassFolders.RootFor(""), Is.EqualTo(Path.Combine(profile, "ClassScribe")));
        Assert.That(ClassFolders.RootFor(null), Is.EqualTo(Path.Combine(profile, "ClassScribe")));
        Assert.That(ClassFolders.RootFor("Classes"), Is.EqualTo(Path.Combine(profile, "Classes")));
        Assert.That(ClassFolders.RootFor($"\"{_folder.File("Classes")}\""), Is.EqualTo(_folder.File("Classes")));
    }

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

    /// <summary>
    /// A recording's folder is the date folder of its start under the folder of its name; its files carry the name and the
    /// UTC start time. The name's folder is shared by later recordings of the same name, each class in a date folder of its own.
    /// </summary>
    [Test]
    public void A_recording_gets_its_name_as_folder_and_its_start_time_in_its_files()
    {
        var folders = Folders();

        var first = folders.CreateRecording("Kalam");
        TestAudio.WriteWav(first.Segment(1), 16000, [1]);
        var second = folders.CreateRecording("Kalam");

        Assert.That(first.Folder, Is.EqualTo(_folder.File(@"Kalam\2026-10-14")));
        Assert.That(Directory.Exists(first.Folder), Is.True);
        Assert.That(first.BaseName, Is.EqualTo("Kalam_2026-10-14_18-30-00Z"));
        Assert.That(second.Folder, Is.EqualTo(_folder.File(@"Kalam\2026-10-14 (2)")), "recordings of one class share the folder of its name");
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
        Assert.That(FolderNames.SafeName("  Kalam: lesson  <12>?  "), Is.EqualTo("Kalam lesson 12"));
        Assert.That(FolderNames.SafeName("Uṣūl al-dīn, part 1"), Is.EqualTo("Uṣūl al-dīn, part 1"), "letters outside ASCII and commas are fine");
        Assert.That(FolderNames.SafeName(" ... "), Is.Empty, "a name of only dots would not be a valid folder");
        Assert.That(FolderNames.SafeName(""), Is.Empty);
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
        Assert.That(FolderNames.SafeName(name), Is.EqualTo(safe));

    /// <summary>A transcribed file gets a folder named after the file, with the date folder of the transcription in it, and files stamped with the transcription time.</summary>
    [Test]
    public void A_file_gets_a_folder_named_after_it()
    {
        var files = Folders().CreateForFile(@"D:\Downloads\Lecture 5.mp4");

        Assert.That(files.Folder, Is.EqualTo(Path.Combine(_folder.File("Lecture 5"), "2026-10-14")));
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

        Assert.That(files.Folder, Is.EqualTo(Path.Combine(_folder.File(folder), "2026-10-14")));
        WriteTranscript(files);
    }

    /// <summary>A file named like a device, such as nul.wav, gets a folder that can be written to.</summary>
    [Test]
    public void A_file_named_like_a_device_gets_a_folder_that_can_be_written_to()
    {
        var files = Folders().CreateForFile(@"D:\Downloads\nul.wav");

        Assert.That(files.Folder, Is.EqualTo(Path.Combine(_folder.File("nul_"), "2026-10-14")));
        WriteTranscript(files);
    }

    /// <summary>A file with nothing usable in its name, such as ".mp3", still gets a folder of its own rather than the root or an error.</summary>
    [TestCase(@"D:\Downloads\.mp3")]
    [TestCase(@"D:\Downloads\....mp3")]
    [TestCase(@"D:\Downloads\ .mp3")]
    public void A_file_without_a_usable_name_gets_an_untitled_folder(string source)
    {
        var files = Folders().CreateForFile(source);

        Assert.That(files.Folder, Is.EqualTo(Path.Combine(_folder.File("Untitled"), "2026-10-14")));
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
        using var runtime = new TestRuntime();
        using var services = new ServiceCollection()
            .AddLogging()
            .AddClassScribe(configuration)
            .AddSingleton(runtime.Folder)
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

    /// <summary>
    /// A recording is being written while a writer holds its newest segment open and lets others read it, as the
    /// recorder does with the segment it writes. An earlier segment held that way does not count, since the recorder has
    /// closed those, and once the writer lets go the recording is no longer being written.
    /// </summary>
    [Test]
    public void A_recording_whose_newest_segment_is_held_open_for_writing_is_being_written()
    {
        var files = WriteRecording("Kalam", Now);
        TestAudio.WriteWav(files.Segment(2), 16000, new short[1600]);

        using (HoldForWriting(files.Segment(2)))
            Assert.That(ClassFolders.IsBeingWritten(files), Is.True);

        using (HoldForWriting(files.Segment(1)))
            Assert.That(ClassFolders.IsBeingWritten(files), Is.False, "an earlier segment is not the one being written");

        Assert.That(ClassFolders.IsBeingWritten(files), Is.False, "nothing writes to it any more");
    }

    /// <summary>
    /// A newest segment that another program holds without sharing it at all, as a scanner might, does not make the
    /// recording one being recorded: the recorder always lets others read what it writes. Transcribing it then fails on
    /// the file itself, with the real reason.
    /// </summary>
    [Test]
    public void A_segment_held_without_any_sharing_is_not_taken_for_one_being_recorded()
    {
        var files = WriteRecording("Kalam", Now);
        using var held = HoldOpen(files);

        Assert.That(ClassFolders.IsBeingWritten(files), Is.False);
    }

    /// <summary>A recording without any segment on disk, such as one whose audio was deleted, is not being written.</summary>
    [Test]
    public void A_recording_without_audio_is_not_being_written()
    {
        var files = WriteRecording("Kalam", Now);
        File.Delete(files.Segment(1));

        Assert.That(ClassFolders.IsBeingWritten(files), Is.False);
        Assert.That(ClassFolders.IsBeingWritten(new RecordingFiles(_folder.File("not-there"), "Kalam_2026-10-14_18-30-00Z")), Is.False);
    }

    /// <summary>The list says which recording is being written, so that the menu can show it and refuse to transcribe it.</summary>
    [Test]
    public void The_list_marks_a_recording_that_is_being_written()
    {
        var recordingNow = WriteRecording("Kalam", new DateTimeOffset(2026, 10, 14, 19, 30, 0, TimeSpan.Zero));
        var stopped = WriteRecording("Fiqh", new DateTimeOffset(2026, 10, 12, 10, 0, 0, TimeSpan.Zero));

        IReadOnlyList<RecordedClass> recordings;
        using (HoldForWriting(recordingNow.Segment(1)))
            recordings = Folders().ListRecent(9);

        Assert.That(recordings.Select(r => (r.Files, r.BeingWritten)), Is.EqualTo(new[] { (recordingNow, true), (stopped, false) }));
        Assert.That(_log.Problems, Is.Empty, "a recording being written is listed, not left out");
    }

    /// <summary>
    /// The recordings in one folder are listed newest first by the time each started, whatever their files' write
    /// times, so that the first is the one <see cref="ClassFolders.FindNewest"/> finds. Each is described as the menu's
    /// list describes it: its length, its transcript and the answer kept for it. Other folders and stray files are left out.
    /// </summary>
    [Test]
    public void The_recordings_in_one_folder_are_listed_newest_first_and_described()
    {
        var older = WriteRecording("Kalam", new DateTimeOffset(2026, 10, 11, 14, 5, 0, TimeSpan.Zero), seconds: 2.0, withTranscript: true);
        var newer = WriteRecording("Kalam", new DateTimeOffset(2026, 10, 14, 19, 30, 0, TimeSpan.Zero), seconds: 1.0);
        File.SetLastWriteTimeUtc(older.Segment(1), new DateTime(2026, 10, 15, 9, 0, 0, DateTimeKind.Utc));
        File.WriteAllText(newer.UnreadAnswer(), "{}");
        WriteRecording("Fiqh", new DateTimeOffset(2026, 10, 16, 10, 0, 0, TimeSpan.Zero));
        File.WriteAllBytes(_folder.File("Kalam/notes.wav"), [0]);
        File.WriteAllBytes(newer.Upload, [0]);

        var recordings = Folders().RecordingsIn(newer.Folder);

        Assert.That(recordings.Select(r => r.Files), Is.EqualTo(new[] { newer, older }));
        Assert.That(recordings[0].Files, Is.EqualTo(ClassFolders.FindNewest(newer.Folder)));
        Assert.That(recordings.Select(r => r.HasTranscript), Is.EqualTo(new[] { false, true }));
        Assert.That(recordings.Select(r => r.KeptAnswer), Is.EqualTo(new[] { newer.UnreadAnswer(), null }));
        Assert.That(recordings.Select(r => r.Length.TotalSeconds), Is.EqualTo(new[] { 1.0, 2.0 }).Within(0.001));
    }

    /// <summary>
    /// A recording in the folder with a file another program holds without sharing is left out of the recordings in the
    /// folder with a warning that names it, as in the menu's list; the others are still given.
    /// </summary>
    [Test]
    public void A_recording_in_the_folder_that_cannot_be_read_is_left_out_with_a_warning()
    {
        var held = WriteRecording("Kalam", new DateTimeOffset(2026, 10, 14, 19, 30, 0, TimeSpan.Zero));
        var readable = WriteRecording("Kalam", new DateTimeOffset(2026, 10, 11, 14, 5, 0, TimeSpan.Zero));
        using var stream = HoldOpen(held);

        Assert.That(Folders().RecordingsIn(held.Folder).Select(r => r.Files), Is.EqualTo(new[] { readable }));
        Assert.That(_log.Problems, Has.Count.EqualTo(1));
        Assert.That(_log.Problems[0], Does.Contain(held.BaseName));
    }

    /// <summary>A folder ClassScribe may not look into is an error for the recordings in it, as it is for the newest one: not a folder without recordings.</summary>
    [Test, Platform("Win")]
    [SupportedOSPlatform("windows")]
    public void The_recordings_in_a_folder_that_cannot_be_looked_into_are_an_error()
    {
        var folder = WriteRecording("Kalam", Now).Folder;
        using var denied = Denied.ListingOf(folder);
        Assume.That(() => Directory.GetFiles(folder), Throws.InstanceOf<UnauthorizedAccessException>(), "the folder could not be shut to this user");

        Assert.That(() => Folders().RecordingsIn(folder), Throws.InstanceOf<UnauthorizedAccessException>());
    }

    /// <summary>A segment is measured to the end of its file, as the upload reads it, even when its header was left behind by a crash.</summary>
    [Test]
    public void A_segment_is_measured_to_the_end_of_its_file()
    {
        var segment = WriteRecording("Kalam", Now, seconds: 1.0).Segment(1);
        using (var stream = new FileStream(segment, FileMode.Append)) stream.Write(new byte[16000]);

        Assert.That(ClassFolders.MeasureSegment(segment).TotalSeconds, Is.EqualTo(1.5).Within(0.001));
    }

    /// <summary>
    /// The answer kept for a recording is found beside it or, when its own folder would not take it, in the recordings
    /// folder; of several, the newest. A recording without one has none, even with a transcript.
    /// </summary>
    [Test]
    public void The_answer_kept_for_a_recording_is_found_beside_it_or_in_the_recordings_folder()
    {
        var beside = WriteRecording("Kalam", new DateTimeOffset(2026, 10, 14, 19, 30, 0, TimeSpan.Zero));
        var inTheRoot = WriteRecording("Fiqh", new DateTimeOffset(2026, 10, 12, 10, 0, 0, TimeSpan.Zero));
        var without = WriteRecording("Tafsir", new DateTimeOffset(2026, 10, 11, 10, 0, 0, TimeSpan.Zero), withTranscript: true);
        File.WriteAllText(beside.UnreadAnswer(), "{}");
        File.SetLastWriteTimeUtc(beside.UnreadAnswer(), new DateTime(2026, 10, 14, 21, 0, 0, DateTimeKind.Utc));
        File.WriteAllText(beside.Json(2), "{}");
        File.SetLastWriteTimeUtc(beside.Json(2), new DateTime(2026, 10, 15, 9, 0, 0, DateTimeKind.Utc));
        var keptInTheRoot = (inTheRoot with { Folder = _folder.Path }).UnreadAnswer();
        File.WriteAllText(keptInTheRoot, "{}");
        var folders = Folders();

        Assert.That(folders.KeptAnswerOf(beside), Is.EqualTo(beside.Json(2)));
        Assert.That(folders.KeptAnswerOf(inTheRoot), Is.EqualTo(keptInTheRoot));
        Assert.That(folders.KeptAnswerOf(without), Is.Null);
    }

    /// <summary>
    /// A recording goes under the recordings folder given for its run (--output), in a folder of its name and date there, and
    /// under the setting's folder when none is given; the setting's folder stays the root either way.
    /// </summary>
    [Test]
    public void A_recording_goes_under_the_root_given_for_its_run()
    {
        var folders = Folders();
        var elsewhere = _folder.File("Elsewhere");

        var given = folders.CreateRecording("Kalam", elsewhere);
        var unset = folders.CreateRecording("Fiqh", root: null);

        Assert.That(given.Folder, Is.EqualTo(Path.Combine(elsewhere, "Kalam", "2026-10-14")));
        Assert.That(given.BaseName, Is.EqualTo("Kalam_2026-10-14_18-30-00Z"));
        Assert.That(Directory.Exists(given.Folder), Is.True);
        Assert.That(unset.Folder, Is.EqualTo(Path.Combine(_folder.File("Fiqh"), "2026-10-14")));
        Assert.That(folders.Root, Is.EqualTo(_folder.Path));
    }

    /// <summary>A file transcribed with a recordings folder given for it gets the folder of its transcript there.</summary>
    [Test]
    public void A_file_gets_its_folder_under_the_root_given_for_it()
    {
        var files = Folders().CreateForFile(@"D:\Downloads\Lecture 5.mp4", _folder.File("Elsewhere"));

        Assert.That(files.Folder, Is.EqualTo(Path.Combine(_folder.File("Elsewhere"), "Lecture 5", "2026-10-14")));
        Assert.That(Directory.Exists(files.Folder), Is.True);
    }

    /// <summary>A recording to continue is opened by its folder and base name, as a run's request names it: its files are the ones it was recorded into.</summary>
    [Test]
    public void A_recording_to_continue_is_opened_by_its_folder_and_base_name()
    {
        var files = WriteRecording("Kalam", Now.AddHours(-1));

        Assert.That(Folders().Open(files.Folder, files.BaseName), Is.EqualTo(files));
    }

    /// <summary>A recording to continue with no audio left in its folder is an error that names it, rather than a recording started afresh under its old name.</summary>
    [Test]
    public void A_recording_to_continue_without_audio_is_an_error_that_names_it()
    {
        var files = WriteRecording("Kalam", Now.AddHours(-1));
        File.Delete(files.Segment(1));

        var error = Assert.Throws<FileNotFoundException>(() => Folders().Open(files.Folder, files.BaseName));

        Assert.That(error!.Message, Does.Contain(files.BaseName));
    }

    /// <summary>A base name that is not a recording's, such as one that leads out of the folder or has no time stamp, is refused.</summary>
    [TestCase(@"..\Kalam_2026-10-14_18-30-00Z")]
    [TestCase(@"Fiqh\Kalam_2026-10-14_18-30-00Z")]
    [TestCase("Kalam")]
    [TestCase("")]
    public void A_base_name_that_is_not_a_recordings_is_refused(string baseName) =>
        Assert.Throws<ArgumentException>(() => Folders().Open(_folder.File("Kalam"), baseName));

    /// <summary>
    /// A recording of the name that stopped recently without a transcript is offered to be continued, with its length on
    /// disk, when its audio was last written, and the parts it was made in so far.
    /// </summary>
    [Test]
    public void A_recording_that_stopped_recently_without_a_transcript_is_offered()
    {
        var files = WriteStopped("Kalam", TimeSpan.FromMinutes(4), seconds: 1.5);
        new ClassMarks(NullLogger<ClassMarks>.Instance).AddResume(files, TimeSpan.FromSeconds(1), Now.AddMinutes(-30), TimeSpan.FromMinutes(2));

        var candidate = Folders().FindContinuable("Kalam", RecordingMode.Class, TimeSpan.FromMinutes(60));

        Assert.That(candidate, Is.EqualTo(new ContinueCandidate(files, TimeSpan.FromSeconds(1.5), Now.AddMinutes(-4), Parts: 2)));
    }

    /// <summary>
    /// Only a recording whose audio was last written within the time allowed is offered: with an hour, one that stopped 59
    /// minutes ago is, and one that stopped 61 minutes ago is not. A time of zero never offers one.
    /// </summary>
    [TestCase(59, 60, true)]
    [TestCase(61, 60, false)]
    [TestCase(0, 0, false)]
    public void Only_a_recording_that_stopped_within_the_time_allowed_is_offered(int stoppedMinutesAgo, int withinMinutes, bool offered)
    {
        WriteStopped("Kalam", TimeSpan.FromMinutes(stoppedMinutesAgo));

        var candidate = Folders().FindContinuable("Kalam", RecordingMode.Class, TimeSpan.FromMinutes(withinMinutes));

        Assert.That(candidate is not null, Is.EqualTo(offered));
    }

    /// <summary>
    /// A recording as long as ClassScribe sends in one file, ElevenLabs:LongestFileHours less half an hour, is not offered:
    /// continued, it could no longer be sent whole (architecture 7.2). With a file of an hour and three quarters, one of an
    /// hour and a quarter is not offered, though it is with ElevenLabs' own 10 hours. Its audio is written at 10 Hz, so the
    /// hour and a quarter takes 90 KB.
    /// </summary>
    [Test]
    public void A_recording_as_long_as_one_file_less_half_an_hour_is_not_offered()
    {
        var files = WriteStopped("Kalam", TimeSpan.FromMinutes(4));
        File.Delete(files.Segment(1));
        using (var writer = new WavSegmentWriter(files.Segment(1), 10)) writer.Write(new byte[(int)TimeSpan.FromHours(1.25).TotalSeconds * 10 * 2]);
        File.SetLastWriteTimeUtc(files.Segment(1), Now.AddMinutes(-4).UtcDateTime);

        Assert.That(Folders(longestFileHours: 1.75).FindContinuable("Kalam", RecordingMode.Class, TimeSpan.FromMinutes(60)), Is.Null);
        Assert.That(Folders().FindContinuable("Kalam", RecordingMode.Class, TimeSpan.FromMinutes(60))?.Length, Is.EqualTo(TimeSpan.FromHours(1.25)));
    }

    /// <summary>A recording with a transcript, here a later copy of one, is finished: it is not offered, even a moment after it stopped.</summary>
    [Test]
    public void A_recording_with_a_transcript_is_not_offered()
    {
        var files = WriteStopped("Kalam", TimeSpan.FromMinutes(1));
        File.WriteAllText(files.Transcript(2), "# Kalam");

        Assert.That(Folders().FindContinuable("Kalam", RecordingMode.Class, TimeSpan.FromMinutes(60)), Is.Null);
    }

    /// <summary>
    /// A recording with an answer from ElevenLabs that was paid for but did not become a transcript is not offered,
    /// wherever the answer is kept: beside it, in the recordings folder when its own folder would not take it (the
    /// setting's, where the workflow keeps it, or the one given for the run), or saved without its transcript. Continued,
    /// it would be sent again whole, so what was paid for would be billed twice; the answer can be laid out for free
    /// instead. The reason is logged with where the answer is.
    /// </summary>
    [TestCase("unread, beside it", false)]
    [TestCase("unread, in the recordings folder", false)]
    [TestCase("saved without its transcript", false)]
    [TestCase("unread, in the recordings folder given for the run", true)]
    [TestCase("unread, in the setting's recordings folder, for a run given another", true)]
    public void A_recording_with_an_answer_paid_for_and_kept_is_not_offered(string kept, bool rootGiven)
    {
        var root = rootGiven ? _folder.File("Elsewhere") : null;
        var files = WriteStopped("Kalam", TimeSpan.FromMinutes(4), root: root);
        var answer = kept switch
        {
            "unread, beside it" => files.UnreadAnswer(),
            "saved without its transcript" => files.Json(),
            "unread, in the recordings folder given for the run" => (files with { Folder = root! }).UnreadAnswer(),
            _ => (files with { Folder = _folder.Path }).UnreadAnswer()
        };
        var folders = Folders();
        Assert.That(folders.FindContinuable("Kalam", RecordingMode.Class, TimeSpan.FromMinutes(60), root), Is.Not.Null, "offered before the answer was kept");

        File.WriteAllText(answer, "{}");

        Assert.That(folders.FindContinuable("Kalam", RecordingMode.Class, TimeSpan.FromMinutes(60), root), Is.Null);
        Assert.That(_log.Messages, Has.Some.Contains($"which has been paid for, is kept in {answer}"));
    }

    /// <summary>
    /// A recording with its upload copy beside it and no transcript is not offered: a recorder or a window is transcribing
    /// it now, or a transcription of it was cut off, and ElevenLabs may bill it either way. Continued, it would be sent
    /// again whole. Once a failed transcription has removed its copy, it is offered again.
    /// </summary>
    [Test]
    public void A_recording_being_transcribed_is_not_offered()
    {
        var files = WriteStopped("Kalam", TimeSpan.FromMinutes(1));
        var folders = Folders();
        Assert.That(folders.FindContinuable("Kalam", RecordingMode.Class, TimeSpan.FromMinutes(60)), Is.Not.Null, "offered before its upload copy was made");

        TestAudio.WriteWav(files.Upload, 16000, new short[1600]);
        Assert.That(folders.FindContinuable("Kalam", RecordingMode.Class, TimeSpan.FromMinutes(60)), Is.Null);
        Assert.That(_log.Messages, Has.Some.Contains("upload copy"));

        File.Delete(files.Upload);
        Assert.That(folders.FindContinuable("Kalam", RecordingMode.Class, TimeSpan.FromMinutes(60))?.Files, Is.EqualTo(files));
    }

    /// <summary>A recording that a recorder still writes, holding its newest segment open for writing, is not offered.</summary>
    [Test]
    public void A_recording_being_written_is_not_offered()
    {
        var files = WriteStopped("Kalam", TimeSpan.FromMinutes(1));
        using var writer = HoldForWriting(files.Segment(1));

        Assert.That(Folders().FindContinuable("Kalam", RecordingMode.Class, TimeSpan.FromMinutes(60)), Is.Null);
    }

    /// <summary>
    /// A recording whose microphone track a recorder still writes is being written too, although its class track is not,
    /// and is not offered.
    /// </summary>
    [Test]
    public void A_recording_whose_microphone_is_being_written_is_not_offered()
    {
        var files = WriteStopped("Kalam", TimeSpan.FromMinutes(1));
        var microphone = WriteMicrophone(files, 1, stoppedAgo: TimeSpan.FromMinutes(1));
        using var writer = HoldForWriting(microphone);

        Assert.That(ClassFolders.IsBeingWritten(files), Is.True);
        Assert.That(Folders().FindContinuable("Kalam", RecordingMode.ClassAndMicrophone, TimeSpan.FromMinutes(60)), Is.Null);
    }

    /// <summary>
    /// A recording with the microphone stopped when the last of its tracks did: written to last on the microphone's track,
    /// it is offered within the time allowed after that, although its class track stopped longer ago, and that is the time
    /// it is said to have stopped.
    /// </summary>
    [Test]
    public void A_recording_with_the_microphone_stopped_when_the_last_of_its_tracks_did()
    {
        var files = WriteStopped("Kalam", TimeSpan.FromMinutes(61), seconds: 1.0);
        WriteMicrophone(files, 1, seconds: 1.0, stoppedAgo: TimeSpan.FromMinutes(4));

        var candidate = Folders().FindContinuable("Kalam", RecordingMode.ClassAndMicrophone, TimeSpan.FromMinutes(60));

        Assert.That(candidate, Is.EqualTo(new ContinueCandidate(files, TimeSpan.FromSeconds(1), Now.AddMinutes(-4), Parts: 1)));
    }

    /// <summary>
    /// A recording whose microphone track holds nine and a half hours or more is not offered, although its class track is
    /// short: the longer track decides. One a second shorter still is.
    /// </summary>
    [TestCase(9 * 3600 + 30 * 60, false)]
    [TestCase(9 * 3600 + 30 * 60 - 1, true)]
    public void A_recording_whose_microphone_track_is_nine_and_a_half_hours_or_more_is_not_offered(int seconds, bool offered)
    {
        var files = WriteStopped("Kalam", TimeSpan.FromMinutes(5));
        var microphone = WriteMicrophone(files, 1);
        WriteLong(microphone, TimeSpan.FromSeconds(seconds));
        File.SetLastWriteTimeUtc(microphone, Now.AddMinutes(-5).UtcDateTime);

        var candidate = Folders().FindContinuable("Kalam", RecordingMode.ClassAndMicrophone, TimeSpan.FromMinutes(60));

        Assert.That(candidate is not null, Is.EqualTo(offered));
    }

    /// <summary>
    /// A recording with the microphone is described from both its tracks, in a listing as for one recording: its length
    /// is the longer track's, here the microphone's; it was last recorded when its microphone's track was last written,
    /// after its class track; and it is being written while a recorder holds the newest segment of the microphone's track,
    /// though not an earlier one.
    /// </summary>
    [Test]
    public void A_recording_with_the_microphone_is_described_from_both_its_tracks()
    {
        var files = WriteStopped("Kalam", TimeSpan.FromMinutes(30), seconds: 1.0);
        var firstMicrophone = WriteMicrophone(files, 1, seconds: 1.5, stoppedAgo: TimeSpan.FromMinutes(20));
        var newestMicrophone = WriteMicrophone(files, 2, seconds: 1.0, stoppedAgo: TimeSpan.FromMinutes(3));
        var folders = Folders();

        var described = folders.Describe(files);
        var listed = folders.ListAll().Single();
        RecordedClass whileWritten, whileEarlierHeld;
        using (HoldForWriting(newestMicrophone))
            whileWritten = folders.ListAll().Single();
        using (HoldForWriting(firstMicrophone))
            whileEarlierHeld = folders.Describe(files);

        Assert.That(listed, Is.EqualTo(described));
        Assert.That(described.Length.TotalSeconds, Is.EqualTo(2.5).Within(0.001), "the microphone's track, the longer");
        Assert.That(described.LastRecordedUtc, Is.EqualTo(Now.AddMinutes(-3).UtcDateTime), "the microphone's track, written to last");
        Assert.That((described.HasMicrophone, described.BeingWritten), Is.EqualTo((true, false)));
        Assert.That(whileWritten.BeingWritten, Is.True, "a recorder holds the newest segment of the microphone's track");
        Assert.That(whileEarlierHeld.BeingWritten, Is.False, "an earlier segment is not the one being written");
    }

    /// <summary>
    /// Only a recording with the same tracks as the new run is offered: one with the microphone's .mic.wav files for a run
    /// with the microphone, and one without them for a run of the class alone.
    /// </summary>
    [TestCase(false, RecordingMode.Class, true)]
    [TestCase(true, RecordingMode.Class, false)]
    [TestCase(false, RecordingMode.ClassAndMicrophone, false)]
    [TestCase(true, RecordingMode.ClassAndMicrophone, true)]
    public void Only_a_recording_with_the_same_tracks_is_offered(bool withMicrophone, RecordingMode mode, bool offered)
    {
        var files = WriteStopped("Kalam", TimeSpan.FromMinutes(5));
        if (withMicrophone) WriteMicrophone(files, 1);

        var candidate = Folders().FindContinuable("Kalam", mode, TimeSpan.FromMinutes(60));

        Assert.That(candidate is not null, Is.EqualTo(offered));
    }

    /// <summary>A recording of the name in another letter case is offered, in its folder as it is on disk, since Windows takes the two names for one.</summary>
    [Test]
    public void A_recording_of_the_name_in_another_letter_case_is_offered()
    {
        var files = WriteStopped("kalam", TimeSpan.FromMinutes(5));

        Assert.That(Folders().FindContinuable("KALAM", RecordingMode.Class, TimeSpan.FromMinutes(60))?.Files, Is.EqualTo(files));
    }

    /// <summary>
    /// A recording of nine and a half hours or more is not offered: continued, it would be longer than ClassScribe sends to
    /// ElevenLabs in one file. One a second shorter still is.
    /// </summary>
    [TestCase(9 * 3600 + 30 * 60, false)]
    [TestCase(9 * 3600 + 30 * 60 - 1, true)]
    public void A_recording_of_nine_and_a_half_hours_or_more_is_not_offered(int seconds, bool offered)
    {
        var files = WriteStopped("Kalam", TimeSpan.FromMinutes(5));
        WriteLong(files.Segment(1), TimeSpan.FromSeconds(seconds));
        File.SetLastWriteTimeUtc(files.Segment(1), Now.AddMinutes(-5).UtcDateTime);

        var candidate = Folders().FindContinuable("Kalam", RecordingMode.Class, TimeSpan.FromMinutes(60));

        Assert.That(candidate is not null, Is.EqualTo(offered));
    }

    /// <summary>
    /// Only the newest recording of the name is ever offered: when it has a transcript, an earlier one that stopped within
    /// the time allowed is not offered instead, since the class went on after it.
    /// </summary>
    [Test]
    public void Only_the_newest_recording_of_the_name_is_offered()
    {
        var earlier = WriteRecording("Kalam", Now.AddMinutes(-50));
        File.SetLastWriteTimeUtc(earlier.Segment(1), Now.AddMinutes(-40).UtcDateTime);
        var newest = WriteRecording("Kalam", Now.AddMinutes(-30), withTranscript: true);
        File.SetLastWriteTimeUtc(newest.Segment(1), Now.AddMinutes(-10).UtcDateTime);

        Assert.That(Folders().FindContinuable("Kalam", RecordingMode.Class, TimeSpan.FromMinutes(60)), Is.Null);
    }

    /// <summary>
    /// A recording of another name is never offered, even one moved into the name's folder by hand; nor is anything for a
    /// name without a folder, or without a letter that can go into one.
    /// </summary>
    [Test]
    public void Nothing_of_another_name_is_offered()
    {
        var other = RecordingFiles.ForNew(_folder.File("Kalam"), "Fiqh", Now.AddMinutes(-20));
        Directory.CreateDirectory(other.Folder);
        TestAudio.WriteWav(other.Segment(1), 16000, new short[1600]);
        File.SetLastWriteTimeUtc(other.Segment(1), Now.AddMinutes(-5).UtcDateTime);
        var folders = Folders();

        Assert.That(folders.FindContinuable("Kalam", RecordingMode.Class, TimeSpan.FromMinutes(60)), Is.Null);
        Assert.That(folders.FindContinuable("Tafsir", RecordingMode.Class, TimeSpan.FromMinutes(60)), Is.Null);
        Assert.That(folders.FindContinuable(" ... ", RecordingMode.Class, TimeSpan.FromMinutes(60)), Is.Null);
    }

    /// <summary>The recording to continue is looked for under the recordings folder given for the run (--output), rather than under the setting's.</summary>
    [Test]
    public void The_recording_to_continue_is_looked_for_under_the_root_given()
    {
        var elsewhere = _folder.File("Elsewhere");
        var files = WriteStopped("Kalam", TimeSpan.FromMinutes(5), root: elsewhere);
        var folders = Folders(outputFolder: _folder.File("Setting"));

        Assert.That(folders.FindContinuable("Kalam", RecordingMode.Class, TimeSpan.FromMinutes(60), elsewhere)?.Files, Is.EqualTo(files));
        Assert.That(folders.FindContinuable("Kalam", RecordingMode.Class, TimeSpan.FromMinutes(60)), Is.Null);
    }

    /// <summary>
    /// A recording's length on disk is that of its longest track, each track's segments read to the end of their files, so
    /// that a header a crash left behind still gives the whole length.
    /// </summary>
    [Test]
    public void The_length_on_disk_is_the_longest_track_read_to_the_end_of_its_files()
    {
        var files = WriteRecording("Kalam", Now, seconds: 1.0);
        TestAudio.WriteWav(files.Segment(2), 48000, new short[48000]);
        using (var stream = new FileStream(files.Segment(2), FileMode.Append)) stream.Write(new byte[48000]);   // half a second after the last header update
        var folders = Folders();

        Assert.That(folders.LengthOnDisk(files).TotalSeconds, Is.EqualTo(2.5).Within(0.001));

        WriteMicrophone(files, 1, seconds: 3.0);
        Assert.That(folders.LengthOnDisk(files).TotalSeconds, Is.EqualTo(3.0).Within(0.001));
    }

    /// <summary>
    /// The list command's listing: every recording under the root, however many, newest first by the time each started
    /// whatever was written to last, each with the parts it was made in, whether it has the microphone's track, and
    /// whether it is being written now.
    /// </summary>
    [Test]
    public void Every_recording_under_the_root_is_listed_newest_first_by_its_start()
    {
        var days = Enumerable.Range(1, 10).Select(day => WriteRecording("Kalam", new DateTimeOffset(2026, 10, day, 10, 0, 0, TimeSpan.Zero))).ToList();
        File.SetLastWriteTimeUtc(days[0].Segment(1), new DateTime(2026, 10, 13, 0, 0, 0, DateTimeKind.Utc));   // the oldest, written to last
        var continued = WriteRecording("Fiqh", new DateTimeOffset(2026, 10, 11, 10, 0, 0, TimeSpan.Zero));
        WriteMicrophone(continued, 1);
        new ClassMarks(NullLogger<ClassMarks>.Instance).AddResume(continued, TimeSpan.FromSeconds(0.05), Now, TimeSpan.FromMinutes(3));
        var recordingNow = WriteRecording("Tafsir", new DateTimeOffset(2026, 10, 12, 10, 0, 0, TimeSpan.Zero));

        IReadOnlyList<RecordedClass> all;
        using (HoldForWriting(recordingNow.Segment(1)))
            all = Folders().ListAll();

        Assert.That(all.Select(r => r.Files), Is.EqualTo(new[] { recordingNow, continued }.Concat(Enumerable.Reverse(days))));
        Assert.That(all.Take(3).Select(r => (r.Parts, r.HasMicrophone, r.BeingWritten)), Is.EqualTo(new[] { (1, false, true), (2, true, false), (1, false, false) }));
        Assert.That(_log.Problems, Is.Empty);
    }

    /// <summary>A recordings folder given for the listing (--output) is listed instead of the setting's; one that is not there lists nothing.</summary>
    [Test]
    public void A_root_given_for_the_listing_is_listed_instead_of_the_settings()
    {
        WriteRecording("Kalam", Now);
        var elsewhere = WriteRecording("Fiqh", Now, root: _folder.File("Elsewhere"));
        var folders = Folders(outputFolder: _folder.Path);

        Assert.That(folders.ListAll(_folder.File("Elsewhere")).Select(r => r.Files), Is.EqualTo(new[] { elsewhere }));
        Assert.That(folders.ListAll(_folder.File("not-there")), Is.Empty);
    }

    /// <summary>
    /// The files of a recording without any of its audio are no recording to list: an upload copy, a transcript and marks
    /// alone, as a transcribed file leaves them, list nothing, and their folder has no newest recording, although each of
    /// those files names one.
    /// </summary>
    [Test]
    public void Files_of_a_recording_without_its_audio_are_not_listed_as_one()
    {
        var files = RecordingFiles.ForNew(_folder.File("Lecture 5"), "Lecture 5", Now);
        Directory.CreateDirectory(files.Folder);
        TestAudio.WriteWav(files.Upload, 16000, new short[1600]);
        File.WriteAllText(files.Transcript(), "# Lecture 5");
        File.WriteAllLines(files.Marks, ["00:00:01"]);

        Assert.That(Folders().ListRecent(9), Is.Empty);
        Assert.That(Folders().ListAll(), Is.Empty);
        Assert.That(ClassFolders.FindNewest(files.Folder), Is.Null);
    }
}
