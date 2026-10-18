using ClassScribe.Commands;
using ClassScribe.Desktop;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ClassScribe.YouTube;

/// <summary>Registers the YouTube feature's services: what both paths share and the download path.</summary>
public static class YouTubeServices
{
    /// <summary>
    /// Adds the feature: its folders, the lock, the tools' locator and runner, the check, and per batch (each in a scope of its
    /// own) the pace, the wait for a class, the conversion, the transcripts (each again in a scope of its own), the batch and
    /// its command and menu. The runner reaches Windows' own start of a process, and so needs Windows.
    /// </summary>
    public static IServiceCollection AddYouTube(this IServiceCollection services)
    {
        services.AddSingleton<YouTubeFolders>();
        services.AddSingleton<YouTubeLock>();
        services.AddSingleton<IFileProbe, FileProbe>();
        services.AddSingleton<ToolLocator>();
        services.AddSingleton<IToolRunner>(provider => OperatingSystem.IsWindows()
            ? new ProcessToolRunner(provider.GetRequiredService<ILogger<ProcessToolRunner>>())
            : throw new PlatformNotSupportedException("Running yt-dlp needs Windows."));
        services.AddSingleton<YouTubeMenuMemory>();

        services.AddTransient<YtDlpCheck>();
        services.AddScoped<IBatchPace>(provider => new BatchPace(provider.GetRequiredService<TimeProvider>()));
        services.AddScoped<ClassWait>();
        services.AddScoped<DownloadedAudio>();
        services.AddScoped<TranscriptionRuns>();
        services.AddScoped<VideoTranscriptRun>();
        services.AddScoped<IVideoTranscriber, ScopedVideoTranscriber>();
        services.AddScoped<BatchTranscripts>();
        services.AddScoped<YouTubeBatch>();
        services.AddScoped<DownloadBatch>();
        services.AddScoped<YouTubeCommand>();
        services.AddScoped<YouTubeMenu>();
        return services;
    }
}
