namespace ClassScribe.Desktop;

/// <summary>
/// The windows of ClassScribe's own Chrome for a YouTube video (design 4.1): the titles of a process's visible top-level
/// windows of Chrome's own window class, and the request to close them, posted as a click on the close button is, never a
/// kill, so that Chrome ends cleanly and never offers to restore its pages. Behind an interface so that no test lists, reads or
/// closes another process's windows; the real one is <see cref="Windows.ChromeWindows"/>. Nothing in a window's page is ever
/// read or clicked.
/// </summary>
public interface IVideoWindows
{
    /// <summary>The titles of the visible top-level windows of Chrome's class that <paramref name="processId"/> shows, as Windows gives them; empty when it shows none.</summary>
    IReadOnlyList<string> TitlesOf(int processId);

    /// <summary>Asks each visible top-level window of Chrome's class that <paramref name="processId"/> shows to close (WM_CLOSE, posted); returns how many were asked.</summary>
    int Close(int processId);
}
