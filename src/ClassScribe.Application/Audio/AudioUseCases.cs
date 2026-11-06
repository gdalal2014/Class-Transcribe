using ClassScribe.Audio.Microphones;
using Microsoft.Extensions.DependencyInjection;

namespace ClassScribe.Audio;

/// <summary>
/// Registers what records, as far as it is the use cases' (architecture 5.2): the choice of what to record, the recordings a
/// run makes from its source plan, and the advice about the microphone with the watch for the class app on a Bluetooth
/// microphone. Windows audio and the upload preparation are registered with the adapters.
/// </summary>
public static class AudioUseCases
{
    /// <summary>Adds the audio's use cases and the services they drive.</summary>
    public static IServiceCollection AddAudioUseCases(this IServiceCollection services)
    {
        services.AddSingleton<SourceChoice>();

        // One per use, inside a run's scope, with that run's settings and its freeze watch: the recording it makes is the
        // class alone, the class and your microphone on one clock, or your microphone alone in person, as the plan says,
        // each with sessions of its own.
        services.AddTransient<TrackSetFactory>();
        services.AddTransient<IRecordingFactory>(provider => provider.GetRequiredService<TrackSetFactory>());

        // The advice about the microphone, one per run: it remembers Vantage's mode from the start, so that the run can
        // say when the mode changes. The watch for the class app on a Bluetooth microphone is one per run too: it
        // remembers what it last found, so that each use is warned about once.
        services.AddScoped<MicrophoneAdvice>();
        services.AddScoped<BluetoothMicrophoneWatch>();
        return services;
    }
}