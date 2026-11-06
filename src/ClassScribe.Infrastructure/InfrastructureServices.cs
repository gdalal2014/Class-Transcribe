using ClassScribe.Infrastructure.Audio;
using ClassScribe.Infrastructure.Audio.Microphones;
using ClassScribe.Infrastructure.Camera;
using ClassScribe.Infrastructure.Desktop;
using ClassScribe.Infrastructure.Drive;
using ClassScribe.Infrastructure.Recorder;
using ClassScribe.Infrastructure.Settings;
using ClassScribe.Infrastructure.Storage;
using ClassScribe.Infrastructure.Transcription;
using ClassScribe.Infrastructure.YouTube;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ClassScribe.Infrastructure;

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