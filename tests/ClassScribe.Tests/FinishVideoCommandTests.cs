using ClassScribe.Camera;
using ClassScribe.Commands;
using ClassScribe.Desktop;
using ClassScribe.Drive;
using ClassScribe.Settings;
using ClassScribe.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ClassScribe.Tests;

/// <summary>
/// finish-video, menu choice 3's V and the report's V (teach-video 6.9): a lesson's video made again, free, in the window,
/// through the real camera host with <see cref="FakeLessonVideoMedia"/>: by a file or a folder; refused for no lesson, a
/// lesson being recorded or having its video made, one without pieces, and one whose sound is on Google Drive only; the
/// older video replaced only after the check; the laptop kept awake; Ctrl+C cancelling; what is new going up to Drive.
/// </summary>
[TestFixture]
public sealed class FinishVideoCommandTests
{
    private static readonly DateTimeOffset Started = new(2026, 10, 19, 14, 0, 2, TimeSpan.Zero);
    private TempFolder _folder = null!;
    private RecordingFiles _files = null!;
    private FakeLessonVideoMedia _media = null!;
    private FakeUserSignals _signals = null!;
    private FakePowerRequests _power = null!;
    private FakeDriveAutoUpload _drive = null!;
    private Mock<IDesktop> _desktop = null!;
    private RecordingLogger<FinishVideoCommand> _log = null!;
    private TestLoggerFactory _logs = null!;
    private AudioOnDrive? _onDrive;

    /// <summary>A lesson in a course and date folder, with two seconds of sound and a piece, and new fakes.</summary>
    [SetUp]
    public void SetUp()
    {
        _folder = new TempFolder();
        _files = RecordingFiles.ForNew(Directory.CreateDirectory(_folder.File(@"Tajwid\2026-10-19")).FullName, "Tajwid", Started);
        TestAudio.WriteWav(_files.Segment(1), CameraFinishJob.SoundRate, TestAudio.Tone(CameraFinishJob.SoundRate, 2, 440, 0.25));
        File.WriteAllBytes(_files.CameraPiece(TimeSpan.Zero), [1, 2, 3]);
        _media = new FakeLessonVideoMedia();
        _signals = new FakeUserSignals();
        _power = new FakePowerRequests();
        _drive = new FakeDriveAutoUpload();
        _desktop = new Mock<IDesktop>();
        _log = new RecordingLogger<FinishVideoCommand>();
        _logs = new TestLoggerFactory();
        _onDrive = null;
    }

    /// <summary>Deletes the temporary folder and the log.</summary>
    [TearDown]
    public void TearDown()
    {
        _logs.Dispose();
        _folder.Dispose();
    }

    /// <summary>The command, its camera processes running the real host with the fake media, Drive not set up, opening the folder at the end.</summary>
    private FinishVideoCommand Command()
    {
        var recording = TestOptions.Create(new RecordingOptions { OutputFolder = _folder.Path, OpenFolderWhenDone = true });
        var folders = new ClassFolders(recording, TimeProvider.System, NullLogger<ClassFolders>.Instance, new OnDrive(this));
        var starter = new FakeCameraProcessStarter { Run = (process, jobId) => new CameraHost { Media = _media }.RunAsync(jobId, process.ProcessStream, process.ProcessStream) };
        return new FinishVideoCommand(folders, new FileProbe(), new LessonVideoMaker(starter, TimeProvider.System, _logs),
            new LessonVideoDrive(new FakeCredentialStore(), TestOptions.Create(new DriveOptions())), _drive, _signals, _power, new FakePowerSource(), _desktop.Object, recording, _log);
    }

    /// <summary>What the tests say of the lesson's audio on Google Drive.</summary>
    private sealed class OnDrive(FinishVideoCommandTests tests) : IAudioOnDrive
    {
        /// <inheritdoc />
        public AudioOnDrive? Find(RecordingFiles files) => tests._onDrive;
    }

    /// <summary>A lesson named by one of its files is made into its video: exit 0, the laptop kept awake meanwhile, the folder opened, what is new sent to Drive.</summary>
    [Test]
    public async Task A_lesson_named_by_a_file_is_made_into_its_video()
    {
        var exit = await Command().RunAsync(new FinishVideoRequest(_files.Segment(1)));

        Assert.That(exit, Is.EqualTo(0));
        Assert.That(File.Exists(_files.LessonVideo), Is.True);
        Assert.That(_power.Holds.Single().Reason, Is.EqualTo(StayAwake.LessonVideoReason));
        Assert.That(_power.Holding, Is.False);
        _desktop.Verify(desktop => desktop.OpenFolder(_files.Folder), Times.Once);
        Assert.That(_drive.Calls, Is.EqualTo(new[] { $"Upload {_files.BaseName}" }));
    }

    /// <summary>A folder names its newest lesson with camera pieces, past a newer recording without them; --no-open opens nothing.</summary>
    [Test]
    public async Task A_folder_names_its_newest_lesson_with_pieces()
    {
        var newer = RecordingFiles.ForNew(_files.Folder, "Tajwid", Started.AddHours(1));
        TestAudio.WriteWav(newer.Segment(1), 16000, new short[1600]);

        var exit = await Command().RunAsync(new FinishVideoRequest(_folder.File("Tajwid"), Open: false));

        Assert.That(exit, Is.EqualTo(0));
        Assert.That(File.Exists(_files.LessonVideo), Is.True);
        Assert.That(File.Exists(newer.LessonVideo), Is.False);
        _desktop.Verify(desktop => desktop.OpenFolder(It.IsAny<string>()), Times.Never);
    }

    /// <summary>After free-space the lesson's sound is on Google Drive only, so its video cannot be made again here: exit 1, in those words.</summary>
    [Test]
    public async Task After_free_space_the_video_cannot_be_made_again_here()
    {
        File.Delete(_files.Segment(1));
        _onDrive = new AudioOnDrive(TimeSpan.FromSeconds(2), HasMicrophone: false, "My Drive > ClassScribe > Tajwid > 2026-10-19", Link: null, AllOfIt: true);

        var exit = await Command().RunAsync(new FinishVideoRequest(_files.CameraPiece(TimeSpan.Zero)));

        Assert.That(exit, Is.EqualTo(1));
        Assert.That(_log.Problems, Has.Member(FinishVideoCommand.SoundOnDriveOnly));
        Assert.That(_media.Written, Is.Empty);
    }

    /// <summary>A lesson being recorded, its newest piece held open, is refused; so is one whose video is being made, its .partial held open.</summary>
    [Test]
    public async Task A_lesson_being_written_is_refused()
    {
        using (new FileStream(_files.CameraPiece(TimeSpan.Zero), FileMode.Open, FileAccess.Write, FileShare.Read))
            Assert.That(await Command().RunAsync(new FinishVideoRequest(_files.Segment(1))), Is.EqualTo(1));
        Assert.That(_log.Problems, Has.Member($"{_files.BaseName} is being recorded now; make its video once it has stopped."));

        using (new FileStream(_files.LessonVideoPartial, FileMode.Create, FileAccess.Write, FileShare.Read))
            Assert.That(await Command().RunAsync(new FinishVideoRequest(_files.Segment(1))), Is.EqualTo(1));
        Assert.That(_log.Problems, Has.Some.StartsWith($"The video of {_files.BaseName} is being made now"));
        Assert.That(_media.Written, Is.Empty);
    }

    /// <summary>A recording without camera pieces has no picture to make a video of; one whose pieces went after its video was made says so.</summary>
    [Test]
    public async Task Without_pieces_there_is_nothing_to_make()
    {
        File.Delete(_files.CameraPiece(TimeSpan.Zero));
        Assert.That(await Command().RunAsync(new FinishVideoRequest(_files.Segment(1))), Is.EqualTo(1));
        Assert.That(_log.Problems, Has.Member($"{_files.BaseName} has no camera pieces, so it has no picture to make a video of."));

        File.WriteAllText(_files.LessonVideo, "made");
        Assert.That(await Command().RunAsync(new FinishVideoRequest(_files.Segment(1))), Is.EqualTo(1));
        Assert.That(_log.Problems, Has.Member($"{_files.BaseName} has its video, {_files.BaseName}.mp4, and no camera pieces left to make it again from."));
    }

    /// <summary>A path that names no lesson is refused, exit 1.</summary>
    [Test]
    public async Task A_path_that_names_no_lesson_is_refused()
    {
        var notes = _folder.File("notes.txt");
        File.WriteAllText(notes, "notes");

        Assert.That(await Command().RunAsync(new FinishVideoRequest(notes)), Is.EqualTo(1));
        Assert.That(_log.Problems, Has.Member($"{notes} names no lesson with the camera's pieces to make a video from"));
    }

    /// <summary>An older video is replaced only by one that passed its check: a failed check leaves it as it was, exit 1.</summary>
    [Test]
    public async Task A_failed_check_leaves_the_older_video()
    {
        File.WriteAllText(_files.LessonVideo, "the older video");
        _media.CheckAnswer = "it has no sound";

        Assert.That(await Command().RunAsync(new FinishVideoRequest(_files.Segment(1))), Is.EqualTo(1));
        Assert.That(File.ReadAllText(_files.LessonVideo), Is.EqualTo("the older video"));
        Assert.That(_drive.Calls, Is.Empty);
    }

    /// <summary>Ctrl+C cancels the making: exit 130, the pieces kept.</summary>
    [Test]
    public async Task Ctrl_C_cancels_the_making()
    {
        using var gate = new ManualResetEventSlim();
        _media.HoldWrite = gate;
        var run = Command().RunAsync(new FinishVideoRequest(_files.Segment(1)));
        await Wait.UntilAsync(() => _media.Writing);

        _signals.Cancel();

        Assert.That(await run.WaitAsync(TimeSpan.FromSeconds(30)), Is.EqualTo(130));
        Assert.That(_files.FindCameraPieces(), Has.Count.EqualTo(1));
        Assert.That(File.Exists(_files.LessonVideo), Is.False);
    }
}
