using System.Collections;
using System.Runtime.Versioning;
using ClassScribe.Desktop;
using ClassScribe.Desktop.Windows;
using ClassScribe.YouTube;
using Microsoft.Extensions.Logging.Abstractions;
using Win32Exception = System.ComponentModel.Win32Exception;

namespace ClassScribe.Tests;

/// <summary>
/// Running a tool such as yt-dlp (design 3.2): through Windows' own start, with an environment block without the API key,
/// three pipes for its standard handles and no other handle, no window, and a job of its own. Most tests stand in for
/// Windows; those in the WindowsProcess category start Windows' own cmd.exe through the real path, suspended, in a job,
/// with its output read, and end it; none starts yt-dlp, Deno or FFmpeg, and none reaches the network.
/// </summary>
[TestFixture, Platform("Win")]
[SupportedOSPlatform("windows")]
public class ProcessToolRunnerTests
{
    /// <summary>Windows' own command processor, the one program a test may start through the tool path.</summary>
    private static readonly string Cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");

    private FakeProcessCreationApi _api = null!;
    private TempFolder _folder = null!;

    /// <summary>The stand-in for Windows' start, handing back a process that has ended with 0, and a working folder.</summary>
    [SetUp]
    public void SetUp()
    {
        _api = new FakeProcessCreationApi { Next = new StartedProcess(4321, DateTimeOffset.UtcNow, () => 0) };
        _folder = new TempFolder();
    }

    /// <summary>Deletes the working folder.</summary>
    [TearDown]
    public void TearDown() => _folder.Dispose();

    /// <summary>A runner over the stand-in.</summary>
    private ProcessToolRunner Runner() => new(_api, guarded: false, NullLogger.Instance);

    /// <summary>
    /// The tool's launch: its program by full path, the arguments quoted as Windows programs read them, its own pipes as its
    /// only handles, no window, the folder given, and a job; its environment has no API key and no ElevenLabs setting, and
    /// asks Python for UTF-8.
    /// </summary>
    [Test]
    public async Task A_tools_launch_has_its_own_environment_handles_and_job()
    {
        using var key = new TemporaryVariable("ELEVENLABS_API_KEY", "dummy-key-for-a-local-test");
        using var stub = new TemporaryVariable("ElevenLabs__BaseUrl", "http://127.0.0.1:5003/");
        const string program = @"C:\Tools\yt-dlp.exe";

        await Runner().RunAsync(new ToolCall(program, ["--version", @"C:\My Folder\x", "a\"b"], _folder.Path), null, CancellationToken.None);

        var launch = _api.Launches.Single();
        Assert.That(launch.Application, Is.EqualTo(program));
        Assert.That(launch.CommandLine, Is.EqualTo("\"C:\\Tools\\yt-dlp.exe\" --version \"C:\\My Folder\\x\" \"a\\\"b\""));
        Assert.That(launch.InheritHandles, Is.True, "a handle list needs it; the list gives the tool its three pipes and nothing else");
        Assert.That(launch.Flags, Is.EqualTo(ProcessCreationFlags.NoWindow | ProcessCreationFlags.UnicodeEnvironment));
        Assert.That(launch.WorkingFolder, Is.EqualTo(_folder.Path));
        var (environment, hadHandles, hadJob) = _api.ToolParts.Single();
        Assert.That((hadHandles, hadJob), Is.EqualTo((true, true)));
        Assert.That(environment!.Keys, Has.None.EqualTo("ELEVENLABS_API_KEY").IgnoreCase);
        Assert.That(environment.Keys, Has.None.StartsWith("ElevenLabs__").IgnoreCase);
        Assert.That(environment.Values, Has.None.Contains("dummy-key-for-a-local-test"));
        Assert.That(environment, Does.ContainKey("PYTHONIOENCODING").WithValue("utf-8"));
        Assert.That(environment, Does.ContainKey("PATH").Or.ContainKey("Path"), "the rest of the environment is passed on");
    }

    /// <summary>The tool's lines come back as it wrote them, UTF-8 included, each handed on as it comes, output and errors apart, with its exit code.</summary>
    [Test]
    public async Task The_tools_lines_and_exit_code_come_back()
    {
        _api.Next = new StartedProcess(4321, DateTimeOffset.UtcNow, () => 2);
        _api.OnStart = launch =>
        {
            FakeProcessCreationApi.WriteTo(launch.Handles!.Output, "[download]  45.3% of 57.0MiB\r\nالعقيدة\n");
            FakeProcessCreationApi.WriteTo(launch.Handles!.Error, "ERROR: [youtube] abcdefghijk: Video unavailable\n");
        };
        List<ToolLine> seen = [];

        var result = await Runner().RunAsync(new ToolCall(@"C:\Tools\yt-dlp.exe", ["x"], _folder.Path), line => { lock (seen) seen.Add(line); }, CancellationToken.None);

        Assert.That(result.ExitCode, Is.EqualTo(2));
        Assert.That(result.Output, Is.EqualTo(["[download]  45.3% of 57.0MiB", "العقيدة"]));
        Assert.That(result.Errors, Is.EqualTo(["ERROR: [youtube] abcdefghijk: Video unavailable"]));
        Assert.That(seen, Has.Count.EqualTo(3));
        Assert.That(seen.Where(line => line.IsError).Select(line => line.Text), Is.EqualTo(["ERROR: [youtube] abcdefghijk: Video unavailable"]));
    }

    /// <summary>A tool Windows will not start is thrown with Windows' error, and nothing is left running.</summary>
    [Test]
    public void A_tool_Windows_will_not_start_is_thrown_with_its_error()
    {
        _api.Refusal = 2;

        var refused = Assert.ThrowsAsync<Win32Exception>(() => Runner().RunAsync(new ToolCall(@"C:\Tools\yt-dlp.exe", [], _folder.Path), null, CancellationToken.None));

        Assert.That(refused!.NativeErrorCode, Is.EqualTo(2));
        Assert.That(refused.Message, Does.StartWith("Windows did not start yt-dlp.exe"));
    }

    /// <summary>The environment a tool gets leaves out the key and the ElevenLabs settings in any letter case, and Windows' hidden drive variables.</summary>
    [Test]
    public void The_tools_environment_leaves_out_the_key_in_any_case()
    {
        var current = new Hashtable
        {
            ["elevenlabs_api_key"] = "secret", ["ELEVENLABS__BASEURL"] = "http://127.0.0.1:1/", ["=C:"] = @"C:\", ["Path"] = @"C:\Windows", ["TEMP"] = @"C:\Temp"
        };

        var environment = ProcessToolRunner.ToolEnvironment(current);

        Assert.That(environment.Keys, Is.EquivalentTo(["Path", "TEMP", "PYTHONIOENCODING"]));
    }

    /// <summary>An environment block is each name and value, sorted by name in any case, each ended by a null character, and one more at the end.</summary>
    [Test]
    public void An_environment_block_is_sorted_and_ended_twice() =>
        Assert.That(NativeProcessCreationApi.EnvironmentBlock(new Dictionary<string, string> { ["b"] = "2", ["A"] = "1" }), Is.EqualTo("A=1\0b=2\0\0"));

    /// <summary>Arguments are quoted as Windows programs read them back.</summary>
    [TestCase("plain", "plain")]
    [TestCase("", "\"\"")]
    [TestCase("two words", "\"two words\"")]
    [TestCase("say \"hi\"", "\"say \\\"hi\\\"\"")]
    [TestCase(@"C:\folder\", @"C:\folder\")]
    [TestCase(@"C:\my folder\", "\"C:\\my folder\\\\\"")]
    [TestCase(@"a\\""b", "\"a\\\\\\\\\\\"b\"")]
    public void Arguments_are_quoted_as_Windows_reads_them(string argument, string quoted) =>
        Assert.That(WindowsCommandLine.Quote(argument), Is.EqualTo(quoted));

    /// <summary>In a test run the runner made for the app refuses to run anything, naming the fake to use.</summary>
    [Test]
    public void The_apps_runner_refuses_to_run_a_tool_in_a_test()
    {
        var refused = Assert.ThrowsAsync<InvalidOperationException>(() =>
            new ProcessToolRunner(NullLogger<ProcessToolRunner>.Instance).RunAsync(new ToolCall(@"C:\Tools\yt-dlp.exe", ["--version"], _folder.Path), null, CancellationToken.None));

        Assert.That(refused!.Message, Does.Contain("FakeToolRunner"));
    }

    /// <summary>In a test run Windows' own start refuses any tool but its own command processor, before anything starts.</summary>
    [Test]
    public void Windows_own_start_refuses_a_real_tool_in_a_test()
    {
        using var job = ToolJob.Create();
        var launch = new ProcessLaunch(@"C:\Tools\yt-dlp.exe", "\"C:\\Tools\\yt-dlp.exe\" --version", true, ProcessCreationFlags.NoWindow, _folder.Path, null) { Job = job.Handle };

        var refused = Assert.Throws<InvalidOperationException>(() => NativeProcessCreationApi.Instance.Start(launch, out _));

        Assert.That(refused!.Message, Does.Contain("FakeToolRunner"));
    }

    /// <summary>
    /// A real cmd.exe through the tool path: made suspended, put in its job, resumed, its line read through its own pipe,
    /// and its exit code read once it ends.
    /// </summary>
    [Test, Category("WindowsProcess")]
    public async Task A_real_command_processor_runs_through_the_tool_path_and_its_output_is_read()
    {
        var runner = new ProcessToolRunner(NativeProcessCreationApi.Instance, guarded: false, NullLogger.Instance);

        var result = await runner.RunAsync(new ToolCall(Cmd, ["/d", "/c", "echo tool-path-ok& exit 3"], _folder.Path), null, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(30));

        Assert.That(result.Output, Is.EqualTo(["tool-path-ok"]));
        Assert.That(result.ExitCode, Is.EqualTo(3));
    }

    /// <summary>The tool's environment reaches it without the key: the real cmd.exe finds no ELEVENLABS_API_KEY and finds PYTHONIOENCODING.</summary>
    [Test, Category("WindowsProcess")]
    public async Task A_real_tool_finds_no_key_in_its_environment()
    {
        using var key = new TemporaryVariable("ELEVENLABS_API_KEY", "dummy-key-for-a-local-test");
        var runner = new ProcessToolRunner(NativeProcessCreationApi.Instance, guarded: false, NullLogger.Instance);

        var result = await runner.RunAsync(new ToolCall(Cmd, ["/d", "/c", "if defined ELEVENLABS_API_KEY (echo key) else (echo no-key)& echo %PYTHONIOENCODING%"], _folder.Path),
            null, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.That(result.Output.Select(line => line.Trim()), Is.EqualTo(["no-key", "utf-8"]));
    }

    /// <summary>A cancel ends the tool and everything in its job at once: a cmd.exe that would loop for ever is ended, and the cancel is thrown.</summary>
    [Test, Category("WindowsProcess")]
    public void A_cancel_ends_the_tool_at_once()
    {
        var runner = new ProcessToolRunner(NativeProcessCreationApi.Instance, guarded: false, NullLogger.Instance);
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));

        Assert.That(async () => await runner.RunAsync(new ToolCall(Cmd, ["/d", "/c", "echo started& for /l %i in (1,0,2) do @rem"], _folder.Path), null, cancel.Token)
            .WaitAsync(TimeSpan.FromSeconds(30)), Throws.InstanceOf<OperationCanceledException>());
    }

    /// <summary>A process the job will not take is ended before it has run: with a handle that is no job's, the start is refused and cmd.exe never writes its line.</summary>
    [Test, Category("WindowsProcess")]
    public void A_process_the_job_will_not_take_never_runs()
    {
        using var notAJob = new ManualResetEvent(false);
        var marker = _folder.File("ran.txt");
        var launch = new ProcessLaunch(Cmd, $"\"{Cmd}\" /d /c echo ran> \"{marker}\"", InheritHandles: false, ProcessCreationFlags.NoWindow, _folder.Path, null)
        {
            Environment = new Dictionary<string, string> { ["SystemRoot"] = Environment.GetEnvironmentVariable("SystemRoot")! },
            Job = notAJob.SafeWaitHandle
        };

        var started = NativeProcessCreationApi.Instance.Start(launch, out var error);

        Assert.That(started, Is.Null);
        Assert.That(error, Is.Not.Zero);
        Assert.That(File.Exists(marker), Is.False, "the suspended process was ended before it ran");
    }
}
