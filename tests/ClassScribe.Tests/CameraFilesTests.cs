using ClassScribe.Audio;
using ClassScribe.Drive;
using ClassScribe.Settings;
using ClassScribe.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClassScribe.Tests;

/// <summary>
/// The camera's pieces among a recording's files (teach-video 6.6 and 6.15, camera-first step 12): named by where on the
/// clock they begin, found in that order, a sign of a class, held while written, never sent to Google Drive, and a lesson
/// with them continued only by a run with the camera.
/// </summary>
[TestFixture]
public sealed class CameraFilesTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 18, 30, 0, TimeSpan.Zero);

    private TempFolder _folder = null!;
    private ManualTimeProvider _time = null!;

    /// <summary>A temporary recordings folder and a clock moved by hand.</summary>
    [SetUp]
    public void SetUp()
    {
        _folder = new TempFolder();
        _time = new ManualTimeProvider(Now);
    }

    /// <summary>Deletes the temporary folder.</summary>
    [TearDown]
    public void TearDown() => _folder.Dispose();

    /// <summary>The folders under test, rooted in the temporary folder, on the test's clock read in US Eastern time.</summary>
    private ClassFolders Folders() =>
        new(TestOptions.Create(new RecordingOptions { OutputFolder = _folder.Path }), new ZonedClock(_time, ZonedTimeProvider.EasternSummer.LocalTimeZone), NullLogger<ClassFolders>.Instance);

    /// <summary>A recording of Tajwid that started an hour ago and stopped <paramref name="stoppedAgo"/> ago, with its audio.</summary>
    private RecordingFiles WriteStopped(TimeSpan stoppedAgo)
    {
        var files = RecordingFiles.ForNew(_folder.File(@"Tajwid\2026-10-14"), "Tajwid", Now.AddHours(-1));
        Directory.CreateDirectory(files.Folder);
        TestAudio.WriteWav(files.Segment(1), 16000, new short[16000]);
        File.SetLastWriteTimeUtc(files.Segment(1), (Now - stoppedAgo).UtcDateTime);
        return files;
    }

    /// <summary>Writes a piece of the recording beginning at <paramref name="start"/>, and returns its path.</summary>
    private static string WritePiece(RecordingFiles files, TimeSpan start)
    {
        var path = files.CameraPiece(start);
        File.WriteAllBytes(path, [0, 0, 0, 24, (byte)'f', (byte)'t', (byte)'y', (byte)'p']);
        return path;
    }

    /// <summary>A piece at position 0 has the plain name; a later one carries its position to the millisecond, and one under a millisecond counts as 0.</summary>
    [Test]
    public void A_piece_is_named_by_where_on_the_clock_it_begins()
    {
        var files = new RecordingFiles(@"C:\r", "Tajwid 4_2026-10-19_14-00-02Z");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(files.CameraPiece(TimeSpan.Zero), Is.EqualTo(@"C:\r\Tajwid 4_2026-10-19_14-00-02Z.cam.mp4"));
            Assert.That(files.CameraPiece(TimeSpan.FromTicks(4000)), Is.EqualTo(@"C:\r\Tajwid 4_2026-10-19_14-00-02Z.cam.mp4"));
            Assert.That(files.CameraPiece(new TimeSpan(0, 1, 2, 3, 456)), Is.EqualTo(@"C:\r\Tajwid 4_2026-10-19_14-00-02Z.cam@01-02-03.456.mp4"));
            Assert.That(files.CameraPiece(TimeSpan.FromHours(9.5)), Is.EqualTo(@"C:\r\Tajwid 4_2026-10-19_14-00-02Z.cam@09-30-00.000.mp4"));
        }
    }

    /// <summary>A piece is a file of its recording, by its name; a name that only looks like one, or a piece half made, is not.</summary>
    [TestCase("Tajwid_2026-10-14_17-30-00Z.cam.mp4", true)]
    [TestCase("Tajwid_2026-10-14_17-30-00Z.cam@00-41-07.250.mp4", true)]
    [TestCase("Tajwid_2026-10-14_17-30-00Z.CAM.MP4", true)]
    [TestCase("Tajwid_2026-10-14_17-30-00Z.cam@0-41-07.250.mp4", false)]
    [TestCase("Tajwid_2026-10-14_17-30-00Z.cam@00-61-07.250.mp4", false)]
    [TestCase("Tajwid_2026-10-14_17-30-00Z.cam.mp4.partial", false)]
    [TestCase("Tajwid_2026-10-14_17-30-00Z.mp4", false)]
    [TestCase("Tajwid_2026-10-14_17-30-00Z.wav", false)]
    public void A_piece_is_told_by_its_name(string name, bool isPiece)
    {
        var path = Path.Combine(_folder.Path, name);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(RecordingFiles.IsCameraPiece(path), Is.EqualTo(isPiece));
            if (isPiece) Assert.That(RecordingFiles.TryParse(path)?.BaseName, Is.EqualTo("Tajwid_2026-10-14_17-30-00Z"));
        }
    }

    /// <summary>The pieces are found by where they begin, in that order; another recording's pieces and a name that is no piece's are left out.</summary>
    [Test]
    public void The_pieces_are_found_in_their_order_on_the_clock()
    {
        var files = WriteStopped(TimeSpan.FromMinutes(5));
        var later = WritePiece(files, TimeSpan.FromMinutes(41));
        var first = WritePiece(files, TimeSpan.Zero);
        var middle = WritePiece(files, TimeSpan.FromSeconds(73.5));
        File.WriteAllText(Path.Combine(files.Folder, $"{files.BaseName}.cam.mp4.partial"), "");
        WritePiece(RecordingFiles.ForNew(files.Folder, "Tajwid", Now.AddHours(-3)), TimeSpan.Zero);

        var pieces = OnDisk.FindCameraPieces(files);

        Assert.That(pieces, Is.EqualTo(new[]
        {
            new CameraPieceFile(first, TimeSpan.Zero), new CameraPieceFile(middle, TimeSpan.FromSeconds(73.5)), new CameraPieceFile(later, TimeSpan.FromMinutes(41))
        }));
    }

    /// <summary>A recording is being written while a camera process holds its newest piece open for writing, letting others read it; an earlier piece held so does not count.</summary>
    [Test]
    public void A_lesson_whose_newest_piece_is_held_for_writing_is_being_written()
    {
        var files = WriteStopped(TimeSpan.FromMinutes(5));
        var first = WritePiece(files, TimeSpan.Zero);
        var newest = WritePiece(files, TimeSpan.FromMinutes(20));

        using (new FileStream(newest, FileMode.Open, FileAccess.Write, FileShare.Read))
            Assert.That(ClassFolders.IsBeingWritten(files), Is.True);

        using (new FileStream(first, FileMode.Open, FileAccess.Write, FileShare.Read))
            Assert.That(ClassFolders.IsBeingWritten(files), Is.False, "an earlier piece is not the one being written");

        Assert.That(ClassFolders.IsBeingWritten(files), Is.False);
    }

    /// <summary>A date folder holding a lesson's piece holds a class, so a new recording of the day takes the next date folder.</summary>
    [Test]
    public void A_date_folder_with_a_piece_is_a_class_s()
    {
        var folder = _folder.File(@"Tajwid\2026-10-14");
        Directory.CreateDirectory(folder);
        WritePiece(RecordingFiles.ForNew(folder, "Tajwid", Now.AddMinutes(-30)), TimeSpan.Zero);

        var next = Folders().CreateRecording("Tajwid");

        Assert.That(next.Folder, Is.EqualTo(_folder.File(@"Tajwid\2026-10-14 (2)")));
    }

    /// <summary>
    /// A lesson with pieces is offered to be continued only to a run with the camera. A recording without them is offered by
    /// its files to either: they cannot tell a recording made without the camera from a lesson whose camera never gave a
    /// piece, so the run files decide (see <see cref="ContinueQuestionTests"/>).
    /// </summary>
    [Test]
    public void A_lesson_with_the_camera_is_continued_only_with_the_camera()
    {
        var plain = WriteStopped(TimeSpan.FromMinutes(5));

        Assert.That(Folders().FindContinuable("Tajwid", RecordingMode.Microphone, TimeSpan.FromHours(1))?.Files, Is.EqualTo(plain));
        Assert.That(Folders().FindContinuable("Tajwid", RecordingMode.Microphone, TimeSpan.FromHours(1), camera: true)?.Files, Is.EqualTo(plain));

        WritePiece(plain, TimeSpan.Zero);

        Assert.That(Folders().FindContinuable("Tajwid", RecordingMode.Microphone, TimeSpan.FromHours(1), camera: true)?.Files, Is.EqualTo(plain));
        Assert.That(Folders().FindContinuable("Tajwid", RecordingMode.Microphone, TimeSpan.FromHours(1)), Is.Null);
    }

    /// <summary>A lesson whose newest piece a camera process still writes is being recorded, and is not offered.</summary>
    [Test]
    public void A_lesson_whose_piece_is_being_written_is_not_offered()
    {
        var files = WriteStopped(TimeSpan.FromMinutes(5));
        var piece = WritePiece(files, TimeSpan.Zero);

        using (new FileStream(piece, FileMode.Open, FileAccess.Write, FileShare.Read))
            Assert.That(Folders().FindContinuable("Tajwid", RecordingMode.Microphone, TimeSpan.FromHours(1), camera: true), Is.Null);
    }

    /// <summary>
    /// The pieces never go to Google Drive, even with everything sent: the files that go up are the recording's others, and
    /// a waiting recording is described without them.
    /// </summary>
    [Test]
    public void The_pieces_never_go_to_Drive()
    {
        using var world = new GoogleWorld();
        var files = world.WriteRecording(@"Tajwid\2026-10-14", "Tajwid", Now, extras: [".marks.txt", ".log"]);
        WritePiece(files, TimeSpan.Zero);
        WritePiece(files, TimeSpan.FromMinutes(12));

        var going = world.RecordingUpload().FilesToUpload(files);
        var (count, _) = UploadManifests.OnDisk(files);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(going.Any(RecordingFiles.IsCameraPiece), Is.False);
            Assert.That(going.Select(Path.GetFileName), Is.EquivalentTo(new[] { $"{files.BaseName}.wav", $"{files.BaseName}.marks.txt", $"{files.BaseName}.log" }));
            Assert.That(count, Is.EqualTo(3), "the pieces are left out of what waits to go up");
        }
    }
}
