using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using ClassScribe.YouTube;

namespace ClassScribe.Desktop.Windows;

/// <summary>
/// The windows of ClassScribe's own Chrome for a YouTube video, from Windows itself (see <see cref="IVideoWindows"/>): the
/// visible top-level windows a process made (EnumWindows, GetWindowThreadProcessId) of Chrome's own window class,
/// Chrome_WidgetWin_1, their titles (GetWindowTextW), and WM_CLOSE posted to them, as a click on the close button is: never a
/// kill. It reads and asks nothing of the page in a window. In a test run it refuses everything, naming the stand-in to use
/// (see <see cref="YouTubeGuards"/>), so that no test lists, reads or closes another process's windows.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ChromeWindows : IVideoWindows
{
    /// <summary>The window class of Chrome's own windows, its browser windows and --app windows among them.</summary>
    public const string ChromeWindowClass = "Chrome_WidgetWin_1";

    /// <summary>WM_CLOSE: a window is asked to close, as its close button asks.</summary>
    private const uint CloseMessage = 0x0010;

    /// <summary>The longest title read, in characters; a longer one is cut there.</summary>
    private const int LongestTitle = 512;

    /// <inheritdoc />
    public IReadOnlyList<string> TitlesOf(int processId)
    {
        YouTubeGuards.RefuseInTests("the real windows of Chrome", "FakeVideoWindows");
        return [.. WindowsOf(processId).Select(TitleOf)];
    }

    /// <inheritdoc />
    public int Close(int processId)
    {
        YouTubeGuards.RefuseInTests("closing the real windows of Chrome", "FakeVideoWindows");
        var asked = 0;
        foreach (var window in WindowsOf(processId))
        {
            if (PostMessageW(window, CloseMessage, 0, 0)) asked++;
        }

        return asked;
    }

    /// <summary>The visible top-level windows of Chrome's class that <paramref name="processId"/> made, from the front to the back.</summary>
    private static List<IntPtr> WindowsOf(int processId)
    {
        List<IntPtr> windows = [];
        var className = new StringBuilder(64);
        EnumWindows((window, _) =>
        {
            GetWindowThreadProcessId(window, out var owner);
            if (owner != processId || !IsWindowVisible(window)) return true;

            className.Clear();
            if (GetClassNameW(window, className, className.Capacity) > 0 && className.ToString() == ChromeWindowClass) windows.Add(window);
            return true;
        }, 0);

        return windows;
    }

    /// <summary>The title of <paramref name="window"/>, as Windows gives it; empty for none.</summary>
    private static string TitleOf(IntPtr window)
    {
        var title = new StringBuilder(LongestTitle + 1);
        return GetWindowTextW(window, title, title.Capacity) > 0 ? title.ToString() : "";
    }

    /// <summary>Called for each top-level window; returns false to stop.</summary>
    private delegate bool WindowCallback(IntPtr window, IntPtr parameter);

    /// <summary>Calls <paramref name="callback"/> for each top-level window.</summary>
    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(WindowCallback callback, IntPtr parameter);

    /// <summary>The thread and process that made a window.</summary>
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out int processId);

    /// <summary>Whether a window is visible.</summary>
    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);

    /// <summary>A window's class name.</summary>
    [DllImport("user32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(IntPtr window, StringBuilder className, int length);

    /// <summary>A window's title.</summary>
    [DllImport("user32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(IntPtr window, StringBuilder text, int length);

    /// <summary>Posts a message to a window's queue, without waiting for it to be handled.</summary>
    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessageW(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
}
