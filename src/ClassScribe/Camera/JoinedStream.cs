using ClassScribe.Link;

namespace ClassScribe.Camera;

/// <summary>
/// One stream over two: reads from <paramref name="reader"/>, such as a process's standard input or the read end of a pipe,
/// and writes to <paramref name="writer"/>, such as its standard output, so that a <see cref="LineChannel"/> speaks over a
/// pair of one-way pipes. Disposing it disposes both.
/// </summary>
/// <param name="reader">Where it reads from.</param>
/// <param name="writer">Where it writes to.</param>
public sealed class JoinedStream(Stream reader, Stream writer) : Stream
{
    /// <inheritdoc />
    public override bool CanRead => true;

    /// <inheritdoc />
    public override bool CanSeek => false;

    /// <inheritdoc />
    public override bool CanWrite => true;

    /// <inheritdoc />
    public override long Length => throw new NotSupportedException();

    /// <inheritdoc />
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count) => reader.Read(buffer, offset, count);

    /// <inheritdoc />
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => reader.ReadAsync(buffer, cancellationToken);

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count) => writer.Write(buffer, offset, count);

    /// <inheritdoc />
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => writer.WriteAsync(buffer, cancellationToken);

    /// <inheritdoc />
    public override void Flush() => writer.Flush();

    /// <inheritdoc />
    public override Task FlushAsync(CancellationToken cancellationToken) => writer.FlushAsync(cancellationToken);

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <summary>Disposes both streams.</summary>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            reader.Dispose();
            writer.Dispose();
        }

        base.Dispose(disposing);
    }

    /// <summary>Disposes both streams.</summary>
    public override async ValueTask DisposeAsync()
    {
        await reader.DisposeAsync();
        await writer.DisposeAsync();
        await base.DisposeAsync();
    }
}
