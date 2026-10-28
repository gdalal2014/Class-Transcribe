using ClassScribe.Commands;
using ClassScribe.Recorder;
using ClassScribe.Runs;
using ClassScribe.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace ClassScribe.Tests;

/// <summary>
/// The scopes the command runner makes, as a test watches them: each is a real scope of the container this wraps, noted in
/// the order it was made, with whether it has been disposed since.
/// </summary>
public sealed class WatchedScopes(IServiceScopeFactory inner) : IServiceScopeFactory
{
    private readonly Lock _gate = new();
    private readonly List<WatchedScope> _made = [];

    /// <summary>The scopes made so far, in order.</summary>
    public IReadOnlyList<WatchedScope> Made
    {
        get { lock (_gate) return [.. _made]; }
    }

    /// <inheritdoc />
    public IServiceScope CreateScope()
    {
        var scope = new WatchedScope(inner.CreateScope());
        lock (_gate) _made.Add(scope);
        return scope;
    }
}

/// <summary>One scope a test watches: the real one inside, and whether it has been disposed.</summary>
public sealed class WatchedScope(IServiceScope inner) : IServiceScope, IAsyncDisposable
{
    private volatile bool _disposed;

    /// <summary>True once the scope has been disposed, which disposes the services it made.</summary>
    public bool Disposed => _disposed;

    /// <inheritdoc />
    public IServiceProvider ServiceProvider => inner.ServiceProvider;

    /// <inheritdoc />
    public void Dispose()
    {
        _disposed = true;
        inner.Dispose();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        if (inner is IAsyncDisposable asynchronous) await asynchronous.DisposeAsync();
        else inner.Dispose();
    }
}

/// <summary>A run a test's starter was asked to start.</summary>
/// <param name="Request">The run as it was handed over.</param>
/// <param name="Origin">Where it was started from.</param>
/// <param name="Services">The services of the scope the starter was resolved in.</param>
/// <param name="Seen">What the starter read in that scope as it was handed the run, when the test asked it to read something; null otherwise.</param>
public sealed record StartedRun(RecordRequest Request, StartOrigin Origin, IServiceProvider Services, string? Seen);

/// <summary>A run a test's view was asked to show.</summary>
/// <param name="Run">The run, as its file said when it was found.</param>
/// <param name="Mode">How it was to be shown.</param>
/// <param name="Services">The services of the scope the view was resolved in.</param>
public sealed record ShownRun(RunRecord Run, ViewMode Mode, IServiceProvider Services);

/// <summary>
/// What the starter and the view of runs that a test plays were asked to do, shared by every instance a scope makes of
/// them, and how they answer: a start with <see cref="StartExitCode"/>, and each showing as the test queued it with
/// <see cref="EndNextView"/>, or as Esc when nothing is queued.
/// </summary>
public sealed class PlayedRuns
{
    private readonly Lock _gate = new();
    private readonly List<StartedRun> _started = [];
    private readonly List<ShownRun> _shown = [];
    private readonly Queue<ViewResult> _results = new();

    /// <summary>The exit code every start answers with: 0 unless the test sets another.</summary>
    public int StartExitCode { get; set; }

    /// <summary>What happens while a run is shown, before the showing ends, such as the recorder's process ending; nothing unless the test sets it.</summary>
    public Action<ShownRun>? WhileShown { get; set; }

    /// <summary>The runs the starter was asked to start, in order.</summary>
    public IReadOnlyList<StartedRun> Started
    {
        get { lock (_gate) return [.. _started]; }
    }

    /// <summary>The runs the view was asked to show, in order.</summary>
    public IReadOnlyList<ShownRun> Shown
    {
        get { lock (_gate) return [.. _shown]; }
    }

    /// <summary>Queues how the next showing of a run ends.</summary>
    public void EndNextView(ViewResult result)
    {
        lock (_gate) _results.Enqueue(result);
    }

    /// <summary>Notes a start, and returns its exit code.</summary>
    internal int Start(StartedRun run)
    {
        lock (_gate)
        {
            _started.Add(run);
            return StartExitCode;
        }
    }

    /// <summary>Notes a showing, lets <see cref="WhileShown"/> happen, and returns how it ends: as queued, or as Esc.</summary>
    internal ViewResult Show(ShownRun run)
    {
        lock (_gate) _shown.Add(run);
        WhileShown?.Invoke(run);
        lock (_gate) return _results.TryDequeue(out var result) ? result : new ViewResult(ViewEnd.Left, null, null);
    }
}

/// <summary>
/// The starter of runs as a test plays it, made in a scope: it notes each run it is handed, with the services of its
/// scope, in <see cref="PlayedRuns"/>, and starts nothing. With <paramref name="reading"/>, it also notes what that reads in
/// its scope at that moment, such as a setting.
/// </summary>
public sealed class FakeRecordStarter(PlayedRuns played, IServiceProvider services, Func<IServiceProvider, string?>? reading = null) : IRecordStarter
{
    /// <inheritdoc />
    public Task<int> RunAsync(RecordRequest request, StartOrigin origin) =>
        Task.FromResult(played.Start(new StartedRun(request, origin, services, reading?.Invoke(services))));
}

/// <summary>
/// The view of runs as a test plays it, made in a scope: it notes each run it is asked to show in <see cref="PlayedRuns"/>,
/// and shows nothing. (The starter's tests have their own <see cref="FakeRunViewer"/>, which queues answers per run.)
/// </summary>
public sealed class PlayedRunViewer(PlayedRuns played, IServiceProvider services) : IRunViewer
{
    /// <inheritdoc />
    public Task<ViewResult> ShowAsync(RunRecord run, ViewMode mode, CancellationToken cancellationToken) =>
        Task.FromResult(played.Show(new ShownRun(run, mode, services)));
}

/// <summary>Run files as the tests of the list, the menu and the runner write them.</summary>
public static class TestRunFiles
{
    /// <summary>
    /// Writes the run file of run <paramref name="number"/> (see <see cref="TestRuns.Id"/>), which records, or recorded,
    /// <paramref name="files"/> and is now in <paramref name="state"/>, recorded by process 1000 plus the number, which is
    /// started when <paramref name="alive"/>. <paramref name="change"/> fills in anything else the test needs. Returns the
    /// run as it was handed to the journal.
    /// </summary>
    public static RunRecord WriteRun(this TestRuntime runtime, int number, RecordingFiles files, RunState state, bool alive, Func<RunRecord, RunRecord>? change = null)
    {
        var processId = 1000 + number;
        var record = TestRuns.Record(TestRuns.Id(number), state, processId, name: files.Name) with { BaseName = files.BaseName, Folder = files.Folder };
        record = change?.Invoke(record) ?? record;
        runtime.Journal.Begin(record).Dispose();
        if (alive) runtime.Processes.Start(processId, record.ProcessStartedUtc);
        return record;
    }
}
