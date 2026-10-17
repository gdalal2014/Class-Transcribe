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
/// thread of its own.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class StopSignal : IDisposable
{
    /// <summary>The start of every stop event's name: this session's namespace and ClassScribe's own prefix.</summary>
    public const string NamePrefix = @"Local\ClassScribe.Stop.";

    /// <summary>Only this user may open the event, and only in this session.</summary>
    private static readonly NamedWaitHandleOptions ThisUserInThisSession = new() { CurrentUserOnly = true, CurrentSessionOnly = true };

    /// <summary>How long letting go waits for an answer to the stop that is under way to finish.</summary>
    private static readonly TimeSpan AnswerPatience = TimeSpan.FromSeconds(5);

    private readonly EventWaitHandle _event;
    private readonly ILogger _logger;
    private int _disposed;

    /// <summary>
    /// The wait in the thread pool. Null only until the constructor has set it, which an answer to an event already set
    /// when listening began may come before.
    /// </summary>
    private readonly RegisteredWaitHandle? _wait;

    /// <summary>The managed id of the thread answering the stop while it does, or 0.</summary>
    private int _answeringThread;

    /// <summary>Listens on <paramref name="stopEvent"/> for <paramref name="onStop"/>, once.</summary>
    private StopSignal(EventWaitHandle stopEvent, Action onStop, ILogger logger)
    {
        _event = stopEvent;
        _logger = logger;
        _wait = ThreadPool.RegisterWaitForSingleObject(stopEvent, (_, _) => Answer(onStop), null, Timeout.Infinite, executeOnlyOnce: true);
    }

    /// <summary>The name of the stop event of run <paramref name="runId"/>. Throws an <see cref="ArgumentException"/> for anything but a run id as ClassScribe makes them.</summary>
    public static string NameFor(string runId)
    {
        if (!RunIds.IsWellFormed(runId)) throw new ArgumentException($"\"{runId}\" is not a ClassScribe run id.", nameof(runId));

        return NamePrefix + runId;
    }

    /// <summary>
    /// Makes the stop event of run <paramref name="runId"/> and calls <paramref name="onStop"/> on a thread of the thread
    /// pool once a window sets it, once however often it is set, until the result is disposed. A failure in
    /// <paramref name="onStop"/> is logged to <paramref name="logger"/>, since an exception there would end the process.
    /// Throws a <see cref="WaitHandleCannotBeOpenedException"/> when an event of that name was made without ClassScribe's
    /// protection, as another program could plant it: the run then has no emergency stop, which the caller decides about.
    /// </summary>
    public static StopSignal Listen(string runId, Action onStop, ILogger logger)
    {
        var name = NameFor(runId);
        EventWaitHandle stopEvent;
        bool createdNew;
        try
        {
            stopEvent = new EventWaitHandle(false, EventResetMode.ManualReset, name, ThisUserInThisSession, out createdNew);
        }
        catch (WaitHandleCannotBeOpenedException ex)
        {
            logger.LogWarning(ex, "Something other than ClassScribe made an event named {Name}, so this recording cannot be stopped from another window this time; Q pressed twice still stops it", name);
            throw;
        }

        try
        {
            if (!createdNew) logger.LogDebug("The stop event {Name} was already there; listening to it all the same", name);
            var signal = new StopSignal(stopEvent, onStop, logger);
            logger.LogDebug("Listening for a stop asked for from another window, through {Name}", name);
            return signal;
        }
        catch
        {
            stopEvent.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Sets the stop event of run <paramref name="runId"/>, which stops its recorder, and returns true; or returns false when
    /// no recorder of this user listens for it, because it has ended or was never started, or when the event of that name
    /// is not ClassScribe's.
    /// </summary>
    public static bool TrySet(string runId, ILogger logger)
    {
        var name = NameFor(runId);
        bool opened;
        EventWaitHandle? stopEvent;
        try
        {
            opened = EventWaitHandle.TryOpenExisting(name, ThisUserInThisSession, out stopEvent);
        }
        catch (UnauthorizedAccessException ex)
        {
            // Deliberately not rethrown: an event this user may not open is no recorder of this user's, as the answer says.
            logger.LogDebug(ex, "The stop event {Name} is not this user's to set", name);
            return false;
        }

        if (!opened || stopEvent is null)
        {
            logger.LogDebug("No recorder listens for a stop through {Name}", name);
            return false;
        }

        using (stopEvent) stopEvent.Set();
        logger.LogDebug("Asked the recorder to stop, through {Name}", name);
        return true;
    }

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

    /// <summary>Calls <paramref name="onStop"/> for a stop that was asked for, unless the signal was disposed meanwhile. Runs on a thread of the thread pool.</summary>
    private void Answer(Action onStop)
    {
        if (Volatile.Read(ref _disposed) != 0) return;

        Volatile.Write(ref _answeringThread, Environment.CurrentManagedThreadId);
        try
        {
            _logger.LogInformation("Another window asked for this recording to stop");
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
