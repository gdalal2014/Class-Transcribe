using ClassScribe.Audio;
using ClassScribe.Commands;
using ClassScribe.Desktop;
using ClassScribe.Desktop.Windows;
using ClassScribe.Recorder;
using ClassScribe.YouTube;

namespace ClassScribe.Tests;

/// <summary>
/// The video window a YouTube video's run records (design 4.3): its browser process from the run's request, alive only while
/// it is the very process the batch started; the tree of processes it heads, read from one snapshot of the processes, each
/// child started no earlier than its parent; its title as the window shows it; its close, posted; and, for a class's run, the
/// processes of every live video window the run files name, which a recording of Chrome leaves out. The chooser records the
/// window's tree from before the video plays, takes its level from the tree's loudest session, and fails closed. Windows'
/// processes, windows and sound are played.
/// </summary>
[TestFixture]
public class VideoPlaybackTests
{
    /// <summary>The window's browser process.</summary>
    private const int Root = 7100;

    /// <summary>The window's audio service, a child of its browser process.</summary>
    private const int AudioService = 7101;

    /// <summary>A renderer of the window's page, another child.</summary>
    private const int Renderer = 7102;

    /// <summary>Gulzar's everyday Chrome's audio service, which is no part of the window.</summary>
    private const int EverydayChrome = 4242;

    /// <summary>When the window's browser process started.</summary>
    private static readonly DateTimeOffset RootStarted = TestRuns.Start.AddMinutes(1);

    private TestRuntime _runtime = null!;
    private FakeProcessTree _tree = null!;
    private FakeVideoWindows _windows = null!;
    private FakeCaptureProvider _provider = null!;

    /// <summary>A test's own ClassScribe folder, the window's processes running, its window shown, and the sound of the apps.</summary>
    [SetUp]
    public void SetUp()
    {
        _runtime = new TestRuntime();
        _tree = new FakeProcessTree()
            .Add(Root, 900, "chrome.exe", RootStarted)
            .Add(AudioService, Root, "chrome.exe", RootStarted.AddSeconds(1))
            .Add(Renderer, Root, "chrome.exe", RootStarted.AddSeconds(2))
            .Add(EverydayChrome, 800, "chrome.exe", TestRuns.Start.AddHours(-3));
        _runtime.Processes.Start(Root, RootStarted);
        _windows = new FakeVideoWindows();
        _windows.Show(Root, "(2) The creed, part 1 - YouTube");
        _provider = new FakeCaptureProvider();
    }

    /// <summary>Deletes the test's folder.</summary>
    [TearDown]
    public void TearDown() => _runtime.Dispose();

    /// <summary>The playback under test.</summary>
    private VideoPlayback Playback() =>
        new(_runtime.Processes, _windows, new ProcessFamily(_tree), _runtime.Journal, _runtime.Time, new RecordingLogger<VideoPlayback>());

    /// <summary>The video the batch asks a run to record, in the window <see cref="Root"/> heads.</summary>
    private static VideoRequest Video(int? root = Root, DateTimeOffset? started = null) =>
        YouTubeTestRuns.Video() with { Obtained = VideoSource.Recorded, BrowserProcessId = root, BrowserStartedUtc = root is null ? null : started ?? RootStarted };

    /// <summary>A chooser of chrome's sound with <paramref name="playback"/>.</summary>
    private CaptureChooser Chooser(VideoPlayback playback) => new(_runtime.Time, new ClassSource.App("chrome"), new RecordingLogger<CaptureChooser>(), playback);

    /// <summary>The window is followed by its browser process, and is open while that very process runs.</summary>
    [Test]
    public void The_window_is_followed_by_its_browser_process()
    {
        var playback = Playback();

        Assert.That(playback.Begin(Video()), Is.True);
        Assert.That(playback.IsVideo, Is.True);
        Assert.That(playback.IsOpen(), Is.True);

        _runtime.Processes.End(Root);
        Assert.That(playback.IsOpen(), Is.False);
    }

    /// <summary>A window gone before the run began, a process id now another process's, and a request that names no window each give no window to record.</summary>
    [Test]
    public void A_window_that_is_not_there_is_not_open()
    {
        Assert.That(Playback().Begin(Video(started: RootStarted.AddMinutes(-5))), Is.False, "the id is another process's now");

        _runtime.Processes.End(Root);
        Assert.That(Playback().Begin(Video()), Is.False);

        var none = Playback();
        Assert.That(none.Begin(Video(root: null)), Is.False);
        Assert.That(none.IsVideo, Is.False);
    }

    /// <summary>The tree is the browser process and every process it started, never a later process given a parent's id, nor another Chrome.</summary>
    [Test]
    public void The_tree_is_the_browser_and_what_it_started()
    {
        _tree.Add(7103, AudioService, "chrome.exe", RootStarted.AddSeconds(3))
            .Add(7104, Root, "chrome.exe", RootStarted.AddMinutes(-10));
        var playback = Playback();
        playback.Begin(Video());

        Assert.That(playback.Tree(), Is.EquivalentTo(new[] { Root, AudioService, Renderer, 7103 }));

        _runtime.Processes.End(Root);
        _tree.Add(Root, 900, "notepad.exe", RootStarted.AddMinutes(30));
        Assert.That(playback.Tree(), Is.Empty, "a later process under the same id heads no window of ours");
    }

    /// <summary>The title is the window's, cleaned to the video's; none while it shows no video's title.</summary>
    [Test]
    public void The_title_is_the_windows_cleaned()
    {
        var playback = Playback();
        playback.Begin(Video());

        Assert.That(playback.Title(), Is.EqualTo("The creed, part 1"));

        _windows.Show(Root, "YouTube");
        Assert.That(playback.Title(), Is.Null);
    }

    /// <summary>The close is posted to the window while it is open, and never waited for; a window gone is not asked.</summary>
    [Test]
    public void The_close_is_posted_while_the_window_is_open()
    {
        var playback = Playback();
        playback.Begin(Video());
        _windows.Answer = FakeCloseAnswer.Never;

        playback.Close();
        Assert.That(_windows.ClosesOf(Root), Is.EqualTo(1));

        _runtime.Processes.End(Root);
        playback.Close();
        Assert.That(_windows.ClosesOf(Root), Is.EqualTo(1));
    }

    /// <summary>
    /// The chooser records the window's browser process, whose tree process loopback takes in, from before anything plays, so
    /// that the first second is not lost; it waits for nothing.
    /// </summary>
    [Test]
    public void The_chooser_records_the_windows_tree_from_before_it_plays()
    {
        var playback = Playback();
        playback.Begin(Video());
        var chooser = Chooser(playback);

        var target = chooser.Choose(_provider, recordedId: null, sinceRecordedSound: null);

        Assert.That(target, Is.EqualTo(new CaptureTarget($"process:{Root}", "ClassScribe's video window", Root)));
        Assert.That(chooser.RecordedLevel, Is.Null, "nothing plays in the window yet");
        Assert.That(chooser.WaitingFor, Is.EqualTo("ClassScribe's video window"));
    }

    /// <summary>The level is the loudest session of the window's tree, never Gulzar's everyday Chrome's, however loud.</summary>
    [Test]
    public void The_level_is_the_windows_own()
    {
        _provider.AddApp(AudioService, "chrome", "Speakers", level: 0.3f);
        _provider.AddApp(EverydayChrome, "chrome", "Speakers", level: 0.9f);
        var playback = Playback();
        playback.Begin(Video());
        var chooser = Chooser(playback);

        Assert.That(chooser.Choose(_provider, recordedId: null, sinceRecordedSound: null)!.ProcessId, Is.EqualTo(Root));
        Assert.That(chooser.RecordedLevel, Is.EqualTo(0.3f));
    }

    /// <summary>With the window gone, the chooser picks nothing: never another Chrome by its name, which could be Gulzar's everyday one.</summary>
    [Test]
    public void The_chooser_fails_closed_without_the_window()
    {
        _provider.AddApp(EverydayChrome, "chrome", "Speakers", level: 0.9f);
        var playback = Playback();
        playback.Begin(Video());
        _runtime.Processes.End(Root);

        var chooser = Chooser(playback);

        Assert.That(chooser.Choose(_provider, recordedId: $"process:{Root}", sinceRecordedSound: null), Is.Null);
        Assert.That(chooser.Choose(_provider, recordedId: null, sinceRecordedSound: null), Is.Null);
        Assert.That(chooser.RecordedLevel, Is.Null);
    }

    /// <summary>
    /// An ordinary recording of Chrome leaves out every process of a live video window the run files name, however loud, so
    /// that a class never records a video window left open; with only the window playing, it waits for Chrome.
    /// </summary>
    [Test]
    public void A_class_recording_Chrome_leaves_a_video_window_out()
    {
        WriteVideoRun(Root, RootStarted);
        _provider.AddApp(AudioService, "chrome", "Speakers", level: 0.9f);
        _provider.AddApp(EverydayChrome, "chrome", "Speakers", level: 0.2f);
        var chooser = Chooser(Playback());

        Assert.That(chooser.Choose(_provider, recordedId: null, sinceRecordedSound: null)!.ProcessId, Is.EqualTo(EverydayChrome));
        Assert.That(chooser.ChooseApp([new PlayingApp(AudioService, "chrome", "Speakers", 0.9f)], recordedId: null), Is.Null);
    }

    /// <summary>A video window whose browser has ended is no longer left out, once the run files are read again, at most every 10 seconds.</summary>
    [Test]
    public void A_video_window_that_ended_is_no_longer_left_out()
    {
        WriteVideoRun(Root, RootStarted);
        var playback = Playback();
        Assert.That(playback.VideoWindowsProcesses(), Is.EquivalentTo(new[] { Root, AudioService, Renderer }));

        _runtime.Processes.End(Root);
        _runtime.Time.Advance(VideoPlayback.OtherWindowsReadEvery);

        Assert.That(playback.VideoWindowsProcesses(), Is.Empty);
    }

    /// <summary>Without a video window in the run files, nothing is left out, and Windows' processes are not even looked at.</summary>
    [Test]
    public void Without_a_video_window_nothing_is_left_out()
    {
        var lists = _tree.Lists;

        Assert.That(Playback().VideoWindowsProcesses(), Is.Empty);
        Assert.That(_tree.Lists, Is.EqualTo(lists));
    }

    /// <summary>Writes the run file of a video's run whose window <paramref name="root"/> heads.</summary>
    private void WriteVideoRun(int root, DateTimeOffset started)
    {
        var run = TestRuns.Record(TestRuns.Id(70), RunState.Recording) with
        {
            Request = TestRuns.Request(TestRuns.Id(70), "Risala") with { Video = Video(root, started) }
        };
        _runtime.Journal.Write(_runtime.Journal.RunFile(run.RunId), run);
    }
}
