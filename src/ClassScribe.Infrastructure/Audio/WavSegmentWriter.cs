using System.Buffers.Binary;

namespace ClassScribe.Audio;

/// <summary>
/// Writes mono 16-bit PCM to a WAV file. Each write goes to Windows at once, with no buffer of the writer's
/// own, so a crash, a kill, a shutdown or a sign-out loses at most the write in progress, and
/// <see cref="DataBytes"/> counts only what Windows accepted. <see cref="Flush"/> rewrites the header sizes and
/// pushes everything to the disk itself, so the file stays a valid WAV while recording and a power cut loses
/// at most what Windows had not yet written to the disk since the last flush. It is the writer the WAV track writer factory
/// opens for a recording session (see <see cref="ITrackWriter"/>).
/// </summary>
public sealed class WavSegmentWriter : ITrackWriter
{
    private const int BytesPerSample = 2;

    private readonly FileStream _stream;
    private bool _disposed;

    /// <summary>Creates the file. Fails if it already exists, so a recording is never overwritten.</summary>
    public WavSegmentWriter(string path, int sampleRate)
    {
        Path = path;
        SampleRate = sampleRate;
        // No buffer of its own (bufferSize 0): every write is handed to Windows as it comes (see the class summary).
        _stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, bufferSize: 0);
        try
        {
            // On disk the file is a valid, empty WAV from its first moment.
            WriteHeader();
            _stream.Flush();
        }
        catch
        {
            _stream.Dispose();
            throw;
        }
    }

    /// <summary>Full path of the WAV file.</summary>
    public string Path { get; }

    /// <summary>Sample rate of the audio in the file.</summary>
    public int SampleRate { get; }

    /// <summary>Bytes of audio written so far, every one of them accepted by Windows.</summary>
    public long DataBytes { get; private set; }

    /// <summary>Length of the audio written so far.</summary>
    public TimeSpan Duration => TimeSpan.FromSeconds(DataBytes / (double)(SampleRate * BytesPerSample));

    /// <summary>Appends mono 16-bit PCM samples, handing them to Windows at once. A write that fails is not counted.</summary>
    public void Write(ReadOnlySpan<byte> mono16)
    {
        _stream.Write(mono16);
        DataBytes += mono16.Length;
    }

    /// <summary>
    /// Rewrites the header with the current sizes and pushes everything to disk. When that fails, the writer is left
    /// as it was: the next audio still goes after the audio already written, and the next flush tries the header again.
    /// </summary>
    public void Flush()
    {
        var end = _stream.Position;
        _stream.Position = 0;
        try
        {
            WriteHeader();
        }
        finally
        {
            // Back to the end even when the header could not be written, or the next audio would go over the header.
            _stream.Position = end;
        }

        _stream.Flush(flushToDisk: true);
    }

    /// <summary>Finalises the header and closes the file. The file is closed even when the last write fails.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            Flush();
        }
        finally
        {
            _stream.Dispose();
        }
    }

    /// <summary>Writes the 44-byte RIFF/WAVE header for mono 16-bit PCM.</summary>
    private void WriteHeader()
    {
        var dataSize = (uint)Math.Min(DataBytes, uint.MaxValue - 36);
        Span<byte> header = stackalloc byte[WavHeader.Size];
        "RIFF"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..], 36 + dataSize);
        "WAVE"u8.CopyTo(header[8..]);
        "fmt "u8.CopyTo(header[12..]);
        BinaryPrimitives.WriteUInt32LittleEndian(header[16..], 16);                                  // fmt chunk size
        BinaryPrimitives.WriteUInt16LittleEndian(header[20..], 1);                                   // PCM
        BinaryPrimitives.WriteUInt16LittleEndian(header[22..], 1);                                   // mono
        BinaryPrimitives.WriteUInt32LittleEndian(header[24..], (uint)SampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(header[28..], (uint)(SampleRate * BytesPerSample)); // bytes per second
        BinaryPrimitives.WriteUInt16LittleEndian(header[32..], BytesPerSample);                      // block align
        BinaryPrimitives.WriteUInt16LittleEndian(header[34..], 16);                                  // bits per sample
        "data"u8.CopyTo(header[36..]);
        BinaryPrimitives.WriteUInt32LittleEndian(header[40..], dataSize);
        _stream.Write(header);
    }
}
