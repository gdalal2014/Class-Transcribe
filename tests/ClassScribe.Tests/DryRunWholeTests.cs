using System.Diagnostics;

namespace ClassScribe.Tests;

/// <summary>
/// The whole dry run (tools\dry-run\Start-DryRun.ps1), in a hidden Windows PowerShell 5.1 of the test's own, with every
/// folder it reads or writes in a temporary folder. The window is a stand-in ClassScribe.exe built here, a program
/// without a window that plays a run as the real one leaves it: a run file, a recording's files, one request for a
/// transcript and one read of the account sent to the kit's stand-in for ElevenLabs on 127.0.0.1, and a line in the
/// recorder's log. The kit's test player is built too, and never started. Nothing is recorded and nothing is played.
/// Explicit: it builds those programs and the stand-in, which takes about half a minute; run it before merging a change
/// to tools\dry-run.
/// </summary>
[TestFixture]
[Explicit("Builds a stand-in ClassScribe.exe and runs the whole dry run with it: about half a minute")]
[Category("DryRunKit")]
public class DryRunWholeTests
{
    /// <summary>The stand-in ClassScribe: what each mode makes it do is in its comments.</summary>
    private const string FakeProgram = """
        // A stand-in ClassScribe.exe for DryRunWholeTests: it plays a dry run's window and recorder as the run files show
        // them, without recording anything. DRY_RUN_FAKE_DATA is its %LOCALAPPDATA%\ClassScribe, DRY_RUN_FAKE_USER its
        // %USERPROFILE%\ClassScribe. DRY_RUN_FAKE_MODE: good; or inwindow, which records "in the window" and leaves a stray
        // file in the user's folder.
        using System.Diagnostics;
        using System.Text;
        using System.Text.Json;

        var data = Environment.GetEnvironmentVariable("DRY_RUN_FAKE_DATA")!;
        var mode = Environment.GetEnvironmentVariable("DRY_RUN_FAKE_MODE") ?? "good";
        var output = args[Array.IndexOf(args, "--output") + 1];
        var name = args[1];
        var runId = "20261020-200000Z-" + Guid.NewGuid().ToString("N")[..8];
        var runs = Path.Combine(data, "runs");
        Directory.CreateDirectory(runs);
        var folder = Path.Combine(output, name, "2026-10-20");
        Directory.CreateDirectory(folder);
        var baseName = name + "_2026-10-21_00-00-00Z";
        using var self = Process.GetCurrentProcess();

        void Run(string state, string? outcome) => File.WriteAllText(Path.Combine(runs, runId + ".json"), JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["version"] = 1, ["runId"] = runId, ["processId"] = Environment.ProcessId,
            ["processStartedUtc"] = self.StartTime.ToUniversalTime().ToString("o"), ["exePath"] = Environment.ProcessPath,
            ["home"] = mode == "inwindow" ? "InWindow" : "Separate", ["state"] = state, ["baseName"] = baseName, ["folder"] = folder,
            ["outcome"] = outcome is null ? null : new Dictionary<string, object?> { ["kind"] = outcome, ["message"] = "m", ["transcriptPath"] = Path.Combine(folder, baseName + ".md") },
            ["seen"] = outcome is not null
        }));

        Run("Recording", null);
        var samples = new short[16000 * 4];
        for (var i = 16000; i < 48000; i++) samples[i] = (short)(Math.Sin(2 * Math.PI * 440 * i / 16000) * 6000);
        var wav = new byte[44 + samples.Length * 2];
        Encoding.ASCII.GetBytes("RIFF").CopyTo(wav, 0);
        BitConverter.GetBytes(36 + samples.Length * 2).CopyTo(wav, 4);
        Encoding.ASCII.GetBytes("WAVEfmt ").CopyTo(wav, 8);
        BitConverter.GetBytes(16).CopyTo(wav, 16);
        BitConverter.GetBytes((short)1).CopyTo(wav, 20);
        BitConverter.GetBytes((short)1).CopyTo(wav, 22);
        BitConverter.GetBytes(16000).CopyTo(wav, 24);
        BitConverter.GetBytes(32000).CopyTo(wav, 28);
        BitConverter.GetBytes((short)2).CopyTo(wav, 32);
        BitConverter.GetBytes((short)16).CopyTo(wav, 34);
        Encoding.ASCII.GetBytes("data").CopyTo(wav, 36);
        BitConverter.GetBytes(samples.Length * 2).CopyTo(wav, 40);
        Buffer.BlockCopy(samples, 0, wav, 44, samples.Length * 2);

        var facts = new StringBuilder();
        facts.AppendLine("key " + (Environment.GetEnvironmentVariable("ELEVENLABS_API_KEY") == "dry-run-dummy-key-not-an-elevenlabs-key" ? "dummy" : "other"));
        facts.AppendLine("drive " + Environment.GetEnvironmentVariable("Drive__Enabled"));
        facts.AppendLine("settings " + Environment.GetEnvironmentVariable("CLASSSCRIBE_SETTINGS_FOLDER"));
        facts.AppendLine("base " + Environment.GetEnvironmentVariable("ElevenLabs__BaseUrl"));
        facts.AppendLine("args " + string.Join("|", args));
        using var http = new HttpClient { BaseAddress = new Uri(Environment.GetEnvironmentVariable("ElevenLabs__BaseUrl")!), Timeout = TimeSpan.FromSeconds(30) };
        using var form = new MultipartFormDataContent("fake-boundary")
        {
            { new StringContent("scribe_v2"), "model_id" },
            { new StringContent("kalam"), "keyterms" },
            { new StringContent("tawhid"), "keyterms" },
            { new ByteArrayContent(wav), "file", "audio.wav" }
        };
        using var answer = await http.PostAsync("v1/speech-to-text", form);
        var transcript = await answer.Content.ReadAsStringAsync();
        using var account = await http.GetAsync("v1/user/subscription");
        facts.AppendLine($"answer {(int)answer.StatusCode} account {(int)account.StatusCode}");
        File.WriteAllText(Path.Combine(folder, baseName + ".wav"), "");
        File.WriteAllText(Path.Combine(folder, baseName + ".log"), "00:00:01  Recording started\r\n00:00:02  WARNING  A warning to read\r\n");
        File.WriteAllText(Path.Combine(folder, baseName + ".facts.txt"), facts.ToString());
        File.WriteAllText(Path.Combine(folder, baseName + ".md"), transcript);
        File.AppendAllText(Path.Combine(data, "recorder.log"), "00:00:00  The stand-in recorder ran\r\n");
        if (mode == "inwindow") File.WriteAllText(Path.Combine(Environment.GetEnvironmentVariable("DRY_RUN_FAKE_USER")!, "stray.txt"), "x");
        Run("Finished", "Transcribed");
        return 0;
        """;

    private static TempFolder _build = null!;
    private static string _fakeExe = null!;
    private TempFolder _folder = null!;

    /// <summary>Builds the stand-in ClassScribe.exe once for the fixture, as a program without a window.</summary>
    [OneTimeSetUp]
    public static async Task BuildTheStandInClassScribe()
    {
        _build = new TempFolder();
        var project = Path.Combine(_build.Path, "project");
        Directory.CreateDirectory(project);
        await File.WriteAllTextAsync(Path.Combine(project, "Program.cs"), FakeProgram);
        await File.WriteAllTextAsync(Path.Combine(project, "ClassScribe.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>WinExe</OutputType>
                <TargetFramework>net10.0</TargetFramework>
                <Nullable>enable</Nullable>
                <ImplicitUsings>enable</ImplicitUsings>
                <AssemblyName>ClassScribe</AssemblyName>
              </PropertyGroup>
            </Project>
            """);
        var output = Path.Combine(_build.Path, "bin");
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "build", project, "-o", output, "--disable-build-servers", "-nologo" }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var log = process.StandardOutput.ReadToEndAsync();
        await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.That(process.ExitCode, Is.EqualTo(0), await log);
        _fakeExe = Path.Combine(output, "ClassScribe.exe");
    }

    /// <summary>Deletes the stand-in ClassScribe.</summary>
    [OneTimeTearDown]
    public static void DeleteTheStandInClassScribe() => _build.Dispose();

    /// <summary>Creates the temporary folders that play this Windows user's, with a little in each.</summary>
    [SetUp]
    public void SetUp()
    {
        _folder = new TempFolder();
        Directory.CreateDirectory(Path.Combine(Root("LocalAppData"), "runs"));
        File.WriteAllText(Path.Combine(Root("LocalAppData"), "recorder.log"), "00:00:00  An older line\r\n");
        Directory.CreateDirectory(Root("AppData"));
        File.WriteAllText(Path.Combine(Root("AppData"), "google-drive.dat"), "sealed");
        Directory.CreateDirectory(Path.Combine(Root("UserProfile"), "Mantiq"));
        File.WriteAllText(Path.Combine(Root("UserProfile"), "Mantiq", "Mantiq.md"), "# Mantiq");
    }

    /// <summary>Deletes the temporary folders.</summary>
    [TearDown]
    public void TearDown() => _folder.Dispose();

    /// <summary>The temporary folder that plays the root named <paramref name="name"/>.</summary>
    private string Root(string name) => Path.Combine(_folder.Path, name);

    /// <summary>The roots as the script's hashtable, all in the temporary folder.</summary>
    private string Roots() => $$"""
        @{
            LocalAppData = '{{Root("LocalAppData")}}'
            AppData      = '{{Root("AppData")}}'
            UserProfile  = '{{Root("UserProfile")}}'
            Side         = '{{Root("Side")}}'
            DryRuns      = '{{Root("DryRuns")}}'
            Recordings   = '{{Root("Recordings")}}'
            UserSettings = '{{Path.Combine(Root("AppData"), "appsettings.json")}}'
        }
        """;

    /// <summary>Runs the whole dry run with the stand-in ClassScribe in <paramref name="mode"/>, and returns its output and exit code.</summary>
    private async Task<(int Code, string Output)> DryRunAsync(string mode)
    {
        var script = Path.GetFullPath(Path.Combine(TestContext.CurrentContext.TestDirectory, "..", "..", "..", "..", "..", "tools", "dry-run", "Start-DryRun.ps1"));
        var commands = $$"""
            . {{WindowsPowerShell.Quoted(script)}}
            $Exe = {{WindowsPowerShell.Quoted(_fakeExe)}}
            [Environment]::SetEnvironmentVariable('DRY_RUN_FAKE_DATA', {{WindowsPowerShell.Quoted(Root("LocalAppData"))}}, 'Process')
            [Environment]::SetEnvironmentVariable('DRY_RUN_FAKE_USER', {{WindowsPowerShell.Quoted(Root("UserProfile"))}}, 'Process')
            [Environment]::SetEnvironmentVariable('DRY_RUN_FAKE_MODE', '{{mode}}', 'Process')
            $code = Invoke-DryRun -Roots {{Roots()}}
            "exit code $code"
            "key after $(if ($env:ELEVENLABS_API_KEY -eq 'dry-run-dummy-key-not-an-elevenlabs-key') { 'dummy' } else { 'restored' })"
            """;

        var (exitCode, output, errors) = await WindowsPowerShell.RunAsync(_folder.Path, commands, TimeSpan.FromMinutes(3));
        await TestContext.Out.WriteLineAsync(output);

        Assert.That(errors, Is.Empty, "nothing on the error stream");
        Assert.That(exitCode, Is.EqualTo(0), "the commands ran");
        var line = output.Split(["\r\n", "\n"], StringSplitOptions.None).Last(l => l.StartsWith("exit code ", StringComparison.Ordinal));
        return (int.Parse(line["exit code ".Length..], System.Globalization.CultureInfo.InvariantCulture), output);
    }

    /// <summary>The one dry run's own folder under the kit's folder.</summary>
    private string DryRunFolder() => Directory.GetDirectories(Root("DryRuns")).Single();

    /// <summary>
    /// A build that behaves passes: the window gets the dry run's command line and its environment (a dummy key, the
    /// stand-in's address, Drive off, the settings folder), the stand-in answers one transcript with its keyterms, the run
    /// file passes, the log's warning is shown, the only changes are the run file and the recorder's log, the result is
    /// recorded as PASS in runs.log, and the script's own process has its environment back.
    /// </summary>
    [Test]
    public async Task A_build_that_behaves_passes()
    {
        var (code, output) = await DryRunAsync("good");

        Assert.That(code, Is.EqualTo(0), output);
        Assert.That(output, Does.Contain("The dry run passed.").And.Contain("WARNING  A warning to read").And.Contain("New runs\\20261020-200000Z-"));
        Assert.That(output, Does.Contain("key after restored"));
        var facts = File.ReadAllLines(Directory.GetFiles(Root("Recordings"), "*.facts.txt", SearchOption.AllDirectories).Single());
        Assert.That(facts[0..2], Is.EqualTo(new[] { "key dummy", "drive false" }));
        Assert.That(facts[2], Is.EqualTo("settings " + Path.Combine(DryRunFolder(), "settings")));
        Assert.That(facts[3], Does.Match(@"^base http://127\.0\.0\.1:\d+/$"));
        Assert.That(facts[4], Is.EqualTo($"args record|Dry run|--app|chrome|--mic|Microphone Array|--mic-follows|chrome|--new|--output|{Root("Recordings")}"));
        Assert.That(facts[5], Is.EqualTo("answer 200 account 200"));
        Assert.That(File.ReadAllText(Path.Combine(DryRunFolder(), "stand-in.log")), Does.Contain("2 keyterms").And.Contain("answered 200"));
        var record = File.ReadAllLines(Path.Combine(Root("DryRuns"), "runs.log")).Single();
        Assert.That(record, Does.Contain("  PASS  ").And.Contain("all checks passed").And.Contain(DryRunFolder()));
        Assert.That(File.ReadAllText(Path.Combine(DryRunFolder(), "result.txt")), Does.Contain("Note: 1 warning or error line(s) in the recording's log"));
        Assert.That(Directory.Exists(Root("Side")), Is.False, "nothing was published with -Exe");
    }

    /// <summary>
    /// The test player builds, as the kit builds it for -Player; it is never started here, since it plays sound aloud.
    /// </summary>
    [Test]
    public async Task The_test_player_builds()
    {
        var project = Path.GetFullPath(Path.Combine(TestContext.CurrentContext.TestDirectory, "..", "..", "..", "..", "..", "tools", "dry-run", "TestPlayer", "ClassScribeTestPlayer.csproj"));
        var output = Path.Combine(_folder.Path, "player");
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "build", project, "-c", "Release", "-o", output, "--disable-build-servers", "-nologo" }) start.ArgumentList.Add(argument);

        using var process = Process.Start(start)!;
        var log = process.StandardOutput.ReadToEndAsync();
        await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        Assert.That(process.ExitCode, Is.EqualTo(0), await log);
        Assert.That(await log, Does.Contain("0 Warning(s)"));
        Assert.That(File.Exists(Path.Combine(output, "ClassScribeTestPlayer.exe")), Is.True);
    }

    /// <summary>
    /// A build whose run recorded in the window, and which left a file in the user's recordings folder, fails on both, and
    /// the failure is recorded in runs.log with the first problem.
    /// </summary>
    [Test]
    public async Task A_run_in_the_window_and_a_stray_file_fail()
    {
        var (code, output) = await DryRunAsync("inwindow");

        Assert.That(code, Is.EqualTo(1), output);
        Assert.That(output, Does.Contain("The dry run failed").And.Contain("The recorder ran in the window").And.Contain("New stray.txt   (not expected)"));
        var record = File.ReadAllLines(Path.Combine(Root("DryRuns"), "runs.log")).Single();
        Assert.That(record, Does.Contain("  FAIL  ").And.Contain("The recorder ran in the window"));
    }
}
