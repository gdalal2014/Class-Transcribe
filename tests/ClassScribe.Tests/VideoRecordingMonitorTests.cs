using ClassScribe.Audio;
using ClassScribe.Audio.Microphones;
using ClassScribe.Commands;
using ClassScribe.Desktop;
using ClassScribe.Desktop.Windows;
using ClassScribe.Recorder;
using ClassScribe.Settings;
using ClassScribe.YouTube;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClassScribe.Tests;

/// <summary>
/// How a YouTube video's recording ends by itself (design 4.4), one look a second on the steady clock: the window gone; the
/// window moved on to another video; two minutes of silence after sound, never while paused, with its own warning; nothing
/// heard in five minutes, with a Notice at 30 seconds; YouTube:LongestVideoHours; the safety stop after them; and their order.
/// A class's run is untouched, and a video's run is not watched for a Bluetooth microphone. The window is played: its browser
/// process, its title, and the sound the recording hears.
/// </summary>
[TestFixture]
public class VideoRecordingMonitorTests
{
    /// <summary>The window's browser process.</summary>
    private const int Root = 7100;

    /// <summary>The video's title, as the window shows it.</summary>
    private const string Shown = "(1) The creed, part 1 - YouTube";

    private ManualTimeProvider _time = null!;
    private TestRuntime _runtime = null!;
    private FakeVideoWindows _windows = null!;
    private FakeMicrophoneSessions _sessions = null!;
    private VideoPlayback _playback = null!;
    private RecordingMonitor _monitor = null!;
    private MonitorContext _context = null!;

    /// <summary>The clock at the tests' class, the window's browser running and showing the video, and no app on a microphone.</summary>
    [SetUp]
    public void SetUp()
    {
        _runtime = new TestRuntime();
        _time = _runtime.Time;
        _runtime.Processes.Start(Root, TestRuns.Start);
        _windows = new FakeVideoWindows();
        _windows.Show(Root, Shown);
        _sessions = new FakeMicrophoneSessions();
    }

    /// <summary>Deletes the test's folder.</summary>
    [TearDown]
    public void TearDown() => _runtime.Dispose();

    /// <summary>The video's request, its window <see cref="Root"/>'s.</summary>
    private static RecordRequest VideoRequest() =>
        TestRuns.Request(TestRuns.Id(), "Risala") with
        {
            Transcribe = false,
            Source = new SourcePlan(RecordingMode.Class, new ClassSource.App("chrome"), Microphone: null),
            Video = YouTubeTestRuns.Video() with { Obtained = VideoSource.Recorded, BrowserProcessId = Root, BrowserStartedUtc = TestRuns.Start }
        };

    /// <summary>Makes the monitor for <paramref name="request"/> (a video's unless given), as the loop starts it now, the playback begun as the run begins it.</summary>
    private void Begin(RecordingOptions? options = null, YouTubeOptions? youTube = null, RecordRequest? request = null)
    {
        request ??= VideoRequest();
        _playback = new VideoPlayback(_runtime.Processes, _windows, new ProcessFamily(new FakeProcessTree()), _runtime.Journal, _time, NullLogger<VideoPlayback>.Instance);
        if (request.Video is { } video) _playback.Begin(video);
        var bluetooth = new BluetoothMicrophoneWatch(_sessions, new FakeMicrophoneProvider().Connect(LaptopMicrophones.Array, LaptopMicrophones.PixelBuds),
            new FakeOwnProcesses(Laptop.OwnProcess), _time, NullLogger<BluetoothMicrophoneWatch>.Instance);
        _monitor = new RecordingMonitor(new FreezeWatch(new FakeSystemTimes(_time), _time, NullLogger<FreezeWatch>.Instance), new FakeFreeSpace(),
            TestOptions.Create(options ?? new RecordingOptions { StatusIntervalSeconds = 3600 }), new MicrophoneAdvice(new FakeRegistryValues(), NullLogger<MicrophoneAdvice>.Instance),
            new FakeCaptureProvider(), bluetooth, _time, _playback, TestOptions.Create(youTube ?? new YouTubeOptions()));
        _context = new MonitorContext(request, @"D:\Classes\Risala", _time.GetUtcNow(), _time.GetTimestamp(), TimeSpan.Zero);
    }

    /// <summary>
    /// The recording of the window as a look sees it, sound last heard <paramref name="quiet"/> seconds ago; without
    /// <paramref name="heard"/>, no sound has reached its files yet, and the time since the last sound counts from the start.
    /// </summary>
    private static RecordingSnapshot Window(int quiet = 0, bool paused = false, TimeSpan position = default, bool heard = true) =>
        new(position, paused, FakeRecording.Track("ClassScribe's video window", deliversContinuously: true, sinceLastSound: TimeSpan.FromSeconds(quiet), heardSound: heard),
            Microphone: null);

    /// <summary>Moves the clock on a second, then looks at <paramref name="snapshot"/>.</summary>
    private MonitorTick Tick(RecordingSnapshot snapshot)
    {
        _time.Advance(TimeSpan.FromSeconds(1));
        return _monitor.Tick(snapshot, _context);
    }

    /// <summary>Looks once a second for <paramref name="seconds"/> seconds, at the snapshot <paramref name="snapshot"/> gives for each second, until a look stops the recording.</summary>
    private List<MonitorTick> Run(int seconds, Func<int, RecordingSnapshot> snapshot)
    {
        List<MonitorTick> ticks = [];
        for (var second = 1; second <= seconds; second++)
        {
            ticks.Add(Tick(snapshot(second)));
            if (ticks[^1].StopBecause is not null) break;
        }

        return ticks;
    }

    /// <summary>Every line of every look, in order.</summary>
    private static string[] Texts(IEnumerable<MonitorTick> ticks) => [.. ticks.SelectMany(tick => tick.Lines.Select(line => line.Text))];

    /// <summary>Rule 1: the window gone ends the recording at the next look, confirmed, whatever else holds.</summary>
    [Test]
    public void Rule_1_the_window_closed_stops_the_recording()
    {
        Begin();
        Tick(Window());
        _runtime.Processes.End(Root);

        var tick = Tick(Window());

        Assert.That(tick.StopBecause, Is.EqualTo(RunEndCause.VideoClosed));
        Assert.That(tick.Lines, Has.Some.EqualTo((LogLevel.Information, "The video window was closed; stopping the recording")));
    }

    /// <summary>
    /// Rule 2: one title for at least 5 seconds with sound heard, then another for two looks, as YouTube's Autoplay moves on;
    /// the title the video had is kept for its source file.
    /// </summary>
    [Test]
    public void Rule_2_the_window_moving_on_to_another_video_stops_the_recording()
    {
        Begin();
        var playing = Run(6, _ => Window());
        _windows.Show(Root, "The creed, part 2 - YouTube");

        var first = Tick(Window());
        var second = Tick(Window());

        Assert.That(playing.Select(tick => tick.StopBecause), Has.All.Null);
        Assert.That(first.StopBecause, Is.Null, "one look is not enough");
        Assert.That(second.StopBecause, Is.EqualTo(RunEndCause.VideoEnded));
        Assert.That(second.Lines, Has.Some.EqualTo((LogLevel.Information,
            "The window moved on to another video (\"The creed, part 2\"); the recording of \"The creed, part 1\" stops here")));
        Assert.That(_playback.PlayedTitle, Is.EqualTo("The creed, part 1"));
    }

    /// <summary>A title change before the first title stood 5 seconds with sound, as the page loading does, only takes its place.</summary>
    [Test]
    public void A_title_that_changes_before_the_video_played_only_takes_its_place()
    {
        Begin();
        _windows.Show(Root, "YouTube - Loading");
        Run(10, _ => Window(quiet: 60));
        _windows.Show(Root, Shown);

        var ticks = Run(4, _ => Window(quiet: 60));

        Assert.That(ticks.Select(tick => tick.StopBecause), Has.All.Null);
    }

    /// <summary>A look that reads no title, as between two pages, changes nothing: the next video still needs two looks.</summary>
    [Test]
    public void A_look_without_a_title_changes_nothing()
    {
        Begin();
        Run(6, _ => Window());
        _windows.Show(Root, "YouTube");
        var none = Tick(Window());
        _windows.Show(Root, "The creed, part 2 - YouTube");

        Assert.That(none.StopBecause, Is.Null);
        Assert.That(Tick(Window()).StopBecause, Is.Null);
        Assert.That(Tick(Window()).StopBecause, Is.EqualTo(RunEndCause.VideoEnded));
    }

    /// <summary>
    /// Rule 5: once sound was heard, two minutes of silence stop the recording, not confirmed, after a warning in a video's own
    /// words at the silence warning's time (here 60 seconds) and a Notice while it lasts; the class's silence warning is not given.
    /// </summary>
    [Test]
    public void Rule_5_two_minutes_of_silence_after_sound_stop_the_recording()
    {
        Begin();
        Tick(Window());

        var ticks = Run(130, second => Window(quiet: second));

        Assert.That(ticks[^1].StopBecause, Is.EqualTo(RunEndCause.VideoSilent));
        Assert.That(ticks.Count, Is.EqualTo(120));
        Assert.That(Texts(ticks), Is.EqualTo(new[]
        {
            "Nothing has played for 60 seconds; the recording stops at 2 minutes unless it plays again. Press P here to pause the count",
            "Nothing has played for 2 minutes; the video has most likely ended, or YouTube paused it"
        }));
        Assert.That(ticks[70].Attention.Short, Is.EqualTo("nothing plays"));
        Assert.That(ticks[30].Attention.Level, Is.EqualTo(AttentionLevel.None));
    }

    /// <summary>P stops rule 5's count: paused for ten minutes, nothing stops; resumed, the count starts again from the resume.</summary>
    [Test]
    public void Rule_5_does_not_count_while_paused()
    {
        Begin();
        Tick(Window());
        Run(90, second => Window(quiet: second));

        var paused = Run(600, _ => Window(quiet: 90, paused: true));
        var resumed = Run(100, second => Window(quiet: second));

        Assert.That(paused.Select(tick => tick.StopBecause), Has.All.Null);
        Assert.That(resumed.Select(tick => tick.StopBecause), Has.All.Null, "the silence after the resume is 100 seconds");
    }

    /// <summary>
    /// Rule 6: nothing heard since the window opened stops the recording at 5 minutes, after a Notice and a line at 30 seconds
    /// asking to press play; the silence warning is not given meanwhile.
    /// </summary>
    [Test]
    public void Rule_6_nothing_heard_in_five_minutes_stops_the_recording()
    {
        Begin();

        var ticks = Run(400, second => Window(quiet: second, heard: false));

        Assert.That(ticks[^1].StopBecause, Is.EqualTo(RunEndCause.NothingPlayed));
        Assert.That(ticks.Count, Is.EqualTo(300));
        Assert.That(ticks[28].Attention.Level, Is.EqualTo(AttentionLevel.None));
        Assert.That((ticks[29].Attention.Level, ticks[29].Attention.Short, ticks[29].Attention.Text),
            Is.EqualTo((AttentionLevel.Notice, "press play in the video window", VideoEndWatch.PressPlay)));
        Assert.That(Texts(ticks), Is.EqualTo(new[] { VideoEndWatch.PressPlay, "Nothing has played in ClassScribe's video window for 5 minutes; stopping" }));
    }

    /// <summary>Paused before anything played, rule 6 waits: P is a choice.</summary>
    [Test]
    public void Rule_6_waits_while_paused()
    {
        Begin();

        var ticks = Run(400, second => Window(quiet: second, paused: true, heard: false));

        Assert.That(ticks.Select(tick => tick.StopBecause), Has.All.Null);
    }

    /// <summary>Rule 7: YouTube:LongestVideoHours of recording stops it, with the line naming the setting.</summary>
    [Test]
    public void Rule_7_the_longest_video_stops_the_recording()
    {
        Begin(youTube: new YouTubeOptions { LongestVideoHours = 0.25 });

        var ticks = Run(1000, second => Window(position: TimeSpan.FromSeconds(second)));

        Assert.That(ticks[^1].StopBecause, Is.EqualTo(RunEndCause.VideoLimit));
        Assert.That(ticks.Count, Is.EqualTo(900));
        Assert.That(Texts(ticks), Is.EqualTo(new[] { "This video has played for 15 minutes (YouTube:LongestVideoHours); stopping" }));
    }

    /// <summary>
    /// The rules come in order, the video's before the safety stop: at the same look, the window gone wins over the limit,
    /// and the limit over the safety stop, which is not said.
    /// </summary>
    [Test]
    public void The_rules_come_in_order_and_before_the_safety_stop()
    {
        Begin(new RecordingOptions { StatusIntervalSeconds = 3600, StopAfterHours = 0.25 }, new YouTubeOptions { LongestVideoHours = 0.25 });
        var limit = Run(1000, second => Window(position: TimeSpan.FromSeconds(second)));

        Assert.That(limit[^1].StopBecause, Is.EqualTo(RunEndCause.VideoLimit));
        Assert.That(Texts(limit), Has.None.StartsWith("This recording stops now"), "the safety stop is not said");

        Begin(youTube: new YouTubeOptions { LongestVideoHours = 0.25 });
        Run(899, second => Window(position: TimeSpan.FromSeconds(second)));
        _runtime.Processes.End(Root);
        Assert.That(Tick(Window(position: TimeSpan.FromMinutes(15))).StopBecause, Is.EqualTo(RunEndCause.VideoClosed));
    }

    /// <summary>A stop is said once: the looks after it, until the loop stops, give the same cause and say nothing more.</summary>
    [Test]
    public void A_stop_is_said_once()
    {
        Begin();
        Tick(Window());
        _runtime.Processes.End(Root);

        var first = Tick(Window());
        var again = Tick(Window());

        Assert.That((first.StopBecause, again.StopBecause), Is.EqualTo((RunEndCause.VideoClosed, RunEndCause.VideoClosed)));
        Assert.That(again.Lines, Is.Empty);
    }

    /// <summary>A class's run, with the same playback in its scope, is never stopped by a video's rules, and keeps its usual silence warning.</summary>
    [Test]
    public void A_classs_run_is_never_stopped_by_a_videos_rules()
    {
        Begin(new RecordingOptions { StatusIntervalSeconds = 3600, SilenceWarningSeconds = 60 }, request: TestRuns.Request(TestRuns.Id()));
        _runtime.Processes.End(Root);

        var ticks = Run(400, second => Window(quiet: second));

        Assert.That(ticks.Select(tick => tick.StopBecause), Has.All.Null);
        Assert.That(Texts(ticks), Has.Some.StartsWith("No sound for"));
    }

    /// <summary>A video's run is not watched for a class app on a Bluetooth microphone: no class app listens there.</summary>
    [Test]
    public void A_videos_run_is_not_watched_for_a_Bluetooth_microphone()
    {
        _sessions.SetSessions(LaptopMicrophones.PixelBuds.Id, Laptop.Session(LaptopMicrophones.PixelBuds.Id, Laptop.ChromeUtility));
        Begin();

        var ticks = Run(15, _ => Window());

        Assert.That(Texts(ticks), Has.None.Contains("Bluetooth"));
        Assert.That(_sessions.ReadAlls, Is.Zero);
    }
}
