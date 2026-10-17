using System.Net;
using System.Runtime.Versioning;
using System.Text.Json;
using ClassScribe.Audio;
using ClassScribe.Commands;
using ClassScribe.Desktop;
using ClassScribe.Link;
using ClassScribe.Recorder;
using ClassScribe.Settings;
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
/// to the recorder's real server; the recorder records a simulated device, sends the recording to a stub standing in for
/// ElevenLabs, and saves the transcript; and the window told to open the results opens them, on a mocked desktop. The run
/// files, the lock and the recordings are real files in temporary folders; no process is started, no window is opened, no
/// device is opened, nothing is played, no request leaves this process, and the key is a dummy. The clock is the
/// computer's: the recorder checks on the recording once a second, so a test takes a few seconds.
/// </summary>
[TestFixture, Platform("Win"), Category("WindowsProcess")]
[SupportedOSPlatform("windows")]
public class EndToEndTests
{
    /// <summary>The key the window and the recorder run with: never the real one.</summary>
    private const string Key = "dummy-key-for-a-local-test";

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
    private StubHttpHandler _elevenLabs = null!;
    private FakeCtrlC _ctrlC = null!;
    private RecorderServer? _server;
    private List<Task<int>> _recorders = null!;
    private List<DuplexStreamPair> _connections = null!;
    private List<string> _pipesOpened = null!;

    /// <summary>The folders, the window's console, window and desktop, the recorder's device, the stub for ElevenLabs, and the recorder started in this process.</summary>
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
        _elevenLabs = new StubHttpHandler();
        _ctrlC = new FakeCtrlC();
        _server = null;
        _recorders = [];
        _connections = [];
        _pipesOpened = [];

        // The window starts the recorder: here as the real recorder host, in the background of this process, over the
        // same ClassScribe folder, with the dummy key as the key in its environment; its exit code ends the pretend process.
        _starts.OnStart = launch =>
        {
            if (launch.Kind != FakeLaunchKind.Recorder) return;

            var recorder = Task.Run(() => Recorder().RunAsync(launch.RunId, Key));
            _recorders.Add(recorder);
            _ = recorder.ContinueWith(ended => launch.Exit(ended.IsCompletedSuccessfully ? ended.Result : RecorderHost.FailedExitCode), TaskScheduler.Default);
        };
    }

    /// <summary>Waits for every recorder to end, closes the connections and deletes the folders; a file left open fails the deletion, which is the point.</summary>
    [TearDown]
    public async Task TearDown()
    {
        foreach (var recorder in _recorders) await recorder.WaitAsync(EndPatience);
        foreach (var connection in _connections) connection.Dispose();
        _runtime.Dispose();
        _recordings.Dispose();
        _settingsFolder.Dispose();
    }

    /// <summary>
    /// The window, as Program builds it, over settings that send the recordings to the test's folder, uploads to the stub,
    /// and bring no window back by itself; with ClassScribe's own folder in the test's folder, the test's console, window
    /// and desktop, no real device, the recorder started in this process, no job, and the view of runs reaching the
    /// recorder's server over an in-memory connection, with Ctrl+C as the test presses it.
    /// </summary>
    private IHost BuildWindow()
    {
        File.WriteAllText(_settingsFolder.File("appsettings.json"), JsonSerializer.Serialize(new
        {
            Recording = new { OutputFolder = _recordings.Path, ReopenWindow = "Never" },
            ElevenLabs = new { BaseUrl = "http://localhost:9/", KeytermsFile = "", MaxAttempts = 1 }
        }));
        return AppHost.Build(_settingsFolder.Path, Key, services => services
            .AddSingleton(_runtime.Folder)
            .AddSingleton<IConsoleUi>(_ui)
            .AddSingleton<IWindowSignals>(_window)
            .AddSingleton(_desktop.Object)
            .AddSingleton<ILoopbackCaptureProvider>(new FakeCaptureProvider())
            .AddSingleton<IProcessStarter>(_starts)
            .AddSingleton<IJobInspector>(new FakeJobInspector())
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

    /// <summary>
    /// The recorder's host over ClassScribe's folder, with the simulated device in place of Windows audio, stand-ins for
    /// the power, the clocks and Windows' end of the session, the stub in place of ElevenLabs, a preparer that writes a
    /// small upload file, a mocked desktop, a pretend starter for any window it would bring back, and its server kept for
    /// the window's connections.
    /// </summary>
    private RecorderHost Recorder() => new()
    {
        Folder = _runtime.Folder,
        ConfigureServices = services =>
        {
            services.Replace(ServiceDescriptor.Singleton<ILoopbackCaptureProvider>(_devices));
            services.Replace(ServiceDescriptor.Singleton<IProcessStarter>(_recorderStarts));
            services.Replace(ServiceDescriptor.Singleton<IPowerRequests>(new FakePowerRequests()));
            services.Replace(ServiceDescriptor.Singleton<IPowerSource>(new FakePowerSource()));
            services.Replace(ServiceDescriptor.Singleton<ISystemTimes>(new FakeSystemTimes(TimeProvider.System)));
            services.Replace(ServiceDescriptor.Scoped<ISessionEvents>(_ => new FakeSessionEvents()));
            services.Replace(ServiceDescriptor.Singleton(Mocks.AudioPreparer().Object));
            services.Replace(ServiceDescriptor.Singleton(_recorderDesktop.Object));
            services.Replace(ServiceDescriptor.Singleton(provider => _server = new RecorderServer(
                provider.GetRequiredService<RecorderLog>(), provider.GetRequiredService<TimeProvider>(), provider.GetRequiredService<ILogger<RecorderServer>>())));
            services.ConfigureAll<HttpClientFactoryOptions>(options => options.HttpMessageHandlerBuilderActions.Add(builder => builder.PrimaryHandler = _elevenLabs));
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
        _capture.Deliver(TestAudio.Shorts(TestAudio.Tone(16000, seconds: 0.5, frequency: 440, amplitude: 0.5)));
    }

    /// <summary>The one run file, as it is now.</summary>
    private RunRecord TheRun() => _runtime.Journal.List().Single().Record;

    /// <summary>
    /// Menu choice 2, from the key press to the saved transcript: the recorder starts in the background and this window
    /// shows it as the window that started it; Esc leaves the view and the recording goes on, with A offered in the menu;
    /// A shows it again, with the log so far replayed; Q twice stops it; the recording is sent to ElevenLabs with the
    /// dummy key and the transcript saved; the window shown the end opens the folder and the transcript, the recorder
    /// opens nothing; the run file says the run finished, transcribed, and has been seen; and the menu comes back.
    /// </summary>
    [Test]
    public async Task Menu_choice_2_records_in_the_background_from_the_key_press_to_the_saved_transcript()
    {
        _elevenLabs.Enqueue(HttpStatusCode.OK, Answer);
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
        var transcript = Directory.GetFiles(Path.Combine(_recordings.Path, "Kalam"), "*.md").Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(File.ReadAllText(transcript), Does.Contain("Hello world."));
            Assert.That((run.State, run.Outcome?.Kind, run.Outcome?.TranscriptPath, run.Seen, run.Home),
                Is.EqualTo((RunState.Finished, OutcomeKind.Transcribed, transcript, true, RunHome.Separate)));
            Assert.That(run.Request, Is.EqualTo(new RecordRequest(run.RunId, "Kalam", Transcribe: true, new SourcePlan(RecordingMode.Class, new ClassSource.Automatic(), null),
                _recordings.Path, ContinueChoice.New, ContinueBaseName: null, Minutes: null, Open: true)), "the request the menu made, with the starter's decision not to continue");
            Assert.That(File.Exists(_runtime.Journal.RequestFile(run.RunId)), Is.False, "the recorder took its request");
            Assert.That(_elevenLabs.Requests.Select(request => request.ApiKey), Is.EqualTo(new[] { Key }), "one upload, with the dummy key");
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
            Assert.That(Directory.GetFiles(Path.Combine(_recordings.Path, "Kalam"), "*.wav"), Has.Length.EqualTo(1));
            Assert.That(Directory.GetFiles(Path.Combine(_recordings.Path, "Kalam"), "*.md"), Is.Empty, "record-only makes no transcript");
            Assert.That(_elevenLabs.Requests, Is.Empty);
            Assert.That((run.State, run.Outcome?.Kind, run.Seen), Is.EqualTo((RunState.Finished, OutcomeKind.Recorded, true)));
            Assert.That((run.Request.Transcribe, run.Request.Open, run.Request.OutputRoot, run.Request.Source.Class), Is.EqualTo((false, false, _recordings.Path, new ClassSource.Automatic())),
                "the command line's pieces reached the recorder");
            Assert.That(_starts.Recorders.Single().Breakaway, Is.False, "no job to break away from");
        }

        _desktop.VerifyNoOtherCalls();
        _recorderDesktop.VerifyNoOtherCalls();
    }
}
