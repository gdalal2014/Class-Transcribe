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

/// <summary>A running capture of one audio device.</summary>
public interface IAudioCapture : IDisposable
{
    /// <summary>Identifier of the device being captured.</summary>
    string DeviceId { get; }

    /// <summary>Name of the device being captured.</summary>
    string DeviceName { get; }

    /// <summary>Format of the audio passed to <see cref="DataAvailable"/>.</summary>
    AudioFormat Format { get; }

    /// <summary>Raised on the capture thread whenever audio arrives.</summary>
    event EventHandler<AudioDataEventArgs>? DataAvailable;

    /// <summary>Raised on the capture thread when the capture ends, whether it was asked to or not.</summary>
    event EventHandler<CaptureStoppedEventArgs>? Stopped;

    /// <summary>Starts delivering audio.</summary>
    void Start();

    /// <summary>Asks the capture to stop. <see cref="IDisposable.Dispose"/> waits until it has.</summary>
    void Stop();
}

/// <summary>Creates captures of whatever the computer is playing.</summary>
public interface ILoopbackCaptureProvider : IDisposable
{
    /// <summary>
    /// Raised when the operating system reports a change of default output device. It can also be
    /// raised when nothing really changed, so listeners compare <see cref="GetDefaultDeviceId"/>
    /// with what they are capturing.
    /// </summary>
    event EventHandler? DefaultDeviceChanged;

    /// <summary>Identifier of the current default output device, or null when there is none.</summary>
    string? GetDefaultDeviceId();

    /// <summary>Creates a capture of the current default output device.</summary>
    IAudioCapture CreateForDefaultDevice();
}
