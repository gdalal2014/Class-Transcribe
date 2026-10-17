using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Threading.Channels;
using ClassScribe.Desktop;
using ClassScribe.Link;
using ClassScribe.Recorder;
using ClassScribe.Settings;
using ClassScribe.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClassScribe.Commands;

/// <summary>
/// Shows, in this window, a run that records in a separate recorder (design 2.8). It connects to the run's recorder and
/// writes a heading that names the run, when it started and how far it is; then what the recorder has logged so far, and
/// each new line as it comes, never one twice and none left out, even across a reconnect. It passes the recording keys on:
/// Q, P, M and V as they are pressed, and Ctrl+C as the console raises it, whether or not a key is being read, so that a
/// window whose input is redirected, as a script's is, can cancel too. Esc says bye and leaves; it never stops or pauses
/// anything. Every state the run publishes sets the window's title and taskbar button, and flashes the window for each new
/// Alert, as for a run recording in the window (see <see cref="WindowStatusSink"/>). At the end it shows how the run ended
/// and, in the one window the recorder tells to, opens the results as the settings say.
/// </summary>
/// <remarks>
/// <para>
/// A window the recorder opened by itself (<see cref="ViewMode.Alert"/>) ignores every key until Enter, Esc and Ctrl+C
/// included, so that text typed into it by mistake cannot stop, pause or mark the class; a hint to press Enter is shown for
/// a burst of such keys, once every five seconds at most. The same holds at the questions asked before the run is shown,
/// which then say to press Enter first. It never opens the results.
/// </para>
/// <para>
/// When the recorder ends without the run's end, the view ends as <see cref="ViewEnd.RecorderLost"/>, with the title in
/// red, for the window to act on: to continue the recording in a new recorder, or to report the end. Ten seconds without a
/// state while the recorder's process runs, the window says the recorder is not answering, and offers W to keep waiting
/// and S to stop it through its stop event, which does not need the link. A pipe that does not answer at the start gets
/// the same offer, with Esc, which leaves the recorder running; either says that the audio so far is on disk only once
/// the run has begun to record. A recorder of another build that refuses the window is named by its exe, which can show
/// the run, and S stops it the same way.
/// </para>
/// <para>
/// Nothing it writes can make a sound: what comes from the recorder is written without its control characters, a BEL
/// among them, and the title and the taskbar never carry one (see <see cref="TitleText"/>). Each run is shown on a window
/// state of its own, so a view can show one run after another, as after a recording continued in a new recorder.
/// </para>
/// </remarks>
/// <param name="ui">The window's keyboard and screen.</param>
/// <param name="window">The window's title, taskbar button and flash.</param>
/// <param name="desktop">Opens the folder and the transcript at the end.</param>
/// <param name="liveness">Whether the recorder's process still runs, for the link.</param>
/// <param name="recording">Whether to open the folder and the transcript at the end.</param>
/// <param name="time">The clock for the link's waits, the hint and the times written.</param>
/// <param name="logger">Where the view notes how a showing goes; what Gulzar needs to read, it writes to the window itself.</param>
/// <param name="clientLogger">Where the link to the recorder notes how it fares.</param>
public sealed class RecorderView(IConsoleUi ui, IWindowSignals window, IDesktop desktop, IProcessLiveness liveness,
    IOptionsSnapshot<RecordingOptions> recording, TimeProvider time, ILogger<RecorderView> logger, ILogger<RecorderClient> clientLogger) : IRunViewer
{
    /// <summary>How long after the hint to press Enter a key pressed in a window opened by itself shows it again.</summary>
    public static readonly TimeSpan HintAgainAfter = TimeSpan.FromSeconds(5);

    /// <summary>What a window opened by itself says when a key is pressed in it before Enter.</summary>
    internal const string EnterHint = "Press Enter to control the recording from this window.";

    /// <summary>
    /// Opens a run's pipe, waiting at most the time given for a free instance (see <see cref="RecorderClient.OpenRealPipeAsync"/>).
    /// Tests put an in-memory connection to a recorder of theirs in its place.
    /// </summary>
    internal Func<string, TimeSpan, CancellationToken, Task<Stream>> OpenPipe { get; init; } = RecorderClient.OpenRealPipeAsync;

    /// <summary>
    /// Watches for Ctrl+C: calls the action given on each press until the result is disposed (see <see cref="ConsoleCtrlC.Watch"/>).
    /// Tests press Ctrl+C themselves through it.
    /// </summary>
    internal Func<Action, IDisposable> WatchCtrlC { get; init; } = ConsoleCtrlC.Watch;

    /// <summary>
    /// Asks the recorder of the run whose id it is given to stop, through the run's stop event, and answers whether a
    /// recorder listened; null, as outside the tests, to set the stop event in Windows (see <see cref="StopSignal.TrySet"/>).
    /// </summary>
    internal Func<string, bool>? AskToStop { get; init; }

    /// <summary>
    /// The lines that tell how a run ended: its message, and the command that tries again when the message does not hold it
    /// already. A run file of an earlier build without a message is told by how it ended.
    /// </summary>
    public static IReadOnlyList<string> OutcomeLines(RunOutcome outcome)
    {
        var message = string.IsNullOrWhiteSpace(outcome.Message) ? DescribeKind(outcome.Kind) : outcome.Message;
        return outcome.RetryCommand is { Length: > 0 } retry && !message.Contains(retry, StringComparison.Ordinal)
            ? [message, $"To try again, run: {retry}"]
            : [message];
    }

    /// <summary>
    /// Shows <paramref name="run"/>, which records in a separate recorder, as <paramref name="mode"/> says, until the run
    /// ends, Esc leaves it, or the recorder is lost, out of reach or of another build; the result says which, with the run's
    /// exit code and outcome when it ended. Throws an <see cref="ArgumentException"/> for a run that records in its window,
    /// which has no recorder to show it from, and an <see cref="OperationCanceledException"/> when
    /// <paramref name="cancellationToken"/> is cancelled.
    /// </summary>
    public async Task<ViewResult> ShowAsync(RunRecord run, ViewMode mode, CancellationToken cancellationToken)
    {
        if (run.Home != RunHome.Separate || !LinkProtocol.IsPipeName(run.Pipe))
            throw new ArgumentException($"Run {run.RunId} records in its window or names no pipe of its own, so there is no recorder to show it from.", nameof(run));

        var showing = new Showing(run, mode, new WindowStatusSink(window));
        await using var client = new RecorderClient(liveness, time, clientLogger) { OpenPipe = OpenPipe };
        logger.LogDebug("Showing run {RunId} in this window ({Mode})", run.RunId, mode);

        var (welcome, unreached) = await ConnectAsync(showing, client, cancellationToken);
        if (welcome is null) return unreached!;

        ShowHeading(showing, welcome);
        Publish(showing, welcome.State);

        // A window opened by itself comes minimized, without the focus: it flashes once so that its button is seen, unless
        // the state flashed it already.
        if (mode == ViewMode.Alert && !Flashes(welcome.State)) window.Flash();

        // A key pressed before the run was shown, as while the window connected, was not meant for it: P would pause the
        // class without anyone seeing why.
        ui.DiscardPendingKeys();

        // Ctrl+C is watched only once there is a link to pass it on: one pressed while connecting is not kept, to stop the
        // recording later with a press that seemed to do nothing.
        var presses = Channel.CreateUnbounded<bool>(new UnboundedChannelOptions { SingleReader = true });
        using (WatchCtrlC(() => presses.Writer.TryWrite(true)))
        {
            return await FollowAsync(showing, client, presses.Reader, cancellationToken);
        }
    }

    /// <summary>The state a window shows a run in when its recorder has published none to it: starting, at the position its file gives.</summary>
    internal static RecorderState StateOf(RunRecord run) =>
        new(RecorderPhase.Starting, run.Request.Name, run.Position, null, null, null, null, null, Attention.None, run.Home == RunHome.InWindow);

    /// <summary>The state a window shows a run in once it has ended with <paramref name="outcome"/>, from <paramref name="last"/>, the last state shown.</summary>
    internal static RecorderState EndedState(RecorderState last, RunOutcome outcome) =>
        last with { Phase = PhaseOf(outcome.Kind), UploadPercent = null };

    /// <summary>
    /// The state a window shows a run in once its recorder has ended without the run's end, from <paramref name="last"/>, the
    /// last state shown: an Alert, ENDED UNEXPECTEDLY, new, so that the window flashes for it.
    /// </summary>
    internal static RecorderState LostState(RecorderState last) =>
        last with { Attention = new Attention(AttentionLevel.Alert, "ENDED UNEXPECTEDLY", "The recorder ended unexpectedly.", TitleText.AttentionOf(last).Count + 1) };

    /// <summary>A key pressed without Ctrl or Alt: Shift is allowed, as for Q pressed with Caps Lock on.</summary>
    private static bool IsPlain(ConsoleKeyInfo key) => (key.Modifiers & (ConsoleModifiers.Control | ConsoleModifiers.Alt)) == 0;

    /// <summary>
    /// True when <paramref name="state"/>, shown first on a window, flashes it, as <see cref="WindowStatusSink"/> decides: an
    /// Alert that is counted, or a failure.
    /// </summary>
    private static bool Flashes(RecorderState state) =>
        state.Phase == RecorderPhase.Failed || TitleText.AttentionOf(state) is { Level: AttentionLevel.Alert, Count: > 0 };

    /// <summary>What a run ended with, in words, for a run file of an earlier build that gives no message.</summary>
    private static string DescribeKind(OutcomeKind kind) => kind switch
    {
        OutcomeKind.Transcribed => "The transcript is saved.",
        OutcomeKind.Recorded => "The recording is saved.",
        OutcomeKind.NothingRecorded => "No audio was captured. Was anything playing?",
        OutcomeKind.TranscriptFailed => "The transcript failed; the audio is kept.",
        OutcomeKind.Cancelled => "The transcription was cancelled; the audio is kept.",
        OutcomeKind.Refused => "The recording did not start: another recording was running.",
        _ => "The recorder failed; the audio so far is kept."
    };

    /// <summary>The phase the window shows a run in once it has ended with <paramref name="kind"/>.</summary>
    private static RecorderPhase PhaseOf(OutcomeKind kind) => kind switch
    {
        OutcomeKind.Transcribed or OutcomeKind.Recorded => RecorderPhase.Done,
        OutcomeKind.Cancelled => RecorderPhase.Cancelled,
        _ => RecorderPhase.Failed
    };

    /// <summary>What carries on when the window leaves a run in <paramref name="phase"/>: the recording or the transcription; null once it has ended.</summary>
    private static string? CarriesOn(RecorderPhase phase) => phase switch
    {
        RecorderPhase.Starting or RecorderPhase.Recording or RecorderPhase.Paused or RecorderPhase.NotRecording or RecorderPhase.Stopping => "recording",
        RecorderPhase.Preparing or RecorderPhase.Uploading or RecorderPhase.Transcribing => "transcription",
        _ => null
    };

    /// <summary>
    /// Connects to the run's recorder and returns its welcome; or, when the recorder cannot be reached, says why and
    /// returns how the showing ends instead: lost when its process has ended, refused when it is of another build, not
    /// answering when its pipe does not answer or it runs with other rights. W, after a pipe that did not answer, tries again.
    /// </summary>
    private async Task<(Welcome? Welcome, ViewResult? Unreached)> ConnectAsync(Showing showing, RecorderClient client, CancellationToken cancellationToken)
    {
        while (true)
        {
            RecorderLinkException failure;
            try
            {
                return (await client.ConnectAsync(showing.Run, showing.Mode, ui.IsInteractive, cancellationToken), null);
            }
            catch (RecorderLinkException ex)
            {
                // Deliberately not rethrown: a recorder that cannot be reached is what this view reports, in words, and its
                // result says why, for the window to act on; the recording itself is not affected by it.
                logger.LogDebug(ex, "Could not connect to the recorder of run {RunId} ({Failure})", showing.Run.RunId, ex.Failure);
                failure = ex;
            }

            switch (failure.Failure)
            {
                case LinkFailure.RecorderGone:
                    return (null, Lost(showing));

                case LinkFailure.Refused:
                    return (null, await RefusedAsync(showing, failure, cancellationToken));

                case LinkFailure.NoAnswer:
                    if (await WaitAgainAsync(showing, cancellationToken)) continue;

                    return (null, new ViewResult(ViewEnd.NotAnswering, null, null));

                default:
                    Say(failure.Message, LogLevel.Warning);
                    return (null, new ViewResult(ViewEnd.NotAnswering, null, null));
            }
        }
    }

    /// <summary>
    /// A recorder whose pipe does not answer while its process runs: says so and asks what to do. True for W, to wait and try
    /// again; false for S, which asks the recorder to stop through its stop event, for Esc, which leaves it running, and in a
    /// window nobody types into. Other keys do nothing.
    /// </summary>
    private async Task<bool> WaitAgainAsync(Showing showing, CancellationToken cancellationToken)
    {
        Say(NotAnswering(showing, atStart: true), LogLevel.Warning);
        if (!ui.IsInteractive) return false;

        // A key typed before the question appeared does not answer it.
        ui.DiscardPendingKeys();
        while (await ReadUnlockedKeyAsync(showing, cancellationToken) is { } key)
        {
            if (!IsPlain(key)) continue;

            switch (key.Key)
            {
                case ConsoleKey.W:
                    Say("Waiting for the recorder to answer.");
                    return true;

                case ConsoleKey.S:
                    Stop(showing);
                    return false;

                case ConsoleKey.Escape:
                    return false;
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        return false;
    }

    /// <summary>
    /// A recorder of another build refused the window: names the exe that can show the run and, where someone can type,
    /// offers S, which stops the recording through its stop event; any other key leaves it running. In a window opened by
    /// itself, the keys count only after Enter, and the question says so.
    /// </summary>
    private async Task<ViewResult> RefusedAsync(Showing showing, RecorderLinkException refusal, CancellationToken cancellationToken)
    {
        if (!ui.IsInteractive)
        {
            Say(refusal.Message, LogLevel.Warning);
            return new ViewResult(ViewEnd.Refused, null, null);
        }

        const string keys = "S stops the recording (the audio so far is kept); any other key leaves it running.";
        Say(showing.Locked ? $"{refusal.Message} Press Enter, then {keys}" : $"{refusal.Message} {keys}", LogLevel.Warning);

        // A key typed before the question appeared does not answer it.
        ui.DiscardPendingKeys();
        if (await ReadUnlockedKeyAsync(showing, cancellationToken) is { Key: ConsoleKey.S } key && IsPlain(key)) Stop(showing);

        cancellationToken.ThrowIfCancellationRequested();
        return new ViewResult(ViewEnd.Refused, null, null);
    }

    /// <summary>
    /// Reads keys until one counts: in a window opened by itself, Enter first lets the keys count, and any key before it
    /// shows the hint. Null once no more keys can be read, or the read is cancelled.
    /// </summary>
    private async Task<ConsoleKeyInfo?> ReadUnlockedKeyAsync(Showing showing, CancellationToken cancellationToken)
    {
        while (await ui.ReadKeyAsync(cancellationToken) is { } key)
        {
            if (!showing.Locked) return key;

            if (key.Key == ConsoleKey.Enter)
            {
                showing.Locked = false;
                Say("This window controls the recording now.");
            }
            else
            {
                Hint(showing);
            }
        }

        return null;
    }

    /// <summary>
    /// Shows the run as it goes, until it ends, Esc leaves it or the recorder is lost: what the recorder sends, the keys
    /// pressed, and Ctrl+C. Each read under way is ended and let finish before the showing ends.
    /// </summary>
    private async Task<ViewResult> FollowAsync(Showing showing, RecorderClient client, ChannelReader<bool> presses, CancellationToken cancellationToken)
    {
        using var leaving = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var messages = client.ReadAsync(leaving.Token).GetAsyncEnumerator(leaving.Token);
        Task<bool>? nextMessage = null;
        Task<ConsoleKeyInfo?>? nextKey = null;
        Task<bool>? nextPress = null;
        var keysLeft = ui.IsInteractive;
        try
        {
            while (true)
            {
                nextMessage ??= messages.MoveNextAsync().AsTask();
                if (keysLeft) nextKey ??= ui.ReadKeyAsync(leaving.Token);
                nextPress ??= presses.WaitToReadAsync(leaving.Token).AsTask();

                if (nextKey is null)
                    await Task.WhenAny(nextMessage, nextPress);
                else
                    await Task.WhenAny(nextMessage, nextKey, nextPress);

                if (nextMessage.IsCompleted)
                {
                    var more = await nextMessage;
                    nextMessage = null;

                    // The reading ends without a word only after bye or once the link is closed, which this view does as it
                    // leaves; should it end otherwise, the window has left the run all the same.
                    if (!more) return new ViewResult(ViewEnd.Left, null, null);
                    if (OnMessage(showing, messages.Current) is { } ended) return ended;
                }

                if (nextKey is { IsCompleted: true })
                {
                    var key = await nextKey;
                    nextKey = null;
                    if (key is { } pressed)
                    {
                        if (await OnKeyAsync(showing, client, pressed, leaving.Token) is { } ended) return ended;
                    }
                    else
                    {
                        // A read that answers without a key, unless the showing is being cancelled, means the keys have run
                        // out, as when their source has closed: reading again would only spin. Ctrl+C still comes.
                        cancellationToken.ThrowIfCancellationRequested();
                        keysLeft = false;
                        logger.LogDebug("No more keys can be read in this window; the run is still shown, and Ctrl+C still reaches it");
                    }
                }

                if (nextPress.IsCompleted)
                {
                    await nextPress;
                    nextPress = null;
                    while (presses.TryRead(out _)) await OnCtrlCAsync(showing, client, leaving.Token);
                }
            }
        }
        finally
        {
            await leaving.CancelAsync();
            foreach (Task? pending in new Task?[] { nextMessage, nextKey, nextPress })
                if (pending is not null) await pending.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

            await messages.DisposeAsync();
        }
    }

    /// <summary>Acts on what the recorder sent; returns how the showing ends when it does.</summary>
    private ViewResult? OnMessage(Showing showing, object message)
    {
        switch (message)
        {
            case LogLine line:
                ui.WriteLine(WindowText.Printable(line.Text));
                showing.LastSeq = Math.Max(showing.LastSeq, line.Seq);
                return null;

            case StateUpdate update:
                if (showing.Silent)
                {
                    showing.Silent = false;
                    Say("The recorder is answering again.");
                }

                Publish(showing, update.State);
                return null;

            case RecorderSilent:
                showing.Silent = true;
                Say(NotAnswering(showing, atStart: false), LogLevel.Warning);
                return null;

            case Reconnected reconnected:
                showing.Run = reconnected.Welcome.Run;
                showing.Silent = false;
                Say("Reconnected to the recording.");
                NoteLinesNoLongerHeld(showing, reconnected.Welcome.OldestSeq);
                Publish(showing, reconnected.Welcome.State);
                return null;

            case End end:
                return ShowEnd(showing, end);

            case LinkLost { Why: LinkLoss.RecorderGone }:
                return Lost(showing);

            case LinkLost:
                Say("Lost the connection to the recording; it carries on. Start ClassScribe again to see it.", LogLevel.Warning);
                ClearWindow();
                logger.LogDebug("The link to the recorder of run {RunId} could not be made again; it carries on", showing.Run.RunId);
                return new ViewResult(ViewEnd.NotAnswering, null, null);

            default:
                return null;
        }
    }

    /// <summary>
    /// Acts on a key pressed in the window; returns how the showing ends when it does. In a window opened by itself, every
    /// key but Enter only shows the hint until Enter has been pressed.
    /// </summary>
    private async Task<ViewResult?> OnKeyAsync(Showing showing, RecorderClient client, ConsoleKeyInfo key, CancellationToken cancellationToken)
    {
        if (showing.Locked)
        {
            if (key.Key == ConsoleKey.Enter)
            {
                showing.Locked = false;
                Say("This window controls the recording now: Q twice stops it, P pauses or resumes it, M marks the moment, and Esc leaves this view.");
            }
            else
            {
                Hint(showing);
            }

            return null;
        }

        if (key.Key == ConsoleKey.Escape)
        {
            await client.ByeAsync(ByeReasons.Esc);
            ClearWindow();
            logger.LogDebug("Left the view of run {RunId} with Esc; it carries on", showing.Run.RunId);
            return new ViewResult(ViewEnd.Left, null, null);
        }

        // A key held with Ctrl or Alt is a shortcut meant for another program. Ctrl+C, which the console also hands over as a
        // key while one is read, is passed on as the console raises it (see WatchCtrlC), so that it never counts twice.
        if (!IsPlain(key)) return null;

        switch (key.Key)
        {
            case ConsoleKey.Q:
                await PassOnAsync(client, LinkKeys.Q, "Q", cancellationToken);
                break;

            case ConsoleKey.P:
                await PassOnAsync(client, LinkKeys.P, "P", cancellationToken);
                break;

            case ConsoleKey.M:
                await PassOnAsync(client, LinkKeys.M, "M", cancellationToken);
                break;

            case ConsoleKey.V:
                await PassOnAsync(client, LinkKeys.V, "V", cancellationToken);
                break;

            case ConsoleKey.W when showing.Silent:
                Say("Waiting for the recorder to answer.");
                break;

            case ConsoleKey.S when showing.Silent:
                Stop(showing);
                break;
        }

        return null;
    }

    /// <summary>Passes a Ctrl+C on to the recorder; in a window opened by itself, before Enter, it only shows the hint.</summary>
    private async Task OnCtrlCAsync(Showing showing, RecorderClient client, CancellationToken cancellationToken)
    {
        if (showing.Locked)
        {
            Hint(showing);
            return;
        }

        await PassOnAsync(client, LinkKeys.CtrlC, "Ctrl+C", cancellationToken);
    }

    /// <summary>Passes the key named <paramref name="key"/> on to the recorder, and says so when it did not reach it, so that it can be pressed again.</summary>
    private async Task PassOnAsync(RecorderClient client, string key, string shown, CancellationToken cancellationToken)
    {
        if (await client.SendKeyAsync(key, cancellationToken)) return;

        Say($"{shown} did not reach the recording: the connection to it is being made again. Press it again in a moment.", LogLevel.Warning);
    }

    /// <summary>Asks the run's recorder to stop through its stop event, which does not need the link, and says how that went.</summary>
    private void Stop(Showing showing)
    {
        var runId = showing.Run.RunId;
        var asked = AskToStop?.Invoke(runId) ?? (OperatingSystem.IsWindows() && StopSignal.TrySet(runId, logger));
        if (asked)
        {
            logger.LogDebug("Asked the recorder of run {RunId} to stop through its stop event", runId);
            Say("Asked the recording to stop; its audio so far is kept.");
        }
        else
        {
            Say("Could not ask the recording to stop: no recorder listens for it, so it may have ended already.", LogLevel.Warning);
        }
    }

    /// <summary>Shows how the run ended, sets the title for it, and opens the results when this window is told to; returns the end, with the run's exit code.</summary>
    private ViewResult ShowEnd(Showing showing, End end)
    {
        var outcome = end.Outcome;
        Publish(showing, EndedState(showing.State ?? StateOf(showing.Run), outcome));

        var level = outcome.ExitCode == 0 ? LogLevel.Information : LogLevel.Warning;
        foreach (var line in OutcomeLines(outcome)) Say(line, level);

        // The recorder tells one window to open the results; a window it opened by itself never does, whatever it is told.
        if (end.Open && showing.Mode != ViewMode.Alert) OpenResults(outcome);

        logger.LogDebug("Run {RunId} ended ({Kind}, exit code {ExitCode})", showing.Run.RunId, outcome.Kind, outcome.ExitCode);
        return new ViewResult(ViewEnd.RunEnded, outcome.ExitCode, outcome);
    }

    /// <summary>
    /// Opens what the run left, as the settings say: a transcript's folder and the transcript itself, or the folder of a
    /// recording kept without a transcript. A run that failed has nothing to open.
    /// </summary>
    private void OpenResults(RunOutcome outcome)
    {
        var settings = recording.Value;
        if (outcome.TranscriptPath is { Length: > 0 } transcript)
        {
            if (settings.OpenFolderWhenDone && (Path.GetDirectoryName(transcript) ?? outcome.Folder) is { Length: > 0 } folder) desktop.OpenFolder(folder);
            if (settings.OpenTranscriptWhenDone) desktop.OpenFile(transcript);
        }
        else if (outcome.Kind == OutcomeKind.Recorded && outcome.Folder is { Length: > 0 } folder && settings.OpenFolderWhenDone)
        {
            desktop.OpenFolder(folder);
        }
    }

    /// <summary>The recorder ended without the run's end: the title says so in red, with a flash, and the showing ends as lost.</summary>
    private ViewResult Lost(Showing showing)
    {
        Publish(showing, LostState(showing.State ?? StateOf(showing.Run)));
        logger.LogDebug("The recorder of run {RunId} (process {ProcessId}) ended without the run's end", showing.Run.RunId, showing.Run.ProcessId);
        return new ViewResult(ViewEnd.RecorderLost, null, null);
    }

    /// <summary>
    /// The heading: in a window opened by itself, that keys wait for Enter, unless Enter has freed them already at a
    /// question; in one someone types into, that Esc leaves; a recorder of another build; then the run, when it started and
    /// how far it is; and lines of its log no longer held.
    /// </summary>
    private void ShowHeading(Showing showing, Welcome welcome)
    {
        showing.Run = welcome.Run;
        var phase = welcome.State.Phase;
        var name = welcome.Run.Request.Name;
        var started = WindowText.Local(time, welcome.Run.StartedUtc, "HH:mm");
        var position = Durations.Format(welcome.State.Position);

        if (showing.Locked)
            Write("ClassScribe opened this window by itself. Keys do nothing here until you press Enter.");
        else if (ui.IsInteractive && CarriesOn(phase) is { } carrying)
            Write($"Esc leaves this view; the {carrying} carries on in the background.");

        if (welcome.Build != LinkProtocol.Build)
            Write($"This recording was started by another build of ClassScribe ({welcome.ExePath}); it is shown here all the same.");

        Write(phase switch
        {
            RecorderPhase.Starting or RecorderPhase.Recording or RecorderPhase.Paused or RecorderPhase.NotRecording =>
                $"ClassScribe is recording \"{name}\" (started {started}; {position} so far). What happened so far:",
            RecorderPhase.Stopping => $"ClassScribe is stopping the recording \"{name}\" (started {started}; {position} recorded). What happened so far:",
            RecorderPhase.Preparing or RecorderPhase.Uploading or RecorderPhase.Transcribing =>
                $"ClassScribe is transcribing \"{name}\" (started {started}; {position} recorded). What happened so far:",
            _ => $"The run of \"{name}\" (started {started}) has ended. What happened:"
        });

        NoteLinesNoLongerHeld(showing, welcome.OldestSeq);
    }

    /// <summary>Says, when the recorder no longer holds lines the window has not shown, that they are not shown, and where the whole log is.</summary>
    private void NoteLinesNoLongerHeld(Showing showing, long oldestSeq)
    {
        if (oldestSeq <= showing.LastSeq + 1) return;

        var log = showing.Run is { Folder: { Length: > 0 } folder, BaseName: { Length: > 0 } baseName } ? new RecordingFiles(folder, baseName).Log : null;
        Say(log is null ? "Some lines of the log are no longer held here." : $"Some lines of the log are no longer held here; the whole log is in {log}.");
    }

    /// <summary>
    /// That the recorder is not answering, which process it is and when it started, that its audio so far is on disk once it
    /// has begun to record, and what can be done, as far as anyone can type here. <paramref name="atStart"/> is true for a
    /// recorder that has not answered since the window came: the window has not said yet that Esc leaves, so the line says
    /// it, and in a window nobody types into the view ends there. Later, the view waits for the recorder while the link
    /// stays, and says so where nobody can type.
    /// </summary>
    private string NotAnswering(Showing showing, bool atStart)
    {
        var run = showing.Run;
        var said = $"The recorder is not answering (process {run.ProcessId}, started {WindowText.Local(time, run.ProcessStartedUtc, "HH:mm")})";
        if (HasBegunRecording(showing)) said += ". Its audio so far is on disk";
        if (!ui.IsInteractive) return atStart ? $"{said}." : $"{said}; waiting for it.";

        var keys = atStart ? "W keeps waiting, S stops it, Esc leaves it running." : showing.Locked ? "W keeps waiting and S stops it." : "W keeps waiting, S stops it.";
        return showing.Locked ? $"{said}. Press Enter, then {keys}" : $"{said}. {keys}";
    }

    /// <summary>
    /// True once the run has begun to record, as its run file or the last state shown says: only then is some of its audio
    /// on disk. A recorder that stops answering while it starts may have recorded nothing.
    /// </summary>
    private static bool HasBegunRecording(Showing showing) =>
        showing.Run.State is RunState.Recording or RunState.Stopping or RunState.Transcribing || showing.State is { Phase: not RecorderPhase.Starting };

    /// <summary>Shows the hint to press Enter, unless it was shown less than <see cref="HintAgainAfter"/> ago, so that a burst of keys shows it once.</summary>
    private void Hint(Showing showing)
    {
        var now = time.GetTimestamp();
        if (showing.HintedAt is { } shown && time.GetElapsedTime(shown, now) < HintAgainAfter) return;

        showing.HintedAt = now;
        Say(EnterHint);
    }

    /// <summary>Shows <paramref name="state"/> on the window's title and taskbar, flashing it for a new Alert.</summary>
    private static void Publish(Showing showing, RecorderState state)
    {
        showing.State = state;
        showing.Signals.Publish(state);
    }

    /// <summary>The window no longer shows a run: its title is ClassScribe's name alone, and its taskbar button has no bar.</summary>
    private void ClearWindow()
    {
        window.SetTaskbar(TaskbarState.None);
        window.SetTitle(TitleText.AppName);
    }

    /// <summary>Writes a line of the window's own, without control characters.</summary>
    private void Write(string text) => ui.WriteLine(WindowText.Printable(text));

    /// <summary>Writes a line as the log shows its lines: the time, then the label for a warning or an error, then <paramref name="text"/>.</summary>
    private void Say(string text, LogLevel level = LogLevel.Information) => ui.WriteLine(WindowText.Line(time, level, text));

    /// <summary>One showing of a run: what the window knows of it, and what it has shown so far.</summary>
    /// <param name="run">The run.</param>
    /// <param name="mode">How the window shows it.</param>
    /// <param name="signals">Its title, taskbar and flash.</param>
    private sealed class Showing(RunRecord run, ViewMode mode, WindowStatusSink signals)
    {
        /// <summary>The run, as the recorder's welcome last gave it.</summary>
        public RunRecord Run { get; set; } = run;

        /// <summary>How the window shows it.</summary>
        public ViewMode Mode { get; } = mode;

        /// <summary>Its title, taskbar and flash, one for each showing, since its flashes count the run's Alerts.</summary>
        public WindowStatusSink Signals { get; } = signals;

        /// <summary>True in a window opened by itself until Enter is pressed: no key counts until then.</summary>
        public bool Locked { get; set; } = mode == ViewMode.Alert;

        /// <summary>When the hint to press Enter was last shown, on the steady clock; null before it was.</summary>
        public long? HintedAt { get; set; }

        /// <summary>True from the notice that the recorder is not answering until it answers: W and S count meanwhile.</summary>
        public bool Silent { get; set; }

        /// <summary>The run's state as last shown; null before the first.</summary>
        public RecorderState? State { get; set; }

        /// <summary>The number of the last line of the run's log shown; 0 before the first.</summary>
        public long LastSeq { get; set; }
    }
}

/// <summary>Ctrl+C in the console this process runs in, for the view of a run, which passes it on to the recorder.</summary>
internal static class ConsoleCtrlC
{
    /// <summary>
    /// Turns Ctrl+C back on in this process, as SetConsoleCtrlHandler with no handler and FALSE does, and returns 0 when
    /// Windows did, else its error. A process begins with Ctrl+C turned off when the process that started it had it off,
    /// and the recorder has it off as the root of a process group of its own: in the window it opens by itself, Ctrl+C
    /// would otherwise never reach <see cref="Console.CancelKeyPress"/>. Turned on, it does not end the process: ClassScribe's
    /// console takes it (see <see cref="ConsoleUi"/>), and the view passes it on. Nothing to do outside Windows.
    /// </summary>
    public static int TurnOn()
    {
        if (!OperatingSystem.IsWindows()) return 0;

        return SetConsoleCtrlHandler(IntPtr.Zero, add: false) ? 0 : Marshal.GetLastPInvokeError();
    }

    /// <summary>
    /// Calls <paramref name="pressed"/> on each Ctrl+C pressed in the console, until the result is disposed, and keeps the
    /// press from ending the process. Ctrl+Break is left to the console's other handlers.
    /// </summary>
    public static IDisposable Watch(Action pressed)
    {
        ConsoleCancelEventHandler handler = (_, e) =>
        {
            if (e.SpecialKey != ConsoleSpecialKey.ControlC) return;

            e.Cancel = true;
            pressed();
        };

        Console.CancelKeyPress += handler;
        return new Unhook(handler);
    }

    /// <summary>
    /// Adds or removes a handler of Ctrl+C for this process; with no handler, TRUE makes the process ignore Ctrl+C and
    /// FALSE turns Ctrl+C back on, for this process and the ones it starts afterwards.
    /// </summary>
    [SupportedOSPlatform("windows")]
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetConsoleCtrlHandler(IntPtr handler, [MarshalAs(UnmanagedType.Bool)] bool add);

    /// <summary>Takes the handler off the console's Ctrl+C when disposed, once.</summary>
    /// <param name="handler">The handler.</param>
    private sealed class Unhook(ConsoleCancelEventHandler handler) : IDisposable
    {
        private int _disposed;

        /// <summary>Takes the handler off.</summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) Console.CancelKeyPress -= handler;
        }
    }
}

/// <summary>Lines a window writes about a run it shows, in the form the console log gives its own lines.</summary>
internal static class WindowText
{
    /// <summary>
    /// <paramref name="text"/> as a line of the log: the local time now on <paramref name="time"/>, two spaces, the label of
    /// a warning or an error, and the text without control characters, as <see cref="PlainConsoleFormatter"/> writes lines.
    /// </summary>
    public static string Line(TimeProvider time, LogLevel level, string text)
    {
        var label = level switch
        {
            LogLevel.Warning => "WARNING  ",
            LogLevel.Error or LogLevel.Critical => "ERROR  ",
            _ => ""
        };

        return $"{Local(time, time.GetUtcNow(), "HH:mm:ss")}  {label}{Printable(text)}";
    }

    /// <summary><paramref name="moment"/> in the local time of <paramref name="time"/>, written with <paramref name="format"/> whatever the regional settings.</summary>
    public static string Local(TimeProvider time, DateTimeOffset moment, string format) =>
        TimeZoneInfo.ConvertTime(moment, time.LocalTimeZone).ToString(format, CultureInfo.InvariantCulture);

    /// <summary>
    /// <paramref name="text"/> fit to write to the console: each control character but line breaks and tabs becomes a space,
    /// since a BEL can ring, and the class recording would take the sound in, and an escape can start a sequence.
    /// </summary>
    public static string Printable(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        if (!text.Any(IsUnprintable)) return text;

        var printable = new StringBuilder(text.Length);
        foreach (var character in text) printable.Append(IsUnprintable(character) ? ' ' : character);
        return printable.ToString();
    }

    /// <summary>True for a control character other than a line break or a tab.</summary>
    private static bool IsUnprintable(char character) => char.IsControl(character) && character is not ('\r' or '\n' or '\t');
}
