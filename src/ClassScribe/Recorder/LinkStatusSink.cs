using ClassScribe.Link;
using ClassScribe.Runs;
using Microsoft.Extensions.Logging;

namespace ClassScribe.Recorder;

/// <summary>
/// Where a run in a recorder publishes its state: to the windows that show the run, through the server, and to the alerts
/// that make the run seen when no window does. The windows take a state as the sign that the recorder answers, and report
/// silence after 10 seconds without one; the run publishes once a second while it records, but only at each change after
/// the stop, where an upload may take half an hour. So the last state is sent again once a second while no new one comes,
/// until the run is over, which also gives the alerts their once-a-second look at the clock. The state that says the run
/// is over is sent once: the end follows within moments, and a failure sent again after the windows have taken the end
/// and gone would look to the alerts like one nobody saw. One per run.
/// </summary>
/// <param name="server">The windows' end.</param>
/// <param name="alerts">What brings a window back when none shows the run.</param>
/// <param name="time">The clock the repeat is timed on.</param>
/// <param name="logger">Where a repeat that fails is noted.</param>
public sealed class LinkStatusSink(RecorderServer server, UnattendedAlerts alerts, TimeProvider time, ILogger<LinkStatusSink> logger) : IRecorderStatusSink, IDisposable
{
    /// <summary>How long after the last state the same state is sent again, while no new one comes.</summary>
    public static readonly TimeSpan RepeatAfter = TimeSpan.FromSeconds(1);

    private readonly Lock _gate = new();

    /// <summary>The state published last; null before the first.</summary>
    private RecorderState? _last;

    /// <summary>When it was published, on the steady clock.</summary>
    private long _lastAt;

    /// <summary>The timer that repeats the last state; null until the first state, and once disposed.</summary>
    private ITimer? _timer;

    /// <summary>True once disposed: nothing is repeated any more.</summary>
    private bool _disposed;

    /// <summary>True while repeating keeps failing, so that a failure is logged once.</summary>
    private bool _failing;

    /// <inheritdoc />
    public void Publish(RecorderState state)
    {
        ITimer? finished = null;
        lock (_gate)
        {
            _last = state;
            _lastAt = time.GetTimestamp();
            if (IsOver(state))
            {
                finished = _timer;
                _timer = null;
            }
            else if (!_disposed)
            {
                _timer ??= time.CreateTimer(_ => Repeat(), null, RepeatAfter, RepeatAfter);
            }
        }

        finished?.Dispose();
        PassOn(state);
    }

    /// <summary>Stops repeating the last state, as the run's scope ends.</summary>
    public void Dispose()
    {
        ITimer? timer;
        lock (_gate)
        {
            if (_disposed) return;

            _disposed = true;
            timer = _timer;
            _timer = null;
        }

        timer?.Dispose();
    }

    /// <summary>True when <paramref name="state"/> says the run is over: done, failed or cancelled, after which no state comes and none is sent again.</summary>
    private static bool IsOver(RecorderState state) => state.Phase is RecorderPhase.Done or RecorderPhase.Failed or RecorderPhase.Cancelled;

    /// <summary>Sends the last state again, when no new one has come for <see cref="RepeatAfter"/> and the run goes on. Runs on the timer's thread.</summary>
    private void Repeat()
    {
        RecorderState? state;
        lock (_gate)
        {
            if (_disposed || _last is null || IsOver(_last) || time.GetElapsedTime(_lastAt) < RepeatAfter) return;
            state = _last;
        }

        try
        {
            PassOn(state);
            _failing = false;
        }
        catch (Exception ex)
        {
            // Deliberately not rethrown: this runs on a timer's thread, where an exception nobody catches ends the process,
            // and the recording with it. Repeating a state only tells the windows that the recorder lives, which the next
            // repeat tries again; the failure is logged once while it lasts.
            if (!_failing) logger.LogError(ex, "Could not send the run's state to the windows again; the next second tries again");
            _failing = true;
        }
    }

    /// <summary>Passes <paramref name="state"/> to the windows and to the alerts.</summary>
    private void PassOn(RecorderState state)
    {
        server.SetState(state);
        alerts.Publish(state);
    }
}
