using ClassScribe.Drive;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ClassScribe.Settings;

/// <summary>Registers the settings: their sections, their check, and what reads appsettings.json again before each run.</summary>
public static class SettingsServices
{
    /// <summary>
    /// Binds the Recording, ElevenLabs, Transcript, Drive and YouTube sections of <paramref name="configuration"/>, and adds the check.
    /// Long-lived parts read them through IOptionsMonitor when they use them, and the parts of a run through
    /// IOptionsSnapshot, computed once in the run's own scope, so a change counts from the next run.
    /// </summary>
    public static IServiceCollection AddSettings(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<RecordingOptions>(configuration.GetSection(RecordingOptions.SectionName));
        services.Configure<ElevenLabsOptions>(configuration.GetSection(ElevenLabsOptions.SectionName));
        services.Configure<TranscriptOptions>(configuration.GetSection(TranscriptOptions.SectionName));
        services.Configure<DriveOptions>(configuration.GetSection(DriveOptions.SectionName));
        services.Configure<YouTubeOptions>(configuration.GetSection(YouTubeOptions.SectionName));
        services.AddSingleton<SettingsCheck>();
        return services;
    }

    /// <summary>
    /// Adds <paramref name="places"/>, the settings file they name, and what reads it again into <paramref name="configuration"/>,
    /// through <paramref name="accepted"/>, which must be one of its sources. AppHost adds them, since it builds that configuration.
    /// </summary>
    public static IServiceCollection AddSettingsReload(this IServiceCollection services, ConfigurationManager configuration, CheckedSettingsSource accepted, SettingsPlaces places)
    {
        if (!configuration.Sources.Contains(accepted))
            throw new InvalidOperationException("The settings to reload must take appsettings.json through the CheckedSettingsSource given; see AppHost.Build.");

        services.AddSingleton(accepted);
        services.AddSingleton(places);
        services.AddSingleton(provider => new SettingsFile(places, provider.GetRequiredService<ILogger<SettingsFile>>()));
        services.AddSingleton(provider => new SettingsReloader(
            configuration,
            accepted,
            provider.GetRequiredService<SettingsFile>(),
            provider.GetRequiredService<SettingsCheck>(),
            provider.GetRequiredService<ILogger<SettingsReloader>>()));
        return services;
    }
}
