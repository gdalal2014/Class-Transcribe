using System.Globalization;
using ClassScribe.Desktop;
using ClassScribe.Recorder;
using ClassScribe.Runs;
using ClassScribe.Settings;
using ClassScribe.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClassScribe.Commands;

/// <summary>
/// Where a run records, as whoever starts it in its scope says: in a separate recorder, with its pipe and the run file its
/// host began as starting, or in this window. A scope nobody fills runs its run in this window (see
/// <see cref="RunContextSlot"/>).
/// </summary>
/// <param name="RunId">The run's id, the one its request carries (see <see cref="RunIds"/>).</param>
/// <param name="Home">Where the run records.</param>
/// <param name="PipeName">The name of the pipe the windows that show the run connect to; null for a run in this window, which has none.</param>
public sealed record RunContext(string RunId, RunHome Home, string? PipeName)
{
    /// <summary>
    /// The run file the recorder's host began, as starting, for the run to keep up to date: one run has one handle, which
    /// the host lets go of. Null in this window, where the run begins its own run file.
    /// </summary>
    public IRunHandle? Run { get; init; }
}

/// <summary>
/// The place in a run's scope for its <see cref="RunContext"/>: the recorder's host fills it, and so may the starter in the
/// window, before the run itself is resolved in the scope. One per scope. Left empty, the run records in this window.
/// </summary>
public sealed class RunContextSlot
{
    private readonly Lock _gate = new();
    private RunContext? _value;
    private bool _read;

    /// <summary>The context filled in, or null when nobody filled it: the run then records in this window.</summary>
    public RunContext? Value
    {
        get
        {
            lock (_gate)
            {
                _read = true;
                return _value;
            }
        }
    }

    /// <summary>
    /// Fills in where the run records. Once a run has read it, or it was filled once, it cannot change any more: an
    /// <see cref="InvalidOperationException"/> says so, as filling it late is a mistake in the code that starts the run.
    /// </summary>
    public void Fill(RunContext context)
    {
        lock (_gate)
        {
            if (_value is not null) throw new InvalidOperationException("The run's context is filled once.");
            if (_read) throw new InvalidOperationException("The run's context was read before it was filled; fill it before the run is resolved in its scope.");

            _value = context;
        }
    }
}

/// <summary>Warns a running command that an error nobody caught is taking the process down.</summary>
public interface ICrashWarnings
{
    /// <summary>
    /// Calls <paramref name="onCrash"/> with the error when an exception nobody caught is taking the process down. A warning
    /// registered before the exit warnings (see <see cref="IExitWarnings"/>), which such an error raises as well, comes
    /// before theirs. Disposing the result stops the warning.
    /// </summary>
    IDisposable Register(Action<object> onCrash);
}

/// <summary>
/// What every run holds while it records, in a recorder or in this window: one recording at a time (the lock, from just
/// before the run makes any file until its capture stops), the run file (the mark that a recording is under way, which
/// the next start of ClassScribe reports should the process end unexpectedly, and how the run ended), the recording's own
/// log beside it (&lt;base&gt;.log), the laptop kept awake with Windows asked to wait before a restart, and the handlers
/// for an end that comes without the usual stop: Windows ending the session, the window being closed, and an error nobody
/// caught. One per run, and so per scope.
/// </summary>
/// <remarks>
/// <para>None of this may cost the class. When ClassScribe's own folder cannot be written, the run records without the lock
/// and without a run file, each said with a warning; a run file that cannot be brought up to date later is reported once
/// by its handle, and the log counts the lines its file does not take, which <see cref="End"/> reports.</para>
/// <para>In this window the run begins its own run file, and marks it seen once it ends, since the window shows how it
/// ended; the recording's log takes the lines from the start of the run only, and is let go of when the run's scope ends;
/// and the window's title and taskbar button, which show the run's states, are given back then. In a recorder the run
/// keeps up to date the run file its host began (see <see cref="RunContext.Run"/>), which the windows that show the run
/// mark seen, and the recording's log takes every line the recorder wrote, as the host may log more after the run.</para>
/// </remarks>
public sealed class RunBookkeeping(
    IRecordingLock recordingLock,
    IRunJournal journal,
    IRunLog log,
    IPowerRequests power,
    IPowerSource battery,
    ISessionEvents session,
    IExitWarnings exitWarnings,
    ICrashWarnings crashes,
    IWindowSignals window,
    RunContextSlot context,
    IThisProgram program,
    IOptionsSnapshot<RecordingOptions> recording,
    TimeProvider time,
    ILogger<RunBookkeeping> logger) : IDisposable
{
    /// <summary>The recorder's own exit code for a start the lock refused (see design 1.11).</summary>
    public const int RefusedExitCode = 3;

    /// <summary>How long a handler for the end of the process or the session waits for the log to reach its file, unless told otherwise: Windows allows little time.</summary>
    internal static readonly TimeSpan DefaultHandlerLogPatience = TimeSpan.FromSeconds(1);

    /// <summary>How long the end of the run waits for its log to reach its file, while the disk is slow to take it.</summary>
    private static readonly TimeSpan EndLogPatience = TimeSpan.FromSeconds(5);

    private readonly Lock _gate = new();

    /// <summary>The run file the run keeps up to date: its own, or its host's; null before <see cref="Begin"/> and when it could not be written.</summary>
    private IRunHandle? _run;

    /// <summary>The run file this bookkeeping began itself, which it lets go of when the run's scope ends.</summary>
    private IRunHandle? _own;

    /// <summary>The recording's log, once named; null before.</summary>
    private string? _logFile;

    /// <summary>How many lines the log had lost before the run, so that the end reports the run's own.</summary>
    private int _lostBefore;

    /// <summary>The laptop kept awake, while it is; null before and after.</summary>
    private Once? _awake;

    /// <summary>The handlers for an end without the usual stop, while they are hooked; null before and after.</summary>
    private Once? _hooked;

    /// <summary>Brings the recording on disk up to date, for the handlers; null while none are hooked.</summary>
    private Action? _flush;

    /// <summary>Finishes the recording's files and stops the run, for Windows ending the session; null while no handler is hooked.</summary>
    private Action? _finish;

    /// <summary>Where the recording is now, for the heartbeat the handlers write; null while no handler is hooked.</summary>
    private Func<TimeSpan>? _position;

    /// <summary>1 once an error nobody caught is taking the process down.</summary>
    private int _crashing;

    /// <summary>True once the run has ended with an outcome, which the window or the recorder has been given to show.</summary>
    private bool _ended;

    /// <summary>True once the run's scope has ended.</summary>
    private bool _disposed;

    /// <summary>
    /// How long a handler for the end of the process or the session waits for the run's log to reach its file:
    /// <see cref="DefaultHandlerLogPatience"/> unless a test, standing in for a stalled disk, says otherwise.
    /// </summary>
    internal TimeSpan HandlerLogPatience { get; init; } = DefaultHandlerLogPatience;

    /// <summary>Where the run records: in a separate recorder, or in this window when nobody said otherwise.</summary>
    public RunHome Home => context.Value?.Home ?? RunHome.InWindow;

    /// <summary>The run file the run keeps up to date, or null when there is none: before <see cref="Begin"/>, or when it could not be written.</summary>
    public IRunHandle? Run
    {
        get
        {
            lock (_gate) return _run ?? context.Value?.Run;
        }
    }

    /// <summary>
    /// Takes the lock that allows one recording at a time, before the run makes any file, and returns what lets it go; or
    /// null when another recording holds it. In this window the recording's log begins here: the lines the window wrote
    /// before, for other work, stay out of it. When ClassScribe's own folder cannot be written, there is no lock to take:
    /// the run records without it, with a warning, and what is returned lets go of nothing.
    /// </summary>
    public IDisposable? TryTakeLock()
    {
        if (Home == RunHome.InWindow) log.KeepFromNow();
        _lostBefore = log.LostFileLines;

        try
        {
            var held = recordingLock.TryTake();
            if (held is null) logger.LogDebug("Another recording holds the lock; this run is refused");
            return held;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Deliberately not rethrown: the lock only keeps a second recording from starting, and a folder of ClassScribe's
            // own that cannot be written must not cost the class. The warning says what is missing.
            logger.LogWarning(
                "Could not take the lock that allows one recording at a time ({Reason}), so this recording runs without it: start no other recording until it stops",
                ex.Message);
            return new Once(() => { });
        }
    }

    /// <summary>
    /// The outcome of a start the lock refused: another recording runs. It names that recording when a run file shows it,
    /// with when it started by the local time of day and how long it has run. In a recorder, the run file its host began
    /// says the start was refused, for the window that started it.
    /// </summary>
    public RunOutcome Refuse(RecordRequest request)
    {
        var outcome = new RunOutcome(OutcomeKind.Refused, 1, DescribeRefusal(request.RunId), TranscriptPath: null, Folder: null, RetryCommand: null);
        if (context.Value?.Run is { } begun) begun.End(outcome);
        return outcome;
    }

    /// <summary>
    /// Begins the run's bookkeeping once its files are known: the run file says it records into <paramref name="files"/>,
    /// the mark that a recording is under way, and the recording's log (&lt;base&gt;.log) takes the run's lines from now on,
    /// with the lines written since the run began. In a recorder that is the run file its host began; in this window the
    /// run begins its own, and run files with nothing left to report are pruned. A run file that cannot be written is said
    /// with a warning, and the run records without it. Returns the run file, or null without one.
    /// </summary>
    public IRunHandle? Begin(RecordRequest request, RecordingFiles files)
    {
        var current = context.Value;
        if (current is not null && current.RunId != request.RunId)
            throw new ArgumentException($"The run's context is run {current.RunId}'s, and the request run {request.RunId}'s.", nameof(request));

        IRunHandle? run;
        if (current?.Run is { } begun)
        {
            begun.Recording(files);
            run = begun;
        }
        else
        {
            run = BeginOwn(request, files, current);
        }

        lock (_gate)
        {
            _run = run;
            if (current?.Run is null) _own = run;
            _logFile = files.Log;
        }

        log.WriteFileTo(files.Log);
        return run;
    }

    /// <summary>
    /// Keeps the laptop awake, ClassScribe running, and the screen on as Recording:KeepScreenOn says (on battery only, by
    /// default), and, while Recording:BlockShutdown is true, makes Windows ask before a restart, a shutdown or a sign-out
    /// that Gulzar starts, naming ClassScribe; both until the result is disposed. A request Windows refuses is logged, never
    /// thrown. Disposing the result twice lets go once.
    /// </summary>
    public IDisposable KeepAwake()
    {
        var settings = recording.Value;
        var screen = settings.KeepScreenOn;
        var hold = power.Hold(StayAwake.RecordingReason, () => StayAwake.Needs(screen, battery));
        var askFirst = settings.BlockShutdown;
        if (askFirst) session.BlockShutdown(StayAwake.RecordingReason);

        var awake = new Once(() =>
        {
            if (askFirst) session.BlockShutdown(null);
            hold.Dispose();
        });
        lock (_gate) _awake = awake;
        return awake;
    }

    /// <summary>
    /// Hooks the handlers for an end that comes without the usual stop, until the result is disposed:
    /// <list type="bullet">
    /// <item>Windows asking whether it may end the session: the run file puts a coming end down to Windows, first, then
    /// <paramref name="flush"/> brings the recording on disk up to date and the run file's heartbeat says where the
    /// recording is (<paramref name="position"/>); when Windows goes on after all, the cause is taken back;</item>
    /// <item>Windows ending the session: the run file puts the end down to Windows, and <paramref name="finish"/> finishes
    /// the recording's files and stops the run, before Windows ends the process;</item>
    /// <item>the window being closed, or an error nobody caught: the run file puts the end down to the window (in this
    /// window) or to the error, first, then <paramref name="flush"/> and the heartbeat.</item>
    /// </list>
    /// Each handler writes the cause before anything else, so that it is on disk should the process end during the handler.
    /// The handlers then wait a moment for the run's log to reach its file, except when Windows forces the end of the
    /// session, which leaves about a second to answer its question. They never throw: the process is on its way out.
    /// </summary>
    public IDisposable OnEnding(Action flush, Action finish, Func<TimeSpan> position)
    {
        lock (_gate)
        {
            if (_hooked is not null) throw new InvalidOperationException("The handlers for the end of a run are hooked once.");

            _flush = flush;
            _finish = finish;
            _position = position;
        }

        // The warning of an error nobody caught is registered first, so that it comes before the exit warnings' own, which
        // such an error raises as well: the error, not the window, is then the cause written down (see OnExit).
        var crash = crashes.Register(OnCrash);
        var exit = exitWarnings.Register(OnExit);
        session.Ending += OnSessionEnding;
        session.Ended += OnSessionEnded;
        session.EndingCancelled += OnSessionEndingCancelled;

        var hooked = new Once(() =>
        {
            session.EndingCancelled -= OnSessionEndingCancelled;
            session.Ended -= OnSessionEnded;
            session.Ending -= OnSessionEnding;
            exit.Dispose();
            crash.Dispose();
            lock (_gate)
            {
                _flush = null;
                _finish = null;
                _position = null;
            }
        });
        lock (_gate) _hooked = hooked;
        return hooked;
    }

    /// <summary>
    /// The run is over, as <paramref name="outcome"/> says: the run file says so (finished, or failed for a recording that
    /// failed), and in this window, which has shown how the run ended, it is marked seen, so that no start of ClassScribe
    /// reports it again. Lines of the run's log that its file did not take are reported first.
    /// </summary>
    public void End(RunOutcome outcome)
    {
        lock (_gate) _ended = true;
        ReportLostLines();
        if (Run is not { } run) return;

        run.End(outcome);
        if (Home == RunHome.InWindow) journal.MarkSeen(run.Current.RunId);
    }

    /// <summary>
    /// Ends the run's bookkeeping with its scope: unhooks the handlers and lets the laptop go when that has not happened yet,
    /// lets go of the run file this bookkeeping began, and, in this window, of the recording's log, whose lines are written
    /// first. A run in this window that has ended, and shown how, gives the window back: its taskbar button loses the run's
    /// bar and its title says ClassScribe, so that the run's last state does not stay through whatever the window does next.
    /// </summary>
    public void Dispose()
    {
        Once? hooked, awake;
        IRunHandle? own;
        string? logFile;
        bool ended;
        lock (_gate)
        {
            if (_disposed) return;

            _disposed = true;
            (hooked, awake, own, logFile, ended) = (_hooked, _awake, _own, _logFile, _ended);
        }

        hooked?.Dispose();
        awake?.Dispose();
        if (logFile is not null && Home == RunHome.InWindow)
        {
            log.EndFile();
            if (!log.Flush(EndLogPatience)) logger.LogDebug("The recording's log {Path} was still being written when the run ended", logFile);
        }

        own?.Dispose();
        if (ended && Home == RunHome.InWindow)
        {
            window.SetTaskbar(TaskbarState.None);
            window.SetTitle(TitleText.AppName);
        }
    }

    /// <summary>
    /// What an error nobody caught does to the run as it takes the process down: the run file puts the end down to the
    /// error, and the recording on disk is brought up to date. Registered through <see cref="OnEnding"/>.
    /// </summary>
    private void OnCrash(object error)
    {
        Interlocked.Exchange(ref _crashing, 1);
        Run?.SetEndedBy(RunEndCause.Error);
        logger.LogCritical(error as Exception, "An error nobody caught is ending ClassScribe; the recording so far is saved");
        Save();
        log.Flush(HandlerLogPatience);
    }

    /// <summary>
    /// The window is being closed, or an error nobody caught takes the process down (the exit warnings do not tell which):
    /// unless the error's own handler has dealt with it, the run file puts the end down to the window, in this window, or to
    /// an error in a recorder, which has no window to close, and the recording on disk is brought up to date.
    /// </summary>
    private void OnExit()
    {
        if (Volatile.Read(ref _crashing) == 0)
        {
            Run?.SetEndedBy(Home == RunHome.InWindow ? RunEndCause.WindowClosed : RunEndCause.Error);
            logger.LogWarning("ClassScribe is being closed; the recording so far is saved");
            Save();
        }

        log.Flush(HandlerLogPatience);
    }

    /// <summary>
    /// Windows asks whether it may end the session: the run file puts a coming end down to Windows, first, then the
    /// recording on disk is brought up to date. When Windows forces the end, it leaves about a second for the answer, so the
    /// handler does not wait for the log then; the end of the session that follows has more time, and waits for it.
    /// </summary>
    private void OnSessionEnding(object? sender, SessionEndEventArgs e)
    {
        Run?.SetEndedBy(RunEndCause.Windows);
        logger.LogWarning("Windows is asking to end the session ({Kind}); the recording so far is saved", Describe(e.Kind));
        Save();
        if (!e.Forced) log.Flush(HandlerLogPatience);
    }

    /// <summary>Windows ends the session now: the run file puts the end down to Windows, and the recording's files are finished.</summary>
    private void OnSessionEnded(object? sender, SessionEndEventArgs e)
    {
        Run?.SetEndedBy(RunEndCause.Windows);
        logger.LogWarning("Windows is ending the session now ({Kind}); finishing the recording's files", Describe(e.Kind));

        Action? finish;
        lock (_gate) finish = _finish;
        try
        {
            finish?.Invoke();
        }
        catch (Exception ex)
        {
            // Deliberately not rethrown: Windows ends the process once this handler returns, so an exception here has nowhere
            // to go; what is on disk stays, and the next start of ClassScribe reports the recording.
            logger.LogError(ex, "Could not finish the recording's files before Windows ends the session");
        }

        log.Flush(HandlerLogPatience);
    }

    /// <summary>Windows does not end the session after all: the end it announced is taken back, and the class goes on recording.</summary>
    private void OnSessionEndingCancelled(object? sender, EventArgs e)
    {
        Run?.WithdrawEndedBy(RunEndCause.Windows);
        logger.LogInformation("Windows is not ending the session after all; the recording goes on");
    }

    /// <summary>
    /// Brings the recording on disk up to date because the process may be about to end, and has the run file's heartbeat
    /// say where the recording is, for the report at the next start.
    /// </summary>
    private void Save()
    {
        Action? flush;
        Func<TimeSpan>? position;
        lock (_gate) (flush, position) = (_flush, _position);
        try
        {
            flush?.Invoke();
            if (position is not null) Run?.Heartbeat(position());
        }
        catch (Exception ex)
        {
            // Deliberately not rethrown: the process is already on its way out, and an exception from a shutdown handler has
            // nowhere to go.
            logger.LogError(ex, "Could not bring the recording on disk up to date before exiting");
        }
    }

    /// <summary>
    /// Begins this run's own run file, recording into <paramref name="files"/>, and prunes the run files that have nothing
    /// left to report, as the run is the one that writes run files in this window. Null, with a warning, when it cannot be
    /// written: the run records without it.
    /// </summary>
    private IRunHandle? BeginOwn(RecordRequest request, RecordingFiles files, RunContext? current)
    {
        var now = time.GetUtcNow();
        var record = new RunRecord(RunRecord.CurrentVersion, request.RunId, program.ProcessId, program.StartedUtc, program.ProcessPath ?? program.AppFolder,
            BuildInfo.Build, LinkVersion.Current, current?.PipeName, current?.Home ?? RunHome.InWindow, request, files.BaseName, files.Folder,
            RunState.Recording, now, now, TimeSpan.Zero, UploadPercent: null, EndedBy: null, Outcome: null, Seen: false);
        IRunHandle run;
        try
        {
            run = journal.Begin(record);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Deliberately not rethrown: the run file is bookkeeping, and a class must never go unrecorded for it. Without it
            // only the report after an unexpected end is lost, which the warning says.
            logger.LogWarning(
                "Could not write this recording's run file ({Reason}). It records all the same, but should it end unexpectedly, the next start of ClassScribe cannot report it",
                ex.Message);
            return null;
        }

        try
        {
            journal.Prune(RunTimings.KeepSeenRunsFor);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Deliberately not rethrown: pruning is housekeeping, which the next run tries again, and must not cost the class.
            logger.LogWarning(ex, "Could not delete the old run files in ClassScribe's own folder; the next recording tries again");
        }

        return run;
    }

    /// <summary>
    /// Why a start was refused, in words: the recording that runs, from its run file, when one shows it, with when it started
    /// by the local time of day and how long it has run, and where it can be seen or stopped.
    /// </summary>
    private string DescribeRefusal(string ownRunId)
    {
        var other = journal.List().FirstOrDefault(run => run.IsRecordingNow && run.Record.RunId != ownRunId)?.Record;
        if (other is null) return "Only one recording at a time: another recording is running. Start ClassScribe to see it, or stop it first.";

        var since = TimeZoneInfo.ConvertTime(other.StartedUtc, time.LocalTimeZone).ToString("HH:mm", CultureInfo.InvariantCulture);
        var running = Durations.Format(time.GetUtcNow() - other.StartedUtc);
        return other.Home == RunHome.InWindow
            ? $"Only one recording at a time: \"{other.Request.Name}\" has been recording since {since} ({running}) in another ClassScribe window (process {other.ProcessId}), which shows it. Stop it there first."
            : $"Only one recording at a time: \"{other.Request.Name}\" has been recording since {since} ({running}). Start ClassScribe to see it, or stop it first.";
    }

    /// <summary>Reports the lines of the run's log that its file did not take, once they have all been tried, with the first failure.</summary>
    private void ReportLostLines()
    {
        if (Volatile.Read(ref _logFile) is null) return;

        log.Flush(EndLogPatience);
        var lost = log.LostFileLines - _lostBefore;
        if (lost <= 0) return;

        logger.LogWarning("{Count} lines of this recording's log were not saved to its file. {Reason}", lost,
            log.FirstFileFailure is { } failure ? failure.TrimEnd('.') + "." : "The disk did not keep up with them.");
    }

    /// <summary>The kind of end Windows announces, in words for the log.</summary>
    private static string Describe(SessionEndKind kind) => kind switch
    {
        SessionEndKind.SignOut => "a sign-out",
        SessionEndKind.ClosingPrograms => "programs being closed, as for an update",
        _ => "a restart or a shutdown"
    };

    /// <summary>Does its work the first time it is disposed, and nothing after, from whichever thread.</summary>
    private sealed class Once(Action work) : IDisposable
    {
        private int _done;

        /// <summary>Does the work, the first time only.</summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0) work();
        }
    }
}
