using System.Runtime.Versioning;
using ClassScribe.Commands;
using ClassScribe.Recorder;
using ClassScribe.Runs;

namespace ClassScribe.Tests;

/// <summary>
/// The emergency stop: a named event per run, in this session, that only this user can open, which any window can set to
/// stop a recorder whatever happens to the pipe, and which the recorder waits on without a thread of its own. Each test
/// uses a run id of its own, made now, so that no recorder's event is ever touched.
/// </summary>
[TestFixture, Platform("Win"), Category("WindowsProcess")]
[SupportedOSPlatform("windows")]
public class StopSignalTests
{
    private RecordingLogger<StopSignalTests> _log = null!;
    private string _runId = null!;

    /// <summary>Makes a log to inspect and a run id of the test's own.</summary>
    [SetUp]
    public void SetUp()
    {
        _log = new RecordingLogger<StopSignalTests>();
        _runId = RunIds.New(DateTimeOffset.UtcNow);
    }

    /// <summary>The event is named after the run, in this session's own namespace.</summary>
    [Test]
    public void The_event_is_named_after_the_run_in_this_session()
    {
        Assert.That(StopSignal.NameFor(_runId), Is.EqualTo($@"Local\ClassScribe.Stop.{_runId}"));
    }

    /// <summary>A stop asked for reaches the recorder listening for it.</summary>
    [Test]
    public void A_stop_asked_for_reaches_the_recorder_listening_for_it()
    {
        using var heard = new ManualResetEventSlim();
        using var signal = StopSignal.Listen(_runId, heard.Set, _log);

        var reached = StopSignal.TrySet(_runId, _log);

        Assert.That(reached, Is.True);
        Assert.That(heard.Wait(TimeSpan.FromSeconds(5)), Is.True);
        Assert.That(_log.Problems, Is.Empty);
    }

    /// <summary>A stop asked for a run nobody listens for reaches nobody, and says so.</summary>
    [Test]
    public void A_stop_asked_for_a_run_nobody_listens_for_reaches_nobody()
    {
        Assert.That(StopSignal.TrySet(_runId, _log), Is.False);
    }

    /// <summary>
    /// Once the recorder stops listening, a stop asked for reaches nobody, and the recorder is not called. The thread pool
    /// lets go of its own hold on the event a moment after the recorder has (see <see cref="StopSignal.Dispose"/>), so a
    /// stop asked for in that moment may still find the event, as a busy test run shows now and then. That stop is not
    /// answered either, and once the event is gone no stop finds it.
    /// </summary>
    [Test]
    public async Task Once_the_recorder_stops_listening_a_stop_reaches_nobody()
    {
        var calls = 0;
        StopSignal.Listen(_runId, () => Interlocked.Increment(ref calls), _log).Dispose();

        await Wait.UntilAsync(() => !StopSignal.TrySet(_runId, _log));
        await Task.Delay(100);

        Assert.That(Volatile.Read(ref calls), Is.Zero);
    }

    /// <summary>The stop is heard once, however often it is asked for: a second window asking again changes nothing.</summary>
    [Test]
    public void A_stop_is_heard_once_however_often_it_is_asked_for()
    {
        var calls = 0;
        using var heard = new ManualResetEventSlim();
        using var signal = StopSignal.Listen(_runId, () =>
        {
            Interlocked.Increment(ref calls);
            heard.Set();
        }, _log);

        StopSignal.TrySet(_runId, _log);
        heard.Wait(TimeSpan.FromSeconds(5));
        var again = StopSignal.TrySet(_runId, _log);
        Thread.Sleep(200);

        Assert.That(again, Is.True, "the event is still there while the recorder listens");
        Assert.That(Volatile.Read(ref calls), Is.EqualTo(1));
    }

    /// <summary>
    /// An event of the same name made without ClassScribe's protection, as another program could plant it, is not
    /// trusted: the recorder does not listen to it, and a window does not set it.
    /// </summary>
    [Test]
    public void An_event_of_the_same_name_made_without_ClassScribes_protection_is_not_trusted()
    {
        using var planted = new EventWaitHandle(false, EventResetMode.ManualReset, StopSignal.NameFor(_runId));

        Assert.Throws<WaitHandleCannotBeOpenedException>(() => StopSignal.Listen(_runId, () => { }, _log));
        Assert.That(StopSignal.TrySet(_runId, _log), Is.False);
        Assert.That(planted.WaitOne(0), Is.False, "the planted event was not set");
        Assert.That(_log.Problems.Single(), Does.StartWith($"Something other than ClassScribe made an event named {StopSignal.NameFor(_runId)}"));
    }

    /// <summary>
    /// A stop asked for while no recorder listens, here after the last one let go, reaches nobody and is not kept: a
    /// recorder that listens later starts clean, and is not stopped by it. The stop that counts is the one asked for once
    /// the event is gone: one asked for in the moment before, while the thread pool still held the event (see
    /// <see cref="StopSignal.Dispose"/>), could still find it.
    /// </summary>
    [Test]
    public async Task A_stop_asked_for_while_nobody_listens_is_not_kept_for_a_later_listener()
    {
        using var heard = new ManualResetEventSlim();
        StopSignal.Listen(_runId, () => { }, _log).Dispose();
        await Wait.UntilAsync(() => !StopSignal.TrySet(_runId, _log));

        using var signal = StopSignal.Listen(_runId, heard.Set, _log);

        Assert.That(heard.Wait(TimeSpan.FromMilliseconds(300)), Is.False);
    }

    /// <summary>
    /// Letting go while the answer to a stop is under way on the pool's thread waits for the answer to finish, so that no
    /// answer runs once the recorder has let go of its stop signal.
    /// </summary>
    [Test]
    public void Letting_go_waits_for_an_answer_under_way()
    {
        using var answering = new ManualResetEventSlim();
        using var finish = new ManualResetEventSlim();
        var signal = StopSignal.Listen(_runId, () =>
        {
            answering.Set();
            finish.Wait(TimeSpan.FromSeconds(10));
        }, _log);
        StopSignal.TrySet(_runId, _log);
        Assert.That(answering.Wait(TimeSpan.FromSeconds(5)), Is.True, "the answer began");

        var lettingGo = Task.Run(signal.Dispose);
        var letGoWhileAnswering = lettingGo.Wait(TimeSpan.FromMilliseconds(300));
        finish.Set();
        var letGoOnceAnswered = lettingGo.Wait(TimeSpan.FromSeconds(5));

        Assert.That(letGoWhileAnswering, Is.False, "letting go waited for the answer");
        Assert.That(letGoOnceAnswered, Is.True);
    }

    /// <summary>
    /// Letting go from inside the answer itself, as a recorder that ends the run there would, returns at once rather than
    /// waiting for the very answer it is part of.
    /// </summary>
    [Test]
    public void Letting_go_from_inside_the_answer_returns_at_once()
    {
        StopSignal? signal = null;
        using var letGo = new ManualResetEventSlim();
        var took = TimeSpan.Zero;
        signal = StopSignal.Listen(_runId, () =>
        {
            var started = System.Diagnostics.Stopwatch.StartNew();
            signal!.Dispose();
            took = started.Elapsed;
            letGo.Set();
        }, _log);

        StopSignal.TrySet(_runId, _log);

        Assert.That(letGo.Wait(TimeSpan.FromSeconds(10)), Is.True, "the answer let go");
        Assert.That(took, Is.LessThan(TimeSpan.FromSeconds(2)));
        Assert.That(_log.Problems, Is.Empty);
    }

    /// <summary>
    /// A recorder's answer to the stop that fails is logged, and goes no further: it runs on a thread of the thread pool,
    /// where an exception would end the recorder, and the recording with it.
    /// </summary>
    [Test]
    public async Task A_failure_in_answering_the_stop_is_logged_and_does_not_end_the_recorder()
    {
        using var signal = StopSignal.Listen(_runId, () => throw new InvalidOperationException("The run had already ended."), _log);

        StopSignal.TrySet(_runId, _log);
        await Wait.UntilAsync(() => _log.Problems.Count > 0);

        Assert.That(_log.Problems.Single(), Does.StartWith("Could not stop the recording as another window asked"));
    }

    /// <summary>Only a run id as ClassScribe makes them names the event: nothing else is made, opened or set.</summary>
    [TestCase(@"..\..\BaseNamedObjects\x")]
    [TestCase("20261015-230002Z-1F3A9C07")]
    [TestCase("")]
    public void Only_a_run_id_as_ClassScribe_makes_them_names_the_event(string runId)
    {
        Assert.Throws<ArgumentException>(() => StopSignal.NameFor(runId));
        Assert.Throws<ArgumentException>(() => StopSignal.Listen(runId, () => { }, _log));
        Assert.Throws<ArgumentException>(() => StopSignal.TrySet(runId, _log));
    }

    /// <summary>
    /// The yield event of a video's run (design 2.5) is named after the run in this session too, beside its stop event: a
    /// class asks a video's recording to give way through it.
    /// </summary>
    [Test]
    public void The_yield_event_is_named_after_the_run_beside_its_stop_event()
    {
        Assert.That(StopSignal.YieldNameFor(_runId), Is.EqualTo($@"Local\ClassScribe.Yield.{_runId}"));
        Assert.Throws<ArgumentException>(() => StopSignal.YieldNameFor("not-a-run"));
    }

    /// <summary>A yield asked for reaches the video's run listening for it, and says why, at Information.</summary>
    [Test]
    public void A_yield_asked_for_reaches_the_run_listening_for_it()
    {
        using var heard = new ManualResetEventSlim();
        using var signal = StopSignal.ListenForYield(_runId, heard.Set, _log);

        Assert.That(StopSignal.TrySetYield(_runId, _log), Is.True);
        Assert.That(heard.Wait(TimeSpan.FromSeconds(5)), Is.True);
        Assert.That(_log.Messages, Has.Some.EqualTo("A class is starting, so this video's recording gives way to it"));
        Assert.That(_log.Problems, Is.Empty);
    }

    /// <summary>The yield and the stop are two events: either set leaves the other's listener unheard.</summary>
    [Test]
    public async Task The_yield_and_the_stop_are_two_events()
    {
        var stops = 0;
        var yields = 0;
        using var stop = StopSignal.Listen(_runId, () => Interlocked.Increment(ref stops), _log);
        using var yielded = StopSignal.ListenForYield(_runId, () => Interlocked.Increment(ref yields), _log);

        StopSignal.TrySetYield(_runId, _log);
        await Wait.UntilAsync(() => Volatile.Read(ref yields) == 1);
        await Task.Delay(100);
        Assert.That(Volatile.Read(ref stops), Is.Zero);

        StopSignal.TrySet(_runId, _log);
        await Wait.UntilAsync(() => Volatile.Read(ref stops) == 1);
        Assert.That(Volatile.Read(ref yields), Is.EqualTo(1));
    }

    /// <summary>A yield asked of a run nobody listens for reaches nobody: a class's run, or a video's that has ended.</summary>
    [Test]
    public void A_yield_asked_of_a_run_nobody_listens_for_reaches_nobody()
    {
        Assert.That(StopSignal.TrySetYield(_runId, _log), Is.False);
    }

    /// <summary>The real yield of the app goes through the same events: what it listens for, it hears when it is asked.</summary>
    [Test]
    public void The_apps_yield_listens_and_asks_through_the_events()
    {
        var yield = new WindowsVideoYield(new RecordingLogger<WindowsVideoYield>());
        using var heard = new ManualResetEventSlim();

        using (yield.Listen(_runId, heard.Set))
        {
            Assert.That(yield.Ask(_runId), Is.True);
            Assert.That(heard.Wait(TimeSpan.FromSeconds(5)), Is.True);
        }

        Assert.That(yield.Ask(RunIds.New(DateTimeOffset.UtcNow)), Is.False, "no run listens for a fresh id");
    }
}
