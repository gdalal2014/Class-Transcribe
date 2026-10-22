using System.Collections.Concurrent;

namespace ClassScribe.Camera;

/// <summary>
/// The camera process's own STA thread, on which every call on the camera's route is made (the probe fix's rule 5:
/// Microsoft documents InitializeAsync on an STA thread, and "calls from an MTA thread may result in undefined behavior";
/// the CLR's blocking waits on an STA thread pump COM calls). Work is queued to it and waited for with a time limit; work
/// that does not return in time is given up, the thread is left behind as stuck inside Windows, and every later call is
/// refused, so the camera process ends and its job is handed back.
/// </summary>
public sealed class CameraThread : IDisposable
{
    private readonly BlockingCollection<Action> _queue = new();
    private readonly Thread _thread;
    private readonly TimeProvider _time;
    private volatile bool _abandoned;

    /// <summary>Starts the thread, STA, named <paramref name="name"/>, timing its work on <paramref name="time"/>.</summary>
    public CameraThread(string name, TimeProvider time)
    {
        _time = time;
        _thread = new Thread(Loop) { IsBackground = true, Name = name };
        if (OperatingSystem.IsWindows()) _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    /// <summary>True once a call did not return in time: the thread may be stuck inside Windows, and takes no more work.</summary>
    public bool Abandoned => _abandoned;

    /// <summary>
    /// Runs <paramref name="work"/> on the thread and waits up to <paramref name="limit"/>: its result, or the exception it
    /// threw as it was; a <see cref="TimeoutException"/> when it did not return in time, after which the thread is abandoned.
    /// </summary>
    public async Task<T> RunAsync<T>(Func<T> work, TimeSpan limit)
    {
        if (_abandoned) throw new TimeoutException("The camera's thread is stuck inside Windows, so nothing more is asked of it");

        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _queue.Add(() =>
        {
            try
            {
                done.SetResult(work());
            }
            catch (Exception ex)
            {
                // Deliberately not rethrown: this runs on the camera's own thread, where an exception would end the camera
                // process; it is handed to the waiting caller as it was, through the task, which throws it there.
                done.SetException(ex);
            }
        });

        try
        {
            return await done.Task.WaitAsync(limit, _time);
        }
        catch (TimeoutException)
        {
            _abandoned = true;
            throw;
        }
    }

    /// <summary>Runs <paramref name="work"/> with no result on the thread (see <see cref="RunAsync{T}"/>).</summary>
    public Task RunAsync(Action work, TimeSpan limit) => RunAsync(() =>
    {
        work();
        return 0;
    }, limit);

    /// <summary>Lets the thread end once its queue is done; a thread left stuck inside Windows is not waited for.</summary>
    public void Dispose()
    {
        _queue.CompleteAdding();
        if (!_abandoned) _thread.Join(TimeSpan.FromSeconds(5));
    }

    /// <summary>Runs the queued work in order until the queue is closed.</summary>
    private void Loop()
    {
        foreach (var work in _queue.GetConsumingEnumerable()) work();
    }
}
