using ClassScribe.Audio;
using ClassScribe.Commands;
using ClassScribe.Desktop;
using ClassScribe.Settings;
using ClassScribe.Storage;
using ClassScribe.Transcription;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ClassScribe;

/// <summary>Registers the app's services, one area at a time, each from its own registration file.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds everything ClassScribe needs, bound to the given configuration. The settings file and what reads it again
    /// before each run are added by AppHost (see <see cref="SettingsServices.AddSettingsReload"/>), which builds the
    /// configuration they work on; the gate, the menu and the app need them.
    /// </summary>
    public static IServiceCollection AddClassScribe(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton(TimeProvider.System);
        return services
            .AddSettings(configuration)
            .AddStorage()
            .AddAudio()
            .AddTranscription()
            .AddDesktop()
            .AddCommands();
    }
}
