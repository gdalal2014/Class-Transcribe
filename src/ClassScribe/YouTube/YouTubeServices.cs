using ClassScribe.Commands;
using ClassScribe.Desktop;
using ClassScribe.Recorder;
using ClassScribe.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Refit;

namespace ClassScribe.YouTube;

/// <summary>Registers the YouTube feature's services: what both paths share, the download path and the record path.</summary>
public static class YouTubeServices
{
    /// <summary>
    /// Adds the feature: its folders, the lock, the tools' locator and runner, the check, the oEmbed client for the record
    /// path's titles, and per batch (each in a scope of its own) the pace, the wait for a class, the conversion, the
    /// transcripts (each again in a scope of its own), the titles, the batches and their command and menu. The runner reaches
    /// Windows' own start of a process, and so needs Windows.
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

        // YouTube's public oEmbed address, for the record path's titles: its base address read from the settings each time a
        // client is made, once per batch; HttpClient's own limit off, as the reader holds its own; a transport of its own that
        // keeps no cookie, so that a cookie one answer sets never goes back with the next request and links the batch's reads;
        // the guard that keeps a test away from youtube.com itself; and nothing of these requests logged by
        // Microsoft.Extensions.Http.
        services
            .AddRefitGeneratedClient<IYouTubeOEmbedApi>(YouTubeJson.Default, _ => OEmbedTitles.RefitSettings())
            .ConfigureHttpClient((provider, http) =>
            {
                var settings = provider.GetRequiredService<IOptionsMonitor<YouTubeOptions>>().CurrentValue;
                http.BaseAddress = new Uri(settings.OEmbedBaseUrl.TrimEnd('/') + "/");
                http.Timeout = Timeout.InfiniteTimeSpan;
            })
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { UseCookies = false })
            .AddHttpMessageHandler(() => new YouTubeHostGuard())
            .RemoveAllLoggers();

        // The record path: where Chrome is, one for the app, which reads the setting at each search; the yield of a video's
        // run to a class, through the run's own event; the window the batch opens and closes; and, one per run, the window a
        // video's run records, which a class's run reads to leave video windows out.
        services.AddSingleton<BrowserLocator>();
        services.AddSingleton<IVideoYield>(provider => OperatingSystem.IsWindows()
            ? new WindowsVideoYield(provider.GetRequiredService<ILogger<WindowsVideoYield>>())
            : throw new PlatformNotSupportedException("A video's run giving way to a class needs Windows."));
        services.AddTransient<VideoWindow>();
        services.AddScoped<VideoPlayback>();

        services.AddTransient<YtDlpCheck>();
        services.AddScoped<IBatchPace>(provider => new BatchPace(provider.GetRequiredService<TimeProvider>()));
        services.AddScoped<ClassWait>();
        services.AddScoped<DownloadedAudio>();
        services.AddScoped<TranscriptionRuns>();
        services.AddScoped<VideoTranscriptRun>();
        services.AddScoped<IVideoTranscriber, ScopedVideoTranscriber>();
        services.AddScoped<BatchTranscripts>();
        services.AddScoped<OEmbedTitles>();
        services.AddScoped<YouTubeBatch>();
        services.AddScoped<DownloadBatch>();
        services.AddScoped<VideoBatch>();
        services.AddScoped<YouTubeCommand>();
        services.AddScoped<YouTubeMenu>();
        return services;
    }
}
