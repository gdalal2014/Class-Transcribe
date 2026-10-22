using ClassScribe.YouTube;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClassScribe.Tests;

/// <summary>
/// One YouTube batch at a time (design 2.1): a real lock file in a test's own ClassScribe folder. A second batch is refused
/// while the first holds it, and told which process holds it; once let go, it can be taken again.
/// </summary>
[TestFixture, Category("WindowsProcess")]
public class YouTubeLockTests
{
    /// <summary>A second batch is refused while the first holds the lock, and learns the holder's process id.</summary>
    [Test]
    public void A_second_batch_is_refused_and_told_who_holds_the_lock()
    {
        using var runtime = new TestRuntime();
        var folders = new YouTubeFolders(runtime.Folder);
        var first = new YouTubeLock(folders, NullLogger<YouTubeLock>.Instance);
        var second = new YouTubeLock(folders, NullLogger<YouTubeLock>.Instance);

        using var held = first.TryTake(out _);
        var refused = second.TryTake(out var holder);

        Assert.That(held, Is.Not.Null);
        Assert.That(refused, Is.Null);
        Assert.That(holder, Is.EqualTo(Environment.ProcessId));
        Assert.That(folders.LockFile, Does.StartWith(runtime.Folder.Root));
    }

    /// <summary>A lock let go can be taken again, and letting it go twice does no harm.</summary>
    [Test]
    public void A_lock_let_go_can_be_taken_again()
    {
        using var runtime = new TestRuntime();
        var youTubeLock = new YouTubeLock(new YouTubeFolders(runtime.Folder), NullLogger<YouTubeLock>.Instance);

        var held = youTubeLock.TryTake(out _)!;
        held.Dispose();
        held.Dispose();
        using var again = youTubeLock.TryTake(out _);

        Assert.That(again, Is.Not.Null);
    }
}
