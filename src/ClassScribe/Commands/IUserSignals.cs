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
