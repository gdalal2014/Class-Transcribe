using System.ComponentModel;
using ClassScribe.Desktop;
using ClassScribe.Link;
using ClassScribe.Runs;
using ClassScribe.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClassScribe.Recorder;

/// <summary>
/// Makes the recorder seen while no window shows its run, without a sound, as Recording:ReopenWindow says: it brings back
/// a ClassScribe window, minimized and without taking the focus, 3 seconds after the last window went away while the class
/// records, unless it left with Esc, which is a choice made at the keyboard and not a window closed (from the menu, the
/// menu is still there); at once when something needs attention, or when the run fails; and once something worth a look has lasted
/// 2 minutes (the "no sound" notice begins at Recording:SilenceWarningSeconds, so by default the window comes back after
/// about 3 minutes of silence). That window ignores every key until Enter is pressed in it. One such window at a time, and
/// at most one launch every 2 minutes; and a window brought back for a mere close that is closed again within 2 minutes
/// means "leave me alone": no more windows for a close in this run, while a problem still brings one. What a window showed,
/// a failure or an alert, brings none back once that window is gone: whoever closed it had seen it. The reason is logged
/// before the window opens, so that the window shows it in its replay of the log.
/// </summary>
/// <param name="server">The recorder's server, which says how many windows show the run.</param>
/// <param name="starter">What starts the window.</param>
/// <param name="options">The settings, for Recording:ReopenWindow.</param>
/// <param name="time">The clock the waits and limits are measured on.</param>
/// <param name="logger">Where the reasons and the failures are logged.</param>
public sealed class UnattendedAlerts(RecorderServer server, IProcessStarter starter, IOptionsMonitor<RecordingOptions> options, TimeProvider time,
    ILogger<UnattendedAlerts> logger) : IDisposable
{
    /// <summary>How long after the last window went away the window is brought back, if none came meanwhile.</summary>
    public static readonly TimeSpan AfterClose = TimeSpan.FromSeconds(3);

    /// <summary>How long something worth a look lasts before the window is brought back for it.</summary>
    public static readonly TimeSpan NoticePatience = TimeSpan.FromMinutes(2);

    /// <summary>The least time between two launches of the window.</summary>
    public static readonly TimeSpan LaunchInterval = TimeSpan.FromMinutes(2);

    /// <summary>A window brought back for a close and closed again within this time means that closes should bring none back again.</summary>
    public static readonly TimeSpan CloseSnooze = TimeSpan.FromMinutes(2);

    private readonly Lock _gate = new();

    /// <summary>The run, once started; null before.</summary>
    private string? _runId;

    /// <summary>True once disposed: nothing opens a window any more.</summary>
    private bool _disposed;

    /// <summary>How many windows showed the run at the last look, to tell the last one going away from any other change.</summary>
    private int _windows;

    /// <summary>When the last window went away, on the steady clock, while none has come since; null otherwise.</summary>
    private long? _closedAt;

    /// <summary>Where the run was at the last state.</summary>
    private RecorderPhase _phase = RecorderPhase.Starting;

    /// <summary>What a window is wanted for and not yet opened: the reason to log, and whether it is for a mere close.</summary>
    private readonly List<(string Reason, bool ForClose)> _wanted = [];

    /// <summary>When the window was last launched, or tried, on the steady clock; null before the first.</summary>
    private long? _lastLaunch;

    /// <summary>The window launched last, while it may still live; null when none was, or it has ended.</summary>
    private StartedProcess? _window;

    /// <summary>When <see cref="_window"/> was launched, on the steady clock.</summary>
    private long _windowLaunchedAt;

    /// <summary>True when <see cref="_window"/> was brought back for a mere close.</summary>
    private bool _windowForClose;

    /// <summary>True once a window brought back for a close was closed within <see cref="CloseSnooze"/>: closes bring none back again.</summary>
    private bool _closesSnoozed;

    /// <summary>The attention count of the last Alert a window was wanted for, or a window showed, so that each new Alert wants one once.</summary>
    private int _lastAlertCount;

    /// <summary>When the Notice that lasts began, on the steady clock; null while nothing is worth a look.</summary>
    private long? _noticeSince;

    /// <summary>True once a window was wanted for the Notice that lasts, so that it is wanted once.</summary>
    private bool _noticeWanted;

    /// <summary>True once a window was wanted for the run's failure, or a window showed it: the failure wants none again.</summary>
    private bool _failureSeen;

    /// <summary>
    /// Starts following the windows of run <paramref name="runId"/>, which the window brought back is started for. Once
    /// per run: a second start throws an <see cref="InvalidOperationException"/>, and anything but a run id an
    /// <see cref="ArgumentException"/>, before it could name a process to start.
    /// </summary>
    public void Start(string runId)
    {
        if (!RunIds.IsWellFormed(runId)) throw new ArgumentException($"\"{runId}\" is not a ClassScribe run id.", nameof(runId));

        lock (_gate)
        {
            if (_runId is not null) throw new InvalidOperationException("The alerts are started once, for one run.");

            _runId = runId;
            _windows = server.Windows;
        }

        server.WindowsChanged += OnWindowsChanged;
    }

    /// <summary>
    /// Takes the run's state, as the run publishes it on every change and once a second, and brings the window back when
    /// the state and the setting call for it (see the class). Never throws: a window Windows will not start is logged as a
    /// warning, and the recording goes on.
    /// </summary>
    public void Publish(RecorderState state)
    {
        (string Reason, bool ForClose)? launch;
        string runId;
        lock (_gate)
        {
            if (_disposed || _runId is null) return;

            runId = _runId;
            var now = time.GetTimestamp();
            _phase = state.Phase;
            NoteTheWindowLocked(now);
            var setting = options.CurrentValue.ReopenWindow;
            if (setting == ReopenWindow.Never)
            {
                _wanted.Clear();
                return;
            }

            // A window shows the run: whoever is there sees what needs attention, so nothing is wanted; and what that
            // window has seen wants nothing later either. The run's last state is sent again once a second after the end,
            // and a repeat that lands once the windows have taken the end and gone must not open a window for it.
            if (server.Windows > 0)
            {
                _wanted.Clear();
                _closedAt = null;
                SeenByAWindowLocked(state);
                return;
            }

            WantLocked(state, now, setting);
            launch = ChooseLocked(now);
        }

        if (launch is { } wanted) Launch(runId, wanted.Reason, wanted.ForClose);
    }

    /// <summary>Stops following the windows; nothing opens a window after this.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }

        server.WindowsChanged -= OnWindowsChanged;
    }

    /// <summary>
    /// A window came or went: notes when the last one went away, unless it left with Esc, which is a choice made at the
    /// keyboard and not a window closed, and forgets that when one comes.
    /// </summary>
    private void OnWindowsChanged(object? sender, RecorderServer.WindowChange e)
    {
        var now = server.Windows;
        lock (_gate)
        {
            if (_disposed) return;

            if (now == 0 && _windows > 0) _closedAt = e.LeftWithEsc ? null : time.GetTimestamp();
            else if (now > 0) _closedAt = null;
            _windows = now;
        }
    }

    /// <summary>
    /// Notes the end of the window launched last, once it has ended: one brought back for a mere close and closed again
    /// within <see cref="CloseSnooze"/> snoozes closes for the rest of the run. Call with the lock held.
    /// </summary>
    private void NoteTheWindowLocked(long now)
    {
        if (_window is not { } window || window.ExitCode() is null) return;

        if (_windowForClose && time.GetElapsedTime(_windowLaunchedAt, now) < CloseSnooze)
        {
            _closesSnoozed = true;
            logger.LogInformation(
                "The ClassScribe window that came back was closed within {Minutes} minutes, so a closed window brings none back again in this run; something that needs attention still does",
                CloseSnooze.TotalMinutes);
        }

        _window = null;
        _windowForClose = false;
    }

    /// <summary>
    /// Notes what a window showed in <paramref name="state"/>: a failure, or an Alert, which wants no window once that
    /// window is gone, since whoever closed it had seen it. A new Alert still does. Call with the lock held.
    /// </summary>
    private void SeenByAWindowLocked(RecorderState state)
    {
        if (state.Phase == RecorderPhase.Failed) _failureSeen = true;
        if (state.Attention.Level == AttentionLevel.Alert) _lastAlertCount = Math.Max(_lastAlertCount, state.Attention.Count);
    }

    /// <summary>Adds what <paramref name="state"/> wants a window for, as <paramref name="setting"/> allows. Call with the lock held.</summary>
    private void WantLocked(RecorderState state, long now, ReopenWindow setting)
    {
        if (state.Phase == RecorderPhase.Failed && !_failureSeen)
        {
            _failureSeen = true;
            Want("this recording failed and no window shows it; the log says why", forClose: false);
        }

        var attention = state.Attention;
        if (attention.Level == AttentionLevel.Alert && attention.Count > _lastAlertCount)
        {
            _lastAlertCount = attention.Count;
            Want($"something needs attention and no window shows this recording: {attention.Text}", forClose: false);
        }

        if (attention.Level == AttentionLevel.Notice)
        {
            _noticeSince ??= now;
            if (!_noticeWanted && time.GetElapsedTime(_noticeSince.Value, now) >= NoticePatience)
            {
                _noticeWanted = true;
                Want($"something has needed a look for {NoticePatience.TotalMinutes} minutes and no window shows this recording: {attention.Text}", forClose: false);
            }
        }
        else
        {
            _noticeSince = null;
            _noticeWanted = false;
        }

        // The last window went away while the class records: after a moment, in case it comes straight back.
        if (_closedAt is not { } closedAt) return;

        var recording = _phase is RecorderPhase.Starting or RecorderPhase.Recording or RecorderPhase.Paused or RecorderPhase.NotRecording;
        if (!recording || setting != ReopenWindow.OnCloseAndProblems)
        {
            _closedAt = null;
        }
        else if (time.GetElapsedTime(closedAt, now) >= AfterClose)
        {
            _closedAt = null;
            if (!_closesSnoozed) Want("The window was closed; the recording goes on", forClose: true);
        }
    }

    /// <summary>Wants a window for <paramref name="reason"/>, unless one is wanted for it already. Call with the lock held.</summary>
    private void Want(string reason, bool forClose)
    {
        if (!_wanted.Exists(wanted => wanted.ForClose == forClose && wanted.Reason == reason)) _wanted.Add((reason, forClose));
    }

    /// <summary>
    /// The reason to launch a window for now, if one is wanted and the limits allow it: one window at a time, and at most
    /// one launch every <see cref="LaunchInterval"/>. A problem comes before a mere close. Call with the lock held.
    /// </summary>
    private (string Reason, bool ForClose)? ChooseLocked(long now)
    {
        if (_wanted.Count == 0) return null;
        if (_window is { } window && window.ExitCode() is null) return null;
        if (_lastLaunch is { } last && time.GetElapsedTime(last, now) < LaunchInterval) return null;

        var problem = _wanted.FindIndex(wanted => !wanted.ForClose);
        var chosen = _wanted[problem < 0 ? 0 : problem];
        _wanted.Clear();
        _lastLaunch = now;
        return chosen;
    }

    /// <summary>Logs <paramref name="reason"/>, then starts the window for run <paramref name="runId"/>, minimized; a start Windows refuses is logged as a warning.</summary>
    private void Launch(string runId, string reason, bool forClose)
    {
        if (forClose)
            logger.LogInformation("{Reason}. A ClassScribe window comes back, minimized, so that the recording is not forgotten; its keys do nothing until Enter is pressed in it", reason);
        else
            logger.LogInformation("Opening a ClassScribe window, minimized, because {Reason}; its keys do nothing until Enter is pressed in it", reason);

        StartedProcess started;
        try
        {
            started = starter.StartAlertWindow(runId);
        }
        catch (Win32Exception ex)
        {
            // Deliberately not rethrown: the window only makes the recording seen, which the log does too, and a start
            // Windows refuses must not end the class. It is tried again the next time something needs attention, once the
            // time between two launches has passed; the next start of ClassScribe reports the run's end in any case.
            logger.LogWarning("Could not open a ClassScribe window for this recording: {Reason}. The recording goes on", ex.Message);
            return;
        }

        lock (_gate)
        {
            _window = started;
            _windowLaunchedAt = time.GetTimestamp();
            _windowForClose = forClose;
        }

        logger.LogDebug("Opened a ClassScribe window for run {RunId} as process {ProcessId}", runId, started.ProcessId);
    }
}
