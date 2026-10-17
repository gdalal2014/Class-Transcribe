namespace ClassScribe.Tests;

/// <summary>
/// Ctrl+C as a test presses it, in place of the console's own: what the view of a run watches for, to pass Ctrl+C on to
/// the recorder whether or not a key is being read. Each press calls every watcher, as the console's event calls every
/// handler; a watcher that has let go is called no more.
/// </summary>
public sealed class FakeCtrlC
{
    private readonly Lock _gate = new();
    private readonly List<Action> _watchers = [];

    /// <summary>How many watch for Ctrl+C now.</summary>
    public int Watchers
    {
        get { lock (_gate) return _watchers.Count; }
    }

    /// <summary>Calls <paramref name="pressed"/> on each press, until the result is disposed.</summary>
    public IDisposable Watch(Action pressed)
    {
        lock (_gate) _watchers.Add(pressed);
        return new Unwatch(this, pressed);
    }

    /// <summary>Presses Ctrl+C once: every watcher is called.</summary>
    public void Press()
    {
        Action[] watchers;
        lock (_gate) watchers = [.. _watchers];
        foreach (var watcher in watchers) watcher();
    }

    /// <summary>Stops calling <paramref name="pressed"/>.</summary>
    private void Remove(Action pressed)
    {
        lock (_gate) _watchers.Remove(pressed);
    }

    /// <summary>Lets go of one watcher when disposed, once.</summary>
    /// <param name="ctrlC">The Ctrl+C the watcher watches.</param>
    /// <param name="pressed">The watcher.</param>
    private sealed class Unwatch(FakeCtrlC ctrlC, Action pressed) : IDisposable
    {
        private int _disposed;

        /// <summary>Lets go of the watcher.</summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) ctrlC.Remove(pressed);
        }
    }
}

/// <summary>
/// The emergency stop of a run as a test plays it, in place of setting the run's stop event in Windows: it notes each run
/// a window asked to stop, and answers whether a recorder listened (<see cref="Listening"/>).
/// </summary>
public sealed class FakeStopRequests
{
    private readonly Lock _gate = new();
    private readonly List<string> _asked = [];

    /// <summary>True when a recorder listens for the stop, so asking reaches it; true unless the test says otherwise.</summary>
    public bool Listening { get; set; } = true;

    /// <summary>The ids of the runs asked to stop, in order.</summary>
    public IReadOnlyList<string> Asked
    {
        get { lock (_gate) return [.. _asked]; }
    }

    /// <summary>Asks the recorder of <paramref name="runId"/> to stop; true when one listened.</summary>
    public bool Ask(string runId)
    {
        lock (_gate) _asked.Add(runId);
        return Listening;
    }
}
