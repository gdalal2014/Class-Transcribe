using System.IO.Pipes;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using ClassScribe.Recorder;
using ClassScribe.Runs;
using Microsoft.Extensions.Logging;

namespace ClassScribe.Link;

/// <summary>
/// The link broke and was made again, with the recorder's process still running. The lines the window lacked follow; the
/// welcome says how far back the recorder's log still goes (<see cref="Welcome.OldestSeq"/>).
/// </summary>
/// <param name="Welcome">The recorder's welcome on the new connection, with the run and its state now.</param>
public sealed record Reconnected(Welcome Welcome);

/// <summary>
/// No state has come for <see cref="RecorderClient.SilenceLimit"/> while the recorder's process runs: the recorder is not
/// answering. Said once for each silence; the next state ends it. The link stays: the window may wait, or stop the recording
/// through its stop event, which does not need the link.
/// </summary>
/// <param name="For">How long it has been since the last state.</param>
public sealed record RecorderSilent(TimeSpan For);

/// <summary>Why the link is gone for good.</summary>
public enum LinkLoss
{
    /// <summary>The recorder's process has ended, without the run's end reaching the window: an unexpected end, unless its run file says otherwise.</summary>
    RecorderGone,

    /// <summary>The recorder's process runs, but the link could not be made again within <see cref="RecorderClient.ReconnectPatience"/>: the recording carries on.</summary>
    ConnectionLost
}

/// <summary>The link is gone for good: the last thing a window reads when the run's end did not come.</summary>
/// <param name="Why">Whether the recorder's process ended or only the connection was lost.</param>
public sealed record LinkLost(LinkLoss Why);

/// <summary>Why a window could not connect to a recorder.</summary>
public enum LinkFailure
{
    /// <summary>The recorder's process ended before it answered.</summary>
    RecorderGone,

    /// <summary>The recorder's process runs, but its pipe did not answer within <see cref="RecorderClient.ConnectPatience"/>.</summary>
    NoAnswer,

    /// <summary>The recorder was started by another build of ClassScribe, which speaks another protocol (see <see cref="RecorderLinkException.Refused"/>).</summary>
    Refused,

    /// <summary>
    /// The recorder runs with other rights than the window: one of them was started with Run as administrator and the other
    /// not. The message says which way to start ClassScribe: without Run as administrator from a window that has it, and
    /// with it from a window that has not.
    /// </summary>
    OtherRights
}

/// <summary>A window could not connect to a run's recorder. The message says why in words Gulzar reads.</summary>
/// <param name="failure">Why.</param>
/// <param name="message">What happened, in plain words.</param>
/// <param name="refused">The recorder's refusal, for <see cref="LinkFailure.Refused"/>: which ClassScribe.exe records the run.</param>
/// <param name="innerException">What Windows said, when it said something.</param>
public sealed class RecorderLinkException(LinkFailure failure, string message, Refused? refused = null, Exception? innerException = null)
    : Exception(message, innerException)
{
    /// <summary>Why the window could not connect.</summary>
    public LinkFailure Failure { get; } = failure;

    /// <summary>The recorder's refusal, for <see cref="LinkFailure.Refused"/>; null otherwise.</summary>
    public Refused? Refused { get; } = refused;
}

/// <summary>
/// A window's end of the link to a run's recorder. It connects to the run's pipe, for this user only, says hello with the
/// last line of the log the window has, and reads what the recorder sends: the lines of its log, its states and, last, the
/// run's end. When the link breaks while the recorder's process runs, it is made again, a quarter of a second later and
/// then at pauses that double up to two seconds, for up to ten seconds, and the lines the window has already are passed
/// over; when the process is gone, the reading says so at once. The window passes the recording keys on with
/// <see cref="SendKeyAsync"/>, and says bye when it leaves.
/// </summary>
/// <remarks>
/// One task reads (<see cref="ReadAsync"/>) while others may send keys and say bye. A reading may be stopped and begun
/// again, as at the notice of silence, and goes on where it stopped. Connecting again, to the same run or another, is for
/// after a reading has ended.
/// </remarks>
/// <param name="liveness">Whether the recorder's process, by its id and start time, still runs.</param>
/// <param name="time">The clock for the waits and the pauses.</param>
/// <param name="logger">Where the client notes how the link fares; the window shows Gulzar what matters itself.</param>
public sealed class RecorderClient(IProcessLiveness liveness, TimeProvider time, ILogger<RecorderClient> logger) : IAsyncDisposable
{
    /// <summary>How long connecting to a recorder goes on while its process runs and its pipe does not answer: 20 seconds, as a recorder that has just started may take a moment to make its pipe.</summary>
    public static readonly TimeSpan ConnectPatience = TimeSpan.FromSeconds(20);

    /// <summary>How long a window tries to make a broken link again while the recorder's process runs.</summary>
    public static readonly TimeSpan ReconnectPatience = TimeSpan.FromSeconds(10);

    /// <summary>The pause before the first try again; each pause after doubles it, up to <see cref="LongestRetry"/>.</summary>
    public static readonly TimeSpan FirstRetry = TimeSpan.FromMilliseconds(250);

    /// <summary>The longest pause between two tries.</summary>
    public static readonly TimeSpan LongestRetry = TimeSpan.FromSeconds(2);

    /// <summary>How long without a state, while the recorder's process runs, before the reading says the recorder is not answering. A recorder sends one every second.</summary>
    public static readonly TimeSpan SilenceLimit = TimeSpan.FromSeconds(10);

    /// <summary>How long a recorder has to answer a window's hello.</summary>
    public static readonly TimeSpan WelcomePatience = TimeSpan.FromSeconds(10);

    /// <summary>How long one try at opening the pipe waits for a free instance before the recorder's process is looked at again.</summary>
    private static readonly TimeSpan LongestTry = TimeSpan.FromSeconds(1);

    /// <summary>How long saying bye may take: the window leaves anyway.</summary>
    private static readonly TimeSpan ByePatience = TimeSpan.FromSeconds(1);

    /// <summary>Guards the link and what the client knows of the run.</summary>
    private readonly Lock _gate = new();

    /// <summary>
    /// Cancelled once the window leaves: bye, or the client closed. It is not disposed: it has no timer and nobody asks for
    /// its wait handle, and a reading that ends after the window left may still look at it.
    /// </summary>
    private readonly CancellationTokenSource _leaving = new();

    /// <summary>The link now; null before connecting, while it is being made again, after the end, and once the window has left.</summary>
    private LineChannel? _channel;

    /// <summary>The run connected to; null before connecting.</summary>
    private RunRecord? _run;

    /// <summary>What the window says in its hello, but for the last line it has, which is added each time.</summary>
    private Hello? _hello;

    /// <summary>The number of the last line of the run's log the window has been given.</summary>
    private long _lastSeen;

    /// <summary>When the last state came, or the link was made, on the steady clock.</summary>
    private long _lastStateAt;

    /// <summary>True once the reading has said that the recorder is not answering, until the next state.</summary>
    private bool _silenceTold;

    /// <summary>True once the run's end has come: the link is over.</summary>
    private bool _over;

    /// <summary>
    /// True from a break of the link until it is made again or given up. A reading stopped by its token meanwhile leaves it
    /// true, and the next reading takes the mending up afresh, so that the window is not left without a link and without a
    /// word.
    /// </summary>
    private bool _mending;

    /// <summary>
    /// The wait for the next message, while one is under way, and the link it waits on. It belongs to the client rather than
    /// to one reading, so that a reading stopped while it waited, as at the notice of silence, and begun again, goes on with
    /// the same wait: two waits on one link would each take part of what comes. Only the reading uses these.
    /// </summary>
    private Task<Envelope?>? _pending;

    /// <summary>The link <see cref="_pending"/> waits on.</summary>
    private LineChannel? _pendingOn;

    /// <summary>Opens a run's pipe, waiting at most the time given for a free instance. Tests put an in-memory connection in its place.</summary>
    public Func<string, TimeSpan, CancellationToken, Task<Stream>> OpenPipe { get; init; } = OpenRealPipeAsync;

    /// <summary>
    /// Whether this window runs as administrator, for the advice when a recorder started with other rights cannot be
    /// reached; null, as outside the tests, to ask Windows when it is needed.
    /// </summary>
    internal bool? Elevated { get; init; }

    /// <summary>
    /// Connects to the recorder of <paramref name="run"/>, as a window shown in <paramref name="mode"/> that a person can type
    /// into when <paramref name="interactive"/> is true, and returns the recorder's welcome. Tries at once, then while the
    /// recorder's process runs, at pauses from a quarter of a second doubling to two seconds, for up to
    /// <see cref="ConnectPatience"/>. Throws a <see cref="RecorderLinkException"/> that says why, in words Gulzar reads,
    /// when the process ends first, its pipe never answers, it speaks another protocol, or it runs with other rights; and an
    /// <see cref="ArgumentException"/> for a run with no pipe of its own, such as one recording in its window. Connecting
    /// again ends the link before; to the same run, the window keeps the lines it has, and to another, it starts from the
    /// first.
    /// </summary>
    public async Task<Welcome> ConnectAsync(RunRecord run, ViewMode mode, bool interactive, CancellationToken cancellationToken)
    {
        if (run.Home != RunHome.Separate || !LinkProtocol.IsPipeName(run.Pipe))
            throw new ArgumentException($"Run {run.RunId} records in its window or names no pipe of its own, so there is no recorder to connect to.", nameof(run));

        LineChannel? before;
        Hello hello;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_leaving.IsCancellationRequested, this);
            if (_run?.RunId != run.RunId) _lastSeen = 0;

            _run = run;
            _hello = hello = new Hello(LinkProtocol.Version, LinkProtocol.Build, 0, interactive, Alert: mode == ViewMode.Alert, Owner: mode == ViewMode.Owner);
            _over = false;
            _mending = false;
            before = _channel;
            _channel = null;
        }

        if (before is not null) await before.DisposeAsync();

        // Closing the client, or bye, stops the tries at once, with an OperationCanceledException.
        using var connecting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _leaving.Token);
        var attempt = await ConnectWithinAsync(run, hello, ConnectPatience, TimeSpan.Zero, connecting.Token);
        if (attempt is Attempt.Connected connected)
        {
            if (Install(connected.Channel)) return connected.Welcome;

            await connected.Channel.DisposeAsync();
            throw new ObjectDisposedException(GetType().FullName, "The window left while it connected to the recording.");
        }

        var failure = attempt switch
        {
            Attempt.Gone => new RecorderLinkException(LinkFailure.RecorderGone,
                $"The recording's background process (process {run.ProcessId}) ended before it answered."),
            Attempt.Refusal refusal => new RecorderLinkException(LinkFailure.Refused,
                $"This recording was started by another build of ClassScribe ({refusal.Refused.ExePath}); start that one to see it.", refusal.Refused),
            Attempt.OtherRights rights => new RecorderLinkException(LinkFailure.OtherRights, OtherRightsAdvice(), innerException: rights.Exception),
            _ => new RecorderLinkException(LinkFailure.NoAnswer,
                $"The recording's background process (process {run.ProcessId}) did not answer within {ConnectPatience.TotalSeconds:0} seconds.")
        };

        logger.LogDebug(failure.InnerException, "Could not connect to the recorder of run {RunId}: {Reason}", run.RunId, failure.Message);
        throw failure;
    }

    /// <summary>
    /// Reads what the recorder sends, after the welcome, until the run's end, the window leaves, or the link is lost: each
    /// <see cref="LogLine"/> the window has not been given yet, each <see cref="StateUpdate"/>, and last the
    /// <see cref="End"/>, with the link closed as it is handed over, so that a reading may stop there and the recorder still
    /// sees the window go. Along the way it may also yield <see cref="RecorderSilent"/>, once a
    /// silence lasts <see cref="SilenceLimit"/>, and <see cref="Reconnected"/> after a break it mended; and, last, instead of
    /// the end, <see cref="LinkLost"/>. Messages of types this build does not know, and messages it cannot read, are passed
    /// over. After bye, or once the client is closed, the reading ends without a word. Throws an
    /// <see cref="InvalidOperationException"/> before the client has connected, and an <see cref="OperationCanceledException"/>
    /// when <paramref name="cancellationToken"/> is cancelled; the next reading goes on from there, and a link that was being
    /// made again is made again by it.
    /// </summary>
    public async IAsyncEnumerable<object> ReadAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        lock (_gate)
            if (_run is null) throw new InvalidOperationException("Connect to a run before reading what its recorder sends.");

        while (true)
        {
            LineChannel? channel;
            bool mending;
            lock (_gate)
            {
                var over = _over || _leaving.IsCancellationRequested;
                channel = over ? null : _channel;
                mending = !over && _mending;
            }

            if (channel is not null)
            {
                // A wait begun by a reading that stopped is taken up again; a wait on a link that is no more is let go. The
                // wait is not tied to this reading's token, so that it outlives the reading; this reading stops waiting on
                // its token.
                if (_pending is null || _pendingOn != channel)
                {
                    _pendingOn = channel;
                    _pending = channel.ReceiveAsync(CancellationToken.None).AsTask();
                }

                switch (await NextAsync(_pending, cancellationToken))
                {
                    case Next.Silent silent:
                        yield return new RecorderSilent(silent.For);
                        continue;

                    case Next.Received received:
                        _pending = null;
                        if (Interpret(received.Envelope) is not { } message) continue;

                        if (message is End)
                        {
                            // Closed before the end is handed over: a reading that stops at the end, as a view's loop
                            // does, never asks for more, and the recorder waits for the window to go.
                            await CloseAfterTheEndAsync();
                            yield return message;
                            yield break;
                        }

                        yield return message;
                        continue;
                }

                // The link broke.
                _pending = null;
                if (_leaving.IsCancellationRequested) yield break;
            }
            else if (!mending)
            {
                yield break;
            }

            // The link broke just now, or while an earlier reading, stopped by its token, was making it again: it is made
            // again here.
            var mended = await ReconnectAsync(cancellationToken);
            if (mended is null) yield break;

            yield return mended;
            if (mended is LinkLost) yield break;
        }
    }

    /// <summary>
    /// Passes the key named <paramref name="key"/> on to the recorder: Q, P, M, V or Ctrl+C (see <see cref="LinkKeys"/>);
    /// any other name throws an <see cref="ArgumentException"/>. Returns true once it has gone; false when there is no link
    /// to send it on, before connecting, while a broken link is being made again, after the end, or once the window has
    /// left, or when the link broke as it went.
    /// </summary>
    public async Task<bool> SendKeyAsync(string key, CancellationToken cancellationToken)
    {
        if (!LinkKeys.IsKnown(key)) throw new ArgumentException($"\"{key}\" is not a key the link passes on.", nameof(key));

        LineChannel? channel;
        lock (_gate) channel = _channel;
        if (channel is null)
        {
            logger.LogDebug("The key {Key} was not passed on: there is no link to the recording now", key);
            return false;
        }

        try
        {
            await channel.SendAsync(LinkTypes.Key, new KeyPress(key), LinkJson.Default.KeyPress, cancellationToken);
            return true;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // Deliberately not rethrown: the link broke or closed as the key went. The reading notices and makes the link
            // again; the caller learns that the key did not go, and can say so.
            logger.LogDebug(ex, "The key {Key} was not passed on: the link to the recording broke", key);
            return false;
        }
    }

    /// <summary>
    /// Says bye to the recorder, for <paramref name="reason"/> (see <see cref="ByeReasons"/>; any other throws an
    /// <see cref="ArgumentException"/>), and closes the link. The run carries on. The reading ends without a word, and the
    /// client connects no more.
    /// </summary>
    public async Task ByeAsync(string reason)
    {
        if (!ByeReasons.IsKnown(reason)) throw new ArgumentException($"\"{reason}\" is not a reason a window says bye for.", nameof(reason));

        if (await LeaveAsync() is not { } channel) return;

        try
        {
            using var patience = new CancellationTokenSource(ByePatience, time);
            await channel.SendAsync(LinkTypes.Bye, new Bye(reason), LinkJson.Default.Bye, patience.Token);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
            // Deliberately not rethrown: the window leaves whether or not the recorder heard its bye, and a recorder that did
            // not sees the connection close all the same.
            logger.LogDebug(ex, "The recorder did not take the window's bye; the window leaves all the same");
        }
        finally
        {
            await channel.DisposeAsync();
        }
    }

    /// <summary>Closes the link without a bye: the recorder sees the window go. The reading ends without a word. Closing twice does nothing.</summary>
    public async ValueTask DisposeAsync()
    {
        if (await LeaveAsync() is { } channel) await channel.DisposeAsync();
    }

    /// <summary>Opens the run's pipe, for this user only, waiting at most <paramref name="limit"/> for a free instance: a <see cref="TimeoutException"/> when none came.</summary>
    public static async Task<Stream> OpenRealPipeAsync(string pipeName, TimeSpan limit, CancellationToken cancellationToken)
    {
        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            await pipe.ConnectAsync(limit, cancellationToken);
            return pipe;
        }
        catch
        {
            await pipe.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// What to do about a recorder started with other rights than this window: start ClassScribe without Run as
    /// administrator when this window runs as administrator, as the recording then was started without it, and with Run as
    /// administrator when this window does not.
    /// </summary>
    private string OtherRightsAdvice() =>
        (Elevated ?? Environment.IsPrivilegedProcess)
            ? "Start ClassScribe the same way as the recording (without Run as administrator) to see it."
            : "Start ClassScribe the same way as the recording (with Run as administrator) to see it.";

    /// <summary>The window leaves: no link is made or used any more. Returns the link there was, for the caller to close.</summary>
    private async Task<LineChannel?> LeaveAsync()
    {
        LineChannel? channel;
        lock (_gate)
        {
            channel = _channel;
            _channel = null;
        }

        await _leaving.CancelAsync();
        return channel;
    }

    /// <summary>Makes <paramref name="channel"/> the link, and starts the watch for silence from now. False when the window has left meanwhile.</summary>
    private bool Install(LineChannel channel)
    {
        lock (_gate)
        {
            if (_leaving.IsCancellationRequested) return false;

            _channel = channel;
            _mending = false;
            _lastStateAt = time.GetTimestamp();
            _silenceTold = false;
            return true;
        }
    }

    /// <summary>The run's end has come: closes the link, as there is nothing more to read or send.</summary>
    private async Task CloseAfterTheEndAsync()
    {
        LineChannel? channel;
        lock (_gate)
        {
            _over = true;
            channel = _channel;
            _channel = null;
        }

        if (channel is not null) await channel.DisposeAsync();
    }

    /// <summary>
    /// Waits for <paramref name="pending"/>, the next message on the link, and meanwhile watches for a silence: no state for
    /// <see cref="SilenceLimit"/>, said once. When <paramref name="cancellationToken"/> is cancelled, stops waiting and
    /// throws an <see cref="OperationCanceledException"/>, while the wait itself goes on for the next reading. Never throws
    /// for a link that broke, closed or carried something that is not a message: that is <see cref="Next.Broken"/>.
    /// </summary>
    private async Task<Next> NextAsync(Task<Envelope?> pending, CancellationToken cancellationToken)
    {
        bool told;
        long lastState;
        lock (_gate)
        {
            told = _silenceTold;
            lastState = _lastStateAt;
        }

        if (!told)
        {
            var left = SilenceLimit - time.GetElapsedTime(lastState);
            if (left <= TimeSpan.Zero) return Silence(lastState);

            using var stopWatching = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var silence = Task.Delay(left, time, stopWatching.Token);
            if (await Task.WhenAny(pending, silence) == silence)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Silence(lastState);
            }

            await stopWatching.CancelAsync();
        }
        else
        {
            await ((Task)pending).WaitAsync(cancellationToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            cancellationToken.ThrowIfCancellationRequested();
        }

        try
        {
            return await pending is { } envelope ? new Next.Received(envelope) : new Next.Broken();
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ObjectDisposedException or OperationCanceledException)
        {
            // Deliberately not rethrown: the link broke, was closed under the reading, or carried something that is not a
            // message of the link. The reading makes it again while the recorder's process runs, and says so when it cannot.
            logger.LogDebug(ex, "The link to the recording broke");
            return new Next.Broken();
        }
    }

    /// <summary>Notes that the silence since <paramref name="lastState"/> has been said, and says it.</summary>
    private Next.Silent Silence(long lastState)
    {
        lock (_gate) _silenceTold = true;
        return new Next.Silent(time.GetElapsedTime(lastState));
    }

    /// <summary>
    /// What a message means for the window: a line it has not been given yet, a state (which ends a silence), or the end; null
    /// for a line it has, a message it cannot read, and a type this build does not know or does not expect now.
    /// </summary>
    private object? Interpret(Envelope envelope)
    {
        switch (envelope.Type)
        {
            case LinkTypes.Log:
                if (TryRead(envelope.Body, LinkJson.Default.LogLine) is not { } line) return null;

                lock (_gate)
                {
                    if (line.Seq <= _lastSeen) return null;

                    _lastSeen = line.Seq;
                }

                return line;

            case LinkTypes.State:
                if (TryRead(envelope.Body, LinkJson.Default.StateUpdate) is not { } state) return null;

                lock (_gate)
                {
                    _lastStateAt = time.GetTimestamp();
                    _silenceTold = false;
                }

                return state;

            case LinkTypes.End:
                return TryRead(envelope.Body, LinkJson.Default.End);

            default:
                return null;
        }
    }

    /// <summary>
    /// Makes a broken link again, while the recorder's process runs. Returns <see cref="Reconnected"/>, or
    /// <see cref="LinkLost"/> when the process has ended or the link could not be made within
    /// <see cref="ReconnectPatience"/>; null when the window left meanwhile. When the reading's token stops it first, the
    /// mending is left for the next reading (see <see cref="_mending"/>), which begins it again with the full patience.
    /// </summary>
    private async Task<object?> ReconnectAsync(CancellationToken cancellationToken)
    {
        RunRecord run;
        Hello hello;
        LineChannel? broken;
        lock (_gate)
        {
            run = _run!;
            hello = _hello!;
            broken = _channel;
            _channel = null;
            _mending = true;
        }

        if (broken is not null) await broken.DisposeAsync();

        using var waiting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _leaving.Token);
        Attempt attempt;
        try
        {
            attempt = await ConnectWithinAsync(run, hello, ReconnectPatience, FirstRetry, waiting.Token);
        }
        catch (OperationCanceledException) when (_leaving.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            // Deliberately not rethrown: the window said bye or closed while the link was being made again; the reading ends
            // without a word.
            return null;
        }

        if (attempt is Attempt.Connected connected)
        {
            if (Install(connected.Channel))
            {
                logger.LogDebug("The link to the recording of run {RunId} was made again", run.RunId);
                return new Reconnected(connected.Welcome);
            }

            await connected.Channel.DisposeAsync();
            return null;
        }

        // The link is given up: what follows is the last thing the reading yields.
        lock (_gate) _mending = false;

        if (attempt is Attempt.Gone)
        {
            logger.LogDebug("The recorder of run {RunId} (process {ProcessId}) has ended", run.RunId, run.ProcessId);
            return new LinkLost(LinkLoss.RecorderGone);
        }

        logger.LogDebug("Could not make the link to the recording of run {RunId} again ({Attempt}); the recording carries on", run.RunId, attempt.GetType().Name);
        return new LinkLost(LinkLoss.ConnectionLost);
    }

    /// <summary>
    /// Tries to connect to the recorder of <paramref name="run"/>, after <paramref name="firstPause"/>, then at pauses that
    /// double up to <see cref="LongestRetry"/>, while its process runs, for up to <paramref name="patience"/>.
    /// </summary>
    private async Task<Attempt> ConnectWithinAsync(RunRecord run, Hello hello, TimeSpan patience, TimeSpan firstPause, CancellationToken cancellationToken)
    {
        var started = time.GetTimestamp();
        var pause = firstPause;
        while (true)
        {
            if (!liveness.IsAlive(run.ProcessId, run.ProcessStartedUtc)) return new Attempt.Gone();

            if (pause > TimeSpan.Zero)
            {
                var left = patience - time.GetElapsedTime(started);
                if (left <= TimeSpan.Zero) return new Attempt.NoAnswer();

                await Task.Delay(pause < left ? pause : left, time, cancellationToken);
                if (!liveness.IsAlive(run.ProcessId, run.ProcessStartedUtc)) return new Attempt.Gone();
                if (time.GetElapsedTime(started) >= patience) return new Attempt.NoAnswer();
            }

            if (await TryOnceAsync(run, hello, cancellationToken) is { } attempt) return attempt;

            pause = pause == TimeSpan.Zero ? FirstRetry : TimeSpan.FromTicks(Math.Min(pause.Ticks * 2, LongestRetry.Ticks));
        }
    }

    /// <summary>
    /// One try: opens the pipe, says hello with the last line the window has, and waits for the answer. Null when it is
    /// worth trying again: no instance was free, or the connection ended or broke before an answer came.
    /// </summary>
    private async Task<Attempt?> TryOnceAsync(RunRecord run, Hello hello, CancellationToken cancellationToken)
    {
        Stream stream;
        try
        {
            stream = await OpenPipe(run.Pipe!, LongestTry, cancellationToken);
        }
        catch (TimeoutException)
        {
            // Deliberately not rethrown: no instance of the pipe was free, as before a recorder that has just started has made
            // it, or while four windows show the run; the caller tries again while the recorder's process runs.
            return null;
        }
        catch (IOException ex)
        {
            // Deliberately not rethrown: the pipe was closing as the window opened it; the caller tries again while the
            // recorder's process runs.
            logger.LogDebug(ex, "Could not open the pipe of run {RunId}; trying again", run.RunId);
            return null;
        }
        catch (UnauthorizedAccessException ex)
        {
            // Deliberately not rethrown: a pipe made with other rights cannot be opened however often it is tried, so the
            // caller reports it in plain words, with this exception as its cause.
            return new Attempt.OtherRights(ex);
        }

        long since;
        lock (_gate) since = _lastSeen;

        var channel = new LineChannel(stream);
        Attempt? answer;
        try
        {
            answer = await HandshakeAsync(channel, hello with { Since = since }, cancellationToken);
        }
        catch
        {
            // The window gave up before the recorder answered, as when it left or its caller stopped waiting: the connection
            // is closed, so that the recorder sees the window go rather than keep it as one that never reads.
            await channel.DisposeAsync();
            throw;
        }

        if (answer is not Attempt.Connected) await channel.DisposeAsync();

        return answer;
    }

    /// <summary>
    /// Says hello on <paramref name="channel"/> and waits, for at most <see cref="WelcomePatience"/>, for the recorder's
    /// welcome or refusal. Null when none came: the connection ended, broke or carried what is not a message of the link.
    /// </summary>
    private async Task<Attempt?> HandshakeAsync(LineChannel channel, Hello hello, CancellationToken cancellationToken)
    {
        using var patience = new CancellationTokenSource(WelcomePatience, time);
        using var waiting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, patience.Token);
        try
        {
            await channel.SendAsync(LinkTypes.Hello, hello, LinkJson.Default.Hello, waiting.Token);
            while (await channel.ReceiveAsync(waiting.Token) is { } answer)
            {
                switch (answer.Type)
                {
                    case LinkTypes.Welcome when TryRead(answer.Body, LinkJson.Default.Welcome) is { } welcome:
                        return welcome.Protocol == LinkProtocol.Version
                            ? new Attempt.Connected(channel, welcome)
                            : new Attempt.Refusal(new Refused($"The recorder speaks protocol {welcome.Protocol} of ClassScribe's link, and this window {LinkProtocol.Version}.", welcome.ExePath));

                    case LinkTypes.Refused when TryRead(answer.Body, LinkJson.Default.Refused) is { } refused:
                        return new Attempt.Refusal(refused);
                }
            }

            return null;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ObjectDisposedException
                                       || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            // Deliberately not rethrown: the recorder did not answer the hello in time, or the connection broke or carried
            // what is not a message of the link; the caller tries again while the recorder's process runs.
            logger.LogDebug(ex, "The recorder did not answer the window's hello");
            return null;
        }
    }

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
            // Deliberately not rethrown: a message from the recorder that this build cannot read, as one from a later build
            // may be, is passed over, as the link's protocol says; the reading goes on. A choice that does not say which
            // kind it is, such as a source of the class without its "kind", gives a NotSupportedException rather than a
            // JsonException, and is passed over the same way.
            logger.LogDebug(ex, "The recorder sent a {Type} message this build cannot read; it was passed over", typeof(T).Name);
            return null;
        }
    }

    /// <summary>What waiting for the next message gave.</summary>
    private abstract record Next
    {
        /// <summary>A message.</summary>
        /// <param name="Envelope">The message.</param>
        public sealed record Received(Envelope Envelope) : Next;

        /// <summary>No state for <see cref="SilenceLimit"/>; the message is still awaited.</summary>
        /// <param name="For">How long since the last state.</param>
        public sealed record Silent(TimeSpan For) : Next;

        /// <summary>The link broke, closed or carried what is not a message of the link.</summary>
        public sealed record Broken : Next;
    }

    /// <summary>What trying to connect gave.</summary>
    private abstract record Attempt
    {
        /// <summary>Connected and welcomed.</summary>
        /// <param name="Channel">The link.</param>
        /// <param name="Welcome">The recorder's welcome.</param>
        public sealed record Connected(LineChannel Channel, Welcome Welcome) : Attempt;

        /// <summary>The recorder's process has ended.</summary>
        public sealed record Gone : Attempt;

        /// <summary>The recorder did not answer in the time allowed.</summary>
        public sealed record NoAnswer : Attempt;

        /// <summary>The recorder speaks another protocol.</summary>
        /// <param name="Refused">Its refusal.</param>
        public sealed record Refusal(Refused Refused) : Attempt;

        /// <summary>The recorder runs with other rights than the window.</summary>
        /// <param name="Exception">What Windows said.</param>
        public sealed record OtherRights(UnauthorizedAccessException Exception) : Attempt;
    }
}
