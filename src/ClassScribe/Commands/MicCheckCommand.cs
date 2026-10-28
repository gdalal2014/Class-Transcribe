using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using ClassScribe.Audio.Microphones;
using ClassScribe.Desktop;
using ClassScribe.Storage;
using Microsoft.Extensions.Logging;

namespace ClassScribe.Commands;

/// <summary>
/// The microphone check, MicWatch inside ClassScribe (menu choice 8, <c>ClassScribe mic-check</c>): every 250 ms it reads,
/// from Windows' own lists, every active microphone with its mute and the apps' sessions on it, and Windows' microphone
/// usage record, and writes a line for each change to a log file and to the window, until Q, Ctrl+C, --seconds or a
/// cancellation stops it. M writes a numbered mark. It never opens a microphone, records nothing and sends nothing; it
/// runs in the window, takes no lock, and so may run beside a recording. The log is what Gulzar sends after a class, to
/// learn what the class page does with the microphone when he mutes (design addendum 6.2).
/// </summary>
/// <param name="sessions">Reads the microphones, their mute and the sessions on them.</param>
/// <param name="record">Reads Windows' microphone usage record.</param>
/// <param name="names">Names the processes behind the sessions.</param>
/// <param name="own">Tells ClassScribe's own processes, such as the recorder of a class recorded beside the check.</param>
/// <param name="folders">Where the recordings go, under which the mic-check folder is made.</param>
/// <param name="ui">The window's keyboard and screen.</param>
/// <param name="window">The window's title, which shows the check while it runs.</param>
/// <param name="signals">The Ctrl+C of the check's scope, which stops the check when the runner runs it (<see cref="IMicCheckRun"/>).</param>
/// <param name="time">The clock: the interval, the time of day on the lines, and the gaps between looks.</param>
/// <param name="logger">The app's log; what Gulzar reads goes to the window and the check's own file.</param>
public sealed class MicCheckCommand(IMicrophoneSessions sessions, IMicrophoneUsageRecord record, IProcessNames names, IOwnProcesses own,
    IClassFolders folders, IConsoleUi ui, IWindowSignals window, IUserSignals signals, TimeProvider time, ILogger<MicCheckCommand> logger) : IMicCheckRun
{
    /// <summary>The time between two looks: the longest a change can go unseen, as MicWatch had it.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(250);

    /// <summary>A gap between two looks this long or longer is written as a NOTE: the laptop slept or the check was held up.</summary>
    public static readonly TimeSpan HeldUpAfter = TimeSpan.FromSeconds(2);

    /// <summary>How often the window's title is brought up to date.</summary>
    private static readonly TimeSpan TitleInterval = TimeSpan.FromSeconds(1);

    /// <summary>How the time is written at the start of a footer line: local time to the millisecond, as the timestamped lines have it.</summary>
    private const string TimeFormat = "HH:mm:ss.fff";

    /// <summary>
    /// Runs the check as the runner runs it (<see cref="IMicCheckRun"/>), with Ctrl+C read through the signals of the check's
    /// scope: nothing is recorded here, so the first press cancels, as it does for the transcribe command, and stops the
    /// check as Q does, even when the input is redirected and no key is ever read. Returns the exit code.
    /// </summary>
    public Task<int> RunAsync(MicCheckRequest request)
    {
        // Nothing is recording here, so the first Ctrl+C should already stop the check.
        signals.RequestStop();
        return RunAsync(request, signals.Cancellation);
    }

    /// <summary>
    /// Runs the check as <paramref name="request"/> asks until something stops it, and returns the exit code: 0 after a normal
    /// stop, 1 when the log file could not be created. A cancellation of <paramref name="cancellationToken"/> stops it as Q
    /// does: the caller passes a token that Ctrl+C cancels, since a Ctrl+C reaches the check as a key only while a key is
    /// being read, which is never when the input is redirected.
    /// </summary>
    public async Task<int> RunAsync(MicCheckRequest request, CancellationToken cancellationToken)
    {
        var folder = string.IsNullOrWhiteSpace(request.LogFolder) ? Path.Combine(folders.Root, FolderNames.MicCheck) : folders.RootFor(request.LogFolder);
        var started = Now();
        MicCheckLog log;
        try
        {
            log = MicCheckLog.Create(folder, started);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            // Deliberately not rethrown: a log that cannot be created is the one failure the check reports as its exit code,
            // 1, as the design has it: there is nothing to look at without the file Gulzar sends, and the reason is logged here.
            logger.LogError(ex, "The microphone check cannot create its log in {Folder}: {Reason}", folder, ex.Message);
            return 1;
        }

        using (log)
        {
            var watch = new Watch(new MicrophoneChangeTracker(names, own, Environment.ProcessId, time.LocalTimeZone), log, time.GetTimestamp());
            foreach (var line in MicCheckLog.HeaderLines(started, Environment.ProcessId, log.Path, Interval, request.StopAfter)) Show(log, line);
            logger.LogDebug("The microphone check writes to {Path}", log.Path);

            string reason;
            try
            {
                reason = await LookUntilStoppedAsync(watch, request, cancellationToken);
            }
            catch (Exception ex)
            {
                // Logged to the app's log first, then into the file that gets sent, so that a failure during a class shows in
                // it; then rethrown. The file is written second because it is the loop's likeliest failure.
                logger.LogError(ex, "The microphone check failed after {Looks}", Looks(watch.Looks));
                try
                {
                    log.Plain($"{Stamp(Now())}  The microphone check failed after {Looks(watch.Looks)}: {ex}");
                }
                catch (IOException)
                {
                    // Deliberately not rethrown: the file itself is what failed, on a full or removed disk, and the failure to
                    // report in it must not replace the failure being reported, which is in the app's log and is rethrown.
                }

                throw;
            }
            finally
            {
                window.SetTitle(TitleText.AppName);
            }

            var elapsed = time.GetElapsedTime(watch.StartedAt);
            Show(log, "");
            Show(log, $"{Stamp(Now())}  The microphone check stopped: {reason}. {Looks(watch.Looks)} in {Durations.Format(elapsed)}; {log.TimedLines} timestamped lines.");
            Show(log, MicCheckLog.CostLine());
            Show(log, $"The log is saved as: {log.Path}");
        }

        return 0;
    }

    /// <summary>
    /// Looks at once and then every <see cref="Interval"/>, acting on the keys in between, until Q, Ctrl+C, --seconds or the
    /// cancellation stops the check; returns why it stopped. The looks follow one timer made at the start, so that a look
    /// held up, or the laptop asleep, is followed by one look rather than a burst of them, with a NOTE for the gap.
    /// </summary>
    private async Task<string> LookUntilStoppedAsync(Watch watch, MicCheckRequest request, CancellationToken cancellationToken)
    {
        // Not disposed: the timer's callback may release it a moment after the timer is disposed, and a released semaphore
        // that has been disposed throws on the timer's thread. It holds no handle of the system, so there is nothing to free.
        var ticks = new SemaphoreSlim(0);
        using var timer = time.CreateTimer(_ => ticks.Release(), null, Interval, Interval);
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task<ConsoleKeyInfo?>? nextKey = null;
        var keysLeft = ui.IsInteractive;
        ui.DiscardPendingKeys();
        try
        {
            while (true)
            {
                var now = time.GetTimestamp();
                var gap = time.GetElapsedTime(watch.LastLook, now);
                if (watch.Looks > 0 && gap >= HeldUpAfter)
                {
                    Show(watch.Log, new MicCheckLine(Now(), MicCheckLine.Note,
                        $"no check for {gap.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)} s: the laptop slept or the check was held up, so a change that came and went in that time is not in the log."));
                }

                watch.LastLook = now;
                Look(watch);
                watch.Looks++;

                var elapsed = time.GetElapsedTime(watch.StartedAt, now);
                if (watch.TitleShown is not { } shown || elapsed - shown >= TitleInterval)
                {
                    window.SetTitle($"MIC CHECK {Durations.Format(elapsed)}, {Lines(watch.Log.TimedLines)}  Q stops | {TitleText.AppName}");
                    watch.TitleShown = elapsed;
                }

                if (request.StopAfter is { } limit && elapsed >= limit) return TimeIsUp(limit);

                // Waits for the next tick, acting on each key pressed meanwhile. A read that answers without a key although
                // nothing stopped it means that the keys have run out, as when their source has closed: no more are read.
                var tick = ticks.WaitAsync(stopping.Token);
                while (!tick.IsCompleted)
                {
                    if (keysLeft) nextKey ??= ui.ReadKeyAsync(stopping.Token);
                    await (nextKey is null ? Task.WhenAny(tick) : Task.WhenAny(tick, nextKey));
                    if (nextKey is not { IsCompleted: true }) continue;

                    var key = await nextKey;
                    nextKey = null;
                    if (key is { } pressed)
                    {
                        if (OnKey(pressed, watch) is { } reason) return reason;
                    }
                    else
                    {
                        keysLeft = false;
                        logger.LogDebug("No more keys can be read; the check goes on without them");
                    }
                }

                if (cancellationToken.IsCancellationRequested) return "cancelled";

                // Ticks that piled up while a look was held up are one look, not a burst.
                while (ticks.CurrentCount > 0) ticks.Wait(0, CancellationToken.None);
            }
        }
        finally
        {
            // A cancelled read answers null within moments, so this only waits for it to let go of the keyboard.
            stopping.Cancel();
            if (nextKey is not null) await nextKey;
        }
    }

    /// <summary>
    /// One look: the microphones with their sessions, then the usage record, each handed to the tracker, with its lines
    /// shown. A list of microphones that could not be read as a whole, which the reader says rather than throws, is
    /// unreadable like a reader that threw: one ERROR line, and not every microphone gone.
    /// </summary>
    private void Look(Watch watch)
    {
        var at = Now();
        var microphoneLines = TryRead(sessions.ReadAll, "Windows' list of microphones", out var microphones, out var problem)
            ? microphones.Problem is { } unreadable ? watch.Tracker.MicrophonesUnreadable(unreadable, at) : watch.Tracker.Update(microphones.Listings, at)
            : watch.Tracker.MicrophonesUnreadable(problem, at);
        Show(watch.Log, microphoneLines);
        Show(watch.Log, TryRead(record.Read, "Windows' microphone usage record", out var usage, out problem)
            ? watch.Tracker.Update(usage, at)
            : watch.Tracker.UsageRecordUnreadable(problem, at));
    }

    /// <summary>
    /// Reads from one of the readers, which answer with a listing that says what could not be read and are not meant to
    /// throw; one that throws all the same gives false, with the problem in words for the tracker's ERROR line.
    /// </summary>
    private bool TryRead<T>(Func<T> read, string what, [NotNullWhen(true)] out T? result, [NotNullWhen(false)] out string? problem) where T : class
    {
        try
        {
            result = read();
            problem = null;
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Deliberately not rethrown: the check is a diagnostic that must keep looking, as MicWatch did. A reader that fails
            // in a way it did not expect becomes an ERROR line in the log Gulzar sends, once until it reads again, and the next
            // look tries again. Noted at Debug, because the look is made four times a second.
            logger.LogDebug(ex, "{What} could not be read ({Reason}); the next look tries again", what, ex.Message);
            result = null;
            problem = $"{ex.GetType().Name}: {ex.Message.Trim()}";
            return false;
        }
    }

    /// <summary>
    /// Acts on a key: Ctrl+C, which the console hands over as a key while one is read, and Q stop the check, each the reason
    /// returned; M writes a numbered mark. Other keys do nothing, and so does a key held with Ctrl or Alt, such as a shortcut
    /// meant for another program. One press of Ctrl+C is enough: no class is at risk.
    /// </summary>
    private string? OnKey(ConsoleKeyInfo key, Watch watch)
    {
        if (key.Key == ConsoleKey.C && key.Modifiers.HasFlag(ConsoleModifiers.Control)) return "Ctrl+C pressed";
        if ((key.Modifiers & (ConsoleModifiers.Control | ConsoleModifiers.Alt)) != 0) return null;

        switch (key.Key)
        {
            case ConsoleKey.Q:
                return "Q pressed";
            case ConsoleKey.M:
                watch.Marks++;
                Show(watch.Log, new MicCheckLine(Now(), MicCheckLine.Mark, $"{watch.Marks} (M pressed)"));
                return null;
            default:
                return null;
        }
    }

    /// <summary>Writes each of <paramref name="lines"/> to the log and shows it in the window.</summary>
    private void Show(MicCheckLog log, IReadOnlyList<MicCheckLine> lines)
    {
        foreach (var line in lines) Show(log, line);
    }

    /// <summary>Writes a timestamped line to the log and shows it in the window.</summary>
    private void Show(MicCheckLog log, MicCheckLine line) => ui.WriteLine(log.Write(line));

    /// <summary>Writes a plain line, of the header or the footer, to the log and shows it in the window.</summary>
    private void Show(MicCheckLog log, string text)
    {
        log.Plain(text);
        ui.WriteLine(text);
    }

    /// <summary>The time now, in the laptop's zone, as the lines show it.</summary>
    private DateTimeOffset Now() => TimeZoneInfo.ConvertTime(time.GetUtcNow(), time.LocalTimeZone);

    /// <summary>A local time to the millisecond, as the timestamped lines show it.</summary>
    private static string Stamp(DateTimeOffset at) => at.ToString(TimeFormat, CultureInfo.InvariantCulture);

    /// <summary>Why the check stopped when --seconds ran out: "the 1 second asked for with --seconds has passed", "the 60 seconds ... have passed".</summary>
    private static string TimeIsUp(TimeSpan limit)
    {
        var seconds = MicCheckLog.Seconds(limit);
        return $"the {seconds} asked for with --seconds {(seconds == "1 second" ? "has" : "have")} passed";
    }

    /// <summary>"1 look", "5 looks".</summary>
    private static string Looks(int looks) => looks == 1 ? "1 look" : string.Create(CultureInfo.InvariantCulture, $"{looks} looks");

    /// <summary>"1 line", "23 lines".</summary>
    private static string Lines(int lines) => lines == 1 ? "1 line" : string.Create(CultureInfo.InvariantCulture, $"{lines} lines");

    /// <summary>What one run of the check keeps while it looks: the tracker, the log, the counts and the times of its last look and title.</summary>
    /// <param name="Tracker">The tracker that turns each look into lines.</param>
    /// <param name="Log">The log file.</param>
    /// <param name="StartedAt">When the check started, on the steady clock.</param>
    private sealed record Watch(MicrophoneChangeTracker Tracker, MicCheckLog Log, long StartedAt)
    {
        /// <summary>How many looks have been made.</summary>
        public int Looks { get; set; }

        /// <summary>How many marks have been written.</summary>
        public int Marks { get; set; }

        /// <summary>When the last look began, on the steady clock; the start before the first.</summary>
        public long LastLook { get; set; } = StartedAt;

        /// <summary>How long the check had run when the title was last set; null before the first.</summary>
        public TimeSpan? TitleShown { get; set; }
    }
}
