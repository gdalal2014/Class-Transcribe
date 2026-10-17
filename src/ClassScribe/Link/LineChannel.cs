using System.Buffers;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace ClassScribe.Link;

/// <summary>
/// One end of a connection of the link: messages go out and come in as lines of UTF-8 JSON, one message per line, each
/// {"type":"&lt;type&gt;","body":{...}} ended by a line feed. JSON writes a line break inside a text as \n, so a message
/// never spans two lines. A line may hold at most <see cref="LinkProtocol.MaxLineBytes"/> bytes: a longer one is neither
/// sent nor read, so a peer that never ends its line cannot fill the reader's memory. The channel owns its stream and
/// closes it when it is disposed.
/// </summary>
/// <remarks>
/// Several threads may send at once: each message goes out whole, one after another. Only one may receive at a time.
/// </remarks>
/// <param name="stream">The connection, such as a named pipe.</param>
public sealed class LineChannel(Stream stream) : IAsyncDisposable
{
    /// <summary>The line feed that ends every line.</summary>
    private const byte LineFeed = (byte)'\n';

    /// <summary>The bytes read and not yet taken as lines: room for the longest line and its line feed.</summary>
    private readonly byte[] _buffer = new byte[LinkProtocol.MaxLineBytes + 1];

    /// <summary>Lets one message out at a time, so that two sent at once never mix.</summary>
    private readonly SemaphoreSlim _sending = new(1, 1);

    /// <summary>Where the bytes not yet taken begin in <see cref="_buffer"/>.</summary>
    private int _start;

    /// <summary>Where the bytes read so far end in <see cref="_buffer"/>.</summary>
    private int _end;

    /// <summary>
    /// Sends <paramref name="body"/> as a message of <paramref name="type"/>, written by <paramref name="info"/>, as one line,
    /// and waits until the stream has taken it. A message that would not fit in a line is not sent at all: an
    /// <see cref="ArgumentException"/> says so. A stream that fails, such as a broken pipe, throws its own exception, and one
    /// cancelled by <paramref name="cancellationToken"/> an <see cref="OperationCanceledException"/>; the connection is then
    /// best closed, since part of a line may have gone.
    /// </summary>
    public async ValueTask SendAsync<T>(string type, T body, JsonTypeInfo<T> info, CancellationToken cancellationToken)
    {
        var line = Encode(type, body, info);

        await _sending.WaitAsync(cancellationToken);
        try
        {
            await stream.WriteAsync(line, cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }
        finally
        {
            _sending.Release();
        }
    }

    /// <summary>
    /// Receives the next message, waiting for it as long as it takes. Blank lines are passed over. Returns null at the end of
    /// the stream; a line the end cut off is dropped. Throws an <see cref="InvalidDataException"/> for a line longer than
    /// <see cref="LinkProtocol.MaxLineBytes"/> and for a line that is not a message of the link (not JSON, or without a
    /// type), after which the connection is best closed. A receive cancelled by <paramref name="cancellationToken"/> throws
    /// an <see cref="OperationCanceledException"/> and loses nothing: the next one goes on from where it stopped.
    /// </summary>
    public async ValueTask<Envelope?> ReceiveAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            var newline = _buffer.AsSpan(_start, _end - _start).IndexOf(LineFeed);
            if (newline >= 0)
            {
                var line = _buffer.AsMemory(_start, newline);
                _start += newline + 1;
                if (line.Span.Trim(" \t\r"u8).IsEmpty) continue;

                return Parse(line.Span);
            }

            // The bytes not taken are the start of a line whose end has not come yet. When they fill the buffer, the line is
            // longer than any line may be.
            if (_end - _start > LinkProtocol.MaxLineBytes)
                throw new InvalidDataException($"The other end of the link sent a line longer than {LinkProtocol.MaxLineBytes / 1024} KB, which is not a message of ClassScribe's.");

            if (_start > 0)
            {
                _buffer.AsSpan(_start, _end - _start).CopyTo(_buffer);
                _end -= _start;
                _start = 0;
            }

            var read = await stream.ReadAsync(_buffer.AsMemory(_end), cancellationToken);
            if (read == 0) return null;

            _end += read;
        }
    }

    /// <summary>Closes the stream.</summary>
    public ValueTask DisposeAsync() => stream.DisposeAsync();

    /// <summary>
    /// The line for a message: {"type":..., "body":...} on one line, then a line feed. Throws an
    /// <see cref="ArgumentException"/> when it is longer than a line may be.
    /// </summary>
    internal static ReadOnlyMemory<byte> Encode<T>(string type, T body, JsonTypeInfo<T> info)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("type", type);
            writer.WritePropertyName("body");
            JsonSerializer.Serialize(writer, body, info);
            writer.WriteEndObject();
        }

        if (buffer.WrittenCount > LinkProtocol.MaxLineBytes)
            throw new ArgumentException(
                $"A {type} message of {buffer.WrittenCount} bytes is longer than the {LinkProtocol.MaxLineBytes / 1024} KB a line of the link may hold, so it was not sent.",
                nameof(body));

        buffer.Write([LineFeed]);
        return buffer.WrittenMemory;
    }

    /// <summary>The message a line holds. Throws an <see cref="InvalidDataException"/> when it holds none.</summary>
    private static Envelope Parse(ReadOnlySpan<byte> line)
    {
        try
        {
            return JsonSerializer.Deserialize(line, LinkJson.Default.Envelope)
                ?? throw new InvalidDataException("The other end of the link sent a line holding null, which is not a message of ClassScribe's.");
        }
        catch (JsonException ex)
        {
            // Deliberately not rethrown as is: handed on as what it means on the link, a line that is not a message, with
            // the reader's own failure inside, so that the server lets that window go and the client mends the link, as
            // both do for any InvalidDataException from the channel.
            throw new InvalidDataException($"The other end of the link sent a line that is not a message of ClassScribe's: {ex.Message}", ex);
        }
    }
}
