using ClassScribe.Audio;
using ClassScribe.Camera;
using ClassScribe.Commands;
using ClassScribe.Drive;
using ClassScribe.Transcription;
using ClassScribe.YouTube;
using Microsoft.Extensions.DependencyInjection;

namespace ClassScribe;

/// <summary>
/// Registers what the use cases own (architecture 5.2), one area at a time, each from its own registration: the use cases
/// and the services they drive, scoped or one for the process as each needs. The adapters they reach Windows, the vendors
/// and the disk through are registered apart, by Infrastructure's own registration.
/// </summary>
public static class ApplicationServices
{
    /// <summary>Adds the use cases of what records, the camera, the transcription, Google Drive, YouTube videos and the commands.</summary>
    public static IServiceCollection AddClassScribeApplication(this IServiceCollection services) =>
        services
            .AddAudioUseCases()
            .AddCameraUseCases()
            .AddTranscriptionUseCases()
            .AddDriveUseCases()
            .AddYouTubeUseCases()
            .AddCommandUseCases();
}