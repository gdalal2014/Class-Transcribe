using ClassScribe.Settings;
using ClassScribe.Transcription;

namespace ClassScribe.YouTube;

/// <summary>What the guard decided for one video.</summary>
public enum GuardVerdictKind
{
    /// <summary>Downloaded, under the batch's confirmation.</summary>
    Ready,

    /// <summary>
    /// Unlisted, not known to be public, or of a channel its read does not name: downloaded only on Gulzar's word, in the menu,
    /// or for a video yt-dlp says is unlisted, of a channel it names, by --allow-unlisted.
    /// </summary>
    NeedsWord,

    /// <summary>A video of his course's channel: never downloaded.</summary>
    CourseChannel,

    /// <summary>It needs a sign-in, which ClassScribe never does.</summary>
    NeedsSignIn,

    /// <summary>A stream that is live, upcoming or still being processed.</summary>
    NotFinished,

    /// <summary>Its length is not known.</summary>
    UnknownLength,

    /// <summary>Longer than ElevenLabs takes in one file.</summary>
    TooLong,

    /// <summary>It needed Gulzar's word, and he said no in the menu.</summary>
    Declined
}

/// <summary>The guard's verdict on one video.</summary>
/// <param name="Kind">What was decided.</param>
/// <param name="Words">The verdict as the plan shows it, such as "ready" or "needs a sign-in: left out".</param>
/// <param name="AcceptedBy">What accepts it for downloading, as its source file keeps it (see <see cref="VideoSource.AcceptedBy"/>); null when it is not ready.</param>
public sealed record GuardVerdict(GuardVerdictKind Kind, string Words, string? AcceptedBy);

/// <summary>
/// The guard on downloads (design 3.5): each video gets the first verdict that applies. A video of a channel in
/// YouTube:CourseChannels, by its UC id or its @handle, is never downloaded. One that needs a sign-in (private, members' or
/// Premium videos, those that need a sign-in, and any restricted by age) is left out: ClassScribe never signs in. A stream
/// that is live, upcoming or still being processed is not finished yet. One whose length is unknown, or longer than
/// ElevenLabs takes in one file, is left out. A video of a channel in YouTube:MayDownloadChannels is ready, and so is a public
/// one. An unlisted video, or one yt-dlp is not sure is public, needs Gulzar's word for that video, since course pages embed
/// unlisted videos: given in the menu, or, for every video yt-dlp says is unlisted, by --allow-unlisted, which never overrides
/// any refusal above and never takes a video yt-dlp is not sure of. While YouTube:CourseChannels names channels, a video whose
/// read names no channel cannot be checked against them, so it needs his word in the menu, public or not, and --allow-unlisted
/// never gives it.
/// </summary>
/// <param name="options">The YouTube settings of the batch's run.</param>
public sealed class DownloadGuard(YouTubeOptions options)
{
    /// <summary>The verdict on a video whose channel is not known while YouTube:CourseChannels names channels.</summary>
    public const string ChannelUnknown = "channel unknown: needs your word";

    /// <summary>The availabilities that need a sign-in.</summary>
    private static readonly HashSet<string> SignedIn = new(StringComparer.OrdinalIgnoreCase) { "private", "subscriber_only", "premium_only", "needs_auth" };

    /// <summary>The live states of a stream that is not finished.</summary>
    private static readonly HashSet<string> Unfinished = new(StringComparer.OrdinalIgnoreCase) { "is_live", "is_upcoming", "post_live" };

    /// <summary>The verdict on the video <paramref name="info"/> describes; <paramref name="allowUnlisted"/> is --allow-unlisted.</summary>
    public GuardVerdict Judge(YtDlpInfo info, bool allowUnlisted)
    {
        if (IsOneOf(info, options.CourseChannels))
            return new(GuardVerdictKind.CourseChannel, "your course's channel: never downloaded", AcceptedBy: null);
        if (info.Availability is { } availability && SignedIn.Contains(availability) || info.AgeLimit is > 0)
            return new(GuardVerdictKind.NeedsSignIn, "needs a sign-in: left out", AcceptedBy: null);
        if (info.LiveStatus is { } live && Unfinished.Contains(live))
            return new(GuardVerdictKind.NotFinished, "not finished yet: left out", AcceptedBy: null);
        if (info.Length is not { } length)
            return new(GuardVerdictKind.UnknownLength, "length unknown: left out", AcceptedBy: null);
        if (length > TranscriptionWorkflow.LongestRecording)
            return new(GuardVerdictKind.TooLong, "longer than 9 h 30 min: left out", AcceptedBy: null);

        // The course's channels cannot be told from a channel the read does not name: such a video is taken on his word alone.
        if (options.CourseChannels.Length > 0 && !KnowsChannel(info))
            return new(GuardVerdictKind.NeedsWord, ChannelUnknown, AcceptedBy: null);
        if (IsOneOf(info, options.MayDownloadChannels))
            return new(GuardVerdictKind.Ready, "ready (YouTube:MayDownloadChannels)", VideoSource.ByChannel);
        if (string.Equals(info.Availability, "public", StringComparison.OrdinalIgnoreCase))
            return new(GuardVerdictKind.Ready, "ready", VideoSource.ByYes);

        return allowUnlisted && AllowUnlistedTakes(info)
            ? new(GuardVerdictKind.Ready, "ready (--allow-unlisted)", VideoSource.ByAllowUnlisted)
            : new(GuardVerdictKind.NeedsWord, "needs your word", AcceptedBy: null);
    }

    /// <summary>
    /// True when --allow-unlisted takes the video <paramref name="info"/> describes, should it need a word: yt-dlp says it is
    /// unlisted, not merely that it is not sure it is public, and its read names its channel.
    /// </summary>
    public static bool AllowUnlistedTakes(YtDlpInfo info) =>
        string.Equals(info.Availability, "unlisted", StringComparison.OrdinalIgnoreCase) && KnowsChannel(info);

    /// <summary>True when the video's read names its channel: by its UC id, its @handle, or its /channel/ address.</summary>
    public static bool KnowsChannel(YtDlpInfo info) =>
        !string.IsNullOrWhiteSpace(info.ChannelId) || info.Handle is not null || (info.ChannelUrl?.Contains("/channel/", StringComparison.Ordinal) ?? false);

    /// <summary>
    /// True when the video's channel is one of <paramref name="channels"/>: a UC id the same as its channel's id, or as the id
    /// in its channel's address; an @handle the same as its handle, in any letter case.
    /// </summary>
    public static bool IsOneOf(YtDlpInfo info, IEnumerable<string> channels) =>
        channels.Select(channel => channel.Trim()).Any(channel => channel.StartsWith('@')
            ? string.Equals(channel, info.Handle, StringComparison.OrdinalIgnoreCase)
            : string.Equals(channel, info.ChannelId, StringComparison.Ordinal)
              || (info.ChannelUrl?.EndsWith($"/channel/{channel}", StringComparison.Ordinal) ?? false));
}
