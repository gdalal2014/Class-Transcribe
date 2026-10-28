using ClassScribe.Commands;
using ClassScribe.Recorder;
using ClassScribe.Runs;
using ClassScribe.YouTube;

namespace ClassScribe.Tests;

/// <summary>
/// A class always comes first (design 2.5), and a YouTube batch's video is never taken for a class (4.5): a class started from
/// the menu, the command line or the report at the start asks a video's run to give way, says so, waits for it on the steady
/// clock, then starts; a video that does not give way within 30 seconds is explained, with the keys that stop it; a class of
/// the video's course name never attaches to the video; a batch's video asks nothing, never continues a recording, and never
/// shows another run; and a video's recorder lost while it records is never continued.
/// </summary>
public partial class RecordStarterTests
{
    /// <summary>The run of a YouTube video recording now, or in <paramref name="state"/>, of the course Risala, as process 9000, in a separate recorder.</summary>
    private RunRecord VideoRecordingNow(RunState state = RunState.Recording)
    {
        var id = TestRuns.Id(3);
        var record = TestRuns.Record(id, state, processId: 9000, name: "Risala") with
        {
            Pipe = $"ClassScribe-{id}",
            Request = TestRuns.Request(id, "Risala") with { Transcribe = false, Video = VideoOfTheBatch() }
        };
        _runtime.Journal.Write(_runtime.Journal.RunFile(record.RunId), record);
        _runtime.Processes.Start(record.ProcessId, record.ProcessStartedUtc);
        return record;
    }

    /// <summary>A video of the tests' batch, recorded as it plays.</summary>
    private static VideoRequest VideoOfTheBatch() =>
        YouTubeTestRuns.Video() with { Obtained = VideoSource.Recorded, BrowserProcessId = 7100, BrowserStartedUtc = TestRuns.Start };

    /// <summary>The video's run gives way when asked, as its recorder does: it stops recording, and its file says it has finished.</summary>
    private void GivesWay(RunRecord video) =>
        _videoYield.Listen(video.RunId, () =>
        {
            _runtime.Journal.Write(_runtime.Journal.RunFile(video.RunId), video with
            {
                State = RunState.Finished,
                EndedBy = RunEndCause.ClassStarted,
                Outcome = new RunOutcome(OutcomeKind.Recorded, 0, "Recorded.", null, null, null)
            });
            _runtime.Processes.End(video.ProcessId);
        });

    /// <summary>
    /// From the menu, the command line and the report at the start alike, a class asks the video's run to give way, says so,
    /// and starts once it has stopped, as a new recording in a recorder.
    /// </summary>
    [TestCase(StartOrigin.Menu)]
    [TestCase(StartOrigin.CommandLine)]
    [TestCase(StartOrigin.StartupReport)]
    public async Task A_class_makes_a_videos_run_give_way_and_then_starts(StartOrigin origin)
    {
        var video = VideoRecordingNow();
        GivesWay(video);
        _viewer.Then(Ends(_recorder, Transcribed));

        var exitCode = await RunAsync(Request() with { Continue = ContinueChoice.New }, origin);

        Assert.That(exitCode, Is.Zero);
        Assert.That(_videoYield.Asked, Is.EqualTo(new[] { video.RunId }));
        Assert.That(_ui.Screen, Does.Contain("Stopping the YouTube video \"The creed, part 1\" so that \"Kalam\" can record; what it recorded is kept."));
        Assert.That(_starter.Recorders, Has.Count.EqualTo(1));
        Assert.That(_recorder.Runs.Single().Request!.Name, Is.EqualTo("Kalam"));
    }

    /// <summary>A class waits for the video's run on the steady clock while it stops, and starts the moment it has.</summary>
    [Test]
    public async Task A_class_waits_while_the_video_stops()
    {
        var video = VideoRecordingNow();
        _videoYield.Listen(video.RunId, () => { });
        _viewer.Then(Ends(_recorder, Transcribed));

        var running = Starter().RunAsync(Request() with { Continue = ContinueChoice.New }, StartOrigin.CommandLine);
        await _runtime.Time.AdvanceUntilAsync(() => _ui.Screen.Contains("Stopping the YouTube video", StringComparison.Ordinal), TimeSpan.FromMilliseconds(250));
        for (var second = 0; second < 10; second++)
        {
            _runtime.Time.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(10);
        }

        Assert.That(running.IsCompleted, Is.False);
        Assert.That(_starter.Recorders, Is.Empty, "the video still records");

        _runtime.Processes.End(video.ProcessId);
        await _runtime.Time.AdvanceUntilAsync(() => running.IsCompleted, TimeSpan.FromMilliseconds(250), atMost: TimeSpan.FromSeconds(5));

        Assert.That(await running, Is.Zero);
        Assert.That(_starter.Recorders, Has.Count.EqualTo(1));
    }

    /// <summary>
    /// A video that has not stopped 30 seconds after it was asked is explained as any recording is, with the keys that stop
    /// it; the class does not start.
    /// </summary>
    [Test]
    public async Task A_video_that_does_not_give_way_within_30_seconds_is_explained()
    {
        var video = VideoRecordingNow();
        _videoYield.Listen(video.RunId, () => { });

        var running = Starter().RunAsync(Request() with { Continue = ContinueChoice.New }, StartOrigin.Menu);
        var waited = await _runtime.Time.AdvanceUntilAsync(() => running.IsCompleted, TimeSpan.FromMilliseconds(250), atMost: TimeSpan.FromSeconds(40));

        Assert.That(await running, Is.EqualTo(1));
        Assert.That(waited, Is.GreaterThanOrEqualTo(RecordStarter.YieldPatience));
        Assert.That(_starter.Recorders, Is.Empty);
        Assert.That(_ui.Screen, Does.Contain("Only one recording at a time: the YouTube video \"The creed, part 1\" has been recording since"));
        Assert.That(_ui.Screen, Does.Contain("Press Q twice in the window that shows the video to stop it, then start the class again."));
    }

    /// <summary>
    /// A class that starts in the first moment of a video's run, while its run file says it is starting and before the run
    /// listens for its yield, asks it again at each look until it listens, and then waits for it as always; that the video
    /// is stopping is said once, when it has been asked.
    /// </summary>
    [Test]
    public async Task A_class_asks_a_video_still_starting_again_until_it_listens()
    {
        var video = VideoRecordingNow(RunState.Starting);
        _runtime.Time.CreateTimer(_ => GivesWay(video), null, TimeSpan.FromSeconds(1), Timeout.InfiniteTimeSpan);
        _viewer.Then(Ends(_recorder, Transcribed));

        var running = Starter().RunAsync(Request() with { Continue = ContinueChoice.New }, StartOrigin.Menu);
        await _runtime.Time.AdvanceUntilAsync(() => running.IsCompleted, TimeSpan.FromMilliseconds(250), atMost: TimeSpan.FromSeconds(10));

        Assert.That(await running, Is.Zero);
        Assert.That(_videoYield.Asked, Has.Count.GreaterThan(1), "asked again while its run was starting");
        Assert.That(_starter.Recorders, Has.Count.EqualTo(1));
        Assert.That(_ui.Screen.Split(Environment.NewLine).Count(line => line.StartsWith("Stopping the YouTube video", StringComparison.Ordinal)), Is.EqualTo(1));
    }

    /// <summary>
    /// A video's run that is starting and never listens for its yield within 30 seconds is explained, without the words that
    /// it is stopping, since it was never asked, and with the keys that stop it.
    /// </summary>
    [Test]
    public async Task A_video_still_starting_that_never_listens_is_never_said_to_stop()
    {
        VideoRecordingNow(RunState.Starting);

        var running = Starter().RunAsync(Request() with { Continue = ContinueChoice.New }, StartOrigin.Menu);
        await _runtime.Time.AdvanceUntilAsync(() => running.IsCompleted, TimeSpan.FromMilliseconds(250), atMost: TimeSpan.FromSeconds(40));

        Assert.That(await running, Is.EqualTo(1));
        Assert.That(_starter.Recorders, Is.Empty);
        Assert.That(_ui.Screen, Does.Not.Contain("Stopping the YouTube video"));
        Assert.That(_ui.Screen, Does.Contain("It could not be asked to stop: press Q twice in the window that shows the video to stop it, then start the class again."));
    }

    /// <summary>A video's run that does not listen for a class, as one of an older build, is explained at once.</summary>
    [Test]
    public async Task A_video_that_does_not_listen_is_explained_at_once()
    {
        VideoRecordingNow();

        var running = Starter().RunAsync(Request() with { Continue = ContinueChoice.New }, StartOrigin.CommandLine);

        Assert.That(await running.WaitAsync(TimeSpan.FromSeconds(5)), Is.EqualTo(1));
        Assert.That(_starter.Recorders, Is.Empty);
    }

    /// <summary>
    /// record "Risala" from the command line never attaches to a video of the course Risala, as it would to a class of that
    /// name: the video gives way, and the class records.
    /// </summary>
    [Test]
    public async Task A_class_of_the_videos_course_never_attaches_to_the_video()
    {
        var video = VideoRecordingNow();
        GivesWay(video);
        _viewer.Then(Ends(_recorder, Transcribed));

        Assert.That(await RunAsync(Request("Risala") with { Continue = ContinueChoice.New }, StartOrigin.CommandLine), Is.Zero);

        Assert.That(_viewer.Shown.Select(shown => shown.Run.RunId), Has.None.EqualTo(video.RunId));
        Assert.That(_viewer.Shown.Single().Mode, Is.EqualTo(ViewMode.Owner));
    }

    /// <summary>
    /// A batch's video asks nothing, even with a recording of the course stopped minutes ago that a class would be offered to
    /// continue: a new recording, shown as its owner.
    /// </summary>
    [Test]
    public async Task A_batchs_video_asks_nothing_and_is_a_new_recording()
    {
        WriteStopped("Risala");
        _viewer.Then(Ends(_recorder, FakeRunViewer.Transcribed with { Kind = OutcomeKind.Recorded }));
        var request = Request("Risala") with { Transcribe = false, Video = VideoOfTheBatch() };

        await RunAsync(request, StartOrigin.VideoBatch);

        Assert.That(_ui.Screen, Does.Not.Contain("C continues"));
        var taken = _recorder.Runs.Single().Request!;
        Assert.That((taken.Continue, taken.ContinueBaseName, taken.Video), Is.EqualTo((ContinueChoice.New, (string?)null, VideoOfTheBatch())));
        Assert.That(_viewer.Shown.Single().Mode, Is.EqualTo(ViewMode.Owner));
    }

    /// <summary>A batch's video that finds a class recording is explained, never shown the class, and never makes it give way.</summary>
    [Test]
    public async Task A_batchs_video_finds_a_class_recording_and_waits()
    {
        RecordingNow("Risala");

        var exitCode = await RunAsync(Request("Risala") with { Transcribe = false, Video = VideoOfTheBatch() }, StartOrigin.VideoBatch);

        Assert.That(exitCode, Is.EqualTo(1));
        Assert.That(_viewer.Shown, Is.Empty);
        Assert.That(_videoYield.Asked, Is.Empty);
        Assert.That(_starter.Recorders, Is.Empty);
        Assert.That(_ui.Screen, Does.Contain("Only one recording at a time: \"Risala\" has been recording since").And.Contain("the YouTube videos wait for it."));
    }

    /// <summary>A video's recorder lost while it records is never continued: its audio is kept, and the same command records the video again.</summary>
    [Test]
    public async Task A_lost_video_recorder_is_never_continued()
    {
        _viewer.Then(Loses());

        var exitCode = await RunAsync(Request("Risala") with { Transcribe = false, Video = VideoOfTheBatch() }, StartOrigin.VideoBatch);

        Assert.That(exitCode, Is.EqualTo(1));
        Assert.That(_starter.Recorders, Has.Count.EqualTo(1), "never started again");
        Assert.That(_ui.Screen, Does.Contain("The recorder of the YouTube video \"The creed, part 1\" ended unexpectedly at")
            .And.Contain("a video is never continued, so the same command records it again."));
    }
}
