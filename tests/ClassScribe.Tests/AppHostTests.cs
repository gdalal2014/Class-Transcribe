using ClassScribe.Commands;
using ClassScribe.Settings;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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

    /// <summary>Writes the appsettings.json the host is built from.</summary>
    private void WriteSettings(string json) => File.WriteAllText(SettingsPath, json);

    /// <summary>The key comes from ELEVENLABS_API_KEY: one written into appsettings.json is not used.</summary>
    [Test]
    public void The_key_from_the_environment_variable_wins_over_one_in_appsettings_json()
    {
        WriteSettings("""{ "ElevenLabs": { "ApiKey": "key-in-file" } }""");

        using var host = AppHost.Build(_folder.Path, apiKey: "key-from-variable");

        Assert.That(host.Services.GetRequiredService<IOptionsMonitor<ElevenLabsOptions>>().CurrentValue.ApiKey, Is.EqualTo("key-from-variable"));
    }

    /// <summary>Without ELEVENLABS_API_KEY there is no key at all, even when appsettings.json has one: that one is never used.</summary>
    [Test]
    public void Without_the_variable_a_key_in_appsettings_json_is_still_not_used()
    {
        WriteSettings("""{ "ElevenLabs": { "ApiKey": "key-in-file" } }""");

        using var host = AppHost.Build(_folder.Path, apiKey: null);

        Assert.That(host.Services.GetRequiredService<IOptionsMonitor<ElevenLabsOptions>>().CurrentValue.ApiKey, Is.Empty);
    }

    /// <summary>The settings that pass the check at start are used from the start: the host reads the file once as it is put together.</summary>
    [Test]
    public void Settings_that_pass_are_used_from_the_start()
    {
        WriteSettings("""{ "Recording": { "FlushSeconds": 8 }, "Transcript": { "ParagraphPauseSeconds": 3.5 } }""");

        using var host = AppHost.Build(_folder.Path, apiKey: "test-key");

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

        using var host = AppHost.Build(_folder.Path, apiKey: null);
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
        using var host = AppHost.Build(AppContext.BaseDirectory, apiKey: "test-key");

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
    /// The settings are read again before each menu choice and each command, never in between: a value edited in
    /// appsettings.json while ClassScribe runs, as the menu stays open from class to class, is not taken until the next
    /// choice, and then only once it has passed the check. Here a comma decimal, which would otherwise stop the
    /// transcript after the paid upload, is refused and the value before it stays; the corrected edit is then taken.
    /// </summary>
    [Test]
    public async Task A_setting_edited_while_ClassScribe_runs_counts_from_the_next_choice_once_checked()
    {
        WriteSettings("""{ "Transcript": { "ParagraphPauseSeconds": 3.5 } }""");
        using var host = AppHost.Build(_folder.Path, apiKey: "test-key");
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
        using var host = AppHost.Build(_folder.Path, apiKey: "test-key");
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

        var failure = Assert.Catch(() => AppHost.Build(_folder.Path, apiKey: "test-key").Dispose())!;
        var explanation = AppHost.DescribeFailure(failure, _folder.Path);

        Assert.That(explanation, Does.StartWith("ClassScribe could not start."));
        Assert.That(explanation, Does.Contain("Recording:FlushSeconds is \"abc\", which is not a whole number").And.Contain("environment variable"));
        Assert.That(explanation, Does.Not.Contain("   at "), "no stack trace");
    }

    /// <summary>The host checks that every per-run service is taken inside a run's scope, so a slip shows at once rather than as a leak.</summary>
    [Test]
    public void A_per_run_service_cannot_be_taken_outside_a_run()
    {
        using var host = AppHost.Build(_folder.Path, apiKey: "test-key");

        Assert.That(() => host.Services.GetRequiredService<IUserSignals>(), Throws.InvalidOperationException);
    }

    /// <summary>A failure while ClassScribe is put together is explained in plain words: one line per exception, and the settings file for a configuration error.</summary>
    [Test]
    public void A_failure_at_start_is_explained_in_plain_words()
    {
        var failure = new InvalidOperationException("The configuration of the console log failed.", new FormatException("Loud is not a level."));

        var explanation = AppHost.DescribeFailure(failure, _folder.Path);

        Assert.That(explanation, Does.StartWith("ClassScribe could not start."));
        Assert.That(explanation, Does.Contain("The configuration of the console log failed.").And.Contain("Loud is not a level."));
        Assert.That(explanation, Does.Contain(SettingsPath));
    }
}
