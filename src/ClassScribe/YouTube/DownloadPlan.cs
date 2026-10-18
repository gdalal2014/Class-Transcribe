using System.Globalization;
using ClassScribe.Commands;
using ClassScribe.Drive;
using ClassScribe.Settings;
using ClassScribe.Transcription;

namespace ClassScribe.YouTube;

/// <summary>One video of a batch, from its read to its transcript: what is known of it, what was decided, and what came of it.</summary>
/// <param name="Number">Its place in the batch, from 1.</param>
/// <param name="VideoId">Its ID.</param>
/// <param name="Playlist">The playlist it was taken from, if any.</param>
public sealed class BatchVideo(int Number, string VideoId, VideoPlaylist? Playlist)
{
    /// <summary>Its place in the batch, from 1.</summary>
    public int Number { get; } = Number;

    /// <summary>Its ID.</summary>
    public string VideoId { get; } = VideoId;

    /// <summary>The playlist it was taken from, if any.</summary>
    public VideoPlaylist? Playlist { get; } = Playlist;

    /// <summary>What its read gave; null when it was not read.</summary>
    public YtDlpInfo? Info { get; set; }

    /// <summary>When it was read, on the steady clock; its information file is reused by its download for 5 hours.</summary>
    public long? ReadAt { get; set; }

    /// <summary>Why it was not read, in words; null when it was.</summary>
    public string? NotRead { get; set; }

    /// <summary>What was done with it before (see <see cref="VideoIndex"/>).</summary>
    public PastVerdict Past { get; set; } = new(PastKind.None, null, "");

    /// <summary>The guard's verdict on it; null when it was not read.</summary>
    public GuardVerdict? Verdict { get; set; }

    /// <summary>On whose word it is downloaded, in words with the time, as its source file keeps it; null until it is accepted.</summary>
    public string? Confirmed { get; set; }

    /// <summary>The recording of it this batch made, once made.</summary>
    public Storage.RecordingFiles? Recording { get; set; }

    /// <summary>The length of its audio, once downloaded whole.</summary>
    public TimeSpan? Downloaded { get; set; }

    /// <summary>Why it was not fetched, in words; null when it was, or was not to be.</summary>
    public string? FetchProblem { get; set; }

    /// <summary>What came of its transcript, when one was asked for.</summary>
    public TranscriptOutcome? Transcript { get; set; }

    /// <summary>Its title: as its read gave it, else as its playlist gave it, else its ID.</summary>
    public string Title => Info?.Title is { Length: > 0 } title ? title : ListedTitle ?? VideoId;

    /// <summary>Its title as its playlist's listing gave it, before it was read.</summary>
    public string? ListedTitle { get; init; }

    /// <summary>Its length, when known.</summary>
    public TimeSpan? Length => Info?.Length;

    /// <summary>True when it is to be downloaded: read, never fetched whole before, and ready by the guard's verdict.</summary>
    public bool IsReady => Info is not null && Past.Kind is PastKind.None or PastKind.Unfinished && Verdict is { Kind: GuardVerdictKind.Ready };

    /// <summary>True when it waits for Gulzar's word: read, never fetched whole before, and unlisted or not known to be public.</summary>
    public bool NeedsWord => Info is not null && Past.Kind is PastKind.None or PastKind.Unfinished && Verdict is { Kind: GuardVerdictKind.NeedsWord };

    /// <summary>What the plan's last column says of it.</summary>
    public string Words =>
        NotRead is { } notRead ? $"not read: {notRead}"
        : Past.Kind is PastKind.Transcribed or PastKind.Whole ? Past.Words
        : Past.Kind == PastKind.Unfinished && Verdict is { Kind: GuardVerdictKind.Ready } ? $"{Verdict.Words}; {Past.Words}"
        : Verdict?.Words ?? "";
}

/// <summary>The room the batch's audio takes, on the recordings folder's drive and, for the staging, on ClassScribe's own folder's.</summary>
/// <param name="AudioBytes">The audio of the videos ready, at each one's own rate, mono 16-bit.</param>
/// <param name="StagingBytes">The most one video's download takes in staging, its WAV and its downloaded audio together.</param>
/// <param name="RecordingsDrive">The recordings folder's drive.</param>
/// <param name="RecordingsFree">The room left on it; null when it cannot be read.</param>
/// <param name="StagingDrive">ClassScribe's own folder's drive.</param>
/// <param name="StagingFree">The room left on it; null when it cannot be read.</param>
public sealed record PlanRoom(long AudioBytes, long StagingBytes, string RecordingsDrive, long? RecordingsFree, string StagingDrive, long? StagingFree)
{
    /// <summary>True when both drives share the room, the recordings folder's and ClassScribe's own folder's being one.</summary>
    private bool OneDrive => string.Equals(RecordingsDrive, StagingDrive, StringComparison.OrdinalIgnoreCase);

    /// <summary>The room the recordings folder's drive must have: the audio, and the staging too when they share the drive.</summary>
    private long RecordingsNeed => AudioBytes + (OneDrive ? StagingBytes : 0);

    /// <summary>True when the audio fits, as far as the room can be read.</summary>
    public bool Fits => (RecordingsFree is not { } free || free >= RecordingsNeed) && (OneDrive || StagingFree is not { } staging || staging >= StagingBytes);
}

/// <summary>
/// The plan of a download batch (design 3.6), before anything is fetched or sent: each video read, with its channel, length,
/// availability and the verdict on it; what is ready, its length and about what its transcripts cost; the account's
/// credits; how long the downloads take; the room the audio needs; what goes to Google Drive; and the channels of the videos
/// that need a word, which YouTube:MayDownloadChannels could name.
/// </summary>
public static class DownloadPlan
{
    /// <summary>What a download is reckoned to take besides its pause: a minute for each hour of audio, fetched and converted.</summary>
    private static readonly TimeSpan PerHourOfAudio = TimeSpan.FromMinutes(1);

    /// <summary>
    /// The plan's lines: <paramref name="videos"/> as read from <paramref name="links"/> links of which <paramref name="playlists"/>
    /// were playlists; the rest as the arguments say.
    /// </summary>
    public static IReadOnlyList<string> Lines(IReadOnlyList<BatchVideo> videos, int links, IReadOnlyList<string> playlists, bool transcribe, int keyterms,
        ElevenLabsOptions prices, string? accountLine, PlanRoom room, string driveLine)
    {
        List<string> lines = [];
        var from = playlists.Count switch
        {
            0 => "",
            1 => $" (1 playlist, \"{playlists[0]}\")",
            var count => string.Create(CultureInfo.InvariantCulture, $" ({count} playlists)")
        };
        lines.Add($"Read {Count(videos.Count, "video")} from {Count(links, "link")}{from}:");

        var titleWidth = Math.Min(40, Math.Max(5, videos.Select(video => video.Title.Length).DefaultIfEmpty(0).Max()));
        var channelWidth = Math.Min(24, Math.Max(7, videos.Select(video => video.Info?.Channel?.Length ?? 1).DefaultIfEmpty(0).Max()));
        lines.Add($"  #   {"Title".PadRight(titleWidth)}  {"Channel".PadRight(channelWidth)}  Length    Availability  Verdict");
        foreach (var video in videos)
        {
            lines.Add(string.Create(CultureInfo.InvariantCulture,
                $"  {video.Number:00}  {Cut(video.Title, titleWidth).PadRight(titleWidth)}  {Cut(video.Info?.Channel ?? "-", channelWidth).PadRight(channelWidth)}  {(video.Length is { } length ? Durations.Format(length) : "-"),-8}  {(video.Info?.Availability ?? (video.Info is null ? "-" : "not given")),-12}  {video.Words}"));
        }

        var ready = videos.Where(video => video.IsReady).ToList();
        var before = videos.Where(video => video.Past.Kind == PastKind.Whole && video.Past.Recording?.KeptAnswer is null).ToList();
        var readyLength = Sum(ready.Select(video => video.Length ?? TimeSpan.Zero));
        var beforeLength = Sum(before.Select(video => video.Past.Recording is { } past ? Sum(past.Files.FindSegments().Select(Storage.ClassFolders.MeasureSegment)) : TimeSpan.Zero));

        lines.Add(ready.Count == 0 ? "Nothing to download." : $"Ready: {Count(ready.Count, "video")}, {BatchTranscripts.Hours(readyLength)}{CostOf(readyLength, transcribe, keyterms, prices)}");
        if (transcribe && before.Count > 0)
            lines.Add($"Also transcribed: {Count(before.Count, "recording")} fetched before, {BatchTranscripts.Hours(beforeLength)}{CostOf(beforeLength, transcribe, keyterms, prices)}");
        if (transcribe && accountLine is not null) lines.Add($"{accountLine}.");

        if (ready.Count > 0)
        {
            lines.Add($"{Count(ready.Count, "download")}: about {Minutes(DownloadTime(ready))}, 10 to 20 seconds apart, depending on your connection{(transcribe ? "; then the transcripts, a few minutes each" : "")}.");
            var spare = room.RecordingsFree is { } free ? $", with {DriveCommand.Size(Math.Max(0, free - room.AudioBytes))} to spare" : "";
            lines.Add($"Room: about {DriveCommand.Size(room.AudioBytes)} on {room.RecordingsDrive} for the audio (mono, at YouTube's own rate){spare}.");
            if (!room.Fits) lines.Add("There is not room enough for the audio and its download: free some room, or download fewer videos at a time.");
        }

        lines.Add(driveLine);

        var unsure = videos.Where(video => video.NeedsWord && video.Info is { Channel: not null }).Select(video => $"{video.Info!.Channel}{(video.Info.Handle is { } handle ? $" {handle}" : "")}")
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (unsure.Count > 0)
            lines.Add($"Channels: {string.Join(", ", unsure)} (add one to {YouTubeOptions.SectionName}:{nameof(YouTubeOptions.MayDownloadChannels)} if it is yours, or its owner allows downloading).");
        return lines;
    }

    /// <summary>
    /// The batch's cost line's end, worded beside <see cref="TranscriptionCost.Describe"/>: ": about $2.61 to transcribe ($0.22 an
    /// hour plus $0.05 for the glossary of 75 terms)."; ". Nothing is sent to ElevenLabs." without transcripts; and no price
    /// while ElevenLabs:PricePerHourUsd is 0.
    /// </summary>
    public static string CostOf(TimeSpan length, bool transcribe, int keyterms, ElevenLabsOptions prices)
    {
        if (!transcribe) return ". Nothing is sent to ElevenLabs.";

        var estimate = TranscriptionCost.Estimate(counted: null, length, keyterms, prices);
        if (estimate.Dollars is not { } dollars) return $"; no price is estimated ({ElevenLabsOptions.SectionName}:{nameof(ElevenLabsOptions.PricePerHourUsd)} is 0).";

        var glossary = keyterms > 0 && estimate.KeytermsPricePerHour > 0
            ? string.Create(CultureInfo.InvariantCulture, $" plus ${estimate.KeytermsPricePerHour:0.00##} for the glossary of {Count(keyterms, "term")}")
            : "";
        var amount = dollars < 0.005 ? "less than a cent" : string.Create(CultureInfo.InvariantCulture, $"about ${dollars:0.00}");
        return string.Create(CultureInfo.InvariantCulture, $": {amount} to transcribe (${estimate.PricePerHour:0.00##} an hour{glossary}).");
    }

    /// <summary>
    /// How long the downloads take, reckoned: each a pause of 15 seconds and a minute for each hour of its audio, and at most
    /// 60 begun in an hour, so that a batch of more waits for the hour to pass.
    /// </summary>
    public static TimeSpan DownloadTime(IReadOnlyList<BatchVideo> ready)
    {
        var reckoned = Sum(ready.Select(video => TimeSpan.FromSeconds(15) + PerHourOfAudio * (video.Length ?? TimeSpan.Zero).TotalHours));
        var hours = (ready.Count - 1) / BatchPace.MostDownloadsAnHour;
        return hours > 0 && TimeSpan.FromHours(hours) > reckoned ? TimeSpan.FromHours(hours) : reckoned;
    }

    /// <summary>
    /// The room the ready videos' audio takes: each at its own rate (see <see cref="YtDlpInfo.SourceRate"/>), mono 16-bit, with
    /// the most one download takes in staging beside it, its WAV and its compressed audio, reckoned at 160 kbps.
    /// </summary>
    public static (long Audio, long Staging) BytesOf(IReadOnlyList<BatchVideo> ready)
    {
        long audio = 0, staging = 0;
        foreach (var video in ready)
        {
            var seconds = (video.Length ?? TimeSpan.Zero).TotalSeconds;
            var wav = (long)(seconds * (video.Info?.SourceRate ?? YtDlpInfo.HighestRate) * 2) + Audio.WavSegmentWriter.HeaderSize;
            audio += wav;
            staging = Math.Max(staging, wav + (long)(seconds * 20_000));
        }

        return (audio, staging);
    }

    /// <summary>A length in minutes, or hours and minutes from an hour, rounded up.</summary>
    private static string Minutes(TimeSpan length) =>
        length >= TimeSpan.FromHours(1) ? BatchTranscripts.Hours(length) : Count((long)Math.Ceiling(Math.Max(1, length.TotalMinutes)), "minute");

    /// <summary>The sum of <paramref name="lengths"/>.</summary>
    private static TimeSpan Sum(IEnumerable<TimeSpan> lengths) => lengths.Aggregate(TimeSpan.Zero, (sum, length) => sum + length);

    /// <summary><paramref name="text"/> cut to <paramref name="width"/>, with ... at its end when it was longer.</summary>
    private static string Cut(string text, int width) => text.Length <= width ? text : text[..(width - 3)] + "...";

    /// <summary>A number with its noun, singular for one: "1 video", "9 videos".</summary>
    internal static string Count(long number, string noun) => string.Create(CultureInfo.InvariantCulture, $"{number} {noun}{(number == 1 ? "" : "s")}");
}
