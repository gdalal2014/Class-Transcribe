using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace ClassScribe.Audio.Windows;

/// <summary>
/// Captures what one Windows output device plays, or what one app plays, using WASAPI loopback.
/// </summary>
/// <remarks>
/// NAudio's audio client classes do the COM work, but the capture loop is our own. NAudio 2.2.1's
/// <c>WasapiLoopbackCapture</c> ends its thread with an unguarded <c>AudioClient.Stop()</c>, which throws
/// once the device is gone (a headset unplugged or a Bluetooth link dropped) and takes the whole
/// process down. Here nothing can leave the capture thread: every failure is reported through
/// <see cref="Stopped"/> so the recording can move to the new device.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WasapiLoopbackAudioCapture : IAudioCapture
{
    /// <summary>WASAPI measures time in 100-nanosecond units.</summary>
    private const long UnitsPerMillisecond = 10_000;

    /// <summary>Capacity asked of Windows. A full second leaves room for a slow disk write on the capture thread.</summary>
    private const int BufferMilliseconds = 1000;

    private const int PollMilliseconds = 50;
    private static readonly TimeSpan JoinTimeout = TimeSpan.FromSeconds(5);

    private readonly MMDevice? _device;
    private readonly AudioClient _client;
    private readonly WaveFormat _streamFormat;
    private readonly AudioClientStreamFlags _streamFlags;
    private readonly AutoResetEvent? _packetReady;
    private readonly ILogger _logger;
    private byte[] _buffer = [];
    private Thread? _thread;
    private volatile bool _stopRequested;
    private bool _disposed;

    /// <summary>Prepares a loopback capture of <paramref name="device"/>, known to the user as <paramref name="deviceName"/>. The capture takes ownership of the device.</summary>
    public WasapiLoopbackAudioCapture(MMDevice device, string deviceName, ILogger logger)
    {
        _device = device;
        _logger = logger;
        DeviceId = device.ID;
        DeviceName = deviceName;
        _client = device.AudioClient;
        try
        {
            _streamFormat = _client.MixFormat;
            var format = _streamFormat.AsStandardWaveFormat();
            if (format.Encoding is not (WaveFormatEncoding.IeeeFloat or WaveFormatEncoding.Pcm))
                throw new NotSupportedException($"The output device \"{DeviceName}\" uses the unsupported encoding {format.Encoding}.");

            Format = new AudioFormat(format.SampleRate, format.Channels, format.BitsPerSample, format.Encoding == WaveFormatEncoding.IeeeFloat);

            // Same stream flags NAudio's loopback capture uses.
            _streamFlags = AudioClientStreamFlags.Loopback | AudioClientStreamFlags.AutoConvertPcm | AudioClientStreamFlags.SrcDefaultQuality;
        }
        catch
        {
            _client.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Prepares a capture of one app's sound from <paramref name="processClient"/>, an audio client opened
    /// with <see cref="ProcessLoopback"/>. The capture takes ownership of the client.
    /// </summary>
    public WasapiLoopbackAudioCapture(AudioClient processClient, string id, string appName, ILogger logger)
    {
        _client = processClient;
        _logger = logger;
        DeviceId = id;
        DeviceName = appName;

        // A process loopback stream has no device format of its own: Windows delivers whatever format is
        // asked for, and it insists on event-driven buffering, so there is an event to signal with.
        _streamFormat = new WaveFormat(48000, 16, 2);
        Format = new AudioFormat(48000, 2, 16, false);
        _streamFlags = AudioClientStreamFlags.Loopback | AudioClientStreamFlags.EventCallback;
        _packetReady = new AutoResetEvent(false);
    }

    /// <inheritdoc />
    public string DeviceId { get; }

    /// <inheritdoc />
    public string DeviceName { get; }

    /// <inheritdoc />
    public AudioFormat Format { get; }

    /// <inheritdoc />
    public event EventHandler<AudioDataEventArgs>? DataAvailable;

    /// <inheritdoc />
    public event EventHandler<CaptureStoppedEventArgs>? Stopped;

    /// <summary>Opens the loopback stream and starts the capture thread.</summary>
    public void Start()
    {
        if (_thread is not null)
            throw new InvalidOperationException("This capture has already been started.");

        _client.Initialize(
            AudioClientShareMode.Shared,
            _streamFlags,
            BufferMilliseconds * UnitsPerMillisecond,
            0,
            _streamFormat,
            Guid.Empty);
        if (_packetReady is not null)
            _client.SetEventHandle(_packetReady.SafeWaitHandle.DangerousGetHandle());

        _thread = new Thread(CaptureLoop) { IsBackground = true, Name = "ClassScribe capture", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    /// <summary>Asks the capture thread to finish. <see cref="Dispose"/> waits for it.</summary>
    public void Stop() => _stopRequested = true;

    /// <summary>Stops the capture, waits for its thread and releases the device.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _stopRequested = true;

        if (_thread is { } thread && thread != Thread.CurrentThread && !thread.Join(JoinTimeout))
        {
            // Most likely the thread is waiting for the disk. Releasing the audio client underneath a thread
            // that may still be inside it could crash the process, so it is left for the runtime to reclaim.
            _logger.LogWarning("The capture thread for \"{Device}\" did not stop within {Seconds} seconds; its audio client is left open", DeviceName, JoinTimeout.TotalSeconds);
            return;
        }

        _client.Dispose();
        _device?.Dispose();
        _packetReady?.Dispose();
    }

    /// <summary>Body of the capture thread: collects audio from Windows until asked to stop or until the device fails.</summary>
    private void CaptureLoop()
    {
        Exception? failure = null;
        try
        {
            var capture = _client.AudioCaptureClient;
            _client.Start();
            while (!_stopRequested)
            {
                WaitForPackets();

                var began = Stopwatch.GetTimestamp();
                ReadPackets(capture);
                var took = Stopwatch.GetElapsedTime(began);

                // Windows holds one second of audio for us. Whoever handles the audio (a slow disk, usually)
                // took longer than that, so what arrived meanwhile is gone. At least it does not go unnoticed.
                if (took.TotalMilliseconds >= BufferMilliseconds)
                    _logger.LogWarning("Storing the captured audio took {Seconds:F1} seconds, longer than Windows keeps it waiting; the recording probably has a gap there", took.TotalSeconds);
            }

            // Collect what arrived since the last poll.
            ReadPackets(capture);
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        try
        {
            _client.Stop();
        }
        catch (Exception ex)
        {
            // Expected when the device has gone away. It is passed on instead of thrown.
            failure ??= ex;
        }

        try
        {
            Stopped?.Invoke(this, new CaptureStoppedEventArgs(failure));
        }
        catch (Exception ex)
        {
            // Nothing may leave this thread: an unhandled exception here would end the process and the recording.
            _logger.LogError(ex, "A handler of the capture's Stopped event failed");
        }
    }

    /// <summary>Waits a moment for audio: until Windows signals a packet for an app's sound, or a fixed poll interval for a device.</summary>
    private void WaitForPackets()
    {
        if (_packetReady is null)
            Thread.Sleep(PollMilliseconds);
        else
            _packetReady.WaitOne(PollMilliseconds);
    }

    /// <summary>Hands every waiting packet to the listeners. Silent packets are delivered as zeros.</summary>
    private void ReadPackets(AudioCaptureClient capture)
    {
        while (capture.GetNextPacketSize() > 0)
        {
            var pointer = capture.GetBuffer(out var frames, out var flags);
            var byteCount = frames * _streamFormat.BlockAlign;
            if (_buffer.Length < byteCount) _buffer = new byte[byteCount];

            if (flags.HasFlag(AudioClientBufferFlags.Silent))
                Array.Clear(_buffer, 0, byteCount);
            else
                Marshal.Copy(pointer, _buffer, 0, byteCount);

            capture.ReleaseBuffer(frames);
            if (byteCount > 0)
                DataAvailable?.Invoke(this, new AudioDataEventArgs(_buffer, byteCount));
        }
    }
}
