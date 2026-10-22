using ClassScribe.Commands;
using ClassScribe.Desktop;
using ClassScribe.Recorder;
using Microsoft.Extensions.Logging;

namespace ClassScribe.YouTube;

/// <summary>
/// The video window a YouTube video's run records (design 4.3 and 4.4), one per run, in the recorder or in the window alike:
/// the browser process the batch started, from the run's request, by its id and start time, so that a later process given the
/// same id is never taken for it; whether it still runs; the tree of processes it heads, whose sound the run records through
/// process loopback; the video's title, as its window shows it; and the close, posted to its window, never a kill. For an
/// ordinary run of the class it says which processes belong to a live video window of ClassScribe's, which a recording of
/// Chrome leaves out, so that a class never records a video window left open. It never reads anything in the window's page,
/// nor its profile.
/// </summary>
public sealed class VideoPlayback(IProcessLiveness liveness, IVideoWindows windows, IProcessFamily family, RunJournal journal, TimeProvider time, ILogger<VideoPlayback> logger)
{
    /// <summary>How long the video windows of other runs, which a class leaves out, are taken from the run files before they are read again.</summary>
    public static readonly TimeSpan OtherWindowsReadEvery = TimeSpan.FromSeconds(10);

    /// <summary>The name of what a video's run records, for the log and the status line.</summary>
    public const string WindowName = "ClassScribe's video window";

    /// <summary>The live video windows of the runs, as last read, and when, on the steady clock; null before the first read.</summary>
    private (IReadOnlyList<(int Id, DateTimeOffset Started)> Roots, long At)? _otherWindows;

    /// <summary>The video the run records, from its request; null for a class, and before <see cref="Begin"/>.</summary>
    public VideoRequest? Video { get; private set; }

    /// <summary>True for a run that records a video's window: its request names the window's browser process.</summary>
    public bool IsVideo => Video is { BrowserProcessId: not null, BrowserStartedUtc: not null };

    /// <summary>
    /// Begins following <paramref name="video"/>'s window, as the run's request names it, and says whether it still runs: false
    /// when it does not, or the request names no window, and the run then records nothing (it fails closed).
    /// </summary>
    public bool Begin(VideoRequest video)
    {
        Video = video;
        var open = IsOpen();
        logger.LogDebug("The video window of \"{Title}\": process {ProcessId}, {State}", video.Title, video.BrowserProcessId, open ? "open" : "not open");
        return open;
    }

    /// <summary>True while the window's browser process runs, the very one the batch started.</summary>
    public bool IsOpen() => Root() is { } root && liveness.IsAlive(root.Id, root.Started);

    /// <summary>The processes of the window's tree: its browser process and every process it started; empty when it does not run.</summary>
    public IReadOnlySet<int> Tree() => Root() is { } root && liveness.IsAlive(root.Id, root.Started) ? family.TreeOf(root.Id, root.Started) : new HashSet<int>();

    /// <summary>The window's browser process, the root of the tree a video's run records; null for a class.</summary>
    public int? RootProcessId => Video?.BrowserProcessId;

    /// <summary>
    /// The video's title as the window showed it while the video played, once it had stood with sound heard (see
    /// <see cref="Commands.VideoEndWatch"/>); null until then. The video's source file takes it over the title read before.
    /// </summary>
    public string? PlayedTitle { get; private set; }

    /// <summary>Notes <paramref name="title"/> as the video's, as the window showed it while the video played.</summary>
    public void Played(string title) => PlayedTitle = title;

    /// <summary>The video's title, as the window shows it now, cleaned (see <see cref="VideoTitles"/>); null when it shows none.</summary>
    public string? Title() => Root() is { } root ? VideoTitles.First(windows.TitlesOf(root.Id)) : null;

    /// <summary>Asks the window to close, as its close button does, while it is open; never waits, and never kills it.</summary>
    public void Close()
    {
        if (Root() is not { } root || !liveness.IsAlive(root.Id, root.Started)) return;

        var asked = windows.Close(root.Id);
        logger.LogDebug("Asked the video window (process {ProcessId}) to close: {Count} window(s)", root.Id, asked);
    }

    /// <summary>
    /// The processes of every live video window of ClassScribe's, as the run files name them, for an ordinary recording of
    /// Chrome to leave out; empty when there is none, which needs no look at Windows' processes. The run files are read at most
    /// every 10 seconds.
    /// </summary>
    public IReadOnlySet<int> VideoWindowsProcesses()
    {
        var processes = new HashSet<int>();
        foreach (var root in OtherWindows()) processes.UnionWith(family.TreeOf(root.Id, root.Started));
        return processes;
    }

    /// <summary>The window's browser process and its start time; null for a class.</summary>
    private (int Id, DateTimeOffset Started)? Root() =>
        Video is { BrowserProcessId: { } id, BrowserStartedUtc: { } started } ? (id, started) : null;

    /// <summary>The live video windows the run files name, read again once <see cref="OtherWindowsReadEvery"/> has passed.</summary>
    private IReadOnlyList<(int Id, DateTimeOffset Started)> OtherWindows()
    {
        var now = time.GetTimestamp();
        if (_otherWindows is { } read && time.GetElapsedTime(read.At, now) < OtherWindowsReadEvery) return read.Roots;

        List<(int Id, DateTimeOffset Started)> roots = [];
        foreach (var run in journal.List())
        {
            if (run.Record.Request.Video is { BrowserProcessId: { } id, BrowserStartedUtc: { } started } && liveness.IsAlive(id, started) && !roots.Contains((id, started)))
                roots.Add((id, started));
        }

        _otherWindows = (roots, now);
        return roots;
    }
}
