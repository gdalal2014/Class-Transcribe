using ClassScribe.Audio.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ClassScribe.Audio;

/// <summary>Registers what records: Windows audio, the choice of what to record, the session, and the upload preparation.</summary>
public static class AudioServices
{
    /// <summary>Adds the audio services.</summary>
    public static IServiceCollection AddAudio(this IServiceCollection services)
    {
        // Only resolved by the record command and the menu, so transcribing an existing file works on any system.
        services.AddSingleton<ILoopbackCaptureProvider>(provider => OperatingSystem.IsWindows()
            ? new WasapiLoopbackCaptureProvider(provider.GetRequiredService<ILogger<WasapiLoopbackCaptureProvider>>())
            : throw new PlatformNotSupportedException("Recording system audio needs Windows."));
        services.AddSingleton<SourceChoice>();
        services.AddTransient<CaptureChooser>();
        services.AddTransient<RecordingSession>();
        services.AddSingleton<IAudioPreparer, AudioPreparer>();
        return services;
    }
}
