using ClassScribe.Audio;
using ClassScribe.Commands;
using ClassScribe.Drive;
using ClassScribe.Recorder;
using ClassScribe.Runs;
using ClassScribe.Settings;
using ClassScribe.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ClassScribe.YouTube;

namespace ClassScribe.Tests;

/// <summary>
/// The report at the start (section 1.14 of the design): a recording that ended unexpectedly, with its cause when it is
/// known, and C to continue it, T to transcribe it, F to forget it; a transcription that was interrupted, with T; and an
/// outcome nobody saw, with O to open the transcript. On the command line it prints the commands and asks nothing. The
/// menu's header lines name what runs in the background. The run files and recordings are real files in temporary
/// folders; the clock reads local time in US Eastern time, where the evening class stamped 23:00 UTC began at 19:00.
/// </summary>
[TestFixture]
public partial class StartupReportTests
{
    /// <summary>The run most tests report.</summary>
    private static readonly string RunId = TestRuns.Id();

    /// <summary>When the recording's audio was last written: 23:47:12 UTC, 19:47:12 in the evening in Eastern time.</summary>
    private static readonly DateTimeOffset LastAudio = TestRuns.Start.AddMinutes(47).AddSeconds(10);

    /// <summary>When the run's last heartbeat was: 23:47:05 UTC.</summary>
    private static readonly DateTimeOffset LastHeartbeat = TestRuns.Start.AddMinutes(47).AddSeconds(3);

    private TestRuntime _runtime = null!;
    private TempFolder _recordings = null!;
    private TempFolder _settingsFolder = null!;
    private TestSettings _settings = null!;
    private TestOptions<RecordingOptions> _options = null!;
    private FakeConsoleUi _ui = null!;
    private Mock<ICommandRunner> _commands = null!;
    private Mock<IDesktop> _desktop = null!;
    private RecordingLogger<StartupReport> _log = null!;
    private FakeCredentialStore _store = null!;

    /// <summary>
    /// Makes the folders, the settings, a console to type into and the commands the report runs, with the clock four
    /// minutes after the recording's audio was last written.
    /// </summary>
    [SetUp]
    public void SetUp()
    {
        _runtime = new TestRuntime();
        _runtime.Time.Advance(LastAudio.AddMinutes(4) - TestRuns.Start);
        _recordings = new TempFolder();
        _settingsFolder = new TempFolder();
        _settings = new TestSettings(_settingsFolder.Path);
        _options = TestOptions.Create(new RecordingOptions { OutputFolder = _recordings.Path });
        _ui = new FakeConsoleUi();
        _commands = new Mock<ICommandRunner>();
        _commands.Setup(commands => commands.RecordAsync(It.IsAny<RecordRequest>(), It.IsAny<StartOrigin>())).ReturnsAsync(0);
        _commands.Setup(commands => commands.TranscribeAsync(It.IsAny<TranscribeRequest>())).ReturnsAsync(0);
        _desktop = new Mock<IDesktop>();
        _log = new RecordingLogger<StartupReport>();
        _store = new FakeCredentialStore();
    }

    /// <summary>Deletes the temporary folders.</summary>
    [TearDown]
    public void TearDown()
    {
        _settingsFolder.Dispose();
        _recordings.Dispose();
        _runtime.Dispose();
    }

    /// <summary>The report under test, with Google Drive as <see cref="_store"/> has it: not set up unless a test signs in.</summary>
    private StartupReport Report() =>
        new(_runtime.Journal, new ClassFolders(_options, _runtime.Time, new RecordingLogger<ClassFolders>()), new RecordingFolder(), Printed.Commands, _commands.Object, _settings.Gate(), _ui, _desktop.Object,
            _runtime.Processes, DriveQueue(), _options, ZonedClock.Eastern(_runtime.Time), _log);

    /// <summary>What the report reads of Google Drive's queue: the store, and the manifests in ClassScribe's own folder.</summary>
    private DriveQueueReport DriveQueue() =>
        new(_store, Manifests(), _runtime.Journal, new ClassFolders(_options, _runtime.Time, new RecordingLogger<ClassFolders>()), TestOptions.Create(new DriveOptions()), ZonedClock.Eastern(_runtime.Time));

    /// <summary>The upload manifests in ClassScribe's own folder.</summary>
    private UploadManifests Manifests() => new(_runtime.Folder, _runtime.Time, NullLogger<UploadManifests>.Instance);

    /// <summary>The run as its file says now.</summary>
    private RunRecord Run(string? runId = null) => _runtime.Journal.Find(runId ?? RunId)!.Record;

    /// <summary>
    /// The recording "Kalam" that started at 23:00:02 UTC: 00:47:10 of audio, last written at <see cref="LastAudio"/>; or
    /// the recording of <paramref name="name"/> that started at <paramref name="startedUtc"/>, last written at
    /// <paramref name="lastWrittenUtc"/>, when given. It is flat in the folder of its name, as recordings were kept before
    /// the course and date folders, or in <paramref name="dateFolder"/> there, such as 2026-10-15.
    /// </summary>
    private RecordingFiles WriteRecording(string name = "Kalam", DateTimeOffset? startedUtc = null, DateTimeOffset? lastWrittenUtc = null, string? dateFolder = null)
    {
        var course = Path.Combine(_recordings.Path, name);
        var files = RecordingFiles.ForNew(dateFolder is null ? course : Path.Combine(course, dateFolder), name, startedUtc ?? TestRuns.Start);
        Directory.CreateDirectory(files.Folder);
        using (var writer = new WavSegmentWriter(files.Segment(1), 10)) writer.Write(new byte[2830 * 10 * 2]);
        File.SetLastWriteTimeUtc(files.Segment(1), (lastWrittenUtc ?? LastAudio).UtcDateTime);
        return files;
    }

    /// <summary>
    /// The run file of the run that recorded <paramref name="files"/> in <paramref name="state"/>, as its recorder last wrote
    /// it, at <see cref="LastHeartbeat"/>: a separate recorder, process 4321, which is gone unless the test starts it.
    /// </summary>
    private RunRecord WriteRun(RecordingFiles files, RunState state = RunState.Recording, string? runId = null, Func<RunRecord, RunRecord>? change = null)
    {
        var id = runId ?? RunId;
        var record = TestRuns.Record(id, state, name: files.Name) with
        {
            Request = TestRuns.Request(id, files.Name) with { OutputRoot = _recordings.Path, Minutes = 90, Continue = ContinueChoice.Ask },
            BaseName = files.BaseName,
            Folder = files.Folder,
            HeartbeatUtc = LastHeartbeat,
            Position = TimeSpan.FromSeconds(2823)
        };
        record = change?.Invoke(record) ?? record;
        _runtime.Journal.Write(_runtime.Journal.RunFile(id), record);
        return record;
    }

    /// <summary>
    /// A recording whose recorder ended while it recorded is reported with when it ended (the later of its last heartbeat
    /// and the last audio written), how far in, where to look when nobody stopped it, and C, T, F and Enter. Enter goes on,
    /// and the run is reported again at the next start.
    /// </summary>
    [Test]
    public async Task An_unexpected_end_is_reported_with_continue_transcribe_and_forget()
    {
        WriteRun(WriteRecording());
        _ui.Type('\r');

        var choice = await Report().ShowAsync(interactive: true);

        Assert.That(choice, Is.Null);
        Assert.That(_ui.Screen, Is.EqualTo(string.Join(Environment.NewLine,
            "The recording \"Kalam\" (Kalam_2026-10-15_23-00-02Z) ended unexpectedly at about 19:47, 00:47:10 in. Its audio is kept.",
            "If you did not stop it, check Bitdefender's Notifications (Protection > Advanced Threat Defense).",
            "  C  continue it now as its next part     T  transcribe what was recorded",
            "  F  forget this                          Enter  go on to the menu")));
        Assert.That(Run().Seen, Is.False);
        _commands.VerifyNoOtherCalls();
    }

    /// <summary>A heartbeat later than the last audio written gives the time.</summary>
    [Test]
    public async Task The_time_is_the_later_of_the_last_heartbeat_and_the_last_audio()
    {
        WriteRun(WriteRecording(), change: record => record with { HeartbeatUtc = TestRuns.Start.AddMinutes(49) });
        _ui.Type('\r');

        await Report().ShowAsync(interactive: true);

        Assert.That(_ui.Screen, Does.Contain("ended unexpectedly at about 19:49, 00:47:10 in."));
    }

    /// <summary>
    /// Audio written later than the last heartbeat gives the time: the heartbeat comes every 10 seconds and the audio is
    /// written more often, so the audio is usually the later of the two, and here it moves the minute shown.
    /// </summary>
    [Test]
    public async Task Audio_written_after_the_last_heartbeat_gives_the_time()
    {
        WriteRun(WriteRecording(lastWrittenUtc: TestRuns.Start.AddMinutes(48).AddSeconds(8)));
        _ui.Type('\r');

        await Report().ShowAsync(interactive: true);

        Assert.That(_ui.Screen, Does.Contain("ended unexpectedly at about 19:48, 00:47:10 in."));
    }

    /// <summary>An end on an earlier day than today says the day as well.</summary>
    [Test]
    public async Task An_end_on_an_earlier_day_names_the_day()
    {
        WriteRun(WriteRecording());
        _runtime.Time.Advance(TimeSpan.FromHours(14));

        await Report().ShowAsync(interactive: false);

        Assert.That(_ui.Screen, Does.Contain("ended unexpectedly at about 19:47 on 15 October, 00:47:10 in."));
    }

    /// <summary>When Windows started again after the last heartbeat, the report says Windows restarted instead of naming Bitdefender.</summary>
    [Test]
    public async Task A_restart_after_the_last_heartbeat_is_named_instead_of_Bitdefender()
    {
        WriteRun(WriteRecording());
        _runtime.Processes.BootUtc = LastAudio.AddMinutes(2);
        _ui.Type('\r');

        await Report().ShowAsync(interactive: true);

        Assert.That(_ui.Screen, Does.Contain("Windows restarted at about 19:47."));
        Assert.That(_ui.Screen, Does.Not.Contain("Bitdefender"));
    }

    /// <summary>A run whose recorder heard Windows end the session says so instead of naming Bitdefender.</summary>
    [Test]
    public async Task Windows_ending_the_session_is_named()
    {
        WriteRun(WriteRecording(), change: record => record with { EndedBy = RunEndCause.Windows });
        _ui.Type('\r');

        await Report().ShowAsync(interactive: true);

        Assert.That(_ui.Screen, Does.Contain("Windows shut down or signed you out at about 19:47."));
        Assert.That(_ui.Screen, Does.Not.Contain("Bitdefender"));
    }

    /// <summary>A run that an error nobody caught took down says so, and names the recording's log, which holds the error, instead of Bitdefender.</summary>
    [Test]
    public async Task An_error_that_ended_the_run_is_named_with_the_recordings_log()
    {
        var files = WriteRecording();
        WriteRun(files, change: record => record with { EndedBy = RunEndCause.Error });
        _ui.Type('\r');

        await Report().ShowAsync(interactive: true);

        Assert.That(_ui.Screen, Does.Contain($"It stopped because of an error at about 19:47; {files.Log} says which."));
        Assert.That(_ui.Screen, Does.Not.Contain("Bitdefender"));
    }

    /// <summary>A run that recorded in its window, which was closed, says so.</summary>
    [Test]
    public async Task A_closed_window_is_named()
    {
        WriteRun(WriteRecording(), change: record => record with { EndedBy = RunEndCause.WindowClosed, Home = RunHome.InWindow, Pipe = null });
        _ui.Type('\r');

        await Report().ShowAsync(interactive: true);

        Assert.That(_ui.Screen, Does.Contain("It ended when its window was closed at about 19:47."));
        Assert.That(_ui.Screen, Does.Not.Contain("Bitdefender"));
    }

    /// <summary>
    /// C continues the recording as its next part with the run's own request (its name, source, recordings folder and
    /// transcribe choice, but no --minutes), as a run started from the report, and the run is not reported again.
    /// </summary>
    [Test]
    public async Task C_continues_the_recording_with_the_runs_own_request()
    {
        var run = WriteRun(WriteRecording());
        var started = new List<(RecordRequest Request, StartOrigin Origin)>();
        _commands.Setup(commands => commands.RecordAsync(It.IsAny<RecordRequest>(), It.IsAny<StartOrigin>()))
            .Callback((RecordRequest request, StartOrigin origin) => started.Add((request, origin)))
            .ReturnsAsync(0);
        _ui.Type('c');

        var choice = await Report().ShowAsync(interactive: true);

        Assert.That(choice, Is.EqualTo(new StartupChoice(RunId, StartupAction.Continue)));
        var (request, origin) = started.Single();
        Assert.That(origin, Is.EqualTo(StartOrigin.StartupReport));
        Assert.That(request.RunId, Is.Not.EqualTo(RunId));
        Assert.That(RunIds.IsWellFormed(request.RunId), Is.True);
        Assert.That(request, Is.EqualTo(run.Request with
        {
            RunId = request.RunId,
            Continue = ContinueChoice.Continue,
            ContinueBaseName = "Kalam_2026-10-15_23-00-02Z",
            Minutes = null
        }));
        Assert.That(Run().Seen, Is.True);
    }

    /// <summary>
    /// A recording in its date folder that ended unexpectedly is reported as one of the old layout is, with C to continue
    /// it, which hands on the run's own request naming it; the run that takes the request finds it in its date folder.
    /// </summary>
    [Test]
    public async Task A_recording_in_a_date_folder_is_reported_with_C_to_continue_it()
    {
        var files = WriteRecording(dateFolder: "2026-10-15 (2)");
        WriteRun(files);
        var started = new List<RecordRequest>();
        _commands.Setup(commands => commands.RecordAsync(It.IsAny<RecordRequest>(), It.IsAny<StartOrigin>()))
            .Callback((RecordRequest request, StartOrigin _) => started.Add(request))
            .ReturnsAsync(0);
        _ui.Type('c');

        var choice = await Report().ShowAsync(interactive: true);

        Assert.That(choice, Is.EqualTo(new StartupChoice(RunId, StartupAction.Continue)));
        Assert.That(_ui.Screen, Does.Contain("  C  continue it now as its next part"));
        Assert.That(started.Single().ContinueBaseName, Is.EqualTo(files.BaseName));
    }

    /// <summary>
    /// A newer recording of the same name in the other layout, here in a date folder after a lost one flat in the folder of
    /// its name, is the one a new run of the name continues, so C is not offered for the older one.
    /// </summary>
    [Test]
    public async Task C_is_not_offered_when_a_newer_recording_of_the_name_is_in_the_other_layout()
    {
        WriteRun(WriteRecording());
        WriteRecording(startedUtc: LastAudio.AddMinutes(1), lastWrittenUtc: LastAudio.AddMinutes(3), dateFolder: "2026-10-15");
        _ui.Type('\r');

        await Report().ShowAsync(interactive: true);

        Assert.That(_ui.Screen, Does.Contain("  T  transcribe what was recorded"));
        Assert.That(_ui.Screen, Does.Not.Contain("C  continue"));
    }

    /// <summary>T transcribes what was recorded, by the recording's first file, which names exactly that recording, and the run is not reported again.</summary>
    [Test]
    public async Task T_transcribes_what_was_recorded()
    {
        var files = WriteRecording();
        WriteRun(files);
        _ui.Type('t');

        var choice = await Report().ShowAsync(interactive: true);

        Assert.That(choice, Is.EqualTo(new StartupChoice(RunId, StartupAction.Transcribe)));
        _commands.Verify(commands => commands.TranscribeAsync(new TranscribeRequest(files.Segment(1), null, true, false, false)), Times.Once);
        Assert.That(Run().Seen, Is.True);
    }

    /// <summary>F forgets the run: nothing runs, and it is not reported again.</summary>
    [Test]
    public async Task F_forgets_the_run()
    {
        WriteRun(WriteRecording());
        _ui.Type('x', 'f');

        var choice = await Report().ShowAsync(interactive: true);
        var again = await Report().ShowAsync(interactive: false);

        Assert.That(choice, Is.EqualTo(new StartupChoice(RunId, StartupAction.Forget)));
        Assert.That(Run().Seen, Is.True);
        Assert.That(again, Is.Null);
        _commands.VerifyNoOtherCalls();
    }

    /// <summary>Once the recording stopped longer ago than Recording:ContinueWithinMinutes, C is not offered, and pressing it does nothing.</summary>
    [Test]
    public async Task C_is_not_offered_once_the_recording_cannot_be_continued()
    {
        WriteRun(WriteRecording());
        _runtime.Time.Advance(TimeSpan.FromMinutes(57));
        _ui.Type('c', '\r');

        await Report().ShowAsync(interactive: true);

        Assert.That(_ui.Screen, Does.Contain(
            $"{Environment.NewLine}{"  T  transcribe what was recorded".PadRight(42)}F  forget this{Environment.NewLine}  Enter  go on to the menu"));
        Assert.That(_ui.Screen, Does.Not.Contain("C  continue"));
        _commands.VerifyNoOtherCalls();
    }

    /// <summary>
    /// A newer recording of the same name, stopped since without a transcript, is the one a new run of that name continues,
    /// so C is not offered for the older one that ended unexpectedly, and pressing it does nothing; T and F are still offered.
    /// </summary>
    [Test]
    public async Task C_is_not_offered_when_a_newer_recording_of_the_name_is_the_one_to_continue()
    {
        WriteRun(WriteRecording());
        WriteRecording(startedUtc: LastAudio.AddMinutes(1), lastWrittenUtc: LastAudio.AddMinutes(3));
        _ui.Type('c', '\r');

        await Report().ShowAsync(interactive: true);

        Assert.That(_ui.Screen, Does.StartWith("The recording \"Kalam\" (Kalam_2026-10-15_23-00-02Z) ended unexpectedly"));
        Assert.That(_ui.Screen, Does.Contain("  T  transcribe what was recorded"));
        Assert.That(_ui.Screen, Does.Not.Contain("C  continue"));
        Assert.That(Run().Seen, Is.False);
        _commands.VerifyNoOtherCalls();
    }

    /// <summary>
    /// C and T end the report once their work is done: another run that ended unexpectedly is not shown this time, and is
    /// still reported at the next start. The newest run is reported first.
    /// </summary>
    [TestCase('c', StartupAction.Continue)]
    [TestCase('t', StartupAction.Transcribe)]
    public async Task C_or_T_ends_the_report_and_leaves_the_other_runs_for_the_next_start(char key, StartupAction action)
    {
        WriteRun(WriteRecording());
        WriteRun(WriteRecording("Fiqh"), runId: TestRuns.Id(2), change: record => record with { StartedUtc = TestRuns.Start.AddMinutes(-1) });
        _ui.Type(key, '\r');

        var choice = await Report().ShowAsync(interactive: true);

        Assert.That(choice, Is.EqualTo(new StartupChoice(RunId, action)));
        Assert.That(_ui.Screen, Does.StartWith("The recording \"Kalam\" (Kalam_2026-10-15_23-00-02Z) ended unexpectedly"));
        Assert.That(_ui.Screen, Does.Not.Contain("Fiqh"));
        Assert.That(_ui.KeyReads, Is.EqualTo(1));
        Assert.That(Run().Seen, Is.True);
        Assert.That(Run(TestRuns.Id(2)).Seen, Is.False, "reported at the next start");
    }

    /// <summary>While another recording runs, C is not offered: one recording at a time.</summary>
    [Test]
    public async Task C_is_not_offered_while_another_recording_runs()
    {
        WriteRun(WriteRecording());
        WriteRun(WriteRecording("Fiqh"), runId: TestRuns.Id(2), change: record => record with { ProcessId = 5000 });
        _runtime.Processes.Start(5000, TestRuns.ProcessStart);
        _ui.Type('\r');

        await Report().ShowAsync(interactive: true);

        Assert.That(_ui.Screen, Does.Not.Contain("C  continue"));
    }

    /// <summary>
    /// The settings are read again and checked before T runs; settings that cannot be used run nothing and leave the run to
    /// report again. C continues a class, which a refused file never stops (build-plan amendment 4): it runs without the
    /// setting the check refuses.
    /// </summary>
    [Test]
    public async Task T_runs_only_once_the_settings_pass_the_check_and_C_runs_without_what_they_refuse()
    {
        var files = WriteRecording();
        WriteRun(files, RunState.Transcribing);
        _settings.Write("{ \"Recording\": { \"FlushSeconds\": \"abc\" } }");
        _ui.Type('t');

        var choice = await Report().ShowAsync(interactive: true);

        Assert.That(choice, Is.Null);
        Assert.That(Run().Seen, Is.False);
        _commands.VerifyNoOtherCalls();

        WriteRun(files);
        _ui.Type('c');
        var continued = await Report().ShowAsync(interactive: true);

        Assert.That(continued, Is.EqualTo(new StartupChoice(RunId, StartupAction.Continue)));
        _commands.Verify(commands => commands.RecordAsync(It.IsAny<RecordRequest>(), StartOrigin.StartupReport), Times.Once);
    }

    /// <summary>A transcription that was interrupted is reported: its audio is complete, T transcribes it, and a new try may be billed.</summary>
    [Test]
    public async Task An_interrupted_transcription_is_reported_with_transcribe()
    {
        var files = WriteRecording();
        WriteRun(files, RunState.Transcribing);
        _ui.Type('t');

        var choice = await Report().ShowAsync(interactive: true);

        Assert.That(_ui.Screen, Does.StartWith(
            "The transcript of \"Kalam\" (Kalam_2026-10-15_23-00-02Z) was not finished; its audio is complete. If the upload had finished, ElevenLabs may bill it again."));
        Assert.That(_ui.Screen, Does.Contain("  T  transcribe it"));
        Assert.That(choice, Is.EqualTo(new StartupChoice(RunId, StartupAction.Transcribe)));
        _commands.Verify(commands => commands.TranscribeAsync(It.Is<TranscribeRequest>(request => request.Path == files.Segment(1))), Times.Once);
    }

    /// <summary>
    /// A transcript made while no window showed the run is reported once, with O to open it; any other key goes on. It is
    /// not reported again.
    /// </summary>
    [Test]
    public async Task An_outcome_nobody_saw_is_shown_once_and_O_opens_the_transcript()
    {
        var files = WriteRecording();
        File.WriteAllText(files.Transcript(), "# Kalam");
        WriteRun(files, RunState.Finished, change: record => record with
        {
            Outcome = new RunOutcome(OutcomeKind.Transcribed, 0, "Transcript saved.", files.Transcript(), files.Folder, null)
        });
        _ui.Type('o');

        var choice = await Report().ShowAsync(interactive: true);
        var again = await Report().ShowAsync(interactive: false);

        Assert.That(_ui.Screen, Does.StartWith($"\"Kalam\" was transcribed in the background: {files.Transcript()}. O opens it; any other key goes on."));
        Assert.That(choice, Is.EqualTo(new StartupChoice(RunId, StartupAction.Open)));
        _desktop.Verify(desktop => desktop.OpenFile(files.Transcript()), Times.Once);
        Assert.That(Run().Seen, Is.True);
        Assert.That(again, Is.Null);
        Assert.That(_ui.Screen.Split(Environment.NewLine).Count(line => line.Contains("was transcribed in the background")), Is.EqualTo(1));
    }

    /// <summary>A run that failed while no window showed it is reported once with what went wrong and its command to try again, without a question.</summary>
    [Test]
    public async Task A_failure_nobody_saw_is_shown_with_its_command_to_try_again()
    {
        var files = WriteRecording();
        WriteRun(files, RunState.Finished, change: record => record with
        {
            Outcome = new RunOutcome(OutcomeKind.TranscriptFailed, 1, "The transcript failed: ElevenLabs did not answer.", null, files.Folder, "& \"ClassScribe.exe\" transcribe \"x.wav\"")
        });

        var choice = await Report().ShowAsync(interactive: true);

        Assert.That(choice, Is.Null);
        Assert.That(_ui.Screen, Is.EqualTo(string.Join(Environment.NewLine,
            "\"Kalam\" ended in the background: The transcript failed: ElevenLabs did not answer.",
            "To try again: & \"ClassScribe.exe\" transcribe \"x.wav\"")));
        Assert.That(_ui.KeyReads, Is.Zero);
        Assert.That(Run().Seen, Is.True);
    }

    /// <summary>
    /// A run's own failure message often ends with the command that tries again, as the record command writes it; the
    /// report then shows the message alone, so that the command is not shown twice.
    /// </summary>
    [Test]
    public async Task A_failure_whose_message_already_holds_the_command_shows_it_once()
    {
        var files = WriteRecording();
        const string command = "& \"ClassScribe.exe\" transcribe \"x.wav\"";
        WriteRun(files, RunState.Finished, change: record => record with
        {
            Outcome = new RunOutcome(OutcomeKind.TranscriptFailed, 1, $"The audio is safe in {files.Folder}. To try the transcript again, choose 3 in the menu, or run: {command}", null, files.Folder, command)
        });

        await Report().ShowAsync(interactive: true);

        Assert.That(_ui.Screen, Does.StartWith($"\"Kalam\" ended in the background: The audio is safe in {files.Folder}. To try the transcript again"));
        Assert.That(_ui.Screen, Does.Not.Contain("To try again:"));
        Assert.That(_ui.Screen.Split(command).Length - 1, Is.EqualTo(1), "the command once");
    }

    /// <summary>
    /// A message from elsewhere, such as a run's outcome or Windows' words, is ended as a sentence once: a full stop is added
    /// only when it has no full stop, question mark or exclamation mark of its own, and trailing spaces go.
    /// </summary>
    [TestCase("Transcript saved.", "Transcript saved.")]
    [TestCase("No audio was captured. Was anything playing?", "No audio was captured. Was anything playing?")]
    [TestCase("Stopped!", "Stopped!")]
    [TestCase("Windows did not start the recorder: Access is denied (error 5)", "Windows did not start the recorder: Access is denied (error 5).")]
    [TestCase("Over. ", "Over.")]
    public void A_message_is_ended_as_a_sentence_once(string message, string sentence) => Assert.That(Sentence.Of(message), Is.EqualTo(sentence));

    /// <summary>An outcome nobody saw whose message ends with a question keeps its question mark, with no full stop after it.</summary>
    [Test]
    public async Task An_outcome_that_ends_with_a_question_keeps_its_question_mark()
    {
        var files = WriteRecording();
        WriteRun(files, RunState.Finished, change: record => record with
        {
            Outcome = new RunOutcome(OutcomeKind.NothingRecorded, 1, "No audio was captured. Was anything playing?", null, files.Folder, null)
        });

        await Report().ShowAsync(interactive: true);

        Assert.That(_ui.Screen, Is.EqualTo("\"Kalam\" ended in the background: No audio was captured. Was anything playing?"));
        Assert.That(_ui.KeyReads, Is.Zero);
    }

    /// <summary>
    /// A recording another program holds open without sharing cannot be looked at to see whether it may be continued: the
    /// run is reported all the same, with how far in by its last heartbeat, but without C, and the warning says just that,
    /// not that a new recording starts, since nothing starts here.
    /// </summary>
    [Test]
    public async Task A_recording_that_cannot_be_read_now_is_reported_without_C()
    {
        var files = WriteRecording();
        WriteRun(files);
        using var held = new FileStream(files.Segment(1), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        _ui.Type('\r');

        await Report().ShowAsync(interactive: true);

        Assert.That(_ui.Screen, Does.StartWith(
            "The recording \"Kalam\" (Kalam_2026-10-15_23-00-02Z) ended unexpectedly at about 19:47, 00:47:03 in. Its audio is kept."));
        Assert.That(_ui.Screen, Does.Not.Contain("C  continue"));
        Assert.That(_log.Problems, Is.EqualTo(new[] { "Could not look for a recording of \"Kalam\" to continue; it is not offered to be continued this time" }));
    }

    /// <summary>On the command line the report prints the commands that would deal with the run, asks nothing, and reports it again next time.</summary>
    [Test]
    public async Task On_the_command_line_only_the_commands_are_printed()
    {
        var files = WriteRecording();
        var run = WriteRun(files);

        var choice = await Report().ShowAsync(interactive: false);

        var continued = run.Request with { Continue = ContinueChoice.Continue, ContinueBaseName = run.BaseName, Minutes = null };
        Assert.That(choice, Is.Null);
        Assert.That(_ui.Screen, Is.EqualTo(string.Join(Environment.NewLine,
            "The recording \"Kalam\" (Kalam_2026-10-15_23-00-02Z) ended unexpectedly at about 19:47, 00:47:10 in. Its audio is kept.",
            "If you did not stop it, check Bitdefender's Notifications (Protection > Advanced Threat Defense).",
            $"To continue it as its next part: {Printed.Record(continued)}",
            $"To transcribe what was recorded: {Printed.Transcribe(files.Segment(1))}",
            "To forget it: start ClassScribe without a command, then press F.")));
        Assert.That(_ui.KeyReads, Is.Zero);
        Assert.That(Run().Seen, Is.False);
    }

    /// <summary>A recording continued by a later run, or transcribed since, has been dealt with: it is not reported, and is marked seen.</summary>
    [Test]
    public async Task A_recording_dealt_with_since_is_not_reported()
    {
        var continuedFiles = WriteRecording();
        WriteRun(continuedFiles);
        WriteRun(continuedFiles, RunState.Finished, runId: TestRuns.Id(2), change: record => record with { StartedUtc = LastAudio.AddMinutes(1), Seen = true });
        var transcribedFiles = WriteRecording("Fiqh");
        File.WriteAllText(transcribedFiles.Transcript(), "# Fiqh");
        WriteRun(transcribedFiles, runId: TestRuns.Id(3));

        var choice = await Report().ShowAsync(interactive: false);

        Assert.That(choice, Is.Null);
        Assert.That(_ui.Screen, Is.Empty);
        Assert.That(Run().Seen, Is.True);
        Assert.That(Run(TestRuns.Id(3)).Seen, Is.True);
    }

    /// <summary>
    /// A recording on a drive that is not there now, such as a USB drive unplugged, is not reported this time and not
    /// marked seen, so that it is reported once the drive is back; one whose folder was deleted from a drive that is there
    /// has nothing left to deal with.
    /// </summary>
    [Test]
    public async Task A_recording_on_a_drive_that_is_not_there_now_is_reported_later()
    {
        var drive = Enumerable.Range('D', 'Z' - 'D' + 1).Select(letter => $"{(char)letter}:\\").Reverse().FirstOrDefault(root => !Directory.Exists(root));
        Assume.That(drive, Is.Not.Null, "every drive letter is in use on this machine");
        WriteRun(new RecordingFiles(Path.Combine(drive!, "Classes", "Kalam"), "Kalam_2026-10-15_23-00-02Z"));
        WriteRun(new RecordingFiles(Path.Combine(_recordings.Path, "Fiqh"), "Fiqh_2026-10-15_23-00-02Z"), runId: TestRuns.Id(2));

        var choice = await Report().ShowAsync(interactive: false);

        Assert.That(choice, Is.Null);
        Assert.That(_ui.Screen, Is.Empty);
        Assert.That(Run().Seen, Is.False, "reported once the drive is back");
        Assert.That(Run(TestRuns.Id(2)).Seen, Is.True, "its folder is gone from a drive that is there");
    }

    /// <summary>
    /// The menu's header names the recording in the background, since when and for how long, with A to show it, and each
    /// transcription in the background with how far its upload is.
    /// </summary>
    [Test]
    public void The_header_names_the_recording_and_the_transcriptions_in_the_background()
    {
        _runtime.Time.Advance(TestRuns.Start.AddMinutes(42).AddSeconds(13) - _runtime.Time.GetUtcNow());
        WriteRun(WriteRecording(), change: record => record with { StartedUtc = TestRuns.Start });
        _runtime.Processes.Start(TestRuns.ProcessId, TestRuns.ProcessStart);
        WriteRun(WriteRecording("Fiqh"), RunState.Transcribing, TestRuns.Id(2), record => record with { ProcessId = 5000, UploadPercent = 40 });
        _runtime.Processes.Start(5000, TestRuns.ProcessStart);

        var lines = Report().HeaderLines();

        Assert.That(lines, Is.EqualTo(new[]
        {
            "Recording now: \"Kalam\" since 19:00 (00:42:13). A shows it.",
            "Being transcribed in the background: \"Fiqh\" (uploading 40%)."
        }));
        Assert.That(Report().RecordingNow()?.RunId, Is.EqualTo(RunId));
    }

    /// <summary>A recording in another window has no recorder to show: the header says where it is and what closing that window does, and the menu does not attach to it.</summary>
    [Test]
    public void A_recording_in_another_window_is_named_and_not_shown()
    {
        WriteRun(WriteRecording(), change: record => record with { Home = RunHome.InWindow, Pipe = null });
        _runtime.Processes.Start(TestRuns.ProcessId, TestRuns.ProcessStart);

        Assert.That(Report().HeaderLines(), Is.EqualTo(new[]
        {
            "\"Kalam\" is being recorded in another ClassScribe window (process 4321), which shows it; closing that window ends the recording."
        }));
        Assert.That(Report().RecordingNow(), Is.Null);
    }

    /// <summary>
    /// A YouTube video's run is named as the video, never as its course, which a class of that name would be taken for: in the
    /// header while it records, and while a batch in another window transcribes one, which is said to be there.
    /// </summary>
    [Test]
    public void The_header_names_a_videos_run_as_the_video()
    {
        _runtime.Time.Advance(TestRuns.Start.AddMinutes(42).AddSeconds(13) - _runtime.Time.GetUtcNow());
        WriteRun(WriteRecording("Risala"), change: record => record with { StartedUtc = TestRuns.Start, Request = record.Request with { Video = YouTubeTestRuns.Video() } });
        _runtime.Processes.Start(TestRuns.ProcessId, TestRuns.ProcessStart);
        WriteRun(WriteRecording("Risala", TestRuns.Start.AddHours(-2)), RunState.Transcribing, TestRuns.Id(2), record => record with
        {
            ProcessId = 5000, Home = RunHome.InWindow, Pipe = null, UploadPercent = 40,
            Request = record.Request with { Video = YouTubeTestRuns.Video("bcdefghijkl", "The creed, part 2") }
        });
        _runtime.Processes.Start(5000, TestRuns.ProcessStart);

        Assert.That(Report().HeaderLines(), Is.EqualTo(new[]
        {
            "Recording now: the YouTube video \"The creed, part 1\" since 19:00 (00:42:13). A shows it.",
            "Being transcribed in another ClassScribe window (process 5000): the YouTube video \"The creed, part 2\" (uploading 40%)."
        }));
    }

    /// <summary>A YouTube video's recorder lost while it recorded is reported as the video's recording, never as a class of its course.</summary>
    [Test]
    public async Task A_lost_videos_recording_is_reported_as_the_videos()
    {
        WriteRun(WriteRecording("Risala"), change: record => record with { Request = record.Request with { Video = YouTubeTestRuns.Video() } });

        await Report().ShowAsync(interactive: false);

        Assert.That(_ui.Screen, Does.Contain("The recording of the YouTube video \"The creed, part 1\" (Risala_2026-10-15_23-00-02Z) ended unexpectedly at about"));
    }

    /// <summary>
    /// What came of a YouTube video's run while no window showed it is said of the video, never of a class of its course: at
    /// the start of the line, its words begin with a capital.
    /// </summary>
    [Test]
    public async Task A_videos_outcome_nobody_saw_is_said_of_the_video()
    {
        var files = WriteRecording("Risala");
        WriteRun(files, RunState.Finished, change: record => record with
        {
            Request = record.Request with { Video = YouTubeTestRuns.Video() },
            Outcome = new RunOutcome(OutcomeKind.Recorded, 0, "Recorded.", null, files.Folder, null)
        });

        await Report().ShowAsync(interactive: false);

        Assert.That(_ui.Screen, Is.EqualTo($"The YouTube video \"The creed, part 1\" was recorded in the background: {files.Folder}."));
    }

    /// <summary>On the command line, what runs in the background gets one line each, and the command carries on.</summary>
    [Test]
    public async Task On_the_command_line_what_runs_in_the_background_gets_one_line()
    {
        _runtime.Time.Advance(TestRuns.Start.AddMinutes(42).AddSeconds(13) - _runtime.Time.GetUtcNow());
        WriteRun(WriteRecording(), change: record => record with { StartedUtc = TestRuns.Start });
        _runtime.Processes.Start(TestRuns.ProcessId, TestRuns.ProcessStart);

        var choice = await Report().ShowAsync(interactive: false);

        Assert.That(choice, Is.Null);
        Assert.That(_ui.Screen, Is.EqualTo("Recording now in the background: \"Kalam\" since 19:00 (00:42:13). Start ClassScribe to see it."));
    }

    // ------------------------------------------------------------------ Google Drive

    /// <summary>Signs in to Google Drive, as a sign-in stored earlier would be.</summary>
    private void SignedInToDrive() => _store.Stored = FakeCredentialStore.With("a-refresh-token").Stored;

    /// <summary>Puts a recording of <paramref name="name"/> in Google Drive's queue, as one that could not go up, and returns it with its size as the lines say it.</summary>
    private (RecordingFiles Files, string Size) Waiting(string name = "Kalam")
    {
        var files = WriteRecording(name);
        Manifests().Write(Manifests().Queue(files) with
        {
            LastTriedUtc = LastAudio.AddMinutes(1),
            LastError = "Google Drive could not be reached (No such host is known.)."
        });
        return (files, Sizes.Describe(UploadManifests.OnDisk(files).Bytes));
    }

    /// <summary>
    /// In the menu, what waits to go up to Google Drive is said after the runs, with its size and its last try, and U uploads
    /// it now, through the drive command, once the settings pass; the report ends there.
    /// </summary>
    [Test]
    public async Task What_waits_for_Google_Drive_is_reported_with_U_to_upload_it()
    {
        SignedInToDrive();
        var (files, size) = Waiting();
        _commands.Setup(commands => commands.DriveUploadAsync(null)).ReturnsAsync(0);
        _ui.Type('U');

        var choice = await Report().ShowAsync(interactive: true);

        Assert.That(choice, Is.EqualTo(new StartupChoice(null, StartupAction.Upload)));
        Assert.That(_ui.Screen, Is.EqualTo(string.Join(Environment.NewLine,
            $"Google Drive: 1 recording waits to go up ({files.BaseName}, {size}; last try 15 Oct 19:48: Google Drive could not be reached (No such host is known.)).",
            "  U  upload it now                        Enter  go on to the menu")));
        _commands.Verify(commands => commands.DriveUploadAsync(null), Times.Once);
    }

    /// <summary>Enter goes on to the menu without uploading; what waits is offered again at the next start.</summary>
    [Test]
    public async Task Enter_goes_on_and_nothing_is_uploaded()
    {
        SignedInToDrive();
        Waiting();
        Waiting("Fiqh");
        _ui.Type('\r');

        var choice = await Report().ShowAsync(interactive: true);

        Assert.That(choice, Is.Null);
        Assert.That(_ui.Screen, Does.StartWith("Google Drive: 2 recordings wait to go up (").And.Contain(" in all; last try 15 Oct 19:48: ").And.Contain("  U  upload them now"));
        _commands.Verify(commands => commands.DriveUploadAsync(It.IsAny<string?>()), Times.Never);
    }

    /// <summary>U runs nothing while the settings cannot be used, as a menu choice; the report ends, and the menu shows why.</summary>
    [Test]
    public async Task U_uploads_nothing_while_the_settings_cannot_be_used()
    {
        SignedInToDrive();
        Waiting();
        _settings.Write("{ \"Recording\": { \"FlushSeconds\": \"abc\" } }");
        _ui.Type('U');

        Assert.That(await Report().ShowAsync(interactive: true), Is.Null);

        _commands.Verify(commands => commands.DriveUploadAsync(It.IsAny<string?>()), Times.Never);
    }

    /// <summary>Not signed in, what waits is said with how to sign in, and no key is asked for, since nothing could go up.</summary>
    [Test]
    public async Task Not_signed_in_what_waits_is_said_with_how_to_sign_in()
    {
        _store.Stored = FakeCredentialStore.With(null).Stored;
        var (files, size) = Waiting();

        var choice = await Report().ShowAsync(interactive: true);

        Assert.That(choice, Is.Null);
        Assert.That(_ui.Screen, Is.EqualTo(string.Join(Environment.NewLine,
            $"Google Drive: 1 recording waits to go up ({files.BaseName}, {size}; last try 15 Oct 19:48: Google Drive could not be reached (No such host is known.)).",
            "ClassScribe is not signed in to Google Drive: 9 > S in the menu signs in, then 9 > U uploads it.")));
        Assert.That(_ui.KeyReads, Is.Zero);
    }

    /// <summary>On the command line what waits is said with the command that uploads it, and nothing is asked.</summary>
    [Test]
    public async Task On_the_command_line_what_waits_is_said_with_the_command_that_uploads_it()
    {
        SignedInToDrive();
        var (files, size) = Waiting();

        Assert.That(await Report().ShowAsync(interactive: false), Is.Null);

        Assert.That(_ui.Screen, Is.EqualTo(string.Join(Environment.NewLine,
            $"Google Drive: 1 recording waits to go up ({files.BaseName}, {size}; last try 15 Oct 19:48: Google Drive could not be reached (No such host is known.)).",
            $"To upload it: {Printed.DriveUpload(null)}")));
        Assert.That(_ui.KeyReads, Is.Zero);
    }

    /// <summary>The menu's header ends with Google Drive's line, after the runs going on now; before Drive is set up it has none.</summary>
    [Test]
    public void The_header_ends_with_Google_Drives_line()
    {
        var (_, size) = Waiting();
        Assert.That(Report().HeaderLines(), Is.Empty, "not set up");

        SignedInToDrive();
        Assert.That(Report().HeaderLines(), Is.EqualTo(new[] { $"Google Drive: 1 recording waits to go up ({size}). 9 > U uploads it." }));
    }

    /// <summary>A run to report comes first, then what waits for Google Drive; a choice made about the run ends the report there.</summary>
    [Test]
    public async Task A_run_to_report_comes_before_Google_Drive()
    {
        SignedInToDrive();
        WriteRun(WriteRecording());
        Waiting("Fiqh");
        _ui.Type('\r');
        _ui.Type('\r');

        await Report().ShowAsync(interactive: true);

        var screen = _ui.Screen;
        var run = screen.IndexOf("ended unexpectedly", StringComparison.Ordinal);
        var drive = screen.IndexOf("Google Drive: ", StringComparison.Ordinal);
        Assert.That(run, Is.GreaterThanOrEqualTo(0), "the run is shown");
        Assert.That(drive, Is.GreaterThanOrEqualTo(0), "Google Drive's line is shown");
        Assert.That(run, Is.LessThan(drive));
    }

    /// <summary>With nothing to report, nothing is shown and no key is read.</summary>
    [Test]
    public async Task With_nothing_to_report_nothing_is_shown()
    {
        var choice = await Report().ShowAsync(interactive: true);

        Assert.That(choice, Is.Null);
        Assert.That(_ui.Screen, Is.Empty);
        Assert.That(_ui.KeyReads, Is.Zero);
        Assert.That(Report().HeaderLines(), Is.Empty);
        Assert.That(Report().RecordingNow(), Is.Null);
    }

    /// <summary>
    /// A YouTube video's run that ended unexpectedly is never offered to be continued: its recording is reported with T and
    /// F alone, whatever its files say.
    /// </summary>
    [Test]
    public async Task A_videos_run_is_reported_without_C()
    {
        var files = WriteRecording();
        WriteRun(files, change: record => record with { Request = record.Request with { Video = YouTubeTestRuns.Video() } });
        _ui.Type('\r');

        await Report().ShowAsync(interactive: true);

        Assert.That(_ui.Screen, Does.Contain("T  transcribe what was recorded"));
        Assert.That(_ui.Screen, Does.Not.Contain("C  continue"));
    }

    /// <summary>
    /// A class that ended unexpectedly is still offered C when a YouTube video of its course was recorded after it: the
    /// video is passed over, and the class is the newest recording of its name to continue.
    /// </summary>
    [Test]
    public async Task A_video_recorded_after_a_class_does_not_keep_C_from_the_class()
    {
        var lesson = WriteRecording();
        WriteRun(lesson);
        var video = WriteRecording(startedUtc: TestRuns.Start.AddMinutes(48), lastWrittenUtc: LastAudio.AddMinutes(1));
        VideoSourceFile.Write(video.Source, VideoSourceFileTests.Downloaded());
        _ui.Type('\r');

        await Report().ShowAsync(interactive: true);

        Assert.That(_ui.Screen, Does.Contain("C  continue it now as its next part"));
    }
}
