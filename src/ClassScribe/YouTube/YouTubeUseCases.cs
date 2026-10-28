using ClassScribe.Commands;
using Microsoft.Extensions.DependencyInjection;

namespace ClassScribe.YouTube;

/// <summary>
/// Registers the YouTube feature's use cases and the services they drive (architecture 5.2), for both paths: per batch,
/// each in a scope of its own, the window a video's run records, the pace, the wait for a class, the transcripts (each again
/// in a scope of its own), the batches and their command. What reaches Windows, the tools, YouTube and the disk is
/// registered with the adapters, and the menu with the window's.
/// </summary>
public static class YouTubeUseCases
{
    /// <summary>Adds the YouTube feature's use cases and the services they drive.</summary>
    public static IServiceCollection AddYouTubeUseCases(this IServiceCollection services)
    {
        // The record path: one per run, the window a video's run records, which a class's run reads to leave video windows out.
        services.AddScoped<VideoPlayback>();

        services.AddScoped<IBatchPace>(provider => new BatchPace(provider.GetRequiredService<TimeProvider>()));
        services.AddScoped<ClassWait>();
        services.AddScoped<TranscriptionRuns>();
        services.AddScoped<VideoTranscriptRun>();
        services.AddScoped<IVideoTranscriber, ScopedVideoTranscriber>();
        services.AddScoped<BatchTranscripts>();
        services.AddScoped<YouTubeBatch>();
        services.AddScoped<DownloadBatch>();
        services.AddScoped<VideoBatch>();
        services.AddScoped<YouTubeCommand>();
        return services;
    }
}