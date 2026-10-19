using System.Collections.Concurrent;
using ClassScribe.Desktop;

namespace ClassScribe.Tests;

/// <summary>What a played window does when it is asked to close.</summary>
public enum FakeCloseAnswer
{
    /// <summary>Chrome ends at once, as it does.</summary>
    Ends,

    /// <summary>Nothing happens: Chrome stays open, as one that hangs, or that asks a question first, would.</summary>
    Never
}

/// <summary>
/// ClassScribe's video windows as a test plays them (design 7.2): each process's window shows the title the test gives it,
/// as Windows would give it ("(2) The creed, part 1 - YouTube"), until the test takes it away; each close asked for is
/// counted, and does what the test says: by default it ends the process (<see cref="OnClose"/>), or nothing. No real window
/// is ever listed, read or closed.
/// </summary>
public sealed class FakeVideoWindows : IVideoWindows
{
    private readonly ConcurrentDictionary<int, string> _titles = new();
    private readonly ConcurrentDictionary<int, int> _closes = new();

    /// <summary>What a close does to each process, by default; <see cref="Answers"/> may say otherwise for one.</summary>
    public FakeCloseAnswer Answer { get; set; } = FakeCloseAnswer.Ends;

    /// <summary>What a close does to one process, in place of <see cref="Answer"/>.</summary>
    public ConcurrentDictionary<int, FakeCloseAnswer> Answers { get; } = new();

    /// <summary>Ends the process asked to close, as Chrome would, when its answer is to end: the test's world wires it.</summary>
    public Action<int>? OnClose { get; set; }

    /// <summary>How many times the windows of <paramref name="processId"/> were asked to close.</summary>
    public int ClosesOf(int processId) => _closes.TryGetValue(processId, out var count) ? count : 0;

    /// <summary>The window of <paramref name="processId"/> shows <paramref name="title"/>, as Windows gives it.</summary>
    public void Show(int processId, string title) => _titles[processId] = title;

    /// <summary>The window of <paramref name="processId"/> is gone.</summary>
    public void Gone(int processId) => _titles.TryRemove(processId, out _);

    /// <inheritdoc />
    public IReadOnlyList<string> TitlesOf(int processId) => _titles.TryGetValue(processId, out var title) ? [title] : [];

    /// <inheritdoc />
    public int Close(int processId)
    {
        _closes.AddOrUpdate(processId, 1, (_, count) => count + 1);
        var shown = _titles.ContainsKey(processId) ? 1 : 0;
        var answer = Answers.TryGetValue(processId, out var own) ? own : Answer;
        if (answer == FakeCloseAnswer.Ends)
        {
            Gone(processId);
            OnClose?.Invoke(processId);
        }

        return shown;
    }
}

/// <summary>
/// The video windows' processes as a test plays them, all of a piece: each Chrome the pretend starter starts runs, for the run
/// files' liveness too, and shows a window, titled "YouTube" until the test gives it a video's title, until it ends, by a
/// close that ends it, or by <see cref="End"/>, as Gulzar closing it does.
/// </summary>
public sealed class FakeVideoBrowsers
{
    private readonly FakeProcessStarter _starter;
    private readonly FakeProcessLiveness _liveness;

    /// <summary>Plays the browsers <paramref name="starter"/> starts, alive for <paramref name="liveness"/>, with their windows in <see cref="Windows"/>.</summary>
    public FakeVideoBrowsers(FakeProcessStarter starter, FakeProcessLiveness liveness)
    {
        _starter = starter;
        _liveness = liveness;
        Windows.OnClose = End;
        var before = starter.OnStart;
        starter.OnStart = launch =>
        {
            before?.Invoke(launch);
            if (launch.Kind != FakeLaunchKind.Browser || launch.ExitCode is not null) return;

            liveness.Start(launch.ProcessId, launch.StartedUtc);
            Windows.Show(launch.ProcessId, "YouTube");
        };
    }

    /// <summary>The windows of the browsers.</summary>
    public FakeVideoWindows Windows { get; } = new();

    /// <summary>The browsers started, in order.</summary>
    public IReadOnlyList<FakeLaunch> Started => _starter.Browsers;

    /// <summary>The last browser started.</summary>
    public FakeLaunch Last => _starter.Browsers[^1];

    /// <summary>The browser of process <paramref name="processId"/> ends, its window with it, as when Gulzar closes the window.</summary>
    public void End(int processId)
    {
        Windows.Gone(processId);
        _liveness.End(processId);
        _starter.Browsers.SingleOrDefault(launch => launch.ProcessId == processId)?.Exit(0);
    }
}

/// <summary>
/// The yield of a video's run to a class (design 2.5), as a test plays it: each run that listens is kept by its id until it
/// lets go; a yield asked of a run that listens reaches it on the thread pool, as the real event's answer does, and each run
/// asked is kept, listened to or not.
/// </summary>
public sealed class FakeVideoYield : ClassScribe.Recorder.IVideoYield
{
    private readonly ConcurrentDictionary<string, Action> _listening = new();
    private readonly ConcurrentQueue<string> _asked = new();

    /// <summary>The runs asked to give way, in order.</summary>
    public IReadOnlyList<string> Asked => [.. _asked];

    /// <summary>True while run <paramref name="runId"/> listens for its yield.</summary>
    public bool IsListening(string runId) => _listening.ContainsKey(runId);

    /// <inheritdoc />
    public IDisposable? Listen(string runId, Action onYield)
    {
        _listening[runId] = onYield;
        return new LetGo(() => _listening.TryRemove(runId, out _));
    }

    /// <inheritdoc />
    public bool Ask(string runId)
    {
        _asked.Enqueue(runId);
        if (!_listening.TryGetValue(runId, out var onYield)) return false;

        ThreadPool.QueueUserWorkItem(_ => onYield());
        return true;
    }

    /// <summary>Runs <paramref name="letGo"/> once, when disposed.</summary>
    private sealed class LetGo(Action letGo) : IDisposable
    {
        private Action? _letGo = letGo;

        /// <inheritdoc />
        public void Dispose() => Interlocked.Exchange(ref _letGo, null)?.Invoke();
    }
}
