using ClassScribe.Audio.Microphones;
using ClassScribe.Audio.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ClassScribe.Audio;

/// <summary>
/// Registers what records: Windows audio, made only when something first uses it; the choice of what to record; the
/// recordings a run makes from its source plan; the advice about the microphone; and the upload preparation. The
/// microphone itself is registered apart (see <see cref="MicrophoneServices"/>).
/// </summary>
public static class AudioServices
{
    /// <summary>Adds the audio services.</summary>
    public static IServiceCollection AddAudio(this IServiceCollection services)
    {
        // Windows audio is reached only when a recording starts or the devices are listed: the menu and the command line
        // take the provider as a part of the app, so without this transcribe, rebuild, list and help would all touch Windows
        // audio as the app is put together, and transcribing an existing file would need Windows at all.
        services.AddSingleton<ILoopbackCaptureProvider>(provider => new LazyLoopbackCaptureProvider(() => OperatingSystem.IsWindows()
            ? new WasapiLoopbackCaptureProvider(provider.GetRequiredService<ILogger<WasapiLoopbackCaptureProvider>>())
            : throw new PlatformNotSupportedException("Recording system audio needs Windows.")));

        // For the devices command, which asks for the provider only when it runs: the one registered above, so that a
        // stand-in registered in its place is what the command lists.
        services.AddSingleton(provider => new Lazy<ILoopbackCaptureProvider>(provider.GetRequiredService<ILoopbackCaptureProvider>));
        services.AddSingleton<SourceChoice>();

        // One per use, inside a run's scope, with that run's settings and its freeze watch: the recording it makes is the
        // class alone, the class and your microphone on one clock, or your microphone alone in person, as the plan says,
        // each with sessions of its own.
        services.AddTransient<TrackSetFactory>();
        services.AddTransient<IRecordingFactory>(provider => provider.GetRequiredService<TrackSetFactory>());

        // The advice about the microphone, one per run: it remembers Vantage's mode from the start, so that the run can
        // say when the mode changes.
        services.AddScoped<MicrophoneAdvice>();

        services.AddSingleton<IAudioPreparer, AudioPreparer>();
        return services;
    }
}

/// <summary>
/// The capture provider as the app holds it: the real one is made the first time anything lists the devices or the apps,
/// makes a capture, or listens for the devices to change, and not before, so that the parts of the app that only take the
/// provider along, such as the menu, never touch Windows audio. Made once, it is kept and disposed with this.
/// </summary>
/// <param name="make">Makes the real provider.</param>
internal sealed class LazyLoopbackCaptureProvider(Func<ILoopbackCaptureProvider> make) : ILoopbackCaptureProvider
{
    private readonly Lazy<ILoopbackCaptureProvider> _inner = new(make, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>True once the real provider has been made.</summary>
    internal bool IsMade => _inner.IsValueCreated;

    /// <inheritdoc />
    public event EventHandler? DevicesChanged
    {
        add => _inner.Value.DevicesChanged += value;
        remove
        {
            // A listener that never listened has nothing to let go of; the real provider is not made for it.
            if (_inner.IsValueCreated) _inner.Value.DevicesChanged -= value;
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<OutputDevice> GetOutputDevices() => _inner.Value.GetOutputDevices();

    /// <inheritdoc />
    public IReadOnlyList<PlayingApp> GetPlayingApps() => _inner.Value.GetPlayingApps();

    /// <inheritdoc />
    public IAudioCapture CreateCapture(CaptureTarget target) => _inner.Value.CreateCapture(target);

    /// <summary>Disposes the real provider, when it was made.</summary>
    public void Dispose()
    {
        if (_inner.IsValueCreated) _inner.Value.Dispose();
    }
}
