using ClassScribe.Desktop.Windows;
using ClassScribe.YouTube;

namespace ClassScribe.Tests;

/// <summary>
/// The record path's video window (design 4.1 and 4.2): ClassScribe's own Chrome, its profile in ClassScribe's folder, on the
/// watch page in an --app window, with no switch that makes it play by itself; the check that Chrome did not hand the video to
/// a window left open; the titles as YouTube and Chrome write them, cleaned to the video's; and the close, posted and never a
/// kill, with its 15 seconds. Chrome and its windows are played; nothing starts and no real window is touched.
/// </summary>
[TestFixture]
public class VideoWindowTests
{
    private const string Chrome = @"C:\Program Files\Google\Chrome\Application\chrome.exe";
    private const string Link = "https://www.youtube.com/watch?v=abcdefghijk";

    private TestRuntime _runtime = null!;
    private FakeProcessStarter _starter = null!;
    private FakeVideoBrowsers _browsers = null!;
    private YouTubeFolders _folders = null!;
    private RecordingLogger<VideoWindow> _log = null!;

    /// <summary>A test's own ClassScribe folder, Chrome played, and the log.</summary>
    [SetUp]
    public void SetUp()
    {
        _runtime = new TestRuntime();
        _starter = new FakeProcessStarter(_runtime.Time);
        _browsers = new FakeVideoBrowsers(_starter, _runtime.Processes);
        _folders = new YouTubeFolders(_runtime.Folder);
        _log = new RecordingLogger<VideoWindow>();
    }

    /// <summary>Deletes the test's folder.</summary>
    [TearDown]
    public void TearDown() => _runtime.Dispose();

    /// <summary>The window under test.</summary>
    private VideoWindow Window() => new(_starter, _browsers.Windows, _folders, _runtime.Time, _log);

    /// <summary>Opens the link and moves the clock past the hand-off check.</summary>
    private async Task<WindowOpening> OpenAsync()
    {
        var opening = Window().OpenAsync(Chrome, Link, CancellationToken.None);
        await _runtime.Time.AdvanceUntilAsync(() => opening.IsCompleted, TimeSpan.FromMilliseconds(500), atMost: TimeSpan.FromSeconds(5));
        return await opening;
    }

    /// <summary>
    /// Chrome is started from its path with its own profile in ClassScribe's folder, without first-run pages or the default
    /// browser's question, as one --app window on the video's link; never with a switch that would make it play by itself;
    /// and in the feature's folder, never in the profile's, which ClassScribe never makes.
    /// </summary>
    [Test]
    public async Task Chrome_opens_the_link_in_a_window_of_its_own_profile_and_nothing_plays_by_itself()
    {
        var opening = await OpenAsync();

        var launch = _starter.Browsers.Single();
        Assert.That(launch.Program, Is.EqualTo(Chrome));
        Assert.That(launch.Arguments, Is.EqualTo(new[]
        {
            $"--user-data-dir={Path.Combine(_runtime.Folder.Root, "video-browser")}", "--no-first-run", "--no-default-browser-check", $"--app={Link}"
        }));
        Assert.That(launch.Arguments, Has.None.Contains("autoplay"));
        Assert.That(launch.WorkingFolder, Is.EqualTo(_folders.Root));
        Assert.That(Directory.Exists(_folders.VideoBrowser), Is.False, "the profile is Chrome's to make");
        Assert.That(opening.Window!.Process.ProcessId, Is.EqualTo(launch.ProcessId));
        Assert.That(opening.Problem, Is.Null);
    }

    /// <summary>The window counts as open only once Chrome has stood 3 seconds: not a moment before, on the steady clock.</summary>
    [Test]
    public async Task The_window_counts_as_open_once_Chrome_has_stood_3_seconds()
    {
        var opening = Window().OpenAsync(Chrome, Link, CancellationToken.None);
        _runtime.Time.Advance(TimeSpan.FromMilliseconds(2999));
        await Task.Delay(50);
        Assert.That(opening.IsCompleted, Is.False);

        _runtime.Time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.That((await opening.WaitAsync(TimeSpan.FromSeconds(5))).Window, Is.Not.Null);
    }

    /// <summary>
    /// A Chrome that ended within the 3 seconds handed the video to a window of ClassScribe's left open from before: no window,
    /// with what to do, and Enter tries again.
    /// </summary>
    [Test]
    public async Task A_Chrome_that_handed_the_video_on_gives_no_window_and_says_what_to_do()
    {
        _starter.BrowsersEndAtOnceWith = 0;

        var opening = await OpenAsync();

        Assert.That(opening.Window, Is.Null);
        Assert.That(opening.HandedOff, Is.True);
        Assert.That(opening.Problem, Is.EqualTo(
            "ClassScribe's video window from an earlier run is still open, so Chrome sent the video there. Close every ClassScribe video window, then press Enter to try again"));
    }

    /// <summary>A Chrome that Windows would not start gives no window, with Windows' words.</summary>
    [Test]
    public async Task A_Chrome_Windows_would_not_start_gives_no_window()
    {
        _starter.RefuseBrowsersWith = 2;

        var opening = await Window().OpenAsync(Chrome, Link, CancellationToken.None);

        Assert.That(opening.Window, Is.Null);
        Assert.That(opening.HandedOff, Is.False);
        Assert.That(opening.Problem, Does.StartWith("The video window did not open: Windows did not start Chrome:").And.EndWith("(error 2)"));
    }

    /// <summary>A window's title is cleaned to the video's: the count of notifications and YouTube's and Chrome's endings go; YouTube alone, or nothing, is no title.</summary>
    [TestCase("(3) The creed, part 1 - YouTube", "The creed, part 1")]
    [TestCase("(99+) The creed, part 1 - YouTube", "The creed, part 1")]
    [TestCase("The creed, part 1 - YouTube - Google Chrome", "The creed, part 1")]
    [TestCase("  The creed (2020) - YouTube ", "The creed (2020)")]
    [TestCase("العقيدة 1 - YouTube", "العقيدة 1")]
    [TestCase("YouTube", null)]
    [TestCase("(1) YouTube", null)]
    [TestCase("", null)]
    [TestCase(null, null)]
    public void A_title_is_cleaned_to_the_videos(string? raw, string? clean) => Assert.That(VideoTitles.Clean(raw), Is.EqualTo(clean));

    /// <summary>
    /// A window's title loses every control character, which could ring a bell or start a sequence in the window: each
    /// becomes a space. (Not a test case: a case whose arguments hold a control character never runs under a filter.)
    /// </summary>
    [Test]
    public void A_titles_control_characters_are_taken_out() =>
        Assert.That(VideoTitles.Clean("The creed\a, part\u001b[0m 1 - YouTube"), Is.EqualTo("The creed , part [0m 1"));

    /// <summary>
    /// A close is WM_CLOSE posted to the window, never a kill: true once Chrome has ended; at once, without asking, when it had
    /// ended already.
    /// </summary>
    [Test]
    public async Task A_close_asks_the_window_and_waits_for_Chrome_to_end()
    {
        var window = (await OpenAsync()).Window!;

        Assert.That(await Window().CloseAsync(window, CancellationToken.None), Is.True);
        Assert.That(_browsers.Windows.ClosesOf(window.Process.ProcessId), Is.EqualTo(1));

        Assert.That(await Window().CloseAsync(window, CancellationToken.None), Is.True);
        Assert.That(_browsers.Windows.ClosesOf(window.Process.ProcessId), Is.EqualTo(1), "an ended Chrome is not asked again");
    }

    /// <summary>A Chrome that does not end is given 15 seconds on the steady clock, then the close says it is still open.</summary>
    [Test]
    public async Task A_Chrome_that_will_not_close_is_given_15_seconds()
    {
        var window = (await OpenAsync()).Window!;
        _browsers.Windows.Answer = FakeCloseAnswer.Never;

        var closing = Window().CloseAsync(window, CancellationToken.None);
        var moved = await _runtime.Time.AdvanceUntilAsync(() => closing.IsCompleted, TimeSpan.FromMilliseconds(250), atMost: TimeSpan.FromSeconds(20));

        Assert.That(await closing, Is.False);
        Assert.That(moved, Is.GreaterThanOrEqualTo(VideoWindow.CloseWithin));
    }

    /// <summary>A Chrome that takes a few seconds to end, as one closing its tabs does, is waited for.</summary>
    [Test]
    public async Task A_Chrome_that_takes_a_few_seconds_to_close_is_waited_for()
    {
        var window = (await OpenAsync()).Window!;
        _browsers.Windows.Answer = FakeCloseAnswer.Never;

        var closing = Window().CloseAsync(window, CancellationToken.None);
        _runtime.Time.Advance(TimeSpan.FromSeconds(4));
        await Task.Delay(50);
        _browsers.End(window.Process.ProcessId);
        await _runtime.Time.AdvanceUntilAsync(() => closing.IsCompleted, TimeSpan.FromMilliseconds(250), atMost: TimeSpan.FromSeconds(2));

        Assert.That(await closing, Is.True);
    }
}

/// <summary>The real windows of Chrome, refused in a test (design 7.1): no test lists, reads or closes another process's windows.</summary>
[TestFixture, Platform("Win")]
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public class ChromeWindowsGuardTests
{
    /// <summary>Reading a real window's titles, or closing it, throws in a test, naming the stand-in to use, before Windows is asked anything.</summary>
    [Test]
    public void The_real_windows_of_Chrome_are_refused_in_a_test()
    {
        var windows = new ChromeWindows();

        Assert.That(() => windows.TitlesOf(Environment.ProcessId), Throws.InvalidOperationException.With.Message.Contains("FakeVideoWindows"));
        Assert.That(() => windows.Close(Environment.ProcessId), Throws.InvalidOperationException.With.Message.Contains("FakeVideoWindows"));
    }
}
