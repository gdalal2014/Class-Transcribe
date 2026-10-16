using System.Net;
using System.Reflection;
using System.Text.Json;
using ClassScribe.Audio;
using ClassScribe.Commands;
using ClassScribe.Desktop;
using ClassScribe.Settings;
using ClassScribe.Storage;
using ClassScribe.Transcription;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Refit;

namespace ClassScribe.Tests;

/// <summary>
/// Settings read again before each run: appsettings.json is read and checked as a staged copy, and only values that
/// pass reach the app, which then uses them in every part, from the menu's next choice on. Most tests here drive the
/// app as AppHost puts it together, over an appsettings.json in a temporary folder.
/// </summary>
[TestFixture]
public class SettingsReloadTests
{
    private const string Key = "dummy-key-for-a-local-test";

    /// <summary>An answer from ElevenLabs, as the stub gives it.</summary>
    private const string Answer = """{"language_code":"en","language_probability":0.98,"text":"Hello.","words":[{"text":"Hello.","start":0.0,"end":0.5,"type":"word"}]}""";

    private static readonly AudioFormat Pcm16KMono = new(16000, 1, 16, false);

    private TempFolder _folder = null!;
    private TempFolder _data = null!;

    /// <summary>Creates a folder for appsettings.json, as the app's own folder, and one for recordings.</summary>
    [SetUp]
    public void SetUp()
    {
        _folder = new TempFolder();
        _data = new TempFolder();
    }

    /// <summary>Deletes the temporary folders.</summary>
    [TearDown]
    public void TearDown()
    {
        _folder.Dispose();
        _data.Dispose();
    }

    /// <summary>The settings file of the app under test.</summary>
    private string SettingsPath => _folder.File("appsettings.json");

    /// <summary>Writes the app's appsettings.json.</summary>
    private void WriteSettings(string json) => File.WriteAllText(SettingsPath, json);

    /// <summary>Settings as JSON, written the way a person would write them, paths included.</summary>
    private static string Json(object settings) => JsonSerializer.Serialize(settings);

    /// <summary>Writes the settings and builds the app over them, as Program does, with the services changed as a test needs.</summary>
    private IHost BuildApp(string json, Action<IServiceCollection>? services = null)
    {
        WriteSettings(json);
        return AppHost.Build(_folder.Path, Key, services);
    }

    /// <summary>The menu over the app's own settings, folders, choice and gate, with a scripted keyboard and mocked commands and desktop.</summary>
    private static MenuCommand Menu(IHost app, FakeConsoleUi ui, Mock<ICommandRunner> commands, Mock<IDesktop> desktop, RunGate? gate = null) =>
        new(
            commands.Object,
            ui,
            gate ?? app.Services.GetRequiredService<RunGate>(),
            app.Services.GetRequiredService<SettingsFile>(),
            app.Services.GetRequiredService<ClassFolders>(),
            new FakeCaptureProvider(),
            app.Services.GetRequiredService<SourceChoice>(),
            new RecordingNamePrompt(ui),
            desktop.Object,
            TimeProvider.System,
            NullLogger<MenuCommand>.Instance);

    /// <summary>How many times the menu has been shown.</summary>
    private static int MenusShown(FakeConsoleUi ui) => ui.Screen.Split(Environment.NewLine).Count(line => line == "  Q  Quit");

    /// <summary>An edit made between two menu choices is used by the second: here the recordings folder that choice 5 opens.</summary>
    [Test]
    public async Task An_edit_between_two_menu_choices_is_used_by_the_second()
    {
        var first = _data.File("First");
        var second = _data.File("Second");
        using var app = BuildApp(Json(new { Recording = new { OutputFolder = first } }));
        var ui = new FakeConsoleUi();
        var desktop = new Mock<IDesktop>();
        desktop.Setup(d => d.OpenFolder(first)).Callback(() => WriteSettings(Json(new { Recording = new { OutputFolder = second } })));
        ui.Type('5', '5', 'q');

        await Menu(app, ui, new Mock<ICommandRunner>(), desktop).RunAsync().WaitAsync(TimeSpan.FromSeconds(15));

        desktop.Verify(d => d.OpenFolder(first), Times.Once);
        desktop.Verify(d => d.OpenFolder(second), Times.Once);
        Assert.That(ui.Screen, Does.Contain($"Recordings and transcripts: {second}"), "the menu shows the new folder once the choice has read it");
    }

    /// <summary>
    /// appsettings.json that is not valid JSON, at a menu choice, gives the plain message (the line, and how to write a
    /// path), keeps the settings as they were, and brings the menu back without running the choice.
    /// </summary>
    [Test]
    public async Task Malformed_JSON_gives_the_plain_message_keeps_the_old_values_and_brings_the_menu_back()
    {
        var first = _data.File("First");
        using var app = BuildApp(Json(new { Recording = new { OutputFolder = first } }));
        var log = new RecordingLogger<RunGate>();
        var gate = new RunGate(app.Services.GetRequiredService<SettingsReloader>(), app.Services.GetRequiredService<SettingsFile>(), log);
        var ui = new FakeConsoleUi();
        var desktop = new Mock<IDesktop>();
        WriteSettings("""
            {
              "Recording": {
                "OutputFolder": "D:\Classes"
              }
            }
            """);
        ui.Type('5', 'q');

        await Menu(app, ui, new Mock<ICommandRunner>(), desktop, gate).RunAsync().WaitAsync(TimeSpan.FromSeconds(15));

        desktop.VerifyNoOtherCalls();
        Assert.That(log.Problems, Has.Some.StartsWith("The mistake is on line 3:"));
        Assert.That(log.Problems, Has.Some.Matches<string>(line => line.Contains(@"""D:\\Classes""") && line.Contains(@"""D:/Classes""")), "says how to write a path in JSON");
        Assert.That(log.Problems[^1], Is.EqualTo($"Nothing was started. Fix the settings above in {SettingsPath}, then choose again"));
        Assert.That(MenusShown(ui), Is.EqualTo(2), "the menu came back");
        Assert.That(app.Services.GetRequiredService<IOptionsMonitor<RecordingOptions>>().CurrentValue.OutputFolder, Is.EqualTo(first));
        Assert.That(app.Services.GetRequiredService<ClassFolders>().Root, Is.EqualTo(first));
    }

    /// <summary>
    /// A well-formed file with a value that cannot be read, or an enum setting with a name it does not have (the two
    /// cases of the design's probe), is refused before anything of the file reaches the live settings: the other value
    /// edited with it is not taken either. Reading the settings keeps working, through the monitor and in a new scope,
    /// and once the file is fixed it is taken.
    /// </summary>
    [TestCase("FlushSeconds", "abc", "Recording:FlushSeconds is \"abc\", which is not a whole number")]
    [TestCase("KeepScreenOn", "Sometimes", "Recording:KeepScreenOn is \"Sometimes\", which is not OnBattery, Always or Never.")]
    public void A_value_that_cannot_be_read_is_refused_before_the_live_reload(string setting, string value, string problem)
    {
        var first = _data.File("First");
        var second = _data.File("Second");
        using var app = BuildApp(Json(new { Recording = new { OutputFolder = first, FlushSeconds = 5 } }));
        var reloader = app.Services.GetRequiredService<SettingsReloader>();
        var configuration = app.Services.GetRequiredService<IConfiguration>();
        var monitor = app.Services.GetRequiredService<IOptionsMonitor<RecordingOptions>>();
        WriteSettings(Json(new { Recording = new Dictionary<string, object> { ["OutputFolder"] = second, [setting] = value } }));

        var refused = reloader.TryReload();

        Assert.That(refused.Usable, Is.False);
        Assert.That(refused.Problems, Has.Count.EqualTo(1));
        Assert.That(refused.Problems[0], Does.StartWith(problem));
        Assert.That(refused.Path, Is.EqualTo(SettingsPath));
        Assert.That(configuration[$"Recording:{setting}"], Is.Not.EqualTo(value), "the value never reached the live settings");
        Assert.That(configuration["Recording:OutputFolder"], Is.EqualTo(first), "nothing of the refused file was taken");
        Assert.That(monitor.CurrentValue.OutputFolder, Is.EqualTo(first));
        using (var scope = app.Services.CreateScope())
            Assert.That(scope.ServiceProvider.GetRequiredService<IOptionsSnapshot<RecordingOptions>>().Value.FlushSeconds, Is.EqualTo(5));

        WriteSettings(Json(new { Recording = new { OutputFolder = second, FlushSeconds = 7, KeepScreenOn = "always" } }));
        var fixedUp = reloader.TryReload();

        Assert.That(fixedUp.Usable, Is.True);
        Assert.That((monitor.CurrentValue.OutputFolder, monitor.CurrentValue.FlushSeconds, monitor.CurrentValue.KeepScreenOn), Is.EqualTo((second, 7, KeepScreenOn.Always)));
    }

    /// <summary>
    /// A value the check does not know about, which a part of the app then cannot read, makes the live reload fail. The
    /// settings that passed before are put back, the run is refused with the reason, and reading the settings, that
    /// part's included, keeps working.
    /// </summary>
    [Test]
    public void A_value_that_fails_in_the_live_reload_is_taken_back()
    {
        var folder = new TestSettings(_folder.Path, """{ "Extra": { "Count": 5 }, "Recording": { "FlushSeconds": 6 } }""");
        using var services = new ServiceCollection()
            .Configure<ExtraOptions>(folder.Configuration.GetSection("Extra"))
            .Configure<RecordingOptions>(folder.Configuration.GetSection(RecordingOptions.SectionName))
            .BuildServiceProvider();
        var extra = services.GetRequiredService<IOptionsMonitor<ExtraOptions>>();
        var recording = services.GetRequiredService<IOptionsMonitor<RecordingOptions>>();
        Assert.That((extra.CurrentValue.Count, recording.CurrentValue.FlushSeconds), Is.EqualTo((5, 6)));
        folder.Write("""{ "Extra": { "Count": "many" }, "Recording": { "FlushSeconds": 8 } }""");

        var verdict = folder.Reloader.TryReload();

        Assert.That(verdict.Usable, Is.False);
        Assert.That(verdict.Problems, Has.Count.EqualTo(1));
        Assert.That(verdict.Problems[0], Does.Contain("Extra:Count"));
        Assert.That((extra.CurrentValue.Count, recording.CurrentValue.FlushSeconds), Is.EqualTo((5, 6)), "the settings that passed before are back");
        Assert.That(folder.Configuration["Extra:Count"], Is.EqualTo("5"));
    }

    /// <summary>A command run while the file cannot be used exits with 1, and the command does not run.</summary>
    [Test]
    public async Task A_command_with_a_broken_file_exits_1_without_running()
    {
        var commands = new Mock<ICommandRunner>();
        var video = _data.File("lecture.mp4");
        File.WriteAllBytes(video, [1, 2, 3]);
        using var app = BuildApp("{}", services => services.AddSingleton(commands.Object));
        WriteSettings("""{ "Transcript": { "ParagraphPauseSeconds": "1,5" } }""");

        var exitCode = await AppHost.RunAsync(app.Services, ["transcribe", video]);

        Assert.That(exitCode, Is.EqualTo(1));
        commands.VerifyNoOtherCalls();
    }

    /// <summary>A choice made with menu 6 holds for the rest of the session: a reload of the settings does not undo it.</summary>
    [Test]
    public void Menu_6s_choice_survives_a_reload()
    {
        using var app = BuildApp(Json(new { Recording = new { Device = "Speakers" } }));
        var reloader = app.Services.GetRequiredService<SettingsReloader>();
        var choice = app.Services.GetRequiredService<SourceChoice>();
        choice.App = "chrome";
        WriteSettings(Json(new { Recording = new { Device = "Headphones" } }));

        Assert.That(reloader.TryReload().Usable, Is.True);

        Assert.That((choice.App, choice.Device), Is.EqualTo(("chrome", (string?)null)));
        Assert.That(choice.Description, Is.EqualTo("the app \"chrome\""));
    }

    /// <summary>Without a choice from menu 6, what is recorded follows the settings as they are read again.</summary>
    [Test]
    public void Without_a_choice_from_menu_6_the_source_follows_the_settings()
    {
        using var app = BuildApp(Json(new { Recording = new { Device = "Speakers" } }));
        var choice = app.Services.GetRequiredService<SourceChoice>();
        Assert.That(choice.Device, Is.EqualTo("Speakers"));
        WriteSettings(Json(new { Recording = new { App = "msedge" } }));

        app.Services.GetRequiredService<SettingsReloader>().TryReload();

        Assert.That((choice.App, choice.Device), Is.EqualTo(("msedge", (string?)null)));
    }

    /// <summary>
    /// A change reaches both kinds of reader: the monitor that long-lived parts read at each use, and the settings of a
    /// new scope, in which each command runs. A scope made before the change keeps the values it started with.
    /// </summary>
    [Test]
    public void A_monitor_and_a_new_scope_see_a_change()
    {
        using var app = BuildApp(Json(new { Recording = new { FlushSeconds = 5 }, Transcript = new { MaxParagraphCharacters = 500 } }));
        var monitor = app.Services.GetRequiredService<IOptionsMonitor<RecordingOptions>>();
        using var before = app.Services.CreateScope();
        Assert.That(before.ServiceProvider.GetRequiredService<IOptionsSnapshot<RecordingOptions>>().Value.FlushSeconds, Is.EqualTo(5));
        WriteSettings(Json(new { Recording = new { FlushSeconds = 7 }, Transcript = new { MaxParagraphCharacters = 600 } }));

        Assert.That(app.Services.GetRequiredService<SettingsReloader>().TryReload().Usable, Is.True);

        using var after = app.Services.CreateScope();
        Assert.That(monitor.CurrentValue.FlushSeconds, Is.EqualTo(7));
        Assert.That(after.ServiceProvider.GetRequiredService<IOptionsSnapshot<RecordingOptions>>().Value.FlushSeconds, Is.EqualTo(7));
        Assert.That(after.ServiceProvider.GetRequiredService<IOptionsSnapshot<TranscriptOptions>>().Value.MaxParagraphCharacters, Is.EqualTo(600));
        Assert.That(before.ServiceProvider.GetRequiredService<IOptionsSnapshot<RecordingOptions>>().Value.FlushSeconds, Is.EqualTo(5), "a run keeps the settings it started with");
    }

    /// <summary>
    /// An edit made between two transcriptions reaches every part of the second that reads the settings once per run:
    /// the transcribe command, the workflow and the ElevenLabs client each take the settings of the run's own scope.
    /// Both runs go through the app as AppHost puts it together, with the gate between them, as two menu choices would;
    /// a stub answers for ElevenLabs, and a stand-in keeps the laptop awake, so that no real power request is made.
    /// </summary>
    [Test]
    public async Task An_edit_between_two_transcriptions_reaches_the_command_the_workflow_and_the_ElevenLabs_client()
    {
        var audio = _data.File("lecture.wav");
        File.WriteAllBytes(audio, [1, 2, 3]);
        var recordings = _data.File("Recordings");
        var firstGlossary = _data.File("first.txt");
        var secondGlossary = _data.File("second.txt");
        File.WriteAllText(firstGlossary, "kalām");
        File.WriteAllText(secondGlossary, "wājib al-wujūd");
        string Settings(bool edited) => Json(new
        {
            Recording = new { OutputFolder = recordings, OpenFolderWhenDone = false, OpenTranscriptWhenDone = edited, KeepUploadFile = edited },
            ElevenLabs = new { BaseUrl = "http://localhost:5001/", ModelId = edited ? "scribe_v2" : "scribe_v1", KeytermsFile = edited ? secondGlossary : firstGlossary, MaxAttempts = 1 }
        });
        var handler = new StubHttpHandler();
        handler.Enqueue(HttpStatusCode.OK, Answer);
        handler.Enqueue(HttpStatusCode.OK, Answer);
        var desktop = new Mock<IDesktop>();
        var power = new FakePowerRequests();
        using var app = BuildApp(Settings(edited: false), services => services
            .AddSingleton(desktop.Object)
            .AddSingleton(Mocks.AudioPreparer().Object)
            .AddSingleton<IPowerRequests>(power)
            .AddSingleton<IPowerSource>(new FakePowerSource())
            .AddScoped<IUserSignals, FakeUserSignals>()
            .ConfigureAll<HttpClientFactoryOptions>(options => options.HttpMessageHandlerBuilderActions.Add(builder => builder.PrimaryHandler = handler)));
        var gate = app.Services.GetRequiredService<RunGate>();
        var runner = app.Services.GetRequiredService<ICommandRunner>();
        int UploadCopies() => Directory.GetFiles(recordings, "*.upload.wav", SearchOption.AllDirectories).Length;

        Assert.That(gate.TryOpen(GateUse.MenuChoice), Is.True);
        Assert.That(await runner.TranscribeAsync(new TranscribeRequest(audio)), Is.EqualTo(0));
        var copiesAfterTheFirst = UploadCopies();
        WriteSettings(Settings(edited: true));
        Assert.That(gate.TryOpen(GateUse.MenuChoice), Is.True);
        Assert.That(await runner.TranscribeAsync(new TranscribeRequest(audio)), Is.EqualTo(0));

        Assert.That(handler.Requests, Has.Count.EqualTo(2));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(handler.Requests[0].Body, Does.Contain("scribe_v1").And.Contain("kalām"), "the first run, with the settings as they were");
            Assert.That(handler.Requests[1].Body, Does.Contain("scribe_v2").And.Not.Contain("scribe_v1"), "the client took the edited ElevenLabs:ModelId");
            Assert.That(handler.Requests[1].Body, Does.Contain("wājib al-wujūd").And.Not.Contain("kalām"), "the workflow took the edited ElevenLabs:KeytermsFile");
            Assert.That((copiesAfterTheFirst, UploadCopies()), Is.EqualTo((0, 1)), "the workflow took the edited Recording:KeepUploadFile");
            Assert.That(desktop.Invocations.Count(call => call.Method.Name == nameof(IDesktop.OpenFile)), Is.EqualTo(1), "the command took the edited Recording:OpenTranscriptWhenDone");
            Assert.That(power.Holds.Select(hold => hold.Releases), Is.EqualTo(new[] { 1, 1 }), "each run kept the laptop awake through the stand-in, not through Windows, and let it go");
        }
    }

    /// <summary>
    /// An edit made between two recordings reaches the second: the record command and its recording session each take
    /// the settings of the run's own scope. Both runs go through the app as AppHost puts it together, from a simulated
    /// device, with the gate between them, and with stand-ins for keeping the laptop awake and for the computer's
    /// clocks, so that no real power request is made. Each records ten megabytes and a moment more, which a limit of
    /// ten megabytes puts into a second file.
    /// </summary>
    [Test]
    public async Task An_edit_between_two_recordings_reaches_the_record_command_and_its_session()
    {
        var recordings = _data.File("Recordings");
        string Settings(bool edited) => Json(new { Recording = new { OutputFolder = recordings, OpenFolderWhenDone = edited, MaxSegmentMegabytes = edited ? 10 : 4000 } });
        var devices = new FakeCaptureProvider();
        var captures = new Queue<FakeAudioCapture>([devices.Enqueue(new FakeAudioCapture(Pcm16KMono)), devices.Enqueue(new FakeAudioCapture(Pcm16KMono))]);
        var signals = new List<FakeUserSignals>();
        var power = new FakePowerRequests();
        var desktop = new Mock<IDesktop>();
        var exitWarnings = new Mock<IExitWarnings>();
        exitWarnings.Setup(w => w.Register(It.IsAny<Action>())).Returns(Mock.Of<IDisposable>());
        using var app = BuildApp(Settings(edited: false), services => services
            .AddSingleton<ILoopbackCaptureProvider>(devices)
            .AddSingleton<IConsoleUi>(new FakeConsoleUi { IsInteractive = false })
            .AddSingleton(desktop.Object)
            .AddSingleton(exitWarnings.Object)
            .AddSingleton<IPowerRequests>(power)
            .AddSingleton<IPowerSource>(new FakePowerSource())
            .AddSingleton<ISystemTimes>(new FakeSystemTimes(TimeProvider.System))
            .AddScoped<IUserSignals>(_ =>
            {
                var run = new FakeUserSignals();
                lock (signals) signals.Add(run);
                return run;
            }));
        var gate = app.Services.GetRequiredService<RunGate>();
        var runner = app.Services.GetRequiredService<ICommandRunner>();

        // Records under the name given until the audio is in, then stops, and returns the recording's files.
        async Task<string[]> RecordAsync(string name)
        {
            var capture = captures.Dequeue();
            var run = runner.RecordAsync(name, transcribe: false);
            await Wait.UntilAsync(() => capture.Started);
            capture.Deliver(new byte[10 * 1024 * 1024]);
            capture.Deliver(TestAudio.Shorts(1000, -1000));
            lock (signals) signals[^1].RequestStop();
            Assert.That(await run.WaitAsync(TimeSpan.FromSeconds(15)), Is.EqualTo(0));
            return Directory.GetFiles(Path.Combine(recordings, name), "*.wav");
        }

        Assert.That(gate.TryOpen(GateUse.MenuChoice), Is.True);
        var first = await RecordAsync("First");
        WriteSettings(Settings(edited: true));
        Assert.That(gate.TryOpen(GateUse.MenuChoice), Is.True);
        var second = await RecordAsync("Second");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(first, Has.Length.EqualTo(1), "the first run, with the settings as they were");
            Assert.That(second, Has.Length.EqualTo(2), "the session took the edited Recording:MaxSegmentMegabytes");
            Assert.That(desktop.Invocations.Select(call => $"{call.Method.Name} {Path.GetFileName((string)call.Arguments[0])}").ToArray(), Is.EqualTo(new[] { "OpenFolder Second" }), "the command took the edited Recording:OpenFolderWhenDone");
            Assert.That(power.Holds.Select(hold => hold.Releases), Is.EqualTo(new[] { 1, 1 }), "each run kept the laptop awake through the stand-in, not through Windows, and let it go");
        }
    }

    /// <summary>
    /// No part of ClassScribe takes its settings through IOptions, which keeps the values it first read for the rest of
    /// the session, so that an edit would never reach that part. A part that lasts as long as ClassScribe reads
    /// IOptionsMonitor each time it uses the settings; a part of a run takes IOptionsSnapshot, read once in the run's scope.
    /// </summary>
    [Test]
    public void No_part_keeps_the_settings_it_read_first_for_the_whole_session()
    {
        var readOnce = new[] { typeof(RecordingOptions), typeof(ElevenLabsOptions), typeof(TranscriptOptions) }
            .Select(section => typeof(IOptions<>).MakeGenericType(section))
            .ToHashSet();

        var parts = typeof(AppHost).Assembly.GetTypes()
            .SelectMany(type => type.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .SelectMany(constructor => constructor.GetParameters())
                .Where(parameter => readOnce.Contains(parameter.ParameterType))
                .Select(parameter => $"{type.FullName} ({parameter.Name})"));

        Assert.That(parts, Is.Empty);
    }

    /// <summary>
    /// The ElevenLabs client is made with the base address read when it is made, so a changed ElevenLabs:BaseUrl counts
    /// from the next run. Its HttpClient sets no time limit of its own: the client's own limit covers the whole exchange.
    /// ElevenLabs__BaseUrl, which a test run may set to point at a local stub, is put aside meanwhile, since it would
    /// hide the file's address; the requests go to the stub handler either way.
    /// </summary>
    [Test]
    public async Task The_ElevenLabs_client_follows_a_changed_base_address()
    {
        using var baseUrlVariable = new TemporaryVariable("ElevenLabs__BaseUrl", null);
        var handler = new StubHttpHandler();
        using var app = BuildApp(
            Json(new { ElevenLabs = new { BaseUrl = "http://localhost:5001/first" } }),
            services => services.ConfigureAll<HttpClientFactoryOptions>(options => options.HttpMessageHandlerBuilderActions.Add(builder => builder.PrimaryHandler = handler)));
        var clients = app.Services.GetRequiredService<IHttpClientFactory>();
        var before = clients.CreateClient(UniqueName.ForType<IElevenLabsApi>());
        await SendAsync(app, handler);
        WriteSettings(Json(new { ElevenLabs = new { BaseUrl = "http://localhost:5002/second/" } }));

        Assert.That(app.Services.GetRequiredService<SettingsReloader>().TryReload().Usable, Is.True);

        var after = clients.CreateClient(UniqueName.ForType<IElevenLabsApi>());
        await SendAsync(app, handler);
        Assert.That(before.BaseAddress, Is.EqualTo(new Uri("http://localhost:5001/first/")));
        Assert.That(after.BaseAddress, Is.EqualTo(new Uri("http://localhost:5002/second/")));
        Assert.That(after.Timeout, Is.EqualTo(Timeout.InfiniteTimeSpan), "the client's own limit covers the exchange");
        Assert.That(handler.Requests.Select(request => request.Uri!.AbsoluteUri), Is.EqualTo(new[]
        {
            "http://localhost:5001/first/v1/speech-to-text",
            "http://localhost:5002/second/v1/speech-to-text"
        }));
    }

    /// <summary>Sends one request through the app's ElevenLabs client, made in a scope of its own as a run makes it, to the stub.</summary>
    private static async Task SendAsync(IHost app, StubHttpHandler handler)
    {
        handler.Enqueue(System.Net.HttpStatusCode.OK, "{}");
        using var scope = app.Services.CreateScope();
        var api = scope.ServiceProvider.GetRequiredService<IElevenLabsApi>();
        using var response = await api.TranscribeAsync(Key, "scribe_v2", "false", "word", null, null, [], new StreamPart(new MemoryStream([1, 2, 3]), "audio.wav"), CancellationToken.None);
    }

    /// <summary>
    /// The snapshot a separate recorder will get holds the Recording, ElevenLabs, Transcript and Logging sections, with
    /// an override from an environment variable in place, and never the key: not under ElevenLabs:ApiKey, not as
    /// ELEVENLABS_API_KEY, which the host maps into the settings like every environment variable, and not inside another
    /// value, such as an override that carries it by mistake, which is left out whole. The variables get back the
    /// values they had, so a stub address set for the whole test run still holds afterwards.
    /// </summary>
    [Test]
    public void The_snapshot_holds_the_four_sections_and_never_the_key()
    {
        using var keyVariable = new TemporaryVariable(ElevenLabsOptions.ApiKeyVariable, Key);
        using var baseUrlVariable = new TemporaryVariable("ElevenLabs__BaseUrl", "http://localhost:5003/");
        using var deviceVariable = new TemporaryVariable("Recording__Device", $"{Key} speakers");
        using var app = BuildApp(Json(new
        {
            Recording = new { FlushSeconds = 6 },
            ElevenLabs = new { ModelId = "scribe_v2" },
            Transcript = new { IncludeTimestamps = false },
            Logging = new { LogLevel = new { Default = "Warning" } },
            Other = new { Value = 1 }
        }));
        Assert.That(app.Services.GetRequiredService<IConfiguration>()[ElevenLabsOptions.ApiKeyVariable], Is.EqualTo(Key), "the key is in the settings, so the snapshot must leave it out");

        var snapshot = app.Services.GetRequiredService<SettingsReloader>().Snapshot();

        Assert.That(snapshot, Does.ContainKey("Recording:FlushSeconds").WithValue("6"));
        Assert.That(snapshot, Does.ContainKey("ElevenLabs:ModelId").WithValue("scribe_v2"));
        Assert.That(snapshot, Does.ContainKey("ElevenLabs:BaseUrl").WithValue("http://localhost:5003/"), "an override from the environment is part of the snapshot");
        Assert.That(snapshot["Transcript:IncludeTimestamps"], Is.EqualTo("false").IgnoreCase);
        Assert.That(snapshot, Does.ContainKey("Logging:LogLevel:Default").WithValue("Warning"));
        Assert.That(snapshot.Keys, Has.All.Matches<string>(key => new[] { "Recording:", "ElevenLabs:", "Transcript:", "Logging:" }.Any(section => key.StartsWith(section, StringComparison.OrdinalIgnoreCase))));
        Assert.That(snapshot.Keys, Has.None.EqualTo(ElevenLabsOptions.ApiKeySetting).IgnoreCase);
        Assert.That(snapshot.Keys, Has.None.EqualTo("Recording:Device").IgnoreCase, "the value that holds the key is left out whole");
        Assert.That(snapshot.Values, Has.None.Matches<string?>(value => value is not null && value.Contains(Key, StringComparison.Ordinal)));
    }

    /// <summary>
    /// A file that is JSON but not a settings file, such as one with a setting written twice, or one that is empty, is
    /// refused with the reason in plain words, and the settings stay as they were.
    /// </summary>
    [TestCase("""{ "Recording": { "Device": "Speakers", "device": "Headphones" } }""", "appsettings.json cannot be read: A duplicate key")]
    [TestCase("""[ "Recording" ]""", "appsettings.json cannot be read: Top-level JSON element must be an object")]
    [TestCase("", "appsettings.json is empty.")]
    [TestCase(" \r\n ", "appsettings.json is empty.")]
    [TestCase("﻿", "appsettings.json is empty.")]
    public void A_file_that_is_not_a_settings_file_is_refused_with_the_reason(string json, string reason)
    {
        var folder = new TestSettings(_folder.Path, """{ "Recording": { "FlushSeconds": 9 } }""");
        folder.Write(json);

        var verdict = folder.Reloader.TryReload();

        Assert.That(verdict.Usable, Is.False);
        Assert.That(verdict.Problems, Has.Count.EqualTo(1));
        Assert.That(verdict.Problems[0], Does.StartWith(reason));
        Assert.That(folder.Configuration["Recording:FlushSeconds"], Is.EqualTo("9"));
    }

    /// <summary>Without an appsettings.json the defaults apply, as they always have, and the settings count as usable.</summary>
    [Test]
    public void Without_a_settings_file_the_defaults_apply()
    {
        using var app = AppHost.Build(_folder.Path, Key);

        var verdict = app.Services.GetRequiredService<SettingsReloader>().TryReload();

        Assert.That(verdict.Usable, Is.True);
        Assert.That(app.Services.GetRequiredService<IOptionsMonitor<RecordingOptions>>().CurrentValue.FlushSeconds, Is.EqualTo(5));
    }

    /// <summary>
    /// A file deleted or renamed after its settings were taken does not quietly bring back the defaults, which would send
    /// the next class to another folder or device: the settings stay as they were, the run is refused with the reason, and
    /// once the file is back it is read as usual.
    /// </summary>
    [Test]
    public void A_file_gone_after_it_was_read_keeps_the_settings_and_refuses_the_run()
    {
        var folder = new TestSettings(_folder.Path, """{ "Recording": { "FlushSeconds": 9 } }""", pause: _ => { });
        var log = new RecordingLogger<RunGate>();
        File.Delete(SettingsPath);

        var opened = folder.Gate(log).TryOpen(GateUse.MenuChoice);

        Assert.That(opened, Is.False);
        Assert.That(log.Problems[0], Is.EqualTo(
            $"{SettingsPath} is no longer there, so the settings stay as they were when it was last read. Put the file back, or start ClassScribe again to go back to the defaults."));
        Assert.That(log.Problems[^1], Is.EqualTo($"Nothing was started. Fix the settings above in {SettingsPath}, then choose again"));
        Assert.That(folder.Configuration["Recording:FlushSeconds"], Is.EqualTo("9"));

        folder.Write("""{ "Recording": { "FlushSeconds": 7 } }""");
        Assert.That(folder.Gate(log).TryOpen(GateUse.MenuChoice), Is.True, "the file is back");
        Assert.That(folder.Configuration["Recording:FlushSeconds"], Is.EqualTo("7"));
    }

    /// <summary>
    /// An editor that saves by deleting the file and renaming a new one into its place leaves it missing for a moment. A
    /// reading that comes then waits for the file, as for one another program holds, and takes the new one.
    /// </summary>
    [Test]
    public void A_file_replaced_by_deleting_it_is_read_once_it_is_back()
    {
        string? replacement = null;
        var folder = new TestSettings(_folder.Path, """{ "Recording": { "FlushSeconds": 9 } }""", pause: _ =>
        {
            if (replacement is null) return;
            File.WriteAllText(SettingsPath, replacement);
            replacement = null;
        });
        File.Delete(SettingsPath);
        replacement = """{ "Recording": { "FlushSeconds": 7 } }""";

        var verdict = folder.Reloader.TryReload();

        Assert.That(verdict.Usable, Is.True);
        Assert.That(folder.Configuration["Recording:FlushSeconds"], Is.EqualTo("7"));
    }

    /// <summary>
    /// A missing file is waited for only when it was there before: a read that does not expect it answers at once, and
    /// one that does tries three times, then gives up.
    /// </summary>
    [Test]
    public void A_missing_file_is_waited_for_only_when_it_was_there_before()
    {
        var pauses = new List<TimeSpan>();
        var file = new SettingsFile(_folder.Path, NullLogger<SettingsFile>.Instance, pauses.Add);

        var unexpected = file.Read();
        var waitedAt = pauses.Count;
        var expected = file.Read(expectFile: true);

        Assert.That((unexpected.Bytes, waitedAt), Is.EqualTo(((byte[]?)null, 0)), "not waited for");
        Assert.That(expected.Bytes, Is.Null);
        Assert.That(pauses, Is.EqualTo(Enumerable.Repeat(TimeSpan.FromMilliseconds(200), 3)), "three tries, 200 ms apart");
    }

    /// <summary>
    /// A file that another program holds for a moment, as OneDrive or an editor saving does, is read once it is let go: the
    /// read waits and tries again. Here the other program lets go while the read waits for the first time.
    /// </summary>
    [Test]
    public void A_file_held_for_a_moment_is_read_once_it_is_let_go()
    {
        WriteSettings("""{ "Recording": { "FlushSeconds": 9 } }""");
        var written = File.ReadAllBytes(SettingsPath);
        using var held = new FileStream(SettingsPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var pauses = 0;
        var file = new SettingsFile(_folder.Path, NullLogger<SettingsFile>.Instance, _ =>
        {
            pauses++;
            held.Dispose();
        });

        var text = file.Read();

        Assert.That(pauses, Is.EqualTo(1), "the read waited once, while the file was held");
        Assert.That(text.Bytes, Is.EqualTo(written));
    }

    /// <summary>A file held for longer than three tries, 200 ms apart, is reported as unreadable: the run is refused and the settings stay as they were.</summary>
    [Test]
    public void A_file_held_for_longer_is_reported_and_the_run_refused()
    {
        var folder = new TestSettings(_folder.Path, """{ "Recording": { "FlushSeconds": 9 } }""");
        var log = new RecordingLogger<RunGate>();

        bool opened;
        using (new FileStream(SettingsPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            opened = folder.Gate(log).TryOpen(GateUse.MenuChoice);

        Assert.That(opened, Is.False);
        Assert.That(log.Problems[0], Does.StartWith($"{SettingsPath} could not be read"));
        Assert.That(folder.Configuration["Recording:FlushSeconds"], Is.EqualTo("9"));
    }

    /// <summary>
    /// A file another program keeps open while letting others at it, as an editor or OneDrive may, is read at the first
    /// try: ClassScribe shares the file for reading, writing and deleting, so neither a program that has it open for
    /// writing nor one that has it open to delete or replace it keeps ClassScribe waiting.
    /// </summary>
    [TestCase(FileShare.ReadWrite, FileOptions.None, Description = "open for writing")]
    [TestCase(FileShare.ReadWrite | FileShare.Delete, FileOptions.DeleteOnClose, Description = "open to be deleted")]
    public void A_file_another_program_has_open_but_shares_is_read_at_once(FileShare share, FileOptions options)
    {
        WriteSettings("""{ "Recording": { "FlushSeconds": 9 } }""");
        var written = File.ReadAllBytes(SettingsPath);
        var log = new RecordingLogger<SettingsFile>();
        var file = new SettingsFile(_folder.Path, log);

        SettingsText text;
        using (new FileStream(SettingsPath, FileMode.Open, FileAccess.ReadWrite, share, 4096, options))
            text = file.Read();

        Assert.That(text.Bytes, Is.EqualTo(written));
        Assert.That(log.Messages, Is.Empty, "read at the first try, without waiting for the file to be let go");
    }

    /// <summary>
    /// The gate reports each problem as an error, then what to do: at a menu choice, to choose again; for a command,
    /// to run it again. It names the file it read.
    /// </summary>
    [TestCase(GateUse.MenuChoice, "then choose again")]
    [TestCase(GateUse.Command, "then run the command again")]
    public void The_gate_reports_each_problem_then_what_to_do(GateUse use, string ending)
    {
        var folder = new TestSettings(_folder.Path);
        var log = new RecordingLogger<RunGate>();
        folder.Write("""{ "Recording": { "FlushSeconds": 0 }, "ElevenLabs": { "MaxAttempts": 0 } }""");

        var opened = folder.Gate(log).TryOpen(use);

        Assert.That(opened, Is.False);
        Assert.That(log.Problems, Has.Count.EqualTo(3));
        Assert.That(log.Problems[0], Does.StartWith("Recording:FlushSeconds"));
        Assert.That(log.Problems[1], Does.StartWith("ElevenLabs:MaxAttempts"));
        Assert.That(log.Problems[2], Is.EqualTo($"Nothing was started. Fix the settings above in {SettingsPath}, {ending}"));
    }

    /// <summary>Settings that can be used open the gate without a word.</summary>
    [Test]
    public void Usable_settings_open_the_gate_quietly()
    {
        var folder = new TestSettings(_folder.Path, """{ "Recording": { "FlushSeconds": 3 } }""");
        var log = new RecordingLogger<RunGate>();

        Assert.That(folder.Gate(log).TryOpen(GateUse.Command), Is.True);
        Assert.That(log.Messages, Is.Empty);
        Assert.That(folder.Configuration["Recording:FlushSeconds"], Is.EqualTo("3"));
    }

    /// <summary>
    /// A key written into appsettings.json is not used. The warning that says so comes once per process, however often
    /// the settings are read, and never shows a key.
    /// </summary>
    [Test]
    public void The_key_warning_is_given_once_and_never_shows_a_key()
    {
        var folder = new TestSettings(_folder.Path, """{ "ElevenLabs": { "ApiKey": "key-in-file" } }""");
        var log = new RecordingLogger<RunGate>();
        var gate = folder.Gate(log);

        Assert.That(gate.TryOpen(GateUse.MenuChoice), Is.True, "only a warning: the key from the variable is used");
        Assert.That(gate.TryOpen(GateUse.MenuChoice), Is.True);

        Assert.That(log.Problems, Has.Count.EqualTo(1));
        Assert.That(log.Problems[0], Does.Contain(ElevenLabsOptions.ApiKeySetting).And.Contain("Remove it").And.Contain(ElevenLabsOptions.ApiKeyVariable));
        Assert.That(log.Problems[0], Does.Not.Contain("key-in-file"));
    }

    /// <summary>Makes a development layout, as dotnet build leaves it: the project's appsettings.json, and its copy in bin\Debug\net10.0. Returns the copy's folder.</summary>
    private string DevelopmentLayout(string projectJson, string copyJson, bool projectNewer = true, bool withProjectFile = true)
    {
        var project = Directory.CreateDirectory(_folder.File(Path.Combine("repo", "src", "ClassScribe"))).FullName;
        var output = Directory.CreateDirectory(Path.Combine(project, "bin", "Debug", "net10.0")).FullName;
        if (withProjectFile) File.WriteAllText(Path.Combine(project, "ClassScribe.csproj"), "<Project />");
        File.WriteAllText(Path.Combine(project, "appsettings.json"), projectJson);
        File.WriteAllText(Path.Combine(output, "appsettings.json"), copyJson);
        var earlier = DateTime.UtcNow.AddMinutes(-10);
        File.SetLastWriteTimeUtc(Path.Combine(projectNewer ? output : project, "appsettings.json"), earlier);
        return output;
    }

    /// <summary>
    /// Under dotnet run the app reads the copy of appsettings.json in bin\Debug\net10.0, which a build brings up to date.
    /// When the project's own file is newer and differs, the copy is out of date, and that is said in words.
    /// </summary>
    [Test]
    public void A_project_file_newer_than_its_copy_is_pointed_out()
    {
        var output = DevelopmentLayout("""{ "Recording": { "FlushSeconds": 9 } }""", "{}");

        var stale = new SettingsFile(output, NullLogger<SettingsFile>.Instance).DescribeStaleCopy();

        Assert.That(stale, Is.EqualTo(
            @"src\ClassScribe\appsettings.json has changed since the last build; ClassScribe reads the copy in bin\Debug\net10.0. Build again, or edit the copy, for the change to count"));
    }

    /// <summary>No warning when the copy is up to date, when only the copy changed, or outside the development layout, as for the published app.</summary>
    [TestCase(true, true, true, Description = "same content, saved later")]
    [TestCase(false, false, true, Description = "the copy was edited")]
    [TestCase(false, true, false, Description = "no project next to it")]
    public void A_copy_that_is_current_or_published_brings_no_warning(bool sameContent, bool projectNewer, bool withProjectFile)
    {
        var output = DevelopmentLayout("""{ "Recording": { "FlushSeconds": 9 } }""", sameContent ? """{ "Recording": { "FlushSeconds": 9 } }""" : "{}", projectNewer, withProjectFile);

        Assert.That(new SettingsFile(output, NullLogger<SettingsFile>.Instance).DescribeStaleCopy(), Is.Null);
    }

    /// <summary>The menu names the settings file it reads, and warns right under it when the copy dotnet run reads is out of date.</summary>
    [Test]
    public async Task The_menu_names_the_settings_file_and_warns_of_a_stale_copy()
    {
        var output = DevelopmentLayout("""{ "Recording": { "FlushSeconds": 9 } }""", "{}");
        using var app = AppHost.Build(output, Key);
        var ui = new FakeConsoleUi();
        ui.Type('q');

        await Menu(app, ui, new Mock<ICommandRunner>(), new Mock<IDesktop>()).RunAsync().WaitAsync(TimeSpan.FromSeconds(15));

        var lines = ui.Screen.Split(Environment.NewLine);
        var settingsLine = Array.IndexOf(lines, $"Settings: {Path.Combine(output, "appsettings.json")}");
        Assert.That(settingsLine, Is.GreaterThanOrEqualTo(0), "the menu names the file it reads");
        Assert.That(lines[settingsLine + 1], Does.StartWith(@"WARNING  src\ClassScribe\appsettings.json has changed since the last build"));
    }

    /// <summary>The lines of the last menu on <paramref name="screen"/>, from its title to Q, and the lines below it, shown once a key was pressed there.</summary>
    private static (string[] Menu, string[] Below) LastMenu(string screen)
    {
        var lines = screen.Split(Environment.NewLine);
        var end = Array.LastIndexOf(lines, "  Q  Quit");
        var start = Array.LastIndexOf(lines, "ClassScribe", end);
        return (lines[start..(end + 1)], lines[(end + 1)..]);
    }

    /// <summary>
    /// Under dotnet run, an edit to the project's own appsettings.json made while the menu is shown does not count until
    /// the next build. That is said as soon as a choice is picked, before it runs, not only in the menu shown after it:
    /// for choice 2, that is before a whole class is recorded with the settings as they were. It is said again after a
    /// later edit, though it reads the same, but not twice when the menu's own header has just said it. The menu says it
    /// on the screen; the gate before each choice does not log it as well, as it does for a command.
    /// </summary>
    [Test]
    public async Task An_edit_to_the_project_file_while_the_menu_is_shown_is_pointed_out_before_the_choice_runs()
    {
        const string warning = @"WARNING  src\ClassScribe\appsettings.json has changed since the last build; ClassScribe reads the copy in bin\Debug\net10.0. Build again, or edit the copy, for the change to count.";
        var recordings = _data.File("Recordings");
        var settings = Json(new { Recording = new { OutputFolder = recordings } });
        var output = DevelopmentLayout(settings, settings);
        var copy = Path.Combine(output, "appsettings.json");
        var project = Path.GetFullPath(Path.Combine(output, "..", "..", "..", "appsettings.json"));
        using var app = AppHost.Build(output, Key);
        var gateLog = new RecordingLogger<RunGate>();
        var gate = new RunGate(app.Services.GetRequiredService<SettingsReloader>(), app.Services.GetRequiredService<SettingsFile>(), gateLog);
        var ui = new FakeConsoleUi();
        var desktop = new Mock<IDesktop>();
        var screens = new List<string>();
        desktop.Setup(d => d.OpenFolder(It.IsAny<string>())).Callback(() =>
        {
            screens.Add(ui.Screen);

            // After the first choice, a build brings the copy up to date, so the menu shown next has nothing to warn about.
            if (screens.Count == 1) File.Copy(project, copy, overwrite: true);
        });

        // An edit in VS Code: the project's own file, newer than the copy and different from it.
        void Edit(string playing)
        {
            File.WriteAllText(project, Json(new { Recording = new { OutputFolder = recordings, App = playing } }));
            File.SetLastWriteTimeUtc(project, File.GetLastWriteTimeUtc(copy).AddMinutes(1));
        }

        var menu = Menu(app, ui, new Mock<ICommandRunner>(), desktop, gate).RunAsync().WaitAsync(TimeSpan.FromSeconds(15));
        await Wait.UntilAsync(() => MenusShown(ui) == 1);
        Edit("chrome");
        ui.Type('5');
        await Wait.UntilAsync(() => MenusShown(ui) == 2);
        Edit("msedge");
        ui.Type('5');
        await Wait.UntilAsync(() => MenusShown(ui) == 3);
        ui.Type('5', 'q');
        await menu;

        Assert.That(screens, Has.Count.EqualTo(3));
        var (firstMenu, belowTheFirst) = LastMenu(screens[0]);
        var (secondMenu, belowTheSecond) = LastMenu(screens[1]);
        var (thirdMenu, belowTheThird) = LastMenu(screens[2]);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(firstMenu, Has.No.Member(warning), "the copy was current when the menu was shown");
            Assert.That(belowTheFirst, Has.Member(warning), "the edit made while the menu was shown, before the choice ran");
            Assert.That(secondMenu, Has.No.Member(warning), "the build made the copy current again");
            Assert.That(belowTheSecond, Has.Member(warning), "a later edit, though the warning reads the same");
            Assert.That(thirdMenu, Has.Member(warning), "the menu's header says it");
            Assert.That(belowTheThird, Has.No.Member(warning), "and it is not said twice");
            Assert.That(gateLog.Messages, Has.None.StartsWith(@"src\ClassScribe\appsettings.json has changed since the last build"), "the gate leaves it to the menu");
        }
    }

    /// <summary>A command started from the development layout with an out-of-date copy warns about it once, and runs.</summary>
    [Test]
    public void A_command_warns_once_of_a_stale_copy_and_runs()
    {
        var output = DevelopmentLayout("""{ "Recording": { "FlushSeconds": 9 } }""", "{}");
        var folder = new TestSettings(output, "{}");
        File.SetLastWriteTimeUtc(folder.File.Path, DateTime.UtcNow.AddMinutes(-10));
        var log = new RecordingLogger<RunGate>();
        var gate = folder.Gate(log);

        Assert.That(gate.TryOpen(GateUse.Command), Is.True);
        Assert.That(gate.TryOpen(GateUse.Command), Is.True);

        Assert.That(log.Problems, Has.Count.EqualTo(1));
        Assert.That(log.Problems[0], Does.StartWith(@"src\ClassScribe\appsettings.json has changed since the last build"));
    }

    /// <summary>A setting of a section the check does not know, for the test of a live reload that fails.</summary>
    public sealed class ExtraOptions
    {
        /// <summary>A number, as many settings are.</summary>
        public int Count { get; init; }
    }
}
