namespace ClassScribe.YouTube;

/// <summary>
/// ClassScribe's own Chrome window for a video (architecture 4.3; design 4.1 and 4.2), opened and closed in the batch's
/// window: a profile of its own, never read; the video's watch page in an --app window; never a switch that makes it play
/// by itself; the close a WM_CLOSE, never a kill.
/// </summary>
public interface IVideoWindowOpener
{
    /// <summary>
    /// Opens <paramref name="link"/> in ClassScribe's own Chrome at <paramref name="chrome"/>, and returns the window once it has
    /// stood 3 seconds; or why it did not open: Windows would not start Chrome, or Chrome handed the video to a window left
    /// open, in which case Enter tries again.
    /// </summary>
    Task<WindowOpening> OpenAsync(string chrome, string link, CancellationToken cancellationToken);

    /// <summary>
    /// Closes <paramref name="window"/>: WM_CLOSE posted to it, never a kill, then up to 15 seconds on the steady clock for
    /// Chrome to end. True once it has ended, at once when it had already; false when it is still open after the 15 seconds.
    /// </summary>
    Task<bool> CloseAsync(OpenedWindow window, CancellationToken cancellationToken);
}
