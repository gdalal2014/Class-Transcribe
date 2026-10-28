using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClassScribe.Commands;
using ClassScribe.Recorder;
using ClassScribe.Runs;
using ClassScribe.Settings;
using ClassScribe.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ClassScribe.Tests;

/// <summary>
/// The run files in ClassScribe's own folder: the request a window hands the recorder it starts, with the settings and
/// never the API key; each run's file, written whole by the process that records it from start to outcome, and read by
/// windows; what a start of ClassScribe finds there (a recording now, one that ended unexpectedly, a transcription that was
/// interrupted, an outcome nobody has seen); marking a run seen; and pruning old runs. The files are real, in a temporary
/// folder; the processes and the clock are the test's.
/// </summary>
[TestFixture]
public class RunJournalTests
{
    /// <summary>The run most tests write about.</summary>
    private static readonly string RunId = TestRuns.Id();

    /// <summary>The files of the recording the run makes.</summary>
    private static readonly RecordingFiles Files = new(@"D:\Classes\Kalam", "Kalam_2026-10-15_23-00-02Z");

    /// <summary>A run that ended with its transcript saved.</summary>
    private static readonly RunOutcome Transcribed = new(OutcomeKind.Transcribed, 0, "Transcript saved.", @"D:\Classes\Kalam\Kalam_2026-10-15_23-00-02Z.md",
        @"D:\Classes\Kalam", RetryCommand: null);

    /// <summary>No settings at all.</summary>
    private static readonly IReadOnlyDictionary<string, string?> NoSettings = new Dictionary<string, string?>();

    private TestRuntime _runtime = null!;

    /// <summary>Makes ClassScribe's own folder in a temporary folder, with the test's processes and clock.</summary>
    [SetUp]
    public void SetUp() => _runtime = new TestRuntime();

    /// <summary>Deletes the folder.</summary>
    [TearDown]
    public void TearDown() => _runtime.Dispose();

    /// <summary>The journal under test, which sleeps for real before it tries a file again.</summary>
    private RunJournal Journal => _runtime.Journal;

    /// <summary>The run file of <see cref="RunId"/> as a window reads it now.</summary>
    private RunRecord Read() => Journal.Find(RunId)!.Record;

    /// <summary>
    /// A request is handed to the recorder in its request file, with the settings to record with, a setting without a
    /// value included, and the file is deleted as the recorder takes it, so it is taken once.
    /// </summary>
    [Test]
    public void A_request_is_handed_over_with_its_settings_and_deleted_once_taken()
    {
        var request = TestRuns.Request(RunId) with { Minutes = 90.5, Open = false };
        var settings = new Dictionary<string, string?>
        {
            ["Recording:OutputFolder"] = @"D:\Classes",
            ["ElevenLabs:BaseUrl"] = "http://localhost:5001/",
            ["Transcript:IncludeTimestamps"] = null
        };

        Journal.WriteRequest(request, settings);
        var written = File.Exists(Journal.RequestFile(RunId));
        var taken = Journal.TakeRequest(RunId);

        Assert.That(written, Is.True);
        Assert.That(taken, Is.Not.Null);
        Assert.That(taken!.Value.Request, Is.EqualTo(request));
        Assert.That(taken.Value.Settings, Is.EquivalentTo(settings));
        Assert.That(File.Exists(Journal.RequestFile(RunId)), Is.False, "deleted once taken");
        Assert.That(Journal.TakeRequest(RunId), Is.Null, "taken once only");
        Assert.That(_runtime.FilesInRuns(), Is.Empty);
    }

    /// <summary>Without a request file for the run, there is nothing to take.</summary>
    [Test]
    public void Without_a_request_there_is_nothing_to_take()
    {
        Assert.That(Journal.TakeRequest(RunId), Is.Null);
    }

    /// <summary>
    /// A request file another program holds without sharing through every try is an error for the recorder, which reports
    /// it, and the file stays for a later look.
    /// </summary>
    [Test]
    public void A_request_held_by_another_program_through_every_try_is_an_error_and_stays()
    {
        var pauses = 0;
        var journal = _runtime.JournalPausingWith(_ => pauses++);
        journal.WriteRequest(TestRuns.Request(RunId), NoSettings);

        using (new FileStream(journal.RequestFile(RunId), FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.Throws<IOException>(() => journal.TakeRequest(RunId));

        Assert.That(pauses, Is.EqualTo(3));
        Assert.That(File.Exists(journal.RequestFile(RunId)), Is.True);
        Assert.That(_runtime.Log.Problems.Single(), Does.StartWith($"Could not read the request file {journal.RequestFile(RunId)}"));
    }

    /// <summary>
    /// A request file another program holds open without letting it be deleted, as a virus scanner may, can still be read,
    /// so the request is taken and the recorder records. Deleting the file is tried three more times, 100 ms apart, and then
    /// left, with a warning, for a later start to delete.
    /// </summary>
    [Test]
    public void A_request_that_cannot_be_deleted_is_taken_all_the_same_and_left_for_a_later_start()
    {
        var pauses = 0;
        var journal = _runtime.JournalPausingWith(_ => pauses++);
        var request = TestRuns.Request(RunId);
        journal.WriteRequest(request, NoSettings);
        var path = journal.RequestFile(RunId);

        (RecordRequest Request, IReadOnlyDictionary<string, string?> Settings)? taken;
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            taken = journal.TakeRequest(RunId);

        Assert.That(taken?.Request, Is.EqualTo(request));
        Assert.That(pauses, Is.EqualTo(3));
        Assert.That(File.Exists(path), Is.True);
        Assert.That(_runtime.Log.Problems.Single(), Does.StartWith($"Could not delete {path}"));
    }

    /// <summary>
    /// The request file never holds the API key, wherever the app holds it: from ELEVENLABS_API_KEY, which the host also
    /// keeps as a setting of its own name; written into appsettings.json by mistake; and inside another setting's value. The
    /// settings handed over are the snapshot the window takes after its check, as the launcher hands them on.
    /// </summary>
    [Test]
    public void The_request_file_never_holds_the_API_key()
    {
        const string key = "dummy-key-for-a-local-test";
        using var app = new TempFolder();
        var settings = new TestSettings(app.Path, $$"""
            { "ElevenLabs": { "ApiKey": "{{key}}", "BaseUrl": "http://localhost:5001/" }, "Recording": { "OutputFolder": "D:\\Classes" } }
            """);
        settings.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [ElevenLabsOptions.ApiKeyVariable] = key,
            ["Transcript:Footer"] = $"key {key} pasted by mistake"
        });
        settings.Configuration.AddApiKeyFromEnvironment(key);

        Journal.WriteRequest(TestRuns.Request(RunId), settings.Reloader.Snapshot());
        var text = File.ReadAllText(Journal.RequestFile(RunId));
        var taken = Journal.TakeRequest(RunId)!.Value;

        Assert.That(text, Does.Not.Contain(key));
        Assert.That(text, Does.Contain("http://localhost:5001/"), "the stub's address reaches the recorder");
        Assert.That(taken.Settings.Keys, Has.None.EqualTo(ElevenLabsOptions.ApiKeySetting).IgnoreCase);
    }

    /// <summary>
    /// The API key among the settings handed over, as a caller that handed on more than the snapshot would hand it, is left
    /// out of the request file: under either name it goes by, ElevenLabs:ApiKey or ELEVENLABS_API_KEY, which the host keeps
    /// as a setting of that name, in whatever letter case, and inside the value of any other setting. Each mistake is logged
    /// by the setting's name, never with the key; the recorder takes the key from its own environment only.
    /// </summary>
    [TestCase("elevenlabs:apikey")]
    [TestCase("Elevenlabs_Api_Key")]
    public void An_API_key_among_the_settings_handed_over_is_left_out(string keySetting)
    {
        const string key = "dummy-key-for-a-local-test";

        Journal.WriteRequest(TestRuns.Request(RunId), new Dictionary<string, string?>
        {
            [keySetting] = $" {key} ",
            ["Transcript:Footer"] = $"key={key}, pasted by mistake",
            ["Recording:OutputFolder"] = @"D:\Classes"
        });
        var text = File.ReadAllText(Journal.RequestFile(RunId));
        var taken = Journal.TakeRequest(RunId)!.Value;

        Assert.That(text, Does.Not.Contain(key));
        Assert.That(taken.Settings.Keys, Is.EqualTo(new[] { "Recording:OutputFolder" }));
        Assert.That(_runtime.Log.Problems, Has.Count.EqualTo(2));
        foreach (var name in new[] { keySetting, "Transcript:Footer" })
            Assert.That(_runtime.Log.Problems, Has.Some.StartsWith($"The settings handed to the recorder held the API key, in {name};"));
        Assert.That(_runtime.Log.Messages, Has.None.Contains(key));
    }

    /// <summary>Request files that cannot be read: not JSON, null, without a request, of a layout this build does not know, for another run, and with a class source without its kind.</summary>
    private static IEnumerable<TestCaseData> UnreadableRequests()
    {
        var valid = JsonSerializer.Serialize(new RunRequestFile(RunRequestFile.CurrentVersion, TestRuns.Request(RunId), NoSettings), RecorderJson.Default.RunRequestFile);
        var withoutKind = JsonNode.Parse(valid)!;
        withoutKind["request"]!["source"]!["class"]!.AsObject().Remove("kind");

        yield return new TestCaseData("not JSON at all").SetArgDisplayNames("not JSON");
        yield return new TestCaseData("null").SetArgDisplayNames("null");
        yield return new TestCaseData("""{ "version": 1, "request": null, "settings": {} }""").SetArgDisplayNames("no request");
        yield return new TestCaseData(valid.Replace("\"version\":1", "\"version\":2")).SetArgDisplayNames("a later layout");
        yield return new TestCaseData(JsonSerializer.Serialize(new RunRequestFile(RunRequestFile.CurrentVersion, TestRuns.Request(TestRuns.Id(2)), NoSettings),
            RecorderJson.Default.RunRequestFile)).SetArgDisplayNames("another run's request");
        yield return new TestCaseData(withoutKind.ToJsonString()).SetArgDisplayNames("a class source without its kind");
    }

    /// <summary>
    /// A request file that cannot be read is refused with an <see cref="InvalidDataException"/> that names the file and
    /// says why, so that the recorder can report it, and it is deleted all the same: it can never be used.
    /// </summary>
    [TestCaseSource(nameof(UnreadableRequests))]
    public void A_request_that_cannot_be_read_is_refused_and_deleted(string text)
    {
        Directory.CreateDirectory(_runtime.Folder.Runs);
        File.WriteAllText(Journal.RequestFile(RunId), text);

        var failure = Assert.Throws<InvalidDataException>(() => Journal.TakeRequest(RunId));

        Assert.That(failure!.Message, Does.StartWith($"The request file {Journal.RequestFile(RunId)} cannot be read"));
        Assert.That(File.Exists(Journal.RequestFile(RunId)), Is.False);
    }

    /// <summary>
    /// Only a run id as ClassScribe makes them names a request file or a run file: nothing that could reach outside the runs
    /// folder, nothing in capitals, nothing empty. Nothing is made or read for any other.
    /// </summary>
    [TestCase(@"..\..\Windows\win")]
    [TestCase("20261015-230002Z-1F3A9C07")]
    [TestCase("")]
    public void Only_a_run_id_as_ClassScribe_makes_them_names_a_file(string runId)
    {
        Assert.Throws<ArgumentException>(() => Journal.RunFile(runId));
        Assert.Throws<ArgumentException>(() => Journal.RequestFile(runId));
        Assert.Throws<ArgumentException>(() => Journal.WriteRequest(TestRuns.Request(runId), NoSettings));
        Assert.Throws<ArgumentException>(() => Journal.TakeRequest(runId));
        Assert.Throws<ArgumentException>(() => Journal.Begin(TestRuns.Record(runId)));
        Assert.Throws<ArgumentException>(() => Journal.Find(runId));
        Assert.Throws<ArgumentException>(() => Journal.MarkSeen(runId));
        Assert.That(Directory.Exists(_runtime.Folder.Runs), Is.False);
    }

    /// <summary>
    /// The run file follows the run from its start to its outcome: begun as it is given, then recording into its files,
    /// beating every 10 seconds with its position, what ended the recording, stopping, transcribing with the upload's
    /// progress, and finished with its outcome, the upload's progress gone with it since nothing uploads any more. Each
    /// write brings the heartbeat's time up to date, and windows read each step as it was written.
    /// </summary>
    [Test]
    public void A_run_file_follows_the_run_from_its_start_to_its_outcome()
    {
        _runtime.Processes.Start(TestRuns.ProcessId, TestRuns.ProcessStart);

        using var run = Journal.Begin(TestRuns.Record(RunId));
        var starting = Read();
        _runtime.Time.Advance(TimeSpan.FromSeconds(2));
        run.Recording(Files);
        var recording = Read();
        _runtime.Time.Advance(TimeSpan.FromSeconds(10));
        run.Heartbeat(TimeSpan.FromSeconds(10));
        var beating = Read();
        run.SetEndedBy(RunEndCause.Q);
        run.SetState(RunState.Stopping);
        var stopping = Read();
        _runtime.Time.Advance(TimeSpan.FromSeconds(3));
        run.SetState(RunState.Transcribing);
        run.Heartbeat(TimeSpan.FromSeconds(12), uploadPercent: 40);
        var uploading = Read();
        _runtime.Time.Advance(TimeSpan.FromMinutes(2));
        run.End(Transcribed);
        var ended = Read();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(starting, Is.EqualTo(TestRuns.Record(RunId)));
            Assert.That(recording, Is.EqualTo(starting with
            {
                State = RunState.Recording, BaseName = Files.BaseName, Folder = Files.Folder, HeartbeatUtc = TestRuns.Start.AddSeconds(2)
            }));
            Assert.That(beating, Is.EqualTo(recording with { Position = TimeSpan.FromSeconds(10), HeartbeatUtc = TestRuns.Start.AddSeconds(12) }));
            Assert.That(stopping, Is.EqualTo(beating with { EndedBy = RunEndCause.Q, State = RunState.Stopping }));
            Assert.That(uploading, Is.EqualTo(stopping with
            {
                State = RunState.Transcribing, Position = TimeSpan.FromSeconds(12), UploadPercent = 40, HeartbeatUtc = TestRuns.Start.AddSeconds(15)
            }));
            Assert.That(ended, Is.EqualTo(uploading with { State = RunState.Finished, Outcome = Transcribed, UploadPercent = null, HeartbeatUtc = TestRuns.Start.AddSeconds(135) }));
            Assert.That(run.Current, Is.EqualTo(ended));
            Assert.That(run.FilePath, Is.EqualTo(Journal.RunFile(RunId)).And.EqualTo(Path.Combine(_runtime.Folder.Runs, $"{RunId}.json")));
            Assert.That(_runtime.Log.Problems, Is.Empty);
        }
    }

    /// <summary>
    /// A run file is begun in this build's layout, with the heartbeat's time now, whatever the record handed over says, so
    /// that every window of this build reads it and the heartbeat counts from the start.
    /// </summary>
    [Test]
    public void A_run_file_is_begun_in_this_builds_layout_with_the_heartbeat_now()
    {
        _runtime.Time.Advance(TimeSpan.FromSeconds(3));
        var given = TestRuns.Record(RunId) with { Version = 0, HeartbeatUtc = TestRuns.Start.AddDays(-2) };

        using var run = Journal.Begin(given);

        var begun = given with { Version = RunRecord.CurrentVersion, HeartbeatUtc = TestRuns.Start.AddSeconds(3) };
        Assert.That(Journal.Find(RunId)?.Record, Is.EqualTo(begun));
        Assert.That(run.Current, Is.EqualTo(begun));
    }

    /// <summary>
    /// How a run ended decides its last state: a start refused because another recording was running is Refused, a
    /// recorder that failed is Failed, and every other end is Finished.
    /// </summary>
    [TestCase(OutcomeKind.Transcribed, 0, RunState.Finished)]
    [TestCase(OutcomeKind.Recorded, 0, RunState.Finished)]
    [TestCase(OutcomeKind.NothingRecorded, 1, RunState.Finished)]
    [TestCase(OutcomeKind.TranscriptFailed, 1, RunState.Finished)]
    [TestCase(OutcomeKind.Cancelled, 130, RunState.Finished)]
    [TestCase(OutcomeKind.RecorderFailed, 1, RunState.Failed)]
    [TestCase(OutcomeKind.Refused, 1, RunState.Refused)]
    public void How_a_run_ended_decides_its_last_state(OutcomeKind kind, int exitCode, RunState state)
    {
        using var run = Journal.Begin(TestRuns.Record(RunId));

        run.End(new RunOutcome(kind, exitCode, "How it ended.", null, null, null));

        Assert.That(Read().State, Is.EqualTo(state));
    }

    /// <summary>
    /// Once a run has ended, nothing more is written to its file, so a late heartbeat or a handler that runs after the
    /// end cannot undo the outcome, nor take back the cause it ended with; ending it again does nothing either.
    /// </summary>
    [Test]
    public void Nothing_is_written_after_the_end()
    {
        using var run = Journal.Begin(TestRuns.Record(RunId, RunState.Transcribing));
        run.SetEndedBy(RunEndCause.Windows);
        run.End(Transcribed);
        var ended = Read();

        _runtime.Time.Advance(TimeSpan.FromMinutes(1));
        run.Heartbeat(TimeSpan.FromHours(1), uploadPercent: 99);
        run.SetState(RunState.Recording);
        run.SetEndedBy(RunEndCause.Q);
        run.WithdrawEndedBy(RunEndCause.Windows);
        run.Recording(Files);
        run.End(new RunOutcome(OutcomeKind.Cancelled, 130, "Cancelled.", null, null, null));

        Assert.That(Read(), Is.EqualTo(ended));
        Assert.That(run.Current, Is.EqualTo(ended));
    }

    /// <summary>
    /// A run let go without an outcome, as when its process ends at once, leaves its file as it last was: a recording that
    /// ended unexpectedly is what the next start reports. Nothing is written after that either.
    /// </summary>
    [Test]
    public void A_run_let_go_without_an_outcome_keeps_its_file_as_it_last_was()
    {
        var run = Journal.Begin(TestRuns.Record(RunId));
        run.Recording(Files);
        var recording = Read();

        run.Dispose();
        run.Heartbeat(TimeSpan.FromMinutes(5));
        run.Dispose();

        Assert.That(Read(), Is.EqualTo(recording));
        Assert.That(Journal.Find(RunId)!.EndedUnexpectedly, Is.True);
    }

    /// <summary>A run file is begun only with the request its run was started with, and an end only with what ended it.</summary>
    [Test]
    public void A_run_file_needs_its_request_and_an_end_needs_its_cause()
    {
        Assert.Throws<ArgumentException>(() => Journal.Begin(TestRuns.Record(RunId) with { Request = null! }));
        Assert.That(Journal.Find(RunId), Is.Null, "nothing begun");

        using var run = Journal.Begin(TestRuns.Record(RunId, RunState.Recording));

        Assert.Throws<ArgumentException>(() => run.SetEndedBy(" "));
        Assert.Throws<ArgumentException>(() => run.WithdrawEndedBy(""));
        Assert.That(Read().EndedBy, Is.Null);
    }

    /// <summary>
    /// The last cause given for the end is kept, since it is the one that ended the process should it end before the run
    /// does: Windows asking to end the session, then Q pressed twice, leaves Q.
    /// </summary>
    [Test]
    public void The_last_cause_given_for_the_end_is_kept()
    {
        using var run = Journal.Begin(TestRuns.Record(RunId, RunState.Recording));

        run.SetEndedBy(RunEndCause.Windows);
        run.SetEndedBy(RunEndCause.Q);

        Assert.That(Read().EndedBy, Is.EqualTo(RunEndCause.Q));
    }

    /// <summary>
    /// A cause taken back, as when Windows asked to end the session and then did not because Gulzar cancelled the restart,
    /// leaves the run file naming no cause, so that a later unexpected end is not put down to Windows. A cause given since
    /// stays: taking back Windows after Q was pressed twice leaves Q, and writes nothing.
    /// </summary>
    [Test]
    public void A_cause_taken_back_no_longer_counts_and_a_cause_given_since_stays()
    {
        using var run = Journal.Begin(TestRuns.Record(RunId, RunState.Recording));

        run.SetEndedBy(RunEndCause.Windows);
        run.WithdrawEndedBy(RunEndCause.Windows);
        var cancelled = Read();
        run.SetEndedBy(RunEndCause.Windows);
        run.SetEndedBy(RunEndCause.Q);
        var stopped = Read();
        _runtime.Time.Advance(TimeSpan.FromSeconds(5));
        run.WithdrawEndedBy(RunEndCause.Windows);

        Assert.That(cancelled.EndedBy, Is.Null);
        Assert.That(Read(), Is.EqualTo(stopped));
        Assert.That(Read().EndedBy, Is.EqualTo(RunEndCause.Q));
    }

    /// <summary>A run reaches its last state only with its outcome: Finished, Refused and Failed cannot be set on their own.</summary>
    [TestCase(RunState.Finished)]
    [TestCase(RunState.Refused)]
    [TestCase(RunState.Failed)]
    public void A_run_reaches_its_last_state_only_with_its_outcome(RunState state)
    {
        using var run = Journal.Begin(TestRuns.Record(RunId, RunState.Recording));

        Assert.Throws<ArgumentException>(() => run.SetState(state));
        Assert.That(Read().State, Is.EqualTo(RunState.Recording));
    }

    /// <summary>
    /// A window reading the run file while the recorder writes it 100 times, as fast as it can rather than every 10
    /// seconds, always finds the whole of one version or the next: never a file half written, never no file, and never an
    /// older version after a newer one. Every write lands, and nothing is left over from them.
    /// </summary>
    [Test]
    public void A_window_never_finds_a_run_file_half_written_or_missing()
    {
        const int writes = 100;
        _runtime.Processes.Start(TestRuns.ProcessId, TestRuns.ProcessStart);
        using var run = Journal.Begin(TestRuns.Record(RunId, RunState.Recording));
        var stop = false;
        var reads = 0;
        var problems = new ConcurrentQueue<string>();
        var reader = new Thread(() =>
        {
            var last = TimeSpan.Zero;
            while (!Volatile.Read(ref stop))
            {
                var found = Journal.Find(RunId);
                if (found is null)
                {
                    problems.Enqueue($"no run file after {last}");
                }
                else
                {
                    if (found.Record.Position < last) problems.Enqueue($"back from {last} to {found.Record.Position}");
                    last = found.Record.Position;
                    reads++;
                }

                Thread.Sleep(1);
            }
        });

        reader.Start();
        try
        {
            for (var second = 1; second <= writes; second++) run.Heartbeat(TimeSpan.FromSeconds(second));
        }
        finally
        {
            Volatile.Write(ref stop, true);
            reader.Join();
        }

        var triedAgain = _runtime.Log.Messages.Count(message => message.Contains("writing it again", StringComparison.Ordinal));
        TestContext.Out.WriteLine($"{writes} writes, {reads} reads; the window's reading made {triedAgain} writes try again.");

        Assert.That(problems, Is.Empty);
        Assert.That(reads, Is.GreaterThan(0));
        Assert.That(Read().Position, Is.EqualTo(TimeSpan.FromSeconds(writes)));
        Assert.That(_runtime.Log.Problems, Is.Empty);
        Assert.That(_runtime.FilesInRuns(), Is.EqualTo(new[] { $"{RunId}.json" }));
    }

    /// <summary>
    /// A window that has the run file open while the recorder writes it, sharing it as ClassScribe's own readers do, reads
    /// the whole of the version it opened and nothing of the next: a write never changes the file in place, it replaces the
    /// file whole, and Windows refuses that while the window has the file open, so the write waits, 100 ms at a time. Here
    /// the window reads at the write's first pause and then lets go; the write lands at its next try, and nothing is
    /// reported or left over.
    /// </summary>
    [Test]
    public void A_window_reading_the_run_file_while_it_is_written_reads_the_whole_of_the_version_it_opened()
    {
        FileStream? window = null;
        string? readByWindow = null;
        var pauses = new List<TimeSpan>();
        string ReadAndLetGo()
        {
            using var reader = new StreamReader(window!);
            var text = reader.ReadToEnd();
            window = null;
            return text;
        }

        var journal = _runtime.JournalPausingWith(pause =>
        {
            pauses.Add(pause);
            if (window is not null) readByWindow = ReadAndLetGo();
        });
        using var run = journal.Begin(TestRuns.Record(RunId, RunState.Recording));
        var opened = File.ReadAllText(run.FilePath);
        window = new FileStream(run.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

        run.Heartbeat(TimeSpan.FromMinutes(2));
        if (window is not null) readByWindow = ReadAndLetGo();

        Assert.That(readByWindow, Is.EqualTo(opened), "the whole of the version the window opened");
        Assert.That(pauses, Is.EqualTo(new[] { TimeSpan.FromMilliseconds(100) }), "the write waited for the window once");
        Assert.That(Read().Position, Is.EqualTo(TimeSpan.FromMinutes(2)));
        Assert.That(_runtime.Log.Problems, Is.Empty);
        Assert.That(_runtime.FilesInRuns(), Is.EqualTo(new[] { $"{RunId}.json" }));
    }

    /// <summary>
    /// A run file that another program holds for a moment without sharing it, as a virus scanner may, is written at the
    /// next try, 100 ms later, and nothing is reported.
    /// </summary>
    [Test]
    public void A_run_file_held_by_another_program_for_a_moment_is_written_at_the_next_try()
    {
        FileStream? scanner = null;
        var pauses = new List<TimeSpan>();
        var journal = _runtime.JournalPausingWith(pause =>
        {
            pauses.Add(pause);
            scanner?.Dispose();
            scanner = null;
        });
        using var run = journal.Begin(TestRuns.Record(RunId, RunState.Recording));
        scanner = new FileStream(run.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read);

        run.Heartbeat(TimeSpan.FromMinutes(5));

        Assert.That(pauses, Is.EqualTo(new[] { TimeSpan.FromMilliseconds(100) }));
        Assert.That(Read().Position, Is.EqualTo(TimeSpan.FromMinutes(5)));
        Assert.That(_runtime.Log.Problems, Is.Empty);
        Assert.That(_runtime.FilesInRuns(), Is.EqualTo(new[] { $"{RunId}.json" }));
    }

    /// <summary>
    /// A run file that cannot be written even after three more tries is reported once, not at every heartbeat, and the
    /// recording goes on: nothing is thrown. What could not be written is written with the next write that can.
    /// </summary>
    [Test]
    public void A_run_file_that_cannot_be_written_is_reported_once_and_the_recording_goes_on()
    {
        var pauses = 0;
        var journal = _runtime.JournalPausingWith(_ => pauses++);
        using var run = journal.Begin(TestRuns.Record(RunId, RunState.Recording));

        using (new FileStream(run.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.DoesNotThrow(() => run.Heartbeat(TimeSpan.FromMinutes(1)));
            Assert.DoesNotThrow(() => run.SetEndedBy(RunEndCause.Windows));
        }

        run.Heartbeat(TimeSpan.FromMinutes(2));

        Assert.That(pauses, Is.EqualTo(6), "each write tried four times, with three pauses");
        Assert.That(Read().Position, Is.EqualTo(TimeSpan.FromMinutes(2)));
        Assert.That(Read().EndedBy, Is.EqualTo(RunEndCause.Windows));
        Assert.That(_runtime.Log.Problems.Single(), Does.StartWith($"Could not bring the run file {run.FilePath} up to date"));
        Assert.That(_runtime.FilesInRuns(), Is.EqualTo(new[] { $"{RunId}.json" }), "nothing left over from the failed writes");
    }

    /// <summary>
    /// An outcome that could not be written, because another program held the run file through every try, is written when
    /// the run is let go, its last chance, so that the next start reports how the run ended rather than an interruption.
    /// </summary>
    [Test]
    public void An_outcome_that_could_not_be_written_is_written_when_the_run_is_let_go()
    {
        var journal = _runtime.JournalPausingWith(_ => { });
        var run = journal.Begin(TestRuns.Record(RunId, RunState.Transcribing));

        using (new FileStream(run.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            run.End(Transcribed);

        var beforeLettingGo = Read();
        run.Dispose();

        Assert.That(beforeLettingGo.State, Is.EqualTo(RunState.Transcribing));
        Assert.That(Read().State, Is.EqualTo(RunState.Finished));
        Assert.That(Read().Outcome, Is.EqualTo(Transcribed));
        Assert.That(_runtime.Log.Problems, Has.Count.EqualTo(1));
    }

    /// <summary>A run let go whose last write went through writes nothing more.</summary>
    [Test]
    public void A_run_let_go_after_its_last_write_went_through_writes_nothing_more()
    {
        var run = Journal.Begin(TestRuns.Record(RunId, RunState.Recording));
        run.Heartbeat(TimeSpan.FromMinutes(1));
        var written = File.GetLastWriteTimeUtc(run.FilePath);
        var before = Read();

        _runtime.Time.Advance(TimeSpan.FromMinutes(1));
        run.Dispose();

        Assert.That(Read(), Is.EqualTo(before));
        Assert.That(File.GetLastWriteTimeUtc(run.FilePath), Is.EqualTo(written));
    }

    /// <summary>
    /// A run file that cannot be begun is an error for the caller, which decides what it means, and it is logged: here a file
    /// stands where the runs folder should be.
    /// </summary>
    [Test]
    public void A_run_file_that_cannot_be_begun_is_an_error_for_the_caller()
    {
        Directory.CreateDirectory(_runtime.Folder.Root);
        File.WriteAllText(_runtime.Folder.Runs, "a file where the runs folder should be");
        var journal = _runtime.JournalPausingWith(_ => { });

        Assert.Throws<IOException>(() => journal.Begin(TestRuns.Record(RunId)));
        Assert.That(_runtime.Log.Problems.Single(), Does.StartWith($"Could not write the run file {journal.RunFile(RunId)}"));
    }

    /// <summary>
    /// A recording or a stop whose process is gone ended unexpectedly: it is not recording now, and the next start reports
    /// it.
    /// </summary>
    [TestCase(RunState.Recording)]
    [TestCase(RunState.Stopping)]
    public void A_recording_whose_process_is_gone_ended_unexpectedly(RunState state)
    {
        using (Journal.Begin(TestRuns.Record(RunId, state)))
        {
        }

        var run = Journal.List().Single();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(run.IsAlive, Is.False);
            Assert.That(run.EndedUnexpectedly, Is.True);
            Assert.That(run.NeedsReport, Is.True);
            Assert.That(run.IsRecordingNow, Is.False);
            Assert.That(run.TranscriptionInterrupted, Is.False);
            Assert.That(run.HasUnseenOutcome, Is.False);
        }
    }

    /// <summary>
    /// A recording whose process id now belongs to a process that started 30 minutes later, one Windows gave the id to
    /// once the recorder had ended, ended unexpectedly all the same: the id alone does not keep it alive.
    /// </summary>
    [Test]
    public void A_recording_whose_process_id_was_given_to_another_process_ended_unexpectedly()
    {
        _runtime.Processes.Start(TestRuns.ProcessId, TestRuns.ProcessStart.AddMinutes(30));
        using (Journal.Begin(TestRuns.Record(RunId, RunState.Recording)))
        {
        }

        var run = Journal.List().Single();

        Assert.That(run.IsAlive, Is.False);
        Assert.That(run.EndedUnexpectedly, Is.True);
    }

    /// <summary>
    /// A run whose process runs, with a start time within 2 seconds of the one written down, is recording now while it
    /// starts, records or stops, and needs no report.
    /// </summary>
    [TestCase(RunState.Starting, 0)]
    [TestCase(RunState.Recording, 0)]
    [TestCase(RunState.Recording, 1.9)]
    [TestCase(RunState.Recording, -1.9)]
    [TestCase(RunState.Stopping, 0)]
    public void A_run_whose_process_runs_is_recording_now(RunState state, double startTimeOffSeconds)
    {
        _runtime.Processes.Start(TestRuns.ProcessId, TestRuns.ProcessStart.AddSeconds(startTimeOffSeconds));
        using var recorder = Journal.Begin(TestRuns.Record(RunId, state));

        var run = Journal.List().Single();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(run.IsAlive, Is.True);
            Assert.That(run.IsRecordingNow, Is.True);
            Assert.That(run.IsTranscribingNow, Is.False);
            Assert.That(run.EndedUnexpectedly, Is.False);
            Assert.That(run.NeedsReport, Is.False);
        }
    }

    /// <summary>A transcription whose process runs is going on now; one whose process is gone was interrupted, and the next start reports it.</summary>
    [TestCase(true)]
    [TestCase(false)]
    public void A_transcription_goes_on_while_its_process_runs_and_was_interrupted_when_it_is_gone(bool alive)
    {
        if (alive) _runtime.Processes.Start(TestRuns.ProcessId, TestRuns.ProcessStart);
        using var recorder = Journal.Begin(TestRuns.Record(RunId, RunState.Transcribing));

        var run = Journal.List().Single();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(run.IsTranscribingNow, Is.EqualTo(alive));
            Assert.That(run.TranscriptionInterrupted, Is.EqualTo(!alive));
            Assert.That(run.NeedsReport, Is.EqualTo(!alive));
            Assert.That(run.IsRecordingNow, Is.False);
            Assert.That(run.EndedUnexpectedly, Is.False);
        }
    }

    /// <summary>
    /// An outcome nobody has seen, such as a transcript saved while no window showed the run, is reported, even while the
    /// recorder waits its last seconds for windows, until a window marks it seen. Marking it changes nothing else: the
    /// heartbeat stays the recorder's last.
    /// </summary>
    [Test]
    public void An_outcome_nobody_has_seen_is_reported_until_it_is_marked_seen()
    {
        _runtime.Processes.Start(TestRuns.ProcessId, TestRuns.ProcessStart);
        using (var run = Journal.Begin(TestRuns.Record(RunId, RunState.Transcribing)))
            run.End(Transcribed);
        var whileTheRecorderWaits = Journal.Find(RunId)!;
        _runtime.Processes.End(TestRuns.ProcessId);
        var unseen = Journal.Find(RunId)!;

        _runtime.Time.Advance(TimeSpan.FromMinutes(5));
        Journal.MarkSeen(RunId);
        var seen = Journal.Find(RunId)!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(whileTheRecorderWaits.HasUnseenOutcome, Is.True);
            Assert.That(unseen.HasUnseenOutcome, Is.True);
            Assert.That(unseen.NeedsReport, Is.True);
            Assert.That(seen.Record, Is.EqualTo(unseen.Record with { Seen = true }));
            Assert.That(seen.HasUnseenOutcome, Is.False);
            Assert.That(seen.NeedsReport, Is.False);
        }
    }

    /// <summary>
    /// A recording that ended unexpectedly, or a transcription that was interrupted, is not reported again once handled:
    /// continued, transcribed or forgotten, each of which marks the run seen.
    /// </summary>
    [TestCase(RunState.Recording)]
    [TestCase(RunState.Stopping)]
    [TestCase(RunState.Transcribing)]
    public void A_run_that_ended_unexpectedly_is_not_reported_again_once_marked_seen(RunState state)
    {
        using (Journal.Begin(TestRuns.Record(RunId, state)))
        {
        }

        var before = Journal.Find(RunId)!;
        Journal.MarkSeen(RunId);
        var after = Journal.Find(RunId)!;

        Assert.That(before.NeedsReport, Is.True);
        Assert.That(after.NeedsReport, Is.False);
        Assert.That(after.EndedUnexpectedly || after.TranscriptionInterrupted, Is.False);
    }

    /// <summary>
    /// A start whose recorder died before it recorded anything has nothing to report later, since there is no audio to
    /// continue or transcribe: the window that started it saw it end and said why.
    /// </summary>
    [Test]
    public void A_start_whose_recorder_died_before_it_recorded_has_nothing_to_report()
    {
        using (Journal.Begin(TestRuns.Record(RunId)))
        {
        }

        var run = Journal.Find(RunId)!;

        Assert.That(run.Record.State, Is.EqualTo(RunState.Starting));
        Assert.That(run.IsRecordingNow, Is.False);
        Assert.That(run.EndedUnexpectedly, Is.False);
        Assert.That(run.NeedsReport, Is.False);
    }

    /// <summary>A start refused because another recording was running has nothing to report later: the window that started it dealt with it at once.</summary>
    [Test]
    public void A_refused_start_has_nothing_to_report()
    {
        using (var run = Journal.Begin(TestRuns.Record(RunId)))
            run.End(new RunOutcome(OutcomeKind.Refused, 1, "Another recording is running.", null, null, null));

        var refused = Journal.Find(RunId)!;

        Assert.That(refused.Record.State, Is.EqualTo(RunState.Refused));
        Assert.That(refused.HasUnseenOutcome, Is.False);
        Assert.That(refused.NeedsReport, Is.False);
    }

    /// <summary>A recorder that failed to start is reported, with its reason, until a window marks it seen: nobody may have seen it fail.</summary>
    [Test]
    public void A_recorder_that_failed_to_start_is_reported_until_it_is_marked_seen()
    {
        using (var run = Journal.Begin(TestRuns.Record(RunId)))
            run.End(new RunOutcome(OutcomeKind.RecorderFailed, 1, "The recorder could not read the request it was started with.", null, null, null));

        var failed = Journal.Find(RunId)!;
        Journal.MarkSeen(RunId);

        Assert.That(failed.Record.State, Is.EqualTo(RunState.Failed));
        Assert.That(failed.HasUnseenOutcome, Is.True);
        Assert.That(Journal.Find(RunId)!.HasUnseenOutcome, Is.False);
    }

    /// <summary>
    /// Marking a run seen while its process still records leaves its file alone: the recorder owns its file until its run
    /// is over, and would write over the mark at its next heartbeat anyway.
    /// </summary>
    [Test]
    public void Marking_a_live_recording_seen_leaves_its_file_alone()
    {
        _runtime.Processes.Start(TestRuns.ProcessId, TestRuns.ProcessStart);
        using var run = Journal.Begin(TestRuns.Record(RunId, RunState.Recording));
        var before = Read();

        Journal.MarkSeen(RunId);

        Assert.That(Read(), Is.EqualTo(before));
    }

    /// <summary>Marking a run seen that has no run file, because it was pruned or never written, does nothing and makes no file.</summary>
    [Test]
    public void Marking_a_run_without_a_run_file_seen_does_nothing()
    {
        Assert.DoesNotThrow(() => Journal.MarkSeen(RunId));
        Assert.That(_runtime.FilesInRuns(), Is.Empty);
    }

    /// <summary>
    /// A mark that cannot be written, because another program holds the run file through every try, is logged and goes no
    /// further: the window that marks the run carries on, and the run is reported once more at the next start. Nothing is
    /// left over from the tries.
    /// </summary>
    [Test]
    public void A_mark_that_cannot_be_written_is_logged_and_the_run_is_reported_again()
    {
        using (Journal.Begin(TestRuns.Record(RunId, RunState.Recording)))
        {
        }

        var pauses = 0;
        var journal = _runtime.JournalPausingWith(_ => pauses++);
        var path = journal.RunFile(RunId);

        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.DoesNotThrow(() => journal.MarkSeen(RunId));

        Assert.That(pauses, Is.EqualTo(3));
        Assert.That(_runtime.Log.Problems.Single(), Does.StartWith($"Could not mark run {RunId} seen in {path}"));
        Assert.That(Read().Seen, Is.False);
        Assert.That(Journal.Find(RunId)!.NeedsReport, Is.True);
        Assert.That(_runtime.FilesInRuns(), Is.EqualTo(new[] { $"{RunId}.json" }));
    }

    /// <summary>
    /// The runs are listed newest first. Everything in the runs folder that is not a run file this build can read is
    /// passed over: request files, an unfinished write, a file that is not JSON, a file that holds nothing but null, a run
    /// file of a later layout, a file not named after a run, and a run file named after another run.
    /// </summary>
    [Test]
    public void The_runs_are_listed_newest_first_and_whatever_is_not_a_run_file_is_passed_over()
    {
        foreach (var number in new[] { 1, 3, 2 })
        {
            using var run = Journal.Begin(TestRuns.Record(TestRuns.Id(number)) with { StartedUtc = TestRuns.Start.AddMinutes(number) });
        }

        Journal.WriteRequest(TestRuns.Request(TestRuns.Id(4)), NoSettings);
        var runs = _runtime.Folder.Runs;
        File.WriteAllText(Path.Combine(runs, $"{TestRuns.Id(5)}.json.0123abcd.tmp"), "{ \"version\": 1, \"runId\": ");
        File.WriteAllText(Path.Combine(runs, $"{TestRuns.Id(6)}.json"), "not JSON at all");
        File.WriteAllText(Path.Combine(runs, $"{TestRuns.Id(7)}.json"), JsonSerializer.Serialize(TestRuns.Record(TestRuns.Id(7)) with { Version = 2 }, RecorderJson.Default.RunRecord));
        File.WriteAllText(Path.Combine(runs, "notes.json"), JsonSerializer.Serialize(TestRuns.Record(TestRuns.Id(8)), RecorderJson.Default.RunRecord));
        File.WriteAllText(Path.Combine(runs, $"{TestRuns.Id(9)}.json"), JsonSerializer.Serialize(TestRuns.Record(TestRuns.Id(10)), RecorderJson.Default.RunRecord));
        File.WriteAllText(Path.Combine(runs, $"{TestRuns.Id(11)}.json"), "null");

        var listed = Journal.List();

        Assert.That(listed.Select(run => run.Record.RunId), Is.EqualTo(new[] { TestRuns.Id(3), TestRuns.Id(2), TestRuns.Id(1) }));
        Assert.That(Journal.Find(TestRuns.Id(6)), Is.Null);
        Assert.That(Journal.Find(TestRuns.Id(7)), Is.Null);
        Assert.That(Journal.Find(TestRuns.Id(9)), Is.Null);
        Assert.That(Journal.Find(TestRuns.Id(11)), Is.Null);
    }

    /// <summary>
    /// A runs folder that cannot be looked at, here because a file stands where it should be, lists nothing and prunes
    /// nothing, with a warning that says what it means, rather than keeping a window from its menu or a recorder from
    /// recording.
    /// </summary>
    [Test]
    public void A_runs_folder_that_cannot_be_looked_at_lists_nothing_and_says_so()
    {
        Directory.CreateDirectory(_runtime.Folder.Root);
        File.WriteAllText(_runtime.Folder.Runs, "a file where the runs folder should be");

        var listed = Journal.List();
        Journal.Prune(RunTimings.KeepSeenRunsFor);

        Assert.That(listed, Is.Empty);
        Assert.That(Journal.Find(RunId), Is.Null);
        Assert.That(_runtime.Log.Problems, Has.Count.EqualTo(2).And.All.StartsWith($"Could not look at the runs in {_runtime.Folder.Runs}"));
        Assert.That(File.Exists(_runtime.Folder.Runs), Is.True, "left as it is");
    }

    /// <summary>
    /// A run file another program holds without sharing it through every try, as a virus scanner may for a moment, is passed
    /// over this time, with a warning, after three more tries 100 ms apart, rather than hiding the other runs or keeping a
    /// window from its menu. Once it is let go, it is listed again.
    /// </summary>
    [Test]
    public void A_run_file_held_by_another_program_through_every_try_is_passed_over_this_time()
    {
        var pauses = 0;
        var journal = _runtime.JournalPausingWith(_ => pauses++);
        using (journal.Begin(TestRuns.Record(TestRuns.Id(1), RunState.Recording)))
        using (journal.Begin(TestRuns.Record(TestRuns.Id(2), RunState.Recording)))
        {
        }

        var held = journal.RunFile(TestRuns.Id(2));
        IReadOnlyList<RunView> listed;
        RunView? found;
        using (new FileStream(held, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            listed = journal.List();
            found = journal.Find(TestRuns.Id(2));
        }

        Assert.That(listed.Select(run => run.Record.RunId), Is.EqualTo(new[] { TestRuns.Id(1) }));
        Assert.That(found, Is.Null);
        Assert.That(pauses, Is.EqualTo(6), "each look tried four times, with three pauses");
        Assert.That(_runtime.Log.Problems, Has.Count.EqualTo(2).And.All.StartsWith($"Could not read the run file {held}; it is passed over this time"));
        Assert.That(journal.List().Select(run => run.Record.RunId), Is.EquivalentTo(new[] { TestRuns.Id(1), TestRuns.Id(2) }), "listed again once let go");
    }

    /// <summary>Without a runs folder there is nothing to list, find or prune, and looking makes no folder.</summary>
    [Test]
    public void Without_a_runs_folder_there_is_nothing_to_list_find_or_prune()
    {
        Assert.That(Journal.List(), Is.Empty);
        Assert.That(Journal.Find(RunId), Is.Null);
        Assert.DoesNotThrow(() => Journal.Prune(RunTimings.KeepSeenRunsFor));
        Assert.That(Directory.Exists(_runtime.Folder.Runs), Is.False);
    }

    /// <summary>
    /// Pruning deletes the runs older than the limit, 30 days, that have nothing left to report and no process: seen ones,
    /// a refused start, and a start whose recorder died before it recorded. It keeps every run that still needs a report,
    /// however old (an outcome nobody saw, a recording that ended unexpectedly, an interrupted transcription), every run
    /// whose process runs, and every run younger than the limit.
    /// </summary>
    [Test]
    public void Pruning_deletes_settled_runs_older_than_the_limit_and_keeps_the_rest()
    {
        var seenOld = Ended(1, Transcribed, seen: true);
        var refusedOld = Ended(2, new RunOutcome(OutcomeKind.Refused, 1, "Another recording is running.", null, null, null), seen: false);
        var diedStarting = Left(3, RunState.Starting);
        var unseenOld = Ended(4, Transcribed, seen: false);
        var endedUnexpectedlyOld = Left(5, RunState.Recording);
        var interruptedOld = Left(6, RunState.Transcribing);
        _runtime.Processes.Start(TestRuns.ProcessId + 7, TestRuns.ProcessStart);
        var aliveOld = Left(7, RunState.Recording, processId: TestRuns.ProcessId + 7);
        _runtime.Time.Advance(TimeSpan.FromDays(31));
        var seenRecent = Ended(8, Transcribed, seen: true);

        Journal.Prune(RunTimings.KeepSeenRunsFor);

        Assert.That(RunTimings.KeepSeenRunsFor, Is.EqualTo(TimeSpan.FromDays(30)));
        Assert.That(Journal.List().Select(run => run.Record.RunId), Is.EquivalentTo(new[] { unseenOld, endedUnexpectedlyOld, interruptedOld, aliveOld, seenRecent }));
        Assert.That(_runtime.FilesInRuns(), Does.Not.Contain($"{seenOld}.json").And.Not.Contain($"{refusedOld}.json").And.Not.Contain($"{diedStarting}.json"));
    }

    /// <summary>
    /// Pruning also deletes, once they are older than the limit by their files' times, requests no recorder took, writes
    /// cut short, and run files that cannot be read. It keeps younger ones, a run file of a later layout, which is the
    /// later build's to judge, and old files that ClassScribe did not name, even when their names end as its own do.
    /// </summary>
    [Test]
    public void Pruning_deletes_old_leftovers_and_keeps_a_later_builds_run_files()
    {
        var runs = Directory.CreateDirectory(_runtime.Folder.Runs).FullName;
        var old = TestRuns.Start.AddDays(-31).UtcDateTime;
        var recent = TestRuns.Start.AddDays(-1).UtcDateTime;
        string Leftover(string name, string text, DateTime written)
        {
            var path = Path.Combine(runs, name);
            File.WriteAllText(path, text);
            File.SetLastWriteTimeUtc(path, written);
            return name;
        }

        var request = JsonSerializer.Serialize(new RunRequestFile(RunRequestFile.CurrentVersion, TestRuns.Request(TestRuns.Id(1)), NoSettings), RecorderJson.Default.RunRequestFile);
        var later = JsonSerializer.Serialize(TestRuns.Record(TestRuns.Id(5)) with { Version = 2, Seen = true }, RecorderJson.Default.RunRecord);
        Leftover($"{TestRuns.Id(1)}.request.json", request, old);
        var recentRequest = Leftover($"{TestRuns.Id(2)}.request.json", request.Replace(TestRuns.Id(1), TestRuns.Id(2)), recent);
        Leftover($"{TestRuns.Id(3)}.json.0123abcd.tmp", "{ \"version\": 1, ", old);
        var recentWrite = Leftover($"{TestRuns.Id(3)}.json.4567ef01.tmp", "{ \"version\": 1, ", recent);
        Leftover($"{TestRuns.Id(4)}.json", "not JSON at all", old);
        var recentUnreadable = Leftover($"{TestRuns.Id(6)}.json", "not JSON at all", recent);
        var laterBuilds = Leftover($"{TestRuns.Id(5)}.json", later, old);
        var foreignRequest = Leftover("notes.request.json", request, old);
        var foreign = Leftover("notes.json", "{}", old);

        Journal.Prune(RunTimings.KeepSeenRunsFor);

        Assert.That(_runtime.FilesInRuns(), Is.EquivalentTo(new[] { recentRequest, recentWrite, recentUnreadable, laterBuilds, foreignRequest, foreign }));
    }

    /// <summary>
    /// Pruning passes over an old run file that another program holds open without letting it be deleted: deleting it is
    /// tried three more times, 100 ms apart, then left with a warning, and the recorder that prunes starts all the same. A
    /// later prune, once the file is let go, deletes it.
    /// </summary>
    [Test]
    public void Pruning_leaves_an_old_file_another_program_holds_for_a_later_start()
    {
        var seenOld = Ended(1, Transcribed, seen: true);
        _runtime.Time.Advance(TimeSpan.FromDays(31));
        var pauses = 0;
        var journal = _runtime.JournalPausingWith(_ => pauses++);
        var path = journal.RunFile(seenOld);

        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            Assert.DoesNotThrow(() => journal.Prune(RunTimings.KeepSeenRunsFor));
        var keptWhileHeld = File.Exists(path);
        journal.Prune(RunTimings.KeepSeenRunsFor);

        Assert.That(keptWhileHeld, Is.True);
        Assert.That(pauses, Is.EqualTo(3));
        Assert.That(_runtime.Log.Problems.Single(), Does.StartWith($"Could not delete {path}"));
        Assert.That(File.Exists(path), Is.False, "deleted by the later prune");
    }

    /// <summary>Begins run <paramref name="number"/> in <paramref name="state"/> and lets it go without an outcome, as a process that ended at once would; returns its id.</summary>
    private string Left(int number, RunState state, int processId = TestRuns.ProcessId)
    {
        var runId = TestRuns.Id(number);
        using (Journal.Begin(TestRuns.Record(runId, state, processId)))
        {
        }

        return runId;
    }

    /// <summary>Begins run <paramref name="number"/>, ends it with <paramref name="outcome"/>, and marks it seen when <paramref name="seen"/> is true; returns its id.</summary>
    private string Ended(int number, RunOutcome outcome, bool seen)
    {
        var runId = TestRuns.Id(number);
        using (var run = Journal.Begin(TestRuns.Record(runId, RunState.Transcribing)))
            run.End(outcome);

        if (seen) Journal.MarkSeen(runId);
        return runId;
    }
}

/// <summary>
/// ClassScribe's own folder, %LOCALAPPDATA%\ClassScribe, where the lock and the run files are kept: where it is, and
/// whether it can be written, which decides whether a run can be recorded in a separate recorder.
/// </summary>
[TestFixture]
public class RuntimeFolderTests
{
    /// <summary>
    /// The folder is ClassScribe's own under the user's local application data, which never roams or syncs; the runs and
    /// the lock file are in it. Nothing is made, or even looked at, just by asking where it is.
    /// </summary>
    [Test]
    public void The_folder_is_ClassScribes_own_under_the_users_local_application_data()
    {
        var expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClassScribe");

        var folder = new RuntimeFolder(RuntimeFolder.RealRoot());

        Assert.That(folder.Root, Is.EqualTo(expected));
        Assert.That(folder.Runs, Is.EqualTo(Path.Combine(expected, "runs")));
        Assert.That(folder.LockFile, Is.EqualTo(Path.Combine(expected, "recording.lock")));
    }

    /// <summary>
    /// In a test, asking for the real %LOCALAPPDATA%\ClassScribe throws, and says what to register instead, so that no test
    /// reads or writes the run files of real classes or the real queue for Google Drive.
    /// </summary>
    [Test]
    public void A_test_cannot_reach_the_real_folder()
    {
        var refused = Assert.Throws<InvalidOperationException>(() => _ = new RuntimeFolder());

        Assert.That(refused!.Message, Does.Contain("TestRuntime"));
    }

    /// <summary>
    /// The container makes ClassScribe's own folder, the lock and the run journal as they are, so that registering them needs
    /// no factory: with nothing registered for the folder's root it makes the folder of %LOCALAPPDATA%\ClassScribe, which a
    /// test is refused, and with a folder given it makes the lock and the journal over it. Nothing is made on disk by making
    /// them.
    /// </summary>
    [Test]
    public void The_container_makes_the_folder_the_lock_and_the_journal_as_they_are()
    {
        using var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton(TimeProvider.System)
            .AddSingleton<IProcessLiveness, FakeProcessLiveness>()
            .AddSingleton<RuntimeFolder>()
            .AddSingleton<RecordingLock>()
            .AddSingleton<RunJournal>()
            .BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        Assert.That(() => services.GetRequiredService<RuntimeFolder>(), Throws.InvalidOperationException.With.Message.Contains("TestRuntime"));

        using var temp = new TempFolder();
        using var given = new ServiceCollection()
            .AddLogging()
            .AddSingleton(TimeProvider.System)
            .AddSingleton<IProcessLiveness, FakeProcessLiveness>()
            .AddSingleton(new RuntimeFolder(temp.File("ClassScribe")))
            .AddSingleton<RecordingLock>()
            .AddSingleton<RunJournal>()
            .BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

        Assert.That(given.GetRequiredService<RecordingLock>(), Is.Not.Null);
        Assert.That(given.GetRequiredService<RunJournal>(), Is.Not.Null);
        Assert.That(Directory.Exists(temp.File("ClassScribe")), Is.False, "nothing is made on disk by making them");
    }

    /// <summary>A folder given, as tests give one, is used instead, as a full path.</summary>
    [Test]
    public void A_folder_given_is_used_instead()
    {
        using var temp = new TempFolder();

        var folder = new RuntimeFolder(Path.Combine(temp.Path, "Runtime", "..", "ClassScribe"));

        Assert.That(folder.Root, Is.EqualTo(Path.Combine(temp.Path, "ClassScribe")));
        Assert.That(folder.Runs, Is.EqualTo(Path.Combine(temp.Path, "ClassScribe", "runs")));
    }

    /// <summary>A folder that can be written says so, is made with its runs folder, and is left with nothing else in it.</summary>
    [Test]
    public void A_folder_that_can_be_written_says_so_and_is_made()
    {
        using var temp = new TempFolder();
        var folder = new RuntimeFolder(temp.File("ClassScribe"));

        var problem = folder.DescribeWriteProblem();
        var canWrite = folder.CanWrite();

        Assert.That(problem, Is.Null);
        Assert.That(canWrite, Is.True);
        Assert.That(Directory.Exists(folder.Runs), Is.True);
        Assert.That(Directory.GetFiles(folder.Root, "*", SearchOption.AllDirectories), Is.Empty);
    }

    /// <summary>A folder that cannot be written, here because a file stands where it should be, says so and says why, naming the folder.</summary>
    [Test]
    public void A_folder_that_cannot_be_written_says_why()
    {
        using var temp = new TempFolder();
        File.WriteAllText(temp.File("ClassScribe"), "a file where the folder should be");
        var folder = new RuntimeFolder(temp.File("ClassScribe"));

        var problem = folder.DescribeWriteProblem();

        Assert.That(folder.CanWrite(), Is.False);
        Assert.That(problem, Does.StartWith($"{folder.Root} cannot be written"));
    }
}
