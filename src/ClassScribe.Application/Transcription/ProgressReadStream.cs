namespace ClassScribe.Transcription;

/// <summary>A read-only stream wrapper that reports how many bytes have been read through it.</summary>
public sealed class ProgressReadStream(Stream inner, Action<long> onProgress) : Stream
{
    private long _totalRead;

    /// <inheritdoc />
    public override bool CanRead => true;

    /// <inheritdoc />
    public override bool CanSeek => inner.CanSeek;

    /// <inheritdoc />
    public override bool CanWrite => false;

    /// <inheritdoc />
    public override long Length => inner.Length;

    /// <inheritdoc />
    public override long Position
    {
        get => inner.Position;
        set => inner.Position = value;
    }

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count) => Report(inner.Read(buffer, offset, count));

    /// <inheritdoc />
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        Report(await inner.ReadAsync(buffer, cancellationToken));

    /// <inheritdoc />
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

    /// <inheritdoc />
    public override void Flush() { }

    /// <inheritdoc />
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing) inner.Dispose();
        base.Dispose(disposing);
    }

    /// <summary>Adds the bytes just read to the total and passes the total on.</summary>
    private int Report(int read)
    {
        if (read > 0)
        {
            _totalRead += read;
            onProgress(_totalRead);
        }

        return read;
    }
}
