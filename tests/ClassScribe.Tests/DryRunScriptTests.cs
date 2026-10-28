using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using ClassScribe.DryRun;
using ClassScribe.Recorder;
using Microsoft.Extensions.Logging;

namespace ClassScribe.Tests;

/// <summary>
/// The dry-run kit's scripts (tools\dry-run): ASCII only, so that Windows PowerShell 5.1, which reads a script without a
/// byte-order mark in the ANSI code page, reads them as written; parsed by Windows PowerShell 5.1 itself; and the script's
/// own checks, dot-sourced into a hidden Windows PowerShell 5.1 of the test's own (which runs nothing of the dry run when
/// dot-sourced), over temporary folders only. The run files and log lines the checks read are made by ClassScribe's own
/// writers (<see cref="RecorderJson"/> and <see cref="RecorderLogProvider"/>), so a change to what the app writes fails
/// here rather than leaving the kit unable to find its run.
/// </summary>
[TestFixture]
public class DryRunScriptTests
{
    /// <summary>The side copy's exe, as the run files of a dry run name it.</summary>
    private const string SideExe = @"C:\Users\x\AppData\Local\Programs\ClassScribe-Dry\ClassScribe.exe";

    /// <summary>The installed copy's exe, as the run files of a real class name it.</summary>
    private const string InstalledExe = @"C:\Users\x\AppData\Local\Programs\ClassScribe\ClassScribe.exe";

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

    /// <summary>The id of the tests' run numbered <paramref name="number"/>: 20:00 UTC on 20 October 2026 and the number in 8 hexadecimal digits.</summary>
    private static string Id(int number) => $"20261020-200000Z-{number:x8}";

    /// <summary>The root of a drive letter that names no drive on this computer now, as an unplugged USB drive's does.</summary>
    private static string MissingDrive() => Enumerable.Range('D', 23).Select(letter => $"{(char)letter}:\\").Last(root => !Directory.Exists(root));

    /// <summary>
    /// Writes a run file as ClassScribe writes it, a <see cref="RunRecord"/> through <see cref="RecorderJson"/>, and returns
    /// its path. The run records <paramref name="name"/> from <paramref name="exe"/> in process <paramref name="processId"/>,
    /// which started at <paramref name="started"/>, as the run did; its last update is <paramref name="heartbeat"/>, or its
    /// start when none is given; its recording is <paramref name="baseName"/> in <paramref name="recordingFolder"/> once given.
    /// </summary>
    private static string RunFile(string folder, string runId, RunState state, int processId, DateTimeOffset started, RunHome home = RunHome.Separate,
        OutcomeKind? outcome = null, string? transcript = null, bool seen = false, string exe = SideExe, DateTimeOffset? heartbeat = null,
        string? recordingFolder = null, string? baseName = null, string name = "Dry run")
    {
        Directory.CreateDirectory(folder);
        var record = TestRuns.Record(runId, state, processId, started, name) with
        {
            ExePath = exe,
            Home = home,
            BaseName = baseName,
            Folder = recordingFolder,
            StartedUtc = started,
            HeartbeatUtc = heartbeat ?? started,
            Outcome = outcome is { } kind ? new RunOutcome(kind, kind == OutcomeKind.Transcribed ? 0 : 1, "m", transcript, recordingFolder, null) : null,
            Seen = seen
        };
        var path = Path.Combine(folder, runId + ".json");
        File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(record, RecorderJson.Default.RunRecord));
        return path;
    }

    /// <summary>
    /// Runs the whole dry run of the copy at a stand-in exe, with every folder in the temporary one and %LOCALAPPDATA%\ClassScribe
    /// at <paramref name="local"/>, and returns its output: the lines it wrote, then its exit code, whether its own folder was
    /// left and with how many files, and whether its process's environment is as it was.
    /// </summary>
    private async Task<string[]> DryRunOverAsync(string local)
    {
        var exe = _folder.File("ClassScribe.exe");
        File.WriteAllText(exe, "not run");
        // A folder in the temporary one, as a PowerShell string in single quotes.
        string R(string name) => Quoted(Path.Combine(_folder.Path, name));

        return await PowerShellAsync($$"""
            $Exe = {{Quoted(exe)}}
            $code = Invoke-DryRun -Roots @{ LocalAppData = {{Quoted(local)}}; AppData = {{R("AppData")}}; UserProfile = {{R("UserProfile")}}; Side = {{R("Side")}}; DryRuns = {{R("DryRuns")}}; Recordings = {{R("Recordings")}}; UserSettings = {{R("settings.json")}} }
            "exit code $code"
            "kit folder $(Test-Path -LiteralPath {{R("DryRuns")}} -PathType Container) $(@(Get-ChildItem -LiteralPath {{R("DryRuns")}} -Recurse -ErrorAction SilentlyContinue).Count)"
            "key $(if ($env:ELEVENLABS_API_KEY -eq $script:DummyKey) { 'dummy' } else { 'as it was' }), drive $(if ($env:Drive__Enabled) { $env:Drive__Enabled } else { 'unset' })"
            """);
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

    /// <summary>
    /// Windows PowerShell 5.1's own parser finds no error in any script of the kit, and every catch in them either throws or
    /// gives its reason in a comment that starts "Deliberately not rethrown:", as the convention asks of every catch.
    /// </summary>
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
                $ast = [System.Management.Automation.Language.Parser]::ParseFile($path, [ref]$tokens, [ref]$errors)
                foreach ($e in $errors) { "$path($($e.Extent.StartLineNumber)): $($e.Message)" }
                $catches = $ast.FindAll({ param($node) $node -is [System.Management.Automation.Language.CatchClauseAst] }, $true)
                foreach ($c in $catches) {
                    if ($c.Body.Extent.Text -notmatch '# Deliberately not rethrown: \w|\bthrow\b') { "$path($($c.Extent.StartLineNumber)): a catch that neither throws nor says why not" }
                }
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
    /// class alone in five minutes, a saved class and the camera in person; options given as one string through
    /// Start-DryRun.cmd (a comma list, settings joined by semicolons) split into their items; and the stand-in's command
    /// line, tied by --parent to the dry run's own process, so that it ends with the dry run even when its window is closed.
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
            "stand-in $(Get-StandInArguments -StubFile 'C:\kit\dry-run\ElevenLabsStub.cs' -Log 'C:\runs\a b\stand-in.log' -Ready 'C:\runs\a b\stand-in.ready' -Parent 4321)"
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
            "none 0",
            "stand-in run --no-build C:\\kit\\dry-run\\ElevenLabsStub.cs -- 0 \"C:\\runs\\a b\\stand-in.log\" --ready \"C:\\runs\\a b\\stand-in.ready\" --parent 4321"
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
        RunFile(runs, Id(1), RunState.Finished, self.Id, started, outcome: OutcomeKind.Transcribed, seen: true);
        RunFile(runs, Id(2), RunState.Recording, self.Id, started);
        RunFile(runs, Id(3), RunState.Transcribing, self.Id, started.AddYears(-3));
        RunFile(runs, Id(4), RunState.Stopping, 0, started);
        File.WriteAllText(Path.Combine(runs, Id(5) + ".json"), "not json {");

        var lines = await PowerShellAsync($$"""
            Get-LiveRuns -RunsFolder {{Quoted(runs)}} | ForEach-Object { "live $($_.runId) $($_.state)" }
            "none $(@(Get-LiveRuns -RunsFolder {{Quoted(Path.Combine(_folder.Path, "no runs"))}}).Count)"
            """);

        Assert.That(lines, Is.EqualTo(new[] { $"live {Id(2)} Recording", "none 0" }));
    }

    /// <summary>
    /// The runs a start of ClassScribe reports, which the side copy's own report would act on, are found as the app's
    /// RunView.NeedsReport finds them: an outcome nobody has seen (finished, or a recorder that failed), a recording whose
    /// process ended while it recorded or stopped, and a transcription whose process ended, each with why. Not: a run that
    /// was seen, one under way, a refused start, a start whose recorder died before it recorded, a finished run without an
    /// outcome, a file that cannot be read, and a recording on a drive that is not there now, which every start leaves alone
    /// until the drive is back. Here the test's own process plays a recorder that still runs.
    /// </summary>
    [Test]
    [Category("WindowsProcess")]
    public async Task The_dry_run_finds_the_runs_that_wait_for_their_report()
    {
        using var self = Process.GetCurrentProcess();
        var alive = new DateTimeOffset(self.StartTime);
        var gone = alive.AddYears(-3);
        var runs = Path.Combine(_folder.Path, "runs");
        var recording = Path.Combine(_folder.Path, "Risala", "2026-10-18");
        Directory.CreateDirectory(recording);
        var away = Path.Combine(MissingDrive(), "Classes", "Risala");
        RunFile(runs, Id(1), RunState.Finished, self.Id, alive, outcome: OutcomeKind.Transcribed, exe: InstalledExe);
        RunFile(runs, Id(2), RunState.Failed, 0, gone, outcome: OutcomeKind.RecorderFailed);
        RunFile(runs, Id(3), RunState.Recording, self.Id, gone, recordingFolder: recording, baseName: "Risala_2026-10-18_20-00-00Z", exe: InstalledExe);
        RunFile(runs, Id(4), RunState.Stopping, 0, alive);
        RunFile(runs, Id(5), RunState.Transcribing, 0, alive, recordingFolder: recording, baseName: "Risala_2026-10-18_20-00-00Z");
        RunFile(runs, Id(6), RunState.Finished, 0, alive, outcome: OutcomeKind.Transcribed, seen: true);
        RunFile(runs, Id(7), RunState.Recording, self.Id, alive);
        RunFile(runs, Id(8), RunState.Refused, 0, alive, outcome: OutcomeKind.Refused);
        RunFile(runs, Id(9), RunState.Starting, 0, alive);
        RunFile(runs, Id(10), RunState.Recording, 0, alive, seen: true);
        RunFile(runs, Id(11), RunState.Finished, 0, alive);
        RunFile(runs, Id(12), RunState.Recording, 0, alive, recordingFolder: away, baseName: "Risala_2026-10-18_20-00-00Z");
        File.WriteAllText(Path.Combine(runs, Id(13) + ".json"), "not json {");

        var lines = await PowerShellAsync($$"""
            Get-UnreportedRuns -RunsFolder {{Quoted(runs)}} | ForEach-Object { "$($_.Run.runId.Substring(17)) $($_.Run.state): $($_.Reason)" }
            "none $(@(Get-UnreportedRuns -RunsFolder {{Quoted(Path.Combine(_folder.Path, "no runs"))}}).Count)"
            """);

        Assert.That(lines, Is.EqualTo(new[]
        {
            "00000001 Finished: its outcome was never shown",
            "00000002 Failed: its outcome was never shown",
            "00000003 Recording: its recorder ended while it recorded",
            "00000004 Stopping: its recorder ended while it recorded",
            "00000005 Transcribing: its transcription was interrupted",
            "none 0"
        }));
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
        RunFile(Path.Combine(local, "runs"), Id(13), RunState.Recording, self.Id, new DateTimeOffset(self.StartTime));

        var lines = await DryRunOverAsync(local);

        Assert.That(lines, Has.Some.Contains($"A run is under way: {Id(13)}, Recording"));
        Assert.That(lines, Has.Some.EqualTo("Nothing was done."));
        Assert.That(lines.TakeLast(3), Is.EqualTo(new[] { "exit code 2", "kit folder True 0", "key as it was, drive unset" }));
        Assert.That(Directory.Exists(Path.Combine(_folder.Path, "Side")), Is.False);
    }

    /// <summary>
    /// The whole dry run refuses, and does nothing, while a real class's run waits for its report: the side copy's own
    /// report, which runs before its record command, would mark an unseen outcome seen, rewriting the class's run file, and
    /// would offer to continue or transcribe a recording that ended unexpectedly with the dry run's stand-in. The run is
    /// named with why it waits, Gulzar is told to answer the installed ClassScribe's report first, and the run file is
    /// left exactly as it was.
    /// </summary>
    [TestCase(RunState.Finished, OutcomeKind.Transcribed, "its outcome was never shown")]
    [TestCase(RunState.Recording, null, "its recorder ended while it recorded")]
    [TestCase(RunState.Transcribing, null, "its transcription was interrupted")]
    [Category("WindowsProcess")]
    public async Task A_dry_run_refuses_while_a_run_waits_for_its_report(RunState state, OutcomeKind? outcome, string reason)
    {
        var local = Path.Combine(_folder.Path, "LocalAppData");
        var file = RunFile(Path.Combine(local, "runs"), Id(14), state, 0, DateTimeOffset.UtcNow.AddHours(-3), outcome: outcome, exe: InstalledExe, name: "Risala");
        var bytes = File.ReadAllBytes(file);
        var written = File.GetLastWriteTimeUtc(file);

        var lines = await DryRunOverAsync(local);

        Assert.That(lines, Has.Some.EqualTo($"A run waits for its report: {Id(14)} (\"Risala\", {state}, from {InstalledExe}): {reason}."));
        Assert.That(lines, Has.Some.Contains(@"%LOCALAPPDATA%\Programs\ClassScribe\ClassScribe.exe").And.Some.Contains("start the dry run again"));
        Assert.That(lines, Has.Some.EqualTo("Nothing was done."));
        Assert.That(lines.TakeLast(3), Is.EqualTo(new[] { "exit code 2", "kit folder True 0", "key as it was, drive unset" }));
        Assert.That((File.ReadAllBytes(file), File.GetLastWriteTimeUtc(file)), Is.EqualTo((bytes, written)), "the class's run file is left as it was");
        Assert.That(Directory.Exists(Path.Combine(_folder.Path, "Side")), Is.False);
    }

    /// <summary>
    /// The dry run finds its own run among run files ClassScribe wrote: one added since the dry run began, by the copy it
    /// runs (its exe's path in any case), the newest when there are two; and from it, where the recording's files are.
    /// </summary>
    [Test]
    [Category("WindowsProcess")]
    public async Task The_dry_run_finds_its_own_run_file()
    {
        var runs = Path.Combine(_folder.Path, "runs");
        var started = DateTimeOffset.UtcNow.AddMinutes(-20);
        var recording = Path.Combine(_folder.Path, "ClassScribe dry runs", "Dry run", "2026-10-20");
        RunFile(runs, Id(0x31), RunState.Finished, 0, started, outcome: OutcomeKind.Transcribed, seen: true);
        RunFile(runs, Id(0x32), RunState.Recording, 0, started, exe: InstalledExe);
        RunFile(runs, Id(0x33), RunState.Finished, 0, started, exe: SideExe.ToUpperInvariant(), recordingFolder: recording, baseName: "Dry run_2026-10-20_20-00-00Z");
        RunFile(runs, Id(0x34), RunState.Transcribing, 0, started);

        var lines = await PowerShellAsync($$"""
            $runs = {{Quoted(runs)}}
            $exe = {{Quoted(SideExe)}}
            $newest = Find-DryRun -RunsFolder $runs -ExePath $exe -Known @('runs\{{Id(0x31)}}.json')
            "newest $($newest.runId) $($newest.state) $(if (Get-RecordingBase -Run $newest) { 'files' } else { 'no files yet' })"
            $other = Find-DryRun -RunsFolder $runs -ExePath $exe -Known @('runs\{{Id(0x31)}}.json', 'runs\{{Id(0x34)}}.json')
            "other $($other.runId) $($other.state) $(Get-RecordingBase -Run $other)"
            "none $(@(Find-DryRun -RunsFolder $runs -ExePath 'C:\elsewhere\ClassScribe.exe' -Known @()).Count)"
            """);

        Assert.That(lines, Is.EqualTo(new[]
        {
            $"newest {Id(0x34)} Transcribing no files yet",
            $"other {Id(0x33)} Finished {Path.Combine(recording, "Dry run_2026-10-20_20-00-00Z")}",
            "none 0"
        }));
    }

    /// <summary>
    /// The line the dry run shows while it waits: before the run file, while the run lasts, and once the run is over with
    /// ClassScribe processes of the copy still open, which are its windows: they are named by process id, with where to
    /// look, the minimized window the recorder brought back by itself among them.
    /// </summary>
    [Test]
    [Category("WindowsProcess")]
    public async Task The_wait_names_the_windows_left_open()
    {
        var started = DateTimeOffset.UtcNow.AddMinutes(-20);
        var recording = RunFile(_folder.Path, Id(0x41), RunState.Recording, 0, started);
        var finished = RunFile(Path.Combine(_folder.Path, "f"), Id(0x42), RunState.Finished, 0, started, outcome: OutcomeKind.Transcribed, seen: true);

        var lines = await PowerShellAsync($$"""
            $recording = Get-Content -LiteralPath {{Quoted(recording)}} -Raw | ConvertFrom-Json
            $finished = Get-Content -LiteralPath {{Quoted(finished)}} -Raw | ConvertFrom-Json
            Get-WaitStatus -Run $null -ProcessIds @()
            Get-WaitStatus -Run $recording -ProcessIds @(1234, 5678)
            Get-WaitStatus -Run $finished -ProcessIds @(1234)
            Get-WaitStatus -Run $finished -ProcessIds @(1234, 5678)
            """);

        Assert.That(lines, Is.EqualTo(new[]
        {
            "Run: not started; ClassScribe processes of this copy: 0",
            "Run: Recording; ClassScribe processes of this copy: 2",
            "Run: Finished; still open: ClassScribe process 1234, a window of the dry run (the minimized one that came back by itself on the taskbar, perhaps): press Enter in it, or Q in its menu, or close it with its X",
            "Run: Finished; still open: ClassScribe processes 1234, 5678, windows of the dry run (the minimized one that came back by itself on the taskbar, perhaps): press Enter in each, or Q in its menu, or close it with its X"
        }));
    }

    /// <summary>
    /// Of the changes to %LOCALAPPDATA%\ClassScribe, the dry run expects only its own run file, the recorder's log, the
    /// lock file, and the files of the runs folder a start of ClassScribe prunes going: a run file whose last update (its
    /// heartbeat, read before the dry run, which marking a run seen does not change) is more than 30 days old, however
    /// recently the file was written; and by the file's time, a request file, a file the app cannot read, or one not read
    /// before. Anything else there, and any change at all to the other two folders, is named as unexpected.
    /// </summary>
    [Test]
    [Category("WindowsProcess")]
    public async Task The_dry_run_tells_the_changes_it_expects_from_the_others()
    {
        var runs = Path.Combine(_folder.Path, "runs");
        var now = DateTimeOffset.UtcNow;
        RunFile(runs, Id(0x21), RunState.Finished, 0, now.AddDays(-45), outcome: OutcomeKind.Transcribed, seen: true, heartbeat: now.AddDays(-40));
        RunFile(runs, Id(0x22), RunState.Finished, 0, now.AddDays(-2), outcome: OutcomeKind.Transcribed, seen: true);
        File.WriteAllText(Path.Combine(runs, Id(0x23) + ".json"), "not json {");
        File.WriteAllText(Path.Combine(runs, Id(0x24) + ".request.json"), "{}");

        var lines = await PowerShellAsync($$"""
            $heartbeats = Get-RunHeartbeats -RunsFolder {{Quoted(runs)}}
            "heartbeats $(@($heartbeats.Keys | Sort-Object | ForEach-Object { '{0}={1}' -f $_, [int]((Get-Date).ToUniversalTime() - $heartbeats[$_]).TotalDays }) -join ' ')"
            function C($change, $path, $before = (Get-Date).ToUniversalTime().ToString('o')) { [pscustomobject]@{ Change = $change; Path = $path; Before = $before } }
            $changes = @(
                (C 'New' 'runs\20261020-200000Z-0000000a.json'),
                (C 'Changed' 'recorder.log'),
                (C 'New' 'recorder.old.log'),
                (C 'Changed' 'recording.lock'),
                (C 'Removed' 'runs\20200101-200000Z-00000001.json' '2020-01-01T20:00:10.0000000Z'),
                (C 'New' 'runs\20261020-200000Z-0000000b.json'),
                (C 'New' 'drive\recordings\Dry run.json'),
                (C 'Removed' 'runs\20261020-200000Z-0000000a.json'),
                (C 'Removed' 'runs\{{Id(0x21)}}.json'),
                (C 'Removed' 'runs\{{Id(0x22)}}.json' '2020-01-01T20:00:10.0000000Z'),
                (C 'Removed' 'runs\{{Id(0x23)}}.json' '2020-01-01T20:00:10.0000000Z'),
                (C 'Removed' 'runs\{{Id(0x24)}}.request.json' '2020-01-01T20:00:10.0000000Z'),
                (C 'Removed' 'runs\{{Id(0x25)}}.request.json')
            )
            Get-UnexpectedChanges -Changes $changes -Area 'LocalAppData' -RunId '20261020-200000Z-0000000a' -Heartbeats $heartbeats | ForEach-Object { "local $($_.Change) $($_.Path)" }
            Get-UnexpectedChanges -Changes @((C 'Changed' 'google-drive.dat')) -Area 'AppData' -RunId '20261020-200000Z-0000000a' | ForEach-Object { "roaming $($_.Change) $($_.Path)" }
            "nothing $(@(Get-UnexpectedChanges -Changes @() -Area 'UserProfile' -RunId 'x').Count)"
            """);

        Assert.That(lines, Is.EqualTo(new[]
        {
            $"heartbeats runs\\{Id(0x21)}.json=40 runs\\{Id(0x22)}.json=2",
            "local New runs\\20261020-200000Z-0000000b.json",
            "local New drive\\recordings\\Dry run.json",
            "local Removed runs\\20261020-200000Z-0000000a.json",
            $"local Removed runs\\{Id(0x22)}.json",
            $"local Removed runs\\{Id(0x25)}.request.json",
            "roaming Changed google-drive.dat",
            "nothing 0"
        }));
    }

    /// <summary>
    /// The run's verdict from its run file and its recording's log: a run that recorded in the separate recorder, was
    /// transcribed, saved its transcript and was seen passes; a run in the window, one without a transcript, and one never
    /// seen each fail with the reason; the log's warning and error lines, as the recorder's own log writes them, are named,
    /// and its errors told from its warnings.
    /// </summary>
    [Test]
    [Category("WindowsProcess")]
    public async Task The_dry_run_checks_the_run_file_and_the_log()
    {
        var started = DateTimeOffset.UtcNow.AddMinutes(-20);
        var transcript = _folder.File("Dry run.md");
        File.WriteAllText(transcript, "# Dry run");
        var good = RunFile(_folder.Path, Id(10), RunState.Finished, 1, started, outcome: OutcomeKind.Transcribed, transcript: transcript, seen: true);
        var inWindow = RunFile(Path.Combine(_folder.Path, "w"), Id(11), RunState.Finished, 1, started, home: RunHome.InWindow, outcome: OutcomeKind.Transcribed, transcript: transcript, seen: true);
        var failed = RunFile(Path.Combine(_folder.Path, "f"), Id(12), RunState.Finished, 1, started, outcome: OutcomeKind.TranscriptFailed, seen: false);
        var log = _folder.File("Dry run.log");
        var warning = RecorderLogProvider.Text(LogLevel.Warning, "The class is silent");
        var error = RecorderLogProvider.Text(LogLevel.Error, "The upload failed", new IOException("The disk is full"));
        File.WriteAllText(log, string.Join(Environment.NewLine,
            RecorderLogProvider.Text(LogLevel.Information, "Recording started"), warning, error, RecorderLogProvider.Text(LogLevel.Information, "Transcript saved")) + Environment.NewLine);

        var lines = await PowerShellAsync($$"""
            foreach ($path in @({{Quoted(good)}}, {{Quoted(inWindow)}}, {{Quoted(failed)}})) {
                $run = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
                $problems = @(Get-RunProblems -Run $run -Transcribe $true)
                "$($run.runId) $($problems.Count)"
                $problems | ForEach-Object { "  $_" }
            }
            $warnings = @(Get-LogWarnings -Path {{Quoted(log)}})
            $warnings | ForEach-Object { "log $_" }
            Select-LogErrors -Lines $warnings | ForEach-Object { "error $_" }
            "no log $(@(Get-LogWarnings -Path {{Quoted(_folder.File("none.log"))}}).Count)"
            """);

        Assert.That(lines[0], Is.EqualTo($"{Id(10)} 0"));
        Assert.That(lines[1], Is.EqualTo($"{Id(11)} 1"));
        Assert.That(lines[2], Does.Contain("in the window"));
        Assert.That(lines[3], Is.EqualTo($"{Id(12)} 3"));
        Assert.That(lines.Skip(4).Take(3), Has.Some.Contains("TranscriptFailed").And.Some.Contains("no transcript").And.Some.Contains("not marked seen"));
        var errorLine = error.Split(Environment.NewLine)[0];
        Assert.That(lines.Skip(7), Is.EqualTo(new[] { "log " + warning, "log " + errorLine, "error " + errorLine, "no log 0" }));
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

    /// <summary>
    /// The kit copies into its own settings folder each glossary the seeded settings name by a relative path, from the user's
    /// settings folder, as a class takes it (architecture 8.7): ElevenLabs:KeytermsFile and each saved class's, a -Setting
    /// override among them; one named with %APPDATA% or a full path is read where it is, none is nothing to copy, and one
    /// that is not in the user's folder is said. Only temporary folders are touched.
    /// </summary>
    [Test]
    [Category("WindowsProcess")]
    public async Task The_kit_copies_the_glossaries_the_settings_name_by_a_relative_path()
    {
        var user = Path.Combine(_folder.Path, "AppData");
        var kit = Path.Combine(_folder.Path, "kit settings");
        Directory.CreateDirectory(Path.Combine(user, "glossaries"));
        Directory.CreateDirectory(kit);
        File.WriteAllText(Path.Combine(user, "keyterms-islamic-sciences.txt"), "kalam");
        File.WriteAllText(Path.Combine(user, "glossaries", "mantiq.txt"), "mantiq");
        File.WriteAllText(Path.Combine(kit, "appsettings.json"), """
            {
              "ElevenLabs": { "KeytermsFile": "keyterms-islamic-sciences.txt" },
              "Classes": {
                "Mantiq (Chrome)": { "Course": "Mantiq", "App": "chrome", "KeytermsFile": "glossaries\\mantiq.txt" },
                "Fiqh": { "Course": "Fiqh", "App": "chrome", "KeytermsFile": "%APPDATA%\\ClassScribe\\fiqh.txt" },
                "Kalam": { "Course": "Kalam", "App": "chrome", "KeytermsFile": "none" },
                "Aqidah": { "Course": "Aqidah", "App": "chrome" }
              }
            }
            """);

        var lines = await PowerShellAsync($$"""
            $glossaries = @(Get-GlossarySettings -SettingsFile {{Quoted(Path.Combine(kit, "appsettings.json"))}} -Overrides @('Recording:FlushSeconds=5', 'classes:kalam:keytermsfile=gone.txt'))
            $glossaries | ForEach-Object { "setting $($_.Key)=$($_.Value) [$($_.Course)]" }
            Copy-RelativeGlossaries -Glossaries $glossaries -From {{Quoted(user)}} -To {{Quoted(kit)}} | ForEach-Object { "$($_.Copied) $($_.Line)" }
            "named $(@($glossaries | Where-Object { -not (Test-NoGlossary $_.Value) }).Count)"
            "none $(@(Get-GlossarySettings -SettingsFile {{Quoted(Path.Combine(_folder.Path, "no settings.json"))}}).Count)"
            """);

        Assert.That(lines, Is.EqualTo(new[]
        {
            "setting ElevenLabs:KeytermsFile=keyterms-islamic-sciences.txt []",
            "setting Classes:Mantiq (Chrome):KeytermsFile=glossaries\\mantiq.txt [Mantiq]",
            "setting Classes:Fiqh:KeytermsFile=%APPDATA%\\ClassScribe\\fiqh.txt [Fiqh]",
            "setting Classes:Kalam:KeytermsFile=gone.txt [Kalam]",
            $"True Copied keyterms-islamic-sciences.txt, which ElevenLabs:KeytermsFile names, from {user} into the dry run's settings folder.",
            $"True Copied glossaries\\mantiq.txt, which Classes:Mantiq (Chrome):KeytermsFile names, from {user} into the dry run's settings folder.",
            "False Classes:Fiqh:KeytermsFile names %APPDATA%\\ClassScribe\\fiqh.txt, which the dry run reads where it is.",
            $"False Classes:Kalam:KeytermsFile names gone.txt, which is not in {user}, so the dry run has no such glossary either.",
            "named 4",
            "none 0"
        }));
        Assert.That(File.ReadAllText(Path.Combine(kit, "glossaries", "mantiq.txt")), Is.EqualTo("mantiq"));
        Assert.That(File.ReadAllText(Path.Combine(kit, "keyterms-islamic-sciences.txt")), Is.EqualTo("kalam"));
    }

    /// <summary>
    /// The kit counts a glossary's terms as ClassScribe counts those of a glossary whose terms are all valid: comments and
    /// blank lines passed over, a term repeated in any case or spelling composed differently counted once; no file is none.
    /// The lines of a recording's log that say which glossary the run took are found, as the recorder's own log writes them.
    /// </summary>
    [Test]
    [Category("WindowsProcess")]
    public async Task The_kit_counts_a_glossarys_terms_and_finds_the_runs_glossary_lines()
    {
        var glossary = _folder.File("terms.txt");
        File.WriteAllText(glossary, "# terms\r\n\r\nkal\u0101m\r\n  Kal\u0101m  \r\nkala\u0304m\r\nmanti\u1E6Dq\r\n", new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        var log = _folder.File("Dry run.log");
        File.WriteAllText(log, string.Join(Environment.NewLine,
            RecorderLogProvider.Text(LogLevel.Information, "Recording \"Dry run\" to D:\\x"),
            RecorderLogProvider.Text(LogLevel.Information, "Glossary: keyterms-islamic-sciences.txt (ElevenLabs:KeytermsFile), 94 terms"),
            RecorderLogProvider.Text(LogLevel.Information, "Glossary: keyterms-islamic-sciences.txt, as decided when this recording started (94 terms then, 94 now)"),
            RecorderLogProvider.Text(LogLevel.Information, "Transcript saved to D:\\x.md")) + Environment.NewLine, new UTF8Encoding(false));

        var lines = await PowerShellAsync($$"""
            "terms $(Get-GlossaryTermCount -Path {{Quoted(glossary)}})"
            "missing $(if ($null -eq (Get-GlossaryTermCount -Path {{Quoted(_folder.File("gone.txt"))}})) { 'null' } else { 'a number' })"
            Get-GlossaryLines -Path {{Quoted(log)}} | ForEach-Object { "line $($_.Substring($_.IndexOf('Glossary')))" }
            """);

        Assert.That(lines, Is.EqualTo(new[]
        {
            "terms 2",
            "missing null",
            "line Glossary: keyterms-islamic-sciences.txt (ElevenLabs:KeytermsFile), 94 terms",
            "line Glossary: keyterms-islamic-sciences.txt, as decided when this recording started (94 terms then, 94 now)"
        }));
    }

    /// <summary>
    /// The kit expects keyterms only when the seeded settings name a glossary (architecture 8.7): with none named, keyterms:
    /// none is a note, not a problem; with one named, the stand-in's count must equal the recording's .glossary.json entry and
    /// the glossary's own terms, and a run that decided no glossary, as a class that says none, is a note; a build that writes
    /// no record is checked as before, by the stand-in's count alone.
    /// </summary>
    [Test]
    [Category("WindowsProcess")]
    public async Task The_kit_expects_keyterms_only_when_the_settings_name_a_glossary()
    {
        var lines = await PowerShellAsync("""
            function Show($label, $findings) { "$label problems: $(@($findings.Problems) -join ' | ')"; "$label notes: $(@($findings.Notes) -join ' | ')" }
            function Record($path, $terms) { [pscustomobject]@{ decided = [pscustomobject]@{ setting = 'x.txt'; path = $path; from = 'ElevenLabs:KeytermsFile'; terms = $terms }; sent = @([pscustomobject]@{ copy = 1; path = $path; terms = $terms }) } }
            Show 'none named' (Get-GlossaryFindings -Named $false -StandInTerms @(0) -Record $null -FileTerms $null)
            Show 'old build' (Get-GlossaryFindings -Named $false -StandInTerms @(94) -Record $null -FileTerms $null)
            Show 'good' (Get-GlossaryFindings -Named $true -StandInTerms @(94) -Record (Record 'C:\g.txt' 94) -FileTerms 94)
            Show 'short' (Get-GlossaryFindings -Named $true -StandInTerms @(90) -Record (Record 'C:\g.txt' 94) -FileTerms 97)
            Show 'class none' (Get-GlossaryFindings -Named $true -StandInTerms @(0) -Record (Record '' 0) -FileTerms $null)
            Show 'no record' (Get-GlossaryFindings -Named $true -StandInTerms @(0) -Record $null -FileTerms $null)
            Show 'no transcript' (Get-GlossaryFindings -Named $true -StandInTerms @() -Record $null -FileTerms $null)
            """);

        Assert.That(lines, Is.EqualTo(new[]
        {
            "none named problems: ",
            "none named notes: keyterms: none, as the seeded settings name no glossary",
            "old build problems: ",
            "old build notes: The stand-in counted 94 keyterms though the seeded settings name no glossary (a build that ships one)",
            "good problems: ",
            "good notes: ",
            "short problems: The stand-in counted 90 keyterms, but the recording's .glossary.json says 94 went up. | C:\\g.txt holds 97 terms, but 94 went up.",
            "short notes: ",
            "class none problems: ",
            "class none notes: The run took no glossary: ElevenLabs:KeytermsFile says none",
            "no record problems: No keyterms went up with the transcript, though the seeded settings name a glossary.",
            "no record notes: This build writes no .glossary.json beside the recording",
            "no transcript problems: ",
            "no transcript notes: "
        }));
    }
}