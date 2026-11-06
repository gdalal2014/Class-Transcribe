using ClassScribe.Desktop.Windows;
using ClassScribe.Recorder;

namespace ClassScribe.Desktop;

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
