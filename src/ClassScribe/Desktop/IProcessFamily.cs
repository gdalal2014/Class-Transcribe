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
