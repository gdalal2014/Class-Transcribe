using System.ComponentModel;
using System.Text.RegularExpressions;
using ClassScribe.Desktop;
using Microsoft.Extensions.Logging;

namespace ClassScribe.YouTube;

/// <summary>A video's window as the batch opened it: Chrome's browser process, the root of the tree a video's run records.</summary>
/// <param name="Process">The process, with its id, its start time and its exit code once it has ended.</param>
public sealed record OpenedWindow(StartedProcess Process);

/// <summary>What opening a video's window came to: the window, or why there is none.</summary>
/// <param name="Window">The window; null when it did not open.</param>
/// <param name="Problem">Why it did not open, in words Gulzar reads; null when it did.</param>
/// <param name="HandedOff">True when Chrome handed the video to a window of ClassScribe's left open from before: Enter tries again once it is closed.</param>
public sealed record WindowOpening(OpenedWindow? Window, string? Problem, bool HandedOff = false);

/// <summary>
/// The titles of a video's window (design 4.1), as the run reads them to see the video end: YouTube writes the video's title,
/// with "(3) " before it when there are notifications and " - YouTube" after it, and Chrome adds " - Google Chrome" to an
/// ordinary window's; what is left is the video's title. YouTube alone, as between two videos, is no title.
/// </summary>
public static partial class VideoTitles
{
    /// <summary>The video's title in a window's <paramref name="raw"/> title; null for none.</summary>
    public static string? Clean(string? raw)
    {
        var title = (raw ?? "").Trim();
        title = Notifications().Replace(title, "");
        foreach (var ending in new[] { " - Google Chrome", " - YouTube" })
        {
            if (title.EndsWith(ending, StringComparison.Ordinal)) title = title[..^ending.Length].TrimEnd();
        }

        return title.Length == 0 || title.Equals("YouTube", StringComparison.Ordinal) ? null : title;
    }

    /// <summary>The first title of <paramref name="titles"/> that names a video; null for none.</summary>
    public static string? First(IEnumerable<string> titles) => titles.Select(Clean).FirstOrDefault(title => title is not null);

    /// <summary>YouTube's count of notifications before a title, such as "(3) ".</summary>
    [GeneratedRegex(@"^\([0-9]{1,4}\+?\)\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Notifications();
}

/// <summary>
/// Opens and closes a video's window for the record path's batch (design 4.1 and 4.2), in the batch's window, so that a hidden
/// process never starts a visible one: ClassScribe's own Chrome, with a profile of its own in &lt;RuntimeFolder&gt;\video-browser
/// (which Chrome makes on first use and ClassScribe never reads), on the video's watch page in an --app window, without tabs
/// or an address bar, and without any switch that would make it play by itself: Gulzar presses play. When a window of that
/// profile is already open, a new chrome.exe hands it the video and ends, so the process started must still run 3 seconds
/// later, or the video went to that window. The close is WM_CLOSE posted to the window, never a kill, then up to 15
/// seconds for Chrome to end, on the steady clock.
/// </summary>
public sealed class VideoWindow(IProcessStarter starter, IVideoWindows windows, YouTubeFolders folders, TimeProvider time, ILogger<VideoWindow> logger)
{
    /// <summary>How long after the start Chrome must still run, or it handed the video to a window left open.</summary>
    public static readonly TimeSpan HandOffCheckAfter = TimeSpan.FromSeconds(3);

    /// <summary>How long a window asked to close has to end.</summary>
    public static readonly TimeSpan CloseWithin = TimeSpan.FromSeconds(15);

    /// <summary>How often a window asked to close is looked at, while it has not ended.</summary>
    private static readonly TimeSpan ClosePoll = TimeSpan.FromMilliseconds(250);

    /// <summary>What the batch says when Chrome handed the video to a window of ClassScribe's left open from before.</summary>
    public const string HandedOff =
        "ClassScribe's video window from an earlier run is still open, so Chrome sent the video there. Close every ClassScribe video window, then press Enter to try again";

    /// <summary>
    /// Chrome's arguments for the video at <paramref name="link"/> with the profile in <paramref name="profile"/>: the profile;
    /// no first-run pages and no question about the default browser, which a fresh profile would show; and one window on the
    /// link, without tabs or an address bar. Never a switch that makes it play by itself.
    /// </summary>
    public static IReadOnlyList<string> Arguments(string profile, string link) =>
        [$"--user-data-dir={profile}", "--no-first-run", "--no-default-browser-check", $"--app={link}"];

    /// <summary>
    /// Opens <paramref name="link"/> in ClassScribe's own Chrome at <paramref name="chrome"/>, and returns the window once it has
    /// stood 3 seconds; or why it did not open: Windows would not start Chrome, or Chrome handed the video to a window left
    /// open (<see cref="HandedOff"/>). Chrome runs in the feature's folder, never in its profile's, which ClassScribe never
    /// makes or touches.
    /// </summary>
    public async Task<WindowOpening> OpenAsync(string chrome, string link, CancellationToken cancellationToken)
    {
        StartedProcess process;
        try
        {
            Directory.CreateDirectory(folders.Root);
            process = starter.StartBrowser(chrome, Arguments(folders.VideoBrowser, link), folders.Root);
        }
        catch (Win32Exception ex)
        {
            // Deliberately not rethrown: a window that would not open is said, and is this video's; two in a row stop the
            // batch, which says what is wrong with Chrome.
            logger.LogDebug(ex, "Chrome did not start for {Link}", link);
            return new WindowOpening(null, $"The video window did not open: {ex.Message}");
        }

        await Task.Delay(HandOffCheckAfter, time, cancellationToken);
        if (process.ExitCode() is { } exitCode)
        {
            logger.LogDebug("Chrome (process {ProcessId}) ended with exit code {ExitCode} within {Seconds} seconds: it handed {Link} to a window already open",
                process.ProcessId, exitCode, (int)HandOffCheckAfter.TotalSeconds, link);
            return new WindowOpening(null, HandedOff, HandedOff: true);
        }

        logger.LogDebug("The video window is open: Chrome, process {ProcessId}, on {Link}", process.ProcessId, link);
        return new WindowOpening(new OpenedWindow(process), null);
    }

    /// <summary>
    /// Closes <paramref name="window"/>: WM_CLOSE posted to it, never a kill, then up to 15 seconds on the steady clock for
    /// Chrome to end. True once it has ended, at once when it had already; false when it is still open after the 15 seconds.
    /// </summary>
    public async Task<bool> CloseAsync(OpenedWindow window, CancellationToken cancellationToken)
    {
        var process = window.Process;
        if (process.ExitCode() is not null) return true;

        var asked = windows.Close(process.ProcessId);
        logger.LogDebug("Asked {Count} window(s) of Chrome, process {ProcessId}, to close", asked, process.ProcessId);
        var since = time.GetTimestamp();
        while (process.ExitCode() is null)
        {
            if (time.GetElapsedTime(since) >= CloseWithin)
            {
                logger.LogDebug("Chrome, process {ProcessId}, is still open {Seconds} seconds after it was asked to close", process.ProcessId, (int)CloseWithin.TotalSeconds);
                return false;
            }

            await Task.Delay(ClosePoll, time, cancellationToken);
        }

        return true;
    }
}
