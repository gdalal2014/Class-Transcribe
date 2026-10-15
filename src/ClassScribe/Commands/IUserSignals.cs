namespace ClassScribe.Commands;

/// <summary>What the person at the keyboard has asked for: stop recording, or cancel the upload.</summary>
public interface IUserSignals
{
    /// <summary>Completes when the recording should stop.</summary>
    Task StopRequested { get; }

    /// <summary>Cancelled when Ctrl+C is pressed after a stop was already requested.</summary>
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
    private readonly TaskCompletionSource _stop = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _cancellation = new();

    /// <summary>Hooks Ctrl+C.</summary>
    public ConsoleUserSignals() => Console.CancelKeyPress += OnCancelKeyPress;

    /// <inheritdoc />
    public Task StopRequested => _stop.Task;

    /// <inheritdoc />
    public CancellationToken Cancellation => _cancellation.Token;

    /// <inheritdoc />
    public void RequestStop() => _stop.TrySetResult();

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
        if (!_stop.TrySetResult()) _cancellation.Cancel();
    }
}
