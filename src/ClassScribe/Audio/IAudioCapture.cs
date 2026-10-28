using ClassScribe.Audio.Microphones;

namespace ClassScribe.Audio;

/// <summary>A running capture of one audio device, of one app's sound, or of a microphone.</summary>
public interface IAudioCapture : IDisposable
{
    /// <summary>Identifier of what is being captured: the device's, or <c>process:</c> and the app's process id.</summary>
    string DeviceId { get; }

    /// <summary>Name of what is being captured: the device, or the app.</summary>
    string DeviceName { get; }

    /// <summary>Format of the audio passed to <see cref="DataAvailable"/>.</summary>
    AudioFormat Format { get; }

    /// <summary>
    /// True when the capture hands over audio all the time, silence included, as an app's process loopback and a
    /// microphone do: a stretch with no audio at all then means that the capture has stopped working although it is
    /// still open. False for an output device's loopback, which hands over nothing while nothing plays, so a gap there
    /// is no fault in itself.
    /// </summary>
    bool DeliversContinuously { get; }

    /// <summary>
    /// For a microphone, how Windows processes its audio before handing it over: what was asked for, or
    /// <see cref="MicrophoneProcessing.Default"/> when unprocessed audio was not available. Null for a device or an app, and
    /// for a capture that reports nothing of it.
    /// </summary>
    MicrophoneProcessing? Processing { get; }

    /// <summary>Raised on the capture thread whenever audio arrives.</summary>
    event EventHandler<AudioDataEventArgs>? DataAvailable;

    /// <summary>Raised on the capture thread when the capture ends, whether it was asked to or not.</summary>
    event EventHandler<CaptureStoppedEventArgs>? Stopped;

    /// <summary>Starts delivering audio.</summary>
    void Start();

    /// <summary>Asks the capture to stop. <see cref="IDisposable.Dispose"/> waits until it has.</summary>
    void Stop();
}

/// <summary>Creates captures of what the computer plays, and reports on its output devices and the apps playing through them.</summary>
public interface ILoopbackCaptureProvider : IDisposable
{
    /// <summary>
    /// Raised when Windows reports a change among output devices: a new default device, or a device
    /// added, removed or changing state. It can also be raised when nothing changed for the recording,
    /// so listeners look at <see cref="GetOutputDevices"/> rather than at the event.
    /// </summary>
    event EventHandler? DevicesChanged;

    /// <summary>The output devices Windows can play through right now, with how loud each is playing.</summary>
    IReadOnlyList<OutputDevice> GetOutputDevices();

    /// <summary>The apps that have sound open on an output device right now, with how loud each is playing.</summary>
    IReadOnlyList<PlayingApp> GetPlayingApps();

    /// <summary>
    /// The apps as <see cref="GetPlayingApps"/> lists them, with what the listing could not read in full: an app whose
    /// process Windows named is listed even when its session's state could not be read, since its process is what a
    /// recording of it captures; any other session that could not be read is left out and noted here, so that an app
    /// missing from the list is not taken for one that has closed.
    /// </summary>
    PlayingAppsListing GetPlayingAppsListing();

    /// <summary>Creates a capture of the device or the app the target names.</summary>
    IAudioCapture CreateCapture(CaptureTarget target);
}
