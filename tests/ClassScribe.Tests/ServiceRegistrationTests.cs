using System.Runtime.Versioning;
using System.Text.Json.Nodes;
using ClassScribe.Audio;
using ClassScribe.Audio.Microphones;
using ClassScribe.Audio.Windows;
using ClassScribe.Commands;
using ClassScribe.Desktop;
using ClassScribe.Desktop.Windows;
using ClassScribe.Drive;
using ClassScribe.Drive.Windows;
using ClassScribe.Link;
using ClassScribe.Recorder;
using ClassScribe.Settings;
using ClassScribe.Storage;
using ClassScribe.Transcription;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Refit;

namespace ClassScribe.Tests;

/// <summary>
/// Dependency injection wiring and settings binding, through the app as Program puts it together: the window's role, with
/// every command and what starts and shows a run in the background, and the recorder's role, through the recorder's own
/// host. ClassScribe's own folder is a temporary one, so the real %LOCALAPPDATA%\ClassScribe, where a class may be
/// recording, is never touched.
/// </summary>
[TestFixture]
public class ServiceRegistrationTests
{
    private TempFolder _folder = null!;
    private TestRuntime _runtime = null!;

    /// <summary>Creates the folder that holds the app's appsettings.json, and ClassScribe's own folder for the run files.</summary>
    [SetUp]
    public void SetUp()
    {
        _folder = new TempFolder();
        _runtime = new TestRuntime();
    }

    /// <summary>Deletes the folders.</summary>
    [TearDown]
    public void TearDown()
    {
        _runtime.Dispose();
        _folder.Dispose();
    }

    /// <summary>
    /// Writes the settings, given as setting names and values such as Recording:FlushSeconds, into an appsettings.json
    /// and builds the app over it, the way Program builds it, with ClassScribe's own folder and its roaming folder in the
    /// test's temporary folders, and then whatever <paramref name="replace"/> puts in place. The host validates scopes, so a
    /// per-run service taken from the root fails here.
    /// </summary>
    private IHost BuildApp(Dictionary<string, string?> settings, string? apiKey = "test-key", Action<IServiceCollection>? replace = null)
    {
        var json = new JsonObject();
        foreach (var (name, value) in settings)
        {
            var parts = name.Split(':');
            var section = json;
            foreach (var part in parts[..^1])
                section = (JsonObject)(section[part] ??= new JsonObject());
            section[parts[^1]] = value;
        }

        File.WriteAllText(_folder.File("appsettings.json"), json.ToJsonString());
        return AppHost.Build(Places(), apiKey, services =>
        {
            services.AddSingleton(_runtime.Folder).AddSingleton(new RoamingFolder(_folder.File("Roaming")));
            replace?.Invoke(services);
        });
    }

    /// <summary>Where the app under test reads its settings and its glossary: the test's folder as the program's, and no user folder.</summary>
    private SettingsPlaces Places() => new(_folder.Path, userFolder: null);

    /// <summary>The app's capture provider, as registered: the one made only when it is first used.</summary>
    private static LazyLoopbackCaptureProvider CaptureProvider(IServiceProvider provider) =>
        (LazyLoopbackCaptureProvider)provider.GetRequiredService<ILoopbackCaptureProvider>();

    /// <summary>The app's microphone provider, as registered: the one made only when it is first used.</summary>
    private static LazyMicrophoneProvider MicrophoneProvider(IServiceProvider provider) =>
        (LazyMicrophoneProvider)provider.GetRequiredService<IMicrophoneProvider>();

    /// <summary>
    /// Puts stand-ins in place of everything a recording reaches Windows through, so that a run's recording can be
    /// made through the real factory without a device, a microphone or the registry: the devices, the microphones, the
    /// apps' microphone sessions, Windows' usage record, the process names, ClassScribe's own processes, the registry,
    /// the power, the clocks and Windows' end of the session.
    /// </summary>
    private static void ReplaceWindowsWithFakes(IServiceCollection services, TimeProvider time)
    {
        services.Replace(ServiceDescriptor.Singleton<ILoopbackCaptureProvider>(new FakeCaptureProvider()));
        services.Replace(ServiceDescriptor.Singleton<IMicrophoneProvider>(new FakeMicrophoneProvider().Connect(LaptopMicrophones.Array)));
        services.Replace(ServiceDescriptor.Singleton<IMicrophoneSessions>(new FakeMicrophoneSessions()));
        services.Replace(ServiceDescriptor.Singleton<IMicrophoneUsageRecord>(new FakeMicrophoneUsageRecord()));
        services.Replace(ServiceDescriptor.Singleton<IProcessNames>(new FakeProcessNames()));
        services.Replace(ServiceDescriptor.Singleton<IOwnProcesses>(new FakeOwnProcesses(Environment.ProcessId)));
        services.Replace(ServiceDescriptor.Singleton<IRegistryValues>(new FakeRegistryValues()));
        services.Replace(ServiceDescriptor.Singleton<IPowerRequests>(new FakePowerRequests()));
        services.Replace(ServiceDescriptor.Singleton<IPowerSource>(new FakePowerSource()));
        services.Replace(ServiceDescriptor.Singleton<ISystemTimes>(new FakeSystemTimes(time)));
        services.Replace(ServiceDescriptor.Scoped<ISessionEvents>(_ => new FakeSessionEvents()));
    }

    /// <summary>
    /// Every setting section binds, and settings that are not given keep their defaults. (The API key is
    /// bound the same way, but only ever from ELEVENLABS_API_KEY: see <see cref="AppHostTests"/>.)
    /// </summary>
    [Test]
    public void Settings_are_bound_from_configuration()
    {
        using var app = BuildApp(new()
        {
            ["Recording:FlushSeconds"] = "9",
            ["Recording:OutputFolder"] = "D:\\Classes",
            ["Recording:OpenTranscriptWhenDone"] = "false",
            ["Recording:Device"] = "Pixel Buds",
            ["Recording:KeepScreenOn"] = "Never",
            ["Recording:SeparateProcess"] = "false",
            ["Recording:ReopenWindow"] = "OnProblems",
            ["Recording:BlockShutdown"] = "false",
            ["Recording:ContinueWithinMinutes"] = "30",
            ["Recording:StopAfterHours"] = "4.5",
            ["ElevenLabs:TimeoutMinutes"] = "90",
            ["ElevenLabs:SlowestUploadMbps"] = "2.5",
            ["Transcript:IncludeTimestamps"] = "false"
        });
        var provider = app.Services;

        var recording = provider.GetRequiredService<IOptionsMonitor<RecordingOptions>>().CurrentValue;
        var elevenLabs = provider.GetRequiredService<IOptionsMonitor<ElevenLabsOptions>>().CurrentValue;
        var transcript = provider.GetRequiredService<IOptionsMonitor<TranscriptOptions>>().CurrentValue;

        Assert.That(recording.FlushSeconds, Is.EqualTo(9));
        Assert.That(recording.OutputFolder, Is.EqualTo("D:\\Classes"));
        Assert.That(recording.OpenTranscriptWhenDone, Is.False);
        Assert.That(recording.Device, Is.EqualTo("Pixel Buds"));
        Assert.That(recording.KeepScreenOn, Is.EqualTo(KeepScreenOn.Never));
        Assert.That(recording.SeparateProcess, Is.False);
        Assert.That(recording.ReopenWindow, Is.EqualTo(ReopenWindow.OnProblems));
        Assert.That(recording.BlockShutdown, Is.False);
        Assert.That(recording.ContinueWithinMinutes, Is.EqualTo(30));
        Assert.That(recording.StopAfterHours, Is.EqualTo(4.5));
        Assert.That(provider.GetRequiredService<SourceChoice>().Device, Is.EqualTo("Pixel Buds"), "the choice starts from the setting");
        Assert.That(provider.GetRequiredService<ClassFolders>().Root, Is.EqualTo("D:\\Classes"));
        Assert.That(recording.SilenceWarningSeconds, Is.EqualTo(60), "defaults survive for settings that are not given");
        Assert.That(recording.OpenFolderWhenDone, Is.True);
        Assert.That(elevenLabs.TimeoutMinutes, Is.EqualTo(90));
        Assert.That(elevenLabs.SlowestUploadMbps, Is.EqualTo(2.5));
        Assert.That(elevenLabs.ModelId, Is.EqualTo("scribe_v2"));
        Assert.That(transcript.IncludeTimestamps, Is.False);
    }

    /// <summary>
    /// Settings added since the first release have their defaults when the file leaves them out: the screen on battery
    /// only, and 1 Mbps; and for the recorder in the background, a separate process, the window reopened when it is closed
    /// and when something needs attention, a restart that asks first, continuing within 60 minutes, and the safety stop
    /// after 9 hours.
    /// </summary>
    [Test]
    public void The_new_settings_have_their_defaults()
    {
        using var app = BuildApp([]);
        var recording = app.Services.GetRequiredService<IOptionsMonitor<RecordingOptions>>().CurrentValue;

        Assert.That(recording.KeepScreenOn, Is.EqualTo(KeepScreenOn.OnBattery));
        Assert.That(app.Services.GetRequiredService<IOptionsMonitor<ElevenLabsOptions>>().CurrentValue.SlowestUploadMbps, Is.EqualTo(1));
        Assert.That(
            (recording.SeparateProcess, recording.ReopenWindow, recording.BlockShutdown, recording.ContinueWithinMinutes, recording.StopAfterHours),
            Is.EqualTo((true, ReopenWindow.OnCloseAndProblems, true, 60, 9.0)));
    }

    /// <summary>The app, the menu, the gate and the transcribe command resolve without an audio device; commands resolve inside a run's scope.</summary>
    [Test]
    public void The_app_and_the_transcribe_command_resolve_without_an_audio_device()
    {
        using var app = BuildApp([]);
        var provider = app.Services;
        using var scope = provider.CreateScope();

        Assert.That(provider.GetRequiredService<SettingsCheck>(), Is.Not.Null);
        Assert.That(provider.GetRequiredService<SettingsReloader>(), Is.Not.Null);
        Assert.That(provider.GetRequiredService<SettingsFile>().Path, Is.EqualTo(_folder.File("appsettings.json")));
        Assert.That(provider.GetRequiredService<RunGate>(), Is.SameAs(provider.GetRequiredService<RunGate>()), "one gate, so a warning is given once");
        Assert.That(provider.GetRequiredService<IExitWarnings>(), Is.InstanceOf<ProcessExitWarnings>());
        Assert.That(provider.GetRequiredService<App>(), Is.Not.Null);
        Assert.That(provider.GetRequiredService<MenuCommand>(), Is.Not.Null);
        Assert.That(scope.ServiceProvider.GetRequiredService<TranscribeCommand>(), Is.Not.Null);
        Assert.That(scope.ServiceProvider.GetRequiredService<ITranscriptionClient>(), Is.InstanceOf<ElevenLabsClient>());
        Assert.That(provider.GetRequiredService<IElevenLabsApi>(), Is.Not.Null, "the Refit client is registered");
    }

    /// <summary>
    /// Google Drive's services resolve with the app: the Drive settings bound; the store of the sign-in, which is Windows'
    /// data protection in the roaming folder given, never the real one; one token cache for the process; the browser and the
    /// listener; and in a run's scope the sign-in, the drive command and menu choice 9. The two Refit clients take their
    /// addresses from the Drive settings, with HttpClient's own time limit off. Nothing is read or written by resolving them.
    /// </summary>
    [Test]
    public void The_Google_Drive_services_resolve_with_the_settings_bound()
    {
        using var app = BuildApp(new()
        {
            ["Drive:FolderName"] = "Classes",
            ["Drive:ApiBaseUrl"] = "http://127.0.0.1:5005/api",
            ["Drive:OAuthBaseUrl"] = "http://127.0.0.1:5006"
        });
        var provider = app.Services;
        using var scope = provider.CreateScope();

        Assert.That(provider.GetRequiredService<IOptionsMonitor<DriveOptions>>().CurrentValue.FolderName, Is.EqualTo("Classes"));
        Assert.That(provider.GetRequiredService<IGoogleCredentialStore>().Location, Is.EqualTo(Path.Combine(_folder.File("Roaming"), "google-drive.dat")));
        Assert.That(provider.GetRequiredService<GoogleTokenCache>(), Is.SameAs(provider.GetRequiredService<GoogleTokenCache>()), "one cache for the process");
        Assert.That(provider.GetRequiredService<IBrowser>(), Is.InstanceOf<ShellBrowser>());
        Assert.That(provider.GetRequiredService<ILoopbackReceiver>(), Is.InstanceOf<HttpListenerLoopbackReceiver>());
        Assert.That(scope.ServiceProvider.GetRequiredService<GoogleSignIn>(), Is.Not.Null);
        Assert.That(scope.ServiceProvider.GetRequiredService<DriveCommand>(), Is.Not.Null);
        Assert.That(scope.ServiceProvider.GetRequiredService<DriveMenu>(), Is.Not.Null);
        Assert.That(scope.ServiceProvider.GetRequiredService<DriveFolders>(), Is.Not.Null);

        var clients = provider.GetRequiredService<IHttpClientFactory>();
        var drive = clients.CreateClient(UniqueName.ForType<IDriveApi>());
        var oauth = clients.CreateClient(UniqueName.ForType<IGoogleOAuthApi>());
        Assert.That((drive.BaseAddress, drive.Timeout), Is.EqualTo((new Uri("http://127.0.0.1:5005/api/"), Timeout.InfiniteTimeSpan)));
        Assert.That((oauth.BaseAddress, oauth.Timeout), Is.EqualTo((new Uri("http://127.0.0.1:5006/"), Timeout.InfiniteTimeSpan)));
        Assert.That(Directory.Exists(_folder.File("Roaming")), Is.False, "nothing is made until a sign-in is stored");
    }

    /// <summary>
    /// The uploads resolve with the app: in a run's scope the upload and free-space command, the pass, the upload of one
    /// recording, the resumable upload with its retries and the freeing of room; for the process one manifest store and one
    /// upload lock, in ClassScribe's folder given; and the class folders know of the audio kept on Drive alone, from the
    /// manifests. Nothing is made by resolving them.
    /// </summary>
    [Test]
    public void The_uploads_resolve_with_the_app()
    {
        using var app = BuildApp([]);
        var provider = app.Services;
        using var scope = provider.CreateScope();

        Assert.That(scope.ServiceProvider.GetRequiredService<DriveUploadCommand>(), Is.Not.Null);
        Assert.That(scope.ServiceProvider.GetRequiredService<DriveUploadPass>(), Is.Not.Null);
        Assert.That(scope.ServiceProvider.GetRequiredService<RecordingUpload>(), Is.Not.Null);
        Assert.That(scope.ServiceProvider.GetRequiredService<ResumableUpload>(), Is.Not.Null);
        Assert.That(scope.ServiceProvider.GetRequiredService<LocalAudioFreer>(), Is.Not.Null);
        Assert.That(provider.GetRequiredService<UploadManifests>(), Is.SameAs(provider.GetRequiredService<UploadManifests>()));
        Assert.That(provider.GetRequiredService<UploadManifests>().Folder, Is.EqualTo(Path.Combine(_runtime.Folder.Root, "drive", "recordings")));
        Assert.That(provider.GetRequiredService<DriveUploadLock>().LockFile, Is.EqualTo(Path.Combine(_runtime.Folder.Root, "drive", "upload.lock")));
        Assert.That(provider.GetRequiredService<IAudioOnDrive>(), Is.InstanceOf<DriveCopies>());
        Assert.That(provider.GetRequiredService<ClassFolders>().AudioOnDriveOf(new RecordingFiles(_folder.Path, "Kalam_2026-10-15_23-00-02Z")), Is.Null);
        Assert.That(Directory.Exists(Path.Combine(_runtime.Folder.Root, "drive")), Is.False, "nothing is made by resolving them");
    }

    /// <summary>
    /// The uploads that happen by themselves are one per run, shared by the run and whoever runs it, and the record command,
    /// transcribe and rebuild resolve with them; what the menu and the report at the start read of the queue is one for the
    /// process. Before Drive is set up the header has no line, and nothing is made by resolving them.
    /// </summary>
    [Test]
    public void The_uploads_after_each_class_are_one_per_run()
    {
        using var app = BuildApp([]);
        using var first = app.Services.CreateScope();
        using var second = app.Services.CreateScope();
        var run = first.ServiceProvider;

        Assert.That(run.GetRequiredService<IDriveAutoUpload>(), Is.InstanceOf<DriveAutoUpload>());
        Assert.That(run.GetRequiredService<IDriveAutoUpload>(), Is.SameAs(run.GetRequiredService<IDriveAutoUpload>()), "one per run");
        Assert.That(run.GetRequiredService<IDriveAutoUpload>(), Is.Not.SameAs(second.ServiceProvider.GetRequiredService<IDriveAutoUpload>()), "each run its own");
        Assert.That(run.GetRequiredService<TranscribeCommand>(), Is.Not.Null);
        Assert.That(run.GetRequiredService<RebuildCommand>(), Is.Not.Null);
        Assert.That(app.Services.GetRequiredService<DriveQueueReport>(), Is.SameAs(app.Services.GetRequiredService<DriveQueueReport>()));
        Assert.That(app.Services.GetRequiredService<DriveQueueReport>().HeaderLine(), Is.Null, "not set up");
        Assert.That(Directory.Exists(Path.Combine(_runtime.Folder.Root, "drive")), Is.False, "nothing is made by resolving them");
    }

    /// <summary>Without settings for it, the Drive section has its defaults: the ClassScribe folder, and Google's own addresses.</summary>
    [Test]
    public void The_Drive_section_has_its_defaults()
    {
        using var app = BuildApp([]);
        var clients = app.Services.GetRequiredService<IHttpClientFactory>();

        Assert.That(app.Services.GetRequiredService<IOptionsMonitor<DriveOptions>>().CurrentValue.FolderName, Is.EqualTo("ClassScribe"));
        Assert.That(clients.CreateClient(UniqueName.ForType<IDriveApi>()).BaseAddress, Is.EqualTo(new Uri("https://www.googleapis.com/")));
        Assert.That(clients.CreateClient(UniqueName.ForType<IGoogleOAuthApi>()).BaseAddress, Is.EqualTo(new Uri("https://oauth2.googleapis.com/")));
    }

    /// <summary>
    /// Both roles resolve where the settings and the glossary are read from, as the host was given it: the window's settings
    /// file and glossary loader work from the same places, and the recorder's glossary loader from its own. No test container
    /// points at the real user folder, so no test reads, or could write, the real %APPDATA%\ClassScribe.
    /// </summary>
    [Test]
    public void Both_roles_resolve_the_settings_places_and_none_points_at_the_real_user_folder()
    {
        var real = SettingsPlaces.UserFolderForThisUser;
        using var app = BuildApp([]);
        var runId = TestRuns.Id();
        using var log = new RecorderLog(TimeProvider.System);
        using var recorder = AppHost.BuildRecorder(new RecorderStart(runId, TestRuns.Request(runId), new Dictionary<string, string?>()), "test-key", log, _runtime.Folder, Places());

        var window = app.Services.GetRequiredService<SettingsPlaces>();
        var background = recorder.Services.GetRequiredService<SettingsPlaces>();

        Assert.That(window.ProgramFolder, Is.EqualTo(_folder.Path));
        Assert.That(app.Services.GetRequiredService<SettingsFile>().Places, Is.SameAs(window));
        Assert.That(app.Services.GetRequiredService<SettingsWriter>(), Is.SameAs(app.Services.GetRequiredService<SettingsWriter>()), "one writer for the process");
        using (var scope = app.Services.CreateScope()) Assert.That(scope.ServiceProvider.GetRequiredService<SettingsCommand>(), Is.Not.Null, "the settings command, in a run's scope");
        using (var scope = app.Services.CreateScope()) Assert.That(scope.ServiceProvider.GetRequiredService<ClassCommand>(), Is.Not.Null, "the class command, in a run's scope");
        using (var scope = app.Services.CreateScope()) Assert.That(scope.ServiceProvider.GetRequiredService<ClassesMenu>(), Is.Not.Null, "menu C, in a run's scope");
        using (var scope = app.Services.CreateScope()) Assert.That(scope.ServiceProvider.GetRequiredService<SettingsMenu>(), Is.Not.Null, "menu S, in a run's scope");
        Assert.That(app.Services.GetRequiredService<SavedClasses>(), Is.SameAs(app.Services.GetRequiredService<SavedClasses>()), "the saved classes, one for the process");
        Assert.That(app.Services.GetRequiredService<KeytermLoader>().ResolvePath("keyterms.txt"), Is.EqualTo(_folder.File("keyterms.txt")));
        Assert.That(recorder.Services.GetRequiredService<KeytermLoader>().ResolvePath("keyterms.txt"), Is.EqualTo(_folder.File("keyterms.txt")));
        Assert.That(new[] { window.UserFolder, background.UserFolder },
            Has.All.Matches<string?>(folder => folder is null || !folder.StartsWith(real, StringComparison.OrdinalIgnoreCase)), "no test container points at the real user folder");
    }

    /// <summary>
    /// The command line resolves with the app, and the report at the start is the real one, one for the process and the
    /// only one registered, in place of the report that reports nothing.
    /// </summary>
    [Test]
    public void The_command_line_and_the_report_at_the_start_resolve()
    {
        using var app = BuildApp([]);
        var provider = app.Services;

        Assert.That(provider.GetRequiredService<CommandLineTree>(), Is.Not.Null);
        Assert.That(provider.GetRequiredService<IStartupReport>(), Is.InstanceOf<StartupReport>());
        Assert.That(provider.GetRequiredService<IStartupReport>(), Is.SameAs(provider.GetRequiredService<IStartupReport>()), "one report for the process");
        Assert.That(provider.GetServices<IStartupReport>().Select(report => report.GetType()), Is.EqualTo(new[] { typeof(StartupReport) }), "the stand-in is gone");
    }

    /// <summary>
    /// The window's role resolves every command and what starts and shows a run in the background: in a scope of its own,
    /// the starter, the view of runs, the attach, list, devices, transcribe and rebuild commands and the question whether
    /// to continue a recent recording; for the process, the launcher of the recorder, the starter of processes and the
    /// reader of the job ClassScribe runs in. The starter and the view take the settings of their run, so they cannot be
    /// taken outside a run. The view makes its own client of the link, so none is registered.
    /// </summary>
    [Test]
    public void The_window_resolves_every_command_and_what_starts_and_shows_a_run()
    {
        using var app = BuildApp([]);
        var provider = app.Services;
        using var scope = provider.CreateScope();
        var run = scope.ServiceProvider;

        Assert.That(run.GetRequiredService<IRecordStarter>(), Is.InstanceOf<RecordStarter>());
        Assert.That(run.GetRequiredService<IRunViewer>(), Is.InstanceOf<RecorderView>());
        Assert.That(run.GetRequiredService<AttachCommand>(), Is.Not.Null);
        Assert.That(run.GetRequiredService<ListCommand>(), Is.Not.Null);
        Assert.That(run.GetRequiredService<DevicesCommand>(), Is.Not.Null);
        Assert.That(run.GetRequiredService<TranscribeCommand>(), Is.Not.Null);
        Assert.That(run.GetRequiredService<RebuildCommand>(), Is.Not.Null);
        Assert.That(run.GetRequiredService<ContinueQuestion>(), Is.Not.Null);
        Assert.That(provider.GetRequiredService<RecorderLauncher>(), Is.SameAs(provider.GetRequiredService<RecorderLauncher>()), "one launcher for the process");
        Assert.That(provider.GetRequiredService<RunJournal>(), Is.Not.Null);
        if (OperatingSystem.IsWindows())
        {
            Assert.That(provider.GetRequiredService<IProcessStarter>(), Is.InstanceOf<ProcessStarter>());
            Assert.That(provider.GetRequiredService<IJobInspector>(), Is.InstanceOf<JobInspector>());
        }

        Assert.That(() => provider.GetRequiredService<IRecordStarter>(), Throws.InvalidOperationException, "a starter belongs to a run");
        Assert.That(() => provider.GetRequiredService<IRunViewer>(), Throws.InvalidOperationException, "a view belongs to a run");
    }

    /// <summary>
    /// The window's starter is given the camera's start: from the menu, a lesson with a camera picked hears why the camera
    /// cannot record in this build and is offered the sound only, rather than being refused with exit 1 as a starter without
    /// one refuses it. The cameras, Windows' camera facts, its record and the registry are stand-ins, and nobody types, so
    /// the offer goes back and nothing starts.
    /// </summary>
    [Test]
    public async Task The_windows_starter_has_the_cameras_start()
    {
        var ui = new FakeConsoleUi { KeysClosed = true };
        using var app = BuildApp([], replace: services =>
        {
            services.Replace(ServiceDescriptor.Singleton<IConsoleUi>(ui));
            services.Replace(ServiceDescriptor.Singleton<Camera.ICameraProvider>(new FakeCameraProvider()));
            services.Replace(ServiceDescriptor.Singleton<Camera.ICameraSystem>(new FakeCameraSystem()));
            services.Replace(ServiceDescriptor.Singleton<Camera.ICameraUsageRecord>(new FakeCameraUsageRecord()));
            services.Replace(ServiceDescriptor.Singleton<IRegistryValues>(new FakeRegistryValues()));
        });
        using var scope = app.Services.CreateScope();
        var lesson = TestRuns.Request(TestRuns.Id(1), "Tajwid 4") with
        {
            OutputRoot = _folder.File("Classes"),
            Source = new SourcePlan(RecordingMode.Microphone, null, new MicrophoneChoice("OBSBOT"), new CameraChoice("OBSBOT"))
        };

        var exitCode = await scope.ServiceProvider.GetRequiredService<IRecordStarter>().RunAsync(lesson, StartOrigin.Menu);

        Assert.That(exitCode, Is.Zero, "nobody typed, so the offer went back");
        Assert.That(ui.Screen, Does.Contain(CameraStart.SoundOnlyOffer));
        Assert.That(ui.Screen, Does.Not.Contain(CameraStart.NothingStarted));
    }

    /// <summary>
    /// The recorder's role, through the recorder's own host, resolves the run and the recorder's own services: in a scope of
    /// its own, the record command as the run, its bookkeeping, the link's status sink and the recorder's result shower;
    /// for the process, the server, the alerts that bring a window back, the starter of that window and the link's
    /// console. What the run records with and what reaches Windows are stand-ins, since resolving the run constructs them.
    /// </summary>
    [Test]
    public void The_recorder_resolves_the_run_and_its_own_services()
    {
        var runId = TestRuns.Id();
        var time = new ManualTimeProvider(TestRuns.Start);
        using var log = new RecorderLog(time);
        using var host = AppHost.BuildRecorder(new RecorderStart(runId, TestRuns.Request(runId), new Dictionary<string, string?>()), "test-key", log, _runtime.Folder, Places(), time, recorder =>
        {
            recorder.Replace(ServiceDescriptor.Scoped<ISessionEvents>(_ => new FakeSessionEvents()));
            recorder.Replace(ServiceDescriptor.Transient<IRecordingFactory>(_ => new FakeRecordingFactory()));
            recorder.Replace(ServiceDescriptor.Singleton<IPowerRequests>(new FakePowerRequests()));
            recorder.Replace(ServiceDescriptor.Singleton<IPowerSource>(new FakePowerSource()));
            recorder.Replace(ServiceDescriptor.Singleton<ISystemTimes>(new FakeSystemTimes(time)));
            recorder.AddSingleton(new RoamingFolder(_folder.File("Roaming")));
        });
        var services = host.Services;
        using var scope = services.CreateScope();
        var run = scope.ServiceProvider;

        Assert.That(run.GetRequiredService<IRecordRun>(), Is.InstanceOf<RecordCommand>());
        Assert.That(run.GetRequiredService<RunBookkeeping>(), Is.SameAs(run.GetRequiredService<RunBookkeeping>()), "one per run");
        Assert.That(run.GetRequiredService<IRecorderStatusSink>(), Is.InstanceOf<LinkStatusSink>());
        Assert.That(run.GetRequiredService<IResultShower>(), Is.InstanceOf<RecorderResultShower>());
        Assert.That(services.GetRequiredService<RecorderServer>(), Is.SameAs(services.GetRequiredService<RecorderServer>()), "one server for the run");
        Assert.That(services.GetRequiredService<UnattendedAlerts>(), Is.Not.Null);
        Assert.That(services.GetRequiredService<IConsoleUi>(), Is.InstanceOf<LinkConsoleUi>());
        Assert.That(services.GetRequiredService<IWindowSignals>(), Is.InstanceOf<NoWindowSignals>(), "a recorder has no window");
        if (OperatingSystem.IsWindows()) Assert.That(services.GetRequiredService<IProcessStarter>(), Is.InstanceOf<ProcessStarter>());
    }

    /// <summary>
    /// The window's role has the microphone registered: for the process, the microphone provider, made only when it is
    /// first used, the factory that opens a microphone's capture, the reader of the apps' microphone sessions, Windows'
    /// microphone usage record, the process names and ClassScribe's own processes; in a run's scope, the track-set factory
    /// as the factory of recordings, the microphone menu of choice 7, and the microphone check of choice 8 as the run the
    /// runner resolves. Resolving all of it touches no microphone.
    /// </summary>
    [Test]
    public void The_window_has_the_microphone_registered_and_makes_the_provider_only_when_used()
    {
        using var app = BuildApp([]);
        var provider = app.Services;
        using var scope = provider.CreateScope();
        var run = scope.ServiceProvider;

        Assert.That(provider.GetRequiredService<IMicrophoneProvider>(), Is.SameAs(provider.GetRequiredService<IMicrophoneProvider>()), "one provider for the process");
        Assert.That(provider.GetRequiredService<IMicrophoneProvider>(), Is.SameAs(provider.GetRequiredService<Lazy<IMicrophoneProvider>>().Value), "one provider, however it is reached");
        Assert.That(provider.GetRequiredService<Lazy<IMicrophoneSessions>>().Value, Is.SameAs(provider.GetRequiredService<IMicrophoneSessions>()), "one reader, however it is reached");
        Assert.That(provider.GetRequiredService<IOwnProcesses>(), Is.InstanceOf<OwnProcesses>());
        Assert.That(run.GetRequiredService<IRecordingFactory>(), Is.InstanceOf<TrackSetFactory>());
        Assert.That(run.GetRequiredService<MicrophoneMenu>(), Is.Not.Null);
        Assert.That(run.GetRequiredService<IMicCheckRun>(), Is.InstanceOf<MicCheckCommand>());
        if (OperatingSystem.IsWindows())
        {
            Assert.That(provider.GetRequiredService<IMicrophoneCaptureFactory>(), Is.InstanceOf<MicrophoneActivation>());
            Assert.That(provider.GetRequiredService<IMicrophoneCaptureFactory>(), Is.SameAs(provider.GetRequiredService<IMicrophoneCaptureFactory>()), "one factory, which remembers where RAW failed");
            Assert.That(provider.GetRequiredService<IMicrophoneSessions>(), Is.InstanceOf<WasapiMicrophoneSessions>());
            Assert.That(provider.GetRequiredService<IMicrophoneUsageRecord>(), Is.InstanceOf<MicrophoneUsageRecord>());
            Assert.That(provider.GetRequiredService<IProcessNames>(), Is.InstanceOf<ProcessNames>());
        }

        Assert.That(MicrophoneProvider(provider).IsMade, Is.False, "nothing has touched a microphone");
    }

    /// <summary>
    /// The recorder's role makes a run's recording through the track-set factory, which records the class and your
    /// microphone on one clock: with stand-ins for everything that reaches Windows, the run resolves, the factory it
    /// records with is the track-set factory, and a plan for the class and your microphone gives a track set, which opens
    /// nothing until it starts.
    /// </summary>
    [Test]
    public void The_recorder_makes_recordings_through_the_track_set_factory()
    {
        var runId = TestRuns.Id();
        var time = new ManualTimeProvider(TestRuns.Start);
        using var log = new RecorderLog(time);
        using var host = AppHost.BuildRecorder(new RecorderStart(runId, TestRuns.Request(runId), new Dictionary<string, string?>()), "test-key", log, _runtime.Folder, Places(), time,
            recorder =>
            {
                ReplaceWindowsWithFakes(recorder, time);
                recorder.AddSingleton(new RoamingFolder(_folder.File("Roaming")));
            });
        using var scope = host.Services.CreateScope();
        var run = scope.ServiceProvider;

        var factory = run.GetRequiredService<IRecordingFactory>();
        using var recording = factory.Create(new SourcePlan(RecordingMode.ClassAndMicrophone, new ClassSource.Automatic(), new MicrophoneChoice(null)));

        Assert.That(run.GetRequiredService<IRecordRun>(), Is.InstanceOf<RecordCommand>());
        Assert.That(run.GetRequiredService<IDriveAutoUpload>(), Is.InstanceOf<DriveAutoUpload>(), "the recorder uploads after its class");
        Assert.That(factory, Is.InstanceOf<TrackSetFactory>());
        Assert.That(recording, Is.InstanceOf<TrackSet>());
        Assert.That(((FakeMicrophoneProvider)host.Services.GetRequiredService<IMicrophoneProvider>()).Opened, Is.Empty, "nothing is opened before the recording starts");
    }

    /// <summary>
    /// The audio provider and the microphone provider are made only when something first uses them, so that transcribe,
    /// rebuild, list and the app itself never touch Windows audio: the app, with its menu and command line, resolves; the
    /// three commands resolve and run through the real services; and neither provider has been made. The devices command
    /// takes the providers as something to make when it runs.
    /// </summary>
    [Test]
    public async Task Transcribe_rebuild_list_and_the_app_resolve_and_run_without_Windows_audio()
    {
        using var data = new TempFolder();
        using var app = BuildApp(new() { ["Recording:OutputFolder"] = data.File("Recordings") });
        var provider = app.Services;
        var application = provider.GetRequiredService<App>();
        using (var scope = provider.CreateScope())
        {
            Assert.That(scope.ServiceProvider.GetRequiredService<TranscribeCommand>(), Is.Not.Null);
            Assert.That(scope.ServiceProvider.GetRequiredService<RebuildCommand>(), Is.Not.Null);
            Assert.That(scope.ServiceProvider.GetRequiredService<ListCommand>(), Is.Not.Null);
            Assert.That(scope.ServiceProvider.GetRequiredService<DevicesCommand>(), Is.Not.Null);
        }

        var missing = data.File("missing.wav");
        Assert.That(await application.RunAsync(["list", "--output", data.File("Recordings")]), Is.EqualTo(0));
        Assert.That(await application.RunAsync(["transcribe", missing]), Is.EqualTo(1));
        Assert.That(await application.RunAsync(["rebuild", missing]), Is.EqualTo(1));

        Assert.That(CaptureProvider(provider).IsMade, Is.False, "nothing has touched Windows audio");
        Assert.That(provider.GetRequiredService<Lazy<ILoopbackCaptureProvider>>().IsValueCreated, Is.False);
        Assert.That(MicrophoneProvider(provider).IsMade, Is.False, "nothing has touched a microphone");
        Assert.That(provider.GetRequiredService<Lazy<IMicrophoneProvider>>().IsValueCreated, Is.False);
        Assert.That(provider.GetRequiredService<Lazy<IMicrophoneSessions>>().IsValueCreated, Is.False);
    }

    /// <summary>
    /// The devices command makes the audio provider and the microphone provider when it runs, and only then: it lists the
    /// real output devices, the apps with sound and the microphones with the apps that hold them, which opens no device
    /// and no microphone and records nothing, and ends with exit code 0.
    /// </summary>
    [Test, Platform("Win"), Category("WindowsAudio")]
    [SupportedOSPlatform("windows")]
    public async Task The_devices_command_makes_the_audio_providers_when_it_runs()
    {
        using var app = BuildApp([]);
        var provider = app.Services;
        var made = CaptureProvider(provider).IsMade || MicrophoneProvider(provider).IsMade;

        var exitCode = await provider.GetRequiredService<App>().RunAsync(["devices"]);

        Assert.That(made, Is.False, "not before the command ran");
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(CaptureProvider(provider).IsMade, Is.True);
        Assert.That(MicrophoneProvider(provider).IsMade, Is.True);
        Assert.That(provider.GetRequiredService<Lazy<IMicrophoneSessions>>().IsValueCreated, Is.True, "the apps that hold each microphone were read");
        Assert.That(provider.GetRequiredService<ILoopbackCaptureProvider>(), Is.SameAs(provider.GetRequiredService<Lazy<ILoopbackCaptureProvider>>().Value), "one provider, however it is reached");
    }

    /// <summary>Through the real services, help succeeds and a record command without its pieces is a mistake, with exit code 2.</summary>
    [Test]
    public async Task Help_and_a_mistake_have_their_exit_codes_through_the_real_services()
    {
        using var app = BuildApp([]);

        Assert.That(await app.Services.GetRequiredService<App>().RunAsync(["--help"]), Is.EqualTo(0));
        Assert.That(await app.Services.GetRequiredService<App>().RunAsync(["record", "--help"]), Is.EqualTo(0));
        Assert.That(await app.Services.GetRequiredService<App>().RunAsync(["record", "Kalam"]), Is.EqualTo(2));
    }

    /// <summary>
    /// Through the real services, record with a saved class's name and no source points at --class (N5) even while a
    /// setting outside Classes is refused, so that the first read left none of the file's values in force: the registered
    /// saved classes read the file's own classes for the hint.
    /// </summary>
    [Test]
    public async Task A_saved_class_is_pointed_at_class_through_the_real_services_while_a_setting_is_refused()
    {
        using var app = BuildApp(new() { ["Recording:FlushSeconds"] = "0", ["Classes:Mantiq:Course"] = "Mantiq", ["Classes:Mantiq:App"] = "chrome" });
        var errors = new StringWriter();
        var before = Console.Error;
        Console.SetError(errors);
        try
        {
            Assert.That(await app.Services.GetRequiredService<App>().RunAsync(["record", "Mantiq"]), Is.EqualTo(2));
        }
        finally
        {
            Console.SetError(before);
        }

        Assert.That(errors.ToString(), Does.Contain("Mantiq is a saved class: ClassScribe record --class \"Mantiq\""));
    }

    /// <summary>Each run gets signals of its own, so a Ctrl+C from one run does not leak into the next.</summary>
    [Test]
    public void Each_run_gets_its_own_signals()
    {
        using var app = BuildApp([]);
        using var first = app.Services.CreateScope();
        using var second = app.Services.CreateScope();

        Assert.That(first.ServiceProvider.GetRequiredService<IUserSignals>(), Is.Not.SameAs(second.ServiceProvider.GetRequiredService<IUserSignals>()));
    }

    /// <summary>The registered ElevenLabs client lets network errors through unwrapped, so they are recognised and retried.</summary>
    [Test]
    public void The_ElevenLabs_client_reports_send_failures_unwrapped()
    {
        using var app = BuildApp([]);
        var failure = new HttpRequestException("Connection reset.");

        var settings = app.Services.GetRequiredService<SettingsFor<IElevenLabsApi>>().Settings!;

        Assert.That(settings.TransportExceptionFactory(new HttpRequestMessage(), failure, CancellationToken.None), Is.SameAs(failure));
    }

    /// <summary>
    /// The upload streams from disk as it is sent: the Refit settings the ElevenLabs client is registered with
    /// neither buffer the request body nor capture it, either of which would hold a whole class in memory
    /// before the first byte goes out, and they do not keep the call's arguments, the API key among them,
    /// on the request. The client's own tests build their client outside the app's registration, so only this
    /// test sees the settings the app really uses.
    /// </summary>
    [Test]
    public void The_ElevenLabs_upload_is_streamed_not_buffered()
    {
        using var app = BuildApp([]);

        var settings = app.Services.GetRequiredService<SettingsFor<IElevenLabsApi>>().Settings!;

        Assert.That(settings.Buffered, Is.False, "a buffered body is read into memory before it is sent");
        Assert.That(settings.CaptureRequestContent, Is.False, "a captured body is read into a string before it is sent");
        Assert.That(settings.CaptureMethodArguments, Is.False, "captured arguments stay on the request and on any exception made from it");
    }

    /// <summary>A word that is neither a command nor a path shows the usage and fails with exit code 2.</summary>
    [Test]
    public async Task An_unknown_command_shows_the_usage_and_fails()
    {
        using var app = BuildApp([]);

        var exitCode = await app.Services.GetRequiredService<App>().RunAsync(["frobnicate"]);

        Assert.That(exitCode, Is.EqualTo(2));
    }

    /// <summary>
    /// Without a key, transcribing fails, through the real services, in a folder of its own rather than
    /// the user's temporary folder. What exactly it skips is checked in <see cref="TranscribeCommandTests"/>.
    /// </summary>
    [Test]
    public async Task Transcribing_without_a_key_fails_before_any_work()
    {
        using var data = new TempFolder();
        using var app = BuildApp(new() { ["Recording:OutputFolder"] = data.File("Recordings") }, apiKey: null);

        var exitCode = await app.Services.GetRequiredService<App>().RunAsync(["transcribe", data.Path]);

        Assert.That(exitCode, Is.EqualTo(1));
        Assert.That(Directory.GetFileSystemEntries(data.Path), Is.Empty, "nothing was written");
    }

    /// <summary>A path that leads nowhere is reported as a failure, not a crash.</summary>
    [Test]
    public async Task Transcribing_a_missing_file_fails_cleanly()
    {
        using var app = BuildApp([]);

        var exitCode = await app.Services.GetRequiredService<App>().RunAsync(["transcribe", Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".wav")]);

        Assert.That(exitCode, Is.EqualTo(1));
    }

    /// <summary>
    /// The YouTube feature is registered in the window's role: its command, menu and batch made in a run's scope, the
    /// settings of the YouTube section bound (everything of a video's recording to Drive by default), its folders under
    /// ClassScribe's own folder, and the real runner of yt-dlp, which a test run refuses to run.
    /// </summary>
    [Test, Platform("Win")]
    [SupportedOSPlatform("windows")]
    public void The_YouTube_feature_is_registered_and_the_real_runner_is_refused_in_tests()
    {
        using var app = BuildApp(new() { ["YouTube:MayDownloadChannels:0"] = "@yourchannel" });
        using var scope = app.Services.CreateScope();

        Assert.That(scope.ServiceProvider.GetRequiredService<YouTubeCommand>(), Is.Not.Null);
        Assert.That(scope.ServiceProvider.GetRequiredService<YouTubeMenu>(), Is.Not.Null);
        Assert.That(scope.ServiceProvider.GetRequiredService<YouTube.DownloadBatch>(), Is.Not.Null);
        Assert.That(scope.ServiceProvider.GetRequiredService<YouTube.IVideoTranscriber>(), Is.InstanceOf<YouTube.ScopedVideoTranscriber>());
        var youTube = scope.ServiceProvider.GetRequiredService<IOptionsSnapshot<YouTubeOptions>>().Value;
        Assert.That(youTube.MayDownloadChannels, Is.EqualTo(["@yourchannel"]));
        Assert.That(youTube.DriveUpload, Is.EqualTo(DriveUploadScope.Everything));
        Assert.That(app.Services.GetRequiredService<YouTube.YouTubeFolders>().Root, Is.EqualTo(Path.Combine(_runtime.Folder.Root, "youtube")));

        var runner = app.Services.GetRequiredService<YouTube.IToolRunner>();
        Assert.That(runner, Is.InstanceOf<YouTube.ProcessToolRunner>());
        Assert.That(async () => await runner.RunAsync(new YouTube.ToolCall(@"C:\Tools\yt-dlp.exe", ["--version"], _folder.Path), null, CancellationToken.None),
            Throws.InvalidOperationException.With.Message.Contains("FakeToolRunner"));
    }

    /// <summary>A transcript of a batch is made in a scope of its own, with the workflow, the run files and the usage report of that scope.</summary>
    [Test]
    public void A_batchs_transcript_is_made_in_a_scope_of_its_own()
    {
        using var app = BuildApp([]);
        using var first = app.Services.CreateScope();
        using var second = app.Services.CreateScope();

        var one = first.ServiceProvider.GetRequiredService<YouTube.VideoTranscriptRun>();

        Assert.That(one, Is.SameAs(first.ServiceProvider.GetRequiredService<YouTube.VideoTranscriptRun>()));
        Assert.That(one, Is.Not.SameAs(second.ServiceProvider.GetRequiredService<YouTube.VideoTranscriptRun>()));
    }

    /// <summary>
    /// The record path is registered in the window's role: its batch, the window it opens, the titles' client and Chrome's
    /// locator in a run's scope; the yield of a video's run, and Windows' windows and processes for a video's window, for the
    /// process, the real ones refusing a test; the YouTube section's record-path settings bound with their defaults; and the
    /// playback a run records a video's window through, one per run.
    /// </summary>
    [Test, Platform("Win")]
    [SupportedOSPlatform("windows")]
    public void The_record_path_is_registered_in_the_window()
    {
        using var app = BuildApp([]);
        using var scope = app.Services.CreateScope();
        using var other = app.Services.CreateScope();
        var run = scope.ServiceProvider;

        Assert.That(run.GetRequiredService<YouTube.VideoBatch>(), Is.Not.Null);
        Assert.That(run.GetRequiredService<YouTube.VideoWindow>(), Is.Not.Null);
        Assert.That(run.GetRequiredService<YouTube.OEmbedTitles>(), Is.Not.Null);
        Assert.That(run.GetRequiredService<YouTube.VideoPlayback>(), Is.SameAs(run.GetRequiredService<YouTube.VideoPlayback>()), "one per run");
        Assert.That(run.GetRequiredService<YouTube.VideoPlayback>(), Is.Not.SameAs(other.ServiceProvider.GetRequiredService<YouTube.VideoPlayback>()));
        Assert.That(app.Services.GetRequiredService<IVideoYield>(), Is.InstanceOf<WindowsVideoYield>());
        Assert.That(app.Services.GetRequiredService<IProcessFamily>(), Is.InstanceOf<ProcessFamily>());
        Assert.That(() => app.Services.GetRequiredService<IVideoWindows>().TitlesOf(Environment.ProcessId), Throws.InvalidOperationException.With.Message.Contains("FakeVideoWindows"));
        var youTube = run.GetRequiredService<IOptionsSnapshot<YouTubeOptions>>().Value;
        Assert.That((youTube.BrowserPath, youTube.LongestVideoHours, youTube.OEmbedBaseUrl), Is.EqualTo(("", 4.0, "https://www.youtube.com/")));
    }

    /// <summary>
    /// The recorder's role records a video's window: in a run's scope, the run with its playback, and the yield it listens to
    /// for a class, for the process; what reaches Windows is a stand-in, since resolving the run constructs it.
    /// </summary>
    [Test, Platform("Win")]
    [SupportedOSPlatform("windows")]
    public void The_recorder_resolves_a_videos_run()
    {
        var runId = TestRuns.Id();
        var time = new ManualTimeProvider(TestRuns.Start);
        using var log = new RecorderLog(time);
        using var host = AppHost.BuildRecorder(new RecorderStart(runId, TestRuns.Request(runId) with { Video = YouTubeTestRuns.Video() }, new Dictionary<string, string?>()),
            "test-key", log, _runtime.Folder, Places(), time, recorder =>
            {
                ReplaceWindowsWithFakes(recorder, time);
                recorder.AddSingleton(new RoamingFolder(_folder.File("Roaming")));
            });
        using var scope = host.Services.CreateScope();
        var run = scope.ServiceProvider;

        Assert.That(run.GetRequiredService<IRecordRun>(), Is.InstanceOf<RecordCommand>());
        Assert.That(run.GetRequiredService<YouTube.VideoPlayback>(), Is.Not.Null);
        Assert.That(run.GetRequiredService<RecordingMonitor>(), Is.Not.Null);
        Assert.That(host.Services.GetRequiredService<IVideoYield>(), Is.InstanceOf<WindowsVideoYield>());
    }
}
