using ClassScribe.Desktop;
using ClassScribe.Recorder;
using ClassScribe.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Refit;

namespace ClassScribe.YouTube;

/// <summary>
/// Registers the YouTube feature's parts that reach Windows, the tools, YouTube and the disk (architecture 5.2), for both
/// paths. The batches, their pace, their transcripts and their command are the use cases' (see <see cref="YouTubeUseCases"/>);
/// the menu is the window's.
/// </summary>
public static class YouTubeServices
{
    /// <summary>
    /// Adds the feature's adapters: its folders, the lock, the tools' locator and runner, the check, the oEmbed client for the
    /// record path's titles, Chrome's locator, the yield of a video's run, the window, and per batch (each in a scope of its
    /// own) the conversion and the titles. The runner reaches Windows' own start of a process, and so needs Windows.
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
        // run to a class, through the run's own event; and the window the batch opens and closes.
        services.AddSingleton<BrowserLocator>();
        services.AddSingleton<IVideoYield>(provider => OperatingSystem.IsWindows()
            ? new WindowsVideoYield(provider.GetRequiredService<ILogger<WindowsVideoYield>>())
            : throw new PlatformNotSupportedException("A video's run giving way to a class needs Windows."));
        services.AddTransient<VideoWindow>();

        services.AddTransient<YtDlpCheck>();
        services.AddScoped<DownloadedAudio>();
        services.AddScoped<OEmbedTitles>();
        return services;
    }
}
