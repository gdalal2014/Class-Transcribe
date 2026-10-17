using Microsoft.Extensions.Logging;

namespace ClassScribe.Commands;

/// <summary>What the person at the keyboard has asked for: stop recording, or cancel the upload.</summary>
public interface IUserSignals
{
    /// <summary>Completes when the recording should stop.</summary>
    Task StopRequested { get; }

    /// <summary>Cancelled when Ctrl+C is pressed after the stop, such as during the upload.</summary>
    CancellationToken Cancellation { get; }

    /// <summary>Asks for the recording to stop.</summary>
    void RequestStop();

    /// <summary>
    /// Acts on a Ctrl+C: one pressed in this window, or one pressed in a window that shows the run and passes it on to the
    /// recorder. While recording, it stops the recording when it is the second within five seconds, as Q does; after the
    /// stop, it cancels whatever still runs, such as the upload.
    /// </summary>
    void CtrlCPressed();
}

/// <summary>
/// Turns Ctrl+C into a run's signals, whether it was pressed in this window or passed on to the recorder by a window that
/// shows the run. While recording, two presses within five seconds stop it, as Q pressed twice does, so that a Ctrl+C
/// pressed by mistake, or to get out of the window, does not end the class; after the stop, a press cancels whatever is
/// still running, such as the upload. Each run has its own instance. The recording keys (Q, P, M and V) are read by the
/// recording loop (see <see cref="RecordingLoop"/>; until the record command hands its loop over, also by
/// <see cref="RecordCommand"/>).
/// </summary>
public sealed class ConsoleUserSignals : IUserSignals, IDisposable
{
    /// <summary>How long after the Ctrl+C that stopped the recording another Ctrl+C still counts as part of that stop.</summary>
    private static readonly TimeSpan RepeatAllowance = TimeSpan.FromSeconds(2);

    private readonly TaskCompletionSource _stop = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Lock _gate = new();
    private readonly TimeProvider _time;
    private readonly ILogger<ConsoleUserSignals> _logger;

    /// <summary>
    /// When a Ctrl+C pressed while recording came, on the steady clock (<see cref="TimeProvider.GetTimestamp"/>), while it
    /// waits for its second; null when none waits.
    /// </summary>
    private long? _firstPress;

    /// <summary>When the Ctrl+C that stopped the recording came, on the steady clock (<see cref="TimeProvider.GetTimestamp"/>), or null when the stop came another way.</summary>
    private long? _stoppedByCtrlC;

    /// <summary>
    /// True once disposed, as the run's scope ends: a press that still comes, such as one a window passes on to the recorder
    /// while the run ends, does nothing.
    /// </summary>
    private bool _disposed;

    /// <summary>
    /// How many presses are cancelling at this moment, which they do outside the lock, so that the cancel's callbacks never
    /// run under it. The token source is disposed only once none is, never under a cancel.
    /// </summary>
    private int _cancelling;

    /// <summary>Hooks Ctrl+C.</summary>
    public ConsoleUserSignals(TimeProvider time, ILogger<ConsoleUserSignals> logger)
    {
        _time = time;
        _logger = logger;
        Console.CancelKeyPress += OnCancelKeyPress;
    }

    /// <inheritdoc />
    public Task StopRequested => _stop.Task;

    /// <inheritdoc />
    public CancellationToken Cancellation => _cancellation.Token;

    /// <inheritdoc />
    public void RequestStop() => _stop.TrySetResult();

    /// <summary>
    /// Acts on a Ctrl+C. While recording, the first press only asks for a second, and a second within
    /// <see cref="RecordingLoop.SecondPressWithin"/> of it stops the recording; a press later than that is a first press
    /// again. After the stop, a press cancels whatever is still running, except one that comes within two seconds of the
    /// Ctrl+C that stopped the recording, which a double press, or a key held down, gives: that is one stop rather than a
    /// stop and a cancel. A second press within the five seconds of a first one made while recording also belongs to the
    /// stop when the recording stopped another way in between, as when Q was pressed twice meanwhile. All of it is timed on
    /// the steady clock (<see cref="TimeProvider.GetTimestamp"/>), so Windows setting its time of day neither stretches nor
    /// shortens a wait. After a stop asked for in another way (Q, or a command with nothing to record), the first Ctrl+C
    /// cancels at once. Once the signals are disposed, a press does nothing.
    /// </summary>
    public void CtrlCPressed()
    {
        bool cancel;
        lock (_gate)
        {
            if (_disposed) return;

            var now = _time.GetTimestamp();
            if (_firstPress is { } first && _time.GetElapsedTime(first, now) <= RecordingLoop.SecondPressWithin)
            {
                // The second press of a pair stops the recording, or belongs to a stop that came another way meanwhile.
                _firstPress = null;
                _stoppedByCtrlC = now;
                _stop.TrySetResult();
                return;
            }

            if (_stoppedByCtrlC is { } stopped && _time.GetElapsedTime(stopped, now) < RepeatAllowance) return;

            cancel = _stop.Task.IsCompleted;
            _firstPress = cancel ? null : now;
            if (cancel) _cancelling++;
        }

        if (!cancel)
        {
            _logger.LogInformation("Press Ctrl+C again within {Seconds} seconds to stop", RecordingLoop.SecondPressWithin.TotalSeconds);
            return;
        }

        try
        {
            _cancellation.Cancel();
        }
        finally
        {
            // The last press to finish cancelling disposes the token source when the signals were disposed meanwhile.
            lock (_gate)
            {
                if (--_cancelling == 0 && _disposed) _cancellation.Dispose();
            }
        }
    }

    /// <summary>
    /// Unhooks Ctrl+C; a press that still comes does nothing from now on. The token source is disposed now, or, when a
    /// press is cancelling it at this moment, by that press as it finishes.
    /// </summary>
    public void Dispose()
    {
        Console.CancelKeyPress -= OnCancelKeyPress;
        lock (_gate)
        {
            if (_disposed) return;

            _disposed = true;
            if (_cancelling == 0) _cancellation.Dispose();
        }
    }

    /// <summary>Keeps the process alive on Ctrl+C and turns the key press into a stop or a cancel.</summary>
    private void OnCancelKeyPress(object? sender, ConsoleCancelEventArgs e)
    {
        e.Cancel = true;
        CtrlCPressed();
    }
}
