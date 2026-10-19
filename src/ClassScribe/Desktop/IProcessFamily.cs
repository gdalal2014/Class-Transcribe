using ClassScribe.Desktop.Windows;
using ClassScribe.Recorder;

namespace ClassScribe.Desktop;

/// <summary>
/// The processes a process started, and those they started in turn (design 4.3): the tree of ClassScribe's video window, whose
/// browser process is its root and whose audio service is one of its children, so that a video's run measures the window's
/// own sound, and an ordinary recording of Chrome leaves the window out. Behind an interface so that no test reads Windows'
/// process list.
/// </summary>
public interface IProcessFamily
{
    /// <summary>
    /// The tree of <paramref name="rootId"/>, which started at <paramref name="rootStartedUtc"/>: the root itself and every
    /// process descended from it, each started no earlier than its parent, so that a later process given a parent's id once
    /// that parent ended is never counted. Empty when the root no longer runs, or its id is another process's now.
    /// </summary>
    IReadOnlySet<int> TreeOf(int rootId, DateTimeOffset rootStartedUtc);
}

/// <summary>The tree of a process, from one snapshot of Windows' process list (see <see cref="IProcessFamily"/>). It only reads.</summary>
public sealed class ProcessFamily : IProcessFamily
{
    /// <summary>Windows' process list and start times, or a test's.</summary>
    private readonly IProcessTree _tree;

    /// <summary>The family over <paramref name="tree"/>.</summary>
    internal ProcessFamily(IProcessTree tree) => _tree = tree;

    /// <inheritdoc />
    public IReadOnlySet<int> TreeOf(int rootId, DateTimeOffset rootStartedUtc)
    {
        var processes = _tree.List();
        if (!processes.ContainsKey(rootId) || _tree.StartedUtc(rootId) is not { } rootStarted || !IProcessLiveness.StartedTogether(rootStarted, rootStartedUtc))
            return new HashSet<int>();

        var found = new HashSet<int> { rootId };
        var started = new Dictionary<int, DateTimeOffset> { [rootId] = rootStarted };
        var parents = new Queue<int>([rootId]);
        while (parents.TryDequeue(out var parent))
        {
            foreach (var (id, facts) in processes)
            {
                if (facts.ParentId != parent || found.Contains(id) || _tree.StartedUtc(id) is not { } childStarted || childStarted < started[parent]) continue;

                found.Add(id);
                started[id] = childStarted;
                parents.Enqueue(id);
            }
        }

        return found;
    }
}
