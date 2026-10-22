using System.Globalization;
using ClassScribe.Audio;
using ClassScribe.Commands;
using ClassScribe.Recorder;
using ClassScribe.Settings;
using ClassScribe.Storage;
using ClassScribe.Transcription;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClassScribe.YouTube;

/// <summary>One video of a batch on the record path, from its title to its transcript: what is known of it, and what came of it.</summary>
/// <param name="number">Its place in the batch, from 1.</param>
/// <param name="link">Its link, rebuilt from its ID, with where it starts.</param>
public sealed class RecordedVideo(int number, YouTubeLink link)
{
    /// <summary>Its place in the batch, from 1.</summary>
    public int Number { get; } = number;

    /// <summary>Its link, with where it starts (t=), which its window opens at.</summary>
    public YouTubeLink Link { get; } = link;

    /// <summary>Its title and channel as YouTube's oEmbed answer gave them; null when it gave none.</summary>
    public VideoTitle? Read { get; set; }

    /// <summary>Its title: as YouTube gave it, else its ID.</summary>
    public string Title => Read?.Title ?? Link.Id;

    /// <summary>What was done with it before (see <see cref="VideoIndex"/>).</summary>
    public PastVerdict Past { get; set; } = new(PastKind.None, null, "");

    /// <summary>True when it is to be recorded: never recorded whole or transcribed before, or --again.</summary>
    public bool ToRecord => Past.Kind is PastKind.None or PastKind.Unfinished;

    /// <summary>The recording this batch made of it, once its run made one.</summary>
    public RecordingFiles? Recording { get; set; }

    /// <summary>How its run ended, as its run file says.</summary>
    public RunOutcome? Outcome { get; set; }

    /// <summary>What ended its recording, as its run file names it (see <see cref="RunEndCause"/>).</summary>
    public string? EndedBy { get; set; }

    /// <summary>Its source file, as its run left it: how the recording ended, and whether that end is confirmed.</summary>
    public VideoSource? Source { get; set; }

    /// <summary>The length of its recording, once recorded.</summary>
    public TimeSpan? Length { get; set; }

    /// <summary>Why it was not recorded, in words; null when it was, or was not to be.</summary>
    public string? Problem { get; set; }

    /// <summary>True when its view was left with Esc: it records on in the background, and the same command transcribes it later.</summary>
    public bool Left { get; set; }

    /// <summary>What came of its transcript, when one was asked for.</summary>
    public TranscriptOutcome? Transcript { get; set; }

    /// <summary>True once this batch recorded audio of it.</summary>
    public bool Recorded => Recording is not null && Outcome is { Kind: OutcomeKind.Recorded };

    /// <summary>The words of the plan's last column: what was done before, or YouTube's missing title.</summary>
    public string Words => Past.Kind == PastKind.None ? Read is null ? $"({OEmbedTitles.NoTitle})" : "" : Past.Words;
}

/// <summary>
/// The plan of a batch on the record path (design 4.5), before any window opens: the videos and their channels, what was done
/// with each before, how the videos are played and end, the longest each is recorded for and the most their transcripts can
/// cost, and the account's credits.
/// </summary>
public static class VideoPlan
{
    /// <summary>
    /// The plan's lines for <paramref name="videos"/> of the course <paramref name="name"/>, recorded into
    /// <paramref name="courseFolder"/>, each for at most <paramref name="longest"/> (as <paramref name="longestSetting"/> sets it);
    /// the cost as <paramref name="prices"/> say, with the glossary of <paramref name="keyterms"/> terms, when transcribed.
    /// </summary>
    public static IReadOnlyList<string> Lines(IReadOnlyList<RecordedVideo> videos, string name, string courseFolder, bool transcribe, int keyterms,
        ElevenLabsOptions prices, string? accountLine, TimeSpan longest, string longestSetting)
    {
        var toRecord = videos.Where(video => video.ToRecord).ToList();
        List<string> lines = [toRecord.Count > 0
            ? $"Recording {DownloadPlan.Count(toRecord.Count, "video")} for \"{name}\" as they play, one at a time, into {courseFolder}:"
            : $"{DownloadPlan.Count(videos.Count, "video")} for \"{name}\":"];

        var titleWidth = Math.Min(40, Math.Max(5, videos.Select(video => video.Title.Length).DefaultIfEmpty(0).Max()));
        var channelWidth = Math.Min(24, Math.Max(7, videos.Select(video => video.Read?.Channel?.Length ?? 1).DefaultIfEmpty(0).Max()));
        foreach (var video in videos)
        {
            lines.Add(string.Create(CultureInfo.InvariantCulture,
                $"  {video.Number:00}  {Cut(video.Title, titleWidth).PadRight(titleWidth)}  {Cut(video.Read?.Channel ?? "-", channelWidth).PadRight(channelWidth)}  {video.Words}").TrimEnd());
        }

        if (toRecord.Count == 0)
        {
            lines.Add("Nothing to record.");
        }
        else
        {
            lines.Add("Each opens in a Chrome window of ClassScribe's own: press play there. ClassScribe records until the video ends,");
            lines.Add("closes the window, and opens the next when you press Enter. Leave YouTube's Autoplay on: its move to the next");
            lines.Add("video is how ClassScribe knows this one has ended.");
            var most = longest * toRecord.Count;
            var each = $"Each is recorded for as long as it plays, at most {Hours(longest)} ({longestSetting}), ads included, so at most {Hours(most)} for {These(toRecord.Count)}";
            lines.Add(transcribe ? $"{each}: {WorstCost(most, keyterms, prices)} The transcripts follow the recordings." : $"{each}. Nothing is sent to ElevenLabs.");
        }

        var before = videos.Where(video => video.Past.Kind == PastKind.Whole && video.Past.Recording?.KeptAnswer is null).ToList();
        if (transcribe && before.Count > 0)
        {
            var length = before.Aggregate(TimeSpan.Zero, (sum, video) => sum + Recorded(video.Past.Recording!.Files));
            lines.Add($"Also transcribed: {DownloadPlan.Count(before.Count, "recording")} made before, {BatchTranscripts.Hours(length)}{DownloadPlan.CostOf(length, transcribe, keyterms, prices)}");
        }

        if (transcribe && accountLine is not null) lines.Add($"{accountLine}.");
        return lines;
    }

    /// <summary>
    /// The most the recordings can cost, which the lengths, unknown before the videos play, cannot exceed: "they cost at most
    /// $3.24 to transcribe, most likely much less ($0.27 for each hour recorded)."; or that no price is estimated.
    /// </summary>
    public static string WorstCost(TimeSpan most, int keyterms, ElevenLabsOptions prices)
    {
        var estimate = TranscriptionCost.Estimate(counted: null, most, keyterms, prices);
        if (estimate.Dollars is not { } dollars) return $"no price is estimated ({ElevenLabsOptions.SectionName}:{nameof(ElevenLabsOptions.PricePerHourUsd)} is 0).";

        var hour = estimate.PricePerHour + (keyterms > 0 ? estimate.KeytermsPricePerHour : 0);
        return string.Create(CultureInfo.InvariantCulture, $"they cost at most ${dollars:0.00} to transcribe, most likely much less (${hour:0.00} for each hour recorded).");
    }

    /// <summary>How long the recording of <paramref name="files"/> is, on disk.</summary>
    private static TimeSpan Recorded(RecordingFiles files) => files.FindSegments().Aggregate(TimeSpan.Zero, (sum, segment) => sum + ClassFolders.MeasureSegment(segment));

    /// <summary>A length in whole hours, "4 hours", or in hours and minutes, "1 h 30 min", or in minutes under an hour.</summary>
    internal static string Hours(TimeSpan length) =>
        length.TotalMinutes % 60 == 0 && length >= TimeSpan.FromHours(1)
            ? DownloadPlan.Count((long)length.TotalHours, "hour")
            : BatchTranscripts.Hours(length);

    /// <summary>"this one" or "these 3".</summary>
    private static string These(int count) => count == 1 ? "this one" : string.Create(CultureInfo.InvariantCulture, $"these {count}");

    /// <summary><paramref name="text"/> cut to <paramref name="width"/>, with ... at its end when it was longer.</summary>
    private static string Cut(string text, int width) => text.Length <= width ? text : text[..(width - 3)] + "...";
}

/// <summary>
/// A batch on the record path (design 1.4, 2 and 4), in the window, attended: one batch at a time; Chrome found; each video's
/// title read from YouTube's oEmbed address, a second apart; each found among the videos recorded or downloaded before; the
/// key checked and the account read; the plan shown; then, once confirmed (Y in the menu; --yes on the command line), each
/// video in turn, after any class that records, opened in ClassScribe's own Chrome window when Gulzar asks (Y for the first in
/// the menu, Enter for each other, and for each on the command line), recorded as it plays by a run of its own through the
/// starter, which this window shows as its owner, until the video ends, and its window closed; the batch's videos in one date
/// folder. Then each new recording transcribed, with those made before that have no transcript; then a summary, the course
/// folder opened once, and the exit code: 0 when every video was recorded whole and transcribed when asked, or was done
/// before; 1 when anything was left out, failed, may be cut short, or waits; 2 for links the record path does not take;
/// 130 for Ctrl+C twice, which stops the video in hand and the batch, with no transcripts. ClassScribe never plays a video
/// by itself, and never opens the next without Gulzar's key.
/// </summary>
public sealed class VideoBatch(
    YouTubeBatch batch,
    DownloadedAudio downloads,
    BrowserLocator browsers,
    OEmbedTitles titles,
    VideoWindow windows,
    ICommandRunner commands,
    RunJournal journal,
    ClassFolders classFolders,
    KeytermLoader keyterms,
    IUserSignals signals,
    IConsoleUi ui,
    IOptionsSnapshot<YouTubeOptions> youTube,
    IOptionsSnapshot<RecordingOptions> recording,
    IOptionsSnapshot<ElevenLabsOptions> elevenLabs,
    TimeProvider time,
    ILogger<VideoBatch> logger)
{
    /// <summary>The most videos one batch records as they play: each takes as long as it plays.</summary>
    public const int MostVideos = 10;

    /// <summary>How many windows in a row that would not open, or runs that failed before they recorded, stop the batch.</summary>
    private const int FailuresInARow = 2;

    /// <summary>The exit code of a mistake on the command line.</summary>
    private const int Mistake = 2;

    /// <summary>The exit code of a batch stopped with Ctrl+C.</summary>
    private const int Cancelled = 130;

    /// <summary>The batch's id, which each video's run and transcript carries (see <see cref="VideoRequest.BatchId"/>).</summary>
    private string _batchId = "";

    /// <summary>What Gulzar answered before a video's window opens.</summary>
    private enum Next
    {
        /// <summary>Enter, or Y at the plan: the window opens.</summary>
        Open,

        /// <summary>S or Esc: no more windows open; the transcripts of those recorded follow.</summary>
        Stop,

        /// <summary>Ctrl+C twice: the batch stops, with no transcripts.</summary>
        Cancel
    }

    /// <summary>Runs the batch <paramref name="request"/> describes, and returns the exit code (see the class).</summary>
    public async Task<int> RunAsync(YouTubeRequest request)
    {
        if (request.Links.FirstOrDefault(link => link.Kind != YouTubeLinkKind.Video) is { } other)
        {
            logger.LogError("{Link} is a {Kind}'s link, which only --download takes; give each video's own link to record it as it plays", other.Url,
                other.Kind == YouTubeLinkKind.Playlist ? "playlist" : "live stream");
            return Mistake;
        }

        if (request.Links.Count > MostVideos)
        {
            logger.LogError("A batch records at most {Most} videos as they play, each for as long as it plays; these links name {Count}", MostVideos, request.Links.Count);
            return Mistake;
        }

        using var held = batch.TakeLock();
        if (held is null) return 1;

        // What an earlier download batch left half done is cleared under the lock by any batch, this one included (design 1.4).
        downloads.CleanUp();

        // Without a key the transcripts cannot be made, so no window is opened either.
        if (request.Transcribe && !batch.HasKey()) return 1;

        if (browsers.Find() is not { } chrome)
        {
            logger.LogError("{Problem}", BrowserLocator.NotFound);
            return 1;
        }

        // Ctrl+C pressed twice stops the batch wherever it is; the token follows it. It holds no timer, so it is left to the
        // collector rather than disposed while the signals may still cancel it.
        var stopping = new CancellationTokenSource();
        _ = signals.StopRequested.ContinueWith(_ => stopping.Cancel(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

        using var awake = batch.KeepAwake();
        var batchId = _batchId = RunIds.New(time.GetUtcNow());
        var videos = request.Links.Select((link, i) => new RecordedVideo(i + 1, link)).ToList();
        try
        {
            foreach (var video in videos) video.Read = await titles.ReadAsync(video.Link.Id, stopping.Token);

            var index = VideoIndex.Read(request.OutputRoot, classFolders, logger);
            foreach (var video in videos) video.Past = index.Judge(video.Link.Id, request.Again, time.LocalTimeZone);

            var accountLine = request.Transcribe ? await batch.AccountLineAsync(stopping.Token) : null;
            var (longest, setting) = Longest();
            var courseFolder = Path.Combine(ClassFolders.RootFor(request.OutputRoot), ClassFolders.SafeName(request.Name));
            var glossary = keyterms.Load(KeytermLoader.ResolvePath(elevenLabs.Value.KeytermsFile)).Count;
            foreach (var line in VideoPlan.Lines(videos, request.Name, courseFolder, request.Transcribe, glossary, elevenLabs.Value, accountLine, longest, setting))
                ui.WriteLine(line);

            if (request.Confirmation == BatchConfirmation.PlanOnly) return 0;

            var toRecord = videos.Where(video => video.ToRecord).ToList();
            var openFirst = false;
            if (request.Confirmation == BatchConfirmation.Ask && (toRecord.Count > 0 || TranscribedBefore(request, videos).Count > 0))
            {
                ui.WriteLine("Y starts; any other key goes back.");
                if (await ui.ReadKeyAsync(CancellationToken.None) is not { Key: ConsoleKey.Y }) return 0;
                openFirst = true;
            }

            if (await RecordAllAsync(request, toRecord, chrome, batchId, openFirst, stopping.Token) is { } stopped) return stopped;

            await TranscribeAsync(request, videos);
            var exitCode = Summarize(request, videos);
            batch.OpenCourse(videos.Any(video => video.Recorded || video.Transcript is { Step: TranscriptStep.Transcribed }) ? courseFolder : null, request.Open);
            return exitCode;
        }
        catch (OperationCanceledException) when (signals.StopRequested.IsCompleted)
        {
            // Deliberately not rethrown: Ctrl+C is how Gulzar stops the batch, which ends with its own exit code, 130, as when it
            // is pressed between two videos; what was recorded is kept.
            logger.LogWarning("The YouTube batch was stopped with Ctrl+C; what was recorded is kept, and the same command transcribes it later");
            return Cancelled;
        }
    }

    /// <summary>
    /// Records each video to record, in turn: after any class that records; once Gulzar asks; in its window; through a run of its
    /// own, shown here; then the window closed. Two windows in a row that would not open, or two runs that failed before they
    /// recorded, stop the recordings: something is wrong with Chrome. S or Esc between videos, and Esc in a video's view, stop
    /// opening windows. Returns 130 for Ctrl+C pressed twice, and null otherwise.
    /// </summary>
    private async Task<int?> RecordAllAsync(YouTubeRequest request, IReadOnlyList<RecordedVideo> toRecord, string chrome, string batchId, bool openFirst,
        CancellationToken stopping)
    {
        string? batchFolder = null;
        var failures = 0;
        var tool = browsers.VersionOf(chrome);
        for (var i = 0; i < toRecord.Count; i++)
        {
            var video = toRecord[i];
            await batch.Wait.WhileAClassRecordsAsync(stopping);

            // Y at the plan opens the first; every other window, and the first again after a class took its place, opens on Enter.
            var next = $"Next: {video.Number:00} {video.Title}. Enter opens it; {StopWords(request)}.";
            var answer = openFirst ? Next.Open : await AskAsync(next, request);
            openFirst = false;
            OpenedWindow? window = null;
            while (answer == Next.Open)
            {
                // A class that started while Gulzar was asked comes first: the window opens only once it has ended, on his Enter again.
                if (await batch.Wait.WhileAClassRecordsAsync(stopping))
                {
                    answer = await AskAsync(next, request);
                    continue;
                }

                var opening = await windows.OpenAsync(chrome, video.Link.UrlAtStart, stopping);
                if (opening.Window is { } opened)
                {
                    window = opened;
                    break;
                }

                if (!opening.HandedOff)
                {
                    video.Problem = opening.Problem;
                    logger.LogError("{Problem}", opening.Problem);
                    break;
                }

                logger.LogWarning("{Problem}", opening.Problem);
                answer = await AskAsync($"Enter tries again; {StopWords(request)}.", request);
            }

            if (answer == Next.Cancel) return Cancelled;
            if (answer == Next.Stop)
            {
                foreach (var left in toRecord.Skip(i)) left.Problem ??= "not recorded: you stopped the videos";
                break;
            }

            if (window is null)
            {
                if (++failures >= FailuresInARow) return StopAfterFailures(toRecord.Skip(i + 1));
                continue;
            }

            var refused = await RecordAsync(request, video, window, batchFolder, tool, batchId, toRecord.Count);
            batchFolder ??= video.Recording?.Folder;

            // A video left recording in the background keeps its window, which its run closes at its end.
            if (!video.Left && !await windows.CloseAsync(window, CancellationToken.None))
                logger.LogWarning("ClassScribe's video window of \"{Title}\" did not close; close it before the next one opens", video.Title);

            if (signals.StopRequested.IsCompleted) return Cancelled;
            if (refused)
            {
                // A class took the recording in the moment between the look and the start: the batch waits for it, and asks again.
                video.Problem = null;
                i--;
                continue;
            }

            if (video.Left)
            {
                foreach (var left in toRecord.Skip(i + 1)) left.Problem ??= "not recorded: you left the view of the video before it";
                break;
            }

            failures = video.Recorded || video.Outcome is { Kind: OutcomeKind.NothingRecorded } ? 0 : failures + 1;
            if (failures >= FailuresInARow) return StopAfterFailures(toRecord.Skip(i + 1));
        }

        return null;
    }

    /// <summary>
    /// Records <paramref name="video"/> in <paramref name="window"/> through a run of its own, which the starter shows here as its
    /// owner, and reads what came of it from its run file and its source file. True when the start was refused because a class
    /// records, which the batch waits for; false otherwise. A run in this window that failed is that video's alone.
    /// </summary>
    private async Task<bool> RecordAsync(YouTubeRequest request, RecordedVideo video, OpenedWindow window, string? batchFolder, string tool, string batchId, int count)
    {
        var videoRequest = new VideoRequest(video.Link.Url, video.Link.Id, video.Title, video.Read?.Channel, VideoSource.Recorded, batchFolder, window.Process.ProcessId,
            window.Process.StartedUtc, video.Number, count, batchId, video.Read?.ChannelHandle, tool, video.Link.StartSeconds);
        var runRequest = new RecordRequest(RunIds.New(time.GetUtcNow()), request.Name, Transcribe: false,
            new SourcePlan(RecordingMode.Class, new ClassSource.App("chrome"), Microphone: null), request.OutputRoot, ContinueChoice.New, ContinueBaseName: null,
            Minutes: null, Open: false, videoRequest);
        try
        {
            await commands.RecordAsync(runRequest, StartOrigin.VideoBatch);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Deliberately not rethrown: a video's run in this window that failed is that video's alone; it has said why, and
            // written its outcome, which is read next, and the batch goes on to the next video.
            logger.LogDebug(ex, "The run of \"{Title}\" failed", video.Title);
        }

        var run = journal.List().Where(found => found.Record.Request.Video is { } recorded && recorded.BatchId == batchId && recorded.Position == video.Number)
            .OrderByDescending(found => found.Record.StartedUtc).FirstOrDefault();
        if (run is null)
        {
            if (journal.List().Any(found => found.IsRecordingNow && !found.IsVideo)) return true;

            video.Problem = "not recorded: its recording did not start";
            return false;
        }

        video.Left = run.IsRecordingNow;
        video.Outcome = run.Record.Outcome;
        video.EndedBy = run.Record.EndedBy;
        if (run.Record is { Folder: { } folder, BaseName: { } baseName })
        {
            var files = new RecordingFiles(folder, baseName);
            video.Recording = files;
            video.Length = classFolders.LengthOnDisk(files);
            video.Source = ReadSource(files);
        }

        if (video.Left)
        {
            logger.LogInformation("\"{Title}\" records on in the background to its end; no more videos open, and the same command transcribes it later", video.Title);
        }
        else if (video.Outcome is null && video.Recording is { } kept && kept.FindSegments().Count > 0)
        {
            // Its recorder was lost while it recorded: a video is never continued, so the audio so far is kept, transcribed as
            // a recording that may be cut short, which its source file says, and the same command records it again.
            video.Outcome = new RunOutcome(OutcomeKind.Recorded, 1, "Its recorder ended unexpectedly; the audio so far is kept.", null, kept.Folder, null);
        }
        else if (video.Outcome is not { Kind: OutcomeKind.Recorded })
        {
            video.Problem = video.Outcome is { } failed ? $"not recorded: {Sentence.Of(failed.Message).TrimEnd('.')}" : "not recorded: its recorder ended before it recorded";
        }

        return false;
    }

    /// <summary>Asks what follows with <paramref name="prompt"/>: Enter opens, S or Esc stops, Ctrl+C twice cancels; nobody to answer stops.</summary>
    private async Task<Next> AskAsync(string prompt, YouTubeRequest request)
    {
        ui.DiscardPendingKeys();
        ui.WriteLine(prompt);
        while (true)
        {
            if (signals.StopRequested.IsCompleted) return Next.Cancel;

            using var reading = new CancellationTokenSource();
            var read = ui.ReadKeyAsync(reading.Token);
            await Task.WhenAny(read, signals.StopRequested);
            if (signals.StopRequested.IsCompleted)
            {
                await reading.CancelAsync();
                await read;
                return Next.Cancel;
            }

            switch (await read)
            {
                case null:
                    logger.LogDebug("No key can be read, so no more videos open{Transcripts}", request.Transcribe ? "; the transcripts follow" : "");
                    return Next.Stop;
                case { Key: ConsoleKey.Enter }:
                    return Next.Open;
                case { Key: ConsoleKey.S or ConsoleKey.Escape }:
                    return Next.Stop;
            }
        }
    }

    /// <summary>What S does, as a prompt says it.</summary>
    private static string StopWords(YouTubeRequest request) => request.Transcribe ? "S stops here and goes on to the transcripts" : "S stops here";

    /// <summary>
    /// Stops the recordings after two windows in a row that would not open, or two runs that failed before they recorded:
    /// something is wrong with Chrome. The videos left are said not to be recorded; the transcripts of those recorded follow.
    /// </summary>
    private int? StopAfterFailures(IEnumerable<RecordedVideo> left)
    {
        logger.LogError("Two videos in a row could not be recorded, so no more windows open: something is wrong with Chrome. The errors above say what");
        foreach (var video in left) video.Problem ??= "not recorded: the videos stopped after two that could not be recorded";
        return null;
    }

    /// <summary>
    /// Transcribes, when asked, each recording this batch made with audio, then each made before that holds the whole video and
    /// has no transcript, in order, after any class that records; Q stops after the one in hand, and one Ctrl+C cancels the
    /// upload in hand (see <see cref="BatchTranscripts"/>).
    /// </summary>
    private async Task TranscribeAsync(YouTubeRequest request, IReadOnlyList<RecordedVideo> videos)
    {
        if (!request.Transcribe) return;

        var toTranscribe = videos.Where(video => video is { Recorded: true, Left: false }).Select(video => ToTranscribe(request, video, video.Recording!, video.Length ?? TimeSpan.Zero, videos.Count))
            .Concat(TranscribedBefore(request, videos).Select(video => ToTranscribe(request, video, video.Past.Recording!.Files, classFolders.LengthOnDisk(video.Past.Recording.Files), videos.Count)))
            .ToList();
        if (toTranscribe.Count == 0) return;

        // From here one Ctrl+C cancels the upload in hand, as transcribe's does.
        signals.RequestStop();
        using var keys = batch.WatchKeys(signals.Cancellation);
        logger.LogInformation("Q stops after the transcript in hand; Ctrl+C cancels it");
        var outcomes = await batch.Transcripts.RunAsync(toTranscribe, () => keys.StopAfterThis, keys.Token);
        foreach (var outcome in outcomes)
            videos.First(video => video.Recording == outcome.Video.Files || video.Past.Recording?.Files == outcome.Video.Files).Transcript = outcome;
    }

    /// <summary>The videos recorded or downloaded whole before, without a transcript or an answer paid for, which the batch transcribes when asked.</summary>
    private static List<RecordedVideo> TranscribedBefore(YouTubeRequest request, IReadOnlyList<RecordedVideo> videos) =>
        request.Transcribe ? [.. videos.Where(video => video.Past is { Kind: PastKind.Whole, Recording: { KeptAnswer: null } })] : [];

    /// <summary>The recording <paramref name="files"/> of <paramref name="video"/> to transcribe, as the request's video.</summary>
    private VideoToTranscribe ToTranscribe(YouTubeRequest request, RecordedVideo video, RecordingFiles files, TimeSpan length, int count) =>
        new(files, new VideoRequest(video.Link.Url, video.Link.Id, video.Title, video.Read?.Channel, VideoSource.Recorded, files.Folder, BrowserProcessId: null,
            BrowserStartedUtc: null, video.Number, count, _batchId, video.Read?.ChannelHandle), request.Name, request.OutputRoot, length);

    /// <summary>
    /// Says what came of each video, and what to do about what is left, and returns the exit code: 0 when every video was
    /// recorded whole and transcribed when asked, or was done before; 1 otherwise.
    /// </summary>
    private int Summarize(YouTubeRequest request, IReadOnlyList<RecordedVideo> videos)
    {
        var allDone = true;
        ui.WriteLine("Summary:");
        foreach (var video in videos)
        {
            var (done, line) = Outcome(request, video);
            allDone &= done;
            ui.WriteLine($"  {video.Number:00}  {video.Title}: {line}");
        }

        if (!allDone) ui.WriteLine("Run the same command again to record what is left; what is done is skipped.");
        return allDone ? 0 : 1;
    }

    /// <summary>Whether <paramref name="video"/> ended as asked, and what the summary says of it.</summary>
    private (bool Done, string Line) Outcome(YouTubeRequest request, RecordedVideo video)
    {
        if (!video.ToRecord)
        {
            if (video.Transcript is { Step: TranscriptStep.Transcribed, TranscriptPath: { } before }) return (true, $"made before; transcribed: {before}");
            if (video.Transcript is { } beforeFailed) return (false, $"made before; not transcribed: {beforeFailed.Problem}");
            if (video.Past is { Kind: PastKind.Whole, Recording: { KeptAnswer: { } kept } })
                return (false, $"made before; ElevenLabs' answer, paid for, is kept in {kept}. To lay it out for free: {CommandLine.Rebuild(kept)}");
            return video.Past.Kind == PastKind.Whole && request.Transcribe
                ? (false, $"made before; not transcribed: {CommandLine.Transcribe(video.Past.Recording!.Files.Segment(1))}")
                : (true, video.Past.Words);
        }

        if (video.Left) return (false, $"records on in the background; run the same command again once it has ended to transcribe it");
        if (!video.Recorded) return (false, video.Problem ?? "not recorded");

        var files = video.Recording!;
        var length = Durations.Format(video.Length ?? TimeSpan.Zero);
        var whole = video.Source?.EndConfirmed == true;
        var how = video.Source?.Ended ?? VideoEnds.NotFinished;
        var recorded = whole ? $"recorded ({length}), {how}" : $"recorded ({length}), but {how}, so it may be cut short; the same command records it again";
        if (video.EndedBy is RunEndCause.VideoLimit or RunEndCause.SafetyStop)
        {
            var from = (video.Link.StartSeconds ?? 0) + (int)(video.Length ?? TimeSpan.Zero).TotalSeconds;
            recorded += $". For the rest: {CommandLine.YouTube([YouTubeLinks.WatchUrl(video.Link.Id, from)], request.Name, request.OutputRoot)}";
        }

        if (video.Transcript is { Step: TranscriptStep.Transcribed, TranscriptPath: { } transcript }) return (whole, $"{recorded}; transcribed: {transcript}");
        if (video.Transcript is { } failed) return (false, $"{recorded}; not transcribed: {failed.Problem}");
        return request.Transcribe
            ? (false, $"{recorded}; not transcribed yet: {CommandLine.Transcribe(files.Segment(1))}")
            : (whole, $"{recorded}. To transcribe it: {CommandLine.Transcribe(files.Segment(1))}");
    }

    /// <summary>
    /// The longest a video is recorded for, and the setting that sets it: YouTube:LongestVideoHours, or the safety stop,
    /// Recording:StopAfterHours, when that comes first.
    /// </summary>
    private (TimeSpan Longest, string Setting) Longest()
    {
        var video = TimeSpan.FromHours(youTube.Value.LongestVideoHours);
        var safety = recording.Value.StopAfterHours;
        return safety > 0 && TimeSpan.FromHours(safety) < video
            ? (TimeSpan.FromHours(safety), $"{RecordingOptions.SectionName}:{nameof(RecordingOptions.StopAfterHours)}")
            : (video, $"{YouTubeOptions.SectionName}:{nameof(YouTubeOptions.LongestVideoHours)}");
    }

    /// <summary>The source file of <paramref name="files"/>; null when there is none, or it cannot be read, which is said at Debug.</summary>
    private VideoSource? ReadSource(RecordingFiles files)
    {
        try
        {
            return VideoSourceFile.Read(files.Source);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            // Deliberately not rethrown: the source file only says how the recording ended; without it the recording counts as
            // one that may be cut short, which the same command records again.
            logger.LogDebug(ex, "Could not read {Path}", files.Source);
            return null;
        }
    }
}
