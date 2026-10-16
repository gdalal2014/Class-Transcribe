using System.Text.Json.Nodes;
using ClassScribe.Audio;
using ClassScribe.Commands;
using ClassScribe.Settings;
using ClassScribe.Storage;
using ClassScribe.Transcription;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Refit;

namespace ClassScribe.Tests;

/// <summary>Dependency injection wiring and settings binding, through the app as Program puts it together.</summary>
[TestFixture]
public class ServiceRegistrationTests
{
    private TempFolder _folder = null!;

    /// <summary>Creates the folder that holds the app's appsettings.json.</summary>
    [SetUp]
    public void SetUp() => _folder = new TempFolder();

    /// <summary>Deletes the folder.</summary>
    [TearDown]
    public void TearDown() => _folder.Dispose();

    /// <summary>
    /// Writes the settings, given as setting names and values such as Recording:FlushSeconds, into an appsettings.json
    /// and builds the app over it, the way Program builds it. The host validates scopes, so a per-run service taken from
    /// the root fails here.
    /// </summary>
    private IHost BuildApp(Dictionary<string, string?> settings, string? apiKey = "test-key")
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
        return AppHost.Build(_folder.Path, apiKey);
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
        Assert.That(provider.GetRequiredService<SourceChoice>().Device, Is.EqualTo("Pixel Buds"), "the choice starts from the setting");
        Assert.That(provider.GetRequiredService<ClassFolders>().Root, Is.EqualTo("D:\\Classes"));
        Assert.That(recording.SilenceWarningSeconds, Is.EqualTo(60), "defaults survive for settings that are not given");
        Assert.That(recording.OpenFolderWhenDone, Is.True);
        Assert.That(elevenLabs.TimeoutMinutes, Is.EqualTo(90));
        Assert.That(elevenLabs.SlowestUploadMbps, Is.EqualTo(2.5));
        Assert.That(elevenLabs.ModelId, Is.EqualTo("scribe_v2"));
        Assert.That(transcript.IncludeTimestamps, Is.False);
    }

    /// <summary>Settings added for phase 1 have their defaults when the file leaves them out: the screen on battery only, and 1 Mbps.</summary>
    [Test]
    public void The_new_settings_have_their_defaults()
    {
        using var app = BuildApp([]);

        Assert.That(app.Services.GetRequiredService<IOptionsMonitor<RecordingOptions>>().CurrentValue.KeepScreenOn, Is.EqualTo(KeepScreenOn.OnBattery));
        Assert.That(app.Services.GetRequiredService<IOptionsMonitor<ElevenLabsOptions>>().CurrentValue.SlowestUploadMbps, Is.EqualTo(1));
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

    /// <summary>An unknown command shows the usage and fails with exit code 2.</summary>
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
}
