using ClassScribe.Audio;
using ClassScribe.Commands;
using ClassScribe.Desktop;
using ClassScribe.Recorder;
using ClassScribe.Runs;
using ClassScribe.Settings;
using ClassScribe.Storage;
using ClassScribe.YouTube;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClassScribe.Tests;

/// <summary>
/// A YouTube video's run (design 4.5), through the real session, loop and checks: into its batch's date folder, or a date
/// folder of its own for the batch's first; its source file written at the start and again at the stop, with how it ended
/// and whether that end is confirmed; the window's tree recorded through process loopback; failing closed when the window is
/// gone before it records, which leaves the date folder free for a class; giving way to a class through its yield; and the
/// window asked to close once its capture has stopped. A class's run neither listens for a yield nor writes a source file.
/// </summary>
public partial class RecordCommandTests
{
    /// <summary>The video window's browser process.</summary>
    private const int VideoRoot = 7100;

    /// <summary>When the video window's browser process started.</summary>
    private static readonly DateTimeOffset VideoRootStarted = new(2026, 10, 18, 14, 0, 0, TimeSpan.Zero);

    /// <summary>The video window's titles and closes, played.</summary>
    private FakeVideoWindows _videoWindows = null!;

    /// <summary>The yield of the video's run, played.</summary>
    private FakeVideoYield _yield = null!;

    /// <summary>
    /// The video window open, its browser running, showing the video's title, with its capture ready, and the run's playback
    /// over it: what the run's scope holds for a video's run.
    /// </summary>
    private (VideoPlayback Playback, FakeAudioCapture Capture) VideoWindowOpen()
    {
        _videoWindows = new FakeVideoWindows();
        _videoWindows.Show(VideoRoot, "The creed, part 1 - YouTube");
        _yield = new FakeVideoYield();
        _runtime.Processes.Start(VideoRoot, VideoRootStarted);
        var tree = new FakeProcessTree().Add(VideoRoot, 900, "chrome.exe", VideoRootStarted);
        var playback = new VideoPlayback(_runtime.Processes, _videoWindows, new ProcessFamily(tree), _runtime.Journal, TimeProvider.System,
            NullLogger<VideoPlayback>.Instance);
        return (playback, _provider.Enqueue(AppCapture(VideoRoot)));
    }

    /// <summary>A request to record the video in its window, as its batch asks: record only, chrome's process loopback, into <paramref name="folder"/> once the batch has one.</summary>
    private RecordRequest VideoRunRequest(string? folder = null) =>
        Request(transcribe: false, name: "Risala", open: false) with
        {
            Source = new SourcePlan(RecordingMode.Class, new ClassSource.App("chrome"), Microphone: null),
            Video = YouTubeTestRuns.Video(folder: folder) with
            {
                Obtained = VideoSource.Recorded, BrowserProcessId = VideoRoot, BrowserStartedUtc = VideoRootStarted, ChannelHandle = "@yourchannel",
                Tool = "Chrome 141.0.7390.108"
            }
        };

    /// <summary>The one video's source file under the course Risala, read.</summary>
    private VideoSource SourceFile() =>
        VideoSourceFile.Read(Directory.GetFiles(Path.Combine(_folder.Path, "Risala"), "*.source.json", SearchOption.AllDirectories).Single())!;

    /// <summary>
    /// The batch's later video goes into its batch's date folder; its source file says from the start that it is recorded as it
    /// plays, by Chrome, not finished yet; the window's browser process is what is recorded; and the window closed ends the
    /// run, confirmed, with the outcome naming the video.
    /// </summary>
    [Test]
    public async Task A_videos_run_records_into_its_batchs_folder_and_says_how_it_ended()
    {
        var (playback, capture) = VideoWindowOpen();
        var batchFolder = Path.Combine(_folder.Path, "Risala", "2026-10-18");
        Directory.CreateDirectory(batchFolder);

        var run = CreateCommand(video: playback, yield: _yield).RunAsync(VideoRunRequest(batchFolder));
        await Wait.UntilAsync(() => capture.Started);
        var atTheStart = SourceFile();
        capture.Deliver(TestAudio.Shorts(1000, -1000));
        _runtime.Processes.End(VideoRoot);

        Assert.That(await run.WaitAsync(TimeSpan.FromSeconds(15)), Is.Zero);
        var source = SourceFile();
        Assert.That(Path.GetDirectoryName(Directory.GetFiles(batchFolder, "*.source.json").Single()), Is.EqualTo(batchFolder));
        Assert.That((atTheStart.Obtained, atTheStart.Tool, atTheStart.ChannelHandle, atTheStart.Ended, atTheStart.EndConfirmed),
            Is.EqualTo((VideoSource.Recorded, "Chrome 141.0.7390.108", "@yourchannel", VideoEnds.NotFinished, false)));
        Assert.That((source.Title, source.Ended, source.EndConfirmed), Is.EqualTo(("The creed, part 1", "the video window was closed", true)));
        Assert.That(_provider.Created.Single().DeviceId, Is.EqualTo($"process:{VideoRoot}"));
        Assert.That(RunFile().EndedBy, Is.EqualTo(RunEndCause.VideoClosed));
        Assert.That(RunFile().Outcome!.Message, Does.StartWith("The recording of \"The creed, part 1\" is in ").And.Contain(batchFolder));
        Assert.That(_log.Messages, Has.Some.StartsWith("Recording the YouTube video \"The creed, part 1\" (1 of 1) as it plays in ClassScribe's video window"));
    }

    /// <summary>The batch's first video takes its date folder as a class does: the batch's other videos then go into it.</summary>
    [Test]
    public async Task The_batchs_first_video_takes_its_date_folder_as_a_class_does()
    {
        var (playback, capture) = VideoWindowOpen();

        var run = CreateCommand(video: playback, yield: _yield).RunAsync(VideoRunRequest());
        await Wait.UntilAsync(() => capture.Started);
        _runtime.Processes.End(VideoRoot);
        await run.WaitAsync(TimeSpan.FromSeconds(15));

        var dated = Directory.GetDirectories(Path.Combine(_folder.Path, "Risala")).Single();
        Assert.That(FolderNames.IsDateFolder(Path.GetFileName(dated)), Is.True);
        Assert.That(RunFile().Folder, Is.EqualTo(dated), "the batch reads its folder from the first run's file");
    }

    /// <summary>Q twice is Gulzar deciding the video is done: confirmed, and the window, still open, is asked to close.</summary>
    [Test]
    public async Task Q_twice_ends_a_video_confirmed_and_closes_its_window()
    {
        var (playback, capture) = VideoWindowOpen();
        _videoWindows.Answer = FakeCloseAnswer.Never;

        var run = CreateCommand(video: playback, yield: _yield).RunAsync(VideoRunRequest());
        await Wait.UntilAsync(() => capture.Started);
        capture.Deliver(TestAudio.Shorts(1000, -1000));
        _signals.RequestStop();

        Assert.That(await run.WaitAsync(TimeSpan.FromSeconds(15)), Is.Zero);
        Assert.That((SourceFile().Ended, SourceFile().EndConfirmed), Is.EqualTo(("stopped with Q", true)));
        Assert.That(_videoWindows.ClosesOf(VideoRoot), Is.EqualTo(1));
    }

    /// <summary>
    /// A class that starts asks the video's run to give way: it stops as Q twice does, but its end is not confirmed, since the
    /// video may go on, and its window is asked to close. It listens only while it runs.
    /// </summary>
    [Test]
    public async Task A_class_that_starts_stops_the_video_unconfirmed()
    {
        var (playback, capture) = VideoWindowOpen();
        var request = VideoRunRequest();

        var run = CreateCommand(video: playback, yield: _yield).RunAsync(request);
        await Wait.UntilAsync(() => capture.Started);
        capture.Deliver(TestAudio.Shorts(1000, -1000));
        Assert.That(_yield.IsListening(request.RunId), Is.True);
        Assert.That(_yield.Ask(request.RunId), Is.True);

        Assert.That(await run.WaitAsync(TimeSpan.FromSeconds(15)), Is.Zero);
        Assert.That(RunFile().EndedBy, Is.EqualTo(RunEndCause.ClassStarted));
        Assert.That((SourceFile().Ended, SourceFile().EndConfirmed), Is.EqualTo(("stopped so that a class could record", false)));
        Assert.That(_videoWindows.ClosesOf(VideoRoot), Is.EqualTo(1));
        Assert.That(_yield.IsListening(request.RunId), Is.False, "let go with the run");
    }

    /// <summary>
    /// A class that asks the moment the video's run begins to listen, before its run file is written, still stops it as a class
    /// does: the run file, once written, says a class started, and the source file says the end is not confirmed, so that the
    /// same command records the video again, never as one Gulzar stopped with Q.
    /// </summary>
    [Test]
    public async Task A_class_that_asks_before_the_run_file_is_written_stops_the_video_unconfirmed()
    {
        var (playback, _) = VideoWindowOpen();
        _yield.AskedAsItListens = true;

        await CreateCommand(video: playback, yield: _yield).RunAsync(VideoRunRequest()).WaitAsync(TimeSpan.FromSeconds(15));

        Assert.That(RunFile().EndedBy, Is.EqualTo(RunEndCause.ClassStarted));
        Assert.That((SourceFile().Ended, SourceFile().EndConfirmed), Is.EqualTo(("stopped so that a class could record", false)));
    }

    /// <summary>
    /// A window gone before the run records fails the run closed: nothing is captured, never another Chrome; the source file
    /// says why; and the date folder, holding only traces, is free for the day's class.
    /// </summary>
    [Test]
    public void A_window_gone_before_the_run_records_fails_it_closed_and_leaves_the_folder_free()
    {
        var (playback, _) = VideoWindowOpen();
        _runtime.Processes.End(VideoRoot);
        _provider.AddApp(4242, "chrome", "Speakers", level: 0.9f, capture: AppCapture(4242));

        Assert.That(async () => await CreateCommand(video: playback, yield: _yield).RunAsync(VideoRunRequest()), Throws.InstanceOf<VideoWindowClosedException>());

        Assert.That(_provider.Created, Is.Empty, "nothing captured, Gulzar's everyday Chrome least of all");
        Assert.That(RunFile().Outcome!.Kind, Is.EqualTo(OutcomeKind.RecorderFailed));
        Assert.That(RunFile().Outcome!.Message, Is.EqualTo("The recording failed: The video window was closed before the recording began. Nothing was recorded."));
        Assert.That((SourceFile().Ended, SourceFile().EndConfirmed), Is.EqualTo((VideoEnds.ClosedBeforeItBegan, false)));

        var dated = Directory.GetDirectories(Path.Combine(_folder.Path, "Risala")).Single();
        var classFolders = new ClassFolders(TestOptions.Create(new RecordingOptions { OutputFolder = _folder.Path }), TimeProvider.System, NullLogger<ClassFolders>.Instance);
        Assert.That(classFolders.CreateRecording("Risala", _folder.Path).Folder, Is.EqualTo(dated), "the day's class takes the same date folder");
    }

    /// <summary>A video's window that played nothing ends the run with nothing recorded, in a video's words: nothing of it is kept to transcribe.</summary>
    [Test]
    public async Task A_video_that_played_nothing_is_nothing_recorded()
    {
        var (playback, capture) = VideoWindowOpen();

        var run = CreateCommand(video: playback, yield: _yield).RunAsync(VideoRunRequest());
        await Wait.UntilAsync(() => capture.Started);
        capture.Deliver(TestAudio.Shorts(0, 0, 0));
        _runtime.Processes.End(VideoRoot);

        Assert.That(await run.WaitAsync(TimeSpan.FromSeconds(15)), Is.EqualTo(1));
        Assert.That(RunFile().Outcome!.Kind, Is.EqualTo(OutcomeKind.NothingRecorded));
        Assert.That(RunFile().Outcome!.Message, Is.EqualTo("Nothing played in ClassScribe's video window, so nothing of \"The creed, part 1\" was kept to transcribe"));
    }

    /// <summary>A class's run, with the yield and a video window's playback in its scope, never listens for a yield and writes no source file.</summary>
    [Test]
    public async Task A_classs_run_never_listens_for_a_yield_nor_writes_a_source_file()
    {
        var (playback, _) = VideoWindowOpen();
        var request = Request(transcribe: false);

        var run = CreateCommand(video: playback, yield: _yield).RunAsync(request);
        await Wait.UntilAsync(() => _capture.Started);
        Assert.That(_yield.IsListening(request.RunId), Is.False);
        _capture.Deliver(TestAudio.Shorts(1000, -1000));
        _signals.RequestStop();

        Assert.That(await run.WaitAsync(TimeSpan.FromSeconds(15)), Is.Zero);
        Assert.That(Directory.GetFiles(_folder.Path, "*.source.json", SearchOption.AllDirectories), Is.Empty);
        Assert.That(_videoWindows.ClosesOf(VideoRoot), Is.Zero);
    }
}
