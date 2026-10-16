using ClassScribe.Settings;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moq;

namespace ClassScribe.Tests;

/// <summary>How the app is put together at start-up: where the key comes from, unreadable settings, and the check that runs first.</summary>
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

    /// <summary>Writes the appsettings.json the host is built from.</summary>
    private void WriteSettings(string json) => File.WriteAllText(_folder.File("appsettings.json"), json);

    /// <summary>The key comes from ELEVENLABS_API_KEY: one written into appsettings.json is not used.</summary>
    [Test]
    public void The_key_from_the_environment_variable_wins_over_one_in_appsettings_json()
    {
        WriteSettings("""{ "ElevenLabs": { "ApiKey": "key-in-file" } }""");

        using var host = AppHost.Build(_folder.Path, apiKey: "key-from-variable");

        Assert.That(host.Services.GetRequiredService<IOptions<ElevenLabsOptions>>().Value.ApiKey, Is.EqualTo("key-from-variable"));
    }

    /// <summary>Without ELEVENLABS_API_KEY there is no key at all, even when appsettings.json has one: that one is never used.</summary>
    [Test]
    public void Without_the_variable_a_key_in_appsettings_json_is_still_not_used()
    {
        WriteSettings("""{ "ElevenLabs": { "ApiKey": "key-in-file" } }""");

        using var host = AppHost.Build(_folder.Path, apiKey: null);

        Assert.That(host.Services.GetRequiredService<IOptions<ElevenLabsOptions>>().Value.ApiKey, Is.Empty);
    }

    /// <summary>
    /// A hand-edited appsettings.json that is not valid JSON, typically a path written with single
    /// backslashes, stops the start with a plain explanation: which file, which line, and how to write
    /// a path in JSON. No stack trace.
    /// </summary>
    [Test]
    public void Settings_that_are_not_valid_JSON_are_explained_with_how_to_write_a_path()
    {
        WriteSettings("""
            {
              "Recording": {
                "OutputFolder": "D:\Classes"
              }
            }
            """);

        var failure = Assert.Catch(() => AppHost.Build(_folder.Path, apiKey: null).Dispose())!;
        var explanation = AppHost.DescribeFailure(failure, _folder.Path);

        Assert.That(explanation, Does.Contain(_folder.File("appsettings.json")).And.Contain("line 3"));
        Assert.That(explanation, Does.Contain(@"""D:\\Classes""").And.Contain(@"""D:/Classes"""), "says how to write a path in JSON");
        Assert.That(explanation, Does.Not.Contain("   at "), "no stack trace");
    }

    /// <summary>The appsettings.json that ships passes the settings check, and holds the log format setting the README lists.</summary>
    [Test]
    public void The_shipped_settings_pass_the_check_and_hold_the_log_format()
    {
        using var host = AppHost.Build(AppContext.BaseDirectory, apiKey: "test-key");

        Assert.That(host.Services.GetRequiredService<SettingsCheck>().FindProblems(), Is.Empty);
        Assert.That(host.Services.GetRequiredService<IConfiguration>()["Logging:Console:FormatterName"], Is.EqualTo(PlainConsoleFormatter.FormatterName));
    }

    /// <summary>
    /// The settings are read once, at start, where they are checked. A value edited in appsettings.json
    /// while ClassScribe runs, as the menu stays open from class to class, is not used: here a comma
    /// decimal the check never saw, which would otherwise stop the transcript after the paid upload. The
    /// edit counts from the next start, where it is checked.
    /// </summary>
    [Test]
    public async Task A_setting_edited_while_ClassScribe_runs_is_not_used()
    {
        WriteSettings("""{ "Transcript": { "ParagraphPauseSeconds": 3.5 } }""");
        using var host = AppHost.Build(_folder.Path, apiKey: "test-key");
        Assert.That(host.Services.GetRequiredService<SettingsCheck>().AreUsable(), Is.True);

        // A second reader of the file, which watches it for changes as a host does by default, shows
        // when the edit has been seen; the half second after it is time for any other reader to see it too.
        using var watcher = new ConfigurationManager();
        watcher.AddJsonFile(_folder.File("appsettings.json"), optional: false, reloadOnChange: true);
        WriteSettings("""{ "Transcript": { "ParagraphPauseSeconds": "1,5" } }""");
        await Wait.UntilAsync(() => watcher["Transcript:ParagraphPauseSeconds"] == "1,5", timeoutSeconds: 10);
        await Task.Delay(500);

        Assert.That(host.Services.GetRequiredService<IOptions<TranscriptOptions>>().Value.ParagraphPauseSeconds, Is.EqualTo(3.5));
    }

    /// <summary>Settings that cannot be used stop the run before anything starts: exit code 1, and nothing is uploaded or paid for.</summary>
    [Test]
    public async Task Settings_that_cannot_be_used_stop_the_run_before_anything_is_sent()
    {
        var client = Mocks.TranscriptionClient();
        var video = _folder.File("lecture.mp4");
        File.WriteAllBytes(video, [1, 2, 3]);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Recording:OutputFolder"] = _folder.File("Recordings"),
                ["Transcript:ParagraphPauseSeconds"] = "1,5"
            })
            .AddApiKeyFromEnvironment("test-key")
            .Build();
        await using var services = new ServiceCollection()
            .AddLogging()
            .AddClassScribe(configuration)
            .AddSingleton(client.Object)
            .BuildServiceProvider();

        var exitCode = await AppHost.RunAsync(services, ["transcribe", video]);

        Assert.That(exitCode, Is.EqualTo(1));
        client.Verify(Calls.AnyTranscription, Times.Never);
    }
}
