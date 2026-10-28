namespace ClassScribe.Audio;

/// <summary>The adapter of <see cref="IAudioFileFacts"/>: a WAV file's length read from its header (see <see cref="WavHeader"/>) and its size on disk.</summary>
public sealed class AudioFileFacts : IAudioFileFacts
{
    /// <inheritdoc />
    public TimeSpan? AudioLength(string path)
    {
        Span<byte> header = stackalloc byte[WavHeader.Size];
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (file.ReadAtLeast(header, header.Length, throwOnEndOfStream: false) < header.Length || !WavHeader.TryRead(header, out var sampleRate))
            return null;

        // Two bytes to a sample: the recorder writes mono 16-bit audio.
        return TimeSpan.FromSeconds((file.Length - WavHeader.Size) / 2.0 / sampleRate);
    }
}
