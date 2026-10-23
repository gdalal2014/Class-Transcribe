using System.Net;
using System.Runtime.Versioning;
using System.Text.Json;
using ClassScribe.Audio;
using ClassScribe.Audio.Microphones;
using ClassScribe.Audio.Timeline;
using ClassScribe.Commands;
using ClassScribe.Desktop;
using ClassScribe.Drive;
using ClassScribe.Link;
using ClassScribe.Recorder;
using ClassScribe.Settings;
using ClassScribe.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace ClassScribe.Tests;

/// <summary>
/// A class recorded end to end, in one process: the window as Program puts it together, with its menu and command line,
/// starts the recorder, which runs as the real recorder host over the same ClassScribe folder, in the background of this
/// process instead of a process of its own; the window shows the run through the real view, over an in-memory connection
/// to the recorder's real server; the recorder records a simulated device, and a simulated microphone when asked, sends
/// the recording to a stub standing in for ElevenLabs, and saves the transcript; and the window told to open the results
/// opens them, on a mocked desktop. The run files, the lock and the recordings are real files in temporary folders; no
/// process is started, no window is opened, no device or microphone is opened, nothing is played, no request leaves this
/// process, and the key is a dummy. The clock is the computer's: the recorder checks on the recording once a second, so a
/// test takes a few seconds.
/// </summary>
[TestFixture, Platform("Win"), Category("WindowsProcess")]
[SupportedOSPlatform("windows")]
public class EndToEndTests
{
    /// <summary>The key the window and the recorder run with: never the real one.</summary>
    private const string Key = "dummy-key-for-a-local-test";

    /// <summary>The microphone the tests' laptop records with: its array, Windows' default.</summary>
    private static readonly Microphone Array = LaptopMicrophones.Array;

    /// <summary>Chrome's process, the app the class is recorded from.</summary>
    private const int ChromePid = 4242;

    /// <summary>How often a capture that hands over audio all the time is given a packet, so that the run never takes it for one that stopped.</summary>
    private static readonly TimeSpan PumpInterval = TimeSpan.FromMilliseconds(200);

    /// <summary>What the stub answers an upload with, as ElevenLabs would.</summary>
    private const string Answer = """
        {
          "language_code": "en",
          "language_probability": 0.98,
          "text": "Hello world.",
          "audio_duration_secs": 1.5,
          "transcription_id": "abc123",
          "words": [
            { "text": "Hello", "start": 0.0, "end": 0.5, "type": "word" },
            { "text": " ", "start": 0.5, "end": 0.6, "type": "spacing" },
            { "text": "world.", "start": 0.6, "end": 1.0, "type": "word" }
          ]
        }
        """;

    /// <summary>The format of the simulated device: 16 kHz mono, 16-bit.</summary>
    private static readonly AudioFormat Pcm16KMono = new(16000, 1, 16, false);

    /// <summary>How long a test waits for a step of the run before it fails.</summary>
    private const int StepSeconds = 20;

    /// <summary>How long a test waits for a run, or the recorder, to end.</summary>
    private static readonly TimeSpan EndPatience = TimeSpan.FromSeconds(30);

    private TempFolder _settingsFolder = null!;
    private TempFolder _recordings = null!;
    private TestRuntime _runtime = null!;
    private FakeConsoleUi _ui = null!;
    private FakeWindowSignals _window = null!;
    private Mock<IDesktop> _desktop = null!;
    private Mock<IDesktop> _recorderDesktop = null!;
    private FakeProcessStarter _starts = null!;
    private FakeProcessStarter _recorderStarts = null!;
    private FakeCaptureProvider _devices = null!;
    private FakeAudioCapture _capture = null!;
    private FakeMicrophoneProvider _microphones = null!;
    private FakeMicrophoneSessions _sessions = null!;
    private FakeMicrophoneUsageRecord _record = null!;
    private FakeProcessNames _names = null!;
    private FakeRegistryValues _registry = null!;
    private StubHttpHandler _elevenLabs = null!;
    private FakeCtrlC _ctrlC = null!;
    private RecorderServer? _server;
    private List<Task<int>> _recorders = null!;
    private List<DuplexStreamPair> _connections = null!;
    private List<string> _pipesOpened = null!;
    private CancellationTokenSource _pumps = null!;
    private bool _mixForUpload;
    private FakeGoogle _google = null!;
    private FakeCredentialStore _driveStore = null!;
    private FakeVideoBrowsers _videoBrowsers = null!;
    private FakeProcessTree _videoProcesses = null!;

    /// <summary>The cameras the recorder's camera processes open, played by the test.</summary>
    private FakeCameraCaptureFactory _cameraCaptures = null!;

    /// <summary>The pieces the recorder's camera processes write, played by the test.</summary>
    private FakePieceWriterFactory _pieceWriters = null!;

    /// <summary>The recorder's starts of camera processes: each runs the real camera host, in this process, over the fakes.</summary>
    private FakeCameraProcessStarter _cameraStarts = null!;

    /// <summary>
    /// The folders, the window's console, window and desktop, the recorder's device and microphone, with chrome's
    /// microphone session on the array, Vantage off and ducking set to Do nothing, the stub for ElevenLabs, and the
    /// recorder started in this process.
    /// </summary>
    [SetUp]
    public void SetUp()
    {
        _settingsFolder = new TempFolder();
        _recordings = new TempFolder();
        _runtime = new TestRuntime();
        _ui = new FakeConsoleUi();
        _window = new FakeWindowSignals();
        _desktop = new Mock<IDesktop>();
        _recorderDesktop = new Mock<IDesktop>();
        _starts = new FakeProcessStarter();
        _recorderStarts = new FakeProcessStarter();
        _devices = new FakeCaptureProvider();
        _capture = _devices.Enqueue(new FakeAudioCapture(Pcm16KMono));
        _microphones = new FakeMicrophoneProvider().Connect(Array);
        _sessions = new FakeMicrophoneSessions();
        _record = new FakeMicrophoneUsageRecord();
        _names = new FakeProcessNames();
        _registry = new FakeRegistryValues()
            .Set(MicrophoneAdvice.VantageKey, MicrophoneAdvice.VantageValue, "0")
            .Set(MicrophoneAdvice.DuckingKey, MicrophoneAdvice.DuckingValue, 3);
        _elevenLabs = new StubHttpHandler();
        _ctrlC = new FakeCtrlC();
        _server = null;
        _recorders = [];
        _connections = [];
        _pipesOpened = [];
        _pumps = new CancellationTokenSource();
        _google = new FakeGoogle();
        _driveStore = new FakeCredentialStore();
        _mixForUpload = false;

        // The window starts the recorder: here as the real recorder host, in the background of this process, over the
        // same ClassScribe folder, with the dummy key as the key in its environment; its exit code ends the pretend process.
        _starts.OnStart = launch =>
        {
            if (launch.Kind != FakeLaunchKind.Recorder) return;

            var recorder = Task.Run(() => Recorder().RunAsync(launch.RunId, Key));
            _recorders.Add(recorder);
            _ = recorder.ContinueWith(ended => launch.Exit(ended.IsCompletedSuccessfully ? ended.Result : RecorderHost.FailedExitCode), TaskScheduler.Default);
        };

        // Chrome, for a YouTube video's window, is played on top: alive for the run files, with a window of its own.
        _videoBrowsers = new FakeVideoBrowsers(_starts, _runtime.Processes);
        _videoProcesses = new FakeProcessTree();

        // A lesson's camera process is the real camera host, run in this process over the test's camera and piece writer.
        _cameraCaptures = new FakeCameraCaptureFactory();
        _pieceWriters = new FakePieceWriterFactory();
        _cameraStarts = new FakeCameraProcessStarter
        {
            Run = async (process, jobId) =>
                process.Exited(await new Camera.CameraHost { Captures = _cameraCaptures, Writers = _pieceWriters }.RunAsync(jobId, process.ProcessStream, process.ProcessStream))
        };
    }

    /// <summary>Stops the pumps, waits for every recorder to end, closes the connections and deletes the folders; a file left open fails the deletion, which is the point.</summary>
    [TearDown]
    public async Task TearDown()
    {
        _pumps.Cancel();
        foreach (var recorder in _recorders) await recorder.WaitAsync(EndPatience);
        foreach (var connection in _connections) connection.Dispose();
        _pumps.Dispose();
        _runtime.Dispose();
        _recordings.Dispose();
        _settingsFolder.Dispose();
    }

    /// <summary>
    /// The window, as Program builds it, over settings that send the recordings to the test's folder, uploads to the stub,
    /// bring no window back by itself, and take the class app's mute as <paramref name="classAppMute"/>; with ClassScribe's
    /// own folder in the test's folder, the test's console, window and desktop, no real device or microphone, the test's
    /// readers of the apps' microphone sessions and of Windows' usage record, the recorder started in this process, no job,
    /// and the view of runs reaching the recorder's server over an in-memory connection, with Ctrl+C as the test presses it.
    /// <paramref name="extra"/> adds the test's own stand-ins last.
    /// </summary>
    private IHost BuildWindow(string classAppMute = "Unknown", Action<IServiceCollection>? extra = null)
    {
        File.WriteAllText(_settingsFolder.File("appsettings.json"), JsonSerializer.Serialize(new
        {
            Recording = new { OutputFolder = _recordings.Path, ReopenWindow = "Never", ClassAppMute = classAppMute },
            ElevenLabs = new { BaseUrl = "http://localhost:9/", KeytermsFile = "", MaxAttempts = 1 },
            YouTube = new { OEmbedBaseUrl = "http://localhost:9/" }
        }));
        return AppHost.Build(new SettingsPlaces(_settingsFolder.Path, userFolder: null), Key, services => Extra(services, extra)
            .AddSingleton(_runtime.Folder)
            .AddSingleton<IGoogleCredentialStore>(_driveStore)
            .AddSingleton<IConsoleUi>(_ui)
            .AddSingleton<IWindowSignals>(_window)
            .AddSingleton(_desktop.Object)
            .AddSingleton<ILoopbackCaptureProvider>(new FakeCaptureProvider())
            .AddSingleton<IMicrophoneProvider>(new FakeMicrophoneProvider())
            .AddSingleton<IMicrophoneSessions>(_sessions)
            .AddSingleton<IMicrophoneUsageRecord>(_record)
            .AddSingleton<IProcessNames>(_names)
            .AddSingleton<IOwnProcesses>(new FakeOwnProcesses(Environment.ProcessId))
            .AddSingleton<IProcessStarter>(_starts)
            .AddSingleton<IJobInspector>(new FakeJobInspector())
            .ConfigureAll<HttpClientFactoryOptions>(options => options.HttpMessageHandlerBuilderActions.Add(builder => builder.PrimaryHandler = new GoogleOrElse(_google, _elevenLabs)))
            .AddScoped<IRunViewer>(provider => new RecorderView(
                provider.GetRequiredService<IConsoleUi>(),
                provider.GetRequiredService<IWindowSignals>(),
                provider.GetRequiredService<IDesktop>(),
                provider.GetRequiredService<IProcessLiveness>(),
                provider.GetRequiredService<IOptionsSnapshot<RecordingOptions>>(),
                provider.GetRequiredService<TimeProvider>(),
                provider.GetRequiredService<ILogger<RecorderView>>(),
                provider.GetRequiredService<ILogger<RecorderClient>>())
            {
                OpenPipe = OpenConnectionAsync,
                WatchCtrlC = _ctrlC.Watch
            }));
    }

    /// <summary>Adds <paramref name="extra"/>'s stand-ins to <paramref name="services"/>, last, when there are any, and hands them back.</summary>
    private static IServiceCollection Extra(IServiceCollection services, Action<IServiceCollection>? extra)
    {
        extra?.Invoke(services);
        return services;
    }

    /// <summary>
    /// The recorder's host over ClassScribe's folder, with the simulated device and microphone in place of Windows audio,
    /// the test's readers of the apps' microphone sessions, of Windows' usage record and of the process names, stand-ins
    /// for the registry, the power, the clocks and Windows' end of the session, the stub in place of ElevenLabs, a
    /// preparer that writes a small upload file (or the real one, which mixes the class and the microphone, when the test
    /// asks for the mix), a mocked desktop, a pretend starter for any window it would bring back, and its server kept for
    /// the window's connections.
    /// </summary>
    private RecorderHost Recorder() => new()
    {
        Folder = _runtime.Folder,
        Places = new SettingsPlaces(_settingsFolder.Path, userFolder: null),
        ConfigureServices = services =>
        {
            services.Replace(ServiceDescriptor.Singleton<ILoopbackCaptureProvider>(_devices));
            services.Replace(ServiceDescriptor.Singleton<IMicrophoneProvider>(_microphones));
            services.Replace(ServiceDescriptor.Singleton<IMicrophoneSessions>(_sessions));
            services.Replace(ServiceDescriptor.Singleton<IMicrophoneUsageRecord>(_record));
            services.Replace(ServiceDescriptor.Singleton<IProcessNames>(_names));
            services.Replace(ServiceDescriptor.Singleton<IOwnProcesses>(new FakeOwnProcesses(Environment.ProcessId)));
            services.Replace(ServiceDescriptor.Singleton<IRegistryValues>(_registry));
            services.Replace(ServiceDescriptor.Singleton<IProcessStarter>(_recorderStarts));
            services.Replace(ServiceDescriptor.Singleton<IPowerRequests>(new FakePowerRequests()));
            services.Replace(ServiceDescriptor.Singleton<IGoogleCredentialStore>(_driveStore));
            services.Replace(ServiceDescriptor.Singleton<IPowerSource>(new FakePowerSource()));
            services.Replace(ServiceDescriptor.Singleton<ISystemTimes>(new FakeSystemTimes(TimeProvider.System)));
            services.Replace(ServiceDescriptor.Scoped<ISessionEvents>(_ => new FakeSessionEvents()));
            if (!_mixForUpload) services.Replace(ServiceDescriptor.Singleton(Mocks.AudioPreparer().Object));
            services.Replace(ServiceDescriptor.Singleton(_recorderDesktop.Object));
            services.Replace(ServiceDescriptor.Singleton<IVideoWindows>(_videoBrowsers.Windows));
            services.Replace(ServiceDescriptor.Singleton<Camera.ICameraProvider>(new FakeCameraProvider()));
            services.Replace(ServiceDescriptor.Singleton<Camera.ICameraProcessStarter>(_cameraStarts));
            services.Replace(ServiceDescriptor.Scoped(provider => new YouTube.VideoPlayback(_runtime.Processes, _videoBrowsers.Windows, new ProcessFamily(_videoProcesses),
                provider.GetRequiredService<RunJournal>(), TimeProvider.System, provider.GetRequiredService<ILogger<YouTube.VideoPlayback>>())));
            services.Replace(ServiceDescriptor.Singleton(provider => _server = new RecorderServer(
                provider.GetRequiredService<RecorderLog>(), provider.GetRequiredService<TimeProvider>(), provider.GetRequiredService<ILogger<RecorderServer>>())));
            services.ConfigureAll<HttpClientFactoryOptions>(options => options.HttpMessageHandlerBuilderActions.Add(builder => builder.PrimaryHandler = new GoogleOrElse(_google, _elevenLabs)));
        }
    };

    /// <summary>Opens a connection to the recorder's server, as the window's view opens the run's pipe: an in-memory one the server serves.</summary>
    private Task<Stream> OpenConnectionAsync(string pipeName, TimeSpan limit, CancellationToken cancellationToken)
    {
        var server = _server ?? throw new InvalidOperationException("No recorder serves a run yet.");
        lock (_pipesOpened) _pipesOpened.Add(pipeName);
        var connection = new DuplexStreamPair();
        lock (_connections) _connections.Add(connection);
        _ = server.ServeAsync(connection.RecorderEnd, CancellationToken.None);
        return Task.FromResult(connection.WindowEnd);
    }

    /// <summary>Waits until the window shows <paramref name="text"/> at least <paramref name="times"/> times.</summary>
    private Task UntilShownAsync(string text, int times = 1) => Wait.UntilAsync(() => Shown(text) >= times, StepSeconds);

    /// <summary>How many lines the window shows that hold <paramref name="text"/>.</summary>
    private int Shown(string text) => _ui.Screen.Split(Environment.NewLine).Count(line => line.Contains(text, StringComparison.Ordinal));

    /// <summary>Waits until the recorder has started capturing, and gives it half a second of tone, so that the run has heard something.</summary>
    private async Task PlayIntoTheRecordingAsync()
    {
        await Wait.UntilAsync(() => _capture.Started, StepSeconds);
        _capture.Deliver(Tone());
    }

    /// <summary>Half a second of tone at 16 kHz, loud enough to count as heard on any track.</summary>
    private static byte[] Tone() => TestAudio.Shorts(TestAudio.Tone(16000, seconds: 0.5, frequency: 440, amplitude: 0.5));

    /// <summary>Now on the computer's clock, in the units packets are stamped in.</summary>
    private static long Now => MediaTime.Now(TimeProvider.System);

    /// <summary>A capture of a microphone, 16 kHz mono, which hands over audio all the time as a microphone does, queued as the next one the recorder opens.</summary>
    private FakeAudioCapture MicrophoneCapture() => _microphones.Prepare(new FakeAudioCapture(Pcm16KMono, Array.Name, Array.Id) { DeliversContinuously = true });

    /// <summary>
    /// Keeps <paramref name="capture"/>, which hands over audio all the time, alive as a device would: a fifth of a second
    /// of silence, stamped where it was captured, every fifth of a second from the moment the capture starts until it is
    /// disposed or the test ends. Without it the run would take the capture for one that stopped and replace it.
    /// </summary>
    private void Pump(FakeAudioCapture capture)
    {
        var silence = new byte[(int)(Pcm16KMono.SampleRate * PumpInterval.TotalSeconds) * 2];
        _ = Task.Run(async () =>
        {
            while (!_pumps.IsCancellationRequested && !capture.Disposed)
            {
                if (capture.Started) capture.Deliver(silence, Now - PumpInterval.Ticks);
                await Task.Delay(PumpInterval, _pumps.Token).ConfigureAwait(false);
            }
        });
    }

    /// <summary>The window's latest title, or an empty string before any.</summary>
    private string LatestTitle => _window.Title ?? "";

    /// <summary>The one run file, as it is now.</summary>
    private RunRecord TheRun() => _runtime.Journal.List().Single().Record;

    /// <summary>
    /// The folder the class named Kalam was recorded into, checked to be where a new recording goes: the folder of its
    /// name, the course, under the recordings folder, and in it the one date folder, named after the local date the class
    /// started on, as the recording's own UTC stamp gives it.
    /// </summary>
    private string KalamFolder()
    {
        var dated = Directory.GetDirectories(Path.Combine(_recordings.Path, "Kalam")).Single();
        var started = RecordingFiles.TryParse(Directory.GetFiles(dated, "*.wav").First())!.StartedUtc!.Value;
        Assert.That(Path.GetFileName(dated), Is.EqualTo(ClassFolders.DateFolderName(DateOnly.FromDateTime(started.ToLocalTime().DateTime))),
            "the date folder of the local date the class started on");
        return dated;
    }

    /// <summary>
    /// Menu choice 2, from the key press to the saved transcript: the recorder starts in the background and this window
    /// shows it as the window that started it; Esc leaves the view and the recording goes on, with A offered in the menu;
    /// A shows it again, with the log so far replayed; Q twice stops it; the recording is sent to ElevenLabs with the
    /// dummy key and the transcript saved; the window shown the end opens the folder and the transcript, the recorder opens
    /// nothing; the key, restricted to Speech to Text as the stub plays it, may not read the account, which the recorder
    /// says once, after the end has gone to the window, so the line is in the recording's log; the run file says the run
    /// finished, transcribed, and has been seen; and the menu comes back.
    /// </summary>
    [Test]
    public async Task Menu_choice_2_records_in_the_background_from_the_key_press_to_the_saved_transcript()
    {
        _elevenLabs.Enqueue(HttpStatusCode.OK, Answer);
        _elevenLabs.EnqueueFor(StubHttpHandler.SubscriptionPath, HttpStatusCode.Unauthorized, """{"detail":{"status":"missing_permissions"}}""");
        using var app = BuildWindow();
        _ui.TypeLine("Kalam");
        _ui.Type('2');
        var menu = Task.Run(() => app.Services.GetRequiredService<App>().RunAsync([]));

        await UntilShownAsync(RecordStarter.InTheBackground);
        await PlayIntoTheRecordingAsync();
        await UntilShownAsync("Esc leaves this view");
        await UntilShownAsync("Press Q twice");

        _ui.Type('\u001b');
        await UntilShownAsync("A  Show the recording in progress");
        Assert.That(_capture.Disposed, Is.False, "Esc stops nothing");
        Assert.That(TheRun().State, Is.EqualTo(RunState.Recording));

        _ui.Type('A');
        await UntilShownAsync("Esc leaves this view", times: 2);
        await UntilShownAsync("Press Q twice", times: 2);

        _ui.Type('q', 'q');
        await UntilShownAsync("Transcript saved to");
        Assert.That(await _recorders.Single().WaitAsync(EndPatience), Is.EqualTo(0), "the recorder's exit code");
        await UntilShownAsync("Q  Quit", times: 3);
        _ui.Type('q');
        Assert.That(await menu.WaitAsync(EndPatience), Is.EqualTo(0));

        var run = TheRun();
        var folder = KalamFolder();
        var transcript = Directory.GetFiles(folder, "*.md").Single();
        var log = File.ReadAllLines(Directory.GetFiles(folder, "*.log").Single());
        var saved = System.Array.FindIndex(log, line => line.Contains("Transcript saved to", StringComparison.Ordinal));
        var refused = System.Array.FindIndex(log, line => line.Contains("ElevenLabs would not show the account's credits to this key (HTTP 401, missing_permissions)", StringComparison.Ordinal));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(File.ReadAllText(transcript), Does.Contain("Hello world."));
            Assert.That(refused, Is.GreaterThan(saved), "the refusal is in the recording's log, after the saved transcript");
            Assert.That(log.Count(line => line.Contains("ElevenLabs would not show the account's credits", StringComparison.Ordinal)), Is.EqualTo(1), "said once");
            Assert.That((run.State, run.Outcome?.Kind, run.Outcome?.TranscriptPath, run.Seen, run.Home),
                Is.EqualTo((RunState.Finished, OutcomeKind.Transcribed, transcript, true, RunHome.Separate)));
            Assert.That(run.Request, Is.EqualTo(new RecordRequest(run.RunId, "Kalam", Transcribe: true, new SourcePlan(RecordingMode.Class, new ClassSource.Automatic(), null),
                _recordings.Path, ContinueChoice.New, ContinueBaseName: null, Minutes: null, Open: true)), "the request the menu made, with the starter's decision not to continue");
            Assert.That(File.Exists(_runtime.Journal.RequestFile(run.RunId)), Is.False, "the recorder took its request");
            Assert.That(_elevenLabs.Requests.Where(request => request.Method == HttpMethod.Post).Select(request => request.ApiKey), Is.EqualTo(new[] { Key }), "one upload, with the dummy key");
            Assert.That(_elevenLabs.Requests.Where(request => request.Method == HttpMethod.Get).Select(request => request.ApiKey), Is.EqualTo(new[] { Key }),
                "the account read once, with the dummy key, and not again after the refusal");
            Assert.That(_pipesOpened, Is.EqualTo(new[] { run.Pipe, run.Pipe }), "the two views opened the run's pipe");
            Assert.That(_starts.Recorders.Select(launch => launch.RunId), Is.EqualTo(new[] { run.RunId }));
            Assert.That(_recorderStarts.Launches, Is.Empty, "no window was brought back");
            Assert.That(_window.Titles, Has.Some.StartsWith("REC ").And.Some.StartsWith("DONE "), "the window's title followed the run");
            Assert.That(_capture.Disposed, Is.True);
        }

        _desktop.Verify(desktop => desktop.OpenFolder(Path.GetDirectoryName(transcript)!), Times.Once);
        _desktop.Verify(desktop => desktop.OpenFile(transcript), Times.Once);
        _desktop.VerifyNoOtherCalls();
        _recorderDesktop.VerifyNoOtherCalls();
    }

    /// <summary>
    /// record-only from the command line, with --no-open: the command's pieces reach the recorder whole, the recording is
    /// saved without a transcript and without a request to ElevenLabs, nothing is opened anywhere, the window exits with
    /// the run's exit code, and the run file says the run finished, recorded, and has been seen.
    /// </summary>
    [Test]
    public async Task Record_only_from_the_command_line_records_in_the_background_and_opens_nothing()
    {
        using var app = BuildWindow();
        var command = Task.Run(() => app.Services.GetRequiredService<App>().RunAsync(["record-only", "Kalam", "--device", "auto", "--output", _recordings.Path, "--no-open"]));

        await UntilShownAsync(RecordStarter.InTheBackground);
        await PlayIntoTheRecordingAsync();
        await UntilShownAsync("Press Q twice");
        _ui.Type('q', 'q');

        Assert.That(await command.WaitAsync(EndPatience), Is.EqualTo(0));
        Assert.That(await _recorders.Single().WaitAsync(EndPatience), Is.EqualTo(0));
        var run = TheRun();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Directory.GetFiles(KalamFolder(), "*.wav"), Has.Length.EqualTo(1));
            Assert.That(Directory.GetFiles(KalamFolder(), "*.md"), Is.Empty, "record-only makes no transcript");
            Assert.That(_elevenLabs.Requests, Is.Empty);
            Assert.That((run.State, run.Outcome?.Kind, run.Seen), Is.EqualTo((RunState.Finished, OutcomeKind.Recorded, true)));
            Assert.That((run.Request.Transcribe, run.Request.Open, run.Request.OutputRoot, run.Request.Source.Class), Is.EqualTo((false, false, _recordings.Path, new ClassSource.Automatic())),
                "the command line's pieces reached the recorder");
            Assert.That(_starts.Recorders.Single().Breakaway, Is.False, "no job to break away from");
        }

        _desktop.VerifyNoOtherCalls();
        _recorderDesktop.VerifyNoOtherCalls();
    }

    /// <summary>
    /// Signed in to Google Drive, a class recorded and transcribed from the command line goes up by itself: the window ends
    /// with the transcript, and the recorder, last of all, uploads the transcript, the answer, the audio and the log into
    /// ClassScribe > Kalam > the class's date folder, each checked against Drive's MD5, with the last line, where the class
    /// is on Drive, in Drive's copy of the log; nothing on Drive is changed or removed, and nothing leaves the process.
    /// </summary>
    [Test]
    public async Task Signed_in_a_class_goes_up_to_Google_Drive_after_its_transcript()
    {
        _driveStore.Stored = FakeCredentialStore.With(_google.IssueRefreshToken()).Stored;
        _elevenLabs.Enqueue(HttpStatusCode.OK, Answer);
        _elevenLabs.EnqueueFor(StubHttpHandler.SubscriptionPath, HttpStatusCode.Unauthorized, """{"detail":{"status":"missing_permissions"}}""");
        using var app = BuildWindow();
        var command = Task.Run(() => app.Services.GetRequiredService<App>().RunAsync(["record", "Kalam", "--device", "auto", "--output", _recordings.Path, "--no-open"]));

        await UntilShownAsync(RecordStarter.InTheBackground);
        await PlayIntoTheRecordingAsync();
        await UntilShownAsync("Press Q twice");
        _ui.Type('q', 'q');

        Assert.That(await command.WaitAsync(EndPatience), Is.EqualTo(0));
        Assert.That(await _recorders.Single().WaitAsync(EndPatience), Is.EqualTo(0));
        var folder = KalamFolder();
        var b = Path.GetFileNameWithoutExtension(Directory.GetFiles(folder, "*.md").Single());
        var onDrive = _google.FolderAt($"Kalam/{Path.GetFileName(folder)}");
        Assert.That(onDrive, Is.Not.Null, "ClassScribe > Kalam > the date folder");
        Assert.That(_google.FilesIn(onDrive!.Id).Select(file => file.Name), Is.EquivalentTo(new[] { $"{b}.md", $"{b}.json", $"{b}.wav", $"{b}.log" }));
        foreach (var file in _google.FilesIn(onDrive.Id).Where(file => file.Name != $"{b}.log"))
            Assert.That(file.Content, Is.EqualTo(File.ReadAllBytes(Path.Combine(folder, file.Name))), file.Name);
        var lastLine = $"{b} is on Google Drive: My Drive > ClassScribe > Kalam > {Path.GetFileName(folder)} (4 files,";
        Assert.That(System.Text.Encoding.UTF8.GetString(_google.FilesIn(onDrive.Id).Single(file => file.Name == $"{b}.log").Content), Does.Contain(lastLine));
        Assert.That(File.ReadAllText(Path.Combine(folder, $"{b}.log")), Does.Contain(lastLine));
        Assert.That(_google.Destructive, Is.Empty);
        Assert.That(TheRun().Outcome?.Kind, Is.EqualTo(OutcomeKind.Transcribed));
    }

    /// <summary>
    /// A lesson with the camera from the command line (slice 7): record-only in person with --camera records the microphone
    /// in the background, and beside it the camera's picture, through the real camera track and the real camera process's
    /// record job over the test's camera and piece writer: the start names the camera, its size and its encoder; each frame
    /// is written into the lesson's one piece in its folder; at Q twice the sound is finished, then the piece; and the
    /// request kept the camera with the picture's size the window resolved.
    /// </summary>
    [Test]
    public async Task A_lesson_with_the_camera_records_its_picture_beside_the_sound()
    {
        var microphone = MicrophoneCapture();
        using var app = BuildWindow(extra: services => services
            .Replace(ServiceDescriptor.Singleton<Camera.ICameraProvider>(new FakeCameraProvider()))
            .Replace(ServiceDescriptor.Singleton<Camera.ICameraSystem>(new FakeCameraSystem()))
            .Replace(ServiceDescriptor.Singleton<Camera.ICameraUsageRecord>(new FakeCameraUsageRecord()))
            .Replace(ServiceDescriptor.Singleton<IRegistryValues>(_registry)));
        var command = Task.Run(() => app.Services.GetRequiredService<App>().RunAsync(["record-only", "Kalam", "--in-person", "--camera", "OBSBOT", "--output", _recordings.Path, "--no-open"]));

        await UntilShownAsync(RecordStarter.InTheBackground);
        await Wait.UntilAsync(() => microphone.Started, StepSeconds);
        Pump(microphone);
        microphone.Deliver(Tone(), Now);
        await Wait.UntilAsync(() => _cameraCaptures.Last is { Streaming: true }, StepSeconds);
        for (var frame = 0; frame < 30; frame++)
        {
            _cameraCaptures.Last!.Deliver(Now, 120);
            await Task.Delay(33);
        }

        await UntilShownAsync($"Camera: \"{LaptopCameras.Obsbot.Name}\" (USB), 1280x720 at 30 frames a second, 5 Mbit/s, hardware encoder (Fake H.264 Encoder)");
        await Task.Delay(700);
        _ui.Type('q', 'q');

        Assert.That(await command.WaitAsync(EndPatience), Is.EqualTo(0));
        Assert.That(await _recorders.Single().WaitAsync(EndPatience), Is.EqualTo(0));
        var run = TheRun();
        var folder = KalamFolder();
        var writer = _pieceWriters.Made.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Directory.GetFiles(folder, "*.cam.mp4"), Is.EqualTo(new[] { writer.Path }));
            Assert.That(writer.Samples.Count(sample => !sample.Black), Is.GreaterThan(15), "the frames written into the piece");
            Assert.That(writer.FinishedAt, Is.Not.Null);
            Assert.That(run.Request.Source.Camera, Is.EqualTo(new CameraChoice("OBSBOT", "720p")));
            Assert.That((run.State, run.Outcome?.Kind), Is.EqualTo((RunState.Finished, OutcomeKind.Recorded)));
            Assert.That(Shown("and the camera \"OBSBOT\" at 720p"), Is.GreaterThanOrEqualTo(1));
            Assert.That(Shown("The camera's piece"), Is.GreaterThanOrEqualTo(1));
            Assert.That(_cameraCaptures.Made.Select(made => made.CameraId), Has.All.EqualTo(LaptopCameras.Obsbot.Id));
            Assert.That(_cameraCaptures.Last!.Disposed, Is.True);
            Assert.That(microphone.Disposed, Is.True);
        }
    }

    /// <summary>
    /// In person from the command line: record-only with --in-person records the microphone alone, in the background,
    /// through the link; the start says it records your microphone and that it records the room; the microphone is the
    /// main track, so its file carries the recording's plain name and no microphone file is written beside it; the
    /// end-of-class summary counts the time on and off with V; no output device is opened; and the request the recorder
    /// got says in person with no microphone named.
    /// </summary>
    [Test]
    public async Task In_person_from_the_command_line_records_the_microphone_alone_in_the_background()
    {
        var microphone = MicrophoneCapture();
        using var app = BuildWindow();
        var command = Task.Run(() => app.Services.GetRequiredService<App>().RunAsync(["record-only", "Kalam", "--in-person", "--output", _recordings.Path, "--no-open"]));

        await UntilShownAsync(RecordStarter.InTheBackground);
        await Wait.UntilAsync(() => microphone.Started, StepSeconds);
        Pump(microphone);
        microphone.Deliver(Tone(), Now);
        await UntilShownAsync("Press Q twice");
        await UntilShownAsync("Your microphone records the room");
        _ui.Type('q', 'q');

        Assert.That(await command.WaitAsync(EndPatience), Is.EqualTo(0));
        Assert.That(await _recorders.Single().WaitAsync(EndPatience), Is.EqualTo(0));
        var run = TheRun();
        var folder = KalamFolder();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Directory.GetFiles(folder, "*.wav"), Has.Length.EqualTo(1), "one file, the microphone as the main track");
            Assert.That(Directory.GetFiles(folder, "*.mic.wav"), Is.Empty, "in person there is no microphone track beside the main one");
            Assert.That(Shown("Recording from your microphone"), Is.GreaterThanOrEqualTo(1));
            Assert.That(Shown("Microphone: on for"), Is.GreaterThanOrEqualTo(1), "the summary counts the time on and off with V");
            Assert.That(run.Request.Source, Is.EqualTo(new SourcePlan(RecordingMode.Microphone, Class: null, new MicrophoneChoice(null))));
            Assert.That((run.State, run.Outcome?.Kind), Is.EqualTo((RunState.Finished, OutcomeKind.Recorded)));
            Assert.That(_devices.Created, Is.Empty, "no output device was opened");
            Assert.That(_microphones.Opened.Select(opened => opened.Id), Is.EqualTo(new[] { Array.Id }));
            Assert.That(_elevenLabs.Requests, Is.Empty);
            Assert.That(microphone.Disposed, Is.True);
        }

        _desktop.VerifyNoOtherCalls();
        _recorderDesktop.VerifyNoOtherCalls();
    }

    /// <summary>
    /// The class and your microphone from the command line, the microphone following chrome whose mute Windows can see:
    /// the class is recorded from chrome's sound and the microphone from the array; the start says the microphone is
    /// recorded too; while chrome listens your voice is written, when chrome stops taking sound from the microphone the
    /// title says MIC MUTED and nothing from the microphone goes into the recording, and when chrome listens again your
    /// voice is written again; at the stop the class and the microphone are mixed by the real preparer into one upload for ElevenLabs,
    /// sent with the dummy key, the transcript is saved, both tracks stay on disk, the summary says how long you were
    /// heard, and the request the recorder got carries the whole plan. The stub plays a key allowed to read the account,
    /// so the recording's .log ends with the estimate, the saved transcript and the credits charged, in that order: the
    /// recorder reads the account again only once the end has gone to the window.
    /// </summary>
    [Test]
    public async Task With_the_class_the_microphone_follows_chrome_and_both_are_mixed_for_the_upload()
    {
        _mixForUpload = true;
        _elevenLabs.Enqueue(HttpStatusCode.OK, Answer);
        _elevenLabs.EnqueueFor(StubHttpHandler.SubscriptionPath, HttpStatusCode.OK, """{"character_count":1000,"character_limit":100000}""");
        _elevenLabs.EnqueueFor(StubHttpHandler.SubscriptionPath, HttpStatusCode.OK, """{"character_count":1500,"character_limit":100000}""");
        var chrome = new FakeAudioCapture(Pcm16KMono, "chrome", $"process:{ChromePid}") { DeliversContinuously = true };
        _devices.AddApp(ChromePid, "chrome", "Fake Speakers", level: 0.5f, capture: chrome);
        _sessions.SetSessions(Array.Id, Laptop.Session(Array.Id, Laptop.ChromeUtility));   // chrome is listening on the array
        var microphone = MicrophoneCapture();
        using var app = BuildWindow(classAppMute: "Visible");
        var command = Task.Run(() => app.Services.GetRequiredService<App>().RunAsync(
            ["record", "Kalam", "--app", "chrome", "--mic", "--mic-follows", "chrome", "--output", _recordings.Path, "--no-open"]));

        await UntilShownAsync(RecordStarter.InTheBackground);
        await Wait.UntilAsync(() => chrome.Started && microphone.Started, StepSeconds);
        Pump(chrome);
        Pump(microphone);
        await UntilShownAsync("Your microphone is recorded too");
        await UntilShownAsync("Press Q twice");
        chrome.Deliver(Tone(), Now);
        microphone.Deliver(Tone(), Now);
        await Wait.UntilAsync(() => LatestTitle.StartsWith("REC ", StringComparison.Ordinal) && !LatestTitle.Contains("MIC "), StepSeconds);

        _sessions.SetSessions(Array.Id, Laptop.Session(Array.Id, Laptop.ChromeUtility, CaptureSessionState.Inactive));   // chrome stops taking sound
        await Wait.UntilAsync(() => LatestTitle.Contains("MIC MUTED", StringComparison.Ordinal), StepSeconds);
        microphone.Deliver(TestAudio.Shorts(Enumerable.Repeat((short)1000, 3200).ToArray()), Now);   // said while muted
        await Task.Delay(500);
        _sessions.SetSessions(Array.Id, Laptop.Session(Array.Id, Laptop.ChromeUtility));   // chrome listens again
        await Wait.UntilAsync(() => LatestTitle.StartsWith("REC ", StringComparison.Ordinal) && !LatestTitle.Contains("MIC ", StringComparison.Ordinal), StepSeconds);
        microphone.Deliver(Tone(), Now);
        await Task.Delay(500);
        _ui.Type('q', 'q');

        Assert.That(await command.WaitAsync(EndPatience), Is.EqualTo(0));
        Assert.That(await _recorders.Single().WaitAsync(EndPatience), Is.EqualTo(0));
        var run = TheRun();
        var folder = KalamFolder();
        var microphoneFile = Directory.GetFiles(folder, "*.mic.wav").Single();
        var voice = TestAudio.ReadWav(microphoneFile).Samples;
        var log = File.ReadAllLines(Directory.GetFiles(folder, "*.log").Single());
        var estimate = System.Array.FindIndex(log, line => line.Contains("ElevenLabs transcribed 1 second of audio (00:00:01): less than a cent at the API price, $0.22 an hour", StringComparison.Ordinal));
        var saved = System.Array.FindIndex(log, line => line.Contains("Transcript saved to", StringComparison.Ordinal));
        var charged = System.Array.FindIndex(log, line => line.Contains("ElevenLabs charged 500 credits for this transcript; 98,500 of 100,000 credits are left", StringComparison.Ordinal));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Directory.GetFiles(folder, "*.wav"), Has.Length.EqualTo(2), "the class's file and the microphone's");
            Assert.That(File.ReadAllText(Directory.GetFiles(folder, "*.md").Single()), Does.Contain("Hello world."));
            Assert.That(voice, Has.Some.GreaterThan(8000), "your voice while chrome listened");
            Assert.That(voice, Has.None.EqualTo(1000), "nothing from the microphone while chrome was not listening");
            Assert.That(Shown("Microphone: heard for"), Is.GreaterThanOrEqualTo(1));
            Assert.That(_window.Titles, Has.Some.Contains("MIC MUTED"));
            Assert.That(run.Request.Source, Is.EqualTo(new SourcePlan(RecordingMode.ClassAndMicrophone, new ClassSource.App("chrome"), new MicrophoneChoice(null, new FollowRule.Named("chrome")))));
            Assert.That((run.State, run.Outcome?.Kind), Is.EqualTo((RunState.Finished, OutcomeKind.Transcribed)));
            Assert.That(_elevenLabs.Requests.Where(request => request.Method == HttpMethod.Post).Select(request => request.ApiKey), Is.EqualTo(new[] { Key }), "one upload, with the dummy key");
            Assert.That(_elevenLabs.Requests.Count(request => request.Method == HttpMethod.Get), Is.EqualTo(2), "the account read before the upload and after the transcript");
            Assert.That(estimate, Is.GreaterThanOrEqualTo(0), "the estimate is in the recording's log");
            Assert.That(saved, Is.GreaterThan(estimate), "the saved line follows the estimate");
            Assert.That(charged, Is.GreaterThan(saved), "the credits charged come last");
            Assert.That(_microphones.Opened.Select(opened => opened.Id), Is.EqualTo(new[] { Array.Id }));
            Assert.That(chrome.Disposed, Is.True);
            Assert.That(microphone.Disposed, Is.True);
        }

        _desktop.VerifyNoOtherCalls();
        _recorderDesktop.VerifyNoOtherCalls();
    }

    /// <summary>
    /// The microphone check from the command line, in the window: mic-check --seconds 1 watches the test's readers for a
    /// second and stops by itself with exit code 0, its log in the folder --log-folder names, with the log's path shown
    /// first and last; chrome's session on the array is in the lines; no recorder is started, and no microphone is opened.
    /// </summary>
    [Test]
    public async Task Mic_check_from_the_command_line_watches_for_a_second_and_writes_its_log()
    {
        using var logs = new TempFolder();
        _sessions.Set(Laptop.Listing(Array.Id, Laptop.Session(Array.Id, Laptop.ChromeUtility)));
        using var app = BuildWindow();

        var exitCode = await app.Services.GetRequiredService<App>().RunAsync(["mic-check", "--seconds", "1", "--log-folder", logs.Path]).WaitAsync(EndPatience);

        var log = Directory.GetFiles(logs.Path, "mic-check_*.log").Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(exitCode, Is.EqualTo(0));
            Assert.That(Shown($"Log file: {log}"), Is.EqualTo(1), "the path first");
            Assert.That(Shown($"The log is saved as: {log}"), Is.EqualTo(1), "and last");
            Assert.That(File.ReadAllText(log), Does.Contain("chrome"), "chrome's session on the array");
            Assert.That(_sessions.ReadAlls, Is.GreaterThanOrEqualTo(2), "a look at the start and every quarter second");
            Assert.That(_starts.Launches, Is.Empty, "the check runs in the window");
            Assert.That(_runtime.Journal.List(), Is.Empty, "no run file: the check is no recording");
        }
    }

    /// <summary>
    /// A batch of two YouTube videos from the command line, downloaded and transcribed in the window, signed in to Google
    /// Drive: yt-dlp is played and finds its helpers in winget's links folder of a played disk; each video becomes a
    /// recording of the course in one date folder, with its source file; each is transcribed through the stub, its transcript
    /// headed by the video's title and each position linked to that moment of the video; each transcript's run file is over
    /// and seen; and each recording goes up whole, its audio and source file included, into ClassScribe > Risala > the date
    /// folder. Nothing reaches YouTube, ElevenLabs or Google.
    /// </summary>
    [Test]
    public async Task A_batch_of_two_YouTube_videos_is_downloaded_transcribed_and_goes_up_to_Drive()
    {
        _driveStore.Stored = FakeCredentialStore.With(_google.IssueRefreshToken()).Stored;
        _elevenLabs.Enqueue(HttpStatusCode.OK, Answer);
        _elevenLabs.Enqueue(HttpStatusCode.OK, Answer);
        _elevenLabs.EnqueueFor(StubHttpHandler.SubscriptionPath, HttpStatusCode.Unauthorized, """{"detail":{"status":"missing_permissions"}}""");
        var folders = new YouTube.YouTubeFolders(_runtime.Folder);
        var tools = new FakeToolRunner(folders, TimeProvider.System)
            .With("abcdefghijk", YtDlpReadTests.Info("abcdefghijk", "The creed, part 1", duration: 3), 3)
            .With("bcdefghijkl", YtDlpReadTests.Info("bcdefghijkl", "The creed, part 2", duration: 3), 3);
        var links = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify), "Microsoft", "WinGet", "Links");
        var disk = new FakeFileProbe();
        disk.Add(Path.Combine(links, "yt-dlp.exe"), Path.Combine(links, "deno.exe"), Path.Combine(links, "ffmpeg.exe"), Path.Combine(links, "ffprobe.exe"));
        using var app = BuildWindow(extra: services => services
            .AddSingleton<YouTube.IToolRunner>(tools)
            .AddSingleton<IFileProbe>(disk)
            .AddSingleton<IRegistryValues>(_registry)
            .AddSingleton<IPowerRequests>(new FakePowerRequests())
            .AddScoped<YouTube.IBatchPace, InstantPace>());

        var exitCode = await app.Services.GetRequiredService<App>().RunAsync(
            ["youtube", "https://youtu.be/abcdefghijk", "https://youtu.be/bcdefghijkl", "--name", "Risala", "--output", _recordings.Path, "--download", "--yes", "--no-open"])
            .WaitAsync(EndPatience);

        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(tools.Downloads, Is.EqualTo(["abcdefghijk", "bcdefghijkl"]));
        var dateFolder = Directory.GetDirectories(Path.Combine(_recordings.Path, "Risala")).Single();
        var transcripts = Directory.GetFiles(dateFolder, "*.md").Order(StringComparer.Ordinal).ToList();
        Assert.That(transcripts.Select(path => File.ReadAllLines(path)[0]), Is.EqualTo(["# The creed, part 1", "# The creed, part 2"]));
        Assert.That(File.ReadAllText(transcripts[0]), Does.Contain("[[00:00:00]](https://www.youtube.com/watch?v=abcdefghijk&t=0s) Hello world."));
        Assert.That(_runtime.Journal.List().Select(run => (run.Record.Outcome?.Kind, run.Record.Seen, run.IsVideo)),
            Is.EqualTo(new[] { ((OutcomeKind?)OutcomeKind.Transcribed, true, true), (OutcomeKind.Transcribed, true, true) }));
        var onDrive = _google.FolderAt($"Risala/{Path.GetFileName(dateFolder)}");
        Assert.That(onDrive, Is.Not.Null, "ClassScribe > Risala > the batch's date folder");
        var names = _google.FilesIn(onDrive!.Id).Select(file => file.Name).ToList();
        foreach (var transcript in transcripts)
        {
            var b = Path.GetFileNameWithoutExtension(transcript);
            Assert.That(names, Is.SupersetOf(new[] { $"{b}.md", $"{b}.json", $"{b}.wav", $"{b}.source.json", $"{b}.log" }));
        }

        Assert.That(_google.Destructive, Is.Empty);
        Assert.That(_starts.Launches, Is.Empty, "the batch runs in the window");
    }

    /// <summary>
    /// A batch of two YouTube videos from the command line, recorded as they play (design 4, with Gulzar's answers 2 A and 3
    /// B), each in a recorder of its own, as a class is: the titles read from a stub at a stub address; Chrome found on a
    /// played disk and started for each video's window, with its own profile, never playing by itself; Enter asked before
    /// each; each window's browser process recorded through process loopback, from before anything plays; the first ended by
    /// its window closed, the second by Q twice in the window's view; both in one date folder, each with its source file,
    /// confirmed; both transcribed through the stub, in this window, headed by the video's title; every window closed; and
    /// exit code 0. Nothing reaches YouTube or ElevenLabs.
    /// </summary>
    [Test]
    public async Task A_batch_of_two_YouTube_videos_is_recorded_as_they_play_and_transcribed()
    {
        _elevenLabs.EnqueueFor("/oembed", HttpStatusCode.OK, """{"title":"The creed, part 1","author_name":"Shaykh X","author_url":"https://www.youtube.com/@shaykhx"}""");
        _elevenLabs.EnqueueFor("/oembed", HttpStatusCode.OK, """{"title":"The creed, part 2","author_name":"Shaykh X","author_url":"https://www.youtube.com/@shaykhx"}""");
        _elevenLabs.Enqueue(HttpStatusCode.OK, Answer);
        _elevenLabs.Enqueue(HttpStatusCode.OK, Answer);
        _elevenLabs.EnqueueFor(StubHttpHandler.SubscriptionPath, HttpStatusCode.Unauthorized, """{"detail":{"status":"missing_permissions"}}""");
        var chrome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolderOption.DoNotVerify), "Google", "Chrome", "Application", "chrome.exe");
        var disk = new FakeFileProbe();
        disk.Add(chrome);
        disk.Versions[chrome] = "141.0.7390.108";

        // Each window's browser process plays through process loopback, its capture ready and kept alive as Windows' is.
        List<FakeAudioCapture> windows = [];
        var before = _starts.OnStart;
        _starts.OnStart = launch =>
        {
            before?.Invoke(launch);
            if (launch.Kind != FakeLaunchKind.Browser) return;

            _videoProcesses.Add(launch.ProcessId, Environment.ProcessId, "chrome.exe", launch.StartedUtc);
            var capture = new FakeAudioCapture(Pcm16KMono, "ClassScribe's video window", $"process:{launch.ProcessId}") { DeliversContinuously = true };
            _devices.Enqueue(capture);
            lock (windows) windows.Add(capture);
            Pump(capture);
        };
        using var app = BuildWindow(extra: services => services
            .AddSingleton<IFileProbe>(disk)
            .AddSingleton<IRegistryValues>(_registry)
            .AddSingleton<IVideoWindows>(_videoBrowsers.Windows)
            .AddSingleton<IPowerRequests>(new FakePowerRequests()));

        var command = Task.Run(() => app.Services.GetRequiredService<App>().RunAsync(
            ["youtube", "https://youtu.be/abcdefghijk", "https://youtu.be/bcdefghijkl", "--name", "Risala", "--output", _recordings.Path, "--yes", "--no-open"]));

        await UntilShownAsync("Next: 01 The creed, part 1. Enter opens it");
        _ui.Type('\r');
        await UntilShownAsync("Press play in the video window");
        await Wait.UntilAsync(() => windows.Count == 1 && windows[0].Started, StepSeconds);
        windows[0].Deliver(Tone(), Now);
        await Task.Delay(1500);
        _videoBrowsers.End(_videoBrowsers.Started[0].ProcessId);

        await UntilShownAsync("Next: 02 The creed, part 2. Enter opens it");
        _ui.Type('\r');
        await UntilShownAsync("Press play in the video window", times: 2);
        await Wait.UntilAsync(() => windows.Count == 2 && windows[1].Started, StepSeconds);
        windows[1].Deliver(Tone(), Now);
        await Task.Delay(1500);
        _ui.Type('q', 'q');

        Assert.That(await command.WaitAsync(EndPatience), Is.EqualTo(0));
        foreach (var recorder in _recorders) Assert.That(await recorder.WaitAsync(EndPatience), Is.EqualTo(0));
        var dateFolder = Directory.GetDirectories(Path.Combine(_recordings.Path, "Risala")).Single();
        var sources = Directory.GetFiles(dateFolder, "*.source.json").Order(StringComparer.Ordinal).Select(path => YouTube.VideoSourceFile.Read(path)!).ToList();
        var transcripts = Directory.GetFiles(dateFolder, "*.md").Order(StringComparer.Ordinal).ToList();
        var browsers = _videoBrowsers.Started;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(sources.Select(source => (source.Title, source.Obtained, source.Ended, source.EndConfirmed, source.Tool)), Is.EqualTo(new[]
            {
                ("The creed, part 1", YouTube.VideoSource.Recorded, "the video window was closed", true, "Chrome 141.0.7390.108"),
                ("The creed, part 2", YouTube.VideoSource.Recorded, "stopped with Q", true, "Chrome 141.0.7390.108")
            }));
            Assert.That(transcripts.Select(path => File.ReadAllLines(path)[0]), Is.EqualTo(["# The creed, part 1", "# The creed, part 2"]));
            Assert.That(File.ReadAllText(transcripts[0]), Does.Contain("recorded as it played in ClassScribe's window"));
            Assert.That(browsers.Select(browser => (browser.Program, browser.Arguments[^1])), Is.EqualTo(new[]
            {
                (chrome, "--app=https://www.youtube.com/watch?v=abcdefghijk"), (chrome, "--app=https://www.youtube.com/watch?v=bcdefghijkl")
            }));
            Assert.That(browsers.SelectMany(browser => browser.Arguments), Has.None.Contains("autoplay"));
            Assert.That(browsers.Select(browser => browser.ExitCode), Has.All.Not.Null, "every window closed");
            Assert.That(_devices.Created.Select(capture => capture.DeviceId), Is.EqualTo(browsers.Select(browser => $"process:{browser.ProcessId}")),
                "each window's own browser process, and nothing else");
            Assert.That(_starts.Recorders, Has.Count.EqualTo(2), "a recorder for each video");
            Assert.That(_runtime.Journal.List().Count(run => run.IsVideo && run.Record.Outcome?.Kind == OutcomeKind.Transcribed), Is.EqualTo(2), "each transcript's run file");
            Assert.That(_elevenLabs.Requests.Where(request => request.Method == HttpMethod.Post).Select(request => request.ApiKey), Is.EqualTo(new[] { Key, Key }));
        }
    }
}
