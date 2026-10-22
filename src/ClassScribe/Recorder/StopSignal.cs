using System.Runtime.Versioning;
using ClassScribe.Commands;
using Microsoft.Extensions.Logging;

namespace ClassScribe.Recorder;

/// <summary>
/// The emergency stop of one run: a named event, Local\ClassScribe.Stop.&lt;run id&gt;, that the recorder waits on and any
/// window can set, asking for the same stop as Q pressed twice. It never depends on the pipe between them, so it also
/// reaches a recorder that no longer answers, or one started by another build of ClassScribe. The event lives in this
/// session's namespace, and is made so that only this user can open it; one of the same name made any other way, as
/// another program could plant it, is neither listened to nor set. The recorder waits through the thread pool, without a
/// thread of its own. A YouTube video's run has a second event of the same kind, its yield,
/// Local\ClassScribe.Yield.&lt;run id&gt;, which a class that starts sets to stop the video's recording so that the class can
/// record (design 2.5), and which tells the run why it stopped.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class StopSignal : IDisposable
{
    /// <summary>The start of every stop event's name: this session's namespace and ClassScribe's own prefix.</summary>
    public const string NamePrefix = @"Local\ClassScribe.Stop.";

    /// <summary>The start of every yield event's name, a video's run's second event: this session's namespace and ClassScribe's own prefix.</summary>
    public const string YieldPrefix = @"Local\ClassScribe.Yield.";

    /// <summary>What a run says when its stop event is set.</summary>
    private const string StopHeard = "Another window asked for this recording to stop";

    /// <summary>What a video's run says when its yield event is set.</summary>
    private const string YieldHeard = "A class is starting, so this video's recording gives way to it";

    /// <summary>Only this user may open the event, and only in this session.</summary>
    private static readonly NamedWaitHandleOptions ThisUserInThisSession = new() { CurrentUserOnly = true, CurrentSessionOnly = true };

    /// <summary>How long letting go waits for an answer to the stop that is under way to finish.</summary>
    private static readonly TimeSpan AnswerPatience = TimeSpan.FromSeconds(5);

    private readonly EventWaitHandle _event;
    private readonly ILogger _logger;
    private readonly string _heard;
    private int _disposed;

    /// <summary>
    /// The wait in the thread pool. Null only until the constructor has set it, which an answer to an event already set
    /// when listening began may come before.
    /// </summary>
    private readonly RegisteredWaitHandle? _wait;

    /// <summary>The managed id of the thread answering the stop while it does, or 0.</summary>
    private int _answeringThread;

    /// <summary>Listens on <paramref name="stopEvent"/> for <paramref name="onStop"/>, once, saying <paramref name="heard"/> when it is set.</summary>
    private StopSignal(EventWaitHandle stopEvent, Action onStop, string heard, ILogger logger)
    {
        _event = stopEvent;
        _logger = logger;
        _heard = heard;
        _wait = ThreadPool.RegisterWaitForSingleObject(stopEvent, (_, _) => Answer(onStop), null, Timeout.Infinite, executeOnlyOnce: true);
    }

    /// <summary>The name of the stop event of run <paramref name="runId"/>. Throws an <see cref="ArgumentException"/> for anything but a run id as ClassScribe makes them.</summary>
    public static string NameFor(string runId) => Named(NamePrefix, runId);

    /// <summary>The name of the yield event of run <paramref name="runId"/>, a video's. Throws an <see cref="ArgumentException"/> for anything but a run id as ClassScribe makes them.</summary>
    public static string YieldNameFor(string runId) => Named(YieldPrefix, runId);

    /// <summary>
    /// Makes the stop event of run <paramref name="runId"/> and calls <paramref name="onStop"/> on a thread of the thread
    /// pool once a window sets it, once however often it is set, until the result is disposed. A failure in
    /// <paramref name="onStop"/> is logged to <paramref name="logger"/>, since an exception there would end the process.
    /// Throws a <see cref="WaitHandleCannotBeOpenedException"/> when an event of that name was made without ClassScribe's
    /// protection, as another program could plant it: the run then has no emergency stop, which the caller decides about.
    /// </summary>
    public static StopSignal Listen(string runId, Action onStop, ILogger logger) =>
        ListenOn(NameFor(runId), onStop, StopHeard, logger,
            "Something other than ClassScribe made an event named {Name}, so this recording cannot be stopped from another window this time; Q pressed twice still stops it");

    /// <summary>
    /// Makes the yield event of run <paramref name="runId"/>, a video's, and calls <paramref name="onYield"/> as
    /// <see cref="Listen"/> calls its answer, once a class that starts sets it. Throws as <see cref="Listen"/> does.
    /// </summary>
    public static StopSignal ListenForYield(string runId, Action onYield, ILogger logger) =>
        ListenOn(YieldNameFor(runId), onYield, YieldHeard, logger,
            "Something other than ClassScribe made an event named {Name}, so a class cannot stop this video's recording by itself this time; Q pressed twice still stops it");

    /// <summary>
    /// Sets the stop event of run <paramref name="runId"/>, which stops its recorder, and returns true; or returns false when
    /// no recorder of this user listens for it, because it has ended or was never started, or when the event of that name
    /// is not ClassScribe's.
    /// </summary>
    public static bool TrySet(string runId, ILogger logger) => TrySetNamed(NameFor(runId), logger);

    /// <summary>
    /// Sets the yield event of run <paramref name="runId"/>, a video's, which stops its recording so that a class can record,
    /// and returns true; or false, as <see cref="TrySet"/> does, when nothing of this user listens for it.
    /// </summary>
    public static bool TrySetYield(string runId, ILogger logger) => TrySetNamed(YieldNameFor(runId), logger);

    /// <summary>
    /// Stops listening and closes the event, so that a stop asked for afterwards is not answered. An answer to a stop under
    /// way on another thread is waited for, up to 5 seconds, so that none runs once this returns; letting go from inside the
    /// answer itself does not wait for it. The thread pool lets go of its own hold on the event a moment after the wait is
    /// gone, and Windows deletes the event once nothing holds it; a window that asks for a stop in that moment may still find
    /// the event, but nobody answers it.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        if (Volatile.Read(ref _answeringThread) == Environment.CurrentManagedThreadId)
        {
            // The answer under way is this call's own caller, which the pool counts as running until it returns, so waiting
            // for the answers to finish here would wait for itself.
            _wait?.Unregister(null);
        }
        else
        {
            // The pool sets this event once the wait is gone and no answer to it runs any more.
            using var unregistered = new ManualResetEvent(false);
            if (_wait?.Unregister(unregistered) == true && !unregistered.WaitOne(AnswerPatience))
                _logger.LogDebug("The answer to a stop asked for from another window is still under way");
        }

        _event.Dispose();
    }

    /// <summary><paramref name="prefix"/> and <paramref name="runId"/>, when it is a run id; an <see cref="ArgumentException"/> for anything else.</summary>
    private static string Named(string prefix, string runId)
    {
        if (!RunIds.IsWellFormed(runId)) throw new ArgumentException($"\"{runId}\" is not a ClassScribe run id.", nameof(runId));

        return prefix + runId;
    }

    /// <summary>
    /// Makes the event <paramref name="name"/> and listens on it for <paramref name="onStop"/>, saying <paramref name="heard"/>
    /// when it is set; an event of that name made without ClassScribe's protection is said with <paramref name="planted"/>,
    /// and thrown.
    /// </summary>
    private static StopSignal ListenOn(string name, Action onStop, string heard, ILogger logger, string planted)
    {
        EventWaitHandle stopEvent;
        bool createdNew;
        try
        {
            stopEvent = new EventWaitHandle(false, EventResetMode.ManualReset, name, ThisUserInThisSession, out createdNew);
        }
        catch (WaitHandleCannotBeOpenedException ex)
        {
            logger.LogWarning(ex, planted, name);
            throw;
        }

        try
        {
            if (!createdNew) logger.LogDebug("The event {Name} was already there; listening to it all the same", name);
            var signal = new StopSignal(stopEvent, onStop, heard, logger);
            logger.LogDebug("Listening through {Name}", name);
            return signal;
        }
        catch
        {
            stopEvent.Dispose();
            throw;
        }
    }

    /// <summary>Sets the event <paramref name="name"/>, and returns true; false when nothing of this user listens for it, or it is not ClassScribe's.</summary>
    private static bool TrySetNamed(string name, ILogger logger)
    {
        bool opened;
        EventWaitHandle? stopEvent;
        try
        {
            opened = EventWaitHandle.TryOpenExisting(name, ThisUserInThisSession, out stopEvent);
        }
        catch (UnauthorizedAccessException ex)
        {
            // Deliberately not rethrown: an event this user may not open is no recorder of this user's, as the answer says.
            logger.LogDebug(ex, "The event {Name} is not this user's to set", name);
            return false;
        }

        if (!opened || stopEvent is null)
        {
            logger.LogDebug("Nothing listens through {Name}", name);
            return false;
        }

        using (stopEvent) stopEvent.Set();
        logger.LogDebug("Set {Name}", name);
        return true;
    }

    /// <summary>Calls <paramref name="onStop"/> for a stop that was asked for, unless the signal was disposed meanwhile. Runs on a thread of the thread pool.</summary>
    private void Answer(Action onStop)
    {
        if (Volatile.Read(ref _disposed) != 0) return;

        Volatile.Write(ref _answeringThread, Environment.CurrentManagedThreadId);
        try
        {
            _logger.LogInformation("{Heard}", _heard);
            onStop();
        }
        catch (Exception ex)
        {
            // Deliberately not rethrown: this runs on a thread of the thread pool, where an exception would end the
            // process, and the recording with it. Q pressed twice still stops it.
            _logger.LogError(ex, "Could not stop the recording as another window asked; press Q twice in a ClassScribe window that shows it");
        }
        finally
        {
            Volatile.Write(ref _answeringThread, 0);
        }
    }
}

/// <summary>
/// The yield of a YouTube video's run to a class (design 2.5): a class that starts while a video records asks the video's run
/// to give way, and the run stops as Q pressed twice stops it, noting that a class stopped it, so that its end is not
/// confirmed and the same command records it again. Behind an interface so that the starter's and the run's tests need no
/// real event; the real one is <see cref="WindowsVideoYield"/>.
/// </summary>
public interface IVideoYield
{
    /// <summary>
    /// Listens for the yield of run <paramref name="runId"/>, a video's, and calls <paramref name="onYield"/> once a class asks,
    /// on another thread, until the result is disposed; null when it cannot listen, which is said, and the run goes on: Q
    /// pressed twice still stops it.
    /// </summary>
    IDisposable? Listen(string runId, Action onYield);

    /// <summary>Asks run <paramref name="runId"/>, a video's, to give way to a class; false when nothing listens for it.</summary>
    bool Ask(string runId);
}

/// <summary>The yield through the run's own event, Local\ClassScribe.Yield.&lt;run id&gt; (see <see cref="StopSignal"/>).</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsVideoYield(ILogger<WindowsVideoYield> logger) : IVideoYield
{
    /// <inheritdoc />
    public IDisposable? Listen(string runId, Action onYield)
    {
        try
        {
            return StopSignal.ListenForYield(runId, onYield, logger);
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            // Deliberately not rethrown: the signal has logged that something else made an event of that name, and that Q
            // pressed twice still stops the video; the video is recorded all the same.
            return null;
        }
    }

    /// <inheritdoc />
    public bool Ask(string runId) => StopSignal.TrySetYield(runId, logger);
}
