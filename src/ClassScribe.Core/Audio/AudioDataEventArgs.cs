namespace ClassScribe.Audio;

/// <summary>What Windows says about a packet of captured audio, as AUDCLNT_BUFFERFLAGS says it.</summary>
[Flags]
public enum PacketFlags
{
    /// <summary>Nothing to note.</summary>
    None = 0,

    /// <summary>The packet is silence; Windows may hand it over without writing the samples, and the capture zeroes them.</summary>
    Silent = 1,

    /// <summary>The packet does not follow on from the one before: audio was lost between them, or the stream has just started.</summary>
    Discontinuity = 2,

    /// <summary>Windows could not stamp the packet's first frame, so <see cref="AudioDataEventArgs.Qpc100ns"/> is null and the packet is placed by its arrival.</summary>
    TimestampError = 4
}

/// <summary>
/// Audio handed over by a capture. The buffer is reused, so read it before the handler returns. A packet from Windows
/// carries the time its first frame was recorded, on the performance counter (QPC) in 100-nanosecond units, which puts
/// every track of a recording on one clock; a packet whose stamp Windows flagged as wrong carries none.
/// </summary>
public sealed class AudioDataEventArgs(byte[] buffer, int byteCount, int frames = 0, long? qpc100ns = null, PacketFlags flags = PacketFlags.None) : EventArgs
{
    /// <summary>Buffer holding the captured audio.</summary>
    public byte[] Buffer { get; } = buffer;

    /// <summary>Number of valid bytes at the start of <see cref="Buffer"/>.</summary>
    public int ByteCount { get; } = byteCount;

    /// <summary>How many frames the packet holds, one sample per channel each; 0 when the capture did not count them.</summary>
    public int Frames { get; } = frames;

    /// <summary>
    /// When the packet's first frame was recorded: the performance counter's value in 100-nanosecond units, as
    /// Windows hands it over. Null when the capture gives no stamps, and when Windows flagged the stamp as wrong
    /// (<see cref="PacketFlags.TimestampError"/>), whatever number came with the flag.
    /// </summary>
    public long? Qpc100ns { get; } = flags.HasFlag(PacketFlags.TimestampError) ? null : qpc100ns;

    /// <summary>What Windows said about the packet.</summary>
    public PacketFlags Flags { get; } = flags;
}

/// <summary>Raised when a capture ends. <see cref="Error"/> is set when it ended because of a failure.</summary>
public sealed class CaptureStoppedEventArgs(Exception? error) : EventArgs
{
    /// <summary>The failure that ended the capture, if any.</summary>
    public Exception? Error { get; } = error;
}
