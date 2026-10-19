using System.Collections;
using System.ComponentModel;
using System.Runtime.Versioning;
using ClassScribe.Desktop;
using ClassScribe.Desktop.Windows;

namespace ClassScribe.Tests;

/// <summary>
/// Starting Chrome for the record path's video window (design 4.1), through the starter's own start of a process: the
/// program and its arguments quoted as Windows reads them back; a normal window, no inherited handle, the YouTube feature's
/// own folder as the working folder, never the profile's; outside ClassScribe's job where the job allows it, and inside
/// where it does not; and this process's environment without the ElevenLabs key or any ElevenLabs setting. Windows is played
/// by a stand-in, so nothing starts; and in a test, Windows' own start refuses a browser outright.
/// </summary>
[TestFixture, Platform("Win")]
[SupportedOSPlatform("windows")]
public class BrowserStartTests
{
    private const string Chrome = @"C:\Program Files\Google\Chrome\Application\chrome.exe";
    private const string Profile = @"C:\Users\gdala\AppData\Local\ClassScribe\video-browser";

    /// <summary>The YouTube feature's own folder, which Chrome runs in, as the video window starts it: never the profile's.</summary>
    private const string Feature = @"C:\Users\gdala\AppData\Local\ClassScribe\youtube";
    private const string Link = "https://www.youtube.com/watch?v=abcdefghijk";

    /// <summary>The browser's arguments as the video window gives them.</summary>
    private static readonly string[] Arguments = [$"--user-data-dir={Profile}", "--no-first-run", "--no-default-browser-check", $"--app={Link}"];

    private FakeProcessCreationApi _api = null!;
    private RecordingLogger<ProcessStarter> _log = null!;

    /// <summary>The stand-in for Windows' start, and the log.</summary>
    [SetUp]
    public void SetUp()
    {
        _api = new FakeProcessCreationApi();
        _log = new RecordingLogger<ProcessStarter>();
    }

    /// <summary>A starter over the stand-in, with <paramref name="environment"/> as this process's environment.</summary>
    private ProcessStarter Starter(IDictionary? environment = null) =>
        new(_api, new ThisProgram(@"C:\Users\gdala\AppData\Local\Programs\ClassScribe\ClassScribe.exe", @"C:\Users\gdala\AppData\Local\Programs\ClassScribe\"),
            @"C:\Users\gdala\AppData\Local\ClassScribe", _log)
        {
            CurrentEnvironment = () => environment ?? new Hashtable { ["PATH"] = @"C:\Windows" }
        };

    /// <summary>Chrome is the program at its full path, its arguments after it, each quoted as Windows reads it back.</summary>
    [Test]
    public void Chrome_is_started_with_its_arguments_quoted_as_Windows_reads_them()
    {
        var started = Starter().StartBrowser(Chrome, Arguments, Feature);

        var launch = _api.Launches.Single();
        Assert.That(launch.Application, Is.EqualTo(Chrome));
        Assert.That(launch.CommandLine,
            Is.EqualTo($"\"{Chrome}\" --user-data-dir={Profile} --no-first-run --no-default-browser-check --app={Link}"));
        Assert.That(started.ProcessId, Is.EqualTo(_api.Next.ProcessId));
        Assert.That(_log.Messages, Has.None.Contains("video-browser"), "the profile, which may hold a Google sign-in, is never logged");

        Starter().StartBrowser(Chrome, [@"--user-data-dir=C:\Users\Some One\AppData\Local\ClassScribe\video-browser"], Feature);
        Assert.That(_api.Launches[1].CommandLine, Is.EqualTo($"\"{Chrome}\" \"--user-data-dir=C:\\Users\\Some One\\AppData\\Local\\ClassScribe\\video-browser\""),
            "a folder with a space is quoted whole, as Windows reads it back");
    }

    /// <summary>
    /// A normal window, which Windows shows as Chrome asks; no handle of this process; the YouTube feature's own folder as its
    /// working folder, never the profile's; no job of its own, and outside ClassScribe's job, so that the video plays on when
    /// the window is closed.
    /// </summary>
    [Test]
    public void Chrome_gets_a_normal_window_no_handles_the_features_folder_and_breaks_away_from_the_job()
    {
        Starter().StartBrowser(Chrome, Arguments, Feature);

        var launch = _api.Launches.Single();
        Assert.That(launch.Flags, Is.EqualTo(ProcessCreationFlags.UnicodeEnvironment | ProcessCreationFlags.BreakawayFromJob));
        Assert.That(launch.InheritHandles, Is.False);
        Assert.That(launch.Show, Is.Null);
        Assert.That(launch.WorkingFolder, Is.EqualTo(Feature), "ClassScribe never touches the profile folder, which may hold a Google sign-in");
        Assert.That(launch.Handles, Is.Null);
        Assert.That(launch.Job, Is.Null);
    }

    /// <summary>Chrome gets this process's environment without the ElevenLabs key or any ElevenLabs setting, in any letter case; the rest as it is.</summary>
    [Test]
    public void Chrome_never_gets_the_API_key()
    {
        var current = new Hashtable
        {
            ["PATH"] = @"C:\Windows",
            ["ELEVENLABS_API_KEY"] = "dummy-key-for-a-local-test",
            ["elevenlabs_api_key"] = "dummy-key-for-a-local-test",
            ["ElevenLabs__BaseUrl"] = "http://127.0.0.1:5001/",
            ["LOCALAPPDATA"] = @"C:\Users\gdala\AppData\Local"
        };

        Starter(current).StartBrowser(Chrome, Arguments, Feature);

        var environment = _api.Launches.Single().Environment!;
        Assert.That(environment.Keys, Is.EquivalentTo(new[] { "PATH", "LOCALAPPDATA" }));
        Assert.That(environment.Values, Has.None.Contains("dummy-key"));
        Assert.That(environment.ContainsKey("PYTHONIOENCODING"), Is.False, "that is a tool's, not a browser's");
    }

    /// <summary>A job that does not allow breakaway refuses it with Access Denied: Chrome is then started inside it, once more and no other way.</summary>
    [Test]
    public void A_job_that_refuses_breakaway_starts_Chrome_inside_it()
    {
        _api.RefusalFor = launch => launch.Flags.HasFlag(ProcessCreationFlags.BreakawayFromJob) ? IProcessStarter.AccessDenied : 0;

        var started = Starter().StartBrowser(Chrome, Arguments, Feature);

        Assert.That(_api.Launches.Select(launch => launch.Flags.HasFlag(ProcessCreationFlags.BreakawayFromJob)), Is.EqualTo(new[] { true, false }));
        Assert.That(started, Is.SameAs(_api.Next));
    }

    /// <summary>A start Windows refuses for another reason throws its error, named for Chrome, with nothing tried again.</summary>
    [Test]
    public void A_start_Windows_refuses_throws_its_error()
    {
        _api.Refusal = 2;

        var refused = Assert.Throws<Win32Exception>(() => Starter().StartBrowser(Chrome, Arguments, Feature));

        Assert.That(refused!.NativeErrorCode, Is.EqualTo(2));
        Assert.That(refused.Message, Does.StartWith("Windows did not start Chrome:").And.EndWith("(error 2)"));
        Assert.That(_api.Launches, Has.Count.EqualTo(1));
        Assert.That(_log.Messages, Has.None.Contains("video-browser"), "the profile is never logged");
    }

    /// <summary>
    /// In a test, Windows' own start refuses a browser before anything starts, naming the stand-in to use, so that a test that
    /// forgets it never opens a real window or reaches YouTube.
    /// </summary>
    [Test]
    public void In_a_test_Windows_own_start_of_a_browser_is_refused()
    {
        var launch = new ProcessLaunch(Chrome, $"\"{Chrome}\"", InheritHandles: false, ProcessCreationFlags.UnicodeEnvironment, Profile, Show: null)
        {
            Environment = new Dictionary<string, string> { ["PATH"] = @"C:\Windows" }
        };

        var refused = Assert.Throws<InvalidOperationException>(() => NativeProcessCreationApi.Instance.Start(launch, out _));

        Assert.That(refused!.Message, Does.Contain("chrome.exe").And.Contain("FakeProcessStarter"));
    }
}
