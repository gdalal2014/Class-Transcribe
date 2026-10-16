namespace ClassScribe.Audio;

/// <summary>Audio handed over by a capture. The buffer is reused, so read it before the handler returns.</summary>
public sealed class AudioDataEventArgs(byte[] buffer, int byteCount) : EventArgs
{
    /// <summary>Buffer holding the captured audio.</summary>
    public byte[] Buffer { get; } = buffer;

    /// <summary>Number of valid bytes at the start of <see cref="Buffer"/>.</summary>
    public int ByteCount { get; } = byteCount;
}

/// <summary>Raised when a capture ends. <see cref="Error"/> is set when it ended because of a failure.</summary>
public sealed class CaptureStoppedEventArgs(Exception? error) : EventArgs
{
    /// <summary>The failure that ended the capture, if any.</summary>
    public Exception? Error { get; } = error;
}

/// <summary>A running capture of one audio device, or of one app's sound.</summary>
public interface IAudioCapture : IDisposable
{
    /// <summary>Identifier of what is being captured: the device's, or <c>process:</c> and the app's process id.</summary>
    string DeviceId { get; }

    /// <summary>Name of what is being captured: the device, or the app.</summary>
    string DeviceName { get; }

    /// <summary>Format of the audio passed to <see cref="DataAvailable"/>.</summary>
    AudioFormat Format { get; }

    /// <summary>
    /// True when the capture hands over audio all the time, silence included, as an app's process loopback does: a
    /// stretch with no audio at all then means that the capture has stopped working although it is still open. False
    /// for an output device's loopback, which hands over nothing while nothing plays, so a gap there is no fault in itself.
    /// </summary>
    bool DeliversContinuously { get; }

    /// <summary>Raised on the capture thread whenever audio arrives.</summary>
    event EventHandler<AudioDataEventArgs>? DataAvailable;

    /// <summary>Raised on the capture thread when the capture ends, whether it was asked to or not.</summary>
    event EventHandler<CaptureStoppedEventArgs>? Stopped;

    /// <summary>Starts delivering audio.</summary>
    void Start();

    /// <summary>Asks the capture to stop. <see cref="IDisposable.Dispose"/> waits until it has.</summary>
    void Stop();
}

/// <summary>An output device Windows can play through right now.</summary>
/// <param name="Id">Windows' identifier for the device.</param>
/// <param name="Name">The name Windows shows for it.</param>
/// <param name="IsDefault">True for the device Windows plays through unless an app picks another.</param>
/// <param name="Level">How loud the device is playing at this moment, from 0 (silence) to 1, as Windows' level meter reports it.</param>
public sealed record OutputDevice(string Id, string Name, bool IsDefault, float Level);

/// <summary>An app that has sound open on an output device.</summary>
/// <param name="ProcessId">The process that plays the sound.</param>
/// <param name="Name">The process name, such as chrome or msedge.</param>
/// <param name="DeviceName">The output device the app plays through.</param>
/// <param name="Level">How loud the app is playing at this moment, from 0 (silence) to 1, as Windows' level meter for its sound reports it.</param>
public sealed record PlayingApp(int ProcessId, string Name, string DeviceName, float Level);

/// <summary>What a capture records: an output device, or the sound of one app and its child processes.</summary>
/// <param name="Id">The device's identifier, or <c>process:</c> and the app's process id.</param>
/// <param name="Name">The device's or the app's name, for the log.</param>
/// <param name="ProcessId">The app's process, or null for a device.</param>
public sealed record CaptureTarget(string Id, string Name, int? ProcessId = null)
{
    /// <summary>The target that records an output device.</summary>
    public static CaptureTarget ForDevice(OutputDevice device) => new(device.Id, device.Name);

    /// <summary>The target that records one app's sound, from the process that has it open.</summary>
    public static CaptureTarget ForApp(PlayingApp app) => new($"process:{app.ProcessId}", app.Name, app.ProcessId);
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

    /// <summary>Creates a capture of the device or the app the target names.</summary>
    IAudioCapture CreateCapture(CaptureTarget target);
}
