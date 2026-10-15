using System.Buffers.Binary;

namespace ClassScribe.Audio;

/// <summary>Turns interleaved device audio into mono 16-bit PCM, the format the recorder stores.</summary>
public static class PcmConverter
{
    /// <summary>Returns true when <see cref="ToMono16"/> can read the given format.</summary>
    public static bool IsSupported(AudioFormat format) =>
        format is { Channels: >= 1, SampleRate: > 0 } &&
        (format.IsFloat ? format.BitsPerSample == 32 : format.BitsPerSample is 16 or 24 or 32);

    /// <summary>
    /// Converts the whole frames in <paramref name="source"/> to mono 16-bit little-endian PCM.
    /// Stereo and surround input is reduced to the average of its first two channels.
    /// </summary>
    /// <returns>The number of bytes written to <paramref name="destination"/>.</returns>
    public static int ToMono16(ReadOnlySpan<byte> source, AudioFormat format, Span<byte> destination)
    {
        if (!IsSupported(format))
            throw new NotSupportedException($"Unsupported capture format: {format}.");

        var bytesPerSample = format.BitsPerSample / 8;
        var frameSize = format.BytesPerFrame;
        var frames = source.Length / frameSize;
        if (destination.Length < frames * 2)
            throw new ArgumentException("Destination buffer is too small.", nameof(destination));

        var mixedChannels = Math.Min(format.Channels, 2);
        for (var i = 0; i < frames; i++)
        {
            var frame = source.Slice(i * frameSize, frameSize);
            var sum = 0f;
            for (var channel = 0; channel < mixedChannels; channel++)
                sum += ReadSample(frame.Slice(channel * bytesPerSample, bytesPerSample), format);

            var mono = sum / mixedChannels;
            var clamped = float.IsNaN(mono) ? 0f : Math.Clamp(mono, -1f, 1f);
            BinaryPrimitives.WriteInt16LittleEndian(destination.Slice(i * 2, 2), (short)MathF.Round(clamped * short.MaxValue));
        }

        return frames * 2;
    }

    /// <summary>Returns the largest absolute sample value in a mono 16-bit buffer.</summary>
    public static int Peak(ReadOnlySpan<byte> mono16)
    {
        var peak = 0;
        for (var i = 0; i + 1 < mono16.Length; i += 2)
            peak = Math.Max(peak, Math.Abs((int)BinaryPrimitives.ReadInt16LittleEndian(mono16.Slice(i, 2))));

        return peak;
    }

    /// <summary>Reads one sample and scales it to the range -1 to 1.</summary>
    private static float ReadSample(ReadOnlySpan<byte> sample, AudioFormat format) => (format.IsFloat, format.BitsPerSample) switch
    {
        (true, 32) => BinaryPrimitives.ReadSingleLittleEndian(sample),
        (false, 16) => BinaryPrimitives.ReadInt16LittleEndian(sample) / 32768f,
        (false, 24) => (sample[0] | (sample[1] << 8) | ((sbyte)sample[2] << 16)) / 8388608f,
        (false, 32) => BinaryPrimitives.ReadInt32LittleEndian(sample) / 2147483648f,
        _ => throw new NotSupportedException($"Unsupported capture format: {format}.")
    };
}
