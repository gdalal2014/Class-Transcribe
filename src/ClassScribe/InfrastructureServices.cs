using ClassScribe.Audio;
using ClassScribe.Audio.Microphones;
using ClassScribe.Camera;
using ClassScribe.Desktop;
using ClassScribe.Drive;
using ClassScribe.Recorder;
using ClassScribe.Settings;
using ClassScribe.Storage;
using ClassScribe.Transcription;
using ClassScribe.YouTube;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ClassScribe;

/// <summary>
/// Registers what Infrastructure owns (architecture 5.2), one area at a time, each from its own registration: the settings
/// bound to their sections, the storage, Windows audio and the microphone (each made only when first used), the camera, the
/// transcription's client, Google Drive's, YouTube's tools and clients, the Windows integration and the recorder's log;
/// every adapter, its internal types included, so that nothing outside needs them.
/// </summary>
public static class InfrastructureServices
{
    /// <summary>Adds the adapters, bound to <paramref name="configuration"/>.</summary>
    public static IServiceCollection AddClassScribeInfrastructure(this IServiceCollection services, IConfiguration configuration) =>
        services
            .AddSettings(configuration)
            .AddStorage()
            .AddAudio()
            .AddMicrophone()
            .AddCamera()
            .AddTranscription()
            .AddDrive()
            .AddYouTube()
            .AddDesktop()
            .AddRecorderLog();
}