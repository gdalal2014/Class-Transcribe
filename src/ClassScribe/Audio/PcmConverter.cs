using System.Buffers.Binary;

namespace ClassScribe.Audio;

/// <summary>Turns interleaved device audio into mono 16-bit PCM, the format the recorder stores.</summary>
public static class PcmConverter
{
    /// <summary>The share of the mix that the centre channel, and each pair of surround channels, gets: -3 dB.</summary>
    private const float MinusThreeDb = 0.70710677f;

    /// <summary>Front left, as Windows numbers the speakers in a channel mask.</summary>
    private const int FrontLeft = 0x1;

    /// <summary>Front right.</summary>
    private const int FrontRight = 0x2;

    /// <summary>Front centre, where players put the dialogue of a film, and Firefox a mono track.</summary>
    private const int FrontCentre = 0x4;

    /// <summary>The low-frequency (subwoofer) channel.</summary>
    private const int LowFrequency = 0x8;

    /// <summary>The 5.1 layout Windows uses when a six-channel format does not say: front left, right and centre, low frequency, back left and right.</summary>
    private const int FivePointOne = 0x3F;

    /// <summary>The 7.1 layout Windows uses when an eight-channel format does not say: 5.1 with side left and right.</summary>
    private const int SevenPointOne = 0x63F;

    /// <summary>Returns true when <see cref="ToMono16"/> can read the given format.</summary>
    public static bool IsSupported(AudioFormat format) =>
        format is { Channels: >= 1, SampleRate: > 0 } &&
        (format.IsFloat ? format.BitsPerSample == 32 : format.BitsPerSample is 16 or 24 or 32);

    /// <summary>
    /// Converts the whole frames in <paramref name="source"/> to mono 16-bit little-endian PCM. Stereo is
    /// averaged. On a surround device the front pair is averaged too, while the centre channel and each pair
    /// of surround channels are added at -3 dB, so speech a player sends to the centre is kept, and the
    /// low-frequency channel is left out. Which channel feeds which speaker comes from the format's channel
    /// mask, or from Windows' usual 5.1 and 7.1 layouts; a channel whose speaker is not known is left out,
    /// so a layout nothing describes mixes its first two channels only. The result is clamped to full scale.
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

        Span<float> weights = format.Channels <= 32 ? stackalloc float[format.Channels] : new float[format.Channels];
        MixWeights(format, weights);
        for (var i = 0; i < frames; i++)
        {
            var frame = source.Slice(i * frameSize, frameSize);
            var mono = 0f;
            for (var channel = 0; channel < weights.Length; channel++)
            {
                if (weights[channel] != 0)
                    mono += weights[channel] * ReadSample(frame.Slice(channel * bytesPerSample, bytesPerSample), format);
            }

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

    /// <summary>Fills in each channel's share of the mono mix, from the speaker the channel feeds. Channels whose speaker is not known keep a share of zero.</summary>
    private static void MixWeights(AudioFormat format, Span<float> weights)
    {
        if (weights.Length == 1)
        {
            weights[0] = 1f;
            return;
        }

        var mask = format.ChannelMask != 0 ? format.ChannelMask
            : weights.Length switch { 6 => FivePointOne, 8 => SevenPointOne, _ => FrontLeft | FrontRight };
        var channel = 0;
        for (var bit = 0; bit < 32 && channel < weights.Length; bit++)
        {
            var speaker = 1 << bit;
            if ((mask & speaker) == 0) continue;

            weights[channel++] = speaker switch
            {
                FrontLeft or FrontRight => 0.5f,
                FrontCentre => MinusThreeDb,
                LowFrequency => 0f,
                _ => MinusThreeDb / 2   // a surround or other channel, one of a pair that gets -3 dB together
            };
        }
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
