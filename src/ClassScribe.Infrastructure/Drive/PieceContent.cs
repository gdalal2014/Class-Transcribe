using System.Net;
using System.Net.Http.Headers;

namespace ClassScribe.Drive;

/// <summary>
/// One piece of a file for Drive's resumable upload: the bytes from <c>offset</c> for <c>length</c> bytes of an open stream,
/// with its own Content-Length and Content-Range (bytes 0-8388607/756903404), read from the stream as it is sent, so memory
/// stays flat whatever the size of the file. It can be sent again, as after a renewed token, since it seeks to its offset
/// each time. An empty piece carries Content-Range bytes */size: it asks how much Drive has, and for a file of no bytes it is
/// the whole file.
/// </summary>
public sealed class PieceContent : HttpContent
{
    /// <summary>How much is copied at a time.</summary>
    private const int CopyBuffer = 81920;

    private readonly Stream _source;
    private readonly long _offset;
    private readonly int _length;

    /// <summary>The piece of <paramref name="source"/> from <paramref name="offset"/>, <paramref name="length"/> bytes long, of a file of <paramref name="total"/> bytes.</summary>
    public PieceContent(Stream source, long offset, int length, long total)
    {
        _source = source;
        _offset = offset;
        _length = length;
        Headers.ContentLength = length;
        Headers.ContentRange = length == 0 ? new ContentRangeHeaderValue(total) : new ContentRangeHeaderValue(offset, offset + length - 1, total);
    }

    /// <summary>The empty piece that asks Drive how much of a file of <paramref name="total"/> bytes it has: Content-Range bytes */total.</summary>
    public static HttpContent StatusQuery(long total)
    {
        var query = new ByteArrayContent([]);
        query.Headers.ContentRange = new ContentRangeHeaderValue(total);
        return query;
    }

    /// <inheritdoc />
    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => SerializeToStreamAsync(stream, context, CancellationToken.None);

    /// <summary>Copies the piece from the source to <paramref name="stream"/>; a source that ends before the piece does is an error, as the file has shrunk.</summary>
    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
    {
        _source.Seek(_offset, SeekOrigin.Begin);
        var buffer = new byte[Math.Min(CopyBuffer, Math.Max(_length, 1))];
        var left = _length;
        while (left > 0)
        {
            var read = await _source.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, left)), cancellationToken);
            if (read == 0) throw new IOException("The file ended before the piece did: it is shorter than when its upload began.");

            await stream.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            left -= read;
        }
    }

    /// <inheritdoc />
    protected override bool TryComputeLength(out long length)
    {
        length = _length;
        return true;
    }
}
