using ClassScribe.Settings;
using ClassScribe.YouTube;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClassScribe.Commands;

/// <summary>
/// The youtube command and menu choice Y's batch (design 1), and youtube check, in the window, each in a scope of its own.
/// A batch records its videos as they play (see <see cref="VideoBatch"/>), where Ctrl+C pressed twice stops the video in hand
/// and the batch, as it stops a class; with --download it downloads them (see <see cref="DownloadBatch"/>), where nothing
/// records here, so the first Ctrl+C cancels at once.
/// </summary>
/// <param name="download">The download path's batch.</param>
/// <param name="check">yt-dlp's check.</param>
/// <param name="signals">Ctrl+C, in this command's scope.</param>
/// <param name="ui">The console.</param>
/// <param name="youTube">The YouTube settings of the command's scope.</param>
/// <param name="logger">Where the command says what it refuses.</param>
/// <param name="record">The record path's batch; null, as for the download path's tests, when the record path is not given.</param>
public sealed class YouTubeCommand(DownloadBatch download, YtDlpCheck check, IUserSignals signals, IConsoleUi ui, IOptionsSnapshot<YouTubeOptions> youTube,
    ILogger<YouTubeCommand> logger, VideoBatch? record = null)
{
    /// <summary>Why --allow-unlisted is refused without --download: recording a video as it plays needs no word, and downloads nothing.</summary>
    public const string AllowUnlistedNeedsDownload = "--allow-unlisted is for --download: recording videos as they play needs no word about them, and downloads nothing";

    /// <summary>
    /// Why --allow-unlisted is refused while YouTube:CourseChannels is empty: course pages embed unlisted videos, and the flag
    /// must never take one for a video he may download.
    /// </summary>
    public const string AllowUnlistedNeedsCourseChannels =
        "--allow-unlisted needs your course's channels: list them in YouTube:CourseChannels first, so that a course video is never taken for one you may download";

    /// <summary>The exit code of a mistake on the command line.</summary>
    private const int Mistake = 2;

    /// <summary>
    /// Runs the batch <paramref name="request"/> describes and returns the exit code: recorded as the videos play, or downloaded
    /// with --download; a download's cancel is thrown, as Ctrl+C gives it. --allow-unlisted is refused without --download, and
    /// while YouTube:CourseChannels is empty.
    /// </summary>
    public async Task<int> RunAsync(YouTubeRequest request)
    {
        if (request.AllowUnlisted && !request.Download)
        {
            logger.LogError("{Problem}", AllowUnlistedNeedsDownload);
            return Mistake;
        }

        if (request.AllowUnlisted && youTube.Value.CourseChannels.Length == 0)
        {
            logger.LogError("{Problem}", AllowUnlistedNeedsCourseChannels);
            return Mistake;
        }

        if (!request.Download)
        {
            if (record is not null) return await record.RunAsync(request);

            logger.LogError("Recording YouTube videos as they play is not part of this build; add --download to download them with yt-dlp");
            return Mistake;
        }

        signals.RequestStop();
        return await download.RunAsync(request, signals.Cancellation);
    }

    /// <summary>
    /// youtube check: where yt-dlp, Deno, FFmpeg and FFprobe are, and their versions, what is missing with the winget command
    /// that installs it, and whether yt-dlp is more than 90 days old. Asks nothing of the network. Exit code 0 when everything
    /// is in place and yt-dlp is less than 90 days old, 1 otherwise.
    /// </summary>
    public async Task<int> CheckAsync()
    {
        signals.RequestStop();
        var found = await check.RunAsync(signals.Cancellation);
        foreach (var line in found.Lines) ui.WriteLine(line);
        foreach (var problem in found.Problems) logger.LogError("{Problem}", problem);
        if (found.Warning is { } old) logger.LogWarning("{Warning}", old);
        if (found.Problems.Count > 0 || found.Warning is not null) return 1;

        ui.WriteLine("yt-dlp and its helpers are in place.");
        return 0;
    }
}
