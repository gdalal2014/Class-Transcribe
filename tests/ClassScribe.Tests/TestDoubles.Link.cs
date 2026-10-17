using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using ClassScribe.Link;
using ClassScribe.Recorder;
using Microsoft.Extensions.Logging;

namespace ClassScribe.Tests;

/// <summary>
/// A window as the server's tests play it: it speaks the link's protocol by hand over an in-memory connection to a
/// <see cref="RecorderServer"/>, which serves it as it would a window on the pipe. Every wait has a limit, so a test that
/// waits for a message that never comes fails rather than hangs.
/// </summary>
public sealed class TestWindow : IAsyncDisposable
{
    /// <summary>How long a window waits for a message before the test fails.</summary>
    public static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    /// <summary>A window on <paramref name="pair"/>, served by <paramref name="served"/>.</summary>
    private TestWindow(DuplexStreamPair pair, Task served)
    {
        Pair = pair;
        Served = served;
        Channel = new LineChannel(pair.WindowEnd);
    }

    /// <summary>The connection to the server.</summary>
    public DuplexStreamPair Pair { get; }

    /// <summary>The window's end of the link.</summary>
    public LineChannel Channel { get; }

    /// <summary>The server serving this window: it completes when the server lets the window go.</summary>
    public Task Served { get; }

    /// <summary>The number of the last log line <see cref="LinesUpToAsync"/> has read so far; 0 before the first.</summary>
    public long LastSeq => Interlocked.Read(ref _lastSeq);

    /// <summary>The number of the last log line read; see <see cref="LastSeq"/>.</summary>
    private long _lastSeq;

    /// <summary>
    /// A hello of this build's protocol from a window that has seen the log up to <paramref name="since"/>: one a person can
    /// type into unless <paramref name="interactive"/> is false, opened by the recorder when <paramref name="alert"/> is
    /// true, and the window that started the run when <paramref name="owner"/> is true.
    /// </summary>
    public static Hello Hello(long since = 0, bool interactive = true, bool alert = false, bool owner = false) =>
        new(LinkProtocol.Version, "1.0.0+test", since, interactive, alert, owner);

    /// <summary>
    /// Connects a window to <paramref name="server"/> over a connection whose directions each hold
    /// <paramref name="capacity"/> bytes, and says <paramref name="hello"/>, unless it is null.
    /// </summary>
    public static async Task<TestWindow> ConnectAsync(RecorderServer server, Hello? hello, int capacity = DuplexStreamPair.PipeBuffer)
    {
        var pair = new DuplexStreamPair(capacity);
        var window = new TestWindow(pair, server.ServeAsync(pair.RecorderEnd, CancellationToken.None));
        if (hello is not null) await window.SendAsync(LinkTypes.Hello, hello, LinkJson.Default.Hello);
        return window;
    }

    /// <summary>Connects a window to <paramref name="server"/> with <paramref name="hello"/> (this build's, from the start, unless given) and reads its welcome.</summary>
    public static async Task<(TestWindow Window, Welcome Welcome)> AttachAsync(RecorderServer server, Hello? hello = null, int capacity = DuplexStreamPair.PipeBuffer)
    {
        var window = await ConnectAsync(server, hello ?? Hello(), capacity);
        return (window, await window.NextAsync(LinkTypes.Welcome, LinkJson.Default.Welcome));
    }

    /// <summary>The next message the server sends, or null once it has let the window go.</summary>
    public async Task<Envelope?> NextAsync() => await Channel.ReceiveAsync(CancellationToken.None).AsTask().WaitAsync(Patience);

    /// <summary>The body of the next message, which must be of <paramref name="type"/>.</summary>
    public async Task<T> NextAsync<T>(string type, JsonTypeInfo<T> info)
    {
        var envelope = await NextAsync();
        Assert.That(envelope?.Type, Is.EqualTo(type), "the next message from the recorder");
        return envelope!.Body.Deserialize(info)!;
    }

    /// <summary>
    /// Reads until a log line numbered <paramref name="lastSeq"/> or later has come, and returns the log lines read, in
    /// order; other messages are kept in <paramref name="others"/> when it is given.
    /// </summary>
    public async Task<List<LogLine>> LinesUpToAsync(long lastSeq, List<Envelope>? others = null)
    {
        List<LogLine> lines = [];
        while (lines.Count == 0 || lines[^1].Seq < lastSeq)
        {
            var envelope = await NextAsync() ?? throw new AssertionException($"The recorder let the window go before line {lastSeq}; it had sent {lines.Count} lines.");
            if (envelope.Type == LinkTypes.Log)
            {
                lines.Add(envelope.Body.Deserialize(LinkJson.Default.LogLine)!);
                Interlocked.Exchange(ref _lastSeq, lines[^1].Seq);
            }
            else
            {
                others?.Add(envelope);
            }
        }

        return lines;
    }

    /// <summary>Reads until the run's end, and returns the messages before it and the end itself.</summary>
    public async Task<(List<Envelope> Before, End End)> UntilEndAsync()
    {
        List<Envelope> before = [];
        while (true)
        {
            var envelope = await NextAsync() ?? throw new AssertionException($"The recorder let the window go before the end; it had sent {before.Count} messages.");
            if (envelope.Type == LinkTypes.End) return (before, envelope.Body.Deserialize(LinkJson.Default.End)!);
            before.Add(envelope);
        }
    }

    /// <summary>Reads until the server lets the window go, and returns every message read on the way.</summary>
    public async Task<List<Envelope>> RestAsync()
    {
        List<Envelope> messages = [];
        while (await NextAsync() is { } envelope) messages.Add(envelope);
        return messages;
    }

    /// <summary>Sends a message, as a window does.</summary>
    public async Task SendAsync<T>(string type, T body, JsonTypeInfo<T> info) => await Channel.SendAsync(type, body, info, CancellationToken.None).AsTask().WaitAsync(Patience);

    /// <summary>Sends <paramref name="text"/> as it is, as a program that does not speak the protocol might.</summary>
    public async Task SendRawAsync(string text) => await Pair.WindowEnd.WriteAsync(Encoding.UTF8.GetBytes(text)).AsTask().WaitAsync(Patience);

    /// <summary>Passes on the key named <paramref name="key"/>.</summary>
    public Task SendKeyAsync(string key) => SendAsync(LinkTypes.Key, new KeyPress(key), LinkJson.Default.KeyPress);

    /// <summary>Waits until the server has let the window go.</summary>
    public async Task GoneAsync() => await Served.WaitAsync(Patience);

    /// <summary>Closes the window's end.</summary>
    public async ValueTask DisposeAsync()
    {
        await Channel.DisposeAsync();
        Pair.Dispose();
    }
}

/// <summary>
/// A recorder as the client's tests play it: a real <see cref="RecorderServer"/> over a log of its own, on the test's clock,
/// reached through in-memory connections that <see cref="OpenPipeAsync"/> hands out as the pipe would. The test can make
/// the pipe not answer (<see cref="Answering"/>), refuse as Windows would (<see cref="Refusal"/>), answer by hand instead of
/// the server (<see cref="Answer"/>), and break a connection (<see cref="BreakLast"/>). It notes each time a window opens
/// the pipe, and when on its clock.
/// </summary>
public sealed class TestRecorder : IAsyncDisposable
{
    /// <summary>Guards the connections and the openings.</summary>
    private readonly Lock _gate = new();

    /// <summary>The connections handed out, in order.</summary>
    private readonly List<DuplexStreamPair> _connections = [];

    /// <summary>When each opening of the pipe was tried, on the clock, counted from <see cref="_started"/>.</summary>
    private readonly List<TimeSpan> _openedAt = [];

    /// <summary>When the recorder was made, on the steady clock.</summary>
    private readonly long _started;

    /// <summary>A recorder of <paramref name="run"/>, on <paramref name="time"/>, serving windows as soon as one opens the pipe.</summary>
    public TestRecorder(ManualTimeProvider time, RunRecord run)
    {
        Time = time;
        Run = run;
        _started = time.GetTimestamp();
        Log = new RecorderLog(time);
        Server = new RecorderServer(Log, time, ServerLog);
        Server.Begin(() => Run);
    }

    /// <summary>The clock.</summary>
    public ManualTimeProvider Time { get; }

    /// <summary>The run, as the server welcomes windows with it.</summary>
    public RunRecord Run { get; set; }

    /// <summary>The recorder's log.</summary>
    public RecorderLog Log { get; }

    /// <summary>What the server logged.</summary>
    public RecordingLogger<RecorderServer> ServerLog { get; } = new();

    /// <summary>The server.</summary>
    public RecorderServer Server { get; }

    /// <summary>False while no instance of the pipe is free, as before the recorder has made it: opening it times out.</summary>
    public bool Answering { get; set; } = true;

    /// <summary>When set, opening the pipe throws this, as Windows does for a pipe made with other rights.</summary>
    public Exception? Refusal { get; set; }

    /// <summary>When set, a new connection is answered by this, by hand, instead of by the server.</summary>
    public Func<RecorderByHand, Task>? Answer { get; set; }

    /// <summary>The connections handed out, in order.</summary>
    public IReadOnlyList<DuplexStreamPair> Connections
    {
        get { lock (_gate) return [.. _connections]; }
    }

    /// <summary>When, on the clock, each opening of the pipe was tried, counted from when the recorder was made.</summary>
    public IReadOnlyList<TimeSpan> OpenedAt
    {
        get { lock (_gate) return [.. _openedAt]; }
    }

    /// <summary>The names of the pipes windows opened.</summary>
    public List<string> PipeNames { get; } = [];

    /// <summary>Opens the pipe as a window does, through the in-memory connection the server serves (or <see cref="Answer"/> answers).</summary>
    public Task<Stream> OpenPipeAsync(string pipeName, TimeSpan limit, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _openedAt.Add(Time.GetElapsedTime(_started));
            PipeNames.Add(pipeName);
        }

        if (Refusal is { } refusal) return Task.FromException<Stream>(refusal);
        if (!Answering) return Task.FromException<Stream>(new TimeoutException("No instance of the pipe is free."));

        var pair = new DuplexStreamPair();
        lock (_gate) _connections.Add(pair);
        if (Answer is { } answer) _ = answer(new RecorderByHand(pair));
        else _ = Server.ServeAsync(pair.RecorderEnd, CancellationToken.None);

        return Task.FromResult(pair.WindowEnd);
    }

    /// <summary>Breaks the last connection handed out, as the end of the recorder's process does.</summary>
    public void BreakLast() => Connections[^1].Break();

    /// <summary>Writes the lines "line &lt;from&gt;" to "line &lt;to&gt;" into the log; its first line is numbered 1.</summary>
    public void WriteLines(int to, int from = 1)
    {
        for (var n = from; n <= to; n++) Log.Write(LogLevel.Information, $"line {n}");
    }

    /// <summary>Stops the server and closes every connection.</summary>
    public async ValueTask DisposeAsync()
    {
        await Server.DisposeAsync();
        foreach (var pair in Connections) pair.Dispose();
        Log.Dispose();
    }
}

/// <summary>The recorder's end of one in-memory connection, answered by hand by a test, to make a recorder say what the server never would.</summary>
/// <param name="pair">The connection.</param>
public sealed class RecorderByHand(DuplexStreamPair pair)
{
    /// <summary>The recorder's end of the link.</summary>
    public LineChannel Channel { get; } = new(pair.RecorderEnd);

    /// <summary>The window's hello.</summary>
    public async Task<Hello> HelloAsync()
    {
        var envelope = await Channel.ReceiveAsync(CancellationToken.None).AsTask().WaitAsync(TestWindow.Patience);
        Assert.That(envelope?.Type, Is.EqualTo(LinkTypes.Hello), "the window's first message");
        return envelope!.Body.Deserialize(LinkJson.Default.Hello)!;
    }

    /// <summary>Sends a message as the recorder.</summary>
    public async Task SendAsync<T>(string type, T body, JsonTypeInfo<T> info) => await Channel.SendAsync(type, body, info, CancellationToken.None);

    /// <summary>Sends <paramref name="text"/> as it is, such as a message of a type a later build might send.</summary>
    public async Task SendRawAsync(string text) => await pair.RecorderEnd.WriteAsync(Encoding.UTF8.GetBytes(text));

    /// <summary>The next message from the window, or null once it has closed.</summary>
    public async Task<Envelope?> NextAsync() => await Channel.ReceiveAsync(CancellationToken.None).AsTask().WaitAsync(TestWindow.Patience);
}

/// <summary>Moving a test's clock while code on other threads waits on it.</summary>
public static class ClockSteps
{
    /// <summary>
    /// Moves <paramref name="time"/> forward by <paramref name="step"/> at a time, giving the code that waits on it a moment
    /// between steps, until <paramref name="condition"/> holds. Fails after <paramref name="timeoutSeconds"/> seconds of real
    /// time, or once the clock has moved by <paramref name="atMost"/> when it is given. Returns how far the clock moved.
    /// </summary>
    public static async Task<TimeSpan> AdvanceUntilAsync(this ManualTimeProvider time, Func<bool> condition, TimeSpan step, TimeSpan? atMost = null, int timeoutSeconds = 10)
    {
        var deadline = Environment.TickCount64 + timeoutSeconds * 1000L;
        var moved = TimeSpan.Zero;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline) throw new TimeoutException($"The expected condition was not met within {timeoutSeconds} seconds; the clock had moved {moved}.");
            if (atMost is { } limit && moved >= limit) throw new AssertionException($"The expected condition was not met once the clock had moved {moved}.");

            time.Advance(step);
            moved += step;
            await Task.Delay(5);
        }

        return moved;
    }
}

/// <summary>
/// An in-memory connection that behaves as a pipe of the link does, for the tests of the link, the view of a run and the
/// whole run: two streams, the recorder's end and the window's, where what one writes the other reads. Each direction
/// holds at most <c>capacity</c> bytes that nobody has read yet; a write waits while it is full, as a pipe's does once its
/// buffer is full, so a window that stops reading holds up the recorder's writes to it. When one end is disposed, the other
/// still reads what was written before, then the end of the stream (0), and its writes throw an <see cref="IOException"/>,
/// "Pipe is broken."; a read waiting on the disposed end itself returns 0. A read or a write cancelled by its token throws an
/// <see cref="OperationCanceledException"/> and leaves the stream usable. <see cref="Break"/> breaks both ends at once and
/// drops what nobody has read, as the end of the recorder's process does. This is what a probe of real pipes showed.
/// </summary>
public sealed class DuplexStreamPair : IDisposable
{
    /// <summary>The size of a pipe's buffer in each direction, as the recorder's pipes have it: 64 KB.</summary>
    public const int PipeBuffer = 64 * 1024;

    /// <summary>What the recorder writes and the window reads.</summary>
    private readonly Lane _toWindow;

    /// <summary>What the window writes and the recorder reads.</summary>
    private readonly Lane _toRecorder;

    /// <summary>A connection whose directions each hold at most <paramref name="capacity"/> bytes nobody has read.</summary>
    public DuplexStreamPair(int capacity = PipeBuffer)
    {
        _toWindow = new Lane(capacity);
        _toRecorder = new Lane(capacity);
        RecorderEnd = new End(_toRecorder, _toWindow);
        WindowEnd = new End(_toWindow, _toRecorder);
    }

    /// <summary>The recorder's end: it reads what the window writes, and writes what the window reads.</summary>
    public Stream RecorderEnd { get; }

    /// <summary>The window's end.</summary>
    public Stream WindowEnd { get; }

    /// <summary>How many bytes the recorder has written that the window has not read yet.</summary>
    public int WaitingForWindow => _toWindow.Count;

    /// <summary>Breaks the connection at both ends at once: what nobody has read is dropped, reads give the end of the stream, writes throw.</summary>
    public void Break()
    {
        _toWindow.Break();
        _toRecorder.Break();
    }

    /// <summary>Disposes both ends.</summary>
    public void Dispose()
    {
        RecorderEnd.Dispose();
        WindowEnd.Dispose();
    }

    /// <summary>One direction of the connection: a ring of bytes that one end writes into and the other reads from.</summary>
    /// <param name="capacity">How many bytes nobody has read it holds at most.</param>
    private sealed class Lane(int capacity)
    {
        /// <summary>Guards the ring and the state of both ends.</summary>
        private readonly Lock _gate = new();

        /// <summary>The ring of bytes.</summary>
        private readonly byte[] _bytes = new byte[capacity];

        /// <summary>Completed, and replaced, whenever anything changes: bytes come or go, or an end goes.</summary>
        private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Where the oldest byte nobody has read is.</summary>
        private int _head;

        /// <summary>How many bytes wait to be read.</summary>
        private int _count;

        /// <summary>True once the writing end is gone: the reader gets what is left, then the end of the stream.</summary>
        private bool _writerGone;

        /// <summary>True once the reading end is gone, or the connection broke: a write throws, and a read gives the end of the stream.</summary>
        private bool _readerGone;

        /// <summary>How many bytes wait to be read.</summary>
        public int Count
        {
            get { lock (_gate) return _count; }
        }

        /// <summary>Reads what is there, up to the room in <paramref name="into"/>, waiting while nothing is; 0 at the end of the stream.</summary>
        public async ValueTask<int> ReadAsync(Memory<byte> into, CancellationToken cancellationToken)
        {
            while (true)
            {
                Task changed;
                lock (_gate)
                {
                    if (_readerGone || into.Length == 0) return 0;
                    if (_count > 0) return TakeLocked(into.Span);
                    if (_writerGone) return 0;
                    changed = _changed.Task;
                }

                await changed.WaitAsync(cancellationToken);
            }
        }

        /// <summary>Writes all of <paramref name="from"/>, waiting while the lane is full. Throws "Pipe is broken." once nobody reads.</summary>
        public async ValueTask WriteAsync(ReadOnlyMemory<byte> from, CancellationToken cancellationToken)
        {
            var written = 0;
            while (true)
            {
                Task changed;
                lock (_gate)
                {
                    if (_readerGone) throw new IOException("Pipe is broken.");

                    written += PutLocked(from.Span[written..]);
                    if (written == from.Length) return;
                    changed = _changed.Task;
                }

                await changed.WaitAsync(cancellationToken);
            }
        }

        /// <summary>The writing end is gone.</summary>
        public void WriterGone()
        {
            lock (_gate)
            {
                _writerGone = true;
                SignalLocked();
            }
        }

        /// <summary>The reading end is gone.</summary>
        public void ReaderGone()
        {
            lock (_gate)
            {
                _readerGone = true;
                SignalLocked();
            }
        }

        /// <summary>The connection broke: what nobody read is dropped, and both ends are gone.</summary>
        public void Break()
        {
            lock (_gate)
            {
                _count = 0;
                _writerGone = true;
                _readerGone = true;
                SignalLocked();
            }
        }

        /// <summary>Moves up to the room in <paramref name="into"/> out of the ring. Call with the lock held and bytes waiting.</summary>
        private int TakeLocked(Span<byte> into)
        {
            var taken = Math.Min(_count, into.Length);
            var first = Math.Min(taken, _bytes.Length - _head);
            _bytes.AsSpan(_head, first).CopyTo(into);
            _bytes.AsSpan(0, taken - first).CopyTo(into[first..]);
            _head = (_head + taken) % _bytes.Length;
            _count -= taken;
            SignalLocked();
            return taken;
        }

        /// <summary>Moves as much of <paramref name="from"/> as fits into the ring; returns how much. Call with the lock held.</summary>
        private int PutLocked(ReadOnlySpan<byte> from)
        {
            var put = Math.Min(_bytes.Length - _count, from.Length);
            if (put == 0) return 0;

            var tail = (_head + _count) % _bytes.Length;
            var first = Math.Min(put, _bytes.Length - tail);
            from[..first].CopyTo(_bytes.AsSpan(tail));
            from[first..put].CopyTo(_bytes);
            _count += put;
            SignalLocked();
            return put;
        }

        /// <summary>Wakes whoever waits for a change. Call with the lock held.</summary>
        private void SignalLocked()
        {
            _changed.TrySetResult();
            _changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    /// <summary>One end of the connection: it reads one lane and writes the other.</summary>
    /// <param name="incoming">The lane this end reads.</param>
    /// <param name="outgoing">The lane this end writes.</param>
    private sealed class End(Lane incoming, Lane outgoing) : Stream
    {
        /// <summary>1 once this end is disposed.</summary>
        private int _disposed;

        /// <inheritdoc />
        public override bool CanRead => true;

        /// <inheritdoc />
        public override bool CanWrite => true;

        /// <inheritdoc />
        public override bool CanSeek => false;

        /// <summary>Not supported: a pipe has no length.</summary>
        public override long Length => throw new NotSupportedException();

        /// <summary>Not supported: a pipe has no position.</summary>
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        /// <summary>Does nothing: every write is in the lane at once.</summary>
        public override void Flush()
        {
        }

        /// <summary>Does nothing: every write is in the lane at once.</summary>
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        /// <inheritdoc />
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        /// <inheritdoc />
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        /// <inheritdoc />
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            return incoming.ReadAsync(buffer, cancellationToken);
        }

        /// <inheritdoc />
        public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        /// <inheritdoc />
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        /// <inheritdoc />
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            return outgoing.WriteAsync(buffer, cancellationToken);
        }

        /// <summary>Not supported: a pipe cannot seek.</summary>
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        /// <summary>Not supported: a pipe has no length.</summary>
        public override void SetLength(long value) => throw new NotSupportedException();

        /// <summary>Closes this end: the other end reads what is left and then the end of the stream, and its writes throw.</summary>
        protected override void Dispose(bool disposing)
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                outgoing.WriterGone();
                incoming.ReaderGone();
            }

            base.Dispose(disposing);
        }
    }
}
