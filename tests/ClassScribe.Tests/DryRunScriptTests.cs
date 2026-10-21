using System.Diagnostics;
using System.Net;
using System.Text;
using ClassScribe.DryRun;

namespace ClassScribe.Tests;

/// <summary>
/// The dry-run kit's scripts (tools\dry-run): ASCII only, so that Windows PowerShell 5.1, which reads a script without a
/// byte-order mark in the ANSI code page, reads them as written; parsed by Windows PowerShell 5.1 itself; and the script's
/// own checks, dot-sourced into a hidden Windows PowerShell 5.1 of the test's own (which runs nothing of the dry run when
/// dot-sourced), over temporary folders only.
/// </summary>
[TestFixture]
public class DryRunScriptTests
{
    private TempFolder _folder = null!;

    /// <summary>Creates a temporary folder for the run files, listings and logs the checks read.</summary>
    [SetUp]
    public void SetUp() => _folder = new TempFolder();

    /// <summary>Deletes the temporary folder.</summary>
    [TearDown]
    public void TearDown() => _folder.Dispose();

    /// <summary>The repository's root: the folder above the tests that holds ClassScribe.sln.</summary>
    private static string RepositoryRoot()
    {
        for (var folder = new DirectoryInfo(TestContext.CurrentContext.TestDirectory); folder is not null; folder = folder.Parent)
        {
            if (File.Exists(Path.Combine(folder.FullName, "ClassScribe.sln"))) return folder.FullName;
        }

        throw new InvalidOperationException("ClassScribe.sln was not found above the test folder.");
    }

    /// <summary>The kit's folder in this repository.</summary>
    private static string KitFolder() => Path.Combine(RepositoryRoot(), "tools", "dry-run");

    /// <summary>The kit's main script.</summary>
    private static string Script() => Path.Combine(KitFolder(), "Start-DryRun.ps1");

    /// <summary>A path written as a PowerShell string in single quotes.</summary>
    private static string Quoted(string text) => WindowsPowerShell.Quoted(text);

    /// <summary>
    /// Runs <paramref name="commands"/> in a hidden Windows PowerShell 5.1 of the test's own, after the kit's script has
    /// been dot-sourced, and returns its output lines; fails on an error or an exit code other than 0.
    /// </summary>
    private async Task<string[]> PowerShellAsync(string commands, bool dotSource = true)
    {
        var (exitCode, output, errors) = await WindowsPowerShell.RunAsync(_folder.Path, (dotSource ? $". {Quoted(Script())}\r\n" : "") + commands, TimeSpan.FromSeconds(60));

        Assert.That((exitCode, errors), Is.EqualTo((0, "")), "Windows PowerShell 5.1 ran the commands without an error");
        return output.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries);
    }

    /// <summary>Writes a run file as the app writes it (the fields the script reads) and returns its path.</summary>
    private string RunFile(string folder, string runId, string state, int processId, DateTimeOffset started, string home = "Separate",
        string? outcome = null, string? transcript = null, bool seen = false, string exe = @"C:\Users\x\AppData\Local\Programs\ClassScribe-Dry\ClassScribe.exe")
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, runId + ".json");
        var outcomeJson = outcome is null ? "null"
            : $$"""{"kind":"{{outcome}}","exitCode":0,"message":"m","transcriptPath":{{(transcript is null ? "null" : System.Text.Json.JsonSerializer.Serialize(transcript))}},"folder":null,"retryCommand":null}""";
        File.WriteAllText(path, $$"""
            {"version":1,"runId":"{{runId}}","processId":{{processId}},"processStartedUtc":"{{started.ToUniversalTime():o}}","exePath":{{System.Text.Json.JsonSerializer.Serialize(exe)}},"build":"1.0.0+test","protocol":1,"pipe":null,"home":"{{home}}","request":null,"baseName":null,"folder":null,"state":"{{state}}","startedUtc":"{{started.ToUniversalTime():o}}","heartbeatUtc":"{{started.ToUniversalTime():o}}","position":"00:00:00","uploadPercent":null,"endedBy":null,"outcome":{{outcomeJson}},"seen":{{(seen ? "true" : "false")}}}
            """);
        return path;
    }

    /// <summary>
    /// Every file of the kit, and of installer\ once it exists, is ASCII: no byte above 127, so no byte-order mark either.
    /// Build output (bin and obj) is not the kit's.
    /// </summary>
    [Test]
    public void Every_file_of_the_kit_is_ASCII()
    {
        var root = RepositoryRoot();
        var folders = new[] { Path.Combine(root, "tools"), Path.Combine(root, "installer") }.Where(Directory.Exists).ToList();
        var files = folders
            .SelectMany(folder => Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            .Where(file => !Path.GetRelativePath(root, file).Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj"))
            .ToList();

        Assert.That(files.Select(Path.GetFileName), Is.SupersetOf(new[] { "Start-DryRun.cmd", "Start-DryRun.ps1", "ElevenLabsStub.cs", "Program.cs", "README.md" }));
        foreach (var file in files)
        {
            var bytes = File.ReadAllBytes(file);
            var first = Array.FindIndex(bytes, b => b > 127);
            Assert.That(first, Is.EqualTo(-1), $"{Path.GetRelativePath(root, file)} has a byte above 127 at offset {first}");
        }
    }

    /// <summary>Windows PowerShell 5.1's own parser finds no error in any script of the kit.</summary>
    [Test]
    [Category("WindowsProcess")]
    public async Task The_kit_scripts_parse_under_Windows_PowerShell_5_1()
    {
        var scripts = Directory.EnumerateFiles(Path.Combine(RepositoryRoot(), "tools"), "*.ps1", SearchOption.AllDirectories).ToList();
        Assert.That(scripts, Is.Not.Empty);

        var lines = await PowerShellAsync($$"""
            if ($PSVersionTable.PSVersion.Major -ne 5) { throw "Not Windows PowerShell 5.1: $($PSVersionTable.PSVersion)" }
            foreach ($path in @({{string.Join(", ", scripts.Select(Quoted))}})) {
                $tokens = $null; $errors = $null
                [void][System.Management.Automation.Language.Parser]::ParseFile($path, [ref]$tokens, [ref]$errors)
                foreach ($e in $errors) { "$path($($e.Extent.StartLineNumber)): $($e.Message)" }
                "parsed $path"
            }
            """, dotSource: false);

        Assert.That(lines, Is.EqualTo(scripts.Select(path => $"parsed {path}")));
    }

    /// <summary>
    /// The script's listing of a data folder names each file with its size and time; two listings compared give each file
    /// added, changed or removed, in the order of their paths, and a folder that does not exist lists as empty.
    /// </summary>
    [Test]
    [Category("WindowsProcess")]
    public async Task The_listings_before_and_after_name_each_change()
    {
        var root = Path.Combine(_folder.Path, "ClassScribe");
        Directory.CreateDirectory(Path.Combine(root, "runs"));
        File.WriteAllText(Path.Combine(root, "recorder.log"), "a line\r\n");
        File.WriteAllText(Path.Combine(root, "old.txt"), "old");
        File.WriteAllText(Path.Combine(root, "runs", "same.json"), "{}");

        var lines = await PowerShellAsync($$"""
            $root = {{Quoted(root)}}
            $before = Get-FolderListing -Root $root
            Set-Content -LiteralPath (Join-Path $root 'runs\new.json') -Value 'x'
            Add-Content -LiteralPath (Join-Path $root 'recorder.log') -Value 'more'
            Remove-Item -LiteralPath (Join-Path $root 'old.txt')
            $after = Get-FolderListing -Root $root
            "listed $(@($before).Count) then $(@($after).Count)"
            Compare-FolderListing -Before $before -After $after | ForEach-Object { "$($_.Change) $($_.Path)$(if ($_.Before) { ' (was ' + $_.Before.Substring(0, 2) + ')' })" }
            "missing $(@(Get-FolderListing -Root (Join-Path $root 'not here')).Count)"
            $before | Where-Object { $_.Path -eq 'runs\same.json' } | ForEach-Object { "line $($_.Line)" }
            """);

        Assert.That(lines.Take(5), Is.EqualTo(new[] { "listed 3 then 3", "Removed old.txt (was 20)", "Changed recorder.log (was 20)", "New runs\\new.json", "missing 0" }));
        Assert.That(lines[5], Does.StartWith("line runs\\same.json\t2\t20"));
    }

    /// <summary>
    /// The window's command line: each argument quoted as Windows' C runtime reads it back (spaces, quotes, a trailing
    /// backslash before the closing quote, an empty value), and the record command for the class with the microphone, the
    /// class alone in five minutes, a saved class and the camera in person; and options given as one string through
    /// Start-DryRun.cmd (a comma list, settings joined by semicolons) split into their items.
    /// </summary>
    [Test]
    [Category("WindowsProcess")]
    public async Task The_window_gets_its_command_line_as_written()
    {
        var lines = await PowerShellAsync("""
            foreach ($text in @('chrome', 'Dry run', 'C:\a b\', 'say "hi"', '', 'a\"b')) { "[$(ConvertTo-Argument $text)]" }
            function Line($a) { (Get-RecordArguments @a | ForEach-Object { ConvertTo-Argument $_ }) -join ' ' }
            Line @{ Name = 'Dry run'; App = 'chrome'; Mic = 'Microphone Array'; NoMic = $false; Follows = 'chrome'; Short = $false; Output = 'C:\x\ClassScribe dry runs' }
            Line @{ Name = 'Dry run'; App = 'chrome'; Mic = 'Microphone Array'; NoMic = $true; Follows = 'chrome'; Short = $true; Output = 'C:\out' }
            Line @{ Name = 'Dry run'; Class = 'Mantiq'; Output = 'C:\out' }
            Line @{ Name = 'Dry run'; Mic = 'OBSBOT'; Camera = 'OBSBOT'; Output = 'C:\out' }
            "expect $((Split-Items -Items @('.srt,.vtt', ' .md ') -Separator ',') -join '|')"
            "settings $((Split-Items -Items @('Recording:FlushSeconds=5;ElevenLabs:KeytermsFile=keyterms.txt') -Separator ';') -join '|')"
            "none $(@(Split-Items -Items @() -Separator ',').Count)"
            """);

        Assert.That(lines, Is.EqualTo(new[]
        {
            "[chrome]", "[\"Dry run\"]", "[\"C:\\a b\\\\\"]", "[\"say \\\"hi\\\"\"]", "[\"\"]", "[\"a\\\\\\\"b\"]",
            "record \"Dry run\" --app chrome --mic \"Microphone Array\" --mic-follows chrome --new --output \"C:\\x\\ClassScribe dry runs\"",
            "record \"Dry run\" --app chrome --minutes 5 --new --output C:\\out",
            "record --class Mantiq --new --output C:\\out",
            "record \"Dry run\" --in-person OBSBOT --camera OBSBOT --new --output C:\\out",
            "expect .srt|.vtt|.md",
            "settings Recording:FlushSeconds=5|ElevenLabs:KeytermsFile=keyterms.txt",
            "none 0"
        }));
    }

    /// <summary>
    /// A class is recording when a run file is in an active state and its process is the one that wrote it (the same id and
    /// start time); a finished run, a run whose process id now names another process, a run whose process is gone and a file
    /// that cannot be read are not. Here the test's own process plays the recorder.
    /// </summary>
    [Test]
    [Category("WindowsProcess")]
    public async Task The_dry_run_refuses_while_a_class_records()
    {
        using var self = Process.GetCurrentProcess();
        var started = new DateTimeOffset(self.StartTime);
        var runs = Path.Combine(_folder.Path, "runs");
        RunFile(runs, "20261020-200000Z-00000001", "Finished", self.Id, started, outcome: "Transcribed", seen: true);
        RunFile(runs, "20261020-200000Z-00000002", "Recording", self.Id, started);
        RunFile(runs, "20261020-200000Z-00000003", "Transcribing", self.Id, started.AddYears(-3));
        RunFile(runs, "20261020-200000Z-00000004", "Stopping", 0, started);
        File.WriteAllText(Path.Combine(runs, "20261020-200000Z-00000005.json"), "not json {");

        var lines = await PowerShellAsync($$"""
            Get-LiveRuns -RunsFolder {{Quoted(runs)}} | ForEach-Object { "live $($_.runId) $($_.state)" }
            "none $(@(Get-LiveRuns -RunsFolder {{Quoted(Path.Combine(_folder.Path, "no runs"))}}).Count)"
            """);

        Assert.That(lines, Is.EqualTo(new[] { "live 20261020-200000Z-00000002 Recording", "none 0" }));
    }

    /// <summary>
    /// The whole dry run, with every folder in a temporary one, refuses while a class records: exit code 2, nothing
    /// published, no stand-in, no record in runs.log, no folder of its own left behind, and its process's environment as
    /// it was.
    /// </summary>
    [Test]
    [Category("WindowsProcess")]
    public async Task A_refused_dry_run_does_nothing()
    {
        using var self = Process.GetCurrentProcess();
        var local = Path.Combine(_folder.Path, "LocalAppData");
        RunFile(Path.Combine(local, "runs"), "20261020-200000Z-0000000d", "Recording", self.Id, new DateTimeOffset(self.StartTime));
        var exe = _folder.File("ClassScribe.exe");
        File.WriteAllText(exe, "not run");
        string R(string name) => Quoted(Path.Combine(_folder.Path, name));

        var lines = await PowerShellAsync($$"""
            $Exe = {{Quoted(exe)}}
            $code = Invoke-DryRun -Roots @{ LocalAppData = {{Quoted(local)}}; AppData = {{R("AppData")}}; UserProfile = {{R("UserProfile")}}; Side = {{R("Side")}}; DryRuns = {{R("DryRuns")}}; Recordings = {{R("Recordings")}}; UserSettings = {{R("settings.json")}} }
            "exit code $code"
            "kit folder $(Test-Path -LiteralPath {{R("DryRuns")}} -PathType Container) $(@(Get-ChildItem -LiteralPath {{R("DryRuns")}} -Recurse -ErrorAction SilentlyContinue).Count)"
            "key $(if ($env:ELEVENLABS_API_KEY -eq $script:DummyKey) { 'dummy' } else { 'as it was' }), drive $(if ($env:Drive__Enabled) { $env:Drive__Enabled } else { 'unset' })"
            """);

        Assert.That(lines, Has.Some.Contains("A run is under way: 20261020-200000Z-0000000d, Recording"));
        Assert.That(lines, Has.Some.EqualTo("Nothing was done."));
        Assert.That(lines.TakeLast(3), Is.EqualTo(new[] { "exit code 2", "kit folder True 0", "key as it was, drive unset" }));
        Assert.That(Directory.Exists(Path.Combine(_folder.Path, "Side")), Is.False);
    }

    /// <summary>
    /// Of the changes to %LOCALAPPDATA%\ClassScribe, the dry run expects only its own run file, the recorder's log, the
    /// lock file, and run files last written more than 30 days ago going (any start of ClassScribe prunes them); anything
    /// else there, and any change at all to the other two folders, is named as unexpected.
    /// </summary>
    [Test]
    [Category("WindowsProcess")]
    public async Task The_dry_run_tells_the_changes_it_expects_from_the_others()
    {
        var lines = await PowerShellAsync("""
            function C($change, $path, $before = (Get-Date).ToUniversalTime().ToString('o')) { [pscustomobject]@{ Change = $change; Path = $path; Before = $before } }
            $changes = @(
                (C 'New' 'runs\20261020-200000Z-0000000a.json'),
                (C 'Changed' 'recorder.log'),
                (C 'New' 'recorder.old.log'),
                (C 'Changed' 'recording.lock'),
                (C 'Removed' 'runs\20200101-200000Z-00000001.json' '2020-01-01T20:00:10.0000000Z'),
                (C 'New' 'runs\20261020-200000Z-0000000b.json'),
                (C 'New' 'drive\recordings\Dry run.json'),
                (C 'Removed' 'runs\20261020-200000Z-0000000a.json')
            )
            Get-UnexpectedChanges -Changes $changes -Area 'LocalAppData' -RunId '20261020-200000Z-0000000a' | ForEach-Object { "local $($_.Change) $($_.Path)" }
            Get-UnexpectedChanges -Changes @((C 'Changed' 'google-drive.dat')) -Area 'AppData' -RunId '20261020-200000Z-0000000a' | ForEach-Object { "roaming $($_.Change) $($_.Path)" }
            "nothing $(@(Get-UnexpectedChanges -Changes @() -Area 'UserProfile' -RunId 'x').Count)"
            """);

        Assert.That(lines, Is.EqualTo(new[]
        {
            "local New runs\\20261020-200000Z-0000000b.json",
            "local New drive\\recordings\\Dry run.json",
            "local Removed runs\\20261020-200000Z-0000000a.json",
            "roaming Changed google-drive.dat",
            "nothing 0"
        }));
    }

    /// <summary>
    /// The run's verdict from its run file and its recording's log: a run that recorded in the separate recorder, was
    /// transcribed, saved its transcript and was seen passes; a run in the window, one without a transcript, and one never
    /// seen each fail with the reason; warnings and errors in the log are named.
    /// </summary>
    [Test]
    [Category("WindowsProcess")]
    public async Task The_dry_run_checks_the_run_file_and_the_log()
    {
        var started = DateTimeOffset.UtcNow.AddMinutes(-20);
        var transcript = _folder.File("Dry run.md");
        File.WriteAllText(transcript, "# Dry run");
        var good = RunFile(_folder.Path, "20261020-200000Z-0000000a", "Finished", 1, started, outcome: "Transcribed", transcript: transcript, seen: true);
        var inWindow = RunFile(Path.Combine(_folder.Path, "w"), "20261020-200000Z-0000000b", "Finished", 1, started, home: "InWindow", outcome: "Transcribed", transcript: transcript, seen: true);
        var failed = RunFile(Path.Combine(_folder.Path, "f"), "20261020-200000Z-0000000c", "Finished", 1, started, outcome: "TranscriptFailed", seen: false);
        var log = _folder.File("Dry run.log");
        File.WriteAllText(log, "20:00:01  Recording started\r\n20:00:02  WARNING  The class is silent\r\n20:00:03  ERROR  The upload failed\r\n20:00:04  Saved\r\n");

        var lines = await PowerShellAsync($$"""
            foreach ($path in @({{Quoted(good)}}, {{Quoted(inWindow)}}, {{Quoted(failed)}})) {
                $run = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
                $problems = @(Get-RunProblems -Run $run -Transcribe $true)
                "$($run.runId) $($problems.Count)"
                $problems | ForEach-Object { "  $_" }
            }
            Get-LogWarnings -Path {{Quoted(log)}} | ForEach-Object { "log $_" }
            "no log $(@(Get-LogWarnings -Path {{Quoted(_folder.File("none.log"))}}).Count)"
            """);

        Assert.That(lines[0], Is.EqualTo("20261020-200000Z-0000000a 0"));
        Assert.That(lines[1], Is.EqualTo("20261020-200000Z-0000000b 1"));
        Assert.That(lines[2], Does.Contain("in the window"));
        Assert.That(lines[3], Is.EqualTo("20261020-200000Z-0000000c 3"));
        Assert.That(lines.Skip(4).Take(3), Has.Some.Contains("TranscriptFailed").And.Some.Contains("no transcript").And.Some.Contains("not marked seen"));
        Assert.That(lines.Skip(7), Is.EqualTo(new[] { "log 20:00:02  WARNING  The class is silent", "log 20:00:03  ERROR  The upload failed", "no log 0" }));
    }

    /// <summary>
    /// The script reads the stand-in's own log, as the stand-in writes it: the requests for a transcript (with their terms),
    /// the reads of the account, and any request to a path the stand-in does not know.
    /// </summary>
    [Test]
    [Category("WindowsProcess")]
    public async Task The_script_reads_the_stand_ins_log()
    {
        var wav = _folder.File("upload.wav");
        TestAudio.WriteWav(wav, 16000, TestAudio.Tone(16000, seconds: 2, frequency: 440, amplitude: 0.2));
        var log = _folder.File("stand-in.log");
        await using (var server = StandInServer.Start(0, log, echo: false))
        {
            using var http = new HttpClient { BaseAddress = server.Address, Timeout = TimeSpan.FromSeconds(30) };
            using var form = new MultipartFormDataContent("b0und4ry")
            {
                { new StringContent("scribe_v2"), "model_id" },
                { new StringContent("kalam"), "keyterms" },
                { new StringContent("tawhid"), "keyterms" },
                { new StringContent("fiqh"), "keyterms" },
                { new ByteArrayContent(await File.ReadAllBytesAsync(wav)), "file", "audio.wav" }
            };
            using var answer = await http.PostAsync("v1/speech-to-text", form);
            using var account = await http.GetAsync("v1/user/subscription");
            using var stray = await http.PostAsync("v1/text-to-speech/x", new StringContent("{}"));
            Assert.That(((int)answer.StatusCode, (int)account.StatusCode, (int)stray.StatusCode), Is.EqualTo((200, 200, 404)));
        }

        var lines = await PowerShellAsync($$"""
            $summary = Read-StandInLog -Path {{Quoted(log)}}
            "transcripts $($summary.Transcripts) keyterms $($summary.Keyterms -join ',') accounts $($summary.Accounts) unexpected $(@($summary.Unexpected).Count)"
            $summary.Unexpected | ForEach-Object { "stray $_" }
            $none = Read-StandInLog -Path {{Quoted(_folder.File("none.log"))}}
            "none $($none.Transcripts) $($none.Accounts) $(@($none.Unexpected).Count)"
            """);

        Assert.That(lines[0], Is.EqualTo("transcripts 1 keyterms 3 accounts 1 unexpected 1"));
        Assert.That(lines[1], Does.StartWith("stray ").And.Contain("POST /v1/text-to-speech/x"));
        Assert.That(lines[2], Is.EqualTo("none 0 0 0"));
    }
}
