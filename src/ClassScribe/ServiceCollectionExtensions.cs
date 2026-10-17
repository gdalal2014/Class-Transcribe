using ClassScribe.Audio;
using ClassScribe.Audio.Microphones;
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
    /// Adds everything ClassScribe needs, bound to the given configuration: the settings, the storage, Windows audio and
    /// the microphone (each made only when first used), the transcription, the Windows integration, the commands and what
    /// a run needs in either home, and the window's side of a run in the background, last, since it replaces the stand-in
    /// report the commands add. The link to a recorder needs no registration: the view of a run makes its own client, and
    /// the server is the recorder's. The settings file and what reads it again before each run are added by AppHost (see
    /// <see cref="SettingsServices.AddSettingsReload"/>), which builds the configuration they work on; the gate, the menu
    /// and the app need them. The recorder's host adds its own services after these (see <see cref="AppHost.BuildRecorder"/>).
    /// </summary>
    public static IServiceCollection AddClassScribe(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton(TimeProvider.System);
        return services
            .AddSettings(configuration)
            .AddStorage()
            .AddAudio()
            .AddMicrophone()
            .AddTranscription()
            .AddDesktop()
            .AddCommands()
            .AddWindow();
    }
}
