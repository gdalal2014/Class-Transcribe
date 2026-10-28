using System.ComponentModel;
using System.Globalization;
using ClassScribe.Commands;
using ClassScribe.Drive;
using ClassScribe.Recorder;
using ClassScribe.Runs;
using ClassScribe.Settings;
using ClassScribe.Storage;
using ClassScribe.Transcription;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClassScribe.YouTube;

/// <summary>
/// A batch on the download path (design 1.4 and 3), in the window: one batch at a time; what an earlier batch left half done
/// cleaned up; yt-dlp and its helpers found and asked their versions; each video found among the videos fetched before, and
/// each still to fetch read, paced, a playlist from its flat listing, and judged by the guard, a link copied from an embedded
/// player never read nor downloaded; the key checked and the account read; the plan shown; then, once confirmed (YES in the
/// menu, after a word for each video that needs one and the plan's cost, time and room worked out again for what is then
/// ready; --yes on the command line), each video downloaded in turn, paced and after any class that records, and turned into
/// an ordinary recording of the course, all of the batch in one date folder, with its source file; then each new recording
/// transcribed, with those fetched before that have no transcript; then a summary, the course folder opened once, and the
/// exit code: 0 when every video ended as asked or was done before, 1 when anything was left out, failed or waits, 2 for more
/// videos than a batch takes. Nothing is fetched or sent before the plan is confirmed, and --plan stops at the plan.
/// </summary>
public sealed class DownloadBatch(
    YouTubeBatch batch,
    YtDlpCheck check,
    YouTubeFolders folders,
    IToolRunner tools,
    IBatchPace pace,
    DownloadedAudio audio,
    ClassFolders classFolders,
    RecorderLog log,
    IDriveRoom driveRoom,
    IDriveAutoUpload drive,
    IFreeSpace freeSpace,
    GlossaryChoice glossaries,
    IConsoleUi ui,
    IOptionsSnapshot<YouTubeOptions> youTube,
    IOptionsSnapshot<ElevenLabsOptions> elevenLabs,
    TimeProvider time,
    ILogger<DownloadBatch> logger)
{
    /// <summary>The most videos one download batch takes, once duplicates are removed.</summary>
    public const int MostVideos = 500;

    /// <summary>How long a video's read is reused by its download: the media addresses in it expire after some hours.</summary>
    public static readonly TimeSpan ReadKeptFor = TimeSpan.FromHours(5);

    /// <summary>How many failures to reach YouTube in a row stop the fetching.</summary>
    private const int NetworkFailuresInARow = 3;

    /// <summary>The exit code of a mistake on the command line, such as a batch of more videos than it takes.</summary>
    private const int Mistake = 2;

    /// <summary>The tools' paths, once the check found them.</summary>
    private ToolPaths? _paths;

    /// <summary>The batch's id, which each transcript's run file carries (see <see cref="VideoRequest.BatchId"/>).</summary>
    private string _batchId = "";

    /// <summary>What the batch's confirmation came to.</summary>
    private enum Confirmed
    {
        /// <summary>Go ahead.</summary>
        Go,

        /// <summary>Gulzar went back: nothing is fetched or sent.</summary>
        Back,

        /// <summary>The videos he gave his word for do not fit with the rest: nothing is fetched or sent.</summary>
        NoRoom
    }

    /// <summary>Runs the batch <paramref name="request"/> describes, and returns the exit code. A cancel is thrown.</summary>
    public async Task<int> RunAsync(YouTubeRequest request, CancellationToken cancellationToken)
    {
        using var held = batch.TakeLock();
        if (held is null) return 1;

        // Without a key the transcripts cannot be made, so nothing is asked of YouTube either.
        if (request.Transcribe && !batch.HasKey()) return 1;

        using var awake = batch.KeepAwake();
        _batchId = RunIds.New(time.GetUtcNow());
        audio.CleanUp();

        var found = await check.RunAsync(cancellationToken);
        foreach (var line in found.Lines) logger.LogDebug("{Tool}", line);
        foreach (var problem in found.Problems) logger.LogError("{Problem}", problem);
        if (found.Warning is { } old) logger.LogWarning("{Warning}", old);
        if (found.Paths is not { } paths) return 1;

        _paths = paths;
        var arguments = new YtDlpArguments(paths, folders);
        var index = VideoIndex.Read(request.OutputRoot, classFolders, logger);
        var (videos, playlists, stopped) = await ReadAsync(request, arguments, index, cancellationToken);
        if (videos is null)
        {
            logger.LogError("This batch holds more than {Most} videos; give fewer links, or a shorter playlist, at a time", MostVideos);
            return Mistake;
        }

        var guard = new DownloadGuard(youTube.Value, TranscriptionLimits.From(elevenLabs.Value));
        foreach (var video in videos)
        {
            if (video.Info is { } info) video.Verdict = guard.Judge(info, request.AllowUnlisted);
        }

        var glossary = glossaries.TermsFor(request.Name);
        var accountLine = request.Transcribe ? await batch.AccountLineAsync(cancellationToken) : null;
        var (audioBytes, stagingBytes) = DownloadPlan.BytesOf([.. videos.Where(video => video.IsReady)]);
        var room = Room(request.OutputRoot, audioBytes, stagingBytes);
        var plan = DownloadPlan.Lines(videos, request.Links.Count, playlists, request.Transcribe, glossary, elevenLabs.Value, accountLine, room,
            await DriveLineAsync(audioBytes, cancellationToken));
        foreach (var line in plan) ui.WriteLine(line);

        if (request.Confirmation == BatchConfirmation.PlanOnly) return 0;
        if (!room.Fits) return 1;

        switch (await ConfirmAsync(request, videos, glossary, cancellationToken))
        {
            case Confirmed.Back:
                return 0;
            case Confirmed.NoRoom:
                return 1;
        }

        using var keys = batch.WatchKeys(cancellationToken);
        if (videos.Any(video => video.IsReady)) logger.LogInformation("{Keys}", YouTubeBatch.KeysLine);
        var batchFolder = await FetchAsync(request, videos, arguments, found.YtDlpVersion, stopped, keys);

        var toTranscribe = videos.Select(video => ToTranscribe(request, video, videos.Count)).OfType<VideoToTranscribe>().ToList();
        if (request.Transcribe && toTranscribe.Count > 0)
        {
            keys.NextStep();
            var outcomes = await batch.Transcripts.RunAsync(toTranscribe, () => keys.StopAfterThis, keys.Token);
            foreach (var outcome in outcomes) videos.First(video => Recording(video) == outcome.Video.Files).Transcript = outcome;
        }

        // What did not go up with a transcript waits in the queue for Google Drive: with --no-transcribe all of it goes now.
        var fetched = videos.Where(video => video.Downloaded is not null && video.Transcript is not { Step: TranscriptStep.Transcribed }).Select(video => video.Recording!).ToList();
        foreach (var files in fetched) drive.Queue(files);
        if (!request.Transcribe && fetched.Count > 0) await drive.UploadAsync(fetched[0]);

        var exitCode = Summarize(request, videos);
        var anythingNew = batchFolder is not null || videos.Any(video => video.Transcript is { Step: TranscriptStep.Transcribed });
        batch.OpenCourse(anythingNew ? Path.Combine(ClassFolders.RootFor(request.OutputRoot), FolderNames.SafeName(request.Name)) : null, request.Open);
        return exitCode;
    }

    /// <summary>
    /// Reads the batch's videos: each playlist from its flat listing, then each video still to fetch, paced, its information
    /// into its staging folder. A video done before, as <paramref name="index"/> finds it (transcribed, or fetched whole,
    /// without --again), is not read again: the plan shows it from its source file. A link copied from an embedded player is
    /// never read: the download path never takes it. The first failure that stops the fetching, such as a bot check, stops the
    /// reads too: what is left is not read, and nothing is downloaded. Null videos for more than <see cref="MostVideos"/>.
    /// </summary>
    private async Task<(List<BatchVideo>? Videos, IReadOnlyList<string> Playlists, string? Stopped)> ReadAsync(YouTubeRequest request, YtDlpArguments arguments,
        VideoIndex index, CancellationToken cancellationToken)
    {
        List<(string Id, VideoPlaylist? Playlist, string? Title, bool Embedded)> wanted = [];
        List<string> playlists = [];
        string? stopped = null;
        Directory.CreateDirectory(folders.Root);
        foreach (var link in request.Links)
        {
            if (link.Kind != YouTubeLinkKind.Playlist)
            {
                wanted.Add((link.Id, null, null, link.FromEmbeddedPlayer));
                continue;
            }

            if (stopped is not null)
            {
                logger.LogWarning("The playlist {Playlist} was not read: {Reason}", link.Id, stopped);
                continue;
            }

            await pace.BeforeReadAsync(cancellationToken);
            logger.LogInformation("Reading the playlist {Playlist}", link.Id);
            var run = await RunAsync(arguments.ReadPlaylist(link.Id), folders.Root, null, cancellationToken);
            foreach (var hidden in run.Result.Errors.Where(line => line.Contains("unavailable video", StringComparison.OrdinalIgnoreCase)))
                logger.LogWarning("Playlist {Playlist}: {Warning}", link.Id, hidden.Replace("WARNING:", "", StringComparison.Ordinal).Trim());

            YtDlpPlaylist? playlist = null;
            if (run.NotStarted is null && run.Result.ExitCode == 0)
            {
                try
                {
                    playlist = YtDlpRead.Playlist(string.Join('\n', run.Result.Output), link.Id);
                }
                catch (InvalidDataException ex)
                {
                    // Deliberately not rethrown: a listing that cannot be read leaves this playlist out, which is said; the
                    // batch's other links go on.
                    logger.LogError("{Problem}", ex.Message);
                    continue;
                }
            }

            if (playlist is null)
            {
                var failure = FailureOf(run);
                logger.LogError("The playlist {Playlist} could not be read: {Reason}", link.Id, failure.Said);
                if (failure.StopsFetching) stopped = failure.Said;
                continue;
            }

            playlists.Add(playlist.Title ?? link.Id);
            for (var i = 0; i < playlist.Entries.Count; i++)
                wanted.Add((playlist.Entries[i].Id!, new VideoPlaylist(playlist.Id ?? link.Id, playlist.Title, i + 1), playlist.Entries[i].Title, false));
        }

        var unique = wanted.DistinctBy(item => item.Id).ToList();
        foreach (var twice in wanted.GroupBy(item => item.Id).Where(group => group.Count() > 1))
            logger.LogInformation("The video {VideoId} is in this batch {Count} times; it is taken once", twice.Key, twice.Count());
        if (unique.Count > MostVideos) return (null, playlists, stopped);

        // A video any of whose links came from an embedded player is never read: it is never downloaded.
        var embedded = wanted.Where(item => item.Embedded).Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        List<BatchVideo> videos =
        [
            .. unique.Select((item, i) => new BatchVideo(i + 1, item.Id, item.Playlist)
            {
                ListedTitle = item.Title,
                FromEmbeddedPlayer = embedded.Contains(item.Id),
                Past = index.Judge(item.Id, request.Again, time.LocalTimeZone)
            })
        ];

        var toRead = videos.Where(video => video is { ToFetch: true, RefusedForItsLink: false }).ToList();
        var readTime = BatchPace.ReadEvery * Math.Max(0, toRead.Count - 1);
        if (stopped is null && readTime > TimeSpan.FromMinutes(1))
            logger.LogInformation("Reading {Count} videos from YouTube takes about {Minutes} minutes; Q stops", toRead.Count, (int)Math.Ceiling(readTime.TotalMinutes));

        foreach (var video in toRead)
        {
            if (stopped is not null)
            {
                video.NotRead = "the reads stopped before it";
                continue;
            }

            if (await ReadVideoAsync(video, arguments, cancellationToken) is { StopsFetching: true } failure) stopped = failure.Said;
        }

        return (videos, playlists, stopped);
    }

    /// <summary>Reads one video, paced, into its staging folder, and keeps what it gave; returns the failure when it failed.</summary>
    private async Task<DownloadFailure?> ReadVideoAsync(BatchVideo video, YtDlpArguments arguments, CancellationToken cancellationToken)
    {
        var staging = folders.StagingOf(video.VideoId);
        Directory.CreateDirectory(staging);
        await pace.BeforeReadAsync(cancellationToken);
        logger.LogInformation("Reading {Number}: {VideoId}", Number(video), video.VideoId);
        var run = await RunAsync(arguments.ReadVideo(video.VideoId), staging, null, cancellationToken);
        var infoFile = arguments.InfoFile(video.VideoId);
        if (run.NotStarted is null && run.Result.ExitCode == 0 && File.Exists(infoFile))
        {
            try
            {
                video.Info = YtDlpRead.Info(await File.ReadAllTextAsync(infoFile, cancellationToken), infoFile);
                video.ReadAt = time.GetTimestamp();
                video.NotRead = null;
                return null;
            }
            catch (InvalidDataException ex)
            {
                // Deliberately not rethrown: an information file that cannot be read leaves this video out, which is said;
                // the batch's other videos go on.
                video.NotRead = ex.Message;
                logger.LogError("{Problem}", ex.Message);
                return new DownloadFailure(DownloadFailureKind.Other, ex.Message, StopsFetching: false);
            }
        }

        var failure = FailureOf(run);
        video.NotRead = failure.Said;
        logger.LogError("{Video} could not be read: {Reason}", video.VideoId, failure.Said);
        return failure;
    }

    /// <summary>
    /// Asks for what the batch needs before it goes on: on the command line --yes is that, and nothing is asked. In the menu,
    /// each video that needs a word is asked about, Y taking it and N leaving it out; once any was asked about, the cost, the
    /// time, the room and what goes to Google Drive are worked out again for the videos then ready, and shown, and a batch
    /// that no longer fits is refused; then YES for the downloads, or Y for transcripts alone. <see cref="Confirmed.Back"/>
    /// when Gulzar went back and <see cref="Confirmed.NoRoom"/> when the room ran out: nothing is fetched or sent either way.
    /// </summary>
    private async Task<Confirmed> ConfirmAsync(YouTubeRequest request, IReadOnlyList<BatchVideo> videos, int glossary, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var stamp = now.UtcDateTime.ToString("d MMM yyyy HH:mm", CultureInfo.InvariantCulture) + " UTC";
        if (request.Confirmation == BatchConfirmation.Ask)
        {
            var asked = false;
            foreach (var video in videos.Where(video => video.NeedsWord))
            {
                asked = true;
                var length = video.Length is { } known ? Durations.Format(known) : "length unknown";
                ui.WriteLine($"{Number(video)}  {video.Title} ({video.Info?.Channel ?? "channel unknown"}, {length}) is {video.Info?.Availability ?? "not known to be public"}. Did you upload it, or does its owner allow you");
                ui.WriteLine("    to download it? It must not be one of your course's videos.");
                ui.WriteLine("    Y downloads it   N leaves it out (it can be recorded as it plays instead)   Esc goes back");
                while (true)
                {
                    var key = await ui.ReadKeyAsync(CancellationToken.None);
                    if (key is not { } pressed || pressed.Key == ConsoleKey.Escape) return Confirmed.Back;
                    if (pressed.Key == ConsoleKey.Y)
                    {
                        video.Verdict = new GuardVerdict(GuardVerdictKind.Ready, "ready (your word)", VideoSource.ByWord);
                        break;
                    }

                    if (pressed.Key == ConsoleKey.N)
                    {
                        video.Verdict = new GuardVerdict(GuardVerdictKind.Declined, "left out: you said no", AcceptedBy: null);
                        break;
                    }
                }
            }

            var downloads = videos.Count(video => video.IsReady);
            if (asked && downloads > 0 && !await ShowWhatIsReadyAsync(request, videos, glossary, cancellationToken)) return Confirmed.NoRoom;

            if (downloads > 0)
            {
                ui.WriteLine($"Download the audio of {DownloadPlan.Count(downloads, "video")} with yt-dlp?");
                ui.WriteLine("Only videos you uploaded, or whose owner allows you to download them, and never your course's videos.");
                ui.WriteLine("YouTube's terms allow downloading only where YouTube offers it (such as YouTube Studio, for your own uploads)");
                ui.WriteLine("or with written permission from YouTube and the rights holder; beyond that the choice is yours.");
                ui.WriteLine("ClassScribe never signs in to YouTube and never gives yt-dlp your browser's cookies.");
                ui.WriteLine("Type YES and press Enter to go ahead; Enter alone goes back.");
                ui.Write("> ");
                if (!string.Equals((await ui.ReadLineAsync(CancellationToken.None))?.Trim(), "YES", StringComparison.Ordinal)) return Confirmed.Back;
            }
            else if (request.Transcribe && videos.Any(video => video.Past.Kind == PastKind.Whole && video.Past.Recording?.KeptAnswer is null))
            {
                ui.WriteLine("Nothing to download. Transcribe the recordings fetched before? Y starts; any other key goes back.");
                if (await ui.ReadKeyAsync(CancellationToken.None) is not { Key: ConsoleKey.Y }) return Confirmed.Back;
            }
        }

        foreach (var video in videos.Where(video => video.IsReady))
        {
            video.Confirmed = video.Verdict!.AcceptedBy switch
            {
                VideoSource.ByWord => $"You said you uploaded this video or its owner allows downloading ({stamp})",
                VideoSource.ByAllowUnlisted => $"--allow-unlisted on the command line: you said every unlisted video of the batch is one you uploaded or may download ({stamp})",
                VideoSource.ByChannel => $"Its channel is in YouTube:MayDownloadChannels ({stamp})",
                _ => request.Confirmation == BatchConfirmation.Ask ? $"You typed YES for the batch ({stamp})" : $"--yes on the command line ({stamp})"
            };
        }

        return Confirmed.Go;
    }

    /// <summary>
    /// Shows the plan's cost, time, room and Google Drive lines again for the videos ready now that Gulzar has given his word
    /// on each that needed it, so that what he confirms with YES is what will be fetched. False when they no longer fit on
    /// the drives, which the lines say: nothing is fetched then.
    /// </summary>
    private async Task<bool> ShowWhatIsReadyAsync(YouTubeRequest request, IReadOnlyList<BatchVideo> videos, int glossary, CancellationToken cancellationToken)
    {
        var (audioBytes, stagingBytes) = DownloadPlan.BytesOf([.. videos.Where(video => video.IsReady)]);
        var room = Room(request.OutputRoot, audioBytes, stagingBytes);
        foreach (var line in DownloadPlan.ReadyLines(videos, request.Transcribe, glossary, elevenLabs.Value)) ui.WriteLine(line);
        foreach (var line in DownloadPlan.FetchLines(videos, request.Transcribe, room)) ui.WriteLine(line);
        ui.WriteLine(await DriveLineAsync(audioBytes, cancellationToken));
        return room.Fits;
    }

    /// <summary>
    /// Downloads each video ready, in turn: after any class that records, paced, read again first when its read is more than
    /// 5 hours old; once its audio is downloaded, into a recording of the course, the batch's first that makes a recording
    /// taking its date folder as a class does and the others going into it; its lines into the recording's log. The recording
    /// is made only once its audio is downloaded, so that a date folder holds nothing of a video while it downloads, and a class
    /// that starts meanwhile takes its own. The first failure that stops the fetching, three failures to reach YouTube in a
    /// row, or Q, stops the downloads; what was fetched is transcribed all the same. Returns the batch's date folder, once a
    /// recording is whole in it.
    /// </summary>
    private async Task<string?> FetchAsync(YouTubeRequest request, IReadOnlyList<BatchVideo> videos, YtDlpArguments arguments, string? version,
        string? stopped, BatchKeys keys)
    {
        string? batchFolder = null;
        var networkInARow = 0;
        var ready = videos.Where(video => video.IsReady).ToList();
        for (var i = 0; i < ready.Count; i++)
        {
            var video = ready[i];
            if (stopped is not null || keys.StopAfterThis)
            {
                video.FetchProblem = stopped is not null ? $"not fetched: {stopped}" : "not fetched: the downloads were stopped with Q";
                continue;
            }

            await batch.Wait.WhileAClassRecordsAsync(keys.Token);

            // The video's lines from here on go into its recording's log, once it has one.
            log.KeepFromNow();
            try
            {
                logger.LogInformation("Downloading {Number} of {Count}: \"{Title}\" ({Length})", i + 1, ready.Count, video.Title, Durations.Format(video.Length!.Value));
                var failure = await DownloadAsync(video, arguments, keys.Token);
                if (failure is not null)
                {
                    networkInARow = failure.Kind == DownloadFailureKind.Network ? networkInARow + 1 : 0;
                    video.FetchProblem = failure.Said;
                    logger.LogError("\"{Title}\" was not downloaded: {Reason}", video.Title, failure.Said);
                    if (failure.StopsFetching) stopped = failure.Said;
                    else if (networkInARow >= NetworkFailuresInARow) stopped = $"YouTube could not be reached {NetworkFailuresInARow} times in a row";
                    continue;
                }

                networkInARow = 0;
                var staged = StagedWav(video, arguments);
                var files = batchFolder is null ? classFolders.CreateRecording(request.Name, request.OutputRoot) : classFolders.CreateRecordingIn(batchFolder, request.Name);
                video.Recording = files;
                log.WriteFileTo(files.Log);
                VideoSourceFile.Write(files.Source, SourceOf(video, version, ended: "Downloading", endConfirmed: false));
                logger.LogDebug("Turning \"{Title}\" into {Recording}", video.Title, files.BaseName);
                video.Downloaded = audio.Convert(staged, files, video.Length!.Value, video.Title, keys.Token);
                VideoSourceFile.Write(files.Source, SourceOf(video, version, VideoSource.DownloadedEnd, endConfirmed: true));
                batchFolder ??= files.Folder;
                DeleteStaging(video);
                logger.LogInformation("Downloaded \"{Title}\": {Recording} ({Length})", video.Title, files.BaseName, Durations.Format(video.Downloaded.Value));
            }
            catch (Exception ex) when (ex is IncompleteAudioException or InvalidDataException or FormatException or Win32Exception)
            {
                // Deliberately not rethrown: this video's audio could not be made a recording, for a reason of its own, which
                // is said; its staged download is kept for a look, and the next video goes on.
                video.FetchProblem = ex.Message;
                logger.LogError("\"{Title}\" was not downloaded: {Reason}", video.Title, ex.Message);
            }
            finally
            {
                pace.DownloadEnded();
                log.EndFile();
            }
        }

        if (stopped is not null && ready.Count > 0) logger.LogWarning("The downloads stopped: {Reason}", stopped);
        return batchFolder;
    }

    /// <summary>
    /// Downloads one video into its staging folder: from its read's information file while that is less than 5 hours old,
    /// otherwise after reading it again, once; a download that YouTube refuses with HTTP 403 reads it again and tries once
    /// more. Progress is said every tenth. Returns the failure, or null once yt-dlp has done.
    /// </summary>
    private async Task<DownloadFailure?> DownloadAsync(BatchVideo video, YtDlpArguments arguments, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            var fresh = video.ReadAt is { } readAt && time.GetElapsedTime(readAt) < ReadKeptFor && File.Exists(arguments.InfoFile(video.VideoId));
            if (!fresh && await ReadVideoAsync(video, arguments, cancellationToken) is { } readFailure) return readFailure;

            await pace.BeforeDownloadAsync(cancellationToken);
            var tenth = 0;
            var run = await RunAsync(arguments.Download(video.VideoId, fromInfoFile: true), folders.StagingOf(video.VideoId), line =>
            {
                if (!line.IsError && Percent(line.Text) is { } percent && (int)(percent / 10) > tenth)
                {
                    tenth = (int)(percent / 10);
                    logger.LogInformation("\"{Title}\": {Percent}% downloaded", video.Title, tenth * 10);
                }
            }, cancellationToken);
            if (run.NotStarted is null && run.Result.ExitCode == 0) return null;

            var forbidden = run.Result.Errors.Any(error => error.Contains("HTTP Error 403", StringComparison.OrdinalIgnoreCase));
            if (forbidden && attempt == 1)
            {
                logger.LogInformation("YouTube refused \"{Title}\"'s download with HTTP 403; reading it again and trying once more", video.Title);
                video.ReadAt = null;
                continue;
            }

            return FailureOf(run);
        }
    }

    /// <summary>
    /// Deletes the video's staging folder once its audio is a recording. One that cannot be deleted now is said at Debug; the
    /// next batch prunes it after 7 days.
    /// </summary>
    private void DeleteStaging(BatchVideo video)
    {
        var staging = folders.StagingOf(video.VideoId);
        try
        {
            Directory.Delete(staging, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Deliberately not rethrown: the recording is whole; a staging folder left behind is housekeeping, which the
            // next batch prunes after 7 days.
            logger.LogDebug(ex, "Could not delete the staging folder {Folder}; a later batch prunes it", staging);
        }
    }

    /// <summary>The WAV yt-dlp finished, as done.txt names it: a .wav in the video's own staging folder; otherwise an <see cref="InvalidDataException"/>.</summary>
    private string StagedWav(BatchVideo video, YtDlpArguments arguments)
    {
        var done = arguments.DoneFile(video.VideoId);
        var named = File.Exists(done) ? File.ReadAllLines(done).LastOrDefault(line => line.Trim().Length > 0)?.Trim() : null;
        var staging = folders.StagingOf(video.VideoId);
        if (named is not null && Path.IsPathFullyQualified(named) && string.Equals(Path.GetDirectoryName(Path.GetFullPath(named)), staging, StringComparison.OrdinalIgnoreCase)
            && named.EndsWith(".wav", StringComparison.OrdinalIgnoreCase) && File.Exists(named))
            return named;

        throw new InvalidDataException($"yt-dlp finished \"{video.Title}\", but did not say where its WAV is, or it is not in {staging}; run the same command again");
    }

    /// <summary>
    /// Runs yt-dlp with <paramref name="arguments"/> in <paramref name="folder"/>, each of its lines logged at Debug as it was,
    /// and handed to <paramref name="onLine"/>. A yt-dlp that Windows will not start is a run of its own kind
    /// (<see cref="ToolRun.NotStarted"/>), with Windows' reason, which stops the fetching.
    /// </summary>
    private async Task<ToolRun> RunAsync(IReadOnlyList<string> arguments, string folder, Action<ToolLine>? onLine, CancellationToken cancellationToken)
    {
        try
        {
            return new ToolRun(await tools.RunAsync(new ToolCall(Program, arguments, folder), line =>
            {
                logger.LogDebug("yt-dlp: {Line}", line.Text);
                onLine?.Invoke(line);
            }, cancellationToken), NotStarted: null);
        }
        catch (Win32Exception ex)
        {
            // Deliberately not rethrown: a yt-dlp that Windows will not start, as when Bitdefender blocks it, fails this batch's
            // set-up; it is said with Windows' reason and what to look at, and stops the fetching, as a missing helper does.
            logger.LogDebug(ex, "Could not start yt-dlp");
            return new ToolRun(new ToolResult(-1, [], []),
                new DownloadFailure(DownloadFailureKind.Setup, NotStartedWords(ex.Message), StopsFetching: true));
        }
    }

    /// <summary>What is said when Windows will not start yt-dlp, with Windows' <paramref name="reason"/>.</summary>
    internal static string NotStartedWords(string reason) =>
        $"Windows did not start yt-dlp ({reason.TrimEnd('.')}). Bitdefender may have blocked it (README, Set up once); ClassScribe youtube check says more.";

    /// <summary>The failure <paramref name="run"/> ended with: Windows' refusal to start yt-dlp, or what yt-dlp's lines say.</summary>
    private static DownloadFailure FailureOf(ToolRun run) => run.NotStarted ?? YtDlpErrors.Classify(run.Result.ExitCode, run.Result.Errors);

    /// <summary>yt-dlp's program, as the check found it.</summary>
    private string Program => _paths?.YtDlp ?? throw new InvalidOperationException("yt-dlp's path is known only once the check has found it.");

    /// <summary>
    /// The room the audio needs, on the recordings folder's drive and on ClassScribe's own folder's, with room kept on the
    /// recordings folder's drive for a class of <see cref="DownloadPlan.KeptForAClass"/>, as a class's start warns below it.
    /// </summary>
    private PlanRoom Room(string outputRoot, long audioBytes, long stagingBytes)
    {
        var recordings = Path.GetPathRoot(Path.GetFullPath(outputRoot)) ?? outputRoot;
        var staging = Path.GetPathRoot(folders.Root) ?? folders.Root;
        return new PlanRoom(audioBytes, stagingBytes, recordings, freeSpace.AvailableBytes(recordings), staging, freeSpace.AvailableBytes(staging),
            DownloadPlan.ClassReserveBytes);
    }

    /// <summary>
    /// What goes to Google Drive, for the plan: the transcripts, answers, logs and source files, and the audio too while
    /// YouTube:DriveUpload is Everything, with its size and Drive's room when Drive is signed in and could be asked.
    /// </summary>
    private async Task<string> DriveLineAsync(long audioBytes, CancellationToken cancellationToken)
    {
        if (youTube.Value.DriveUpload == DriveUploadScope.WithoutAudio)
            return "Google Drive: the transcripts, answers, logs and source files, without the audio (YouTube:DriveUpload).";

        var line = $"Google Drive: everything, the audio included (YouTube:DriveUpload), about {Sizes.Describe(audioBytes)} of audio";
        if (audioBytes == 0 || await driveRoom.ReadAsync(cancellationToken) is not { Free: { } free }) return line + ".";

        return free >= audioBytes
            ? $"{line}; Drive has {Sizes.Describe(free)} free."
            : $"{line}; Drive has only {Sizes.Describe(free)} free, so what does not fit waits in the queue until there is room.";
    }

    /// <summary>The video's source file, as it is while it downloads (<paramref name="endConfirmed"/> false) and once its audio is whole.</summary>
    private VideoSource SourceOf(BatchVideo video, string? version, string ended, bool endConfirmed)
    {
        var info = video.Info!;
        return new VideoSource(VideoSource.YouTubeKind, YouTubeLinks.WatchUrl(video.VideoId), video.VideoId, video.Title, info.Channel, info.Handle, info.ChannelId,
            info.Uploaded, info.Length is { } length ? Durations.Format(length) : null, info.Availability, video.Playlist, VideoSource.Downloaded,
            version is null ? "yt-dlp" : $"yt-dlp {version}", time.GetUtcNow(), video.Confirmed, video.Verdict?.AcceptedBy, ended, endConfirmed);
    }

    /// <summary>The recording of <paramref name="video"/> to transcribe: the one this batch downloaded, or the one fetched whole before; null for none.</summary>
    private static Storage.RecordingFiles? Recording(BatchVideo video) =>
        video.Downloaded is not null ? video.Recording : video.Past is { Kind: PastKind.Whole, Recording: { KeptAnswer: null } past } ? past.Files : null;

    /// <summary>What of <paramref name="video"/> is transcribed: its recording, as the request's video; null when none is to be.</summary>
    private VideoToTranscribe? ToTranscribe(YouTubeRequest request, BatchVideo video, int count) =>
        Recording(video) is { } files
            ? new VideoToTranscribe(files,
                new VideoRequest(YouTubeLinks.WatchUrl(video.VideoId), video.VideoId, video.Title, video.Channel, VideoSource.Downloaded, files.Folder,
                    BrowserProcessId: null, BrowserStartedUtc: null, video.Number, count, _batchId),
                request.Name, request.OutputRoot, video.Downloaded ?? video.Length ?? classFolders.LengthOnDisk(files))
            : null;

    /// <summary>
    /// Says what came of each video, and what to do about what is left, and returns the exit code: 0 when every video ended
    /// as asked (downloaded, and transcribed when asked) or was done before, 1 otherwise.
    /// </summary>
    private int Summarize(YouTubeRequest request, IReadOnlyList<BatchVideo> videos)
    {
        var allDone = true;
        ui.WriteLine("Summary:");
        foreach (var video in videos)
        {
            var (done, line) = Outcome(request, video);
            allDone &= done;
            ui.WriteLine($"  {Number(video)}  {video.Title}: {line}");
        }

        if (!allDone) ui.WriteLine("Run the same command again to fetch what is left; what is done is skipped.");
        return allDone ? 0 : 1;
    }

    /// <summary>
    /// Whether <paramref name="video"/> ended as asked, and what the summary says of it. A video the download path may not
    /// take, or could not, but which can be watched (it needs a word, it is the course's, its link came from an embedded
    /// player, it needs a sign-in, or YouTube asked to confirm this is not a bot) is given the command that records it as it
    /// plays instead (Gulzar's answer 3 B). A video that needs a word is told the ways to give it that apply to it:
    /// --allow-unlisted only for one yt-dlp says is unlisted, of a channel it names, and only once YouTube:CourseChannels
    /// lists the course's channels, which the flag needs.
    /// </summary>
    private (bool Done, string Line) Outcome(YouTubeRequest request, BatchVideo video)
    {
        var recordIt = CommandLine.YouTube([YouTubeLinks.WatchUrl(video.VideoId)], request.Name, request.OutputRoot);
        if (video.Transcript is { Step: TranscriptStep.Transcribed, TranscriptPath: { } transcript })
            return (true, $"{(video.Downloaded is null ? "fetched before; " : "downloaded; ")}transcribed: {transcript}");
        if (video.Transcript is { } failed) return (false, $"{(video.Downloaded is null ? "" : "downloaded; ")}not transcribed: {failed.Problem}");
        if (video.Downloaded is not null)
        {
            return request.Transcribe
                ? (false, $"downloaded into {video.Recording!.BaseName}; not transcribed yet: {CommandLine.Transcribe(video.Recording.Segment(1))}")
                : (true, $"downloaded into {video.Recording!.BaseName}. To transcribe it: {CommandLine.Transcribe(video.Recording.Segment(1))}");
        }

        if (video.Past.Kind == PastKind.Transcribed) return (true, video.Past.Words);
        if (video.Past is { Kind: PastKind.Whole, Recording: { } past })
        {
            if (past.KeptAnswer is { } kept) return (false, $"fetched before; ElevenLabs' answer, paid for, is kept in {kept}. To lay it out for free: {CommandLine.Rebuild(kept)}");
            return request.Transcribe ? (false, $"fetched before; not transcribed: {CommandLine.Transcribe(past.Files.Segment(1))}") : (true, video.Past.Words);
        }

        if (video.RefusedForItsLink) return (false, $"{BatchVideo.FromEmbeddedPlayerSaid}; record it as it plays instead: {recordIt}");

        if (video.FetchProblem is { } problem)
        {
            // The bot check's and the sign-in's words end by saying the video can be recorded as it plays: the command follows.
            return (false, problem.Contains("as it plays", StringComparison.Ordinal) || problem.Contains("as they play", StringComparison.Ordinal)
                ? $"{problem.TrimEnd('.')}: {recordIt}"
                : problem);
        }

        if (video.NeedsWord)
        {
            List<string> ways = ["in the menu (Y > D)"];
            if (video.Info is { } info && DownloadGuard.AllowUnlistedTakes(info))
            {
                ways.Add(youTube.Value.CourseChannels.Length == 0
                    ? $"or list your course's channels in {YouTubeOptions.SectionName}:{nameof(YouTubeOptions.CourseChannels)}, then add --allow-unlisted"
                    : "or add --allow-unlisted");
            }

            if ((video.Info?.Handle ?? video.Info?.ChannelId) is { } channel)
                ways.Add($"or its channel {channel} to {YouTubeOptions.SectionName}:{nameof(YouTubeOptions.MayDownloadChannels)} if it is yours");
            return (false, $"needs your word: {string.Join(", ", ways)}; or record it as it plays: {recordIt}");
        }

        return video.Verdict?.Kind is GuardVerdictKind.CourseChannel or GuardVerdictKind.NeedsSignIn or GuardVerdictKind.Declined
            ? (false, $"{video.Words}; record it as it plays instead: {recordIt}")
            : (false, video.Words);
    }

    /// <summary>A video's number as the plan shows it: 01, 02 and so on.</summary>
    private static string Number(BatchVideo video) => video.Number.ToString("00", CultureInfo.InvariantCulture);

    /// <summary>The percent in a progress line of yt-dlp's, such as "[download]  45.3% of 57.0MiB"; null for any other line.</summary>
    private static double? Percent(string line)
    {
        if (!line.StartsWith("[download]", StringComparison.Ordinal)) return null;

        var percent = line.IndexOf('%');
        if (percent < 0) return null;

        var start = line.LastIndexOf(' ', percent) + 1;
        return double.TryParse(line[start..percent], NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : null;
    }

    /// <summary>One run of yt-dlp: what it came to, or, when Windows would not start it, the failure that says so.</summary>
    /// <param name="Result">What the run came to; empty when it did not start.</param>
    /// <param name="NotStarted">The failure of a yt-dlp that Windows would not start; null when it ran.</param>
    private sealed record ToolRun(ToolResult Result, DownloadFailure? NotStarted);
}
