using ClassScribe.Audio;
using ClassScribe.Settings;
using ClassScribe.Storage;
using ClassScribe.YouTube;

namespace ClassScribe.Tests;

/// <summary>
/// Videos among the course's recordings (design 2.3 to 2.5): a batch's videos share one date folder, the day's class after
/// it takes the next; a video whose fetching failed leaves only traces, which hold no date folder; a video's recording is
/// known by its source file, never offered to be continued as a class, and listed with its title.
/// </summary>
[TestFixture]
public class ClassFoldersVideoTests
{
    /// <summary>When the batch started: 14:02:11 UTC, 10:02:11 in US Eastern time on 18 October 2026.</summary>
    private static readonly DateTimeOffset BatchStart = new(2026, 10, 18, 14, 2, 11, TimeSpan.Zero);

    private TempFolder _folder = null!;
    private ManualTimeProvider _time = null!;
    private RecordingLogger<ClassFolders> _log = null!;

    /// <summary>A temporary recordings folder, a clock at the batch's start, and a log to inspect.</summary>
    [SetUp]
    public void SetUp()
    {
        _folder = new TempFolder();
        _time = new ManualTimeProvider(BatchStart);
        _log = new RecordingLogger<ClassFolders>();
    }

    /// <summary>Deletes the temporary folder.</summary>
    [TearDown]
    public void TearDown() => _folder.Dispose();

    /// <summary>The folders under test, in the temporary folder, on the test's clock in US Eastern time.</summary>
    private ClassFolders Folders() =>
        new(TestOptions.Create(new RecordingOptions { OutputFolder = _folder.Path }), new ZonedClock(_time, ZonedTimeProvider.EasternSummer.LocalTimeZone), _log);

    /// <summary>The path of <paramref name="parts"/> under the temporary recordings folder.</summary>
    private string Under(params string[] parts) => Path.Combine([_folder.Path, .. parts]);

    /// <summary>Writes a second of audio for <paramref name="files"/>, last written <paramref name="ago"/> before the clock's time.</summary>
    private void WriteAudio(RecordingFiles files, TimeSpan ago)
    {
        Directory.CreateDirectory(files.Folder);
        TestAudio.WriteWav(files.Segment(1), 16000, new short[16000]);
        File.SetLastWriteTimeUtc(files.Segment(1), (_time.GetUtcNow() - ago).UtcDateTime);
    }

    /// <summary>Makes <paramref name="files"/> a video's recording: its source file, for the video <paramref name="title"/>.</summary>
    private static void MakeVideo(RecordingFiles files, string title = "The creed, part 1") =>
        VideoSourceFile.Write(files.Source, VideoSourceFileTests.Downloaded(title: title));

    /// <summary>
    /// The first video of a batch goes where a class's recording goes, and every later one into that same date folder, with
    /// the next base name, so that a batch of thirty never pushes the day's class to a thirty-first folder.
    /// </summary>
    [Test]
    public void A_batchs_videos_share_one_date_folder()
    {
        var folders = Folders();
        var first = folders.CreateRecording("Risala");
        WriteAudio(first, TimeSpan.Zero);
        MakeVideo(first);
        _time.Advance(TimeSpan.FromSeconds(89));

        var second = folders.CreateRecordingIn(first.Folder, "Risala");

        Assert.That(first.Folder, Is.EqualTo(Under("Risala", "2026-10-18")));
        Assert.That(second.Folder, Is.EqualTo(first.Folder));
        Assert.That(second.BaseName, Is.EqualTo("Risala_2026-10-18_14-03-40Z"));
    }

    /// <summary>Two videos made in the same second, in the batch's folder, never share a base name: the second takes the next second.</summary>
    [Test]
    public void Two_videos_in_one_second_take_two_base_names()
    {
        var folders = Folders();
        var first = folders.CreateRecording("Risala");
        WriteAudio(first, TimeSpan.Zero);

        var second = folders.CreateRecordingIn(first.Folder, "Risala");

        Assert.That(second.BaseName, Is.EqualTo("Risala_2026-10-18_14-02-12Z"));
    }

    /// <summary>The day's class after a batch takes the next date folder, as after any class: the batch's folder holds recordings.</summary>
    [Test]
    public void The_days_class_after_a_batch_takes_the_next_date_folder()
    {
        var folders = Folders();
        var video = folders.CreateRecording("Risala");
        WriteAudio(video, TimeSpan.Zero);
        MakeVideo(video);
        _time.Advance(TimeSpan.FromHours(4));

        var lesson = folders.CreateRecording("Risala");

        Assert.That(lesson.Folder, Is.EqualTo(Under("Risala", "2026-10-18 (2)")));
    }

    /// <summary>
    /// A video whose fetching failed before any audio leaves its source file and its log, traces that hold no date folder,
    /// so the day's class takes the date's own folder, as after a class's false start.
    /// </summary>
    [Test]
    public void A_video_that_failed_before_any_audio_holds_no_date_folder()
    {
        var folders = Folders();
        var failed = folders.CreateRecording("Risala");
        MakeVideo(failed);
        File.WriteAllText(failed.Log, "yt-dlp could not get this video's audio");
        _time.Advance(TimeSpan.FromHours(4));

        var lesson = folders.CreateRecording("Risala");

        Assert.That(lesson.Folder, Is.EqualTo(failed.Folder));
    }

    /// <summary>A recording with a source file is a video's; one without is a class's.</summary>
    [Test]
    public void A_recording_with_a_source_file_is_a_videos()
    {
        var folders = Folders();
        var lesson = folders.CreateRecording("Risala");
        WriteAudio(lesson, TimeSpan.Zero);
        var video = folders.CreateRecordingIn(lesson.Folder, "Risala");
        WriteAudio(video, TimeSpan.Zero);
        MakeVideo(video);

        Assert.That((ClassFolders.IsVideo(lesson), ClassFolders.IsVideo(video)), Is.EqualTo((false, true)));
    }

    /// <summary>
    /// A video recorded ten minutes before a class is never offered for the class to continue: the newest class of the name
    /// is offered instead, when it may be, and with none, nothing is.
    /// </summary>
    [Test]
    public void A_video_is_never_offered_to_be_continued()
    {
        var folders = Folders();
        var lesson = folders.CreateRecording("Risala");
        WriteAudio(lesson, TimeSpan.FromMinutes(20));
        _time.Advance(TimeSpan.FromSeconds(5));
        var video = folders.CreateRecordingIn(lesson.Folder, "Risala");
        WriteAudio(video, TimeSpan.FromMinutes(10));
        MakeVideo(video);

        var offered = folders.FindContinuable("Risala", RecordingMode.Class, TimeSpan.FromMinutes(60));

        Assert.That(offered?.Files, Is.EqualTo(lesson));

        File.Delete(lesson.Segment(1));
        Assert.That(folders.FindContinuable("Risala", RecordingMode.Class, TimeSpan.FromMinutes(60)), Is.Null);
    }

    /// <summary>A video's recording is listed with its source, which the list shows as its title.</summary>
    [Test]
    public void A_videos_recording_is_listed_with_its_source()
    {
        var folders = Folders();
        var video = folders.CreateRecording("Risala");
        WriteAudio(video, TimeSpan.Zero);
        MakeVideo(video, title: "The creed, part 2");

        var listed = folders.ListAll().Single();

        Assert.That(listed.Video?.Title, Is.EqualTo("The creed, part 2"));
        Assert.That(folders.Describe(video).Video?.Obtained, Is.EqualTo(VideoSource.Downloaded));
    }

    /// <summary>A source file that cannot be read leaves the recording listed without its title, with a warning that names the file.</summary>
    [Test]
    public void A_source_file_that_cannot_be_read_leaves_the_recording_listed_without_a_title()
    {
        var folders = Folders();
        var video = folders.CreateRecording("Risala");
        WriteAudio(video, TimeSpan.Zero);
        File.WriteAllText(video.Source, "not json");

        var listed = folders.ListAll().Single();

        Assert.That(listed.Video, Is.Null);
        Assert.That(_log.Problems.Single(), Does.Contain(video.Source));
    }
}
