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

    /// <summary>Starts watching the keyboard for the stop key.</summary>
    void ListenForStopKey();
}

/// <summary>
/// Reads the signals from the console. Q or the first Ctrl+C stops the recording; a later Ctrl+C
/// cancels whatever is still running, such as the upload.
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

    /// <inheritdoc />
    public void ListenForStopKey()
    {
        if (Console.IsInputRedirected) return;

        _ = Task.Run(async () =>
        {
            while (!_stop.Task.IsCompleted)
            {
                if (Console.KeyAvailable && Console.ReadKey(intercept: true).Key == ConsoleKey.Q)
                    RequestStop();
                else
                    await Task.Delay(150);
            }
        });
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
        if (!_stop.TrySetResult()) _cancellation.Cancel();
    }
}
