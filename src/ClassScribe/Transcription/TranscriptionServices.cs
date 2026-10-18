using System.Net.Sockets;
using ClassScribe.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Refit;

namespace ClassScribe.Transcription;

/// <summary>Registers the ElevenLabs client, the glossary, the transcript layout, the workflow that joins them, and the report of what a transcript cost.</summary>
public static class TranscriptionServices
{
    /// <summary>Adds the transcription services.</summary>
    public static IServiceCollection AddTranscription(this IServiceCollection services)
    {
        services
            .AddRefitGeneratedClient<IElevenLabsApi>(ElevenLabsJson.Default, _ => ElevenLabsApiSettings.Create())
            .ConfigureHttpClient((provider, http) =>
            {
                // Read each time a client is made, which is once per run, so a changed ElevenLabs:BaseUrl counts from the
                // next run. HttpClient's own time limit is off: ElevenLabsClient sets the limit itself, around the whole
                // exchange, the answer included, which HttpClient's limit does not cover.
                var settings = provider.GetRequiredService<IOptionsMonitor<ElevenLabsOptions>>().CurrentValue;
                http.BaseAddress = new Uri(settings.BaseUrl.TrimEnd('/') + "/");
                http.Timeout = Timeout.InfiniteTimeSpan;
            })
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { ConnectCallback = ConnectWithKeepAliveAsync });
        services.AddTransient<ITranscriptionClient, ElevenLabsClient>();
        services.AddSingleton<KeytermLoader>();
        services.AddSingleton<TranscriptFormatter>();
        services.AddTransient<TranscriptionWorkflow>();
        services.AddScoped<TranscriptionProgress>();

        // The account's credits, read before and after a transcript: the reader is one per use, over the same Refit
        // client; the report is one per run, since the workflow begins it and the command ends it.
        services.AddTransient<IElevenLabsAccount, ElevenLabsAccount>();
        services.AddScoped<UsageReport>();
        return services;
    }

    /// <summary>
    /// Opens the connection to the service with TCP keep-alive switched on. After the upload the
    /// connection is silent for minutes while the transcript is made, and without a sign of life
    /// some routers and firewalls drop a connection that quiet. Google Drive's clients use it too
    /// (see <see cref="Drive.DriveServices"/>).
    /// </summary>
    internal static async ValueTask<Stream> ConnectWithKeepAliveAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
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
