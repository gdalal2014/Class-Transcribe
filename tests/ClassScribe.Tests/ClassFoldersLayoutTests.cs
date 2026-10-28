using System.Runtime.Versioning;
using ClassScribe.Audio;
using ClassScribe.Settings;
using ClassScribe.Storage;
using Moq;

namespace ClassScribe.Tests;

/// <summary>
/// The course and date layout: a new recording goes to a folder of its name, the course, and below it to a folder of the
/// local date it started on, such as Mantiq\2026-10-17, with "(2)", "(3)" and so on for a second and third class of the
/// name that day. A recording made before, flat in the folder of its name, is still found everywhere it was.
/// </summary>
[TestFixture]
public class ClassFoldersLayoutTests
{
    /// <summary>When today's Mantiq class started: 14:04:42 UTC, 10:04:42 in US Eastern time on 17 October 2026.</summary>
    private static readonly DateTimeOffset MantiqStart = new(2026, 10, 17, 14, 4, 42, TimeSpan.Zero);

    /// <summary>A time zone three hours ahead of UTC, as in Riyadh or Istanbul, where 22:30 UTC is 01:30 the next day.</summary>
    private static readonly TimeZoneInfo ThreeHoursAhead =
        TimeZoneInfo.CreateCustomTimeZone("ClassScribe test UTC+3", TimeSpan.FromHours(3), "UTC+3 (test)", "UTC+3 (test)");

    private TempFolder _folder = null!;
    private ManualTimeProvider _time = null!;
    private RecordingLogger<ClassFolders> _log = null!;

    /// <summary>Creates a temporary recordings folder, a clock moved by hand at the start of today's class, and a log to inspect.</summary>
    [SetUp]
    public void SetUp()
    {
        _folder = new TempFolder();
        _time = new ManualTimeProvider(MantiqStart);
        _log = new RecordingLogger<ClassFolders>();
    }

    /// <summary>Deletes the temporary folder.</summary>
    [TearDown]
    public void TearDown() => _folder.Dispose();

    /// <summary>
    /// The folders under test, rooted in the temporary folder, on the test's clock read in <paramref name="zone"/>, US
    /// Eastern time in October unless another is given, so that the local date is the same on every machine.
    /// </summary>
    private ClassFolders Folders(TimeZoneInfo? zone = null, IAudioOnDrive? onDrive = null) =>
        new(TestOptions.Create(new RecordingOptions { OutputFolder = _folder.Path }), new ZonedClock(_time, zone ?? ZonedTimeProvider.EasternSummer.LocalTimeZone), _log, onDrive);

    /// <summary>The path of <paramref name="parts"/> under the temporary recordings folder.</summary>
    private string Under(params string[] parts) => Path.Combine([_folder.Path, .. parts]);

    /// <summary>
    /// Writes a recording of <paramref name="name"/> that started at <paramref name="started"/> into
    /// <paramref name="folder"/>, a folder under the recordings folder such as "Mantiq\2026-10-17" or, in the old layout,
    /// "Mantiq", its audio last written when it started, with a transcript when asked.
    /// </summary>
    private RecordingFiles WriteIn(string folder, string name, DateTimeOffset started, double seconds = 0.1, bool withTranscript = false)
    {
        var files = RecordingFiles.ForNew(Under(folder), name, started);
        Directory.CreateDirectory(files.Folder);
        TestAudio.WriteWav(files.Segment(1), 16000, new short[(int)(16000 * seconds)]);
        File.SetLastWriteTimeUtc(files.Segment(1), started.UtcDateTime);
        if (withTranscript) File.WriteAllText(files.Transcript(), "# Lecture");
        return files;
    }

    /// <summary>
    /// Writes a recording of <paramref name="name"/> in <paramref name="folder"/> that started an hour before the clock's
    /// time and stopped <paramref name="stoppedAgo"/> before it, as one a crash or Q left.
    /// </summary>
    private RecordingFiles WriteStoppedIn(string folder, string name, TimeSpan stoppedAgo)
    {
        var files = WriteIn(folder, name, _time.GetUtcNow().AddHours(-1), seconds: 1.0);
        File.SetLastWriteTimeUtc(files.Segment(1), (_time.GetUtcNow() - stoppedAgo).UtcDateTime);
        return files;
    }

    /// <summary>Holds a segment open for writing while letting others read it, as the recorder holds the segment it writes.</summary>
    private static FileStream HoldForWriting(string segment) => new(segment, FileMode.Open, FileAccess.Write, FileShare.Read);

    // ------------------------------------------------------------------ where a new recording goes

    /// <summary>
    /// A new recording goes to the folder of its name, the course, and below it to the folder of the local date it
    /// started on; its files keep today's names, the name and the UTC start time. Today's class, started at 10:04:42 in
    /// US Eastern time, goes to Mantiq\2026-10-17 as Mantiq_2026-10-17_14-04-42Z.
    /// </summary>
    [Test]
    public void A_recording_goes_to_its_course_and_the_local_date_it_started_on()
    {
        var files = Folders().CreateRecording("Mantiq");

        Assert.That(files.Folder, Is.EqualTo(Under("Mantiq", "2026-10-17")));
        Assert.That(files.BaseName, Is.EqualTo("Mantiq_2026-10-17_14-04-42Z"));
        Assert.That(files.StartedUtc, Is.EqualTo(MantiqStart));
        Assert.That(Directory.Exists(files.Folder), Is.True);
    }

    /// <summary>
    /// The date folder is the local date, while the stamp in the file name stays UTC: an evening class at 21:30 in US
    /// Eastern time, already 01:30 on the next day in UTC, goes to the folder of the evening's date.
    /// </summary>
    [Test]
    public void An_evening_class_goes_to_the_local_date_although_the_utc_date_is_the_next_day()
    {
        _time = new ManualTimeProvider(new DateTimeOffset(2026, 10, 18, 1, 30, 0, TimeSpan.Zero));

        var files = Folders().CreateRecording("Kalam");

        Assert.That(files.Folder, Is.EqualTo(Under("Kalam", "2026-10-17")));
        Assert.That(files.BaseName, Is.EqualTo("Kalam_2026-10-18_01-30-00Z"));
    }

    /// <summary>
    /// In a time zone ahead of UTC the local date can be the next day: 22:30 UTC is 01:30 on the 18th three hours ahead,
    /// so the recording goes to 2026-10-18, its stamp still 22:30 on the 17th.
    /// </summary>
    [Test]
    public void In_a_time_zone_ahead_of_utc_the_folder_is_the_local_date_there()
    {
        _time = new ManualTimeProvider(new DateTimeOffset(2026, 10, 17, 22, 30, 0, TimeSpan.Zero));

        var files = Folders(ThreeHoursAhead).CreateRecording("Kalam");

        Assert.That(files.Folder, Is.EqualTo(Under("Kalam", "2026-10-18")));
        Assert.That(files.BaseName, Is.EqualTo("Kalam_2026-10-17_22-30-00Z"));
    }

    /// <summary>
    /// The date is read at the start of each recording on the run's clock: one started a second before local midnight
    /// goes to that day, and one started two seconds later to the next day's folder, not to a "(2)" of the first.
    /// </summary>
    [Test]
    public void A_recording_started_just_after_local_midnight_goes_to_the_new_day()
    {
        _time = new ManualTimeProvider(new DateTimeOffset(2026, 10, 18, 3, 59, 59, TimeSpan.Zero));   // 23:59:59 in US Eastern time
        var folders = Folders();

        var before = folders.CreateRecording("Kalam");
        TestAudio.WriteWav(before.Segment(1), 16000, [1]);
        _time.Advance(TimeSpan.FromSeconds(2));
        var after = folders.CreateRecording("Kalam");

        Assert.That(before.Folder, Is.EqualTo(Under("Kalam", "2026-10-17")));
        Assert.That(after.Folder, Is.EqualTo(Under("Kalam", "2026-10-18")));
    }

    /// <summary>
    /// A second, separate class of the name on the same local date goes to "2026-10-17 (2)", a third to "(3)": a date
    /// folder that holds a recording's audio is taken. A class of another name that day has a folder of its own.
    /// </summary>
    [Test]
    public void A_second_and_third_class_of_the_name_that_day_get_folders_marked_2_and_3()
    {
        var folders = Folders();

        var first = folders.CreateRecording("Mantiq");
        TestAudio.WriteWav(first.Segment(1), 16000, [1]);
        _time.Advance(TimeSpan.FromHours(3));
        var second = folders.CreateRecording("Mantiq");
        TestAudio.WriteWav(second.Segment(1), 16000, [1]);
        _time.Advance(TimeSpan.FromHours(3));
        var third = folders.CreateRecording("Mantiq");
        var other = folders.CreateRecording("Kalam");

        Assert.That(new[] { first.Folder, second.Folder, third.Folder },
            Is.EqualTo(new[] { Under("Mantiq", "2026-10-17"), Under("Mantiq", "2026-10-17 (2)"), Under("Mantiq", "2026-10-17 (3)") }));
        Assert.That(third.BaseName, Is.EqualTo("Mantiq_2026-10-17_20-04-42Z"));
        Assert.That(other.Folder, Is.EqualTo(Under("Kalam", "2026-10-17")));
    }

    /// <summary>
    /// A date folder that holds only his own files, the class's PDFs and slides, and folders of his own, is no class of
    /// its own: the recording goes into it, and those files stay as they are.
    /// </summary>
    [Test]
    public void A_date_folder_holding_only_his_own_files_is_used()
    {
        var dated = Under("Mantiq", "2026-10-17");
        Directory.CreateDirectory(Path.Combine(dated, "Slides"));
        File.WriteAllBytes(Path.Combine(dated, "Al-Sullam_al-munawraq_translated.pdf"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(dated, "Class_1_slides.pdf"), [4]);
        File.WriteAllText(Path.Combine(dated, "notes.wav"), "not a recording's name");

        var files = Folders().CreateRecording("Mantiq");

        Assert.That(files.Folder, Is.EqualTo(dated));
        Assert.That(Directory.GetFiles(dated).Select(Path.GetFileName), Is.EquivalentTo(new[] { "Al-Sullam_al-munawraq_translated.pdf", "Class_1_slides.pdf", "notes.wav" }));
        Assert.That(File.ReadAllBytes(Path.Combine(dated, "Al-Sullam_al-munawraq_translated.pdf")), Is.EqualTo(new byte[] { 1, 2, 3 }));
    }

    /// <summary>
    /// A start that recorded nothing, a false start, leaves only its log, its glossary record (written as a run starts) and
    /// perhaps its marks in the date folder. That is no class (Gulzar decided this on 18 October): the day's real class goes
    /// into the same folder, not into "(2)", and the false start's files stay as they are beside it.
    /// </summary>
    [Test]
    public void A_date_folder_holding_only_the_log_and_marks_of_a_start_that_recorded_nothing_is_used()
    {
        var folders = Folders();
        var falseStart = folders.CreateRecording("Mantiq");
        File.WriteAllText(falseStart.Log, "a run that ended before any audio still leaves its log");
        File.WriteAllText(falseStart.Marks, "00:00:00.000 ~ a mark");
        File.WriteAllText(falseStart.Glossary, "{}");

        _time.Advance(TimeSpan.FromMinutes(5));
        var real = folders.CreateRecording("Mantiq");

        Assert.That(real.Folder, Is.EqualTo(falseStart.Folder));
        Assert.That(real.BaseName, Is.EqualTo("Mantiq_2026-10-17_14-09-42Z"));
        Assert.That(Directory.GetFiles(real.Folder).Select(Path.GetFileName),
            Is.EquivalentTo(new[] { "Mantiq_2026-10-17_14-04-42Z.log", "Mantiq_2026-10-17_14-04-42Z.marks.txt", "Mantiq_2026-10-17_14-04-42Z.glossary.json" }));
    }

    /// <summary>
    /// Each of a recording's audio, transcripts and ElevenLabs' answers makes its date folder a class of its own, so the
    /// next class of the name that day goes to "(2)": any track's segment, any transcript copy, the answer, and a paid
    /// answer kept unread.
    /// </summary>
    [TestCase(".wav")]
    [TestCase("_part2.wav")]
    [TestCase(".mic.wav")]
    [TestCase("_part3.mic.wav")]
    [TestCase(".md")]
    [TestCase(" (2).md")]
    [TestCase(".json")]
    [TestCase(" (2).json")]
    [TestCase(".unread.json")]
    [TestCase(".WAV")]
    public void A_recordings_audio_transcript_or_answer_takes_its_date_folder(string suffix)
    {
        var dated = Under("Mantiq", "2026-10-17");
        Directory.CreateDirectory(dated);
        File.WriteAllText(Path.Combine(dated, "Mantiq_2026-10-17_12-00-00Z.log"), "log");
        File.WriteAllText(Path.Combine(dated, "Mantiq_2026-10-17_12-00-00Z" + suffix), "kept");

        var files = Folders().CreateRecording("Mantiq");

        Assert.That(files.Folder, Is.EqualTo(Under("Mantiq", "2026-10-17 (2)")));
    }

    /// <summary>
    /// A class whose audio went up to Google Drive and was then freed from the laptop, with no transcript (a record-only
    /// class), leaves only its log and marks; its audio is on Drive, so its date folder is still its own, and the next
    /// class of the name that day goes to "(2)".
    /// </summary>
    [Test]
    public void A_date_folder_whose_class_has_its_audio_on_drive_alone_is_taken()
    {
        var dated = Under("Mantiq", "2026-10-17");
        Directory.CreateDirectory(dated);
        File.WriteAllText(Path.Combine(dated, "Mantiq_2026-10-17_12-00-00Z.log"), "log");
        File.WriteAllText(Path.Combine(dated, "Mantiq_2026-10-17_12-00-00Z.marks.txt"), "marks");
        var onDrive = new Mock<IAudioOnDrive>();
        onDrive.Setup(drive => drive.Find(It.Is<RecordingFiles>(files => files.BaseName == "Mantiq_2026-10-17_12-00-00Z")))
            .Returns(new AudioOnDrive(TimeSpan.FromHours(2), false, "My Drive > ClassScribe > Mantiq > 2026-10-17", null, true));

        var files = Folders(onDrive: onDrive.Object).CreateRecording("Mantiq");

        Assert.That(files.Folder, Is.EqualTo(Under("Mantiq", "2026-10-17 (2)")));
    }

    /// <summary>
    /// An empty date folder, as one left by a run that ended before anything was written, is used again rather than
    /// passed over for "(2)".
    /// </summary>
    [Test]
    public void An_empty_date_folder_is_used_again()
    {
        var folders = Folders();
        var unused = folders.CreateRecording("Mantiq");

        _time.Advance(TimeSpan.FromMinutes(1));
        var next = folders.CreateRecording("Mantiq");

        Assert.That(next.Folder, Is.EqualTo(unused.Folder));
        Assert.That(next.BaseName, Is.EqualTo("Mantiq_2026-10-17_14-05-42Z"));
    }

    /// <summary>
    /// A name that cannot be a date folder now, as a file named 2026-10-17, is passed over for "(2)": the recording still
    /// gets a folder it can write into.
    /// </summary>
    [Test]
    public void A_file_where_the_date_folder_would_go_is_passed_over()
    {
        Directory.CreateDirectory(Under("Mantiq"));
        File.WriteAllText(Under("Mantiq", "2026-10-17"), "a file, not a folder");

        var files = Folders().CreateRecording("Mantiq");

        Assert.That(files.Folder, Is.EqualTo(Under("Mantiq", "2026-10-17 (2)")));
        Assert.That(Directory.Exists(files.Folder), Is.True);
    }

    /// <summary>
    /// A date folder ClassScribe may not look into, as one copied from another account, is passed over for "(2)" rather
    /// than costing the class, with a line that says why.
    /// </summary>
    [Test, Platform("Win")]
    [SupportedOSPlatform("windows")]
    public void A_date_folder_that_cannot_be_looked_into_is_passed_over()
    {
        var shut = Under("Mantiq", "2026-10-17");
        Directory.CreateDirectory(shut);
        using var denied = Denied.ListingOf(shut);
        Assume.That(() => Directory.GetFiles(shut), Throws.InstanceOf<UnauthorizedAccessException>(), "the folder could not be shut to this user");

        var files = Folders().CreateRecording("Mantiq");

        Assert.That(files.Folder, Is.EqualTo(Under("Mantiq", "2026-10-17 (2)")));
        Assert.That(_log.Messages, Has.Some.Contains(shut));
    }

    /// <summary>
    /// A date folder of the course that leads nowhere, as a link to a drive that is gone, does not keep a class from
    /// starting: the new recording goes to today's date folder, and the list leaves the broken one out with a warning.
    /// </summary>
    [Test, Platform("Win")]
    [SupportedOSPlatform("windows")]
    public void A_date_folder_that_leads_nowhere_does_not_keep_a_class_from_starting()
    {
        Directory.CreateDirectory(Under("Mantiq"));
        var link = Under("Mantiq", "2026-10-16");
        MakeJunction(link, Under("unplugged"));
        try
        {
            Assume.That(new DirectoryInfo(link).LinkTarget, Is.Not.Null, "mklink could not make the junction");
            var folders = Folders();

            var files = folders.CreateRecording("Mantiq");
            TestAudio.WriteWav(files.Segment(1), 16000, [1]);

            Assert.That(files.Folder, Is.EqualTo(Under("Mantiq", "2026-10-17")));
            Assert.That(folders.ListRecent(9).Select(r => r.Files), Is.EqualTo(new[] { files }));
            Assert.That(_log.Problems, Has.One.Contains(link));
        }
        finally
        {
            // Removes the junction itself; what it points to does not exist.
            if (new DirectoryInfo(link).LinkTarget is not null) Directory.Delete(link);
        }
    }

    /// <summary>
    /// Makes <paramref name="link"/> a junction to <paramref name="target"/>, as mklink /J does; unlike a symbolic link, a
    /// junction needs no administrator rights. Only the test's own folders are linked, and a hidden cmd.exe that ends at once
    /// makes the link.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static void MakeJunction(string link, string target)
    {
        var start = new System.Diagnostics.ProcessStartInfo("cmd.exe") { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
        foreach (var argument in new[] { "/c", "mklink", "/J", link, target }) start.ArgumentList.Add(argument);

        using var mklink = System.Diagnostics.Process.Start(start)!;
        mklink.StandardOutput.ReadToEnd();
        mklink.WaitForExit();
    }

    /// <summary>
    /// A recording started within the same second as another of its course takes the next second, so that no two
    /// recordings of a course share a base name, even in two date folders.
    /// </summary>
    [Test]
    public void A_recording_started_within_the_same_second_as_another_takes_the_next_second()
    {
        var folders = Folders();

        var first = folders.CreateRecording("Mantiq");
        TestAudio.WriteWav(first.Segment(1), 16000, [1]);
        var second = folders.CreateRecording("Mantiq");

        Assert.That(second.Folder, Is.EqualTo(Under("Mantiq", "2026-10-17 (2)")));
        Assert.That(second.BaseName, Is.EqualTo("Mantiq_2026-10-17_14-04-43Z"));
    }

    /// <summary>
    /// A false start leaves its date folder free, so the class started again within the same second goes into that same
    /// folder; it still takes the next second, so that it never writes into the false start's log.
    /// </summary>
    [Test]
    public void A_class_started_within_the_same_second_as_a_false_start_shares_its_folder_but_not_its_name()
    {
        var folders = Folders();

        var falseStart = folders.CreateRecording("Mantiq");
        File.WriteAllText(falseStart.Log, "the false start's log");
        var real = folders.CreateRecording("Mantiq");

        Assert.That(real.Folder, Is.EqualTo(falseStart.Folder));
        Assert.That(real.BaseName, Is.EqualTo("Mantiq_2026-10-17_14-04-43Z"));
        Assert.That(File.ReadAllText(falseStart.Log), Is.EqualTo("the false start's log"));
    }

    /// <summary>A recording made with a recordings folder given for its run (--output) goes to the course and date folders there.</summary>
    [Test]
    public void The_same_layout_is_used_under_a_recordings_folder_given_for_the_run()
    {
        var elsewhere = Under("Elsewhere");

        var files = Folders().CreateRecording("Mantiq", elsewhere);

        Assert.That(files.Folder, Is.EqualTo(Path.Combine(elsewhere, "Mantiq", "2026-10-17")));
    }

    /// <summary>
    /// A file transcribed on its own gets the same layout, under a folder named after it and the date of the
    /// transcription; a second file of the name that day goes to "(2)", since the first one's transcript is there.
    /// </summary>
    [Test]
    public void A_transcribed_file_gets_a_folder_of_its_name_and_the_date_it_was_transcribed()
    {
        var folders = Folders();

        var first = folders.CreateForFile(@"D:\Downloads\Lecture 5.mp4");
        File.WriteAllText(first.Transcript(), "# Lecture 5");
        File.WriteAllText(first.Json(), "{}");
        _time.Advance(TimeSpan.FromHours(1));
        var second = folders.CreateForFile(@"D:\Other\Lecture 5.mp3");

        Assert.That(first.Folder, Is.EqualTo(Under("Lecture 5", "2026-10-17")));
        Assert.That(second.Folder, Is.EqualTo(Under("Lecture 5", "2026-10-17 (2)")));
    }

    // ------------------------------------------------------------------ the date folders' names

    /// <summary>A date folder's name is the date as yyyy-MM-dd, and from the second class of the day " (N)" after it.</summary>
    [Test]
    public void A_date_folders_name_is_the_date_with_a_number_from_the_second()
    {
        Assert.That(FolderNames.DateFolderName(new DateOnly(2026, 10, 17)), Is.EqualTo("2026-10-17"));
        Assert.That(FolderNames.DateFolderName(new DateOnly(2026, 1, 5), 2), Is.EqualTo("2026-01-05 (2)"));
    }

    /// <summary>
    /// Only a folder named as a date, yyyy-MM-dd of a real day, alone or with " (N)", is a date folder; anything else
    /// below a course, such as Slides, a date in another form, or the old layout's name with a date in it, is not.
    /// </summary>
    [TestCase("2026-10-17", true)]
    [TestCase("2026-10-17 (2)", true)]
    [TestCase("2026-10-17 (12)", true)]
    [TestCase("2024-02-29", true)]
    [TestCase("2026-02-30", false)]
    [TestCase("2026-13-01", false)]
    [TestCase("2026-10-17(2)", false)]
    [TestCase("2026-10-17 (x)", false)]
    [TestCase("2026-10-17 copy", false)]
    [TestCase("17-10-2026", false)]
    [TestCase("Slides", false)]
    [TestCase("mic-check", false)]
    [TestCase("Kalam_10_15_2026", false)]
    [TestCase("", false)]
    public void Only_a_folder_named_as_a_date_is_a_date_folder(string name, bool dated) =>
        Assert.That(FolderNames.IsDateFolder(name), Is.EqualTo(dated));

    /// <summary>
    /// A later class of its day is told by its date folder's number, which the list shows; the day's first class, a
    /// recording of the old layout and any other folder have none.
    /// </summary>
    [TestCase(@"C:\Classes\Mantiq\2026-10-17 (2)", "2026-10-17 (2)")]
    [TestCase(@"C:\Classes\Mantiq\2026-10-17 (3)\", "2026-10-17 (3)")]
    [TestCase(@"C:\Classes\Mantiq\2026-10-17", null)]
    [TestCase(@"C:\Classes\Mantiq", null)]
    [TestCase(@"C:\Classes\Mantiq\Slides (2)", null)]
    public void A_later_class_of_its_day_is_told_by_its_date_folders_number(string folder, string? shown) =>
        Assert.That(FolderNames.LaterClassOfItsDay(folder), Is.EqualTo(shown));

    /// <summary>
    /// The folders a folder's recordings may be in: the folder itself, for recordings flat in it as the old layout put
    /// them, then its date folders by name; its other folders, such as Slides, are passed over.
    /// </summary>
    [Test]
    public void The_folders_looked_into_are_the_folder_and_its_date_folders()
    {
        foreach (var folder in new[] { "2026-10-17 (2)", "2026-10-15", "Slides", "2026-10-17", "Old 2026-10-01" })
            Directory.CreateDirectory(Under("Mantiq", folder));

        Assert.That(ClassFolders.RecordingFoldersIn(Under("Mantiq")),
            Is.EqualTo(new[] { Under("Mantiq"), Under("Mantiq", "2026-10-15"), Under("Mantiq", "2026-10-17"), Under("Mantiq", "2026-10-17 (2)") }));
    }

    /// <summary>
    /// Google Drive mirrors the laptop: a recording in a date folder goes under its course and that date folder, "(2)"
    /// included, and one still flat in the folder of its name under that name alone, whatever the recordings folder.
    /// </summary>
    [TestCase(@"C:\Users\gdala\ClassScribe\Mantiq\2026-10-17", new[] { "Mantiq", "2026-10-17" })]
    [TestCase(@"D:\Classes\Mantiq\2026-10-17 (2)\", new[] { "Mantiq", "2026-10-17 (2)" })]
    [TestCase(@"C:\Users\gdala\ClassScribe\Kalam", new[] { "Kalam" })]
    [TestCase(@"D:\Classes\Lecture 5", new[] { "Lecture 5" })]
    public void Drive_mirrors_the_course_and_date_folders(string folder, string[] levels) =>
        Assert.That(ClassFolders.DriveLevelsOf(folder), Is.EqualTo(levels));

    // ------------------------------------------------------------------ both layouts are read

    /// <summary>
    /// The list holds the recordings of both layouts, the new ones in their date folders and the old ones flat in the
    /// folder of their name. Files that are not a recording's, as his PDFs, and folders that are not date folders, as
    /// Slides, even one holding a file named like a recording, are passed over; so is the microphone check's folder.
    /// </summary>
    [Test]
    public void Recordings_of_both_layouts_are_listed_and_other_folders_are_passed_over()
    {
        var old = WriteIn("Fiqh", "Fiqh", MantiqStart.AddHours(-1));
        var flat = WriteIn("Kalam", "Kalam", MantiqStart.AddDays(-2));
        var dated = WriteIn(Path.Combine("Mantiq", "2026-10-17"), "Mantiq", MantiqStart);
        var second = WriteIn(Path.Combine("Mantiq", "2026-10-17 (2)"), "Mantiq", MantiqStart.AddHours(3));
        File.WriteAllBytes(Under("Mantiq", "2026-10-17", "Class_1_slides.pdf"), [1]);
        File.WriteAllBytes(Under("Mantiq", "reading.pdf"), [1]);
        WriteIn(Path.Combine("Mantiq", "Slides"), "Mantiq", MantiqStart.AddHours(5));
        Directory.CreateDirectory(Under("mic-check"));
        File.WriteAllText(Under("mic-check", "mic-check_2026-10-17_19-00-12.log"), "MIC");

        var folders = Folders();
        var recent = folders.ListRecent(9);
        var all = folders.ListAll();

        Assert.That(recent.Select(r => r.Files), Is.EqualTo(new[] { second, dated, old, flat }));
        Assert.That(all.Select(r => r.Files), Is.EqualTo(new[] { second, dated, old, flat }));
        Assert.That(_log.Problems, Is.Empty);
    }

    /// <summary>
    /// One date folder ClassScribe may not look into does not hide the course's other recordings: it is passed over
    /// quietly, as a course folder of that kind is.
    /// </summary>
    [Test, Platform("Win")]
    [SupportedOSPlatform("windows")]
    public void A_date_folder_that_cannot_be_looked_into_is_passed_over_in_the_list()
    {
        var listed = WriteIn(Path.Combine("Mantiq", "2026-10-17"), "Mantiq", MantiqStart);
        var shut = WriteIn(Path.Combine("Mantiq", "2026-10-16"), "Mantiq", MantiqStart.AddDays(-1)).Folder;
        using var denied = Denied.ListingOf(shut);
        Assume.That(() => Directory.GetFiles(shut), Throws.InstanceOf<UnauthorizedAccessException>(), "the folder could not be shut to this user");

        Assert.That(Folders().ListRecent(9).Select(r => r.Files), Is.EqualTo(new[] { listed }));
        Assert.That(_log.Problems, Is.Empty);
    }

    /// <summary>
    /// A course folder stands for its recordings in both layouts, its date folders' and its old flat ones: the newest is
    /// the one started last, and the rest are listed after it. A date folder stands for its own, and Slides for none.
    /// </summary>
    [Test]
    public void A_course_folder_holds_the_recordings_of_its_date_folders_and_its_flat_ones()
    {
        var flat = WriteIn("Mantiq", "Mantiq", MantiqStart.AddDays(-7), withTranscript: true);
        var lastWeek = WriteIn(Path.Combine("Mantiq", "2026-10-10"), "Mantiq", MantiqStart.AddDays(-7).AddHours(1));
        var today = WriteIn(Path.Combine("Mantiq", "2026-10-17"), "Mantiq", MantiqStart);
        WriteIn(Path.Combine("Mantiq", "Slides"), "Mantiq", MantiqStart.AddHours(1));
        var folders = Folders();

        Assert.That(ClassFolders.FindNewest(Under("Mantiq")), Is.EqualTo(today));
        Assert.That(folders.RecordingsIn(Under("Mantiq")).Select(r => r.Files), Is.EqualTo(new[] { today, lastWeek, flat }));
        Assert.That(ClassFolders.FindNewest(lastWeek.Folder), Is.EqualTo(lastWeek));
        Assert.That(folders.RecordingsIn(lastWeek.Folder).Select(r => r.Files), Is.EqualTo(new[] { lastWeek }));
        Assert.That(ClassFolders.FindNewest(Under("Mantiq", "Slides")), Is.Not.Null, "a folder given by itself is looked into");
        Assert.That(ClassFolders.FindNewest(_folder.Path), Is.Null, "the recordings folder holds courses, not recordings");
    }

    /// <summary>A recording in a date folder is being written while a recorder holds its newest segment, as one in the old layout is.</summary>
    [Test]
    public void A_recording_in_a_date_folder_is_being_written_while_its_newest_segment_is_held()
    {
        var files = WriteIn(Path.Combine("Mantiq", "2026-10-17"), "Mantiq", MantiqStart);

        bool whileHeld;
        IReadOnlyList<RecordedClass> listed;
        using (HoldForWriting(files.Segment(1)))
        {
            whileHeld = ClassFolders.IsBeingWritten(files);
            listed = Folders().ListRecent(9);
        }

        Assert.That(whileHeld, Is.True);
        Assert.That(listed.Single().BeingWritten, Is.True);
        Assert.That(ClassFolders.IsBeingWritten(files), Is.False);
    }

    /// <summary>
    /// The answer kept for a recording in a date folder is found beside it, or in the recordings folder when its own
    /// folder would not take it, as for a recording of the old layout.
    /// </summary>
    [Test]
    public void The_answer_kept_for_a_recording_in_a_date_folder_is_found_beside_it_or_in_the_recordings_folder()
    {
        var beside = WriteIn(Path.Combine("Mantiq", "2026-10-17"), "Mantiq", MantiqStart);
        var inTheRoot = WriteIn(Path.Combine("Kalam", "2026-10-16"), "Kalam", MantiqStart.AddDays(-1));
        File.WriteAllText(beside.UnreadAnswer(), "{}");
        var keptInTheRoot = (inTheRoot with { Folder = _folder.Path }).UnreadAnswer();
        File.WriteAllText(keptInTheRoot, "{}");
        var folders = Folders();

        Assert.That(folders.KeptAnswerOf(beside), Is.EqualTo(beside.UnreadAnswer()));
        Assert.That(folders.KeptAnswerOf(inTheRoot), Is.EqualTo(keptInTheRoot));
        Assert.That(folders.ListRecent(9).Select(r => r.KeptAnswer), Is.EqualTo(new[] { beside.UnreadAnswer(), keptInTheRoot }));
    }

    // ------------------------------------------------------------------ continuing

    /// <summary>
    /// A recording to continue is looked for among the recordings of its name in both layouts: the newest of them, here
    /// one in a date folder after one flat in the course folder, is offered.
    /// </summary>
    [Test]
    public void The_newest_recording_of_the_name_in_either_layout_is_offered_to_be_continued()
    {
        WriteStoppedIn("Mantiq", "Mantiq", TimeSpan.FromMinutes(50));
        _time.Advance(TimeSpan.FromMinutes(1));
        var dated = WriteStoppedIn(Path.Combine("Mantiq", "2026-10-17"), "Mantiq", TimeSpan.FromMinutes(4));
        var folders = Folders();

        Assert.That(folders.FindContinuable("Mantiq", RecordingMode.Class, TimeSpan.FromMinutes(60))?.Files, Is.EqualTo(dated));

        File.WriteAllText(dated.Transcript(), "# Mantiq");
        Assert.That(folders.FindContinuable("Mantiq", RecordingMode.Class, TimeSpan.FromMinutes(60)), Is.Null, "only the newest is ever offered");
    }

    /// <summary>A recording of the old layout, flat in the folder of its name, is still offered to be continued when it is the newest.</summary>
    [Test]
    public void A_recording_of_the_old_layout_is_still_offered_to_be_continued()
    {
        var flat = WriteStoppedIn("Kalam", "Kalam", TimeSpan.FromMinutes(4));
        Directory.CreateDirectory(Under("Kalam", "Slides"));

        Assert.That(Folders().FindContinuable("Kalam", RecordingMode.Class, TimeSpan.FromMinutes(60))?.Files, Is.EqualTo(flat));
    }

    /// <summary>
    /// A run that continues a recording finds it by its name and base name in whichever layout it was made, even on
    /// another day, so that it keeps writing beside it: in its date folder, "(2)" included, or flat in the folder of its
    /// name; the run's name may differ from the folder's in letter case, as Windows takes the two for one. Another
    /// recording of the course, in another date folder, is not taken for it.
    /// </summary>
    [TestCase("Mantiq", @"Mantiq\2026-10-16 (2)")]
    [TestCase("Mantiq", "Mantiq")]
    [TestCase("mantiq", @"Mantiq\2026-10-16")]
    public void A_recording_to_continue_is_found_in_its_own_folder_in_either_layout(string requestName, string folder)
    {
        WriteIn(Path.Combine("Mantiq", "2026-10-15"), "Mantiq", MantiqStart.AddDays(-2));
        var files = WriteIn(folder, "Mantiq", MantiqStart.AddDays(-1));

        var opened = Folders().OpenToContinue(_folder.Path, requestName, files.BaseName);

        Assert.That(opened, Is.EqualTo(files));
    }

    /// <summary>
    /// A recording to continue whose audio is nowhere under its course any more is an error that names it, so the run
    /// records a new recording instead; a base name that is not a recording's is refused before any folder is looked into.
    /// </summary>
    [Test]
    public void A_recording_to_continue_that_is_gone_is_an_error_that_names_it()
    {
        WriteIn(Path.Combine("Mantiq", "2026-10-17"), "Mantiq", MantiqStart);
        var folders = Folders();

        var gone = Assert.Throws<FileNotFoundException>(() => folders.OpenToContinue(_folder.Path, "Mantiq", "Mantiq_2026-10-15_14-00-00Z"));
        var noCourse = Assert.Throws<FileNotFoundException>(() => folders.OpenToContinue(_folder.Path, "Fiqh", "Fiqh_2026-10-15_14-00-00Z"));

        Assert.That(gone!.Message, Does.Contain("Mantiq_2026-10-15_14-00-00Z"));
        Assert.That(noCourse!.Message, Does.Contain("Fiqh_2026-10-15_14-00-00Z"));
        Assert.Throws<ArgumentException>(() => folders.OpenToContinue(_folder.Path, "Mantiq", @"2026-10-17\Mantiq_2026-10-17_14-04-42Z"));
    }
}
