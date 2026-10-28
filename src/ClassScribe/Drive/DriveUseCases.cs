using ClassScribe.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace ClassScribe.Drive;

/// <summary>
/// Registers Google Drive's use cases and the services they drive (architecture 5.2): the sign-in, the retries, the upload
/// of a recording and the pass over the queue, the freeing of room on the laptop, what the rest of the app reads of audio
/// kept on Drive alone, the uploads that happen by themselves after a run, a lesson's video with Drive, Drive's room for a
/// YouTube plan, and what the menu and the report at the start say of the queue. What reaches Google, Windows and the disk
/// is registered with the adapters.
/// </summary>
public static class DriveUseCases
{
    /// <summary>Adds Google Drive's use cases and the services they drive.</summary>
    public static IServiceCollection AddDriveUseCases(this IServiceCollection services)
    {
        // The sign-in, in the window only.
        services.AddTransient<GoogleSignIn>();

        // The uploads: what the rest of the app reads of audio kept on Drive alone, one for the process; the steps, one per
        // run, over that run's Drive client and settings.
        services.AddSingleton<IAudioOnDrive, DriveCopies>();
        services.AddTransient<DriveRetry>();
        services.AddTransient<RecordingUpload>();
        services.AddTransient<DriveUploadPass>();
        services.AddTransient<LocalAudioFreer>();

        // The uploads that happen by themselves: one per run, shared by the run, which queues its recording and notes how it
        // ended, and by whoever ran it, which uploads after it; the pass is made only when one runs.
        services.AddTransient(provider => new Lazy<DriveUploadPass>(provider.GetRequiredService<DriveUploadPass>));
        services.AddScoped<IDriveAutoUpload, DriveAutoUpload>();

        // What becomes of a lesson's video and its camera pieces with Drive, as the run's settings have it.
        services.AddScoped<LessonVideoDrive>();

        // Drive's room, for a YouTube batch's plan when the videos' audio goes up.
        services.AddTransient<IDriveRoom, DriveRoom>();

        // What the menu's header and the report at the start say of the queue, from the store and the manifests alone.
        services.AddSingleton<DriveQueueReport>();
        return services;
    }
}