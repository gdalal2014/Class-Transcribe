using ClassScribe.Audio.Windows;
using ClassScribe.Desktop;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ClassScribe.Audio.Microphones;

/// <summary>
/// Registers the microphone: the provider that lists the microphones and opens one, made only when something first uses
/// it, as the capture provider is; the factory that opens a microphone's capture, which asks Windows for RAW and falls
/// back to Default; and the reader of the apps' microphone sessions, which the gate's watch, the microphone check and the
/// devices command read Windows' own lists through. What else the watch needs, Windows' microphone usage record and the
/// process names, is registered with the rest of the Windows integration (see <see cref="DesktopServices"/>).
/// </summary>
public static class MicrophoneServices
{
    /// <summary>Adds the microphone's services.</summary>
    public static IServiceCollection AddMicrophone(this IServiceCollection services)
    {
        // One for the process: it remembers, per microphone, that RAW failed when the stream was opened, so that the
        // microphone opens in Default from then on. Nothing is touched until a capture is opened.
        services.AddSingleton<IMicrophoneCaptureFactory>(provider => OperatingSystem.IsWindows()
            ? new MicrophoneActivation(provider.GetRequiredService<ILogger<MicrophoneActivation>>())
            : throw new PlatformNotSupportedException("Recording a microphone needs Windows."));

        // The real provider makes NAudio's enumerator and registers for device notifications as it is made, so it is made
        // only when a recording with the microphone starts, the microphones are listed, or the microphone menu asks for
        // them: the menu and the command line take the provider as a part of the app, and transcribe, rebuild and list
        // must never touch Windows audio.
        services.AddSingleton<IMicrophoneProvider>(provider => new LazyMicrophoneProvider(() => OperatingSystem.IsWindows()
            ? new WasapiMicrophoneProvider(provider.GetRequiredService<IMicrophoneCaptureFactory>(), provider.GetRequiredService<ILogger<WasapiMicrophoneProvider>>())
            : throw new PlatformNotSupportedException("Recording a microphone needs Windows.")));

        // For the devices command, which asks for the provider only when it runs: the one registered above, so that a
        // stand-in registered in its place is what the command lists.
        services.AddSingleton(provider => new Lazy<IMicrophoneProvider>(provider.GetRequiredService<IMicrophoneProvider>));

        // One for the process: it keeps Windows' enumerator between reads. Reading never opens a microphone, and nothing
        // is touched before the first read.
        services.AddSingleton<IMicrophoneSessions>(provider => OperatingSystem.IsWindows()
            ? new WasapiMicrophoneSessions(provider.GetRequiredService<IProcessNames>(), provider.GetRequiredService<ILogger<WasapiMicrophoneSessions>>())
            : throw new PlatformNotSupportedException("Reading which apps have a microphone open needs Windows."));
        services.AddSingleton(provider => new Lazy<IMicrophoneSessions>(provider.GetRequiredService<IMicrophoneSessions>));
        return services;
    }
}

/// <summary>
/// The microphone provider as the app holds it: the real one is made the first time anything lists the microphones,
/// opens a capture of one, asks about its mute, or listens for the microphones to change, and not before, so that the
/// parts of the app that only take the provider along, such as the menu, never touch Windows audio. Made once, it is
/// kept and disposed with this.
/// </summary>
/// <param name="make">Makes the real provider.</param>
internal sealed class LazyMicrophoneProvider(Func<IMicrophoneProvider> make) : IMicrophoneProvider
{
    private readonly Lazy<IMicrophoneProvider> _inner = new(make, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>True once the real provider has been made.</summary>
    internal bool IsMade => _inner.IsValueCreated;

    /// <inheritdoc />
    public event EventHandler? MicrophonesChanged
    {
        add => _inner.Value.MicrophonesChanged += value;
        remove
        {
            // A listener that never listened has nothing to let go of; the real provider is not made for it.
            if (_inner.IsValueCreated) _inner.Value.MicrophonesChanged -= value;
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<Microphone> GetMicrophones() => _inner.Value.GetMicrophones();

    /// <inheritdoc />
    public IAudioCapture CreateCapture(Microphone microphone, MicrophoneProcessing processing) => _inner.Value.CreateCapture(microphone, processing);

    /// <inheritdoc />
    public bool? IsMuted(string id) => _inner.Value.IsMuted(id);

    /// <summary>Disposes the real provider, when it was made.</summary>
    public void Dispose()
    {
        if (_inner.IsValueCreated) _inner.Value.Dispose();
    }
}
