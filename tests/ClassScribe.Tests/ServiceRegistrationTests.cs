using ClassScribe.Commands;
using ClassScribe.Settings;
using ClassScribe.Transcription;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Refit;

namespace ClassScribe.Tests;

/// <summary>Dependency injection wiring and settings binding.</summary>
[TestFixture]
public class ServiceRegistrationTests
{
    /// <summary>
    /// Builds the services from in-memory settings, the way Program builds them from its configuration.
    /// Scopes are validated, so a per-run service taken from the root would fail here.
    /// </summary>
    private static ServiceProvider BuildProvider(Dictionary<string, string?> settings)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        return new ServiceCollection()
            .AddLogging()
            .AddClassScribe(configuration)
            .BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    /// <summary>Every setting section binds, and settings that are not given keep their defaults.</summary>
    [Test]
    public void Settings_are_bound_from_configuration()
    {
        using var provider = BuildProvider(new()
        {
            ["Recording:FlushSeconds"] = "9",
            ["Recording:OutputFolder"] = "D:\\Classes",
            ["Recording:OpenTranscriptWhenDone"] = "false",
            ["ElevenLabs:ApiKey"] = "from-config",
            ["ElevenLabs:TimeoutMinutes"] = "90",
            ["Transcript:IncludeTimestamps"] = "false"
        });

        var recording = provider.GetRequiredService<IOptions<RecordingOptions>>().Value;
        var elevenLabs = provider.GetRequiredService<IOptions<ElevenLabsOptions>>().Value;
        var transcript = provider.GetRequiredService<IOptions<TranscriptOptions>>().Value;

        Assert.That(recording.FlushSeconds, Is.EqualTo(9));
        Assert.That(recording.OutputFolder, Is.EqualTo("D:\\Classes"));
        Assert.That(recording.OpenTranscriptWhenDone, Is.False);
        Assert.That(recording.SilenceWarningSeconds, Is.EqualTo(60), "defaults survive for settings that are not given");
        Assert.That(recording.OpenFolderWhenDone, Is.True);
        Assert.That(elevenLabs.ApiKey, Is.EqualTo("from-config"));
        Assert.That(elevenLabs.TimeoutMinutes, Is.EqualTo(90));
        Assert.That(elevenLabs.ModelId, Is.EqualTo("scribe_v2"));
        Assert.That(transcript.IncludeTimestamps, Is.False);
    }

    /// <summary>The app, the menu and the transcribe command resolve without an audio device; commands resolve inside a run's scope.</summary>
    [Test]
    public void The_app_and_the_transcribe_command_resolve_without_an_audio_device()
    {
        using var provider = BuildProvider(new() { ["ElevenLabs:ApiKey"] = "k" });
        using var scope = provider.CreateScope();

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
        using var provider = BuildProvider(new() { ["ElevenLabs:ApiKey"] = "k" });
        using var first = provider.CreateScope();
        using var second = provider.CreateScope();

        Assert.That(first.ServiceProvider.GetRequiredService<IUserSignals>(), Is.Not.SameAs(second.ServiceProvider.GetRequiredService<IUserSignals>()));
    }

    /// <summary>The registered ElevenLabs client lets network errors through unwrapped, so they are recognised and retried.</summary>
    [Test]
    public void The_ElevenLabs_client_reports_send_failures_unwrapped()
    {
        using var provider = BuildProvider(new() { ["ElevenLabs:ApiKey"] = "k" });
        var failure = new HttpRequestException("Connection reset.");

        var settings = provider.GetRequiredService<SettingsFor<IElevenLabsApi>>().Settings!;

        Assert.That(settings.TransportExceptionFactory(new HttpRequestMessage(), failure, CancellationToken.None), Is.SameAs(failure));
    }

    /// <summary>An unknown command shows the usage and fails with exit code 2.</summary>
    [Test]
    public async Task An_unknown_command_shows_the_usage_and_fails()
    {
        using var provider = BuildProvider(new() { ["ElevenLabs:ApiKey"] = "k" });

        var exitCode = await provider.GetRequiredService<App>().RunAsync(["frobnicate"]);

        Assert.That(exitCode, Is.EqualTo(2));
    }

    /// <summary>Without a key, transcribing stops before any work is done.</summary>
    [Test]
    public async Task Transcribing_without_a_key_fails_before_any_work()
    {
        using var provider = BuildProvider(new());

        var exitCode = await provider.GetRequiredService<App>().RunAsync(["transcribe", Path.GetTempPath()]);

        Assert.That(exitCode, Is.EqualTo(1));
    }

    /// <summary>A path that leads nowhere is reported as a failure, not a crash.</summary>
    [Test]
    public async Task Transcribing_a_missing_file_fails_cleanly()
    {
        using var provider = BuildProvider(new() { ["ElevenLabs:ApiKey"] = "k" });

        var exitCode = await provider.GetRequiredService<App>().RunAsync(["transcribe", Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".wav")]);

        Assert.That(exitCode, Is.EqualTo(1));
    }
}
