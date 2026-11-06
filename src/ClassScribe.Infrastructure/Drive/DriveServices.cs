using ClassScribe.Drive.Windows;
using ClassScribe.Storage;
using ClassScribe.Transcription;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Refit;

namespace ClassScribe.Drive;

/// <summary>
/// Registers Google Drive's parts that reach Google, Windows and the disk (architecture 5.2): Google's OAuth and Drive
/// endpoints as Refit clients, the store of the client and sign-in, the access tokens, the calls to Drive, ClassScribe's
/// folders there, the listener and the browser of the sign-in, the manifests, the upload lock and the resumable upload. The
/// sign-in itself, the retries, the upload of a recording and the pass over the queue, the freeing of room on the laptop,
/// what the rest of the app reads of audio kept on Drive alone, the uploads that happen by themselves after a run, and what
/// the menu and the report at the start say of the queue are the use cases' (see <see cref="DriveUseCases"/>).
/// </summary>
public static class DriveServices
{
    /// <summary>
    /// Adds the Google Drive services that reach Google, Windows and the disk. Each Refit client reads its base address from the Drive settings when it is made, once
    /// per run, so a changed address counts from the next run; HttpClient's own time limit is off, as each call sets its own;
    /// redirects are not followed; and Microsoft.Extensions.Http logs nothing of these requests, not even their addresses,
    /// whatever Logging:LogLevel says, so no secret can reach a log through them. The store needs Windows' data protection.
    /// </summary>
    public static IServiceCollection AddDrive(this IServiceCollection services)
    {
        services
            .AddRefitGeneratedClient<IGoogleOAuthApi>(GoogleOAuthJson.Default, _ => DriveApiSettings.Create())
            .ConfigureHttpClient((provider, http) => Configure(http, provider.GetRequiredService<IOptionsMonitor<DriveOptions>>().CurrentValue.OAuthBaseUrl))
            .ConfigurePrimaryHttpMessageHandler(PrimaryHandler)
            .RemoveAllLoggers();
        services
            .AddRefitGeneratedClient<IDriveApi>(DriveJson.Default, _ => DriveApiSettings.Create())
            .ConfigureHttpClient((provider, http) => Configure(http, provider.GetRequiredService<IOptionsMonitor<DriveOptions>>().CurrentValue.ApiBaseUrl))
            .ConfigurePrimaryHttpMessageHandler(PrimaryHandler)
            .RemoveAllLoggers();

        // %APPDATA%\ClassScribe\google-drive.dat, read and written only when a sign-in, a status or a renewal asks for it.
        services.AddSingleton<IGoogleCredentialStore>(provider => OperatingSystem.IsWindows()
            ? new DpapiGoogleCredentialStore(provider.GetRequiredService<RoamingFolder>(), provider.GetRequiredService<ILogger<DpapiGoogleCredentialStore>>())
            : throw new PlatformNotSupportedException("Keeping the Google sign-in encrypted for your account needs Windows."));

        // The access token of this process, one for the process; what reads and renews it, and everything that calls Drive,
        // one per run, over that run's Refit clients and settings. The sign-in's exchange and revoke go through the tokens,
        // as the port of the account (architecture 4.3).
        services.AddSingleton<GoogleTokenCache>();
        services.AddTransient<GoogleTokens>();
        services.AddTransient<IGoogleAccount>(provider => provider.GetRequiredService<GoogleTokens>());
        services.AddTransient<DriveClient>();
        services.AddTransient<DriveFolders>();

        // The sign-in, in the window only: the listener for Google's answer, the browser, and the reader of a client file.
        services.AddSingleton<ILoopbackReceiver, HttpListenerLoopbackReceiver>();
        services.AddSingleton<IBrowser, ShellBrowser>();
        services.AddSingleton<IGoogleClientFiles, GoogleClientFile>();

        // The uploads: what is remembered of each recording and the lock, one for the process, each behind its port; the
        // resumable upload, one per run, over that run's Drive client and settings; and Drive as the use cases use it, one
        // per run over the three (architecture 4.3).
        services.AddSingleton<UploadManifests>();
        services.AddSingleton<IUploadManifests>(provider => provider.GetRequiredService<UploadManifests>());
        services.AddSingleton<DriveUploadLock>();
        services.AddSingleton<IDriveUploadLock>(provider => provider.GetRequiredService<DriveUploadLock>());
        services.AddTransient<ResumableUpload>();
        services.AddTransient<IGoogleDrive, GoogleDrive>();

        // Drive's room, for a YouTube batch's plan when the videos' audio goes up: Drive is made only when it is asked.
        services.AddTransient(provider => new Lazy<IGoogleDrive>(provider.GetRequiredService<IGoogleDrive>));
        return services;
    }

    /// <summary>Sets the base address of a Google client, with a slash at its end, and turns HttpClient's own time limit off.</summary>
    private static void Configure(HttpClient http, string baseUrl)
    {
        http.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
        http.Timeout = Timeout.InfiniteTimeSpan;
    }

    /// <summary>The connection to Google: redirects are not followed, and TCP keep-alive is on, as for ElevenLabs.</summary>
    private static HttpMessageHandler PrimaryHandler() =>
        new SocketsHttpHandler { AllowAutoRedirect = false, ConnectCallback = TranscriptionServices.ConnectWithKeepAliveAsync };
}
