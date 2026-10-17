using ClassScribe.Commands;

namespace ClassScribe.Desktop;

/// <summary>
/// Shows each state a run publishes on a window (design 1.13): the title and the taskbar button as
/// <see cref="TitleText"/> gives them, and a flash for each new Alert. It serves a run recording in this window; a
/// window that shows a run recording in the background can pass the states it receives through one as well, so that
/// both look and flash alike.
/// </summary>
/// <remarks>
/// <para>The window flashes for an Alert whose <see cref="Attention.Count"/> is beyond that of the last Alert it flashed
/// for in the run: once for each new one, not once for each state that repeats it every second. A Notice that becomes an
/// Alert flashes as it becomes one, whether or not its count grew; an Alert that comes back with the count it had does
/// not flash again.</para>
/// <para>A Notice never flashes: the title notes it and the taskbar button turns yellow (design section 4), so that the
/// silence at every break in a class does not flash the window each time (4.1, row 8).</para>
/// <para>A window that starts showing a run part way through flashes only for an Alert that is on as it comes, and a
/// count that falls back is the next run, which counts from nothing. A run that fails flashes once as it fails, as a
/// failure always needs a look.</para>
/// </remarks>
public sealed class WindowStatusSink(IWindowSignals window) : IRecorderStatusSink
{
    private readonly Lock _gate = new();

    /// <summary>The attention count of the state shown last; a count lower than this is the next run's.</summary>
    private int _attentionCount;

    /// <summary>The count of the last Alert the window flashed for in this run; 0 before the first.</summary>
    private int _alertFlashed;

    /// <summary>The phase of the state shown last, or null before the first.</summary>
    private RecorderPhase? _phase;

    /// <inheritdoc />
    public void Publish(RecorderState state)
    {
        lock (_gate)
        {
            window.SetTitle(TitleText.For(state));
            window.SetTaskbar(TitleText.TaskbarFor(state), TitleText.TaskbarPercentFor(state));

            var attention = TitleText.AttentionOf(state);
            if (attention.Count < _attentionCount) _alertFlashed = 0;
            _attentionCount = attention.Count;

            var newAlert = attention.Level == AttentionLevel.Alert && attention.Count > _alertFlashed;
            if (newAlert) _alertFlashed = attention.Count;

            var newFailure = state.Phase == RecorderPhase.Failed && _phase is not RecorderPhase.Failed;
            _phase = state.Phase;

            if (newAlert || newFailure) window.Flash();
        }
    }
}
