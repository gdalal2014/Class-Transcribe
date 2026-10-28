using System.Globalization;
using System.Net;
using System.Runtime.Versioning;
using ClassScribe.Audio;
using ClassScribe.Audio.Microphones;
using ClassScribe.Commands;
using ClassScribe.Desktop;
using ClassScribe.Desktop.Windows;
using ClassScribe.Link;
using ClassScribe.Recorder;
using ClassScribe.Settings;
using ClassScribe.Storage;
using ClassScribe.Transcription;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ClassScribe.Tests;

/// <summary>
/// One run of the record command, in this window or in a recorder, from the lock to the saved transcript: the real session,
/// recording loop, preparer and workflow, driven by a simulated audio device and keyboard, with the transcription service
/// mocked. ClassScribe's own folder (the lock and the run files) is a temporary folder of the test's, and Windows' end of
/// the session, the window's closing, an error nobody caught, the window's title and taskbar, keeping the laptop awake and
/// opening the results are stand-ins the test plays or looks at. A few tests drive a recording described by hand instead
/// of a session, or watch the calls made on it. What the recording loop does while the class records (the keys, the
/// warnings, the status line) is tested with the loop itself (see <see cref="RecordingLoopTests"/>).
/// </summary>
[TestFixture]
public partial class RecordCommandTests
{
    private static readonly AudioFormat Pcm16KMono = new(16000, 1, 16, false);

    /// <summary>The laptop awake and ClassScribe running, as every recording needs.</summary>
    private const PowerNeeds Awake = PowerNeeds.System | PowerNeeds.Execution;

    /// <summary>The reason Windows is given for keeping the laptop awake and for asking before a restart.</summary>
    private const string StayAwakeReason = "ClassScribe is recording a class";

    private TempFolder _folder = null!;
    private TestRuntime _runtime = null!;
    private FakeCaptureProvider _provider = null!;
    private FakeAudioCapture _capture = null!;
    private FakeUserSignals _signals = null!;
    private FakeConsoleUi _ui = null!;
    private Mock<ITranscriptionClient> _client = null!;
    private TranscriptionProgress _progress = null!;
    private FakeElevenLabsAccount _account = null!;
    private RecordingLogger<UsageReport> _usageLog = null!;
    private Mock<IDesktop> _desktop = null!;
    private Mock<IExitWarnings> _exitWarnings = null!;
    private Action? _processEnding;
    private FakeCrashWarnings _crashes = null!;

    /// <summary>How many crash warnings were registered when the run registered its exit warning; null before it did.</summary>
    private int? _crashWarningsBeforeExitWarning;

    private FakeWindowSignals _window = null!;
    private FakePowerRequests _power = null!;
    private FakePowerSource _powerSource = null!;
    private FakeSessionEvents _session = null!;
    private FakeStatusSink _status = null!;
    private FakeResultShower _results = null!;
    private RecorderLog _recorderLog = null!;
    private RunContextSlot _context = null!;
    private RecordingLogger<RecordCommand> _log = null!;
    private RecordingLogger<GlossaryChoice> _glossaryLog = null!;
    private RecordingLogger<RunBookkeeping> _bookkeepingLog = null!;
    private RecordingLogger<RecordingLoop> _loopLog = null!;
    private FakeSystemTimes _times = null!;
    private RunJournal _journal = null!;
    private SourceChoice _choice = null!;
    private RunBookkeeping? _bookkeeping;
    private List<RunHandle> _hostRuns = null!;
    private FakeDriveAutoUpload _drive = null!;

    /// <summary>
    /// Creates the recordings folder, ClassScribe's own folder, a simulated audio device with a capture ready, the
    /// keyboard and signals, the mocked services, the stand-ins for Windows, and the logs to look at.
    /// </summary>
    [SetUp]
    public void SetUp()
    {
        _folder = new TempFolder();
        _runtime = new TestRuntime();
        _provider = new FakeCaptureProvider();
        _capture = _provider.Enqueue(new FakeAudioCapture(Pcm16KMono));
        _signals = new FakeUserSignals();
        _ui = new FakeConsoleUi();
        _client = Mocks.TranscriptionClient();
        _progress = new TranscriptionProgress();
        _account = new FakeElevenLabsAccount();
        _usageLog = new RecordingLogger<UsageReport>();
        _desktop = new Mock<IDesktop>();
        _processEnding = null;
        _crashes = new FakeCrashWarnings();
        _crashWarningsBeforeExitWarning = null;
        _exitWarnings = new Mock<IExitWarnings>();
        _exitWarnings.Setup(w => w.Register(It.IsAny<Action>())).Callback((Action onExit) =>
        {
            _crashWarningsBeforeExitWarning = _crashes.Registered;
            _processEnding = onExit;
        }).Returns(Mock.Of<IDisposable>());
        _window = new FakeWindowSignals();
        _power = new FakePowerRequests();
        _powerSource = new FakePowerSource();
        _session = new FakeSessionEvents();
        _status = new FakeStatusSink();
        _recorderLog = new RecorderLog(TimeProvider.System);
        _context = new RunContextSlot();
        _log = new RecordingLogger<RecordCommand>();
        _glossaryLog = new RecordingLogger<GlossaryChoice>();
        _bookkeepingLog = new RecordingLogger<RunBookkeeping>();
        _loopLog = new RecordingLogger<RecordingLoop>();
        _bookkeeping = null;
        _hostRuns = [];
        _drive = new FakeDriveAutoUpload();
    }

    /// <summary>
    /// Ends the run's scope, lets go of the runs a test began as a recorder's host would, and deletes the folders. Deleting
    /// fails if a test left a file open, which is the point.
    /// </summary>
    [TearDown]
    public void TearDown()
    {
        _bookkeeping?.Dispose();
        foreach (var run in _hostRuns) run.Dispose();
        _recorderLog.Dispose();
        _runtime.Dispose();
        _folder.Dispose();
    }

    /// <summary>
    /// Builds the command with the real session, loop, preparer and workflow around the simulated device and the mocked
    /// service, or with <paramref name="recordings"/> when given. The recordings go to the temporary folder unless
    /// <paramref name="outputFolder"/> names another; ClassScribe's own folder is the test's, unless
    /// <paramref name="runtimeFolder"/> names another. The computer's clocks (<see cref="_times"/>) are read from
    /// <paramref name="time"/>. The run's states go to <see cref="_status"/> unless <paramref name="status"/> takes them.
    /// The run's bookkeeping is kept in <see cref="_bookkeeping"/>, standing for the run's scope; its handlers for the end of
    /// the process or the session wait for the log as long as <paramref name="handlerLogPatience"/> says, or as long as the
    /// app's do. A lesson's video is made by <paramref name="lessonVideos"/>, with what Drive makes of it from
    /// <paramref name="lessonDrive"/>, when given. The glossary's resolver has no saved classes, as a recorder's, and reads
    /// <paramref name="glossaryFiles"/> when given, else the real files.
    /// </summary>
    private RecordCommand CreateCommand(
        string apiKey = "test-key", TimeProvider? time = null, bool openWhenDone = false, string? outputFolder = null, KeepScreenOn keepScreenOn = KeepScreenOn.OnBattery,
        bool blockShutdown = true, IRecordingFactory? recordings = null, RuntimeFolder? runtimeFolder = null, ILogger<RecordCommand>? logger = null,
        IRecorderStatusSink? status = null, TimeSpan? handlerLogPatience = null, ClassAppMute classAppMute = ClassAppMute.Unknown, YouTube.VideoPlayback? video = null,
        IVideoYield? yield = null, Camera.LessonVideoMaker? lessonVideos = null, Drive.LessonVideoDrive? lessonDrive = null,
        IGlossaryFiles? glossaryFiles = null)
    {
        time ??= TimeProvider.System;
        var recording = TestOptions.Create(new RecordingOptions
        {
            OutputFolder = outputFolder ?? _folder.Path,
            OpenFolderWhenDone = openWhenDone,
            OpenTranscriptWhenDone = openWhenDone,
            KeepScreenOn = keepScreenOn,
            BlockShutdown = blockShutdown,
            ClassAppMute = classAppMute
        });
        var elevenLabs = TestOptions.Create(new ElevenLabsOptions { ApiKey = apiKey, KeytermsFile = _folder.File("no-glossary.txt") });
        var folders = new ClassFolders(recording, time, NullLogger<ClassFolders>.Instance);
        var marks = new ClassMarks(NullLogger<ClassMarks>.Instance);
        var usage = new UsageReport(_account, time, _usageLog);
        var glossaries = TestGlossaries.Choice(elevenLabs, logger: _glossaryLog, time: time, files: glossaryFiles);
        var workflow = new TranscriptionWorkflow(
            new AudioPreparer(NullLogger<AudioPreparer>.Instance),
            _client.Object,
            glossaries,
            new TranscriptFormatter(TestOptions.Create(new TranscriptOptions())),
            folders,
            marks,
            recording,
            elevenLabs,
            _progress,
            usage,
            NullLogger<TranscriptionWorkflow>.Instance);

        _times = new FakeSystemTimes(time);
        status ??= _status;
        var registry = new FakeRegistryValues()
            .Set(MicrophoneAdvice.VantageKey, MicrophoneAdvice.VantageValue, "0")
            .Set(MicrophoneAdvice.DuckingKey, MicrophoneAdvice.DuckingValue, 3);
        var bluetooth = new BluetoothMicrophoneWatch(new FakeMicrophoneSessions(), new FakeMicrophoneProvider(), new FakeOwnProcesses(), time, NullLogger<BluetoothMicrophoneWatch>.Instance);
        var monitor = new RecordingMonitor(new FreezeWatch(_times, time, NullLogger<FreezeWatch>.Instance), new FakeFreeSpace(), recording,
            new MicrophoneAdvice(registry, NullLogger<MicrophoneAdvice>.Instance), _provider, bluetooth, time, video, TestOptions.Create(new YouTubeOptions()));
        var loop = new RecordingLoop(_ui, _signals, marks, monitor, status, time, _loopLog);

        var ownFolder = runtimeFolder ?? _runtime.Folder;
        _journal = new RunJournal(ownFolder, _runtime.Processes, time, NullLogger<RunJournal>.Instance);
        _bookkeeping = new RunBookkeeping(
            new RecordingLock(ownFolder, NullLogger<RecordingLock>.Instance, _ => { }), _journal, _recorderLog, _power, _powerSource, _session, _exitWarnings.Object,
            _crashes, _window, _context, recording, time, _bookkeepingLog)
        {
            HandlerLogPatience = handlerLogPatience ?? RunBookkeeping.DefaultHandlerLogPatience
        };
        _results = new FakeResultShower(new WindowResultShower(_desktop.Object, recording, NullLogger<WindowResultShower>.Instance));
        _choice = new SourceChoice(recording);

        return new RecordCommand(
            recordings ?? new ClassRecordingFactory(_provider, recording, time, NullLogger<RecordingSession>.Instance, NullLogger<CaptureChooser>.Instance,
                NullLogger<ClassRecording>.Instance, video),
            loop, workflow, glossaries, _progress, usage, _signals, folders, marks, _bookkeeping, _results, status, _choice, _drive, recording, elevenLabs, time, logger ?? _log, video, yield, lessonVideos, lessonDrive);
    }

    /// <summary>
    /// What the transcript cost the account is the run's last line: read after the transcript is saved and the results are
    /// open, so that nothing anyone waits for is held up by it, from the read made as the upload began.
    /// </summary>
    [Test]
    public async Task The_accounts_credits_are_reported_after_the_transcript_is_saved_and_the_results_opened()
    {
        _account.Answer(FakeElevenLabsAccount.Read(0), FakeElevenLabsAccount.Read(500));
        (bool Saved, bool Opened)? atTheSecondRead = null;
        _account.OnRead = number =>
        {
            if (number == 2)
                atTheSecondRead = (_log.Messages.Any(message => message.StartsWith("Transcript saved to", StringComparison.Ordinal)), _desktop.Invocations.Count > 0);
        };
        var run = CreateCommand(openWhenDone: true).RunAsync(Request());
        await Wait.UntilAsync(() => _capture.Started);
        _capture.Deliver(TestAudio.Shorts(1000, -1000));
        _signals.RequestStop();

        Assert.That(await run, Is.EqualTo(0));
        Assert.That(atTheSecondRead, Is.EqualTo((true, true)), "saved and opened before the second read");
        Assert.That(UsageLines(), Is.EqualTo(["ElevenLabs charged 500 credits for this transcript; 99,500 of 100,000 credits are left"]));
        Assert.That(RunFile().Outcome?.Kind, Is.EqualTo(OutcomeKind.Transcribed));
    }

    /// <summary>
    /// The recording's glossary record is written as the run starts, before anything is transcribed, so that a recording made
    /// with record-only has it, from the glossary the request carries; the start says which glossary, after the start lines.
    /// </summary>
    [Test]
    public async Task The_glossary_record_is_written_at_the_start_and_a_record_only_recording_has_it()
    {
        var glossary = _folder.File("mantiq.txt");
        File.WriteAllLines(glossary, ["kalām", "manṭiq"]);
        var request = Request(transcribe: false) with { Glossary = new GlossaryPick("mantiq.txt", glossary, "the saved class \"Mantiq\"", "Mantiq") };
        var run = CreateCommand().RunAsync(request);
        await Wait.UntilAsync(() => _capture.Started);
        await Wait.UntilAsync(() => _glossaryLog.Messages.Count > 0);
        var files = new RecordingFiles(RunFile().Folder!, RunFile().BaseName!);
        var whileRecording = File.Exists(files.Glossary);
        _capture.Deliver(TestAudio.Shorts(1000, -1000));
        _signals.RequestStop();

        Assert.That(await run, Is.EqualTo(0));
        Assert.That(whileRecording, Is.True, "written as the run started");
        var record = new GlossaryFiles(new SettingsPlaces(_folder.Path, userFolder: null)).ReadRecord(files)!;
        Assert.That((record.Decided.Path, record.Decided.Terms, record.Decided.From, record.Sent.Count), Is.EqualTo((glossary, 2, "the saved class \"Mantiq\"", 0)));
        Assert.That(_glossaryLog.Messages, Is.EqualTo(new[] { "Glossary: mantiq.txt (the saved class \"Mantiq\"), 2 terms" }));
    }

    /// <summary>
    /// A class transcribed after it stops sends the glossary decided at its start, though the settings changed meanwhile, and
    /// the line before the upload says so; the transcript's entry is added to the record.
    /// </summary>
    [Test]
    public async Task The_transcript_after_the_class_sends_the_glossary_decided_at_its_start()
    {
        var glossary = _folder.File("start.txt");
        File.WriteAllLines(glossary, ["kalām"]);
        var sent = new List<IReadOnlyList<string>>();
        _client.Setup(Calls.AnyTranscription).Callback((string _, IReadOnlyList<string> terms, CancellationToken _) => sent.Add(terms)).ReturnsAsync(Mocks.Transcript);
        var run = CreateCommand().RunAsync(Request() with { Glossary = new GlossaryPick("start.txt", glossary, "ElevenLabs:KeytermsFile") });
        await Wait.UntilAsync(() => _capture.Started);
        _capture.Deliver(TestAudio.Shorts(1000, -1000));
        _signals.RequestStop();

        Assert.That(await run, Is.EqualTo(0));
        Assert.That(sent, Is.EqualTo(new[] { new[] { "kalām" } }));
        Assert.That(_glossaryLog.Messages, Is.EqualTo(new[]
        {
            "Glossary: start.txt (ElevenLabs:KeytermsFile), 1 term",
            "Glossary: start.txt, as decided when this recording started (1 term then, 1 now)"
        }));
        var files = new RecordingFiles(RunFile().Folder!, RunFile().BaseName!);
        Assert.That(new GlossaryFiles(new SettingsPlaces(_folder.Path, userFolder: null)).ReadRecord(files)!.Sent.Single().Copy, Is.EqualTo(1));
    }

    /// <summary>
    /// The run hands the glossary its start decided on to its transcript: with the recording's record damaged during the
    /// class, the transcript still sends the class's glossary the start line named, as a recorder, whose settings hold no saved
    /// classes, must, rather than ElevenLabs:KeytermsFile's.
    /// </summary>
    [Test]
    public async Task The_transcript_sends_the_glossary_the_start_named_when_the_record_is_damaged_during_the_class()
    {
        var glossary = _folder.File("mantiq.txt");
        File.WriteAllLines(glossary, ["kalām", "manṭiq"]);
        var sent = new List<IReadOnlyList<string>>();
        _client.Setup(Calls.AnyTranscription).Callback((string _, IReadOnlyList<string> terms, CancellationToken _) => sent.Add(terms)).ReturnsAsync(Mocks.Transcript);
        var run = CreateCommand().RunAsync(Request() with { ClassName = "Mantiq", Glossary = new GlossaryPick("mantiq.txt", glossary, "the saved class \"Mantiq\"", "Mantiq") });
        await Wait.UntilAsync(() => _capture.Started);
        await Wait.UntilAsync(() => RunFile().BaseName is { } baseName && File.Exists(new RecordingFiles(RunFile().Folder!, baseName).Glossary));
        var files = new RecordingFiles(RunFile().Folder!, RunFile().BaseName!);
        File.WriteAllText(files.Glossary, "not json {");
        _capture.Deliver(TestAudio.Shorts(1000, -1000));
        _signals.RequestStop();

        Assert.That(await run, Is.EqualTo(0));
        Assert.That(sent, Is.EqualTo(new[] { new[] { "kalām", "manṭiq" } }));
        Assert.That(_glossaryLog.Messages, Has.Exactly(2).EqualTo("Glossary: mantiq.txt (the saved class \"Mantiq\"), 2 terms"));
    }

    /// <summary>
    /// Nothing about the glossary may end a class: an error nobody expects at the start, where the class is recording already,
    /// is logged, and the class records on to its end, its recording kept.
    /// </summary>
    [Test]
    public async Task An_error_over_the_glossary_at_the_start_never_ends_the_class()
    {
        var run = CreateCommand(glossaryFiles: new FailingGlossaryFiles()).RunAsync(Request(transcribe: false));
        await Wait.UntilAsync(() => _capture.Started);
        await Wait.UntilAsync(() => _log.Problems.Any(problem => problem.StartsWith("Could not decide the recording's glossary", StringComparison.Ordinal)));
        _capture.Deliver(TestAudio.Shorts(1000, -1000));
        _signals.RequestStop();

        Assert.That(await run, Is.EqualTo(0));
        Assert.That(RunFile().Outcome?.Kind, Is.EqualTo(OutcomeKind.Recorded));
        Assert.That(_log.Problems, Has.Member(
            $"Could not decide the recording's glossary at its start ({FailingGlossaryFiles.Words}); the class records on, and its transcript decides the glossary when it is made"));
    }

    /// <summary>A reader that fails changes neither the exit code nor the outcome: the line says the credits could not be read, and that is all.</summary>
    [Test]
    public async Task A_failed_read_of_the_account_changes_neither_the_exit_code_nor_the_outcome()
    {
        _account.Failure = new InvalidOperationException("boom");
        var run = CreateCommand().RunAsync(Request());
        await Wait.UntilAsync(() => _capture.Started);
        _capture.Deliver(TestAudio.Shorts(1000, -1000));
        _signals.RequestStop();

        Assert.That(await run, Is.EqualTo(0));
        Assert.That(RunFile().Outcome?.Kind, Is.EqualTo(OutcomeKind.Transcribed));
        Assert.That(UsageLines(), Is.EqualTo(["The account's credits could not be read (the request failed: boom); the transcript is not affected"]));
        Assert.That(_usageLog.Problems, Is.Empty);
    }

    /// <summary>
    /// In a recorder the end has still to go to the windows, which open the results, once the run has returned: so the run
    /// leaves the account's report to its host, which makes it after that (see <see cref="RecorderHost"/>). The run itself
    /// makes the read before the upload alone, and says nothing of the account.
    /// </summary>
    [Test]
    public async Task In_a_recorder_the_accounts_report_is_left_to_the_host()
    {
        _account.Answer(FakeElevenLabsAccount.Read(0), FakeElevenLabsAccount.Read(500));
        var command = CreateCommand();
        var request = Request();
        BeginAsTheRecordersHost(request);

        var run = command.RunAsync(request);
        await Wait.UntilAsync(() => _capture.Started);
        _capture.Deliver(TestAudio.Shorts(1000, -1000));
        _signals.RequestStop();

        Assert.That(await run, Is.EqualTo(0));
        Assert.That(_account.Reads, Is.EqualTo(1), "the read before the upload alone");
        Assert.That(UsageLines(), Is.Empty, "the host reports, once the end has gone to the windows");
        Assert.That((RunFile().Outcome?.Kind, _results.Last?.Kind), Is.EqualTo((OutcomeKind.Transcribed, OutcomeKind.Transcribed)));
    }

    /// <summary>
    /// The recording goes into Google Drive's queue as its run starts, beside the run file, before anything is recorded, so
    /// that a class whose recorder crashes still goes up later; how the run ended is noted at its end. The upload itself is
    /// left to whoever ran the run, once the end is shown (see <see cref="RecorderHost"/> and <see cref="RecordStarter"/>).
    /// </summary>
    [Test]
    public async Task The_recording_goes_into_Google_Drives_queue_as_its_run_starts()
    {
        (RunState State, bool Capturing)? atTheQueue = null;
        _drive.OnQueue = _ => atTheQueue = (RunFile().State, _capture.Started);
        var run = CreateCommand().RunAsync(Request());
        await Wait.UntilAsync(() => _capture.Started);
        _capture.Deliver(TestAudio.Shorts(1000, -1000));
        _signals.RequestStop();

        Assert.That(await run, Is.EqualTo(0));
        var queued = _drive.Recordings.Single();
        Assert.That((queued.Folder, queued.BaseName), Is.EqualTo((KalamFolder(), Path.GetFileNameWithoutExtension(Recording()))));
        Assert.That(atTheQueue, Is.EqualTo((RunState.Recording, false)), "queued beside the run file, before the capture started");
        Assert.That(_drive.Calls, Is.EqualTo(new[] { $"Queue {queued.BaseName}", "Ended Transcribed" }));
    }

    /// <summary>Record-only's end is noted for Google Drive as well, for the upload after it.</summary>
    [Test]
    public async Task Record_only_s_end_is_noted_for_Google_Drive()
    {
        var run = CreateCommand().RunAsync(Request(transcribe: false));
        await Wait.UntilAsync(() => _capture.Started);
        _capture.Deliver(TestAudio.Shorts(1000, -1000));
        _signals.RequestStop();

        Assert.That(await run, Is.EqualTo(0));
        Assert.That(_drive.Calls.Last(), Is.EqualTo("Ended Recorded"));
    }

    /// <summary>A start the lock refused records nothing, so nothing goes into Google Drive's queue.</summary>
    [Test]
    public async Task A_refused_start_queues_nothing_for_Google_Drive()
    {
        using var other = OtherLock().TryTake();

        Assert.That(await EndingByItselfAsync(CreateCommand().RunAsync(Request())), Is.EqualTo(1));
        Assert.That(_drive.Calls, Is.Empty);
    }

    /// <summary>
    /// Waits for <paramref name="run"/>, which must end by itself, without a stop: a start that is refused, or one that fails
    /// before it records. Should it record instead, the test fails after 15 seconds, by name, rather than wait for a stop
    /// that never comes; the run is then stopped, and given a moment to let go of its files.
    /// </summary>
    private async Task<int> EndingByItselfAsync(Task<int> run)
    {
        try
        {
            return await run.WaitAsync(TimeSpan.FromSeconds(15));
        }
        catch (TimeoutException)
        {
            _signals.RequestStop();
            await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(15)));
            throw;
        }
    }

    /// <summary>
    /// A request to record <paramref name="name"/> from the device the class plays on into the temporary folder: to
    /// transcribe it unless <paramref name="transcribe"/> is false, as a new recording unless
    /// <paramref name="continueBaseName"/> names one to continue, until stopped unless <paramref name="minutes"/> is given,
    /// and opening the results as the settings say unless <paramref name="open"/> is false.
    /// </summary>
    private RecordRequest Request(bool transcribe = true, string name = "Kalam", string? continueBaseName = null, double? minutes = null, bool open = true) =>
        new(RunIds.New(DateTimeOffset.UtcNow), name, transcribe, new SourcePlan(RecordingMode.Class, new ClassSource.Automatic(), Microphone: null), _folder.Path,
            ContinueChoice.New, continueBaseName, minutes, open);

    /// <summary>
    /// Begins the run file of <paramref name="request"/> in ClassScribe's own folder as a recorder's host does, starting, in
    /// a separate recorder with its pipe, and fills the run's context with it, so that the run is recorded as in a recorder.
    /// Call after <see cref="CreateCommand"/>, which makes the journal.
    /// </summary>
    private RunHandle BeginAsTheRecordersHost(RecordRequest request)
    {
        var record = TestRuns.Record(request.RunId, RunState.Starting) with { Request = request, ProcessId = Environment.ProcessId };
        var run = _journal.Begin(record);
        _hostRuns.Add(run);
        _context.Fill(new RunContext(request.RunId, RunHome.Separate, record.Pipe) { Run = run });
        return run;
    }

    /// <summary>The one run file in ClassScribe's own folder, as it is now.</summary>
    private RunRecord RunFile() => _journal.List().Single().Record;

    /// <summary>What the account's report said at Information, which the window and the recording's log show; its Debug notes are left out.</summary>
    private IReadOnlyList<string> UsageLines() => [.. _usageLog.Entries.Where(entry => entry.Level == LogLevel.Information).Select(entry => entry.Message)];

    /// <summary>A lock over ClassScribe's own folder that stands for another recording's, or looks at it.</summary>
    private RecordingLock OtherLock() => new(_runtime.Folder, NullLogger<RecordingLock>.Instance, _ => { });

    /// <summary>A capture of an app's sound, which like Windows' process loopback hands over audio all the time, silence included.</summary>
    private static FakeAudioCapture AppCapture(int processId) => new(Pcm16KMono, "chrome", $"process:{processId}") { DeliversContinuously = true };

    /// <summary>The folder of the course named Kalam, which holds a date folder for each of its classes.</summary>
    private string KalamCourse() => Path.Combine(_folder.Path, "Kalam");

    /// <summary>
    /// The folder of the recording named Kalam: the one date folder in the course's folder, where a run puts a new
    /// recording, found rather than worked out, so that a run across midnight finds it too.
    /// </summary>
    private string KalamFolder()
    {
        var dated = Directory.GetDirectories(KalamCourse()).Single();
        Assert.That(ClassFolders.IsDateFolderName(Path.GetFileName(dated)), Is.True, $"{dated} is a date folder");
        return dated;
    }

    /// <summary>The one file in the Kalam folder matching the pattern.</summary>
    private string Only(string pattern) => Directory.GetFiles(KalamFolder(), pattern).Single();

    /// <summary>The recording's own audio file, as opposed to the upload copy made from it.</summary>
    private string Recording() => Directory.GetFiles(KalamFolder(), "*.wav").Single(f => !f.EndsWith(".upload.wav", StringComparison.Ordinal));

    /// <summary>A moment as the start lines and the refusal give it: the local time of day, to the minute.</summary>
    private static string LocalMinute(DateTimeOffset moment) =>
        TimeZoneInfo.ConvertTime(moment, TimeZoneInfo.Local).ToString("HH:mm", CultureInfo.InvariantCulture);

    /// <summary>
    /// Records a moment of sound and stops, with a folder put where the transcript's
    /// <paramref name="blockedExtension"/> file is to go, so that saving it fails after ElevenLabs has
    /// answered and charged for the answer. Returns what the command failed with.
    /// </summary>
    private async Task<Exception?> RecordAndFailToSaveAsync(string blockedExtension)
    {
        var run = CreateCommand().RunAsync("Kalam", transcribe: true);
        await Wait.UntilAsync(() => _capture.Started);
        _capture.Deliver(TestAudio.Shorts(1000, -1000));
        Directory.CreateDirectory(Path.ChangeExtension(Recording(), blockedExtension));
        _signals.RequestStop();

        return Assert.CatchAsync(async () => await run);
    }

    /// <summary>
    /// Checks that the hint after a failure whose paid answer is kept in <paramref name="answer"/> ends with two commands
    /// that run as printed: the free rebuild from that answer, then, for another try, transcribe with --again. On the
    /// command line, transcribe refuses a recording whose paid answer is kept unless --again is given, so a plain
    /// transcribe command is never offered for it.
    /// </summary>
    private void AssertTheCommandsAfterAKeptAnswer(string answer)
    {
        Assert.That(_log.Messages.TakeLast(2), Is.EqualTo(new[]
        {
            $"To lay that answer out as a transcript for free, run: {CommandLine.Rebuild(answer)}",
            $"To transcribe it again all the same, choose 3 in the menu, or run: {CommandLine.Transcribe(Recording(), again: true)}"
        }));
        Assert.That(_log.Messages, Has.None.EndsWith(CommandLine.Transcribe(Recording())), "the plain command, which the command line refuses here");
    }

    // ------------------------------------------------------------------ recording and transcribing

    /// <summary>Recording runs until stopped, and the recording is then transcribed under its name.</summary>
    [Test]
    public async Task A_class_is_recorded_until_stopped_and_then_transcribed()
    {
        var run = CreateCommand().RunAsync("Kalam", transcribe: true);
        await Wait.UntilAsync(() => _capture.Started);
        _capture.Deliver(TestAudio.Shorts(TestAudio.Tone(16000, seconds: 0.5, frequency: 440, amplitude: 0.5)));
        _signals.RequestStop();

        var exitCode = await run;

        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(Path.GetFileName(Only("*.wav")), Does.Match(@"^Kalam_\d{4}-\d{2}-\d{2}_\d{2}-\d{2}-\d{2}Z\.wav$"));
        Assert.That(File.ReadAllText(Only("*.md")), Does.StartWith("# Kalam").And.Contain("Hello world."));
        _client.Verify(Calls.TranscriptionOf(Path.ChangeExtension(Only("*.wav"), ".upload.wav")), Times.Once);
        Assert.That(_log.Messages, Has.Some.Contains("Recording \"Kalam\""));
    }

    /// <summary>Record only keeps the recording and sends nothing; the folder is opened, as the settings ask.</summary>
    [Test]
    public async Task Record_only_keeps_the_audio_and_sends_nothing()
    {
        var run = CreateCommand(openWhenDone: true).RunAsync("Kalam", transcribe: false);
        await Wait.UntilAsync(() => _capture.Started);
        _capture.Deliver(TestAudio.Shorts(1000, -1000));
        _signals.RequestStop();

        var exitCode = await run;

        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(TestAudio.ReadWav(Only("*.wav")).Samples, Is.EqualTo(new short[] { 1000, -1000 }));
        Assert.That(Directory.GetFiles(KalamFolder(), "*.md"), Is.Empty);
        _client.Verify(Calls.AnyTranscription, Times.Never);
        _desktop.Verify(d => d.OpenFolder(KalamFolder()), Times.Once);
    }

    /// <summary>When the transcript is ready, its folder and the transcript itself are opened, as the settings ask.</summary>
    [Test]
    public async Task The_folder_and_the_transcript_are_opened_when_it_is_ready()
    {
        var run = CreateCommand(openWhenDone: true).RunAsync("Kalam", transcribe: true);
        await Wait.UntilAsync(() => _capture.Started);
        _capture.Deliver(TestAudio.Shorts(1000, -1000));
        _signals.RequestStop();
        await run;

        _desktop.Verify(d => d.OpenFolder(KalamFolder()), Times.Once);
        _desktop.Verify(d => d.OpenFile(Only("*.md")), Times.Once);
    }

    /// <summary>
    /// With --no-open nothing is opened at the end, whatever the settings say: not the transcript, not its folder, and
    /// not the folder of a recording made without one.
    /// </summary>
    [TestCase(true)]
    [TestCase(false)]
    public async Task With_no_open_nothing_is_opened(bool transcribe)
    {
        var run = CreateCommand(openWhenDone: true).RunAsync(Request(transcribe, open: false));
        await Wait.UntilAsync(() => _capture.Started);
        _capture.Deliver(TestAudio.Shorts(1000, -1000));
        _signals.RequestStop();

        Assert.That(await run, Is.EqualTo(0));
        Assert.That(_results.Shown.Single().Open, Is.False);
        _desktop.VerifyNoOtherCalls();
    }

    /// <summary>M marks the moment: its position goes to the marks file at once, and the mark shows in the transcript.</summary>
    [Test]
    public async Task M_marks_the_moment_in_the_recording_and_the_transcript()
    {
        var run = CreateCommand().RunAsync("Kalam", transcribe: true);
        await Wait.UntilAsync(() => _capture.Started);
        _capture.Deliver(TestAudio.Shorts(TestAudio.Tone(16000, seconds: 0.5, frequency: 440, amplitude: 0.5)));
        _ui.Type('m');
        await Wait.UntilAsync(() => Directory.GetFiles(KalamFolder(), "*.marks.txt").Length == 1);
        _signals.RequestStop();
        await run;

        Assert.That(File.ReadAllLines(Only("*.marks.txt")), Is.EqualTo(new[] { "00:00:00.500" }));
        Assert.That(File.ReadAllText(Only("*.md")), Does.Contain("## [00:00:00] Mark 1"));
    }

    /// <summary>A recording of a day or more is reported with all its hours, not the hours past the last full day.</summary>
    [Test]
    public async Task A_recording_of_a_day_or_more_is_reported_with_all_its_hours()
    {
        _provider = new FakeCaptureProvider();
        _capture = _provider.Enqueue(new FakeAudioCapture(new AudioFormat(1, 1, 16, false)));   // one sample a second
        var run = CreateCommand().RunAsync("Kalam", transcribe: false);
        await Wait.UntilAsync(() => _capture.Started);
        _capture.Deliver(TestAudio.Shorts([.. Enumerable.Repeat((short)1000, 25 * 3600)]));
        _signals.RequestStop();
        await run;

        Assert.That(_log.Messages, Has.Some.EqualTo("Recorded 25:00:00 of audio"));
    }

    /// <summary>
    /// The start lines say where the recording goes, what it records, which keys work, that closing this window ends it,
    /// and that the lid and the power button still put the laptop to sleep.
    /// </summary>
    [Test]
    public async Task The_start_lines_say_where_what_the_keys_and_to_keep_the_lid_open()
    {
        var run = CreateCommand().RunAsync(Request(transcribe: true));
        await Wait.UntilAsync(() => _capture.Started);
        _signals.RequestStop();
        await run;

        Assert.That(_log.Messages.Take(4), Is.EqualTo(new[]
        {
            $"Recording \"Kalam\" to {KalamFolder()}",
            "Recording from the device the class plays on",
            "Press Q twice (or Ctrl+C twice) to stop and transcribe, P to pause or resume, M to mark a moment. If the window is closed instead, the recording ends and the audio so far is kept",
            "Keep the lid open and the laptop plugged in; closing the lid still puts it to sleep"
        }));
    }

    /// <summary>
    /// A saved class's run says so in its first line (layout-config 4.1): "(saved class)" after a course that is the class's
    /// own name, in any case, and the class's name when it differs from the course.
    /// </summary>
    [TestCase("Kalam", " (saved class)")]
    [TestCase("kalam", " (saved class)")]
    [TestCase("Kalam (Zoom app)", " (the saved class \"Kalam (Zoom app)\")")]
    public async Task A_saved_classs_run_says_so_in_its_first_line(string className, string saved)
    {
        var run = CreateCommand().RunAsync(Request(transcribe: false) with { ClassName = className });
        await Wait.UntilAsync(() => _capture.Started);
        _signals.RequestStop();
        await run;

        Assert.That(_log.Messages[0], Is.EqualTo($"Recording \"Kalam\"{saved} to {KalamFolder()}"));
    }

    /// <summary>The sources a run can be asked to record from the command line, each with the start line that names it.</summary>
    private static IEnumerable<TestCaseData> SourcesNamedAtTheStart()
    {
        yield return new TestCaseData(new ClassSource.App("chrome"), "Recording from the app \"chrome\"").SetArgDisplayNames("--app chrome");
        yield return new TestCaseData(new ClassSource.Device("Pixel Buds"), "Recording from the output device \"Pixel Buds\"").SetArgDisplayNames("--device \"Pixel Buds\"");
        yield return new TestCaseData(new ClassSource.Automatic(), "Recording from the device the class plays on").SetArgDisplayNames("--device auto");
    }

    /// <summary>A start line names what is recorded: the app given with --app, the device given with --device, or the device the class plays on.</summary>
    [TestCaseSource(nameof(SourcesNamedAtTheStart))]
    public async Task A_start_line_names_what_is_recorded(ClassSource source, string line)
    {
        var recordings = new FakeRecordingFactory();
        var run = CreateCommand(recordings: recordings).RunAsync(Request(transcribe: false) with { Source = new SourcePlan(RecordingMode.Class, source, Microphone: null) });
        await Wait.UntilAsync(() => recordings.Created is [{ Started: true }]);
        _signals.RequestStop();
        await run;

        Assert.That(recordings.Plans.Single().Class, Is.EqualTo(source));
        Assert.That(_log.Messages, Has.One.EqualTo(line));
    }

    /// <summary>With --minutes, a start line says when the recording stops by itself, by the local time of day.</summary>
    [Test]
    public async Task With_minutes_a_start_line_says_when_the_recording_stops_by_itself()
    {
        var start = new DateTimeOffset(2026, 10, 15, 23, 0, 2, TimeSpan.Zero);
        var run = CreateCommand(time: new ManualTimeProvider(start)).RunAsync(Request(transcribe: false, minutes: 90));
        await Wait.UntilAsync(() => _capture.Started);
        _signals.RequestStop();
        await run;

        Assert.That(_log.Messages, Has.Some.EqualTo($"This recording stops by itself at {LocalMinute(start.AddMinutes(90))} (--minutes 90); Q twice stops it sooner"));
    }

    /// <summary>
    /// A recording asked for without a name of its own (menu choices 1 and 2, until they hand a whole request) records
    /// what menu choice 6 picked, into the recordings folder of the settings, as a new recording, opening the results as
    /// the settings say; the run file keeps that request.
    /// </summary>
    [Test]
    public async Task A_recording_asked_for_by_name_records_what_menu_choice_6_picked_into_the_settings_folder()
    {
        var recordings = new FakeRecordingFactory();
        var command = CreateCommand(recordings: recordings);
        _choice.App = "chrome";

        var run = command.RunAsync("Kalam", transcribe: false);
        await Wait.UntilAsync(() => recordings.Created is [{ Started: true }]);
        recordings.Created[0].MainSegments = [recordings.Created[0].Files!.Segment(1)];
        _signals.RequestStop();

        Assert.That(await run, Is.EqualTo(0));
        Assert.That(recordings.Plans.Single(), Is.EqualTo(new SourcePlan(RecordingMode.Class, new ClassSource.App("chrome"), Microphone: null)));
        Assert.That(recordings.Created[0].Files!.Folder, Is.EqualTo(KalamFolder()));
        var request = RunFile().Request;
        Assert.That((request.Name, request.Transcribe, request.OutputRoot, request.Continue, request.ContinueBaseName, request.Minutes, request.Open),
            Is.EqualTo(("Kalam", false, _folder.Path, ContinueChoice.New, (string?)null, (double?)null, true)));
        Assert.That(RunIds.IsWellFormed(request.RunId), Is.True);
    }

    // ------------------------------------------------------------------ one recording at a time

    /// <summary>
    /// The lock is taken before anything else and let go the moment capture stops, before the upload, so that the next
    /// class may start while this one uploads; at the end nothing holds it.
    /// </summary>
    [Test]
    public async Task The_lock_is_held_while_capturing_and_let_go_before_the_upload()
    {
        bool? heldDuringUpload = null;
        _client.Setup(Calls.AnyTranscription).Returns((string _, IReadOnlyList<string> _, CancellationToken _) =>
        {
            heldDuringUpload = OtherLock().IsHeld();
            return Task.FromResult(Mocks.Transcript);
        });
        var run = CreateCommand().RunAsync(Request());
        await Wait.UntilAsync(() => _capture.Started);
        var heldWhileRecording = OtherLock().IsHeld();
        _capture.Deliver(TestAudio.Shorts(1000, -1000));
        _signals.RequestStop();

        Assert.That(await run, Is.EqualTo(0));
        Assert.That(heldWhileRecording, Is.True);
        Assert.That(heldDuringUpload, Is.False);
        Assert.That(OtherLock().IsHeld(), Is.False);
    }

    /// <summary>
    /// While another recording holds the lock, a run is refused before it makes any file: it says why and returns 1, opens
    /// no capture, writes no run file, and keeps nothing awake.
    /// </summary>
    [Test]
    public async Task A_second_run_is_refused_before_it_makes_any_file()
    {
        using var other = OtherLock().TryTake();
        Assert.That(other, Is.Not.Null);

        var exitCode = await EndingByItselfAsync(CreateCommand().RunAsync(Request()));

        Assert.That(exitCode, Is.EqualTo(1));
        Assert.That(Directory.Exists(KalamCourse()), Is.False, "no file made");
        Assert.That(_runtime.FilesInRuns(), Is.Empty, "no run file");
        Assert.That(_provider.Created, Is.Empty, "no capture opened");
        Assert.That(_power.Holds, Is.Empty);
        Assert.That(_log.Problems.Single(), Is.EqualTo("Only one recording at a time: another recording is running. Start ClassScribe to see it, or stop it first."));
        Assert.That(_results.Last, Is.EqualTo(new RunOutcome(OutcomeKind.Refused, 1, _log.Problems.Single(), null, null, null)));
    }

    /// <summary>
    /// The refusal names the recording that runs, from its run file: its name, when it started by the local time of day, and
    /// how long it has run.
    /// </summary>
    [Test]
    public async Task The_refusal_names_the_recording_that_runs()
    {
        var time = new ManualTimeProvider(TestRuns.Start.Add(new TimeSpan(0, 42, 13)));
        var command = CreateCommand(time: time);
        using (var fiqh = _journal.Begin(TestRuns.Record(TestRuns.Id(), RunState.Recording, name: "Fiqh")))
        {
            _runtime.Processes.Start(TestRuns.ProcessId, TestRuns.ProcessStart);
            using var other = OtherLock().TryTake();

            Assert.That(await EndingByItselfAsync(command.RunAsync(Request())), Is.EqualTo(1));
        }

        Assert.That(_log.Problems.Single(), Is.EqualTo(
            $"Only one recording at a time: \"Fiqh\" has been recording since {LocalMinute(TestRuns.Start)} (00:42:13). Start ClassScribe to see it, or stop it first."));
    }

    /// <summary>
    /// In a recorder, a start the lock refuses is written into the run file its host began, as refused, and the recorder's
    /// own exit code for it, 3, is returned; the window that started it then shows the recording that runs instead.
    /// </summary>
    [Test]
    public async Task In_a_recorder_a_refused_start_is_written_into_the_run_file_and_returns_3()
    {
        using var other = OtherLock().TryTake();
        var command = CreateCommand();
        var request = Request();
        var run = BeginAsTheRecordersHost(request);

        var exitCode = await EndingByItselfAsync(command.RunAsync(request));

        Assert.That(exitCode, Is.EqualTo(3));
        Assert.That(run.Current.State, Is.EqualTo(RunState.Refused));
        Assert.That(run.Current.Outcome, Is.EqualTo(new RunOutcome(OutcomeKind.Refused, 1,
            "Only one recording at a time: another recording is running. Start ClassScribe to see it, or stop it first.", null, null, null)));
        Assert.That(Directory.Exists(KalamCourse()), Is.False);
    }

    // ------------------------------------------------------------------ the run file

    /// <summary>
    /// A run in this window begins its own run file as recording, with its files, then moves it to transcribing for the
    /// upload and to finished with its outcome; the window has shown that outcome, so the run is marked seen.
    /// </summary>
    [Test]
    public async Task The_run_file_goes_through_recording_and_transcribing_to_finished_with_its_outcome()
    {
        RunState? stateDuringUpload = null;
        _client.Setup(Calls.AnyTranscription).Returns((string _, IReadOnlyList<string> _, CancellationToken _) =>
        {
            stateDuringUpload = RunFile().State;
            return Task.FromResult(Mocks.Transcript);
        });
        var request = Request();
        var run = CreateCommand().RunAsync(request);
        await Wait.UntilAsync(() => _capture.Started);
        var whileRecording = RunFile();
        _capture.Deliver(TestAudio.Shorts(1000, -1000));
        _signals.RequestStop();

        Assert.That(await run, Is.EqualTo(0));
        var files = RecordingFiles.TryParse(Recording())!;
        Assert.That(whileRecording.State, Is.EqualTo(RunState.Recording));
        Assert.That((whileRecording.RunId, whileRecording.Home, whileRecording.Pipe, whileRecording.BaseName, whileRecording.Folder),
            Is.EqualTo((request.RunId, RunHome.InWindow, (string?)null, files.BaseName, files.Folder)));
        Assert.That((whileRecording.ProcessId, whileRecording.Protocol, whileRecording.Build, whileRecording.Request),
            Is.EqualTo((Environment.ProcessId, LinkProtocol.Version, BuildInfo.Build, request)));
        Assert.That(stateDuringUpload, Is.EqualTo(RunState.Transcribing));

        var finished = RunFile();
        var outcome = new RunOutcome(OutcomeKind.Transcribed, 0, $"Transcript saved to {Only("*.md")}", Only("*.md"), KalamFolder(), null);
        Assert.That((finished.State, finished.Outcome, finished.EndedBy, finished.Seen), Is.EqualTo((RunState.Finished, outcome, RunEndCause.Q, true)));
        Assert.That(_results.Shown, Is.EqualTo(new[] { (outcome, true) }));
    }

    /// <summary>
    /// While the recording stops and its files are finished, the run file says stopping: should the process end then, the
    /// next start knows that the class was being stopped, not that it was still recording.
    /// </summary>
    [Test]
    public async Task The_run_file_says_stopping_while_the_recording_stops()
    {
        var fakes = new FakeRecordingFactory();
        var recordings = new WatchedRecordingFactory(fakes);
        RunState? atTheStop = null;
        recordings.BeforeCall = call =>
        {
            if (call == "Stop") atTheStop ??= RunFile().State;
        };
        var run = CreateCommand(recordings: recordings).RunAsync(Request(transcribe: false));
        await Wait.UntilAsync(() => fakes.Created is [{ Started: true }]);
        var whileRecording = RunFile().State;
        _signals.RequestStop();
        await run;

        Assert.That((whileRecording, atTheStop), Is.EqualTo((RunState.Recording, (RunState?)RunState.Stopping)));
    }

    /// <summary>
    /// When a run in this window is over, its scope gives the window back: the taskbar button loses the run's bar and the
    /// title says ClassScribe again, so that a failed run's red button and "! FAILED" title do not stay through whatever
    /// the menu does next. Until then the window shows how the run ended.
    /// </summary>
    [Test]
    public async Task When_a_run_in_this_window_is_over_the_window_is_given_back()
    {
        var run = CreateCommand(apiKey: "", status: new WindowStatusSink(_window)).RunAsync(Request());
        await Wait.UntilAsync(() => _capture.Started);
        _capture.Deliver(TestAudio.Shorts(1000, -1000));
        _signals.RequestStop();
        Assert.That(await run, Is.EqualTo(1), "no key: the transcript failed");
        var atTheEnd = (_window.Title, _window.Taskbar);

        _bookkeeping!.Dispose();   // the run's scope ends

        Assert.That(atTheEnd, Is.EqualTo(("! FAILED Kalam (in this window) | ClassScribe", ((TaskbarState, int)?)(TaskbarState.Error, 100))));
        Assert.That((_window.Title, _window.Taskbar), Is.EqualTo(("ClassScribe", ((TaskbarState, int)?)(TaskbarState.None, 0))));
    }

    /// <summary>
    /// A run in this window prunes the run files that have nothing left to report, as a recorder does when it starts, so
    /// that recording from the window alone does not let them pile up: a run seen 31 days ago goes, one that ended
    /// unexpectedly stays however old it is.
    /// </summary>
    [Test]
    public async Task A_run_in_this_window_prunes_old_run_files()
    {
        var time = new ManualTimeProvider(TestRuns.Start.AddDays(31));
        var command = CreateCommand(time: time);
        var seen = TestRuns.Record(TestRuns.Id(1), RunState.Finished) with { Seen = true, Outcome = new RunOutcome(OutcomeKind.Recorded, 0, "Recorded.", null, null, null) };
        var lost = TestRuns.Record(TestRuns.Id(2), RunState.Recording);
        foreach (var old in new[] { seen, lost })
        {
            using var begun = new RunJournal(_runtime.Folder, _runtime.Processes, new ManualTimeProvider(TestRuns.Start), NullLogger<RunJournal>.Instance).Begin(old);
        }

        var run = command.RunAsync(Request(transcribe: false));
        await Wait.UntilAsync(() => _capture.Started);
        _signals.RequestStop();
        await run;

        Assert.That(_journal.Find(seen.RunId), Is.Null, "pruned");
        Assert.That(_journal.Find(lost.RunId), Is.Not.Null, "kept for its report");
    }

    /// <summary>
    /// In a recorder, the run file its host began as starting is the run's own: it moves to recording with the recording's
    /// files and to finished with the outcome, keeps its home and pipe, and stays unseen, for the windows that show the
    /// outcome to mark.
    /// </summary>
    [Test]
    public async Task In_a_recorder_the_run_file_its_host_began_is_kept_up_to_date()
    {
        var command = CreateCommand();
        var request = Request(transcribe: false);
        var run = BeginAsTheRecordersHost(request);

        var recording = command.RunAsync(request);
        await Wait.UntilAsync(() => _capture.Started);
        var whileRecording = run.Current;
        _capture.Deliver(TestAudio.Shorts(1000, -1000));
        _signals.RequestStop();

        Assert.That(await recording, Is.EqualTo(0));
        Assert.That((whileRecording.State, whileRecording.BaseName), Is.EqualTo((RunState.Recording, RecordingFiles.TryParse(Recording())!.BaseName)));
        var finished = RunFile();
        Assert.That((finished.State, finished.Outcome!.Kind, finished.Home, finished.Pipe, finished.Seen),
            Is.EqualTo((RunState.Finished, OutcomeKind.Recorded, RunHome.Separate, LinkProtocol.PipeNameFor(request.RunId), false)));
        Assert.That(_log.Messages, Has.Some.EndsWith("Closing a window that shows this recording does not stop it"));

        _bookkeeping!.Dispose();   // the run's scope ends
        Assert.That(_window.Titles, Is.Empty, "a recorder has no window of its own to give back");
    }

    /// <summary>
    /// After the stop the window's title and taskbar follow the run to its end: preparing the upload copy, uploading (the
    /// mocked service says nothing of how far), then done.
    /// </summary>
    [Test]
    public async Task The_states_after_the_stop_follow_the_run_to_its_end()
    {
        var run = CreateCommand().RunAsync(Request());
        await Wait.UntilAsync(() => _capture.Started);
        _capture.Deliver(TestAudio.Shorts(1000, -1000));
        _signals.RequestStop();
        await run;

        var phases = _status.States.Select(state => state.Phase).ToList();
        var stop = phases.LastIndexOf(RecorderPhase.Stopping);
        Assert.That(stop, Is.GreaterThanOrEqualTo(0));
        Assert.That(phases.Skip(stop).Distinct(), Is.EqualTo(new[] { RecorderPhase.Stopping, RecorderPhase.Preparing, RecorderPhase.Uploading, RecorderPhase.Done }));
        Assert.That(_status.Last!.Name, Is.EqualTo("Kalam"));
    }

    /// <summary>
    /// The upload's progress, as the service reports it, shows on the title and the taskbar (UPLOADING 40%) and goes into
    /// the run file's heartbeat, so that the menu and the list can say how far a transcription in the background is: the
    /// first percent at once, the wait for the transcript as 100, and once the transcript is saved the percent is gone.
    /// </summary>
    [Test]
    public async Task The_upload_s_progress_shows_on_the_window_and_in_the_run_file()
    {
        RecorderState? uploading = null;
        int? inFileWhileUploading = null;
        int? inFileWhileWaiting = null;
        _client.Setup(Calls.AnyTranscription).Returns((string _, IReadOnlyList<string> _, CancellationToken _) =>
        {
            _progress.Report(TranscriptionStep.Uploading, 40);
            uploading = _status.Last;
            inFileWhileUploading = RunFile().UploadPercent;
            _progress.Report(TranscriptionStep.WaitingForTranscript, 100);
            inFileWhileWaiting = RunFile().UploadPercent;
            return Task.FromResult(Mocks.Transcript);
        });
        var run = CreateCommand().RunAsync(Request());
        await Wait.UntilAsync(() => _capture.Started);
        _capture.Deliver(TestAudio.Shorts(1000, -1000));
        _signals.RequestStop();
        await run;

        Assert.That((uploading?.Phase, uploading?.UploadPercent), Is.EqualTo((RecorderPhase.Uploading, 40)));
        Assert.That(TitleText.For(uploading!), Is.EqualTo("UPLOADING 40% Kalam (in this window) | ClassScribe"));
        Assert.That(inFileWhileUploading, Is.EqualTo(40));
        Assert.That(inFileWhileWaiting, Is.EqualTo(100));
        Assert.That(_status.States.Select(state => state.Phase), Does.Contain(RecorderPhase.Transcribing), "waiting for the transcript");
        Assert.That(RunFile().UploadPercent, Is.Null, "nothing uploads once the transcript is saved");
    }

    /// <summary>
    /// A run in this window writes its own log beside the recording, &lt;base&gt;.log: the lines from the run's start, not
    /// what the window did before, and it lets go of the file when the run is over.
    /// </summary>
    [Test]
    public async Task A_run_in_this_window_writes_its_log_beside_the_recording()
    {
        _recorderLog.Write(LogLevel.Information, "The window's work before the run");
        var logger = new TeeLogger<RecordCommand>(_log, new RecorderLogProvider(_recorderLog).CreateLogger(typeof(RecordCommand).FullName!));
        var run = CreateCommand(logger: logger).RunAsync(Request(transcribe: false));
        await Wait.UntilAsync(() => _capture.Started);
        _capture.Deliver(TestAudio.Shorts(1000, -1000));
        _signals.RequestStop();
        await run;

        _bookkeeping!.Dispose();   // the run's scope ends
        _recorderLog.Write(LogLevel.Information, "The window's work after the run");
        Assert.That(_recorderLog.Flush(TimeSpan.FromSeconds(5)), Is.True);

        var lines = File.ReadAllLines(RecordingFiles.TryParse(Recording())!.Log);
        Assert.That(lines, Has.Some.Contains("Recording \"Kalam\" to"));
        Assert.That(lines, Has.Some.Contains("Recorded 00:00:00 of audio"));
        Assert.That(lines, Has.None.Contains("before the run").And.None.Contains("after the run"));
    }

    /// <summary>
    /// Lines of the run's log that its file did not take are counted and reported at the end, with the first failure:
    /// the recording itself is not affected.
    /// </summary>
    [Test]
    public async Task Lines_the_log_file_did_not_take_are_reported_at_the_end()
    {
        _recorderLog.Dispose();
        _recorderLog = new RecorderLog(TimeProvider.System) { OpenFile = _ => throw new IOException("There is not enough space on the disk.") };
        var logger = new TeeLogger<RecordCommand>(_log, new RecorderLogProvider(_recorderLog).CreateLogger(typeof(RecordCommand).FullName!));
        var run = CreateCommand(logger: logger).RunAsync(Request(transcribe: false));
        await Wait.UntilAsync(() => _capture.Started);
        _capture.Deliver(TestAudio.Shorts(1000, -1000));
        _signals.RequestStop();

        Assert.That(await run, Is.EqualTo(0));
        Assert.That(_bookkeepingLog.Problems, Has.Some.Match(
            @"^[1-9][0-9]* lines of this recording's log were not saved to its file\. Could not write the log file .+\.log: There is not enough space on the disk\.$"));
    }

    /// <summary>
    /// When ClassScribe's own folder cannot be written, the run records all the same: without the lock and without a run
    /// file, each said with a warning.
    /// </summary>
    [Test]
    public async Task Without_a_folder_of_its_own_a_run_records_without_the_lock_and_the_run_file()
    {
        File.WriteAllText(_folder.File("not-a-folder"), "");
        var run = CreateCommand(runtimeFolder: new RuntimeFolder(_folder.File("not-a-folder"))).RunAsync(Request(transcribe: false));
        await Wait.UntilAsync(() => _capture.Started);
        _capture.Deliver(TestAudio.Shorts(1000, -1000));
        _signals.RequestStop();

        Assert.That(await run, Is.EqualTo(0));
        Assert.That(TestAudio.ReadWav(Recording()).Samples, Is.EqualTo(new short[] { 1000, -1000 }));
        Assert.That(_bookkeepingLog.Problems, Has.Some.StartsWith("Could not take the lock that allows one recording at a time"));
        Assert.That(_bookkeepingLog.Problems, Has.Some.StartsWith("Could not write this recording's run file"));
    }

    // ------------------------------------------------------------------ continuing a recording

    /// <summary>
    /// A run that continues a recording writes into the same files: its audio goes into the next part, _part2, a resume
    /// note marks where it begins, at the recording's length on disk, with how long was not recorded, and a mark set now
    /// gets its position in the whole recording. The run file names the recording continued, and the one transcript is
    /// made from the whole recording, in 2 parts: the upload holds the first part's audio and then this run's, not this
    /// run's alone.
    /// </summary>
    [Test]
    public async Task A_continued_recording_gets_its_next_part_a_resume_note_and_marks_at_their_place_in_the_whole()
    {
        (int SampleRate, short[] Samples)? uploaded = null;
        _client.Setup(Calls.AnyTranscription).Returns((string path, IReadOnlyList<string> _, CancellationToken _) =>
        {
            var wav = TestAudio.ReadWav(path);
            uploaded = (wav.SampleRate, wav.Samples);
            return Task.FromResult(Mocks.Transcript);
        });
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 10, 15, 23, 0, 2, TimeSpan.Zero));
        var next = _provider.Enqueue(new FakeAudioCapture(Pcm16KMono));
        var first = CreateCommand(time: time).RunAsync(Request(transcribe: false));
        await Wait.UntilAsync(() => _capture.Started);
        _capture.Deliver(TestAudio.Shorts([.. Enumerable.Repeat((short)1000, 32000)]));   // 2 seconds
        _signals.RequestStop();
        Assert.That(await first, Is.EqualTo(0));
        var files = RecordingFiles.TryParse(Recording())!;
        File.SetLastWriteTimeUtc(files.Segment(1), time.GetUtcNow().UtcDateTime);

        // Four minutes later, after the first run's scope has ended, a new run continues it.
        _bookkeeping!.Dispose();
        time.Advance(TimeSpan.FromMinutes(4));
        _signals = new FakeUserSignals();
        var request = Request(transcribe: true, continueBaseName: files.BaseName);
        var second = CreateCommand(time: time).RunAsync(request);
        await Wait.UntilAsync(() => next.Started);
        next.Deliver(TestAudio.Shorts([.. Enumerable.Repeat((short)-1000, 16000)]));   // 1 second
        _ui.Type('m');
        await Wait.UntilAsync(() => _loopLog.Messages.Contains("Mark 1 at 00:00:03"));
        _signals.RequestStop();

        Assert.That(await second, Is.EqualTo(0));
        Assert.That(files.FindSegments(), Is.EqualTo(new[] { files.Segment(1), files.Segment(2) }));
        Assert.That(TestAudio.ReadWav(files.Segment(2)).Samples, Has.Length.EqualTo(16000));
        Assert.That(File.ReadAllLines(files.Marks), Is.EqualTo(new[] { "00:00:02.000 ~ Resumed at 23:04 UTC, about 4 minutes not recorded", "00:00:03.000" }));
        Assert.That(_journal.Find(request.RunId)!.Record.BaseName, Is.EqualTo(files.BaseName));
        _client.Verify(Calls.TranscriptionOf(files.Upload), Times.Once);
        Assert.That(uploaded?.SampleRate, Is.EqualTo(16000));
        Assert.That(uploaded?.Samples, Is.EqualTo((short[])[.. Enumerable.Repeat((short)1000, 32000), .. Enumerable.Repeat((short)-1000, 16000)]),
            "the first part's 2 seconds, then this run's second");
        Assert.That(File.ReadAllText(files.Transcript()), Does.Contain("in 2 parts"));
        Assert.That(_log.Messages, Has.Some.EqualTo($"Continuing \"Kalam\" as its next part ({files.BaseName}, 00:00:02 recorded so far), in {files.Folder}"));
        Assert.That(_log.Messages, Has.Some.EqualTo("Recorded 00:00:01 in this part, 00:00:03 in all"));
    }

    /// <summary>
    /// A continued recording whose new part heard nothing audible still has its earlier parts, so the run goes on to them
    /// and ends with exit code 0: record only keeps the recording and says that this part heard nothing; record sends the
    /// whole recording, which has sound in its first part.
    /// </summary>
    [TestCase(false)]
    [TestCase(true)]
    public async Task A_continued_recording_whose_new_part_heard_nothing_goes_on_with_its_earlier_parts(bool transcribe)
    {
        var next = _provider.Enqueue(new FakeAudioCapture(Pcm16KMono));
        var first = CreateCommand().RunAsync(Request(transcribe: false));
        await Wait.UntilAsync(() => _capture.Started);
        _capture.Deliver(TestAudio.Shorts(1000, -1000));
        _signals.RequestStop();
        Assert.That(await first, Is.EqualTo(0));
        var files = RecordingFiles.TryParse(Recording())!;
        _bookkeeping!.Dispose();   // the first run's scope ends

        _signals = new FakeUserSignals();
        var request = Request(transcribe, continueBaseName: files.BaseName);
        var second = CreateCommand().RunAsync(request);
        await Wait.UntilAsync(() => next.Started);
        next.Deliver(TestAudio.Shorts(0, 0));
        _signals.RequestStop();

        Assert.That(await second, Is.EqualTo(0));
        Assert.That(TestAudio.ReadWav(files.Segment(2)).Samples, Is.EqualTo(new short[] { 0, 0 }));
        var outcome = _journal.Find(request.RunId)!.Record.Outcome!;
        if (transcribe)
        {
            _client.Verify(Calls.TranscriptionOf(files.Upload), Times.Once);
            Assert.That(outcome.Kind, Is.EqualTo(OutcomeKind.Transcribed));
            Assert.That(_log.Problems, Has.None.Contains("Nothing audible"));
        }
        else
        {
            _client.Verify(Calls.AnyTranscription, Times.Never);
            Assert.That(outcome.Kind, Is.EqualTo(OutcomeKind.Recorded));
            Assert.That(_log.Problems, Has.One.EqualTo($"Nothing audible was captured in this part; the earlier parts of {files.BaseName} are kept"));
        }
    }

    /// <summary>
    /// A recording to continue that is no longer there does not cost the class: the run says so and records a new
    /// recording instead.
    /// </summary>
    [Test]
    public async Task A_recording_to_continue_that_is_gone_is_recorded_as_a_new_one()
    {
        var run = CreateCommand().RunAsync(Request(transcribe: false, continueBaseName: "Kalam_2026-10-15_23-00-02Z"));
        await Wait.UntilAsync(() => _capture.Started);
        _capture.Deliver(TestAudio.Shorts(1000, -1000));
        _signals.RequestStop();

        Assert.That(await run, Is.EqualTo(0));
        Assert.That(Path.GetFileName(Recording()), Does.Not.StartWith("Kalam_2026-10-15_23-00-02Z"));
        Assert.That(_log.Problems, Has.Some.StartsWith("Could not continue Kalam_2026-10-15_23-00-02Z (").And.Some.EndsWith("); recording \"Kalam\" as a new recording instead"));
    }

    /// <summary>
    /// A continued recording keeps writing into its own folder in whichever layout it was made, even days later: flat in
    /// the folder of its name, as recordings were kept before the course and date folders, or in its date folder, "(2)"
    /// included. No date folder of today is made for it.
    /// </summary>
    [TestCase(null)]
    [TestCase("2026-10-14 (2)")]
    public async Task A_continued_recording_keeps_writing_into_its_own_folder_in_either_layout(string? dateFolder)
    {
        var earlier = RecordingFiles.ForNew(dateFolder is null ? KalamCourse() : Path.Combine(KalamCourse(), dateFolder), "Kalam",
            new DateTimeOffset(2026, 10, 15, 0, 30, 0, TimeSpan.Zero));
        Directory.CreateDirectory(earlier.Folder);
        TestAudio.WriteWav(earlier.Segment(1), 16000, new short[16000]);
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 10, 16, 14, 0, 0, TimeSpan.Zero));
        var run = CreateCommand(time: time).RunAsync(Request(transcribe: false, continueBaseName: earlier.BaseName));
        await Wait.UntilAsync(() => _capture.Started);
        _capture.Deliver(TestAudio.Shorts(1000, -1000));
        _signals.RequestStop();

        Assert.That(await run, Is.EqualTo(0));
        Assert.That(earlier.FindSegments(), Is.EqualTo(new[] { earlier.Segment(1), earlier.Segment(2) }));
        Assert.That(Directory.GetDirectories(KalamCourse()), Is.EqualTo(dateFolder is null ? Array.Empty<string>() : new[] { earlier.Folder }), "no date folder of today");
        Assert.That(_log.Messages, Has.Some.EqualTo($"Continuing \"Kalam\" as its next part ({earlier.BaseName}, 00:00:01 recorded so far), in {earlier.Folder}"));
    }

    /// <summary>
    /// A class that runs past local midnight stays in the date folder of its start: a part opened after midnight, as after
    /// the device failed, and a mark set then go beside the first part, and no folder of the new day is made.
    /// </summary>
    [Test]
    public async Task A_class_that_runs_past_midnight_stays_in_the_folder_of_its_start()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 10, 18, 3, 59, 30, TimeSpan.Zero));   // 23:59:30 on 17 October in US Eastern time
        var next = _provider.Enqueue(new FakeAudioCapture(Pcm16KMono));
        var run = CreateCommand(time: ZonedClock.Eastern(clock)).RunAsync(Request(transcribe: false));
        await Wait.UntilAsync(() => _capture.Started);
        _capture.Deliver(TestAudio.Shorts(1000, -1000));

        clock.Advance(TimeSpan.FromSeconds(31));   // 00:00:01 on 18 October
        _capture.Fail(new IOException("The device was unplugged."));
        await Wait.UntilAsync(() => next.Started);
        next.Deliver(TestAudio.Shorts(500, -500));
        _ui.Type('m');
        await Wait.UntilAsync(() => _loopLog.Messages.Any(message => message.StartsWith("Mark 1 at", StringComparison.Ordinal)));
        _signals.RequestStop();

        Assert.That(await run, Is.EqualTo(0));
        var startFolder = Path.Combine(KalamCourse(), "2026-10-17");
        Assert.That(Directory.GetDirectories(KalamCourse()), Is.EqualTo(new[] { startFolder }));
        var files = RecordingFiles.TryParse(Directory.GetFiles(startFolder, "*.wav").First())!;
        Assert.That(files.BaseName, Is.EqualTo("Kalam_2026-10-18_03-59-30Z"));
        Assert.That(files.FindSegments(), Has.Count.GreaterThanOrEqualTo(2).And.All.StartsWith(startFolder));
        Assert.That(File.Exists(files.Marks), Is.True);
    }

    // ------------------------------------------------------------------ the end of the process or the session

    /// <summary>
    /// When the window is closed mid-recording, the file on disk is brought up to date at once, and the run file says
    /// that the window's closing ended it, for the next start to report.
    /// </summary>
    [Test]
    public async Task Closing_the_window_saves_the_recording_and_puts_the_end_down_to_the_window()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);   // the clock stands still, so nothing is saved on the usual schedule
        var run = CreateCommand(time: time).RunAsync(Request(transcribe: false));
        await Wait.UntilAsync(() => _capture.Started && _processEnding is not null);
        _capture.Deliver(TestAudio.Shorts(1000, -1000));
        Assert.That(TestAudio.ReadWav(Only("*.wav")).Samples, Is.Empty, "not saved yet");

        _processEnding!();

        Assert.That(TestAudio.ReadWav(Only("*.wav")).Samples, Is.EqualTo(new short[] { 1000, -1000 }));
        Assert.That(RunFile().EndedBy, Is.EqualTo(RunEndCause.WindowClosed));
        _signals.RequestStop();
        await run;
    }

    /// <summary>
    /// When an error nobody caught takes the process down mid-recording, the file on disk is brought up to date, and the run
    /// file puts the end down to the error, not to the window, although the exit warning that the crash raises after comes
    /// too: the run hears of the error first, as it registers for it before it registers for the exit warning. Once the run
    /// is over, it no longer listens.
    /// </summary>
    [Test]
    public async Task A_crash_saves_the_recording_and_puts_the_end_down_to_the_error()
    {
        var run = CreateCommand(time: new ManualTimeProvider(DateTimeOffset.UtcNow)).RunAsync(Request(transcribe: false));
        await Wait.UntilAsync(() => _capture.Started && _processEnding is not null);
        _capture.Deliver(TestAudio.Shorts(1000, -1000));

        _crashes.Crash(new InvalidOperationException("A bug."));   // as .NET warns of the error nobody caught,
        _processEnding!();                                         // and then the exit warnings that it raises too

        Assert.That(TestAudio.ReadWav(Only("*.wav")).Samples, Is.EqualTo(new short[] { 1000, -1000 }));
        Assert.That(RunFile().EndedBy, Is.EqualTo(RunEndCause.Error));
        Assert.That(_crashWarningsBeforeExitWarning, Is.EqualTo(1), "registered for the error before the exit warning");
        _signals.RequestStop();
        await run;
        Assert.That(_crashes.Registered, Is.Zero, "no longer listening once the run is over");
    }

    /// <summary>
    /// When the recording on disk cannot be brought up to date as the process ends, here because another
    /// program has locked the start of the file, that is logged as an error and goes no further: the process
    /// is on its way out. The recording itself is finished as usual afterwards.
    /// </summary>
    [Test, Platform("Win")]
    [SupportedOSPlatform("windows")]
    public async Task A_save_that_fails_as_the_process_ends_is_logged_and_goes_no_further()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);   // the clock stands still, so nothing is saved on the usual schedule
        var run = CreateCommand(time: time).RunAsync("Kalam", transcribe: false);
        await Wait.UntilAsync(() => _capture.Started && _processEnding is not null);
        _capture.Deliver(TestAudio.Shorts(1000, -1000));

        using (var otherProgram = new FileStream(Only("*.wav"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            otherProgram.Lock(0, WavSegmentWriter.HeaderSize);
            Assert.DoesNotThrow(() => _processEnding!());
            otherProgram.Unlock(0, WavSegmentWriter.HeaderSize);
        }

        Assert.That(_bookkeepingLog.Problems, Has.Some.StartsWith("Could not bring the recording on disk up to date before exiting"));
        _signals.RequestStop();
        Assert.That(await run, Is.EqualTo(0));
        Assert.That(TestAudio.ReadWav(Only("*.wav")).Samples, Is.EqualTo(new short[] { 1000, -1000 }), "the audio is kept");
    }

    /// <summary>
    /// While recording, a restart Gulzar starts makes Windows ask first. When Windows asks whether it may end the session,
    /// the recording on disk is brought up to date and the run file puts a coming end down to Windows; when Windows goes on
    /// after all, that is taken back and the class goes on recording.
    /// </summary>
    [Test]
    public async Task Windows_asking_to_end_the_session_saves_the_recording_and_notes_why_it_may_end()
    {
        var run = CreateCommand(time: new ManualTimeProvider(DateTimeOffset.UtcNow)).RunAsync(Request(transcribe: false));
        await Wait.UntilAsync(() => _capture.Started);
        _capture.Deliver(TestAudio.Shorts(1000, -1000));
        Assert.That(TestAudio.ReadWav(Only("*.wav")).Samples, Is.Empty, "not saved yet");

        var mayEnd = _session.AskToEnd();

        Assert.That(mayEnd, Is.False, "Windows asks first");
        Assert.That(TestAudio.ReadWav(Only("*.wav")).Samples, Is.EqualTo(new short[] { 1000, -1000 }));
        Assert.That(RunFile().EndedBy, Is.EqualTo(RunEndCause.Windows));

        _session.CancelEnd();
        Assert.That(RunFile().EndedBy, Is.Null, "taken back");
        _capture.Deliver(TestAudio.Shorts(-7, 7));
        _signals.RequestStop();

        Assert.That(await run, Is.EqualTo(0));
        Assert.That(TestAudio.ReadWav(Only("*.wav")).Samples, Is.EqualTo(new short[] { 1000, -1000, -7, 7 }));
        Assert.That(RunFile().EndedBy, Is.EqualTo(RunEndCause.Q));
    }

    /// <summary>
    /// Each handler for an end without the usual stop has the run file's heartbeat say where the recording is, besides the
    /// cause, so that the report at the next start gives the position the recording ended at: here Windows asking to end
    /// the session 42 seconds in, and then the window closing 50 seconds in.
    /// </summary>
    [Test]
    public async Task The_handlers_write_where_the_recording_is_into_the_run_file()
    {
        var recordings = new FakeRecordingFactory();
        var recording = recordings.Enqueue(new FakeRecording { Written = TimeSpan.FromSeconds(42) });
        var run = CreateCommand(recordings: recordings).RunAsync(Request(transcribe: false));
        await Wait.UntilAsync(() => recording.Started && _processEnding is not null);

        _session.AskToEnd();
        var asked = RunFile();
        _session.CancelEnd();
        recording.Written = TimeSpan.FromSeconds(50);
        _processEnding!();
        var closed = RunFile();
        _signals.RequestStop();
        await run;

        Assert.That((asked.Position, asked.EndedBy), Is.EqualTo((TimeSpan.FromSeconds(42), RunEndCause.Windows)));
        Assert.That((closed.Position, closed.EndedBy), Is.EqualTo((TimeSpan.FromSeconds(50), RunEndCause.WindowClosed)));
        Assert.That(recording.Calls.Count(call => call == "Flush"), Is.EqualTo(2));
    }

    /// <summary>
    /// While Windows asks whether it may end the session, the run file puts the coming end down to Windows before anything
    /// else, before the recording on disk is brought up to date: should Windows end the process during the handler, as it
    /// does when it forces the end and an answer takes longer than about a second, the next start still knows the cause.
    /// </summary>
    [TestCase(false)]
    [TestCase(true)]
    public async Task Windows_asking_to_end_the_session_has_the_cause_written_first(bool forced)
    {
        var fakes = new FakeRecordingFactory();
        var recordings = new WatchedRecordingFactory(fakes);
        var run = CreateCommand(recordings: recordings).RunAsync(Request(transcribe: false));
        await Wait.UntilAsync(() => fakes.Created is [{ Started: true }]);
        fakes.Created[0].Written = TimeSpan.FromSeconds(42);
        string? causeAtTheFlush = "not flushed";
        recordings.BeforeCall = call =>
        {
            if (call == "Flush") causeAtTheFlush = RunFile().EndedBy;
        };

        _session.AskToEnd(forced);

        Assert.That(causeAtTheFlush, Is.EqualTo(RunEndCause.Windows));
        Assert.That((RunFile().EndedBy, RunFile().Position), Is.EqualTo((RunEndCause.Windows, TimeSpan.FromSeconds(42))), "and the heartbeat after it");
        recordings.BeforeCall = null;
        _session.CancelEnd();
        _signals.RequestStop();
        await run;
    }

    /// <summary>
    /// When Windows forces the end of the session, it gives about a second to answer whether it may end it, so the handler
    /// does not wait for the run's log to reach its file, however slow the disk is: the cause and where the recording is are
    /// written, and the answer goes back at once. Here the log's file has stalled, and a handler that waited for it would
    /// wait without end.
    /// </summary>
    [Test]
    public async Task When_Windows_forces_the_end_the_question_is_answered_without_waiting_for_the_log()
    {
        var stalled = new StalledStream();
        _recorderLog.Dispose();
        _recorderLog = new RecorderLog(TimeProvider.System) { OpenFile = _ => stalled };
        var fakes = new FakeRecordingFactory();
        Task<int> run;
        try
        {
            run = CreateCommand(recordings: fakes, handlerLogPatience: Timeout.InfiniteTimeSpan).RunAsync(Request(transcribe: false));
            await Wait.UntilAsync(() => fakes.Created is [{ Started: true }]);
            fakes.Created[0].Written = TimeSpan.FromSeconds(42);
            _recorderLog.Write(LogLevel.Information, "A line the stalled disk does not take");
            Assert.That(stalled.WaitForAWrite(TimeSpan.FromSeconds(5)), Is.True, "the log's file has stalled");

            var answered = Task.Run(() => _session.AskToEnd(forced: true));

            Assert.That(answered.Wait(TimeSpan.FromSeconds(15)), Is.True, "answered without waiting for the log");
            Assert.That((RunFile().EndedBy, RunFile().Position), Is.EqualTo((RunEndCause.Windows, TimeSpan.FromSeconds(42))));
        }
        finally
        {
            stalled.Release();
        }

        _session.CancelEnd();
        _signals.RequestStop();
        await run;
    }

    /// <summary>A run's context that names another run than its request is a mistake in the code that starts the run, and stops it before it records.</summary>
    [Test]
    public void A_context_for_another_run_is_refused()
    {
        var command = CreateCommand();
        _context.Fill(new RunContext(TestRuns.Id(), RunHome.InWindow, PipeName: null));

        Assert.ThrowsAsync<ArgumentException>(() => EndingByItselfAsync(command.RunAsync(Request())));
        Assert.That(_provider.Created, Is.Empty, "nothing recorded");
    }

    /// <summary>
    /// A run's context is filled once: a second filling is a mistake in the code that starts the run, which is told so, and
    /// the first stays.
    /// </summary>
    [Test]
    public void A_runs_context_is_filled_once()
    {
        var slot = new RunContextSlot();
        var first = new RunContext(TestRuns.Id(1), RunHome.Separate, LinkProtocol.PipeNameFor(TestRuns.Id(1)));
        slot.Fill(first);

        Assert.Throws<InvalidOperationException>(() => slot.Fill(new RunContext(TestRuns.Id(2), RunHome.InWindow, PipeName: null)));
        Assert.That(slot.Value, Is.SameAs(first));
    }

    /// <summary>
    /// Once the run has read its context, the context cannot be filled any more: the run has already taken it to record in
    /// this window, and filling it late is a mistake in the code that starts the run, which is told so.
    /// </summary>
    [Test]
    public void A_runs_context_cannot_be_filled_once_the_run_has_read_it()
    {
        var slot = new RunContextSlot();
        Assert.That(slot.Value, Is.Null, "read before it was filled: the run records in this window");

        Assert.Throws<InvalidOperationException>(() => slot.Fill(new RunContext(TestRuns.Id(), RunHome.Separate, LinkProtocol.PipeNameFor(TestRuns.Id()))));
        Assert.That(slot.Value, Is.Null);
    }

    /// <summary>
    /// When Windows ends the session, the recording's files are finished and the run stops there: nothing is uploaded, and
    /// the run file still says recording, with Windows as the cause, so that the next start reports it, and offers to
    /// continue it while it can still be continued, which the last line promises no more than. The lock and Windows'
    /// question are let go.
    /// </summary>
    [Test]
    public async Task Windows_ending_the_session_finishes_the_files_and_leaves_the_run_for_the_next_start()
    {
        var run = CreateCommand(time: new ManualTimeProvider(DateTimeOffset.UtcNow)).RunAsync(Request());
        await Wait.UntilAsync(() => _capture.Started);
        _capture.Deliver(TestAudio.Shorts(1000, -1000));

        _session.AskToEnd();
        _session.End();

        Assert.That(await run.WaitAsync(TimeSpan.FromSeconds(15)), Is.EqualTo(1));
        Assert.That(TestAudio.ReadWav(Only("*.wav")).Samples, Is.EqualTo(new short[] { 1000, -1000 }), "the files are finished");
        _client.Verify(Calls.AnyTranscription, Times.Never);
        var record = RunFile();
        Assert.That((record.State, record.EndedBy, record.Outcome), Is.EqualTo((RunState.Recording, RunEndCause.Windows, (RunOutcome?)null)));
        Assert.That(OtherLock().IsHeld(), Is.False);
        Assert.That(_session.HeldReason, Is.Null);
        Assert.That(_log.Problems.Last(), Is.EqualTo(
            $"Windows is ending the session, so the recording stopped. Its audio is kept in {KalamFolder()}. The next start of ClassScribe reports it, and offers to continue it if that is still possible"));
    }

    // ------------------------------------------------------------------ how a run ends: outcome and exit code

    /// <summary>Record only ends with the recording kept: exit code 0, and an outcome with the command that transcribes it later.</summary>
    [Test]
    public async Task Record_only_ends_with_the_recording_kept_and_the_command_to_transcribe_it()
    {
        var run = CreateCommand().RunAsync(Request(transcribe: false));
        await Wait.UntilAsync(() => _capture.Started);
        _capture.Deliver(TestAudio.Shorts(1000, -1000));
        _signals.RequestStop();

        Assert.That(await run, Is.EqualTo(0));
        var command = CommandLine.Transcribe(Recording());
        var outcome = new RunOutcome(OutcomeKind.Recorded, 0, $"The recording is in {KalamFolder()}. To transcribe it later, choose 3 in the menu, or run: {command}",
            null, KalamFolder(), command);
        Assert.That(RunFile().Outcome, Is.EqualTo(outcome));
        Assert.That(_results.Shown, Is.EqualTo(new[] { (outcome, true) }));
        Assert.That(_status.Last!.Phase, Is.EqualTo(RecorderPhase.Done));
    }

    /// <summary>The hint for transcribing later names menu choice 3 and a transcribe command that runs as printed.</summary>
    [Test]
    public async Task The_hint_for_transcribing_later_gives_a_command_that_runs_as_printed()
    {
        var run = CreateCommand().RunAsync("Kalam", transcribe: false);
        await Wait.UntilAsync(() => _capture.Started);
        _capture.Deliver(TestAudio.Shorts(1000, -1000));
        _signals.RequestStop();
        await run;

        var hint = _log.Messages.Single(m => m.StartsWith("The recording is in", StringComparison.Ordinal));
        Assert.That(hint, Does.Contain("choose 3 in the menu").And.Contain(CommandLine.Transcribe(Only("*.wav"))));
        Assert.That(hint, Does.Not.Contain("run: ClassScribe transcribe"), "a bare ClassScribe is not a command anywhere");
    }

    /// <summary>Nothing captured means nothing to pay for: nothing is uploaded, the run ends with nothing recorded, and exit code 1.</summary>
    [Test]
    public async Task Nothing_is_uploaded_when_nothing_was_captured()
    {
        var run = CreateCommand().RunAsync("Kalam", transcribe: true);
        await Wait.UntilAsync(() => _capture.Started);
        _signals.RequestStop();

        var exitCode = await run;

        Assert.That(exitCode, Is.EqualTo(1));
        _client.Verify(Calls.AnyTranscription, Times.Never);
        Assert.That(_log.Problems.Last(), Is.EqualTo("No audio was captured. Was anything playing?"));
        Assert.That(RunFile().Outcome, Is.EqualTo(new RunOutcome(OutcomeKind.NothingRecorded, 1, "No audio was captured. Was anything playing?", null, KalamFolder(), null)));
        Assert.That(_status.Last!.Phase, Is.EqualTo(RecorderPhase.Failed));
    }

    /// <summary>
    /// A recording of digital silence, nothing but exact zeros, is not sent, by the upload's own rule, and because trying
    /// again would find the same silence, the hint says what to check rather than how to try again. The run ends with
    /// nothing recorded, and the refusal goes on to the caller, which gives exit code 1.
    /// </summary>
    [Test]
    public async Task A_silent_recording_is_not_sent_or_offered_for_another_try()
    {
        var run = CreateCommand().RunAsync("Kalam", transcribe: true);
        await Wait.UntilAsync(() => _capture.Started);
        _capture.Deliver(TestAudio.Shorts(0, 0, 0, 0));
        _signals.RequestStop();

        Assert.ThrowsAsync<NoAudioException>(async () => await run);
        _client.Verify(Calls.AnyTranscription, Times.Never);
        Assert.That(_log.Problems.Last(), Does.StartWith("Nothing audible was captured from \"Fake Speakers\", so nothing was sent to ElevenLabs").And.Contain("Windows is not muted"));
        Assert.That(_log.Problems, Has.None.Contains("try the transcript again"));
        Assert.That(_log.Problems, Has.None.Contains(CommandLine.Transcribe(Recording())));
        Assert.That(RunFile().Outcome, Is.EqualTo(new RunOutcome(OutcomeKind.NothingRecorded, 1, _log.Problems.Last(), null, KalamFolder(), null)));
        Assert.That(_status.Last!.Phase, Is.EqualTo(RecorderPhase.Failed));
    }

    /// <summary>
    /// A class that is merely quiet, with nothing as loud as the recording counts as audible, is still sent, as transcribe
    /// would send its file: for what is sent, only exact zeros count as silence, by the upload's own rule (see
    /// <see cref="AudioPreparer"/>).
    /// </summary>
    [Test]
    public async Task A_quiet_class_is_still_sent()
    {
        var run = CreateCommand().RunAsync(Request());
        await Wait.UntilAsync(() => _capture.Started);
        _capture.Deliver(TestAudio.Shorts(50, -50, 30, -30));
        _signals.RequestStop();

        Assert.That(await run, Is.EqualTo(0));
        _client.Verify(Calls.AnyTranscription, Times.Once);
        Assert.That(RunFile().Outcome!.Kind, Is.EqualTo(OutcomeKind.Transcribed));
        Assert.That(_log.Problems, Has.None.Contains("Nothing audible"));
    }

    /// <summary>
    /// Record only, when nothing audible reached the recording (digital silence, or sound too faint to hear), keeps the file
    /// but ends with nothing recorded and exit code 1, and says what to check: there is no command to transcribe it, since
    /// there is nothing in it to hear.
    /// </summary>
    [TestCase(new short[] { 0, 0, 0, 0 }, TestName = "Record_only_of_digital_silence_ends_with_nothing_recorded")]
    [TestCase(new short[] { 50, -50, 30, -30 }, TestName = "Record_only_of_sound_too_faint_to_hear_ends_with_nothing_recorded")]
    public async Task Record_only_with_nothing_audible_ends_with_nothing_recorded(short[] samples)
    {
        var run = CreateCommand().RunAsync(Request(transcribe: false));
        await Wait.UntilAsync(() => _capture.Started);
        _capture.Deliver(TestAudio.Shorts(samples));
        _signals.RequestStop();

        Assert.That(await run, Is.EqualTo(1));
        const string silent =
            "Nothing audible was captured from \"Fake Speakers\". Check that the class plays through that device or app (menu choice 6 picks another) and that Windows is not muted";
        Assert.That(_log.Problems.Last(), Is.EqualTo(silent));
        Assert.That(RunFile().Outcome, Is.EqualTo(new RunOutcome(OutcomeKind.NothingRecorded, 1, silent, null, KalamFolder(), null)));
        Assert.That(TestAudio.ReadWav(Recording()).Samples, Is.EqualTo(samples), "the file is kept");
        Assert.That(_status.Last!.Phase, Is.EqualTo(RecorderPhase.Failed));
    }

    /// <summary>Without a key the class is still recorded, so it can be transcribed later, and the hint says how.</summary>
    [Test]
    public async Task Without_a_key_the_class_is_still_recorded()
    {
        var run = CreateCommand(apiKey: "").RunAsync("Kalam", transcribe: true);
        await Wait.UntilAsync(() => _capture.Started);
        _capture.Deliver(TestAudio.Shorts(1000, -1000));
        _signals.RequestStop();

        var exitCode = await run;

        Assert.That(exitCode, Is.EqualTo(1));
        Assert.That(TestAudio.ReadWav(Only("*.wav")).Samples, Is.EqualTo(new short[] { 1000, -1000 }));
        _client.Verify(Calls.AnyTranscription, Times.Never);
        Assert.That(_log.Problems.Last(), Does.Contain("open a new terminal").And.Contain("quit every VS Code window")
            .And.Contain("choose 3 in the menu").And.Contain(CommandLine.Transcribe(Only("*.wav"))));
        Assert.That(RunFile().Outcome, Is.EqualTo(new RunOutcome(OutcomeKind.TranscriptFailed, 1, _log.Problems.Last(), null, KalamFolder(), CommandLine.Transcribe(Only("*.wav")))));
    }

    /// <summary>
    /// A problem with the recordings folder, here an environment variable in the setting that is not set, is
    /// warned about before the recording starts, because a recording started from the command line never
    /// shows the menu that points it out. The class is recorded all the same.
    /// </summary>
    [Test]
    public async Task A_problem_with_the_recordings_folder_is_warned_about_before_recording()
    {
        var outputFolder = Path.Combine(_folder.Path, $"%CLASSSCRIBE_UNSET_{Guid.NewGuid():N}%");
        var problem = new ClassFolders(TestOptions.Create(new RecordingOptions { OutputFolder = outputFolder }), TimeProvider.System, NullLogger<ClassFolders>.Instance).DescribeRootProblem();
        Assert.That(problem, Is.Not.Null, "the setting really has a problem");
        var run = CreateCommand(outputFolder: outputFolder).RunAsync("Kalam", transcribe: false);
        await Wait.UntilAsync(() => _capture.Started);
        _capture.Deliver(TestAudio.Shorts(1000, -1000));
        _signals.RequestStop();

        Assert.That(await run, Is.EqualTo(0), "recorded all the same");
        var messages = _log.Messages.ToList();
        var recording = messages.FindIndex(m => m.StartsWith("Recording \"Kalam\" to", StringComparison.Ordinal));
        Assert.That(messages.IndexOf(problem!), Is.GreaterThanOrEqualTo(0).And.LessThan(recording), "the warning comes before the recording starts");
        Assert.That(_log.Problems, Has.Member(problem), "as a warning");
    }

    /// <summary>
    /// A recording made into a recordings folder given for the run (--output) is not warned about for the setting's
    /// folder, which it does not use.
    /// </summary>
    [Test]
    public async Task A_problem_with_the_setting_is_not_warned_about_for_a_run_that_does_not_use_it()
    {
        var outputFolder = Path.Combine(_folder.Path, $"%CLASSSCRIBE_UNSET_{Guid.NewGuid():N}%");
        var run = CreateCommand(outputFolder: outputFolder).RunAsync(Request(transcribe: false));
        await Wait.UntilAsync(() => _capture.Started);
        _signals.RequestStop();
        await run;

        Assert.That(_log.Problems, Has.None.Contains("Recording:OutputFolder"));
    }

    /// <summary>
    /// A failed transcript leaves the recording in place for another try, and the hint says how to make it; the run ends
    /// with the transcript failed, exit code 1, and the command that tries again. Nothing is opened.
    /// </summary>
    [Test]
    public async Task A_failed_transcript_leaves_the_recording_in_place()
    {
        _client.Setup(Calls.AnyTranscription).ThrowsAsync(new TranscriptionException("ElevenLabs is down."));
        var run = CreateCommand(openWhenDone: true).RunAsync("Kalam", transcribe: true);
        await Wait.UntilAsync(() => _capture.Started);
        _capture.Deliver(TestAudio.Shorts(1000, -1000));
        _signals.RequestStop();

        Assert.ThrowsAsync<TranscriptionException>(async () => await run);
        Assert.That(TestAudio.ReadWav(Recording()).Samples, Is.EqualTo(new short[] { 1000, -1000 }));
        Assert.That(_log.Problems.Last(), Does.Contain("choose 3 in the menu").And.Contain(CommandLine.Transcribe(Recording())));
        Assert.That(_log.Problems.Last(), Does.Not.Contain("kept in"), "no answer arrived, so none was kept");
        Assert.That(RunFile().Outcome, Is.EqualTo(new RunOutcome(OutcomeKind.TranscriptFailed, 1, _log.Problems.Last(), null, KalamFolder(), CommandLine.Transcribe(Recording()))));
        Assert.That(RunFile().State, Is.EqualTo(RunState.Finished));
        Assert.That(_status.Last!.Phase, Is.EqualTo(RecorderPhase.Failed));
        _desktop.VerifyNoOtherCalls();
    }

    /// <summary>
    /// A recording longer than ElevenLabs takes in one file is refused before anything is sent, and because a new try
    /// would be refused the same way, the hint says where the audio is rather than how to try again. The recording is
    /// nine and a half hours and a second long, captured at 10 Hz so that its file stays small.
    /// </summary>
    [Test]
    public async Task A_recording_too_long_to_send_is_not_offered_for_another_try()
    {
        _provider = new FakeCaptureProvider();
        _capture = _provider.Enqueue(new FakeAudioCapture(new AudioFormat(10, 1, 16, false)));
        var run = CreateCommand().RunAsync("Kalam", transcribe: true);
        await Wait.UntilAsync(() => _capture.Started);
        _capture.Deliver(TestAudio.Shorts([.. Enumerable.Repeat((short)1000, (int)new TimeSpan(9, 30, 1).TotalSeconds * 10)]));
        _signals.RequestStop();

        Assert.ThrowsAsync<RecordingTooLongException>(async () => await run);
        _client.Verify(Calls.AnyTranscription, Times.Never);
        Assert.That(_log.Problems.Last(), Does.StartWith($"The audio is safe in {KalamFolder()}"));
        Assert.That(_log.Messages, Has.None.Contains("try the transcript again"));
        Assert.That(_log.Messages, Has.None.Contains(CommandLine.Transcribe(Recording())));
        Assert.That(RunFile().Outcome!.RetryCommand, Is.Null);
    }

    /// <summary>
    /// An answer that ElevenLabs charged for but that could not be read is kept, and the hint points to it
    /// before offering a free rebuild from it and another try, which is charged again. The command the outcome gives to try
    /// again is the free rebuild.
    /// </summary>
    [Test]
    public async Task An_answer_that_could_not_be_read_is_pointed_to_before_another_try()
    {
        _client.Setup(Calls.AnyTranscription).ThrowsAsync(new TranscriptionException("The answer could not be read.", HttpStatusCode.OK, "{\"text\": \"Hello"));
        var run = CreateCommand().RunAsync("Kalam", transcribe: true);
        await Wait.UntilAsync(() => _capture.Started);
        _capture.Deliver(TestAudio.Shorts(1000, -1000));
        _signals.RequestStop();

        Assert.ThrowsAsync<TranscriptionException>(async () => await run);
        var answer = Only("*.unread.json");
        Assert.That(_log.Problems.Last(), Does.Contain($"kept in {answer}").And.Contain("charged again"));
        AssertTheCommandsAfterAKeptAnswer(answer);
        Assert.That(RunFile().Outcome!.RetryCommand, Is.EqualTo(CommandLine.Rebuild(answer)));
    }

    /// <summary>
    /// When the transcript cannot be written after ElevenLabs' answer was saved, the hint points to that
    /// answer before offering another try, which is charged again.
    /// </summary>
    [Test]
    public async Task A_transcript_that_could_not_be_written_points_to_the_answer_saved_beside_it()
    {
        var error = await RecordAndFailToSaveAsync(".md");

        Assert.That(error, Is.InstanceOf<IOException>().Or.InstanceOf<UnauthorizedAccessException>());
        var answer = Path.ChangeExtension(Recording(), ".json");
        Assert.That(File.Exists(answer), Is.True, "the answer is saved");
        Assert.That(_log.Problems.Last(), Does.Contain($"kept in {answer}").And.Contain("charged again"));
        AssertTheCommandsAfterAKeptAnswer(answer);
    }

    /// <summary>
    /// When ElevenLabs' answer cannot be saved as the transcript's answer, it is kept beside the recording under a
    /// name of its own, and the hint points there.
    /// </summary>
    [Test]
    public async Task An_answer_that_could_not_be_saved_as_the_transcripts_is_pointed_to()
    {
        var error = await RecordAndFailToSaveAsync(".json");

        Assert.That(error, Is.InstanceOf<IOException>().Or.InstanceOf<UnauthorizedAccessException>());
        var answer = Path.ChangeExtension(Recording(), ".unread.json");
        Assert.That(File.Exists(answer), Is.True, "the answer is kept beside the recording");
        Assert.That(_log.Problems.Last(), Does.Contain($"kept in {answer}").And.Contain("charged again"));
        AssertTheCommandsAfterAKeptAnswer(answer);
    }

    /// <summary>
    /// Records a moment of sound and stops, with a folder put in the way of every place ElevenLabs' answer could go:
    /// the transcript's .json, and the answer's name of its own beside the recording and in the recordings folder.
    /// Returns what the command failed with.
    /// </summary>
    private async Task<Exception?> RecordWithNowhereToKeepTheAnswerAsync()
    {
        var run = CreateCommand().RunAsync("Kalam", transcribe: true);
        await Wait.UntilAsync(() => _capture.Started);
        _capture.Deliver(TestAudio.Shorts(1000, -1000));
        var files = RecordingFiles.TryParse(Recording())!;
        foreach (var inTheWay in new[] { files.Json(), files.UnreadAnswer(), (files with { Folder = _folder.Path }).UnreadAnswer() })
            Directory.CreateDirectory(inTheWay);
        _signals.RequestStop();

        return Assert.CatchAsync(async () => await run);
    }

    /// <summary>
    /// When ElevenLabs' answer can be kept nowhere, neither beside the recording nor in the recordings folder, the
    /// hint still says plainly that ElevenLabs has charged for it and that it could not be saved. It gives the
    /// answer's transcription ID at ElevenLabs, with which the transcript can be fetched from there, and says that
    /// transcribing again is charged again.
    /// </summary>
    [Test]
    public async Task An_answer_that_could_be_kept_nowhere_is_reported_as_charged_for_with_its_transcription_id()
    {
        var error = await RecordWithNowhereToKeepTheAnswerAsync();

        Assert.That(error, Is.InstanceOf<IOException>().Or.InstanceOf<UnauthorizedAccessException>());
        Assert.That(_log.Problems.Last(), Does.Contain("ElevenLabs has charged for this answer, but it could not be saved anywhere.")
            .And.Contain($"Its transcription ID at ElevenLabs is {Mocks.Transcript.TranscriptionId}, with which the transcript can be fetched from ElevenLabs.")
            .And.Contain("Transcribing again is charged again.")
            .And.Contain(CommandLine.Transcribe(Recording()))
            .And.Not.Contain("kept in"));
    }

    /// <summary>
    /// An answer ElevenLabs charged for that could not be read, and that could be kept nowhere either, is still
    /// reported as charged for. No transcription ID could be read from it, so the hint says plainly that the ID is
    /// unknown rather than point to one.
    /// </summary>
    [Test]
    public async Task An_answer_that_could_not_be_read_or_kept_is_reported_as_charged_for_with_its_id_unknown()
    {
        _client.Setup(Calls.AnyTranscription).ThrowsAsync(new TranscriptionException("The answer could not be read.", HttpStatusCode.OK, "{\"text\": \"Hello"));

        var error = await RecordWithNowhereToKeepTheAnswerAsync();

        Assert.That(error, Is.InstanceOf<TranscriptionException>());
        Assert.That(_log.Problems.Last(), Does.Contain("ElevenLabs has charged for this answer, but it could not be saved anywhere, and its transcription ID at ElevenLabs is unknown.")
            .And.Contain("Transcribing again is charged again.")
            .And.Contain(CommandLine.Transcribe(Recording()))
            .And.Not.Contain("kept in"));
    }

    /// <summary>
    /// An earlier transcript left under the same name, say of a recording of the old layout whose audio was since deleted,
    /// is not taken for an answer kept in this run: a failure that charged nothing gets the usual hint. The new recording
    /// takes the next second, so that it never shares a name with a recording of its course.
    /// </summary>
    [Test]
    public async Task An_earlier_transcript_is_not_taken_for_a_kept_answer()
    {
        var started = new DateTimeOffset(2026, 10, 15, 20, 55, 27, TimeSpan.Zero);
        var earlier = RecordingFiles.ForNew(KalamCourse(), "Kalam", started);
        Directory.CreateDirectory(KalamCourse());
        File.WriteAllText(earlier.Json(), "{}");
        File.WriteAllText(earlier.Transcript(), "# Kalam");
        _client.Setup(Calls.AnyTranscription).ThrowsAsync(new TranscriptionException("ElevenLabs is down."));
        var run = CreateCommand(time: new ManualTimeProvider(started)).RunAsync("Kalam", transcribe: true);
        await Wait.UntilAsync(() => _capture.Started);
        _capture.Deliver(TestAudio.Shorts(1000, -1000));
        _signals.RequestStop();

        Assert.ThrowsAsync<TranscriptionException>(async () => await run);
        Assert.That(Path.GetFileName(Recording()), Is.EqualTo("Kalam_2026-10-15_20-55-28Z.wav"), "the next second, beside none of the earlier one's files");
        Assert.That(_log.Problems.Last(), Does.Contain("To try the transcript again").And.Not.Contain("kept in"));
    }

    /// <summary>
    /// An answer kept by an earlier recording of the same name, in the name's folder or in the recordings
    /// folder, is not taken for this run's. Only the answer this run's error reports counts: a failure that
    /// charged nothing must not send the user to last week's paid answer.
    /// </summary>
    [Test]
    public async Task An_answer_kept_by_an_earlier_recording_of_the_name_is_not_pointed_to()
    {
        var started = new DateTimeOffset(2026, 10, 15, 20, 55, 27, TimeSpan.Zero);
        var lastWeek = RecordingFiles.ForNew(KalamCourse(), "Kalam", started.AddDays(-7));
        Directory.CreateDirectory(KalamCourse());
        File.WriteAllText(Path.Combine(KalamCourse(), $"{lastWeek.BaseName}.unread.json"), "{\"text\": \"Hello");
        File.WriteAllText(_folder.File($"{lastWeek.BaseName}.unread.json"), "{\"text\": \"Hello");
        _client.Setup(Calls.AnyTranscription).ThrowsAsync(new TranscriptionException("ElevenLabs is down."));
        var run = CreateCommand(time: new ManualTimeProvider(started)).RunAsync("Kalam", transcribe: true);
        await Wait.UntilAsync(() => _capture.Started);
        _capture.Deliver(TestAudio.Shorts(1000, -1000));
        _signals.RequestStop();

        Assert.ThrowsAsync<TranscriptionException>(async () => await run);
        Assert.That(_log.Problems.Last(), Does.Contain("To try the transcript again")
            .And.Contain(CommandLine.Transcribe(Recording()))
            .And.Not.Contain("kept in"));
    }

    /// <summary>
    /// Ctrl+C during the upload cancels it, as the line logged before the upload says; the recording stays for another
    /// try. The run ends cancelled, with exit code 130 and the command that transcribes it later, and the cancel reaches
    /// the caller, which reports it with that exit code.
    /// </summary>
    [Test]
    public async Task Ctrl_C_during_the_upload_cancels_it_and_keeps_the_recording()
    {
        _client.Setup(Calls.AnyTranscription).Returns(async (string _, IReadOnlyList<string> _, CancellationToken cancellation) =>
        {
            await Task.Delay(Timeout.Infinite, cancellation);
            return Mocks.Transcript;
        });
        var run = CreateCommand().RunAsync("Kalam", transcribe: true);
        await Wait.UntilAsync(() => _capture.Started);
        _capture.Deliver(TestAudio.Shorts(1000, -1000));
        _signals.RequestStop();
        await Wait.UntilAsync(() => _client.Invocations.Count == 1);

        _signals.Cancel();

        Assert.CatchAsync<OperationCanceledException>(async () => await run);
        Assert.That(TestAudio.ReadWav(Recording()).Samples, Is.EqualTo(new short[] { 1000, -1000 }));
        Assert.That(_log.Messages, Has.Some.StartsWith("Press Ctrl+C to cancel the upload"));
        var outcome = RunFile().Outcome!;
        Assert.That((outcome.Kind, outcome.ExitCode, outcome.RetryCommand), Is.EqualTo((OutcomeKind.Cancelled, 130, CommandLine.Transcribe(Recording()))));
        Assert.That(_status.Last!.Phase, Is.EqualTo(RecorderPhase.Cancelled));
    }

    // ------------------------------------------------------------------ keeping the laptop awake

    /// <summary>
    /// The laptop is kept awake from the start of capture until the transcript is saved: the hold, with its reason, is
    /// taken by the time capture starts, still held while the recording uploads, and let go once, after the transcript
    /// is on disk. Windows asks first before a restart for as long, under the same reason.
    /// </summary>
    [Test]
    public async Task The_laptop_is_kept_awake_from_the_start_of_capture_until_the_transcript_is_saved()
    {
        bool? heldDuringUpload = null;
        string? askedDuringUpload = null;
        bool? savedBeforeRelease = null;
        _client.Setup(Calls.AnyTranscription).Returns((string _, IReadOnlyList<string> _, CancellationToken _) =>
        {
            heldDuringUpload = _power.Holding;
            askedDuringUpload = _session.HeldReason;
            return Task.FromResult(Mocks.Transcript);
        });
        _power.OnRelease = () => savedBeforeRelease = Directory.GetFiles(KalamFolder(), "*.md").Length == 1;
        var run = CreateCommand().RunAsync("Kalam", transcribe: true);
        await Wait.UntilAsync(() => _capture.Started);
        var hold = _power.Holds.Single();
        var heldWhileRecording = !hold.Released;
        var askedWhileRecording = _session.HeldReason;
        _capture.Deliver(TestAudio.Shorts(1000, -1000));
        _signals.RequestStop();

        Assert.That(await run, Is.EqualTo(0));
        Assert.That(hold.Reason, Is.EqualTo(StayAwakeReason));
        Assert.That(hold.Needs() & Awake, Is.EqualTo(Awake), "the laptop awake and ClassScribe running");
        Assert.That(heldWhileRecording, Is.True);
        Assert.That(heldDuringUpload, Is.True);
        Assert.That(savedBeforeRelease, Is.True, "let go after the transcript was saved");
        Assert.That(hold.Releases, Is.EqualTo(1));
        Assert.That((askedWhileRecording, askedDuringUpload, _session.HeldReason), Is.EqualTo((StayAwakeReason, StayAwakeReason, (string?)null)));
    }

    /// <summary>With Recording:BlockShutdown false, Windows is never asked to wait for ClassScribe; the laptop is still kept awake.</summary>
    [Test]
    public async Task Without_Recording_BlockShutdown_Windows_is_not_asked_to_wait()
    {
        var run = CreateCommand(blockShutdown: false).RunAsync(Request(transcribe: false));
        await Wait.UntilAsync(() => _capture.Started);
        _signals.RequestStop();
        await run;

        Assert.That(_session.Blocks, Is.Empty);
        Assert.That(_power.Holds, Has.Count.EqualTo(1));
    }

    /// <summary>Record only keeps the laptop awake until the recording's files are closed: by then its header is final.</summary>
    [Test]
    public async Task Record_only_keeps_the_laptop_awake_until_the_files_are_closed()
    {
        short[]? onDiskAtRelease = null;
        _power.OnRelease = () => onDiskAtRelease = TestAudio.ReadWav(Only("*.wav")).Samples;
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);   // the clock stands still, so only closing the file writes its header
        var run = CreateCommand(time: time).RunAsync("Kalam", transcribe: false);
        await Wait.UntilAsync(() => _capture.Started);
        _capture.Deliver(TestAudio.Shorts(1000, -1000));
        _signals.RequestStop();

        Assert.That(await run, Is.EqualTo(0));
        Assert.That(onDiskAtRelease, Is.EqualTo(new short[] { 1000, -1000 }));
        Assert.That(_power.Holds.Single().Releases, Is.EqualTo(1));
    }

    /// <summary>A transcript that fails lets the laptop go, once, as the failure goes on to the caller.</summary>
    [Test]
    public async Task The_laptop_is_let_go_after_a_failed_transcript()
    {
        _client.Setup(Calls.AnyTranscription).ThrowsAsync(new TranscriptionException("ElevenLabs is down."));
        var run = CreateCommand().RunAsync("Kalam", transcribe: true);
        await Wait.UntilAsync(() => _capture.Started);
        _capture.Deliver(TestAudio.Shorts(1000, -1000));
        _signals.RequestStop();

        Assert.ThrowsAsync<TranscriptionException>(async () => await run);
        Assert.That(_power.Holds.Single().Releases, Is.EqualTo(1));
    }

    /// <summary>A recording with nothing captured lets the laptop go, once.</summary>
    [Test]
    public async Task The_laptop_is_let_go_when_nothing_was_captured()
    {
        var run = CreateCommand().RunAsync("Kalam", transcribe: true);
        await Wait.UntilAsync(() => _capture.Started);
        _signals.RequestStop();

        Assert.That(await run, Is.EqualTo(1));
        Assert.That(_power.Holds.Single().Releases, Is.EqualTo(1));
    }

    /// <summary>
    /// A device in a format ClassScribe cannot read ends the run at the start: the laptop is let go, once, and so is the
    /// lock, and the run file says the recording failed, with the error as its cause. Nothing was recorded, and the words
    /// say so, rather than point to audio that is not there.
    /// </summary>
    [Test]
    public void A_recording_that_cannot_start_ends_the_run_as_failed()
    {
        _provider = new FakeCaptureProvider();
        _capture = _provider.Enqueue(new FakeAudioCapture(new AudioFormat(16000, 1, 8, false)));   // 8-bit: not readable

        var error = Assert.ThrowsAsync<NotSupportedException>(() => EndingByItselfAsync(CreateCommand().RunAsync("Kalam", transcribe: false)));
        Assert.That(_power.Holds.Single().Releases, Is.EqualTo(1));
        Assert.That(OtherLock().IsHeld(), Is.False);
        var record = RunFile();
        Assert.That((record.State, record.Outcome!.Kind, record.Outcome.ExitCode, record.EndedBy), Is.EqualTo((RunState.Failed, OutcomeKind.RecorderFailed, 1, RunEndCause.Error)));
        Assert.That(_status.Last!.Phase, Is.EqualTo(RecorderPhase.Failed));
        var message = $"The recording failed: {error!.Message.TrimEnd('.')}. Nothing was recorded.";
        Assert.That((_log.Problems.Last(), record.Outcome.Message, record.Outcome.Folder), Is.EqualTo((message, message, (string?)null)));
    }

    /// <summary>
    /// A recording that fails once it has recorded, here as the recording loop ends, keeps what it recorded, and the words
    /// say where it is.
    /// </summary>
    [Test]
    public async Task A_recording_that_fails_after_it_has_recorded_says_where_its_audio_is()
    {
        var fakes = new FakeRecordingFactory();
        var recordings = new WatchedRecordingFactory(fakes);
        var run = CreateCommand(recordings: recordings).RunAsync(Request(transcribe: false));
        await Wait.UntilAsync(() => fakes.Created is [{ Started: true }]);
        var recording = fakes.Created[0];
        recording.MainSegments = [recording.Files!.Segment(1)];
        recordings.BeforeCall = call =>
        {
            if (call == "GetSnapshot" && _signals.StopRequested.IsCompleted) throw new InvalidOperationException("A bug.");
        };
        _signals.RequestStop();

        Assert.ThrowsAsync<InvalidOperationException>(async () => await run);
        var message = $"The recording failed: A bug. The audio recorded so far is kept in {KalamFolder()}";
        var outcome = RunFile().Outcome!;
        Assert.That((_log.Problems.Last(), outcome.Kind, outcome.Message, outcome.Folder), Is.EqualTo((message, OutcomeKind.RecorderFailed, message, KalamFolder())));
    }

    /// <summary>
    /// A continued recording that fails at the start of its next part still has its earlier parts, and the words say where
    /// they are.
    /// </summary>
    [Test]
    public async Task A_continued_recording_that_cannot_start_says_where_its_earlier_parts_are()
    {
        var first = CreateCommand().RunAsync(Request(transcribe: false));
        await Wait.UntilAsync(() => _capture.Started);
        _capture.Deliver(TestAudio.Shorts(1000, -1000));
        _signals.RequestStop();
        Assert.That(await first, Is.EqualTo(0));
        var files = RecordingFiles.TryParse(Recording())!;
        _bookkeeping!.Dispose();   // the first run's scope ends

        _provider.Enqueue(new FakeAudioCapture(new AudioFormat(16000, 1, 8, false)));   // 8-bit: not readable
        _signals = new FakeUserSignals();
        var error = Assert.ThrowsAsync<NotSupportedException>(() =>
            EndingByItselfAsync(CreateCommand().RunAsync(Request(transcribe: false, continueBaseName: files.BaseName))));

        Assert.That(_log.Problems.Last(), Is.EqualTo($"The recording failed: {error!.Message.TrimEnd('.')}. The audio recorded so far is kept in {files.Folder}"));
    }

    /// <summary>
    /// The screen is kept on as Recording:KeepScreenOn says: by default (OnBattery) only while the laptop runs on battery,
    /// with Always on mains too, and with Never not even on battery.
    /// </summary>
    [TestCase(KeepScreenOn.OnBattery, true, true)]
    [TestCase(KeepScreenOn.OnBattery, false, false)]
    [TestCase(KeepScreenOn.Always, false, true)]
    [TestCase(KeepScreenOn.Never, true, false)]
    public async Task The_screen_is_kept_on_as_the_setting_says(KeepScreenOn setting, bool onBattery, bool screenOn)
    {
        _powerSource.OnBattery = onBattery;
        var run = CreateCommand(keepScreenOn: setting).RunAsync("Kalam", transcribe: false);
        await Wait.UntilAsync(() => _capture.Started);
        var needs = _power.Holds.Single().Needs();
        _signals.RequestStop();
        await run;

        Assert.That(needs, Is.EqualTo(screenOn ? Awake | PowerNeeds.Display : Awake));
    }

    /// <summary>By default the screen follows the power source during the class: what the hold needs is read from it each time.</summary>
    [Test]
    public async Task By_default_the_screen_follows_the_power_source_during_the_class()
    {
        var run = CreateCommand().RunAsync("Kalam", transcribe: false);
        await Wait.UntilAsync(() => _capture.Started);
        var hold = _power.Holds.Single();
        var onMains = hold.Needs();
        _powerSource.OnBattery = true;
        var onBattery = hold.Needs();
        _signals.RequestStop();
        await run;

        Assert.That(onMains, Is.EqualTo(Awake));
        Assert.That(onBattery, Is.EqualTo(Awake | PowerNeeds.Display));
    }

    /// <summary>
    /// An app's capture that stops delivering is still watched once the record command has handed its loop over: the
    /// warning and the reopening come from the loop, with the app's name. (The rest of the arrival watch is tested with the
    /// loop and its checks.)
    /// </summary>
    [Test]
    public async Task The_loop_watches_that_audio_keeps_arriving()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var chrome = _provider.AddApp(4242, "chrome", "Speakers", level: 0.3f, AppCapture(4242))!;
        _provider.AddApp(4242, "chrome", "Speakers", level: 0.3f, AppCapture(4242));   // ready for the reopening
        var command = CreateCommand(time: time);
        _choice.App = "chrome";
        var run = command.RunAsync("Kalam", transcribe: false);
        await Wait.UntilAsync(() => chrome.Started);

        for (var second = 0; second < 5; second++)
        {
            var published = _status.Count;
            time.Advance(TimeSpan.FromSeconds(1));
            await Wait.UntilAsync(() => _status.Count > published);
        }

        _signals.RequestStop();
        await run;
        Assert.That(_loopLog.Problems, Has.Some.StartsWith("No audio has arrived from \"chrome\" for 5 seconds"));
    }

    // ------------------------------------------------------------------ the app's services

    /// <summary>
    /// A run resolves in a scope of its own from what the app registers, in this window: the record command as the run, a
    /// bookkeeping, a loop and a freeze watch of its own, the window's title and taskbar for its states, the window's way of
    /// opening the results, and a window of its own for Windows' end of the session; and, for the process, the run files,
    /// the lock, the liveness of processes, the free space, the window's signals, keeping the laptop awake, the computer's
    /// clocks, .NET's warning of an error nobody caught, and the recorder's log as one more place every logger writes to,
    /// once.
    /// </summary>
    [Test, Platform("Win"), Category("WindowsProcess")]
    [SupportedOSPlatform("windows")]
    public void A_run_resolves_in_a_scope_of_its_own_with_what_it_needs_in_this_window()
    {
        File.WriteAllText(_folder.File("appsettings.json"), "{}");
        using var app = AppHost.Build(new SettingsPlaces(_folder.Path, userFolder: null), "dummy-key-for-a-local-test", services => services
            .AddSingleton<ILoopbackCaptureProvider>(_provider)
            .AddSingleton(_runtime.Folder)                               // never the real %LOCALAPPDATA%\ClassScribe
            .AddSingleton(new RoamingFolder(_folder.File("Roaming"))));  // nor the real %APPDATA%\ClassScribe
        using var first = app.Services.CreateScope();
        using var second = app.Services.CreateScope();
        var run = first.ServiceProvider;

        Assert.That(run.GetRequiredService<IRecordRun>(), Is.InstanceOf<RecordCommand>());
        Assert.That(run.GetRequiredService<RunBookkeeping>(), Is.SameAs(run.GetRequiredService<RunBookkeeping>()), "one per run");
        Assert.That(run.GetRequiredService<RunBookkeeping>(), Is.Not.SameAs(second.ServiceProvider.GetRequiredService<RunBookkeeping>()), "each run its own");
        Assert.That(run.GetRequiredService<RunContextSlot>(), Is.Not.SameAs(second.ServiceProvider.GetRequiredService<RunContextSlot>()));
        Assert.That(run.GetRequiredService<RecordingLoop>(), Is.Not.SameAs(second.ServiceProvider.GetRequiredService<RecordingLoop>()));
        Assert.That(run.GetRequiredService<FreezeWatch>(), Is.Not.SameAs(second.ServiceProvider.GetRequiredService<FreezeWatch>()));
        Assert.That(run.GetRequiredService<IRecorderStatusSink>(), Is.InstanceOf<WindowStatusSink>());
        Assert.That(run.GetRequiredService<IResultShower>(), Is.InstanceOf<WindowResultShower>());
        Assert.That(run.GetRequiredService<ISessionEvents>(), Is.InstanceOf<SessionWindow>());

        var process = app.Services;
        Assert.That(process.GetRequiredService<RunJournal>(), Is.SameAs(process.GetRequiredService<RunJournal>()));
        Assert.That(process.GetRequiredService<RecordingLock>(), Is.Not.Null);
        Assert.That(process.GetRequiredService<IProcessLiveness>(), Is.InstanceOf<ProcessLiveness>());
        Assert.That(process.GetRequiredService<IFreeSpace>(), Is.InstanceOf<FreeSpace>());
        Assert.That(process.GetRequiredService<IWindowSignals>(), Is.InstanceOf<ConsoleWindowSignals>());
        Assert.That(process.GetRequiredService<IPowerRequests>(), Is.InstanceOf<WindowsPowerRequests>());
        Assert.That(process.GetRequiredService<IPowerSource>(), Is.InstanceOf<WindowsPowerSource>());
        Assert.That(process.GetRequiredService<ISystemTimes>(), Is.InstanceOf<WindowsSystemTimes>());
        Assert.That(process.GetRequiredService<ICrashWarnings>(), Is.InstanceOf<AppDomainCrashWarnings>());
        Assert.That(process.GetRequiredService<RecorderLog>().StartupFile, Is.Null, "a window keeps the lines between runs in memory only");
        Assert.That(process.GetServices<ILoggerProvider>().OfType<RecorderLogProvider>().Count(), Is.EqualTo(1));
        Assert.That(run.GetRequiredService<MicrophoneAdvice>(), Is.SameAs(run.GetRequiredService<MicrophoneAdvice>()), "one advice per run, for the checks");
    }

    // ------------------------------------------------------------------ the microphone modes

    /// <summary>A request to record the class from the device it plays on and your microphone, or your microphone alone in person, with <paramref name="namePart"/> naming it.</summary>
    private RecordRequest WithMicrophone(RecordingMode mode, string? namePart = null, bool transcribe = false) =>
        Request(transcribe: transcribe) with
        {
            Source = new SourcePlan(mode, mode == RecordingMode.Microphone ? null : new ClassSource.Automatic(), new MicrophoneChoice(namePart))
        };

    /// <summary>With the class and your microphone, the start lines name both and V among the keys.</summary>
    [Test]
    public async Task With_the_class_and_the_microphone_the_start_lines_name_both_and_V()
    {
        var recordings = new FakeRecordingFactory();
        var run = CreateCommand(recordings: recordings).RunAsync(WithMicrophone(RecordingMode.ClassAndMicrophone, transcribe: true));
        await Wait.UntilAsync(() => recordings.Created is [{ Started: true }]);
        _signals.RequestStop();
        await run;

        Assert.That(_log.Messages, Has.One.EqualTo("Recording from the device the class plays on, and your microphone"));
        Assert.That(_log.Messages, Has.One.EqualTo(
            "Press Q twice (or Ctrl+C twice) to stop and transcribe, P to pause or resume, M to mark a moment, V to switch your microphone off or on. If the window is closed instead, the recording ends and the audio so far is kept"));
    }

    /// <summary>In person the start line names your microphone alone, by the name part when one is given.</summary>
    [TestCase(null, "Recording from your microphone")]
    [TestCase("OBSBOT", "Recording from your microphone \"OBSBOT\"")]
    public async Task In_person_the_start_line_names_the_microphone_alone(string? namePart, string line)
    {
        var recordings = new FakeRecordingFactory();
        var run = CreateCommand(recordings: recordings).RunAsync(WithMicrophone(RecordingMode.Microphone, namePart));
        await Wait.UntilAsync(() => recordings.Created is [{ Started: true }]);
        _signals.RequestStop();
        await run;

        Assert.That(recordings.Plans.Single().Mode, Is.EqualTo(RecordingMode.Microphone));
        Assert.That(_log.Messages, Has.One.EqualTo(line));
        Assert.That(_log.Messages, Has.One.EqualTo("Press Q twice (or Ctrl+C twice) to stop, P to pause or resume, M to mark a moment, V to switch your microphone off or on. If the window is closed instead, the recording ends and the audio so far is kept"));
    }

    /// <summary>
    /// The end-of-class summary gives the microphone's numbers from the gate: heard, in how many stretches, muted, off,
    /// muted in Windows, and how often the app let go of the microphone, which a setting of Invisible is told about.
    /// </summary>
    [Test]
    public async Task The_end_of_class_summary_gives_the_microphones_numbers()
    {
        var time = new ManualTimeProvider(TestRuns.Start);
        var recording = new FakeMicrophoneRecording();
        var gate = recording.Follow(new FollowRule.Named("chrome"), ClassAppMute.Invisible, time);
        recording.SetMicrophone(true);
        var run = CreateCommand(time: time, recordings: new FakeMicrophoneRecordingFactory(recording), classAppMute: ClassAppMute.Invisible)
            .RunAsync(WithMicrophone(RecordingMode.ClassAndMicrophone));
        await Wait.UntilAsync(() => recording.Inner.Started && _status.Count > 0);
        recording.Inner.MainSegments = [recording.Inner.Files!.Segment(1)];

        gate.Record(Looks.Open(TimeSpan.FromTicks(time.GetTimestamp())));
        time.Advance(TimeSpan.FromSeconds(10));
        gate.Record(Looks.NotListening(TimeSpan.FromTicks(time.GetTimestamp())));
        time.Advance(TimeSpan.FromSeconds(10));
        gate.Record(Looks.Open(TimeSpan.FromTicks(time.GetTimestamp())));
        time.Advance(TimeSpan.FromSeconds(5));
        _signals.RequestStop();

        Assert.That(await run, Is.EqualTo(0));
        Assert.That(_log.Messages, Has.One.EqualTo(
            "Microphone: heard for 00:00:15 in 2 stretches while chrome listened, muted for 00:00:10, off (V) for 00:00:00, muted in Windows for 00:00:00; chrome let go of the microphone 1 time although Recording:ClassAppMute says Invisible"));
    }

    /// <summary>With the class recorded and the microphone not, the run says so, in full, and goes on: exit code 0, the class kept.</summary>
    [Test]
    public async Task Exit_0_when_only_the_microphone_failed()
    {
        var recordings = new FakeRecordingFactory();
        var run = CreateCommand(recordings: recordings).RunAsync(WithMicrophone(RecordingMode.ClassAndMicrophone));
        await Wait.UntilAsync(() => recordings.Created is [{ Started: true }]);
        var recording = recordings.Created[0];
        recording.MainSegments = [recording.Files!.Segment(1)];
        _signals.RequestStop();

        Assert.That(await run, Is.EqualTo(0));
        Assert.That(_log.Problems, Has.One.EqualTo("Your microphone was not recorded in this run: no microphone file was written. The class was recorded and is kept"));
        Assert.That(RunFile().Outcome!.Kind, Is.EqualTo(OutcomeKind.Recorded));
    }

    /// <summary>With a transcript asked for, the same words say that the class goes on to it without the microphone, and it does.</summary>
    [Test]
    public async Task The_class_goes_on_to_the_transcript_when_only_the_microphone_failed()
    {
        var recordings = new FakeRecordingFactory();
        var run = CreateCommand(recordings: recordings).RunAsync(WithMicrophone(RecordingMode.ClassAndMicrophone, transcribe: true));
        await Wait.UntilAsync(() => recordings.Created is [{ Started: true }]);
        var recording = recordings.Created[0];
        var segment = recording.Files!.Segment(1);
        TestAudio.WriteWav(segment, 16000, TestAudio.Tone(16000, seconds: 0.5, frequency: 440, amplitude: 0.5));
        recording.MainSegments = [segment];
        _signals.RequestStop();

        Assert.That(await run, Is.EqualTo(0));
        Assert.That(_log.Problems, Has.One.EqualTo("Your microphone was not recorded in this run: no microphone file was written. The class was recorded and goes on to the transcript"));
        Assert.That(RunFile().Outcome!.Kind, Is.EqualTo(OutcomeKind.Transcribed));
    }

    /// <summary>
    /// With the microphone recorded and the class not, nothing is sent, and the words say the microphone's audio is kept
    /// and how to transcribe it on its own: by one of its .mic.wav files, since a folder is read by its .wav files.
    /// </summary>
    [Test]
    public async Task Nothing_from_the_class_says_the_microphones_audio_is_kept_and_how_to_transcribe_it()
    {
        var recordings = new FakeRecordingFactory();
        var run = CreateCommand(recordings: recordings).RunAsync(WithMicrophone(RecordingMode.ClassAndMicrophone, transcribe: true));
        await Wait.UntilAsync(() => recordings.Created is [{ Started: true }]);
        var recording = recordings.Created[0];
        var microphoneFile = recording.Files!.Segment(1, Track.Microphone);
        recording.MicrophoneSegments = [microphoneFile];
        _signals.RequestStop();

        Assert.That(await run, Is.EqualTo(1));
        Assert.That(_log.Problems, Has.One.EqualTo($"No audio was captured from the class. Was anything playing? Your microphone's audio is kept in {KalamFolder()}. To transcribe it on its own, run: {CommandLine.Transcribe(microphoneFile)}"));
        Assert.That(RunFile().Outcome!.Kind, Is.EqualTo(OutcomeKind.NothingRecorded));
    }
}
