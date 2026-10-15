using System.Net.Sockets;
using ClassScribe.Audio;
using ClassScribe.Audio.Windows;
using ClassScribe.Commands;
using ClassScribe.Settings;
using ClassScribe.Transcription;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClassScribe;

/// <summary>Registers the app's services.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>Adds everything ClassScribe needs, bound to the given configuration.</summary>
    public static IServiceCollection AddClassScribe(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<RecordingOptions>(configuration.GetSection(RecordingOptions.SectionName));
        services.Configure<ElevenLabsOptions>(configuration.GetSection(ElevenLabsOptions.SectionName));
        services.Configure<TranscriptOptions>(configuration.GetSection(TranscriptOptions.SectionName));

        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IUserSignals, ConsoleUserSignals>();

        // Only resolved by the record command, so transcribing an existing file works on any system.
        services.AddSingleton<ILoopbackCaptureProvider>(provider => OperatingSystem.IsWindows()
            ? new WasapiLoopbackCaptureProvider(provider.GetRequiredService<ILogger<WasapiLoopbackCaptureProvider>>())
            : throw new PlatformNotSupportedException("Recording system audio needs Windows."));
        services.AddTransient<RecordingSession>();
        services.AddSingleton<IAudioPreparer, AudioPreparer>();

        services
            .AddHttpClient<ITranscriptionClient, ElevenLabsClient>((provider, http) =>
            {
                var settings = provider.GetRequiredService<IOptions<ElevenLabsOptions>>().Value;
                http.BaseAddress = new Uri(settings.BaseUrl.TrimEnd('/') + "/");
                http.Timeout = TimeSpan.FromMinutes(Math.Max(1, settings.TimeoutMinutes));
            })
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { ConnectCallback = ConnectWithKeepAliveAsync });
        services.AddSingleton<KeytermLoader>();
        services.AddSingleton<TranscriptFormatter>();
        services.AddTransient<TranscriptionWorkflow>();

        services.AddTransient<RecordCommand>();
        services.AddTransient<TranscribeCommand>();
        services.AddTransient<App>();
        return services;
    }

    /// <summary>
    /// Opens the connection to the service with TCP keep-alive switched on. After the upload the
    /// connection is silent for minutes while the transcript is made, and without a sign of life
    /// some routers and firewalls drop a connection that quiet.
    /// </summary>
    private static async ValueTask<Stream> ConnectWithKeepAliveAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, 30);
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, 10);
            await socket.ConnectAsync(context.DnsEndPoint, cancellationToken);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
