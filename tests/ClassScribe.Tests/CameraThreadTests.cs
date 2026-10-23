using ClassScribe.Camera;

namespace ClassScribe.Tests;

/// <summary>
/// The camera process's own thread (<see cref="CameraThread"/>, the probe fix's rule 5): every call on the camera's route
/// runs there, in an STA apartment as Microsoft documents InitializeAsync; a call that does not return within its limit is
/// given up with a <see cref="TimeoutException"/>, the thread is abandoned as stuck inside Windows, and every later call is
/// refused at once without being queued. Nothing here touches a camera: the work is the test's own.
/// </summary>
[TestFixture]
public class CameraThreadTests
{
    /// <summary>The work runs on the thread's own STA apartment, named as the camera process names it, never on the caller's thread.</summary>
    [Test]
    public async Task The_work_runs_on_a_thread_of_its_own_in_an_STA_apartment()
    {
        using var thread = new CameraThread("ClassScribe camera test", TimeProvider.System);

        var (apartment, name, id) = await thread.RunAsync(() => (Thread.CurrentThread.GetApartmentState(), Thread.CurrentThread.Name, Environment.CurrentManagedThreadId), TimeSpan.FromSeconds(10));

        Assert.That(apartment, Is.EqualTo(ApartmentState.STA));
        Assert.That(name, Is.EqualTo("ClassScribe camera test"));
        Assert.That(id, Is.Not.EqualTo(Environment.CurrentManagedThreadId));
    }

    /// <summary>An exception the work throws reaches the caller as it was, and the thread goes on taking work.</summary>
    [Test]
    public async Task The_works_own_exception_reaches_the_caller_as_it_was()
    {
        using var thread = new CameraThread("ClassScribe camera test", TimeProvider.System);
        var refused = new UnauthorizedAccessException("Access is denied.") { HResult = CameraHresults.AccessDenied };

        var thrown = Assert.ThrowsAsync<UnauthorizedAccessException>(() => thread.RunAsync(() => throw refused, TimeSpan.FromSeconds(10)));
        var after = await thread.RunAsync(() => 42, TimeSpan.FromSeconds(10));

        Assert.That(thrown, Is.SameAs(refused));
        Assert.That(after, Is.EqualTo(42));
        Assert.That(thread.Abandoned, Is.False);
    }

    /// <summary>
    /// Work that blocks past its limit, on the camera process's clock, is given up with a <see cref="TimeoutException"/> at
    /// the limit and not before; the thread is then abandoned, and the next call is refused at once with the same exception,
    /// its work never queued: it does not run even once the stuck work returns.
    /// </summary>
    [Test]
    public async Task Work_stuck_past_its_limit_abandons_the_thread_and_later_calls_are_refused()
    {
        var time = new ManualTimeProvider(TestRuns.Start);
        var limit = TimeSpan.FromSeconds(30);
        using var stuck = new ManualResetEventSlim();
        using var thread = new CameraThread("ClassScribe camera test", time);
        var later = false;
        try
        {
            var running = thread.RunAsync(() => stuck.Wait(), limit);
            await Wait.UntilAsync(() => time.ActiveTimers == 1);
            time.Advance(limit - TimeSpan.FromMilliseconds(1));
            await Task.Delay(50);
            var beforeTheLimit = running.IsCompleted;
            time.Advance(TimeSpan.FromMilliseconds(1));

            Assert.That(beforeTheLimit, Is.False, "not given up before its limit");
            Assert.ThrowsAsync<TimeoutException>(() => running);
            Assert.That(thread.Abandoned, Is.True);
            var refused = Assert.ThrowsAsync<TimeoutException>(() => thread.RunAsync(() => later = true, limit));
            Assert.That(refused!.Message, Is.EqualTo("The camera's thread is stuck inside Windows, so nothing more is asked of it"));
        }
        finally
        {
            stuck.Set();
        }

        await Task.Delay(100);
        Assert.That(later, Is.False, "the refused call's work was never queued");
    }
}
