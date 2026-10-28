using System.Globalization;
using System.IO.Pipes;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading.Channels;
using ClassScribe.Recorder;
using ClassScribe.Runs;
using Microsoft.Extensions.Logging;

namespace ClassScribe.Link;

/// <summary>
/// The recorder's end of the link: it serves the windows that show its run, up to <see cref="LinkProtocol.MaxWindows"/> at
/// once, on the run's own pipe. A window says hello with the last line of the log it has; it is welcomed with the run and
/// its state, shown the lines after that one, and then every new line, every state and, last, how the run ended. The keys
/// a window passes on go to the recording loop (<see cref="Keys"/>), and Ctrl+C to <see cref="CtrlCPressed"/>.
/// </summary>
/// <remarks>
/// <para>
/// Nothing that records ever waits for a window. Each window has a queue of its own, which holds at most
/// <see cref="QueueCapacity"/> lines; states wait in it coalesced, only the latest; a window that falls that far behind,
/// as a classic console frozen by a text selection does, is let go, and catches up when it connects again with the last
/// line it had. The lines come from the run's <see cref="RecorderLog"/>, which hands them on in the background: when a
/// window says hello, the lines it lacks are taken from the log and its queue is added under one lock, so that no line is
/// lost or sent twice between the two.
/// </para>
/// <para>
/// The server never logs, waits or raises an event from inside its following of the log, since what it logged would come
/// back to it as a line (see <see cref="RecorderLog.Written"/>). Everything it logs goes into that same log, in the
/// recorder, and so to the windows as well.
/// </para>
/// </remarks>
/// <param name="log">The run's log, whose lines go to the windows.</param>
/// <param name="time">The clock for the waits: a window's hello, and the windows taking the end.</param>
/// <param name="logger">Where the server notes the windows that come and go.</param>
public sealed class RecorderServer(RecorderLog log, TimeProvider time, ILogger<RecorderServer> logger) : IAsyncDisposable
{
    /// <summary>How many lines of the log, and the end, may wait for one window before it is let go as fallen behind. States wait apart from them, only the latest.</summary>
    public const int QueueCapacity = 2_000;

    /// <summary>How many keys may wait for the recording loop. A key pressed while that many wait is dropped.</summary>
    public const int KeyCapacity = 64;

    /// <summary>
    /// The longest text of a log line sent to a window, in characters. A longer line is cut, with a note, so that its
    /// message keeps well within a line of the link, whatever its characters; the whole line is in the recording's log.
    /// </summary>
    public const int LongestLogText = 8_000;

    /// <summary>How long a window that has connected has to say hello before it is let go, so that it cannot hold a place for good.</summary>
    public static readonly TimeSpan HelloPatience = TimeSpan.FromSeconds(10);

    /// <summary>What follows a log line cut to <see cref="LongestLogText"/>.</summary>
    internal const string CutNote = " [cut here; the whole line is in the recording's log file]";

    /// <summary>
    /// The buffer for what the recorder writes to a window, in bytes. A probe showed that with none, every write waits until
    /// the window has read it; with 64 KB, a window may lag that much before the writes to it wait.
    /// </summary>
    private const int ToWindowBuffer = 64 * 1024;

    /// <summary>The buffer for what a window writes: keys and goodbyes, which are small.</summary>
    private const int FromWindowBuffer = 4 * 1024;

    /// <summary>How long stopping waits for the windows' connections to end.</summary>
    private static readonly TimeSpan StopPatience = TimeSpan.FromSeconds(5);

    /// <summary>The first pause before listening again after the pipe failed; it doubles with each failure in a row.</summary>
    private static readonly TimeSpan FirstListenPause = TimeSpan.FromSeconds(1);

    /// <summary>The longest pause before listening again after the pipe failed.</summary>
    private static readonly TimeSpan LongestListenPause = TimeSpan.FromSeconds(30);

    /// <summary>Guards the windows, the run, its state and its end.</summary>
    private readonly Lock _gate = new();

    /// <summary>The windows shown the run now, in the order they came.</summary>
    private readonly List<Window> _windows = [];

    /// <summary>The connections being served, until each has ended.</summary>
    private readonly List<Task> _serving = [];

    /// <summary>The keys for the recording loop.</summary>
    private readonly Channel<ConsoleKeyInfo> _keys = Channel.CreateBounded<ConsoleKeyInfo>(new BoundedChannelOptions(KeyCapacity) { FullMode = BoundedChannelFullMode.Wait });

    /// <summary>
    /// Cancelled when the server stops, which ends every connection and the listening. It is not disposed: it has no timer
    /// and nobody asks for its wait handle, and a connection that ends after the server stopped may still look at it.
    /// </summary>
    private readonly CancellationTokenSource _stopping = new();

    /// <summary>
    /// The places for the pipe's instances, one per window: an instance waiting for a window takes one, and gives it back
    /// when its connection ends. Not disposed, for the same reasons as <see cref="_stopping"/>.
    /// </summary>
    private readonly SemaphoreSlim _places = new(LinkProtocol.MaxWindows, LinkProtocol.MaxWindows);

    /// <summary>The run as it is now; null until the server begins serving it.</summary>
    private Func<RunRecord>? _run;

    /// <summary>The run's state as last published; null before the first.</summary>
    private RecorderState? _state;

    /// <summary>How the run ended, for the windows that come after the end; null while it lasts.</summary>
    private End? _end;

    /// <summary>How many windows have come, for numbering them.</summary>
    private int _numbered;

    /// <summary>Waiting for windows on the pipe; null until <see cref="Start(string, Func{RunRecord})"/>.</summary>
    private Task? _listening;

    /// <summary>1 once the server has stopped.</summary>
    private int _disposed;

    /// <summary>
    /// Makes an instance of the run's pipe (see <see cref="NewPipe"/>). Tests put a maker in its place that fails, to see
    /// the server wait and try again.
    /// </summary>
    internal PipeMaker MakePipe { get; init; } = NewPipe;

    /// <summary>
    /// Called as each window's replay has been taken from the log, before the window is added to those shown the run, with
    /// the lock the following of the log takes held. Tests write a line here, to see that it reaches the window once.
    /// </summary>
    internal Action? ReplayTaken { get; init; }

    /// <summary>
    /// The keys passed on from the windows, for the recording loop: Q, P, M and V, as the console would give them when
    /// pressed without Shift. With two windows attached, the keys of both come here. Ctrl+C does not: it raises
    /// <see cref="CtrlCPressed"/>. Completed when the server stops.
    /// </summary>
    public ChannelReader<ConsoleKeyInfo> Keys => _keys.Reader;

    /// <summary>
    /// Raised for each Ctrl+C pressed in a window, while recording and after: the recorder treats it as Ctrl+C in its own
    /// console. Raised on the thread that serves that window.
    /// </summary>
    public event Action? CtrlCPressed;

    /// <summary>Raised when a window comes or goes, on the thread that serves it, saying which and, for one that went, whether it left with Esc.</summary>
    public event EventHandler<WindowChange>? WindowsChanged;

    /// <summary>How many windows show the run now.</summary>
    public int Windows
    {
        get
        {
            lock (_gate) return _windows.Count;
        }
    }

    /// <summary>True while a window a person can type into shows the run; a window the recorder opened by itself counts.</summary>
    public bool AnyInteractive
    {
        get
        {
            lock (_gate) return _windows.Exists(window => window.Hello.Interactive);
        }
    }

    /// <summary>
    /// Begins serving <paramref name="run"/>, which does not change, on the pipe named <paramref name="pipeName"/> (see
    /// <see cref="Start(string, Func{RunRecord})"/>).
    /// </summary>
    public void Start(string pipeName, RunRecord run) => Start(pipeName, () => run);

    /// <summary>
    /// Begins serving the run that <paramref name="run"/> gives as it is now, such as its run file's handle's, on the pipe
    /// named <paramref name="pipeName"/>, which must be the run's (see <see cref="LinkProtocol.PipeNameFor"/>). The pipe is
    /// made for this user only, and as its first instance, so that it fails when another process has a pipe of that name
    /// already: the <see cref="UnauthorizedAccessException"/> or <see cref="IOException"/> is logged and thrown, and the
    /// recorder does not start. From then on windows are welcomed in the background until the server stops.
    /// </summary>
    public void Start(string pipeName, Func<RunRecord> run)
    {
        if (!LinkProtocol.IsPipeName(pipeName)) throw new ArgumentException($"\"{pipeName}\" is not the name of a run's pipe.", nameof(pipeName));

        lock (_gate)
            if (_run is not null) throw new InvalidOperationException("This server serves a run already; a recorder serves one run.");

        NamedPipeServerStream first;
        try
        {
            first = MakePipe(pipeName, first: true);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            logger.LogError(ex, "Could not make the pipe {Pipe} for ClassScribe's windows: another program has a pipe of that name, or Windows refused it", pipeName);
            throw;
        }

        try
        {
            Begin(run);
        }
        catch
        {
            first.Dispose();
            throw;
        }

        _places.Wait(0);
        _listening = Task.Run(() => ListenAsync(pipeName, first, _stopping.Token));
        logger.LogDebug("Waiting for ClassScribe windows on the pipe {Pipe}", pipeName);
    }

    /// <summary>
    /// Begins serving the run that <paramref name="run"/> gives, without a pipe: the server follows the log from now on, and
    /// welcomes windows with the run as it is when each comes. <see cref="Start(string, Func{RunRecord})"/> does this before
    /// it listens; the tests do it, and then hand <see cref="ServeAsync"/> connections of their own.
    /// </summary>
    internal void Begin(Func<RunRecord> run)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            if (_run is not null) throw new InvalidOperationException("This server serves a run already; a recorder serves one run.");
        }

        // Follow the log before any window can come: a line written after a window's replay was taken, and before the
        // server followed the log, would reach that window neither way.
        log.Written += OnLogWritten;
        lock (_gate) _run = run;
    }

    /// <summary>
    /// Passes the run's state on to every window, as the run publishes it: on every change and once a second. Each state is
    /// passed on, even one equal to the last, since a window takes states as the sign that the recorder answers; for a
    /// window that has not taken the last one yet, only the latest waits. Never waits. After the end, the state is kept for
    /// the windows that come, and sent to none.
    /// </summary>
    public void SetState(RecorderState state)
    {
        lock (_gate)
        {
            _state = state;
            if (_end is not null) return;

            foreach (var window in _windows) window.Outbox.SetState(state);
        }
    }

    /// <summary>
    /// Sends every window how the run ended, after the lines the run logged before this, and waits, for at most
    /// <paramref name="patience"/> on the steady clock, for the windows to take it and go. When <paramref name="open"/> is
    /// true, one window is told to open the folder and the transcript: the window that started the run, when it is still
    /// there, else the last window a person can type into to attach, never a window the recorder opened by itself. Returns
    /// true when that window was sent the end, so the results are its to open; false when no window took them, and the
    /// recorder opens them itself if it should. A window that comes after the end is shown it too, without opening
    /// anything. The end is sent once; a second call throws an <see cref="InvalidOperationException"/>.
    /// </summary>
    public async Task<bool> EndAsync(RunOutcome outcome, bool open, TimeSpan patience)
    {
        var started = time.GetTimestamp();

        // The log hands its lines on in the background, so the run's last lines may not have reached the windows' queues yet.
        if (!await Task.Run(() => log.WaitForFollowers(patience)))
            logger.LogDebug("The run's last lines had not all reached the windows when its end was sent");

        List<(Window Window, EndNotice Notice)> sent = [];
        List<Window> behind = [];
        EndNotice? opening = null;
        lock (_gate)
        {
            if (_end is not null) throw new InvalidOperationException("The run's end has been sent already.");

            _end = new End(outcome, Open: false);
            var opener = open ? ChooseOpener() : null;
            foreach (var window in _windows)
            {
                var notice = new EndNotice(new End(outcome, Open: window == opener));
                if (window == opener) opening = notice;
                if (window.Outbox.TryAdd(notice)) sent.Add((window, notice));
                else behind.Add(window);
            }
        }

        foreach (var window in behind) window.DropLater();
        logger.LogDebug("The end of the run goes to {Count} ClassScribe windows", sent.Count);

        var left = patience - time.GetElapsedTime(started);
        if (sent.Count > 0 && left > TimeSpan.Zero)
        {
            using var stopWaiting = new CancellationTokenSource();
            await Task.WhenAny(Task.WhenAll(sent.Select(each => each.Window.Gone.Task)), Task.Delay(left, time, stopWaiting.Token));
            await stopWaiting.CancelAsync();
        }

        return opening?.Sent.Task.IsCompletedSuccessfully == true;
    }

    /// <summary>
    /// Serves one window on <paramref name="connection"/> until it leaves, its connection breaks, it falls behind, it sends
    /// something that is not a message of the link, or the server stops; then closes the connection. A window that does not
    /// say hello first, within <see cref="HelloPatience"/>, is let go, and one that speaks another protocol is refused. The
    /// end of a connection, in any of these ways, is logged and not thrown. The pipe's instances are served this way; the
    /// tests serve in-memory connections. Throws an <see cref="InvalidOperationException"/> before the server has begun
    /// serving a run.
    /// </summary>
    internal Task ServeAsync(Stream connection, CancellationToken cancellationToken)
    {
        Func<RunRecord> run;
        lock (_gate) run = _run ?? throw new InvalidOperationException("The server serves windows once it has begun serving a run.");

        var serving = ServeConnectionAsync(connection, run, cancellationToken);
        lock (_gate) _serving.Add(serving);
        serving.ContinueWith(static (done, state) => ((RecorderServer)state!).Forget(done), this, CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return serving;
    }

    /// <summary>
    /// Stops: no window is welcomed any more, every window's connection ends, and <see cref="Keys"/> completes. Waits a few
    /// seconds at most for the connections to end. Stopping twice does nothing.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        log.Written -= OnLogWritten;
        await _stopping.CancelAsync();

        Task[] ending;
        lock (_gate) ending = [.. _serving, .. _listening is null ? Array.Empty<Task>() : [_listening]];

        // The wait is on the computer's own clock: it only keeps a connection that will not end from holding the recorder.
        var ended = Task.WhenAll(ending);
        using var stopWaiting = new CancellationTokenSource();
        if (await Task.WhenAny(ended, Task.Delay(StopPatience, stopWaiting.Token)) != ended)
            logger.LogWarning("The connections to ClassScribe windows had not all ended {Seconds} seconds after the recorder stopped serving them", StopPatience.TotalSeconds);

        await stopWaiting.CancelAsync();
        _keys.Writer.TryComplete();
    }

    /// <summary>
    /// An instance of the run's pipe: both ways, for this user only, the first of its name when <paramref name="first"/> is
    /// true, with room for <see cref="LinkProtocol.MaxWindows"/> instances, and buffers so that a write does not wait for the
    /// window to read it.
    /// </summary>
    internal static NamedPipeServerStream NewPipe(string pipeName, bool first) =>
        new(pipeName, PipeDirection.InOut, LinkProtocol.MaxWindows, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly | (first ? PipeOptions.FirstPipeInstance : PipeOptions.None),
            FromWindowBuffer, ToWindowBuffer);

    /// <summary>
    /// Waits for windows on the pipe, from <paramref name="first"/> on, and serves each in the background; while four are
    /// served, waits for one of them to end before it waits for another. Runs until the server stops.
    /// </summary>
    private async Task ListenAsync(string pipeName, NamedPipeServerStream first, CancellationToken stopping)
    {
        NamedPipeServerStream? waiting = first;
        var pause = FirstListenPause;
        while (!stopping.IsCancellationRequested)
        {
            try
            {
                if (waiting is null)
                {
                    await _places.WaitAsync(stopping);
                    try
                    {
                        waiting = MakePipe(pipeName, first: false);
                    }
                    catch
                    {
                        _places.Release();
                        throw;
                    }
                }

                await waiting.WaitForConnectionAsync(stopping);
                var connected = waiting;
                waiting = null;
                pause = FirstListenPause;
                _ = ServeAndFreeAsync(connected);
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested)
            {
                // Deliberately not rethrown: the server is stopping, which is what ended the wait.
                break;
            }
            catch (Exception ex)
            {
                // Deliberately not rethrown: the recording must go on whatever becomes of its windows' pipe. A window that
                // went away as it connected, or an instance Windows would not make, is logged, and the server waits for
                // windows again after a pause that doubles up to 30 seconds, so that a failure that lasts cannot fill the log.
                if (waiting is not null)
                {
                    await waiting.DisposeAsync();
                    waiting = null;
                    _places.Release();
                }

                logger.LogWarning(ex, "Could not wait for a ClassScribe window on the pipe {Pipe}; trying again in {Pause}", pipeName, InWords(pause));
                await Task.Delay(pause, time, stopping).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                pause = TimeSpan.FromTicks(Math.Min(pause.Ticks * 2, LongestListenPause.Ticks));
            }
        }

        if (waiting is not null) await waiting.DisposeAsync();
    }

    /// <summary>A pause of whole seconds in words, singular for one and plural otherwise, such as "1 second" or "30 seconds".</summary>
    private static string InWords(TimeSpan pause)
    {
        var seconds = (long)Math.Round(pause.TotalSeconds);
        return string.Create(CultureInfo.InvariantCulture, $"{seconds} {(seconds == 1 ? "second" : "seconds")}");
    }

    /// <summary>Serves a window that connected on the pipe, then frees its place for the next.</summary>
    private async Task ServeAndFreeAsync(NamedPipeServerStream connection)
    {
        try
        {
            await ServeAsync(connection, CancellationToken.None);
        }
        catch (Exception ex)
        {
            // Deliberately not rethrown: this runs in the background, for one window. Whatever went wrong in serving it ends
            // that window's connection only, never the recording or the other windows; it is logged, and the window can
            // connect again.
            logger.LogWarning(ex, "Serving a ClassScribe window failed; the recording goes on, and the window can connect again");
        }
        finally
        {
            _places.Release();
        }
    }

    /// <summary>Forgets a connection that has ended.</summary>
    private void Forget(Task done)
    {
        lock (_gate) _serving.Remove(done);
    }

    /// <summary>
    /// The conversation with one window: its hello, then its welcome and the lines it lacks, then everything that comes,
    /// while its keys are read, until its connection ends in one way or another.
    /// </summary>
    private async Task ServeConnectionAsync(Stream connection, Func<RunRecord> run, CancellationToken cancellationToken)
    {
        await using var channel = new LineChannel(connection);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stopping.Token);
        Window? window = null;
        try
        {
            var greeting = await GreetAsync(channel, lifetime.Token);
            if (greeting.Hello is not { } hello)
            {
                if (greeting.Protocol is { } other) await RefuseAsync(channel, other, run(), lifetime.Token);
                return;
            }

            var current = run();
            (window, var welcome, var replay) = Register(hello, current, lifetime);
            WindowsChanged?.Invoke(this, WindowChange.Came);
            logger.LogDebug("A ClassScribe window is showing this recording ({Count} now)", Windows);

            var writing = WriteToAsync(window, channel, welcome, replay, lifetime.Token);
            var reading = ReadFromAsync(window, channel, lifetime.Token);
            var first = await Task.WhenAny(writing, reading);
            await lifetime.CancelAsync();
            await Task.WhenAll(writing, reading).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

            // The task that ended first says why the connection ends; the other was stopped because of it.
            if ((first.Exception ?? (first == writing ? reading : writing).Exception)?.InnerException is { } failure)
                ExceptionDispatchInfo.Throw(failure);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException or InvalidDataException)
        {
            // Deliberately not rethrown: a window's connection ending ends only that window's part in the run, never the
            // recording. A window that sent what is not a message of the link is let go and noted as a problem; a broken or
            // closed connection, or the server stopping, is only noted.
            if (ex is InvalidDataException)
                logger.LogWarning(ex, "A ClassScribe window sent something that is not a message of the link, so it was let go");
            else if (!_stopping.IsCancellationRequested && window?.FellBehind != true)
                logger.LogDebug(ex, "The connection to a ClassScribe window ended");
        }
        finally
        {
            if (window is not null) Leave(window);
        }
    }

    /// <summary>
    /// Waits, for at most <see cref="HelloPatience"/>, for the window's hello. Returns the protocol and the hello when it is
    /// one this build reads; the protocol alone when the window speaks another, to be refused; neither when the window said
    /// nothing, went away, or began with anything else.
    /// </summary>
    private async Task<Greeting> GreetAsync(LineChannel channel, CancellationToken cancellationToken)
    {
        using var patience = new CancellationTokenSource(HelloPatience, time);
        using var waiting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, patience.Token);
        Envelope? first;
        try
        {
            first = await channel.ReceiveAsync(waiting.Token);
        }
        catch (OperationCanceledException) when (patience.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            // Deliberately not rethrown: a window that connects and says nothing is let go after a while, so that it cannot
            // hold one of the places for good.
            logger.LogDebug("A ClassScribe window connected but said nothing for {Seconds} seconds, so it was let go", HelloPatience.TotalSeconds);
            return default;
        }

        if (first is null)
        {
            logger.LogDebug("A ClassScribe window connected and went away without a word");
            return default;
        }

        if (first.Type != LinkTypes.Hello)
        {
            logger.LogDebug("A ClassScribe window began with something other than hello, so it was let go");
            return default;
        }

        if (ProtocolOf(first.Body) is not { } protocol)
        {
            logger.LogDebug("A ClassScribe window's hello did not say which protocol it speaks, so it was let go");
            return default;
        }

        if (protocol != LinkProtocol.Version) return new Greeting(protocol, null);

        if (TryRead(first.Body, LinkJson.Default.Hello) is not { } hello)
        {
            logger.LogDebug("A ClassScribe window's hello could not be read, so it was let go");
            return default;
        }

        return new Greeting(protocol, hello);
    }

    /// <summary>Tells a window that speaks protocol <paramref name="protocol"/> that it cannot be shown the run, and which ClassScribe.exe can.</summary>
    private async Task RefuseAsync(LineChannel channel, int protocol, RunRecord run, CancellationToken cancellationToken)
    {
        logger.LogInformation(
            "A ClassScribe window of another build asked to show this recording; it speaks protocol {Theirs} of the link and this recorder {Ours}, so it was told to use {Exe}",
            protocol, LinkProtocol.Version, run.ExePath);
        await channel.SendAsync(LinkTypes.Refused,
            new Refused($"This recorder speaks protocol {LinkProtocol.Version} of ClassScribe's link, and the window protocol {protocol}.", run.ExePath),
            LinkJson.Default.Refused, cancellationToken);
    }

    /// <summary>
    /// Adds the window to those shown the run, and takes the lines it lacks from the log, under one lock with the following
    /// of the log, so that each line reaches it exactly once: through the replay, or through its queue.
    /// </summary>
    private (Window Window, Welcome Welcome, IReadOnlyList<LogEntry> Replay) Register(Hello hello, RunRecord run, CancellationTokenSource lifetime)
    {
        lock (_gate)
        {
            var (lines, oldest, last) = TakeReplay(hello.Since);
            var window = new Window(++_numbered, hello, last, lifetime, logger);
            _windows.Add(window);

            // A window that comes after the end is shown it after the lines; the results were settled at the end.
            if (_end is { } end) window.Outbox.TryAdd(new EndNotice(end));

            return (window, new Welcome(LinkProtocol.Version, LinkProtocol.Build, run.ExePath, run, _state ?? Starting(run), oldest), lines);
        }
    }

    /// <summary>
    /// A window's replay: the lines after <paramref name="since"/> that the log still holds, the number of the oldest it
    /// holds, and the number of the last line written, which the window has once it is sent these. Call with the lock held,
    /// the lock the following of the log takes, and add the window before letting go of it: then no line can be handed on
    /// between the replay and the window's adding, where it would reach the window neither way.
    /// </summary>
    private (IReadOnlyList<LogEntry> Lines, long OldestSeq, long LastSeq) TakeReplay(long since)
    {
        var replay = log.Since(Math.Max(0, since));
        ReplayTaken?.Invoke();
        return replay;
    }

    /// <summary>Takes the window away from those shown the run, says so, and tells whoever waits for it to go.</summary>
    private void Leave(Window window)
    {
        int count;
        lock (_gate)
        {
            _windows.Remove(window);
            count = _windows.Count;
        }

        window.Gone.TrySetResult();
        WindowsChanged?.Invoke(this, WindowChange.Went(leftWithEsc: window.ByeReason == ByeReasons.Esc));

        if (window.FellBehind)
            logger.LogInformation(
                "A ClassScribe window fell {Count} messages behind, as a console frozen by a text selection does, so it was let go; it catches up when it connects again",
                QueueCapacity);
        else if (window.ByeReason is { } reason)
            logger.LogDebug("A ClassScribe window left the view of this recording ({Reason}); {Count} still show it", reason, count);
        else if (!_stopping.IsCancellationRequested)
            logger.LogDebug("A ClassScribe window stopped showing this recording; {Count} still show it", count);
    }

    /// <summary>
    /// Writes to the window: its welcome, the lines it lacks, then whatever its queue holds as it comes, until the window
    /// falls behind or the connection ends.
    /// </summary>
    private static async Task WriteToAsync(Window window, LineChannel channel, Welcome welcome, IReadOnlyList<LogEntry> replay, CancellationToken cancellationToken)
    {
        await channel.SendAsync(LinkTypes.Welcome, welcome, LinkJson.Default.Welcome, cancellationToken);
        foreach (var line in replay) await channel.SendAsync(LinkTypes.Log, ToLine(line), LinkJson.Default.LogLine, cancellationToken);

        while (await window.Outbox.TakeAsync(cancellationToken) is { } batch)
        {
            if (batch.State is { } state) await channel.SendAsync(LinkTypes.State, new StateUpdate(state), LinkJson.Default.StateUpdate, cancellationToken);

            foreach (var item in batch.Items)
            {
                switch (item)
                {
                    case LogEntry line:
                        await channel.SendAsync(LinkTypes.Log, ToLine(line), LinkJson.Default.LogLine, cancellationToken);
                        break;

                    case EndNotice end:
                        await channel.SendAsync(LinkTypes.End, end.Message, LinkJson.Default.End, cancellationToken);
                        end.Sent.TrySetResult();
                        break;
                }
            }
        }
    }

    /// <summary>
    /// Reads what the window sends: its keys, for the recording loop or as Ctrl+C, until it says bye or closes. Messages of
    /// any other type, and keys the link does not know, are passed over.
    /// </summary>
    private async Task ReadFromAsync(Window window, LineChannel channel, CancellationToken cancellationToken)
    {
        while (await channel.ReceiveAsync(cancellationToken) is { } envelope)
        {
            switch (envelope.Type)
            {
                case LinkTypes.Key:
                    PassOn(TryRead(envelope.Body, LinkJson.Default.KeyPress)?.Key);
                    break;

                case LinkTypes.Bye:
                    var reason = TryRead(envelope.Body, LinkJson.Default.Bye)?.Reason;
                    window.ByeReason = ByeReasons.IsKnown(reason) ? reason : "for a reason this build does not know";
                    return;
            }
        }
    }

    /// <summary>Passes a key from a window on: Ctrl+C to its event, the recording keys to the loop; any other is passed over.</summary>
    private void PassOn(string? key)
    {
        if (key == LinkKeys.CtrlC)
        {
            CtrlCPressed?.Invoke();
            return;
        }

        if (KeyInfo(key) is not { } pressed)
        {
            logger.LogDebug("A ClassScribe window passed on a key the link does not know; it was passed over");
            return;
        }

        if (!_keys.Writer.TryWrite(pressed))
            logger.LogDebug("{Count} keys wait for the recording already, so a key passed on from a window was dropped", KeyCapacity);
    }

    /// <summary>The key press the console would give for the recording key named <paramref name="key"/> pressed without Shift; null for any other name.</summary>
    private static ConsoleKeyInfo? KeyInfo(string? key) => key switch
    {
        LinkKeys.Q => new ConsoleKeyInfo('q', ConsoleKey.Q, shift: false, alt: false, control: false),
        LinkKeys.P => new ConsoleKeyInfo('p', ConsoleKey.P, shift: false, alt: false, control: false),
        LinkKeys.M => new ConsoleKeyInfo('m', ConsoleKey.M, shift: false, alt: false, control: false),
        LinkKeys.V => new ConsoleKeyInfo('v', ConsoleKey.V, shift: false, alt: false, control: false),
        _ => null
    };

    /// <summary>
    /// Follows the log: queues each line for the windows that lack it. This runs inside the log's handing on of its lines,
    /// so it never logs, waits or raises an event; a window that has fallen too far behind is let go on another thread.
    /// After the end, lines go to the windows only through the replay.
    /// </summary>
    private void OnLogWritten(LogEntry line)
    {
        List<Window>? behind = null;
        lock (_gate)
        {
            if (_end is not null) return;

            foreach (var window in _windows)
                if (line.Seq > window.ReplayedUpTo && !window.Outbox.TryAdd(line))
                    (behind ??= []).Add(window);
        }

        if (behind is null) return;

        foreach (var window in behind) window.DropLater();
    }

    /// <summary>
    /// The window to open the results: the last window that started the run to attach, else the last window a person can
    /// type into to attach; never a window the recorder opened by itself. Call with the lock held.
    /// </summary>
    private Window? ChooseOpener() =>
        _windows.LastOrDefault(window => window.Hello.Owner && !window.Hello.Alert)
        ?? _windows.LastOrDefault(window => window.Hello.Interactive && !window.Hello.Alert);

    /// <summary>The state of a run that has not published one yet: starting.</summary>
    private static RecorderState Starting(RunRecord run) =>
        new(RecorderPhase.Starting, run.Request.Name, TimeSpan.Zero, null, null, null, null, null, Attention.None, run.Home == RunHome.InWindow);

    /// <summary>A line of the log as it goes to a window, cut to <see cref="LongestLogText"/> characters with a note when it is longer.</summary>
    private static LogLine ToLine(LogEntry entry)
    {
        var text = entry.Text;
        if (text.Length > LongestLogText)
        {
            // Never cut between the two halves of a character written as a surrogate pair.
            var keep = char.IsHighSurrogate(text[LongestLogText - 1]) ? LongestLogText - 1 : LongestLogText;
            text = text[..keep] + CutNote;
        }

        return new LogLine(entry.Seq, entry.Utc, entry.Level, text);
    }

    /// <summary>The protocol a hello says the window speaks; null when it does not say, in a number.</summary>
    private static int? ProtocolOf(JsonElement body) =>
        body.ValueKind == JsonValueKind.Object && body.TryGetProperty("protocol", out var protocol)
        && protocol.ValueKind == JsonValueKind.Number && protocol.TryGetInt32(out var number)
            ? number
            : null;

    /// <summary>The message <paramref name="body"/> holds, read with <paramref name="info"/>; null when it holds none this build can read, which is noted.</summary>
    private T? TryRead<T>(JsonElement body, JsonTypeInfo<T> info) where T : class
    {
        if (body.ValueKind != JsonValueKind.Object) return null;

        try
        {
            return body.Deserialize(info);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            // Deliberately not rethrown: a message from a window that this build cannot read is passed over, as the link's
            // protocol says, and the window stays. A choice that does not say which kind it is gives a NotSupportedException
            // rather than a JsonException. No message a window sends holds such a choice yet; should one come to, it is
            // passed over here as it is at the window's end.
            logger.LogDebug(ex, "A ClassScribe window sent a {Type} message this build cannot read; it was passed over", typeof(T).Name);
            return null;
        }
    }

    /// <summary>Makes an instance of the pipe named <paramref name="pipeName"/>, the first of its name when <paramref name="first"/> is true.</summary>
    /// <param name="pipeName">The run's pipe.</param>
    /// <param name="first">True for the first instance, which fails when the name is taken.</param>
    internal delegate NamedPipeServerStream PipeMaker(string pipeName, bool first);

    /// <summary>
    /// What changed in the windows shown the run (see <see cref="WindowsChanged"/>): a window came, or one went, and then
    /// whether it left with Esc. A window that closed, crashed or lost its connection went without Esc.
    /// </summary>
    /// <param name="Gone">True when a window went; false when one came.</param>
    /// <param name="LeftWithEsc">True when the window that went said bye with Esc: a choice made at the keyboard, not a window closed.</param>
    public sealed record WindowChange(bool Gone, bool LeftWithEsc)
    {
        /// <summary>A window came.</summary>
        public static WindowChange Came { get; } = new(Gone: false, LeftWithEsc: false);

        /// <summary>A window went, with Esc when <paramref name="leftWithEsc"/> is true.</summary>
        public static WindowChange Went(bool leftWithEsc) => new(Gone: true, leftWithEsc);
    }

    /// <summary>What a window's first message said: the protocol it speaks, and its hello when this build reads it.</summary>
    /// <param name="Protocol">The protocol, when the window said one.</param>
    /// <param name="Hello">The hello, when it is in this build's protocol and could be read.</param>
    private readonly record struct Greeting(int? Protocol, Hello? Hello);

    /// <summary>What waits for a window when it is taken from its queue: the latest state, if one waits, and the lines and the end, in order.</summary>
    /// <param name="State">The latest state, when one waits.</param>
    /// <param name="Items">The lines of the log (<see cref="LogEntry"/>) and the end (<see cref="EndNotice"/>), in the order they came.</param>
    private sealed record Batch(RecorderState? State, object[] Items);

    /// <summary>How the run ended, as it waits for one window, and whether it has been sent to it.</summary>
    /// <param name="message">The end, telling this window whether to open the results.</param>
    private sealed class EndNotice(End message)
    {
        /// <summary>The end, as this window is sent it.</summary>
        public End Message { get; } = message;

        /// <summary>Completed once the end has been sent to the window.</summary>
        public TaskCompletionSource Sent { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>A window shown the run: what it said in its hello, its queue, and how it left.</summary>
    /// <param name="number">The window's number, counting from 1 in the order the windows came.</param>
    /// <param name="hello">What the window said in its hello.</param>
    /// <param name="replayedUpTo">The number of the last line the window was sent in its replay; a line not beyond it reached the window already.</param>
    /// <param name="lifetime">Cancelled to end the window's connection.</param>
    /// <param name="logger">Where a failure to end the connection is noted.</param>
    private sealed class Window(int number, Hello hello, long replayedUpTo, CancellationTokenSource lifetime, ILogger logger)
    {
        /// <summary>1 once the window has fallen behind and is being let go.</summary>
        private int _behind;

        /// <summary>The window's number, counting from 1 in the order the windows came.</summary>
        public int Number { get; } = number;

        /// <summary>What the window said in its hello.</summary>
        public Hello Hello { get; } = hello;

        /// <summary>The number of the last line the window was sent in its replay.</summary>
        public long ReplayedUpTo { get; } = replayedUpTo;

        /// <summary>What waits to be sent to the window.</summary>
        public Outbox Outbox { get; } = new(QueueCapacity);

        /// <summary>Completed once the window is no longer shown the run.</summary>
        public TaskCompletionSource Gone { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Why the window said bye, once it has.</summary>
        public string? ByeReason { get; set; }

        /// <summary>True once the window has fallen behind and is being let go.</summary>
        public bool FellBehind => Volatile.Read(ref _behind) != 0;

        /// <summary>
        /// Lets the window go because it has fallen behind: once, and on a thread of the pool, since this is called from the
        /// following of the log, where ending a connection could run code that logs.
        /// </summary>
        public void DropLater()
        {
            if (Interlocked.Exchange(ref _behind, 1) != 0) return;

            ThreadPool.UnsafeQueueUserWorkItem(static window => window.Drop(), this, preferLocal: false);
        }

        /// <summary>Ends the window's connection.</summary>
        private void Drop()
        {
            try
            {
                lifetime.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Deliberately not rethrown: the window's connection had ended already and let go of its token, so there is
                // nothing left to end.
            }
            catch (Exception ex)
            {
                // Deliberately not rethrown: this runs on a thread of the pool, where an exception nobody catches ends the
                // process, and the recording with it. The window's connection ends anyway once its writes fail.
                logger.LogWarning(ex, "Could not let go of ClassScribe window {Number}, which fell behind", Number);
            }
        }
    }

    /// <summary>
    /// What waits to be sent to one window: the lines of the log and the end, in order, at most
    /// <see cref="QueueCapacity"/> of them, and the latest state. Adding never waits.
    /// </summary>
    /// <param name="capacity">How many lines and ends may wait.</param>
    private sealed class Outbox(int capacity)
    {
        /// <summary>Guards what waits.</summary>
        private readonly Lock _gate = new();

        /// <summary>The lines of the log and the end, in the order they came.</summary>
        private readonly Queue<object> _items = new();

        /// <summary>The latest state, while one waits.</summary>
        private RecorderState? _state;

        /// <summary>True once the window has fallen behind: nothing more is kept for it.</summary>
        private bool _behind;

        /// <summary>Completed when something comes for the writer to take; replaced once it has taken everything.</summary>
        private TaskCompletionSource _arrived = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Queues a line of the log or the end. Returns false when it does not fit, or did not before: the window has fallen too far behind.</summary>
        public bool TryAdd(object item)
        {
            lock (_gate)
            {
                if (_behind) return false;

                if (_items.Count >= capacity)
                {
                    _behind = true;
                    _arrived.TrySetResult();
                    return false;
                }

                _items.Enqueue(item);
                _arrived.TrySetResult();
                return true;
            }
        }

        /// <summary>Makes <paramref name="state"/> the state that waits, in place of any that waited.</summary>
        public void SetState(RecorderState state)
        {
            lock (_gate)
            {
                if (_behind) return;

                _state = state;
                _arrived.TrySetResult();
            }
        }

        /// <summary>Takes everything that waits, waiting until something does; null once the window has fallen behind.</summary>
        public async Task<Batch?> TakeAsync(CancellationToken cancellationToken)
        {
            while (true)
            {
                Task arrived;
                lock (_gate)
                {
                    if (_behind) return null;

                    if (_state is not null || _items.Count > 0)
                    {
                        var batch = new Batch(_state, [.. _items]);
                        _state = null;
                        _items.Clear();
                        return batch;
                    }

                    if (_arrived.Task.IsCompleted) _arrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    arrived = _arrived.Task;
                }

                await arrived.WaitAsync(cancellationToken);
            }
        }
    }
}
