using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using ClassScribe.Audio.Microphones;
using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace ClassScribe.Audio.Windows;

/// <summary>
/// Captures what one Windows output device plays, what one app plays, or what a microphone hears, through WASAPI: one
/// guarded capture loop for the three kinds, made with <see cref="ForOutputDevice"/>, <see cref="ForApp(AudioClient, string, string, ILogger)"/>
/// and <see cref="ForMicrophone(AudioClient, string, string, MicrophoneProcessing, ILogger, Action{Exception})"/>. Every
/// packet is handed on with the stamp Windows gives its first frame, so that the tracks of a recording can be placed on
/// one clock.
/// </summary>
/// <remarks>
/// NAudio's audio client classes do the COM work, but the capture loop is our own. NAudio 2.2.1's
/// <c>WasapiLoopbackCapture</c> and <c>WasapiCapture</c> end their thread with an unguarded <c>AudioClient.Stop()</c>,
/// which throws once the device is gone (a headset unplugged or a Bluetooth link dropped) and takes the whole process
/// down. Here nothing can leave the capture thread: every failure is reported through <see cref="Stopped"/> so the
/// recording can move to the new device.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WasapiAudioCapture : IAudioCapture
{
    /// <summary>WASAPI measures time in 100-nanosecond units.</summary>
    private const long UnitsPerMillisecond = 10_000;

    /// <summary>Capacity asked of Windows. A full second leaves room for a slow disk write on the capture thread.</summary>
    private const int BufferMilliseconds = 1000;

    private const int PollMilliseconds = 50;

    /// <summary>Where the channel mask sits in a WAVEFORMATEXTENSIBLE: after the 18 bytes of WAVEFORMATEX and the word of valid bits.</summary>
    private const int ChannelMaskOffset = 20;

    /// <summary>The name of the thread that reads an output device or an app.</summary>
    private const string CaptureThreadName = "ClassScribe capture";

    /// <summary>The name of the thread that reads a microphone.</summary>
    private const string MicrophoneThreadName = "ClassScribe microphone";

    private static readonly TimeSpan JoinTimeout = TimeSpan.FromSeconds(5);

    /// <summary>A capture held up again and again is reported at most this often.</summary>
    private static readonly TimeSpan GapReportInterval = TimeSpan.FromSeconds(30);

    private readonly MMDevice? _device;
    private readonly AudioClient _client;
    private readonly WaveFormat _streamFormat;
    private readonly AudioClientStreamFlags _streamFlags;
    private readonly AutoResetEvent? _packetReady;
    private readonly string _threadName;
    private readonly Action<Exception?>? _opened;
    private readonly Func<AudioClient, IPacketSource> _packets;
    private readonly ILogger _logger;
    private byte[] _buffer = [];
    private long _packetsRead;
    private long _unstampedPackets;
    private Thread? _thread;
    private volatile bool _stopRequested;
    private bool _disposed;

    /// <summary>
    /// Prepares a capture around <paramref name="client"/>, whose stream is opened with <paramref name="streamFormat"/> and
    /// <paramref name="streamFlags"/> at <see cref="Start"/>. The capture takes ownership of the client, and of
    /// <paramref name="device"/> when there is one. Packets come from <paramref name="packets"/>, or from Windows' capture
    /// client when that is null. <paramref name="opened"/>, when given, is told how opening the stream went.
    /// </summary>
    private WasapiAudioCapture(AudioClient client, string id, string name, WaveFormat streamFormat, AudioFormat format, AudioClientStreamFlags streamFlags,
        bool deliversContinuously, string threadName, ILogger logger, MMDevice? device = null, bool eventDriven = false,
        MicrophoneProcessing? processing = null, Action<Exception?>? opened = null, IPacketSource? packets = null)
    {
        _client = client;
        _device = device;
        _streamFormat = streamFormat;
        _streamFlags = streamFlags;
        _threadName = threadName;
        _opened = opened;
        _packets = packets is null ? static audioClient => new WindowsPackets(audioClient.AudioCaptureClient) : _ => packets;
        _logger = logger;
        _packetReady = eventDriven ? new AutoResetEvent(false) : null;
        DeviceId = id;
        DeviceName = name;
        Format = format;
        DeliversContinuously = deliversContinuously;
        Processing = processing;
    }

    /// <inheritdoc />
    public string DeviceId { get; }

    /// <inheritdoc />
    public string DeviceName { get; }

    /// <inheritdoc />
    public AudioFormat Format { get; }

    /// <inheritdoc />
    public bool DeliversContinuously { get; }

    /// <inheritdoc />
    public MicrophoneProcessing? Processing { get; }

    /// <inheritdoc />
    public event EventHandler<AudioDataEventArgs>? DataAvailable;

    /// <inheritdoc />
    public event EventHandler<CaptureStoppedEventArgs>? Stopped;

    /// <summary>
    /// Prepares a loopback capture of <paramref name="device"/>, known to the user as <paramref name="name"/>: the device's
    /// mix format, the same stream flags NAudio's loopback capture uses. The capture takes ownership of the device, and
    /// releases its audio client when the format cannot be read or is not supported.
    /// </summary>
    public static WasapiAudioCapture ForOutputDevice(MMDevice device, string name, ILogger logger)
    {
        var client = device.AudioClient;
        try
        {
            var (streamFormat, format) = MixFormatOf(client, $"The output device \"{name}\"");

            // A device's loopback hands over nothing while nothing plays on the device.
            return new WasapiAudioCapture(client, device.ID, name, streamFormat, format,
                AudioClientStreamFlags.Loopback | AudioClientStreamFlags.AutoConvertPcm | AudioClientStreamFlags.SrcDefaultQuality,
                deliversContinuously: false, CaptureThreadName, logger, device: device);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Prepares a capture of one app's sound from <paramref name="processClient"/>, an audio client opened with
    /// <see cref="ProcessLoopback"/>. The capture takes ownership of the client.
    /// </summary>
    public static WasapiAudioCapture ForApp(AudioClient processClient, string id, string appName, ILogger logger) =>
        ForApp(processClient, id, appName, logger, packets: null);

    /// <summary>
    /// <see cref="ForApp(AudioClient, string, string, ILogger)"/>, with the packets read from <paramref name="packets"/>
    /// instead of Windows when a test gives them.
    /// </summary>
    internal static WasapiAudioCapture ForApp(AudioClient processClient, string id, string appName, ILogger logger, IPacketSource? packets)
    {
        // A process loopback stream has no device format of its own: Windows delivers whatever format is asked for, and
        // it insists on event-driven buffering, so there is an event to signal with. It hands over silence while the app
        // plays nothing, so it never falls quiet by itself.
        return new WasapiAudioCapture(processClient, id, appName, new WaveFormat(48000, 16, 2), new AudioFormat(48000, 2, 16, false),
            AudioClientStreamFlags.Loopback | AudioClientStreamFlags.EventCallback,
            deliversContinuously: true, CaptureThreadName, logger, eventDriven: true, packets: packets);
    }

    /// <summary>
    /// Prepares a capture of the microphone <paramref name="id"/>, named <paramref name="name"/>, from <paramref name="client"/>,
    /// its audio client with <paramref name="processing"/> already asked of it: the microphone's mix format, converted to
    /// PCM at Windows' default quality, without loopback. <paramref name="opened"/>, when given, is told how opening the
    /// stream went at <see cref="Start"/>: null once it is open, before anything is read, or the failure, before it is
    /// thrown; that is how the microphone factory learns that RAW was the trouble, and when to say how the microphone is
    /// recorded. The capture takes ownership of the client, and releases it when the format cannot be read or is not
    /// supported.
    /// </summary>
    public static WasapiAudioCapture ForMicrophone(AudioClient client, string id, string name, MicrophoneProcessing processing, ILogger logger,
        Action<Exception?>? opened = null) =>
        ForMicrophone(client, id, name, processing, logger, opened, packets: null);

    /// <summary>
    /// <see cref="ForMicrophone(AudioClient, string, string, MicrophoneProcessing, ILogger, Action{Exception})"/>, with the
    /// packets read from <paramref name="packets"/> instead of Windows when a test gives them.
    /// </summary>
    internal static WasapiAudioCapture ForMicrophone(AudioClient client, string id, string name, MicrophoneProcessing processing, ILogger logger,
        Action<Exception?>? opened, IPacketSource? packets)
    {
        try
        {
            var (streamFormat, format) = MixFormatOf(client, $"The microphone \"{name}\"");

            // A microphone hands over audio all the time, silence included.
            return new WasapiAudioCapture(client, id, name, streamFormat, format,
                AudioClientStreamFlags.AutoConvertPcm | AudioClientStreamFlags.SrcDefaultQuality,
                deliversContinuously: true, MicrophoneThreadName, logger, processing: processing, opened: opened, packets: packets);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    /// <summary>Opens the stream and starts the capture thread.</summary>
    public void Start()
    {
        if (_thread is not null)
            throw new InvalidOperationException("This capture has already been started.");

        try
        {
            _client.Initialize(
                AudioClientShareMode.Shared,
                _streamFlags,
                BufferMilliseconds * UnitsPerMillisecond,
                0,
                _streamFormat,
                Guid.Empty);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Windows did not open the stream for \"{Device}\" (0x{Code:X8})", DeviceName, ex.HResult);
            _opened?.Invoke(ex);
            throw;
        }

        if (_packetReady is not null)
            _client.SetEventHandle(_packetReady.SafeWaitHandle.DangerousGetHandle());

        // The stream is open and nothing has been read yet: the moment to say so, before the first audio.
        _opened?.Invoke(null);

        _thread = new Thread(CaptureLoop) { IsBackground = true, Name = _threadName, Priority = ThreadPriority.AboveNormal };
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

    /// <summary>
    /// Which speaker each channel of a mix format feeds, as Windows' channel mask, or 0 when the format does
    /// not say. NAudio 2.2.1 keeps the mask to itself, so it is read from the format as Windows lays it out.
    /// </summary>
    public static int ChannelMaskOf(WaveFormat format)
    {
        if (format is not WaveFormatExtensible) return 0;

        var pointer = WaveFormat.MarshalToPtr(format);
        try
        {
            return Marshal.ReadInt32(pointer, ChannelMaskOffset);
        }
        finally
        {
            Marshal.FreeHGlobal(pointer);
        }
    }

    /// <summary>Windows' flags for a packet as ClassScribe's, which number them differently.</summary>
    internal static PacketFlags FlagsOf(AudioClientBufferFlags flags)
    {
        var mapped = PacketFlags.None;
        if (flags.HasFlag(AudioClientBufferFlags.Silent)) mapped |= PacketFlags.Silent;
        if (flags.HasFlag(AudioClientBufferFlags.DataDiscontinuity)) mapped |= PacketFlags.Discontinuity;
        if (flags.HasFlag(AudioClientBufferFlags.TimestampError)) mapped |= PacketFlags.TimestampError;
        return mapped;
    }

    /// <summary>
    /// The mix format of <paramref name="client"/>, as the stream is opened with it and as the recorder reads it, or a
    /// <see cref="NotSupportedException"/> naming <paramref name="what"/> when its encoding is neither float nor PCM.
    /// </summary>
    private static (WaveFormat StreamFormat, AudioFormat Format) MixFormatOf(AudioClient client, string what)
    {
        var streamFormat = client.MixFormat;
        var format = streamFormat.AsStandardWaveFormat();
        if (format.Encoding is not (WaveFormatEncoding.IeeeFloat or WaveFormatEncoding.Pcm))
            throw new NotSupportedException($"{what} uses the unsupported encoding {format.Encoding}.");

        return (streamFormat, new AudioFormat(format.SampleRate, format.Channels, format.BitsPerSample, format.Encoding == WaveFormatEncoding.IeeeFloat, ChannelMaskOf(streamFormat)));
    }

    /// <summary>Body of the capture thread: collects audio from Windows until asked to stop or until the device fails.</summary>
    private void CaptureLoop()
    {
        Exception? failure = null;
        try
        {
            var packets = _packets(_client);
            var gaps = new CaptureGapWatch(TimeSpan.FromMilliseconds(BufferMilliseconds), GapReportInterval, TimeProvider.System);
            _client.Start();
            gaps.Drained(TimeSpan.Zero);
            while (!_stopRequested)
            {
                WaitForPackets();

                var began = Stopwatch.GetTimestamp();
                ReadPackets(packets);

                // Windows holds one second of audio for us. When the loop has not emptied it for that long,
                // because storing the audio was slow (a disk, usually) or because the loop did not get to run,
                // what arrived first is gone. At least it does not go unnoticed.
                if (gaps.Drained(Stopwatch.GetElapsedTime(began)) is { } gap) ReportGap(gap);
            }

            // Collect what arrived since the last poll.
            ReadPackets(packets);
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

    /// <summary>Waits a moment for audio: until Windows signals a packet for an app's sound, or a fixed poll interval for a device or a microphone.</summary>
    private void WaitForPackets()
    {
        if (_packetReady is null)
            Thread.Sleep(PollMilliseconds);
        else
            _packetReady.WaitOne(PollMilliseconds);
    }

    /// <summary>Warns that the recording probably has a gap, saying how long the capture was held up and how much of that went on storing the audio.</summary>
    private void ReportGap(CaptureGap gap)
    {
        if (gap.Count == 1)
            _logger.LogWarning(
                "The capture was held up for {Seconds:F1} seconds ({Storing:F1} of them storing the audio), longer than Windows keeps audio waiting; the recording probably has a gap there",
                gap.Longest.TotalSeconds, gap.Storing.TotalSeconds);
        else
            _logger.LogWarning(
                "The capture was held up {Count} times since the last such message, once for {Seconds:F1} seconds ({Storing:F1} of them storing the audio), longer than Windows keeps audio waiting; the recording probably has gaps there",
                gap.Count, gap.Longest.TotalSeconds, gap.Storing.TotalSeconds);
    }

    /// <summary>
    /// Hands every waiting packet to the listeners, each with its frame count, the stamp Windows gave its first frame
    /// and Windows' flags. Silent packets are delivered as zeros. The device position is never read: for process
    /// loopback it was wrong on nearly every packet.
    /// </summary>
    private void ReadPackets(IPacketSource packets)
    {
        while (packets.NextPacketFrames() > 0)
        {
            var pointer = packets.GetBuffer(out var frames, out var flags, out var qpc100ns);

            // Windows flags a packet that does not follow on from the one before. The first packet of a stream
            // has nothing to follow on from, and with loopback the packets stop altogether while nothing plays,
            // so the flag is no proof that audio was lost and is only noted for diagnosis. Real hold-ups are
            // found by timing the loop (see CaptureLoop).
            if (flags.HasFlag(AudioClientBufferFlags.DataDiscontinuity) && _packetsRead > 0)
                _logger.LogDebug("Windows marked a break in the audio captured from \"{Device}\"", DeviceName);
            _packetsRead++;

            // A packet whose stamp Windows could not trust is placed by its arrival instead (the session's rule).
            if (flags.HasFlag(AudioClientBufferFlags.TimestampError) && _unstampedPackets++ == 0)
                _logger.LogDebug("Windows could not stamp a packet captured from \"{Device}\"; such packets are placed by their arrival (further ones are not noted)", DeviceName);

            var byteCount = frames * _streamFormat.BlockAlign;
            if (_buffer.Length < byteCount) _buffer = new byte[byteCount];

            if (flags.HasFlag(AudioClientBufferFlags.Silent))
                Array.Clear(_buffer, 0, byteCount);
            else
                Marshal.Copy(pointer, _buffer, 0, byteCount);

            packets.ReleaseBuffer(frames);
            if (byteCount > 0)
                DataAvailable?.Invoke(this, new AudioDataEventArgs(_buffer, byteCount, frames, qpc100ns, FlagsOf(flags)));
        }
    }

    /// <summary>Windows' capture client as the loop reads it, through NAudio's public wrapper.</summary>
    private sealed class WindowsPackets(AudioCaptureClient capture) : IPacketSource
    {
        /// <inheritdoc />
        public int NextPacketFrames() => capture.GetNextPacketSize();

        /// <inheritdoc />
        public IntPtr GetBuffer(out int frames, out AudioClientBufferFlags flags, out long qpc100ns) =>
            capture.GetBuffer(out frames, out flags, out _, out qpc100ns);

        /// <inheritdoc />
        public void ReleaseBuffer(int frames) => capture.ReleaseBuffer(frames);
    }
}

/// <summary>
/// Where a capture thread reads its packets: Windows' capture client, or a test's stand-in, which lets the loop be
/// tested without a device. The shape is IAudioCaptureClient's, without the device position, which is never used.
/// </summary>
internal interface IPacketSource
{
    /// <summary>How many frames the next packet holds; 0 while none is waiting.</summary>
    int NextPacketFrames();

    /// <summary>
    /// Takes the next packet: where its audio is, how many frames it holds, Windows' flags for it, and the performance
    /// counter's value when its first frame was recorded, in 100-nanosecond units, which is meaningless when the flags
    /// say the stamp is wrong. The memory is Windows' until <see cref="ReleaseBuffer"/>.
    /// </summary>
    IntPtr GetBuffer(out int frames, out AudioClientBufferFlags flags, out long qpc100ns);

    /// <summary>Gives the packet's memory back to Windows.</summary>
    void ReleaseBuffer(int frames);
}
