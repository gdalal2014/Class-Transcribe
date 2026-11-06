using System.Buffers.Binary;

namespace ClassScribe.Audio;

/// <summary>
/// The header of the WAV files ClassScribe writes, mono 16-bit PCM: its size, after which the audio starts, and its reading
/// from bytes, which tells ClassScribe's own files from other tools' and gives their sample rate.
/// </summary>
public static class WavHeader
{
    /// <summary>Size of the header ClassScribe's WAV writer produces. The audio starts right after it.</summary>
    public const int Size = 44;

    /// <summary>
    /// Recognises a header written by ClassScribe's WAV writer and reads its sample rate. The check on the two size
    /// fields tells our files apart from other tools' WAV files, which may carry extra chunks after the audio.
    /// </summary>
    public static bool TryRead(ReadOnlySpan<byte> header, out int sampleRate)
    {
        sampleRate = 0;
        if (header.Length < Size) return false;

        var ours = header[..4].SequenceEqual("RIFF"u8)
            && header[8..12].SequenceEqual("WAVE"u8)
            && header[12..16].SequenceEqual("fmt "u8)
            && BinaryPrimitives.ReadUInt32LittleEndian(header[16..]) == 16
            && BinaryPrimitives.ReadUInt16LittleEndian(header[20..]) == 1
            && BinaryPrimitives.ReadUInt16LittleEndian(header[22..]) == 1
            && BinaryPrimitives.ReadUInt16LittleEndian(header[34..]) == 16
            && header[36..40].SequenceEqual("data"u8)
            && BinaryPrimitives.ReadUInt32LittleEndian(header[4..]) == 36 + BinaryPrimitives.ReadUInt32LittleEndian(header[40..]);

        if (!ours) return false;

        var rate = BinaryPrimitives.ReadUInt32LittleEndian(header[24..]);
        if (rate is 0 or > 768_000) return false;

        sampleRate = (int)rate;
        return true;
    }
}