using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using ClassScribe.Desktop;
using ClassScribe.Desktop.Windows;
using Microsoft.Extensions.Logging;

namespace ClassScribe.Tests;

/// <summary>
/// ClassScribe's window for the end of the session, on Windows. Each test makes a real hidden window on a thread of its
/// own and sends it the messages Windows sends at a restart, a shutdown or a sign-out, to that one window of the test's
/// own process and to no other: nothing is broadcast, nothing is shown, no setting is changed, and no session ends. A
/// reason that makes Windows ask first is held only during the test and let go when the window is disposed. One test fills
/// the window's own queue with empty messages, to see what happens when a message cannot be posted to it.
/// </summary>
[TestFixture, Platform("Win"), Category("WindowsProcess")]
[SupportedOSPlatform("windows")]
public class SessionWindowTests
{
    /// <summary>WM_NULL: a message that does nothing.</summary>
    private const uint NullMessage = 0x0000;

    /// <summary>WM_CLOSE: a request to close a window, as taskkill without /F sends one.</summary>
    private const uint CloseRequest = 0x0010;

    /// <summary>WM_QUERYENDSESSION: Windows asks whether it may end the session.</summary>
    private const uint QueryEndSession = 0x0011;

    /// <summary>WM_ENDSESSION: Windows says whether the session ends.</summary>
    private const uint EndSession = 0x0016;

    /// <summary>ENDSESSION_CLOSEAPP: Windows closes programs for an update or a repair.</summary>
    private const long CloseApp = 0x1;

    /// <summary>ENDSESSION_CRITICAL: an end Windows forces.</summary>
    private const long Critical = 0x40000000;

    /// <summary>ENDSESSION_LOGOFF: the user signs out.</summary>
    private const long Logoff = 0x80000000;

    /// <summary>The reason a recording holds Windows with.</summary>
    private const string Reason = "ClassScribe is recording a class";

    /// <summary>The reason an upload holds Windows with, after the recording's.</summary>
    private const string Transcribing = "ClassScribe is transcribing a recording";

    /// <summary>How long a test waits for the window's answer before it fails instead of hanging.</summary>
    private const uint AnswerTimeoutMs = 10_000;

    /// <summary>Windows' error code for "Access is denied".</summary>
    private const int AccessDenied = 5;

    /// <summary>What is logged when Windows refuses the reason with <see cref="AccessDenied"/>.</summary>
    private static readonly string RefusedReason =
        $"Windows did not accept the request to ask first before ending the session ({WindowsWords(AccessDenied)}, error {AccessDenied}); " +
        "a restart or a shutdown will not wait for ClassScribe";

    private LevelLogger _log = null!;
    private SessionWindow _window = null!;

    /// <summary>Makes the window under test, with a log to inspect.</summary>
    [SetUp]
    public void SetUp()
    {
        _log = new LevelLogger();
        _window = new SessionWindow(_log);
    }

    /// <summary>Closes the window, letting go of any reason still held.</summary>
    [TearDown]
    public void TearDown() => _window.Dispose();

    /// <summary>
    /// The window is a top-level window, so that Windows' session messages reach it (a message-only window gets none);
    /// it is never shown; it belongs to this process, on a thread of its own; and it is titled ClassScribe, the name
    /// Windows' shutdown screen gives the program that holds it.
    /// </summary>
    [Test]
    public void The_window_is_hidden_top_level_titled_ClassScribe_and_on_a_thread_of_its_own()
    {
        var window = _window.Handle;

        Assert.That(Native.IsWindow(window), Is.True);
        Assert.That(Native.IsWindowVisible(window), Is.False, "never shown");
        Assert.That(Native.GetAncestor(window, Native.GaParent), Is.EqualTo(Native.GetDesktopWindow()), "top-level, not message-only");
        Assert.That(TitleOf(window), Is.EqualTo("ClassScribe"));
        var thread = Native.GetWindowThreadProcessId(window, out var process);
        Assert.That(process, Is.EqualTo((uint)Environment.ProcessId));
        Assert.That(thread, Is.Not.EqualTo(Native.GetCurrentThreadId()), "a thread of its own, not the caller's");
        Assert.That(_log.Problems, Is.Empty);
    }

    /// <summary>
    /// The window's thread is a background thread, so that a window nobody disposed never keeps ClassScribe running, as it
    /// would otherwise keep a recorder whose run is over.
    /// </summary>
    [Test]
    public void The_window_thread_never_keeps_ClassScribe_running() =>
        Assert.That(_window.ThreadIsBackground, Is.True);

    /// <summary>
    /// Windows asking whether it may end the session, for a restart or a shutdown, a sign-out or programs being closed,
    /// raises Ending once, on the window's own thread, saying which of these it is and that it is not forced, and is
    /// answered yes, since nothing holds Windows. The session has not ended yet, so Ended is not raised.
    /// </summary>
    [TestCase(0L, SessionEndKind.RestartOrShutdown)]
    [TestCase(Logoff, SessionEndKind.SignOut)]
    [TestCase(CloseApp, SessionEndKind.ClosingPrograms)]
    public void Windows_asking_to_end_the_session_raises_Ending_on_the_window_thread_and_is_answered_yes(long flags, SessionEndKind kind)
    {
        var raised = new List<(uint Thread, SessionEndKind Kind, bool Forced)>();
        var ended = 0;
        _window.Ending += (_, end) => raised.Add((Native.GetCurrentThreadId(), end.Kind, end.Forced));
        _window.Ended += (_, _) => ended++;

        var answer = Send(QueryEndSession, 0, flags);

        Assert.That(answer, Is.EqualTo(1), "yes");
        Assert.That(raised, Is.EqualTo(new[] { (WindowThread(), kind, false) }));
        Assert.That(ended, Is.Zero);
        Assert.That(_log.Problems, Is.Empty);
    }

    /// <summary>
    /// The session ending, for whatever reason, raises Ended once, on the window's own thread, saying what kind of end it
    /// is and that it is not forced, and nothing else.
    /// </summary>
    [TestCase(0L, SessionEndKind.RestartOrShutdown)]
    [TestCase(Logoff, SessionEndKind.SignOut)]
    [TestCase(CloseApp, SessionEndKind.ClosingPrograms)]
    public void The_session_ending_raises_Ended_on_the_window_thread(long flags, SessionEndKind kind)
    {
        var raised = new List<(uint Thread, SessionEndKind Kind, bool Forced)>();
        var others = 0;
        _window.Ended += (_, end) => raised.Add((Native.GetCurrentThreadId(), end.Kind, end.Forced));
        _window.Ending += (_, _) => others++;
        _window.EndingCancelled += (_, _) => others++;

        Send(EndSession, 1, flags);

        Assert.That(raised, Is.EqualTo(new[] { (WindowThread(), kind, false) }));
        Assert.That(others, Is.Zero);
    }

    /// <summary>When Windows does not end the session after all, EndingCancelled is raised, and Ended is not: the recording goes on.</summary>
    [Test]
    public void When_Windows_does_not_end_the_session_after_all_EndingCancelled_is_raised()
    {
        var cancelled = 0;
        var ended = 0;
        _window.EndingCancelled += (_, _) => cancelled++;
        _window.Ended += (_, _) => ended++;

        Send(QueryEndSession, 0, 0);
        Send(EndSession, 0, 0);

        Assert.That(cancelled, Is.EqualTo(1));
        Assert.That(ended, Is.Zero);
    }

    /// <summary>
    /// An end Windows forces raises Ended whatever Windows' message says, since the session ends regardless, and says that
    /// the end is forced and what kind of end it is.
    /// </summary>
    [TestCase(Critical, SessionEndKind.RestartOrShutdown)]
    [TestCase(Logoff | Critical, SessionEndKind.SignOut)]
    public void An_end_Windows_forces_raises_Ended_whatever_the_message_says(long flags, SessionEndKind kind)
    {
        var ended = new List<(SessionEndKind Kind, bool Forced)>();
        var cancelled = 0;
        _window.Ended += (_, end) => ended.Add((end.Kind, end.Forced));
        _window.EndingCancelled += (_, _) => cancelled++;

        Send(EndSession, 0, flags);

        Assert.That(ended, Is.EqualTo(new[] { (kind, true) }));
        Assert.That(cancelled, Is.Zero);
    }

    /// <summary>
    /// The reason is handed to Windows as soon as it is asked for, before Windows asks anything, and let go as soon as
    /// that is asked for: Microsoft documents that when another program holds a restart first, Windows' screen lists a
    /// program only if its reason was registered before Windows asked that program.
    /// </summary>
    [Test]
    public async Task The_reason_is_handed_to_Windows_at_once_not_only_when_Windows_asks()
    {
        var reasons = new FakeShutdownReasonApi();
        UseWindowWith(reasons);

        _window.BlockShutdown(Reason);
        await Wait.UntilAsync(() => reasons.Held == Reason);
        _window.BlockShutdown(null);
        await Wait.UntilAsync(() => reasons.Held is null);

        Assert.That(reasons.Calls, Is.EqualTo(new[] { $"Create {Reason}", "Destroy" }));
        Assert.That(_log.Problems, Is.Empty);
    }

    /// <summary>
    /// When the reason cannot be handed to the window's thread at once, here because that thread is busy in a handler and
    /// its queue is full, that is a warning: Windows is told when it asks, so the reason still counts for the answer, but
    /// until then Windows' screen may leave ClassScribe out when another program holds a restart first.
    /// </summary>
    [Test]
    public async Task A_reason_that_cannot_be_handed_over_at_once_is_a_warning_and_still_counts_when_Windows_asks()
    {
        var reasons = new FakeShutdownReasonApi();
        UseWindowWith(reasons);
        using var inHandler = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        _window.Ending += (_, _) =>
        {
            inHandler.Set();
            release.Wait(TimeSpan.FromSeconds(10));
        };

        var asking = Task.Run(() => Send(QueryEndSession, 0, 0));
        try
        {
            Assert.That(inHandler.Wait(TimeSpan.FromSeconds(10)), Is.True, "the window's thread is busy in the handler");
            Assume.That(FillQueue(), Is.True, "Windows took every message the test posted, so the queue never filled");
            _window.BlockShutdown(Reason);
        }
        finally
        {
            release.Set();
        }

        var answer = await asking;
        var held = reasons.Held;
        var problems = _log.Problems;
        var warnings = _log.At(LogLevel.Warning);
        await WaitForTheQueueToEmpty(reasons);

        Assert.That(answer, Is.Zero, "no: the reason was handed to Windows before it was answered");
        Assert.That(held, Is.EqualTo(Reason));
        Assert.That(problems, Is.EqualTo(warnings), "a warning, and nothing worse");
        Assert.That(warnings, Has.Count.EqualTo(1));
        Assert.That(warnings[0], Does.StartWith("Could not tell Windows at once why ClassScribe holds a restart or a shutdown ("));
        Assert.That(warnings[0], Does.EndWith(
            "). Windows is told when it asks, but if another program holds a restart first, Windows' screen may leave ClassScribe out"));
    }

    /// <summary>
    /// While a reason is held, Windows holds the reason for the window, which it shows on its shutdown screen, and its
    /// question is answered no, so that it asks the user first. Ending is raised all the same, so the files are brought
    /// up to date in case the user goes on. The reason is set from the test's thread, which Windows would refuse: it is
    /// set on the window's own thread.
    /// </summary>
    [Test]
    public void While_a_reason_is_held_Windows_holds_it_and_is_answered_no()
    {
        string? held = null;
        var ending = 0;
        _window.Ending += (_, _) =>
        {
            ending++;
            held = HeldReason(_window.Handle);
        };

        _window.BlockShutdown(Reason);
        var answer = Send(QueryEndSession, 0, 0);

        Assert.That(answer, Is.Zero, "no: Windows asks the user first");
        Assert.That(held, Is.EqualTo(Reason));
        Assert.That(ending, Is.EqualTo(1));
        Assert.That(_log.Problems, Is.Empty);
    }

    /// <summary>
    /// An end Windows forces is not held: the answer is yes even while a reason is held. Ending is raised all the same,
    /// saying that the end is forced, so that its handlers know Windows gives them about a second.
    /// </summary>
    [TestCase(Critical, SessionEndKind.RestartOrShutdown)]
    [TestCase(Logoff | Critical, SessionEndKind.SignOut)]
    public void An_end_Windows_forces_is_not_held(long flags, SessionEndKind kind)
    {
        var ending = new List<(SessionEndKind Kind, bool Forced)>();
        _window.Ending += (_, end) => ending.Add((end.Kind, end.Forced));
        _window.BlockShutdown(Reason);

        var answer = Send(QueryEndSession, 0, flags);

        Assert.That(answer, Is.EqualTo(1));
        Assert.That(ending, Is.EqualTo(new[] { (kind, true) }));
    }

    /// <summary>Letting the reason go removes it from Windows, and Windows' next question is answered yes.</summary>
    [Test]
    public void Letting_the_reason_go_removes_it_and_Windows_is_answered_yes()
    {
        string? held = "not read";
        _window.Ending += (_, _) => held = HeldReason(_window.Handle);
        _window.BlockShutdown(Reason);
        Assert.That(Send(QueryEndSession, 0, 0), Is.Zero, "held first");

        _window.BlockShutdown(null);
        var answer = Send(QueryEndSession, 0, 0);

        Assert.That(answer, Is.EqualTo(1));
        Assert.That(held, Is.Null.Or.Empty);
    }

    /// <summary>A new reason takes the place of the one held, as when the recording has stopped and its upload begins.</summary>
    [Test]
    public void A_new_reason_takes_the_place_of_the_one_held()
    {
        string? held = null;
        _window.Ending += (_, _) => held = HeldReason(_window.Handle);

        _window.BlockShutdown(Reason);
        _window.BlockShutdown(Transcribing);
        var answer = Send(QueryEndSession, 0, 0);

        Assert.That(answer, Is.Zero);
        Assert.That(held, Is.EqualTo(Transcribing));
    }

    /// <summary>
    /// When Windows refuses the reason, that is logged once, and Windows' question is answered yes all the same: Microsoft
    /// documents that a program without a visible window that answers no without a reason Windows holds is ended. The
    /// reason is tried again, and once Windows takes it, the answer is no.
    /// </summary>
    [Test]
    public void When_Windows_refuses_the_reason_it_is_logged_once_and_Windows_is_answered_yes()
    {
        var reasons = new FakeShutdownReasonApi();
        reasons.RefuseToCreate(AccessDenied);
        UseWindowWith(reasons);

        _window.BlockShutdown(Reason);
        var first = Send(QueryEndSession, 0, 0);
        var second = Send(QueryEndSession, 0, 0);
        reasons.RefuseToCreate(0);
        var third = Send(QueryEndSession, 0, 0);

        Assert.That(first, Is.EqualTo(1), "yes, since Windows holds no reason");
        Assert.That(second, Is.EqualTo(1));
        Assert.That(third, Is.Zero, "no, once Windows took the reason");
        Assert.That(reasons.Calls.Count(call => call == $"Refused {Reason}"), Is.GreaterThanOrEqualTo(2), "tried again");
        Assert.That(reasons.Held, Is.EqualTo(Reason));
        Assert.That(_log.Problems, Is.EqualTo(new[] { RefusedReason }));
    }

    /// <summary>
    /// When Windows refuses a new reason while it holds an earlier one, as when the upload begins, the earlier one is let
    /// go as well, and Windows' question is answered yes: the answer is no only while Windows holds the reason wanted now,
    /// and Microsoft does not say what Windows still holds after it refused a new one.
    /// </summary>
    [Test]
    public void When_Windows_refuses_a_new_reason_the_one_held_is_let_go_and_Windows_is_answered_yes()
    {
        var reasons = new FakeShutdownReasonApi();
        UseWindowWith(reasons);
        _window.BlockShutdown(Reason);
        Assert.That(Send(QueryEndSession, 0, 0), Is.Zero, "held first");

        reasons.RefuseToCreate(AccessDenied);
        _window.BlockShutdown(Transcribing);
        var answer = Send(QueryEndSession, 0, 0);

        Assert.That(answer, Is.EqualTo(1));
        Assert.That(reasons.Held, Is.Null, "the earlier reason was let go");
        Assert.That(reasons.Calls, Does.Contain("Destroy"));
        Assert.That(_log.Problems, Is.EqualTo(new[] { RefusedReason }));
    }

    /// <summary>
    /// A reason Windows refused, let go and asked for again later, as for the next class in the same session, is tried
    /// again and its refusal reported again.
    /// </summary>
    [Test]
    public void A_refused_reason_asked_for_again_after_it_was_let_go_is_tried_and_reported_again()
    {
        var reasons = new FakeShutdownReasonApi();
        reasons.RefuseToCreate(AccessDenied);
        UseWindowWith(reasons);

        _window.BlockShutdown(Reason);
        Send(QueryEndSession, 0, 0);
        _window.BlockShutdown(null);
        Send(QueryEndSession, 0, 0);
        _window.BlockShutdown(Reason);
        var answer = Send(QueryEndSession, 0, 0);

        Assert.That(answer, Is.EqualTo(1));
        Assert.That(_log.Problems, Is.EqualTo(new[] { RefusedReason, RefusedReason }));
    }

    /// <summary>
    /// The reason is set and removed on the window's own thread, the only one Windows takes it from, whichever thread asks;
    /// disposing the window lets go of a reason still held.
    /// </summary>
    [Test]
    public void The_reason_is_set_and_removed_on_the_window_thread_and_disposing_lets_it_go()
    {
        var reasons = new FakeShutdownReasonApi();
        UseWindowWith(reasons);
        var windowThread = 0;
        _window.Ending += (_, _) => windowThread = Environment.CurrentManagedThreadId;

        _window.BlockShutdown(Reason);
        Send(QueryEndSession, 0, 0);
        _window.Dispose();

        Assert.That(reasons.Calls, Is.EqualTo(new[] { $"Create {Reason}", "Destroy" }));
        Assert.That(reasons.Threads, Is.EqualTo(new[] { windowThread, windowThread }));
        Assert.That(windowThread, Is.Not.EqualTo(Environment.CurrentManagedThreadId));
        Assert.That(reasons.Held, Is.Null);
    }

    /// <summary>
    /// Asking Windows to wait never waits for the window's thread: while that thread is busy in a handler, a reason is
    /// set at once from another thread, and it counts for the answer Windows is about to get.
    /// </summary>
    [Test]
    public async Task Holding_Windows_never_waits_for_the_window_thread()
    {
        using var inHandler = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        _window.Ending += (_, _) =>
        {
            inHandler.Set();
            release.Wait(TimeSpan.FromSeconds(10));
        };

        var asking = Task.Run(() => Send(QueryEndSession, 0, 0));
        Assert.That(inHandler.Wait(TimeSpan.FromSeconds(10)), Is.True, "the window's thread is busy in the handler");
        var holding = Task.Run(() => _window.BlockShutdown(Reason));
        var returned = holding.Wait(TimeSpan.FromSeconds(5));
        release.Set();

        Assert.That(returned, Is.True, "returned while the window's thread was still busy");
        Assert.That(await asking, Is.Zero, "the reason set meanwhile counts for the answer");
    }

    /// <summary>
    /// A handler that fails is logged, the handlers after it still run, and Windows still gets its answer: an exception
    /// cannot go back to Windows from the window, where it would end the process and the recording with it.
    /// </summary>
    [Test]
    public void A_handler_that_fails_is_logged_and_the_others_still_run()
    {
        var after = new List<string>();
        _window.Ending += (_, _) => throw new InvalidOperationException("The files could not be brought up to date.");
        _window.Ending += (_, _) => after.Add("Ending");
        _window.Ended += (_, _) => throw new InvalidOperationException("The files could not be closed.");
        _window.Ended += (_, _) => after.Add("Ended");

        var answer = Send(QueryEndSession, 0, 0);
        Send(EndSession, 1, 0);

        Assert.That(answer, Is.EqualTo(1));
        Assert.That(after, Is.EqualTo(new[] { "Ending", "Ended" }));
        Assert.That(_log.Problems, Is.EqualTo(new[]
        {
            "Could not finish what ClassScribe does when Windows asks whether it may end the session; the rest still ran",
            "Could not finish what ClassScribe does when the session ends; the rest still ran"
        }));
    }

    /// <summary>
    /// A failure while the window handles a message, here Windows' functions for the reason throwing, is logged and goes
    /// no further: an exception cannot go back to Windows from the window, where it would end the process and the
    /// recording with it. The window goes on, and Windows' question is answered yes, since an answer of no without a
    /// reason Windows holds would get ClassScribe ended.
    /// </summary>
    [Test]
    public async Task A_failure_while_handling_a_message_is_logged_and_Windows_is_answered_yes()
    {
        var reasons = new FakeShutdownReasonApi();
        reasons.ThrowOnCreate(new InvalidOperationException("The reason could not be handed to Windows."));
        UseWindowWith(reasons);

        _window.BlockShutdown(Reason);
        await Wait.UntilAsync(() => reasons.Calls.Count == 1);
        var answer = Send(QueryEndSession, 0, 0);

        Assert.That(answer, Is.EqualTo(1));
        Assert.That(Native.IsWindow(_window.Handle), Is.True, "the window goes on");
        Assert.That(_log.At(LogLevel.Error), Is.EqualTo(new[] { CouldNotHandle(SessionWindow.ApplyMessage), CouldNotHandle(QueryEndSession) }));
        Assert.That(_log.Problems, Is.EqualTo(_log.At(LogLevel.Error)), "errors, and no warnings");
    }

    /// <summary>
    /// A request from outside to close the window, as taskkill without /F sends, leaves it open: only disposing it closes
    /// it, so that ClassScribe never stops hearing about the end of the session unawares.
    /// </summary>
    [Test]
    public void A_request_from_outside_to_close_the_window_leaves_it_open()
    {
        var ending = 0;
        _window.Ending += (_, _) => ending++;

        Send(CloseRequest, 0, 0);

        Assert.That(Native.IsWindow(_window.Handle), Is.True);
        Assert.That(Send(QueryEndSession, 0, 0), Is.EqualTo(1));
        Assert.That(ending, Is.EqualTo(1));
    }

    /// <summary>
    /// ClassScribe's own message to destroy the window, arriving although the window was not disposed, as from another
    /// program that posts or broadcasts that number, is ignored: the window stays, so does the reason it holds, and
    /// Windows' questions are still answered.
    /// </summary>
    [Test]
    public void Its_own_close_message_from_anywhere_but_Dispose_is_ignored()
    {
        var ending = 0;
        _window.Ending += (_, _) => ending++;
        _window.BlockShutdown(Reason);

        Send(SessionWindow.CloseMessage, 0, 0);

        Assert.That(Native.IsWindow(_window.Handle), Is.True, "still there");
        Assert.That(Send(QueryEndSession, 0, 0), Is.Zero, "the reason is still held, so Windows asks the user first");
        Assert.That(ending, Is.EqualTo(1));
        Assert.That(_log.Problems, Is.Empty);
    }

    /// <summary>
    /// Disposing lets go of the reason held, destroys the window and ends its thread before it returns. Disposing again,
    /// and asking Windows to wait afterwards, do nothing.
    /// </summary>
    [Test]
    public void Disposing_destroys_the_window_and_ends_its_thread()
    {
        var window = _window.Handle;
        _window.BlockShutdown(Reason);
        Assert.That(Native.IsWindow(window), Is.True, "made");
        Assert.That(_window.ThreadAlive, Is.True, "running");

        _window.Dispose();

        Assert.That(_window.ThreadAlive, Is.False);
        Assert.That(Native.IsWindow(window), Is.False);
        Assert.DoesNotThrow(_window.Dispose);
        Assert.DoesNotThrow(() => _window.BlockShutdown(Reason));
        Assert.That(_log.Problems, Is.Empty);
    }

    /// <summary>A handler may dispose the window from the window's own thread: it does not wait for itself, and the thread ends once the handler returns.</summary>
    [Test]
    public async Task A_handler_may_dispose_the_window_from_its_own_thread()
    {
        _window.Ended += (_, _) => _window.Dispose();

        Send(EndSession, 1, 0);

        await Wait.UntilAsync(() => !_window.ThreadAlive);
        Assert.That(_log.Problems, Is.Empty);
    }

    /// <summary>
    /// When Windows will not make the window, here because the name of its kind of window is longer than Windows takes,
    /// that is logged with what it means, and the rest works without it: asking Windows to wait and disposing do nothing.
    /// </summary>
    [Test]
    public void When_Windows_will_not_make_the_window_it_is_logged_and_nothing_fails()
    {
        var log = new LevelLogger();

        using var refused = new SessionWindow(log, className: new string('x', 300));

        Assert.That(refused.Handle, Is.EqualTo(IntPtr.Zero));
        Assert.That(refused.ThreadAlive, Is.False);
        Assert.DoesNotThrow(() => refused.BlockShutdown(Reason));
        Assert.That(log.Problems.Single(), Does.StartWith("Windows did not let ClassScribe watch for a restart, a shutdown or a sign-out ("));
        Assert.That(log.Problems.Single(), Does.EndWith("). Recording goes on, but Windows will not ask first before ending the session"));
    }

    /// <summary>
    /// Sends <paramref name="message"/> to the window under test and returns its answer. It is sent only to a window of
    /// this process, so that no other program ever gets it, and it fails rather than hangs when no answer comes.
    /// </summary>
    private int Send(uint message, long wParam, long lParam)
    {
        var window = OwnWindow();

        var sent = Native.SendMessageTimeoutW(window, message, (IntPtr)wParam, (IntPtr)lParam, Native.SmtoNormal, AnswerTimeoutMs, out var answer);

        Assert.That(sent, Is.Not.EqualTo(IntPtr.Zero), $"the window answered message 0x{message:X4} in time");
        return (int)answer;
    }

    /// <summary>
    /// Posts empty messages (WM_NULL) to the window under test, a window of this process, until Windows refuses one
    /// because the queue of the window's thread is full, which takes about 10,000 while that thread is busy. False when
    /// Windows took every one.
    /// </summary>
    private bool FillQueue()
    {
        var window = OwnWindow();
        for (var posted = 0; posted < 100_000; posted++)
        {
            if (!Native.PostMessageW(window, NullMessage, IntPtr.Zero, IntPtr.Zero)) return true;
        }

        return false;
    }

    /// <summary>
    /// Waits until the window's thread has worked through a queue the test filled, so that disposing the window can post
    /// its own message: letting the reason go is asked for until a request gets through and is handled, which happens
    /// only after every message posted before it.
    /// </summary>
    private async Task WaitForTheQueueToEmpty(FakeShutdownReasonApi reasons) =>
        await Wait.UntilAsync(() =>
        {
            _window.BlockShutdown(null);
            return reasons.Held is null;
        });

    /// <summary>The window under test, after checking that it exists and belongs to this process, so that no other program is ever sent anything.</summary>
    private IntPtr OwnWindow()
    {
        var window = _window.Handle;
        Native.GetWindowThreadProcessId(window, out var process);
        Assert.That(window, Is.Not.EqualTo(IntPtr.Zero), "a window to send to");
        Assert.That(process, Is.EqualTo((uint)Environment.ProcessId), "only to a window of the test's own process");
        return window;
    }

    /// <summary>The thread the window under test belongs to, as Windows knows it.</summary>
    private uint WindowThread() => Native.GetWindowThreadProcessId(_window.Handle, out _);

    /// <summary>Puts a window whose reason goes to <paramref name="reasons"/>, instead of Windows, in place of the one made for the test.</summary>
    private void UseWindowWith(FakeShutdownReasonApi reasons)
    {
        _window.Dispose();
        _window = new SessionWindow(_log, reasons);
    }

    /// <summary>What is logged when the window could not handle <paramref name="message"/>.</summary>
    private static string CouldNotHandle(uint message) =>
        $"The window that watches for a restart, a shutdown or a sign-out could not handle message 0x{message:X4}";

    /// <summary>The words Windows has for an error code, as the log gives them.</summary>
    private static string WindowsWords(int error) => new System.ComponentModel.Win32Exception(error).Message.TrimEnd('.');

    /// <summary>The window's title.</summary>
    private static string TitleOf(IntPtr window)
    {
        var title = new StringBuilder(256);
        Native.GetWindowTextW(window, title, title.Capacity);
        return title.ToString();
    }

    /// <summary>
    /// The reason Windows holds for <paramref name="window"/>, or null when it holds none. Windows answers this only on
    /// the window's own thread, so call it from a handler.
    /// </summary>
    private static string? HeldReason(IntPtr window)
    {
        uint length = 0;
        if (!Native.ShutdownBlockReasonQuery(window, null, ref length)) return null;

        var reason = new StringBuilder((int)length + 1);
        length = (uint)reason.Capacity;
        return Native.ShutdownBlockReasonQuery(window, reason, ref length) ? reason.ToString() : null;
    }

    /// <summary>
    /// A log that keeps each message with its level, because the level says how Gulzar sees it: a warning or an error
    /// shows in the window, a debug line does not.
    /// </summary>
    private sealed class LevelLogger : ILogger<SessionWindow>
    {
        private readonly ConcurrentQueue<(LogLevel Level, string Message)> _entries = new();

        /// <summary>The messages logged as warnings or errors, in order.</summary>
        public IReadOnlyList<string> Problems => [.. _entries.Where(entry => entry.Level >= LogLevel.Warning).Select(entry => entry.Message)];

        /// <summary>The messages logged at <paramref name="level"/>, in order.</summary>
        public IReadOnlyList<string> At(LogLevel level) => [.. _entries.Where(entry => entry.Level == level).Select(entry => entry.Message)];

        /// <inheritdoc />
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        /// <inheritdoc />
        public bool IsEnabled(LogLevel logLevel) => true;

        /// <inheritdoc />
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            _entries.Enqueue((logLevel, formatter(state, exception)));
    }

    /// <summary>Windows' functions the tests use to look at the window from outside and to send it messages.</summary>
    private static class Native
    {
        /// <summary>GA_PARENT: the window's parent, not its owner; the desktop for a top-level window.</summary>
        public const uint GaParent = 1;

        /// <summary>SMTO_NORMAL: the sender waits for the answer, up to the time given.</summary>
        public const uint SmtoNormal = 0;

        /// <summary>Sends a message to one window and waits, up to <paramref name="timeoutMs"/>, for its answer.</summary>
        [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
        public static extern IntPtr SendMessageTimeoutW(IntPtr window, uint message, IntPtr wParam, IntPtr lParam, uint flags, uint timeoutMs, out IntPtr result);

        /// <summary>Puts a message in the queue of the window's thread without waiting; false when Windows refuses, as when the queue is full.</summary>
        [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool PostMessageW(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

        /// <summary>True while the window exists.</summary>
        [DllImport("user32.dll", ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool IsWindow(IntPtr window);

        /// <summary>True while the window is shown.</summary>
        [DllImport("user32.dll", ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool IsWindowVisible(IntPtr window);

        /// <summary>The window's ancestor of the given kind.</summary>
        [DllImport("user32.dll", ExactSpelling = true)]
        public static extern IntPtr GetAncestor(IntPtr window, uint flags);

        /// <summary>The desktop window, the parent of every top-level window.</summary>
        [DllImport("user32.dll", ExactSpelling = true)]
        public static extern IntPtr GetDesktopWindow();

        /// <summary>Copies the window's title into <paramref name="text"/>.</summary>
        [DllImport("user32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
        public static extern int GetWindowTextW(IntPtr window, StringBuilder text, int maxCount);

        /// <summary>The thread that made the window, and its process.</summary>
        [DllImport("user32.dll", ExactSpelling = true)]
        public static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

        /// <summary>The reason Windows holds for the window; only on the window's own thread.</summary>
        [DllImport("user32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool ShutdownBlockReasonQuery(IntPtr window, StringBuilder? reason, ref uint length);

        /// <summary>The calling thread, as Windows knows it.</summary>
        [DllImport("kernel32.dll", ExactSpelling = true)]
        public static extern uint GetCurrentThreadId();
    }
}
