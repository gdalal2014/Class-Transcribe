using System.Text.Json;
using ClassScribe.Recorder;
using ClassScribe.Runs;
using ClassScribe.Settings;
using ClassScribe.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClassScribe.Tests;

/// <summary>
/// The contracts of the ports of runs (architecture 4.3, slice 3b2) over their real adapters, in temporary folders that stand
/// for %LOCALAPPDATA%\ClassScribe: ClassScribe's own folder (<see cref="IRuntimeFolder"/>), one recording at a time
/// (<see cref="IRecordingLock"/>), the run and request files (<see cref="IRunJournal"/>) and one run's file while the run
/// lasts (<see cref="IRunHandle"/>), written whole as RecorderJson writes it and with the journal's retries, and the run's
/// log (<see cref="IRunLog"/>: the copy into the recording's log, and the start-up log's last lines); and that the window's
/// host and the recorder's resolve each port to the very adapter they hold.
/// </summary>
[TestFixture]
public class RecorderPortTests
{
    private TempFolder _temp = null!;
    private TestRuntime _runtime = null!;

    /// <summary>A temporary folder of the test's own, and ClassScribe's own folder in another.</summary>
    [SetUp]
    public void SetUp()
    {
        _temp = new TempFolder();
        _runtime = new TestRuntime();
    }

    /// <summary>Deletes the folders.</summary>
    [TearDown]
    public void TearDown()
    {
        _runtime.Dispose();
        _temp.Dispose();
    }

    /// <summary>The lock port over the adapter at <paramref name="folder"/>, which notes nothing and waits for nothing before a new try.</summary>
    private static IRecordingLock Lock(RuntimeFolder folder) => new RecordingLock(folder, NullLogger<RecordingLock>.Instance, _ => { });

    /// <summary>The folder port names its root as a full path, its runs folder and its lock file, makes nothing until asked, and makes both folders when asked whether it can be written.</summary>
    [Test]
    public void The_folder_port_names_its_paths_and_makes_them_only_when_asked_whether_it_can_be_written()
    {
        var root = Path.Combine(_temp.Path, "ClassScribe");
        IRuntimeFolder folder = new RuntimeFolder(Path.Combine(_temp.Path, "Elsewhere", "..", "ClassScribe"));

        Assert.Multiple(() =>
        {
            Assert.That(folder.Root, Is.EqualTo(root), "a full path, with .. resolved");
            Assert.That(folder.Runs, Is.EqualTo(Path.Combine(root, "runs")));
            Assert.That(folder.LockFile, Is.EqualTo(Path.Combine(root, "recording.lock")));
            Assert.That(Directory.Exists(root), Is.False, "nothing is made until it is needed");
        });

        var problem = folder.DescribeWriteProblem();

        Assert.Multiple(() =>
        {
            Assert.That(problem, Is.Null);
            Assert.That(folder.CanWrite(), Is.True);
            Assert.That(Directory.Exists(folder.Runs), Is.True, "the check makes the folder and its runs folder");
            Assert.That(Directory.GetFiles(root), Is.Empty, "the probe files are gone");
            Assert.That(Directory.GetFiles(folder.Runs), Is.Empty);
        });
    }

    /// <summary>A folder that cannot be made, because a file stands where it should be, cannot be written, and the problem names the folder and why.</summary>
    [Test]
    public void The_folder_port_says_what_is_wrong_when_it_cannot_be_written()
    {
        var blocker = _temp.File("blocker");
        File.WriteAllText(blocker, "a file where the folder should be");
        IRuntimeFolder folder = new RuntimeFolder(Path.Combine(blocker, "ClassScribe"));

        Assert.Multiple(() =>
        {
            Assert.That(folder.CanWrite(), Is.False);
            Assert.That(folder.DescribeWriteProblem(), Does.StartWith($"{folder.Root} cannot be written: ").And.Not.EndWith(": "));
        });
    }

    /// <summary>
    /// The lock port takes the lock, making the folder and the lock file; while it is held another take gives null and a
    /// look says it is held; letting go, once or twice, frees it for the next take, and before the first take nothing holds it.
    /// </summary>
    [Test]
    public void The_lock_port_takes_holds_and_lets_go()
    {
        var folder = new RuntimeFolder(_temp.File("ClassScribe"));
        var first = Lock(folder);
        var second = Lock(folder);

        var heldBefore = second.IsHeld();
        var held = first.TryTake();
        var whileHeld = (second.IsHeld(), second.TryTake());
        held?.Dispose();
        held?.Dispose();
        var heldAfter = second.IsHeld();
        using var again = second.TryTake();

        Assert.Multiple(() =>
        {
            Assert.That(heldBefore, Is.False, "no lock file yet: nobody has ever held it");
            Assert.That(held, Is.Not.Null);
            Assert.That(File.Exists(folder.LockFile), Is.True, "the folder and the lock file were made");
            Assert.That(whileHeld, Is.EqualTo((true, (IDisposable?)null)), "held by the first");
            Assert.That(heldAfter, Is.False);
            Assert.That(again, Is.Not.Null, "free once let go; letting go twice does nothing more");
        });
    }

    /// <summary>A lock whose folder cannot be made throws from a take, as the port says: it says nothing about another recording.</summary>
    [Test]
    public void The_lock_port_throws_when_its_folder_cannot_be_made()
    {
        var blocker = _temp.File("blocker");
        File.WriteAllText(blocker, "a file where the folder should be");
        var refused = Lock(new RuntimeFolder(Path.Combine(blocker, "ClassScribe")));

        Assert.That(() => refused.TryTake(), Throws.InstanceOf<IOException>());
    }

    /// <summary>The journal port over ClassScribe's own folder in the test's, which sleeps for real before it tries a file again.</summary>
    private IRunJournal Journal => _runtime.Journal;

    /// <summary>
    /// The journal port hands a request over with its settings and takes it once; begins a run's file, as a handle whose path
    /// is the run file's, written as the record given in this build's layout with the heartbeat's time now, and as
    /// RecorderJson writes it, byte for byte; and finds and lists the run with whether its process runs.
    /// </summary>
    [Test]
    public void The_journal_port_hands_a_request_over_and_begins_finds_and_lists_a_run()
    {
        var runId = TestRuns.Id();
        var request = TestRuns.Request(runId);
        var settings = new Dictionary<string, string?> { ["Recording:OutputFolder"] = @"D:\Classes", ["Logging:LogLevel:Default"] = null };
        _runtime.Processes.Start(TestRuns.ProcessId, TestRuns.ProcessStart);

        Journal.WriteRequest(request, settings);
        var requestWritten = File.Exists(Journal.RequestFile(runId));
        var taken = Journal.TakeRequest(runId);
        var requestLeft = File.Exists(Journal.RequestFile(runId));
        using var run = Journal.Begin(TestRuns.Record(runId) with { Version = 0, HeartbeatUtc = TestRuns.Start.AddDays(-1) });
        var found = Journal.Find(runId);
        var listed = Journal.List();

        Assert.Multiple(() =>
        {
            Assert.That(requestWritten, Is.True, "written before the recorder starts");
            Assert.That(taken?.Request, Is.EqualTo(request));
            Assert.That(taken?.Settings, Is.EqualTo(settings), "the settings as given, a setting without a value included");
            Assert.That(requestLeft, Is.False, "taken once");
            Assert.That(Journal.TakeRequest(runId), Is.Null, "nothing to take twice");
            Assert.That(run.FilePath, Is.EqualTo(Journal.RunFile(runId)).And.EqualTo(Path.Combine(_runtime.Folder.Runs, $"{runId}.json")));
            Assert.That(run.Current, Is.EqualTo(TestRuns.Record(runId) with { Version = RunRecord.CurrentVersion, HeartbeatUtc = TestRuns.Start }), "this build's layout, the heartbeat now");
            Assert.That(File.ReadAllBytes(run.FilePath), Is.EqualTo(JsonSerializer.SerializeToUtf8Bytes(run.Current, RecorderJson.Default.RunRecord)), "the file holds RecorderJson's bytes, whole");
            Assert.That(found, Is.Not.Null);
            Assert.That(JsonSerializer.SerializeToUtf8Bytes(found!.Record, RecorderJson.Default.RunRecord), Is.EqualTo(File.ReadAllBytes(run.FilePath)), "read back as written");
            Assert.That(found.IsAlive, Is.True, "its process runs");
            Assert.That(listed.Select(view => view.Record.RunId), Is.EqualTo(new[] { runId }));
            Assert.That(() => Journal.RunFile("not-a-run-id"), Throws.ArgumentException);
        });
    }

    /// <summary>
    /// Each change through the handle port is written whole and read back through the journal port: recording into files, the
    /// heartbeat with the upload's percent, what ended the recording and its withdrawal, the state, and the outcome, after
    /// which the run is marked seen (not before it is over, through MarkSeenIfOver) and pruned once it is old and its process
    /// gone; a last state is asked for only with the outcome.
    /// </summary>
    [Test]
    public void The_handle_port_writes_each_change_and_the_journal_port_marks_and_prunes_the_run()
    {
        var runId = TestRuns.Id();
        var files = new RecordingFiles(@"D:\Classes\Kalam", "Kalam_2026-10-15_23-00-02Z");
        _runtime.Processes.Start(TestRuns.ProcessId, TestRuns.ProcessStart);
        using var run = Journal.Begin(TestRuns.Record(runId));

        run.Recording(files);
        var recording = Journal.Find(runId)!.Record;
        _runtime.Time.Advance(TimeSpan.FromSeconds(10));
        run.Heartbeat(TimeSpan.FromSeconds(10), uploadPercent: 40);
        var beat = Journal.Find(runId)!.Record;
        run.SetEndedBy(RunEndCause.Windows);
        var windows = Journal.Find(runId)!.Record.EndedBy;
        run.WithdrawEndedBy(RunEndCause.Windows);
        var withdrawn = Journal.Find(runId)!.Record.EndedBy;
        run.SetState(RunState.Stopping);
        run.SetEndedBy(RunEndCause.Q);
        Journal.MarkSeenIfOver(runId);
        var stopping = Journal.Find(runId)!.Record;
        run.End(new RunOutcome(OutcomeKind.Transcribed, 0, "Transcript saved.", files.Transcript(1), files.Folder, RetryCommand: null));
        var ended = Journal.Find(runId)!.Record;
        Journal.MarkSeen(runId);
        var seen = Journal.Find(runId)!.Record.Seen;
        _runtime.Processes.End(TestRuns.ProcessId);
        _runtime.Time.Advance(RunTimings.KeepSeenRunsFor + TimeSpan.FromMinutes(1));
        Journal.Prune(RunTimings.KeepSeenRunsFor);

        Assert.Multiple(() =>
        {
            Assert.That((recording.State, recording.BaseName, recording.Folder), Is.EqualTo((RunState.Recording, files.BaseName, files.Folder)));
            Assert.That((beat.Position, beat.UploadPercent, beat.HeartbeatUtc), Is.EqualTo((TimeSpan.FromSeconds(10), (int?)40, TestRuns.Start.AddSeconds(10))));
            Assert.That(windows, Is.EqualTo(RunEndCause.Windows));
            Assert.That(withdrawn, Is.Null, "the end that did not come is taken back");
            Assert.That((stopping.State, stopping.EndedBy, stopping.Seen), Is.EqualTo((RunState.Stopping, RunEndCause.Q, false)), "not seen while it is not over");
            Assert.That((ended.State, ended.Outcome?.Kind, ended.UploadPercent), Is.EqualTo((RunState.Finished, (OutcomeKind?)OutcomeKind.Transcribed, (int?)null)));
            Assert.That(seen, Is.True);
            Assert.That(Journal.Find(runId), Is.Null, "pruned: seen, its process gone, older than the limit");
            Assert.That(() => run.SetState(RunState.Finished), Throws.ArgumentException, "the last state comes only with the outcome");
        });
    }

    /// <summary>
    /// A write through the handle port waits out a program that holds the run file for a moment, with the journal's retries:
    /// the file is written whole once it is free, and no failure is reported.
    /// </summary>
    [Test]
    public void The_handle_port_waits_out_a_program_that_holds_the_run_file_for_a_moment()
    {
        var runId = TestRuns.Id();
        FileStream? holder = null;
        var pauses = new List<TimeSpan>();
        IRunJournal journal = _runtime.JournalPausingWith(pause =>
        {
            pauses.Add(pause);
            holder?.Dispose();
            holder = null;
        });
        try
        {
            using var run = journal.Begin(TestRuns.Record(runId));
            holder = new FileStream(run.FilePath, FileMode.Open, FileAccess.Read, FileShare.None);

            run.SetState(RunState.Recording);

            Assert.Multiple(() =>
            {
                Assert.That(pauses, Is.EqualTo(new[] { TimeSpan.FromMilliseconds(100) }), "one pause, after which the holder had let go");
                Assert.That(journal.Find(runId)?.Record.State, Is.EqualTo(RunState.Recording), "written whole once the file was free");
                Assert.That(_runtime.Log.Problems, Is.Empty, "no failure to report");
            });
        }
        finally
        {
            holder?.Dispose();
        }
    }

    /// <summary>
    /// The log port takes lines without waiting, copies the lines since the mark into the recording's log once it is named,
    /// names it while it lasts, and sends the lines after its end nowhere, with nothing lost and no file failure.
    /// </summary>
    [Test]
    public void The_log_port_copies_the_lines_since_the_mark_into_the_recordings_log()
    {
        using var adapter = new RecorderLog(_runtime.Time);
        IRunLog log = adapter;
        var path = _temp.File("Kalam_2026-10-15_23-00-02Z.log");

        log.Write(LogLevel.Information, "before the run");
        log.KeepFromNow();
        log.Write(LogLevel.Information, "the run's first line");
        log.WriteFileTo(path);
        var named = log.NamedFile;
        log.Write(LogLevel.Warning, "a line while named");
        var flushed = log.Flush(TimeSpan.FromSeconds(10));
        log.EndFile();
        log.Write(LogLevel.Information, "after the end");
        log.Flush(TimeSpan.FromSeconds(10));

        Assert.Multiple(() =>
        {
            Assert.That(named, Is.EqualTo(path));
            Assert.That(log.NamedFile, Is.Null, "no recording's log is named after its end");
            Assert.That(flushed, Is.True);
            Assert.That(File.ReadAllText(path), Is.EqualTo($"the run's first line{Environment.NewLine}a line while named{Environment.NewLine}"), "the lines from the mark on, and none after the end");
            Assert.That(log.LostFileLines, Is.EqualTo(0), "a line with no file to go to is held in memory, not lost");
            Assert.That(log.FirstFileFailure, Is.Null);
        });
    }

    /// <summary>
    /// The log port reads the last lines of a recorder's start-up log for a window: the last so many that are not empty,
    /// oldest first, only when the file was written since the recorder started; nothing for a file written before that or
    /// not there, or when no lines are asked for; and a file it cannot read throws.
    /// </summary>
    [Test]
    public void The_log_port_reads_the_last_lines_of_a_startup_log_written_since_a_time()
    {
        var startup = Path.Combine(_runtime.Folder.Root, IRunLog.StartupFileName);
        using (var recorder = new RecorderLog(_runtime.Time, startup))
        {
            foreach (var line in new[] { "one", "", "two", "three", "four", "", "five", "six", "seven" }) recorder.Write(LogLevel.Information, line);
            recorder.Flush(TimeSpan.FromSeconds(10));
        }

        var written = new DateTimeOffset(File.GetLastWriteTimeUtc(startup), TimeSpan.Zero);
        var held = _temp.File("held.log");
        File.WriteAllText(held, "a line");
        using var holder = new FileStream(held, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using var adapter = new RecorderLog(_runtime.Time);
        IRunLog window = adapter;

        Assert.Multiple(() =>
        {
            Assert.That(IRunLog.StartupFileName, Is.EqualTo("recorder.log"));
            Assert.That(window.LastStartupLines(startup, written - TimeSpan.FromSeconds(2), 5), Is.EqualTo(new[] { "three", "four", "five", "six", "seven" }), "the last five that are not empty");
            Assert.That(window.LastStartupLines(startup, written + TimeSpan.FromSeconds(1), 5), Is.Empty, "written before the recorder started");
            Assert.That(window.LastStartupLines(_temp.File("none.log"), DateTimeOffset.MinValue, 5), Is.Empty, "no such file");
            Assert.That(window.LastStartupLines(startup, written - TimeSpan.FromSeconds(2), 0), Is.Empty, "no lines asked for");
            Assert.That(window.LastStartupLines(startup, written - TimeSpan.FromSeconds(2), -1), Is.Empty, "fewer than none asked for");
            Assert.That(() => window.LastStartupLines(held, DateTimeOffset.MinValue, 5), Throws.InstanceOf<IOException>(), "a file another program holds without sharing");
        });
    }

    /// <summary>
    /// The log a recorder's host is given belongs to the host that made it (RecorderHost.RunAsync's using), which disposes
    /// it after the container, so that what is logged while the container is disposed, and a failure after it, still reach
    /// recorder.log: the container never disposes it, whether or not the port was asked for in a scope, as every run asks
    /// for it.
    /// </summary>
    [TestCase(true)]
    [TestCase(false)]
    public void The_recorders_host_never_disposes_the_log_it_was_given(bool portAskedFor)
    {
        var startup = Path.Combine(_runtime.Folder.Root, IRunLog.StartupFileName);
        File.WriteAllText(_temp.File(SettingsFile.FileName), "{}");
        var places = new SettingsPlaces(_temp.Path, userFolder: null);
        var runId = TestRuns.Id();
        using var log = new RecorderLog(_runtime.Time, startup);
        var recorder = AppHost.BuildRecorder(new RecorderStart(runId, TestRuns.Request(runId), new Dictionary<string, string?>()), "test-key", log, _runtime.Folder, places, _runtime.Time);
        IRunLog? port = null;

        log.Write(LogLevel.Information, "before the host is disposed");
        if (portAskedFor)
        {
            using var scope = recorder.Services.CreateScope();
            port = scope.ServiceProvider.GetRequiredService<IRunLog>();
        }

        recorder.Dispose();
        log.Write(LogLevel.Error, "after the host is disposed");
        log.Flush(TimeSpan.FromSeconds(10));

        Assert.Multiple(() =>
        {
            if (portAskedFor) Assert.That(port, Is.SameAs(log), "the port is the log the host was given");
            Assert.That(log.LostFileLines, Is.EqualTo(0), "no line was lost: the container did not dispose the log");
            Assert.That(ReadSharing(startup), Does.Contain("before the host is disposed").And.Contain("after the host is disposed"), "both lines reached recorder.log");
            Assert.That(() => log.WriteFileTo(_temp.File("Kalam_2026-10-15_23-00-02Z.log")), Throws.Nothing, "the log is still open");
        });
    }

    /// <summary>Reads a file the log may still hold open for appending.</summary>
    private static string ReadSharing(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(file);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// Both hosts resolve each port to the adapter they hold: the window's to the folder registered last (a test's, as
    /// ClassScribe's own folder), the lock over it, the journal over it and the log; the recorder's to the folder and the
    /// log it was given.
    /// </summary>
    [Test]
    public void Both_hosts_resolve_the_ports_to_the_adapters_they_hold()
    {
        File.WriteAllText(_temp.File(SettingsFile.FileName), "{}");
        var places = new SettingsPlaces(_temp.Path, userFolder: null);
        using var window = AppHost.Build(places, "test-key", services =>
            services.AddSingleton(_runtime.Folder).AddSingleton(new RoamingFolder(_temp.File("Roaming"))));
        var runId = TestRuns.Id();
        using var log = new RecorderLog(_runtime.Time);
        using var recorder = AppHost.BuildRecorder(new RecorderStart(runId, TestRuns.Request(runId), new Dictionary<string, string?>()), "test-key", log, _runtime.Folder, places, _runtime.Time);

        Assert.Multiple(() =>
        {
            foreach (var (name, services) in new[] { ("the window", window.Services), ("the recorder", recorder.Services) })
            {
                Assert.That(services.GetRequiredService<IRuntimeFolder>(), Is.SameAs(_runtime.Folder), $"{name}: ClassScribe's own folder");
                Assert.That(services.GetRequiredService<IRecordingLock>(), Is.SameAs(services.GetRequiredService<RecordingLock>()), $"{name}: the lock");
                Assert.That(services.GetRequiredService<IRunJournal>(), Is.SameAs(services.GetRequiredService<RunJournal>()), $"{name}: the journal");
                Assert.That(services.GetRequiredService<IRunLog>(), Is.SameAs(services.GetRequiredService<RecorderLog>()), $"{name}: the log");
            }

            Assert.That(recorder.Services.GetRequiredService<IRunLog>(), Is.SameAs(log), "the recorder: the log it was given");
        });
    }
}
