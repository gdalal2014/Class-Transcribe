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
}

/// <summary>
/// Turns Ctrl+C into a run's signals: the first Ctrl+C stops the recording, a later one cancels
/// whatever is still running, such as the upload. Each run has its own instance. The recording keys
/// (Q, P and M) are read by <see cref="RecordCommand"/>.
/// </summary>
public sealed class ConsoleUserSignals : IUserSignals, IDisposable
{
    /// <summary>How long after the Ctrl+C that stopped the recording another Ctrl+C still counts as part of that stop.</summary>
    private static readonly TimeSpan RepeatAllowance = TimeSpan.FromSeconds(2);

    private readonly TaskCompletionSource _stop = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Lock _gate = new();
    private readonly TimeProvider _time;

    /// <summary>When the Ctrl+C that stopped the recording came, on the steady clock (<see cref="TimeProvider.GetTimestamp"/>), or null when the stop came another way.</summary>
    private long? _stoppedByCtrlC;

    /// <summary>Hooks Ctrl+C.</summary>
    public ConsoleUserSignals(TimeProvider time)
    {
        _time = time;
        Console.CancelKeyPress += OnCancelKeyPress;
    }

    /// <inheritdoc />
    public Task StopRequested => _stop.Task;

    /// <inheritdoc />
    public CancellationToken Cancellation => _cancellation.Token;

    /// <inheritdoc />
    public void RequestStop() => _stop.TrySetResult();

    /// <summary>
    /// Acts on a Ctrl+C. The first one stops the recording; a later one cancels whatever is still
    /// running, unless it comes within two seconds of the Ctrl+C that stopped the recording: a double
    /// press, or a key held down, is one stop rather than a stop and a cancel. The two seconds are timed on
    /// the steady clock (<see cref="TimeProvider.GetTimestamp"/>), so Windows setting its time of day neither
    /// stretches nor shortens them. After a stop asked for in another way (Q, or a command with nothing to
    /// record), the first Ctrl+C cancels at once.
    /// </summary>
    public void CtrlCPressed()
    {
        lock (_gate)
        {
            var now = _time.GetTimestamp();
            if (_stop.TrySetResult())
            {
                _stoppedByCtrlC = now;
                return;
            }

            if (_stoppedByCtrlC is { } stopped && _time.GetElapsedTime(stopped, now) < RepeatAllowance) return;
        }

        _cancellation.Cancel();
    }

    /// <summary>Unhooks Ctrl+C.</summary>
    public void Dispose()
    {
        Console.CancelKeyPress -= OnCancelKeyPress;
        _cancellation.Dispose();
    }

    /// <summary>Keeps the process alive on Ctrl+C and turns the key press into a stop or a cancel.</summary>
    private void OnCancelKeyPress(object? sender, ConsoleCancelEventArgs e)
    {
        e.Cancel = true;
        CtrlCPressed();
    }
}
