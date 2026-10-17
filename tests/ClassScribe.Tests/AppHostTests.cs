using ClassScribe.Audio;
using ClassScribe.Commands;
using ClassScribe.Desktop;
using ClassScribe.Desktop.Windows;
using ClassScribe.Drive;
using ClassScribe.Link;
using ClassScribe.Recorder;
using ClassScribe.Settings;
using ClassScribe.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;

namespace ClassScribe.Tests;

/// <summary>How the app is put together at start-up: where the key comes from, where the settings come from, and the first read of them.</summary>
[TestFixture]
public class AppHostTests
{
    private TempFolder _folder = null!;

    /// <summary>Creates a temporary folder to hold an appsettings.json.</summary>
    [SetUp]
    public void SetUp() => _folder = new TempFolder();

    /// <summary>Deletes the temporary folder.</summary>
    [TearDown]
    public void TearDown() => _folder.Dispose();

    /// <summary>The settings file of the app under test.</summary>
    private string SettingsPath => _folder.File("appsettings.json");

    /// <summary>Where the app under test reads its settings: the test's folder as the program's, and no user folder, as before this rule.</summary>
    private SettingsPlaces Places() => new(_folder.Path, userFolder: null);

    /// <summary>Writes the appsettings.json the host is built from.</summary>
    private void WriteSettings(string json) => File.WriteAllText(SettingsPath, json);

    /// <summary>The key comes from ELEVENLABS_API_KEY: one written into appsettings.json is not used.</summary>
    [Test]
    public void The_key_from_the_environment_variable_wins_over_one_in_appsettings_json()
    {
        WriteSettings("""{ "ElevenLabs": { "ApiKey": "key-in-file" } }""");

        using var host = AppHost.Build(Places(),apiKey: "key-from-variable");

        Assert.That(host.Services.GetRequiredService<IOptionsMonitor<ElevenLabsOptions>>().CurrentValue.ApiKey, Is.EqualTo("key-from-variable"));
    }

    /// <summary>Without ELEVENLABS_API_KEY there is no key at all, even when appsettings.json has one: that one is never used.</summary>
    [Test]
    public void Without_the_variable_a_key_in_appsettings_json_is_still_not_used()
    {
        WriteSettings("""{ "ElevenLabs": { "ApiKey": "key-in-file" } }""");

        using var host = AppHost.Build(Places(),apiKey: null);

        Assert.That(host.Services.GetRequiredService<IOptionsMonitor<ElevenLabsOptions>>().CurrentValue.ApiKey, Is.Empty);
    }

    /// <summary>The settings that pass the check at start are used from the start: the host reads the file once as it is put together.</summary>
    [Test]
    public void Settings_that_pass_are_used_from_the_start()
    {
        WriteSettings("""{ "Recording": { "FlushSeconds": 8 }, "Transcript": { "ParagraphPauseSeconds": 3.5 } }""");

        using var host = AppHost.Build(Places(),apiKey: "test-key");

        Assert.That(host.Services.GetRequiredService<IOptionsMonitor<RecordingOptions>>().CurrentValue.FlushSeconds, Is.EqualTo(8));
        Assert.That(host.Services.GetRequiredService<IOptions<TranscriptOptions>>().Value.ParagraphPauseSeconds, Is.EqualTo(3.5));
    }

    /// <summary>
    /// A hand-edited appsettings.json that is not valid JSON, typically a path written with single backslashes, no
    /// longer stops ClassScribe as it starts: the defaults stand in, what the first read found is kept for the menu's
    /// header, and the gate before the first choice or command explains it plainly, with the line, how to write a path in
    /// JSON, and which file. No stack trace.
    /// </summary>
    [Test]
    public void Settings_that_are_not_valid_JSON_leave_the_defaults_and_are_explained_at_the_gate()
    {
        WriteSettings("""
            {
              "Recording": {
                "OutputFolder": "D:\Classes"
              }
            }
            """);

        using var host = AppHost.Build(Places(),apiKey: null);
        Assert.That(host.Services.GetRequiredService<RunGate>().Problems, Has.Some.StartsWith("The mistake is on line 3:"), "kept from the first read, for the menu's header");
        var log = new RecordingLogger<RunGate>();
        var opened = new RunGate(host.Services.GetRequiredService<SettingsReloader>(), host.Services.GetRequiredService<SettingsFile>(), log).TryOpen(GateUse.Command);

        Assert.That(host.Services.GetRequiredService<IOptionsMonitor<RecordingOptions>>().CurrentValue.OutputFolder, Is.Empty, "the defaults stand in");
        Assert.That(opened, Is.False);
        Assert.That(log.Problems[0], Does.StartWith("The mistake is on line 3:"));
        Assert.That(log.Problems[1], Does.Contain(@"""D:\\Classes""").And.Contain(@"""D:/Classes"""), "says how to write a path in JSON");
        Assert.That(log.Problems[^1], Does.Contain(SettingsPath));
        Assert.That(string.Join(Environment.NewLine, log.Problems), Does.Not.Contain("   at "), "no stack trace");
    }

    /// <summary>
    /// The appsettings.json that ships passes the check without a warning, holds the log format setting the README lists,
    /// and holds the settings added for keeping the laptop awake and for the upload's time limit, at their defaults.
    /// </summary>
    [Test]
    public void The_shipped_settings_pass_the_check_and_hold_their_settings()
    {
        using var host = AppHost.Build(new SettingsPlaces(AppContext.BaseDirectory, userFolder: null), apiKey: "test-key");

        var verdict = host.Services.GetRequiredService<SettingsReloader>().TryReload();

        Assert.That(verdict.Usable, Is.True);
        Assert.That(verdict.Problems, Is.Empty);
        Assert.That(verdict.Warnings, Is.Empty);
        var configuration = host.Services.GetRequiredService<IConfiguration>();
        Assert.That(configuration["Logging:Console:FormatterName"], Is.EqualTo(PlainConsoleFormatter.FormatterName));
        Assert.That(configuration["Recording:KeepScreenOn"], Is.EqualTo(nameof(KeepScreenOn.OnBattery)));
        Assert.That(configuration["ElevenLabs:SlowestUploadMbps"], Is.EqualTo("1"));
    }

    /// <summary>
    /// The shipped appsettings.json is a template that an update replaces: every value in it is the code's own default, so
    /// a key missing from the user's copy keeps the shipped value, and its Logging keys, which only the file used to hold,
    /// are exactly the defaults AppHost adds (see <see cref="DefaultSettings"/>). The template can never document a default
    /// the code does not have, and every key in it names a setting. Recording:OutputFolder is written out as
    /// %USERPROFILE%\ClassScribe where the code's default is empty; both mean the same folder.
    /// </summary>
    [Test]
    public void The_shipped_template_equals_the_code_defaults()
    {
        var template = new ConfigurationBuilder().AddJsonFile(Path.Combine(AppContext.BaseDirectory, SettingsFile.FileName)).Build();

        AssertEqualsDefaults(template, RecordingOptions.SectionName, new RecordingOptions(), nameof(RecordingOptions.OutputFolder));
        AssertEqualsDefaults(template, ElevenLabsOptions.SectionName, new ElevenLabsOptions());
        AssertEqualsDefaults(template, TranscriptOptions.SectionName, new TranscriptOptions());
        AssertEqualsDefaults(template, DriveOptions.SectionName, new DriveOptions());
        AssertEqualsDefaults(template, YouTubeOptions.SectionName, new YouTubeOptions());
        Assert.That(ClassFolders.RootFor(template["Recording:OutputFolder"]), Is.EqualTo(ClassFolders.RootFor("")), "the same folder, written out");
        var logging = template.GetSection("Logging").AsEnumerable().Where(pair => pair.Value is not null).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        Assert.That(logging, Is.EquivalentTo(DefaultSettings.Values));
        Assert.That(template.GetChildren().Select(section => section.Key), Is.EquivalentTo(new[]
        {
            RecordingOptions.SectionName, ElevenLabsOptions.SectionName, TranscriptOptions.SectionName, DriveOptions.SectionName, YouTubeOptions.SectionName, "Logging"
        }));
    }

    /// <summary>
    /// Asserts that section <paramref name="section"/> of <paramref name="template"/> binds to the values of
    /// <paramref name="fresh"/>, an instance with nothing set, property by property but for <paramref name="except"/>, and
    /// that every key in the section names a property.
    /// </summary>
    private static void AssertEqualsDefaults<T>(IConfiguration template, string section, T fresh, params string[] except) where T : class, new()
    {
        var bound = template.GetSection(section).Get<T>()!;
        var properties = typeof(T).GetProperties().Where(property => property.CanRead && property.SetMethod is not null).ToList();
        Assert.That(template.GetSection(section).GetChildren().Select(child => child.Key),
            Has.All.Matches<string>(key => properties.Any(property => property.Name.Equals(key, StringComparison.OrdinalIgnoreCase))), $"every key in {section} is a setting");
        foreach (var property in properties.Where(property => !except.Contains(property.Name)))
            Assert.That(property.GetValue(bound), Is.EqualTo(property.GetValue(fresh)), $"{section}:{property.Name}");
    }

    /// <summary>
    /// Without a settings file, and with one that leaves the Logging section out, the log is as the shipped file makes it:
    /// ClassScribe's lines at Information, the Microsoft and System categories (the HttpClient's chatter among them) at
    /// Warning, and the plain format. The file's own values win per key, from the next choice on, and the defaults reach
    /// the recorder through the snapshot.
    /// </summary>
    [Test]
    public void The_logging_defaults_apply_without_a_file_and_the_file_wins_per_key()
    {
        using var host = AppHost.Build(Places(), apiKey: "test-key");
        var configuration = host.Services.GetRequiredService<IConfiguration>();
        var loggers = host.Services.GetRequiredService<ILoggerFactory>();

        foreach (var (key, value) in DefaultSettings.Values) Assert.That(configuration[key], Is.EqualTo(value), key);
        Assert.That(loggers.CreateLogger("ClassScribe.Tests").IsEnabled(LogLevel.Information), Is.True);
        Assert.That(loggers.CreateLogger("System.Net.Http.HttpClient.IElevenLabsApi.LogicalHandler").IsEnabled(LogLevel.Information), Is.False, "the HttpClient's chatter stays out");
        Assert.That(loggers.CreateLogger("Microsoft.Extensions.Hosting.Internal.Host").IsEnabled(LogLevel.Information), Is.False);
        Assert.That(host.Services.GetRequiredService<IOptionsMonitor<ConsoleLoggerOptions>>().CurrentValue.FormatterName, Is.EqualTo(PlainConsoleFormatter.FormatterName));
        Assert.That(host.Services.GetRequiredService<SettingsReloader>().Snapshot(), Does.ContainKey("Logging:LogLevel:Microsoft").WithValue("Warning"), "the defaults reach the recorder");

        WriteSettings("""{ "Logging": { "LogLevel": { "Microsoft": "Debug" } } }""");
        Assert.That(host.Services.GetRequiredService<RunGate>().TryOpen(GateUse.MenuChoice), Is.True);

        Assert.That(loggers.CreateLogger("Microsoft.Extensions.Hosting.Internal.Host").IsEnabled(LogLevel.Debug), Is.True, "the file's value, from the next choice");
        Assert.That(configuration["Logging:LogLevel:System"], Is.EqualTo("Warning"), "the other defaults stay");
    }

    /// <summary>
    /// The settings are read again before each menu choice and each command, never in between: a value edited in
    /// appsettings.json while ClassScribe runs, as the menu stays open from class to class, is not taken until the next
    /// choice, and then only once it has passed the check. Here a comma decimal, which would otherwise stop the
    /// transcript after the paid upload, is refused and the value before it stays; the corrected edit is then taken.
    /// </summary>
    [Test]
    public async Task A_setting_edited_while_ClassScribe_runs_counts_from_the_next_choice_once_checked()
    {
        WriteSettings("""{ "Transcript": { "ParagraphPauseSeconds": 3.5 } }""");
        using var host = AppHost.Build(Places(),apiKey: "test-key");
        var monitor = host.Services.GetRequiredService<IOptionsMonitor<TranscriptOptions>>();
        var gate = host.Services.GetRequiredService<RunGate>();

        // A second reader of the file, which watches it for changes as a host does by default, shows
        // when the edit has been seen; the half second after it is time for any other reader to see it too.
        using var watcher = new ConfigurationManager();
        watcher.AddJsonFile(SettingsPath, optional: false, reloadOnChange: true);
        WriteSettings("""{ "Transcript": { "ParagraphPauseSeconds": "1,5" } }""");
        await Wait.UntilAsync(() => watcher["Transcript:ParagraphPauseSeconds"] == "1,5", timeoutSeconds: 10);
        await Task.Delay(500);

        Assert.That(monitor.CurrentValue.ParagraphPauseSeconds, Is.EqualTo(3.5), "the file is not watched");
        Assert.That(gate.TryOpen(GateUse.MenuChoice), Is.False, "the edit is checked at the next choice, and refused");
        Assert.That(monitor.CurrentValue.ParagraphPauseSeconds, Is.EqualTo(3.5));

        WriteSettings("""{ "Transcript": { "ParagraphPauseSeconds": 1.5 } }""");

        Assert.That(gate.TryOpen(GateUse.MenuChoice), Is.True);
        Assert.That(monitor.CurrentValue.ParagraphPauseSeconds, Is.EqualTo(1.5));
        using var run = host.Services.CreateScope();
        Assert.That(run.ServiceProvider.GetRequiredService<IOptionsSnapshot<TranscriptOptions>>().Value.ParagraphPauseSeconds, Is.EqualTo(1.5));
    }

    /// <summary>
    /// The log follows the Logging section of appsettings.json, which the host reads only because it is told to: its
    /// levels, and the console format it names, such as "simple" for full stack traces, as the README describes. That
    /// holds from the start, and an edit counts from the next choice once it has passed the check. A file that names no
    /// format gets the plain one.
    /// </summary>
    [Test]
    public void The_log_follows_the_Logging_section_from_the_start_and_after_an_edit()
    {
        WriteSettings("""{ "Logging": { "LogLevel": { "Default": "Warning" }, "Console": { "FormatterName": "simple" } } }""");
        using var host = AppHost.Build(Places(),apiKey: "test-key");
        var log = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("ClassScribe.Tests");
        var console = host.Services.GetRequiredService<IOptionsMonitor<ConsoleLoggerOptions>>();

        Assert.That((log.IsEnabled(LogLevel.Information), log.IsEnabled(LogLevel.Warning)), Is.EqualTo((false, true)), "the file's level, Warning");
        Assert.That(console.CurrentValue.FormatterName, Is.EqualTo("simple"), "the file's format");

        WriteSettings("""{ "Logging": { "LogLevel": { "Default": "Debug" } } }""");
        Assert.That(host.Services.GetRequiredService<RunGate>().TryOpen(GateUse.MenuChoice), Is.True);

        Assert.That(log.IsEnabled(LogLevel.Debug), Is.True, "the edited level, from the next choice");
        Assert.That(console.CurrentValue.FormatterName, Is.EqualTo(PlainConsoleFormatter.FormatterName), "no format named, so the plain one");
    }

    /// <summary>
    /// An environment variable that sets a value ClassScribe cannot use stops the start, with the reason in plain words:
    /// unlike the file, a variable cannot be fixed while ClassScribe runs, and every part reading the settings would fail
    /// on it. The variable gets back the value it had.
    /// </summary>
    [Test]
    public void A_value_from_an_environment_variable_that_cannot_be_used_stops_the_start()
    {
        WriteSettings("""{ "Recording": { "FlushSeconds": 5 } }""");
        using var variable = new TemporaryVariable("Recording__FlushSeconds", "abc");

        var failure = Assert.Catch(() => AppHost.Build(Places(),apiKey: "test-key").Dispose())!;
        var explanation = AppHost.DescribeFailure(failure, Places());

        Assert.That(explanation, Does.StartWith("ClassScribe could not start."));
        Assert.That(explanation, Does.Contain("Recording:FlushSeconds is \"abc\", which is not a whole number").And.Contain("environment variable"));
        Assert.That(explanation, Does.Not.Contain("   at "), "no stack trace");
    }

    /// <summary>The host checks that every per-run service is taken inside a run's scope, so a slip shows at once rather than as a leak.</summary>
    [Test]
    public void A_per_run_service_cannot_be_taken_outside_a_run()
    {
        using var host = AppHost.Build(Places(),apiKey: "test-key");

        Assert.That(() => host.Services.GetRequiredService<IUserSignals>(), Throws.InvalidOperationException);
    }

    /// <summary>A failure while ClassScribe is put together is explained in plain words: one line per exception, and the settings file for a configuration error.</summary>
    [Test]
    public void A_failure_at_start_is_explained_in_plain_words()
    {
        var failure = new InvalidOperationException("The configuration of the console log failed.", new FormatException("Loud is not a level."));

        var explanation = AppHost.DescribeFailure(failure, Places());

        Assert.That(explanation, Does.StartWith("ClassScribe could not start."));
        Assert.That(explanation, Does.Contain("The configuration of the console log failed.").And.Contain("Loud is not a level."));
        Assert.That(explanation, Does.Contain(SettingsPath));
    }

    /// <summary>A failure at start names the settings file that was read: the user's own copy when that is the one, not the file beside the program.</summary>
    [Test]
    public void A_failure_at_start_names_the_settings_file_that_was_read()
    {
        using var user = new TempFolder();
        File.WriteAllText(user.File(SettingsFile.FileName), "{}");
        var failure = new InvalidOperationException("The configuration of the console log failed.");

        var explanation = AppHost.DescribeFailure(failure, new SettingsPlaces(_folder.Path, user.Path));

        Assert.That(explanation, Does.Contain($"Check the settings in {user.File(SettingsFile.FileName)}.").And.Not.Contain(SettingsPath));
    }

    // ------------------------------------------------------------------ the recorder's host

    /// <summary>What a window hands the recorder it starts: the run, its request, and the settings given, by name.</summary>
    private static RecorderStart Start(Dictionary<string, string?> settings)
    {
        var runId = TestRuns.Id();
        return new RecorderStart(runId, TestRuns.Request(runId), settings);
    }

    /// <summary>Builds the recorder's host from <paramref name="settings"/> and <paramref name="apiKey"/>, over <paramref name="log"/> and the test's folder as ClassScribe's own and as the program's.</summary>
    private IHost BuildRecorder(Dictionary<string, string?> settings, string? apiKey = "test-key", RecorderLog? log = null, Action<IServiceCollection>? configureServices = null) =>
        AppHost.BuildRecorder(Start(settings), apiKey, log ?? new RecorderLog(TimeProvider.System), new RuntimeFolder(_folder.Path), Places(), configureServices: configureServices);

    /// <summary>
    /// The recorder's host holds the settings handed to it and the key from its own environment, and nothing else: not the
    /// appsettings.json beside the exe, which the window has read and checked already (here the shipped one, which names
    /// a recordings folder the snapshot leaves out), and not the environment variables, whose overrides the snapshot
    /// carries already.
    /// </summary>
    [Test]
    public void The_recorders_host_holds_only_the_snapshot_and_the_key()
    {
        using var variable = new TemporaryVariable("Recording__SilenceWarningSeconds", "7");
        Assert.That(File.Exists(Path.Combine(AppContext.BaseDirectory, SettingsFile.FileName)), Is.True, "the shipped appsettings.json is beside this test's exe");

        using var host = BuildRecorder(new() { ["Recording:FlushSeconds"] = "8", ["ElevenLabs:BaseUrl"] = "http://127.0.0.1:9/" }, apiKey: "key-from-variable");

        var recording = host.Services.GetRequiredService<IOptionsMonitor<RecordingOptions>>().CurrentValue;
        var elevenLabs = host.Services.GetRequiredService<IOptionsMonitor<ElevenLabsOptions>>().CurrentValue;
        Assert.That(recording.FlushSeconds, Is.EqualTo(8), "from the snapshot");
        Assert.That(recording.OutputFolder, Is.Empty, "the file beside the exe is not read");
        Assert.That(recording.SilenceWarningSeconds, Is.EqualTo(60), "the environment is not read");
        Assert.That(elevenLabs.BaseUrl, Is.EqualTo("http://127.0.0.1:9/"), "a stub the window was given reaches the recorder");
        Assert.That(elevenLabs.ApiKey, Is.EqualTo("key-from-variable"));
    }

    /// <summary>Without a key in its environment, the recorder has none, whatever the snapshot could say: the snapshot never carries one.</summary>
    [Test]
    public void Without_a_key_in_its_environment_the_recorder_has_none()
    {
        using var host = BuildRecorder(new() { ["ElevenLabs:ApiKey"] = "key-in-snapshot" }, apiKey: null);

        Assert.That(host.Services.GetRequiredService<IOptionsMonitor<ElevenLabsOptions>>().CurrentValue.ApiKey, Is.Empty);
    }

    /// <summary>
    /// The recorder has no console, so its host has no console logger: every logger writes into the recorder's log, the
    /// one given, and nowhere else, at the levels the snapshot's Logging section names.
    /// </summary>
    [Test]
    public void The_recorders_host_logs_only_into_the_recorders_log()
    {
        using var log = new RecorderLog(TimeProvider.System);
        using var host = BuildRecorder(new() { ["Logging:LogLevel:Default"] = "Warning" }, log: log);
        var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("ClassScribe.Tests");

        logger.LogWarning("A line for the windows");
        logger.LogInformation("A line below the level");

        Assert.That(host.Services.GetServices<ILoggerProvider>().Select(provider => provider.GetType()), Is.EqualTo(new[] { typeof(RecorderLogProvider) }));
        Assert.That(host.Services.GetRequiredService<RecorderLog>(), Is.SameAs(log));
        Assert.That(log.Since(0).Lines.Select(line => line.Text), Has.One.Contains("A line for the windows").And.None.Contains("below the level"));
    }

    /// <summary>
    /// The recorder's host has the recorder's own services in place of the window's: the link's console for the keys, the
    /// link's status sink and the recorder's result shower for the run, the server, the alerts, the process starter for
    /// the alert window, no window signals, and ClassScribe's own folder and the clock it was given; and a per-run
    /// service cannot be taken outside a run. Resolving the run constructs what it records with, so what reaches Windows
    /// there (the session's window, the audio capture, the power and the clocks) is replaced by stand-ins: this test is
    /// about the registrations alone.
    /// </summary>
    [Test]
    public void The_recorders_host_has_the_recorders_own_services()
    {
        var folder = new RuntimeFolder(_folder.Path);
        var time = new ManualTimeProvider(TestRuns.Start);
        using var host = AppHost.BuildRecorder(Start([]), "test-key", new RecorderLog(time), folder, Places(), time, recorder =>
        {
            recorder.Replace(ServiceDescriptor.Scoped<ISessionEvents>(_ => new FakeSessionEvents()));
            recorder.Replace(ServiceDescriptor.Transient<IRecordingFactory>(_ => new FakeRecordingFactory()));
            recorder.Replace(ServiceDescriptor.Singleton<IPowerRequests>(new FakePowerRequests()));
            recorder.Replace(ServiceDescriptor.Singleton<IPowerSource>(new FakePowerSource()));
            recorder.Replace(ServiceDescriptor.Singleton<ISystemTimes>(new FakeSystemTimes(time)));
            recorder.AddSingleton(new RoamingFolder(_folder.File("Roaming")));   // never the real %APPDATA%\ClassScribe
        });
        var services = host.Services;
        using var scope = services.CreateScope();
        var run = scope.ServiceProvider;

        Assert.That(services.GetRequiredService<IConsoleUi>(), Is.InstanceOf<LinkConsoleUi>());
        Assert.That(services.GetRequiredService<RecorderServer>(), Is.SameAs(services.GetRequiredService<RecorderServer>()), "one server for the run");
        Assert.That(services.GetRequiredService<UnattendedAlerts>(), Is.SameAs(services.GetRequiredService<UnattendedAlerts>()));
        Assert.That(services.GetRequiredService<IWindowSignals>(), Is.InstanceOf<NoWindowSignals>(), "a recorder has no window");
        Assert.That(services.GetRequiredService<RuntimeFolder>(), Is.SameAs(folder));
        Assert.That(services.GetRequiredService<TimeProvider>(), Is.SameAs(time));
        if (OperatingSystem.IsWindows()) Assert.That(services.GetRequiredService<IProcessStarter>(), Is.InstanceOf<ProcessStarter>());
        Assert.That(run.GetRequiredService<IRecorderStatusSink>(), Is.InstanceOf<LinkStatusSink>());
        Assert.That(run.GetRequiredService<IResultShower>(), Is.InstanceOf<RecorderResultShower>());
        Assert.That(run.GetRequiredService<IResultShower>(), Is.SameAs(run.GetRequiredService<RecorderResultShower>()), "one shower per run, which the host asks at the end");
        Assert.That(run.GetRequiredService<IRecordRun>(), Is.InstanceOf<RecordCommand>());
        Assert.That(run.GetRequiredService<RunBookkeeping>(), Is.Not.Null);
        Assert.That(() => services.GetRequiredService<IUserSignals>(), Throws.InvalidOperationException);
    }

    /// <summary>A test can put stand-ins in place last, as the window's host allows too.</summary>
    [Test]
    public void A_test_can_replace_the_recorders_services()
    {
        var starter = new FakeProcessStarter();

        using var host = BuildRecorder([], configureServices: services => services.AddSingleton<IProcessStarter>(starter));

        Assert.That(host.Services.GetRequiredService<IProcessStarter>(), Is.SameAs(starter));
    }

    /// <summary>
    /// The settings handed over have passed the window's check, so a value that fails it here means a build mismatch: the
    /// recorder's host refuses it, naming the setting, rather than record with a setting it cannot read.
    /// </summary>
    [Test]
    public void The_recorders_host_refuses_settings_that_fail_the_check()
    {
        var failure = Assert.Throws<InvalidOperationException>(() => BuildRecorder(new() { ["Recording:FlushSeconds"] = "abc" }).Dispose())!;

        Assert.That(failure.Message, Does.Contain("Recording:FlushSeconds is \"abc\""));
    }
}
