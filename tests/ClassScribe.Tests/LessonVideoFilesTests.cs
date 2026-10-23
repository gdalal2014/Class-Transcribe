using ClassScribe.Settings;
using ClassScribe.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClassScribe.Tests;

/// <summary>
/// The lesson's finished video among a recording's files (teach-video 6.9 and 6.15, slice 8): <c>&lt;base&gt;.mp4</c> and its
/// <c>.mp4.partial</c> are files of the recording; whether the video is made is decided from the files alone
/// (<see cref="LessonVideoState"/>), never counting a piece or a <c>.partial</c> held open for writing; a <c>.partial</c> held
/// open holds the recording, and the finished video is a sign of a class.
/// </summary>
[TestFixture]
public sealed class LessonVideoFilesTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 18, 30, 0, TimeSpan.Zero);

    private TempFolder _folder = null!;

    /// <summary>A temporary recordings folder.</summary>
    [SetUp]
    public void SetUp() => _folder = new TempFolder();

    /// <summary>Deletes the temporary folder.</summary>
    [TearDown]
    public void TearDown() => _folder.Dispose();

    /// <summary>The folders under test, rooted in the temporary folder.</summary>
    private ClassFolders Folders() =>
        new(TestOptions.Create(new RecordingOptions { OutputFolder = _folder.Path }), new ZonedClock(new ManualTimeProvider(Now), ZonedTimeProvider.EasternSummer.LocalTimeZone),
            NullLogger<ClassFolders>.Instance);

    /// <summary>A lesson of Tajwid that started an hour ago, with its sound.</summary>
    private RecordingFiles WriteLesson()
    {
        var files = RecordingFiles.ForNew(_folder.File(@"Tajwid\2026-10-14"), "Tajwid", Now.AddHours(-1));
        Directory.CreateDirectory(files.Folder);
        TestAudio.WriteWav(files.Segment(1), 16000, new short[16000]);
        return files;
    }

    /// <summary>Writes a file at <paramref name="path"/> last written at <paramref name="at"/>, and returns the path.</summary>
    private static string Write(string path, DateTimeOffset at)
    {
        File.WriteAllBytes(path, [0, 0, 0, 24, (byte)'f', (byte)'t', (byte)'y', (byte)'p']);
        File.SetLastWriteTimeUtc(path, at.UtcDateTime);
        return path;
    }

    /// <summary>The finished video and its unfinished copy are named after the recording, beside its other files.</summary>
    [Test]
    public void The_lesson_video_is_named_after_the_recording()
    {
        var files = new RecordingFiles(@"C:\r", "Tajwid 4_2026-10-19_14-00-02Z");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(files.LessonVideo, Is.EqualTo(@"C:\r\Tajwid 4_2026-10-19_14-00-02Z.mp4"));
            Assert.That(files.LessonVideoPartial, Is.EqualTo(@"C:\r\Tajwid 4_2026-10-19_14-00-02Z.mp4.partial"));
        }
    }

    /// <summary>Both are files of their recording, by their names, and only the finished one is the lesson's video; a piece is neither.</summary>
    [TestCase("Tajwid_2026-10-14_17-30-00Z.mp4", true, true, false)]
    [TestCase("Tajwid_2026-10-14_17-30-00Z.MP4", true, true, false)]
    [TestCase("Tajwid_2026-10-14_17-30-00Z.mp4.partial", true, false, true)]
    [TestCase("Tajwid_2026-10-14_17-30-00Z.cam.mp4", true, false, false)]
    [TestCase("Tajwid_2026-10-14_17-30-00Z.cam.mp4.partial", false, false, false)]
    [TestCase("Tajwid_2026-10-14_17-30-00Z (2).mp4", false, false, false)]
    [TestCase("Tajwid.mp4", false, false, false)]
    public void The_lesson_video_is_told_by_its_name(string name, bool ofTheRecording, bool isVideo, bool isUnfinished)
    {
        var path = Path.Combine(_folder.Path, name);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(RecordingFiles.TryParse(path)?.BaseName, Is.EqualTo(ofTheRecording ? "Tajwid_2026-10-14_17-30-00Z" : null));
            Assert.That(RecordingFiles.IsLessonVideo(path), Is.EqualTo(isVideo));
            Assert.That(RecordingFiles.IsUnfinishedLessonVideo(path), Is.EqualTo(isUnfinished));
        }
    }

    /// <summary>A recording without pieces and without a video has no lesson video to speak of.</summary>
    [Test]
    public void A_recording_without_the_camera_has_no_lesson_video()
    {
        var files = WriteLesson();

        Assert.That(ClassFolders.LessonVideoStateOf(files), Is.EqualTo(LessonVideoState.None));
    }

    /// <summary>Pieces without a video are a lesson whose video is not made yet; with a video newer than every piece, it is made.</summary>
    [Test]
    public void Pieces_without_a_video_are_not_made_and_with_one_newer_they_are()
    {
        var files = WriteLesson();
        Write(files.CameraPiece(TimeSpan.Zero), Now.AddMinutes(-10));
        Write(files.CameraPiece(TimeSpan.FromMinutes(20)), Now.AddMinutes(-5));

        Assert.That(ClassFolders.LessonVideoStateOf(files), Is.EqualTo(LessonVideoState.NotMade));

        Write(files.LessonVideo, Now.AddMinutes(-4));
        Assert.That(ClassFolders.LessonVideoStateOf(files), Is.EqualTo(LessonVideoState.Made));
    }

    /// <summary>A piece newer than the video, as a continued lesson's, leaves the video to be made again.</summary>
    [Test]
    public void A_piece_newer_than_the_video_leaves_it_not_made()
    {
        var files = WriteLesson();
        Write(files.CameraPiece(TimeSpan.Zero), Now.AddMinutes(-30));
        Write(files.LessonVideo, Now.AddMinutes(-25));
        Write(files.CameraPiece(TimeSpan.FromMinutes(40)), Now.AddMinutes(-2));

        Assert.That(ClassFolders.LessonVideoStateOf(files), Is.EqualTo(LessonVideoState.NotMade));
    }

    /// <summary>A video whose pieces were deleted once it passed its check is made: the files say nothing is missing.</summary>
    [Test]
    public void A_video_without_its_pieces_is_made()
    {
        var files = WriteLesson();
        Write(files.LessonVideo, Now.AddMinutes(-4));

        Assert.That(ClassFolders.LessonVideoStateOf(files), Is.EqualTo(LessonVideoState.Made));
    }

    /// <summary>
    /// A piece a camera process still writes, as while the lesson records, is not counted, nor is a .partial: the lesson
    /// being recorded has no video to make yet, and an unfinished video is no video.
    /// </summary>
    [Test]
    public void A_piece_held_for_writing_and_a_partial_are_not_counted()
    {
        var files = WriteLesson();
        var piece = Write(files.CameraPiece(TimeSpan.Zero), Now.AddMinutes(-10));
        Write(files.LessonVideoPartial, Now.AddMinutes(-1));

        using (new FileStream(piece, FileMode.Open, FileAccess.Write, FileShare.Read))
            Assert.That(ClassFolders.LessonVideoStateOf(files), Is.EqualTo(LessonVideoState.None));

        Assert.That(ClassFolders.LessonVideoStateOf(files), Is.EqualTo(LessonVideoState.NotMade), "the .partial is no video");
    }

    /// <summary>
    /// A .partial held open for writing, as while the video is being made, holds the recording; one a crash left, which
    /// nothing holds, blocks nothing.
    /// </summary>
    [Test]
    public void A_partial_held_for_writing_holds_the_recording()
    {
        var files = WriteLesson();
        Write(files.CameraPiece(TimeSpan.Zero), Now.AddMinutes(-10));
        var partial = Write(files.LessonVideoPartial, Now.AddMinutes(-1));

        Assert.That(ClassFolders.IsBeingWritten(files), Is.False, "a .partial nothing holds");
        Assert.That(ClassFolders.IsLessonVideoBeingMade(files), Is.False);

        using (new FileStream(partial, FileMode.Open, FileAccess.Write, FileShare.Read))
        {
            Assert.That(ClassFolders.IsBeingWritten(files), Is.True);
            Assert.That(ClassFolders.IsLessonVideoBeingMade(files), Is.True);
        }
    }

    /// <summary>A date folder holding a lesson's finished video holds a class, even with its pieces and sound gone; a .partial alone does not.</summary>
    [Test]
    public void A_date_folder_with_a_lesson_video_is_a_class_s()
    {
        var folder = _folder.File(@"Tajwid\2026-10-14");
        Directory.CreateDirectory(folder);
        var lesson = RecordingFiles.ForNew(folder, "Tajwid", Now.AddMinutes(-30));
        Write(lesson.LessonVideoPartial, Now.AddMinutes(-20));

        Assert.That(Folders().CreateRecording("Tajwid").Folder, Is.EqualTo(folder), "a .partial alone is a trace, not a class");

        Directory.Delete(folder, recursive: true);
        Directory.CreateDirectory(folder);
        Write(lesson.LessonVideo, Now.AddMinutes(-20));

        Assert.That(Folders().CreateRecording("Tajwid").Folder, Is.EqualTo(_folder.File(@"Tajwid\2026-10-14 (2)")));
    }

    /// <summary>The list describes a lesson with what its files say of its video.</summary>
    [Test]
    public void The_list_describes_a_lesson_with_its_video()
    {
        var files = WriteLesson();
        Write(files.CameraPiece(TimeSpan.Zero), Now.AddMinutes(-10));

        Assert.That(Folders().Describe(files).LessonVideo, Is.EqualTo(LessonVideoState.NotMade));

        Write(files.LessonVideo, Now.AddMinutes(-1));
        Assert.That(Folders().Describe(files).LessonVideo, Is.EqualTo(LessonVideoState.Made));
    }

    /// <summary>The list and menu choice 3 note a lesson's video, made or not made yet, and nothing for a recording without the camera.</summary>
    [TestCase(LessonVideoState.None, null)]
    [TestCase(LessonVideoState.Made, "lesson video")]
    [TestCase(LessonVideoState.NotMade, "lesson video not made yet")]
    public void The_list_notes_the_lesson_video(LessonVideoState state, string? note)
    {
        var recorded = new RecordedClass(new RecordingFiles(_folder.Path, "Tajwid_2026-10-14_17-30-00Z"), Now.UtcDateTime, TimeSpan.FromHours(1), HasTranscript: false,
            KeptAnswer: null, BeingWritten: false, LessonVideo: state);

        var notes = Commands.ListCommand.NotesOf(recorded).ToList();

        Assert.That(notes.Where(found => found.StartsWith("lesson video", StringComparison.Ordinal)), note is null ? Is.Empty : Is.EqualTo(new[] { note }));
    }
}
