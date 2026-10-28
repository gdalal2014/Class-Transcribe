using ClassScribe.Settings;

namespace ClassScribe.Transcription;

/// <summary>
/// How much audio one file to ElevenLabs may hold (ElevenLabs:LongestFileHours, architecture 7.2), and the limits that follow
/// from it: the longest recording ClassScribe sends, half an hour within it, which is also the latest the safety stop comes;
/// and the longest a YouTube video is recorded for, an hour within it. The half hour and the hour are ClassScribe's own
/// margins, not ElevenLabs': a recording is refused before anything is prepared, rather than by ElevenLabs after an hour of
/// upload. The settings it bounds, Recording:StopAfterHours and YouTube:LongestVideoHours, are held to it here, never refused,
/// so that lowering the file limit alone never stops a command (question 10). Built from the run's settings by
/// <see cref="From"/>.
/// </summary>
/// <param name="LongestFile">The most audio one file to ElevenLabs may hold.</param>
public sealed record TranscriptionLimits(TimeSpan LongestFile)
{
    /// <summary>What ClassScribe keeps in hand below the file limit for a recording: half an hour.</summary>
    public static readonly TimeSpan RecordingMargin = TimeSpan.FromMinutes(30);

    /// <summary>What it keeps in hand below the file limit for a YouTube video's recording: an hour.</summary>
    public static readonly TimeSpan VideoMargin = TimeSpan.FromHours(1);

    /// <summary>The shortest a video's limit is ever held to: a quarter of an hour, the least YouTube:LongestVideoHours takes.</summary>
    private static readonly TimeSpan ShortestVideo = TimeSpan.FromHours(YouTubeOptions.ShortestLongestVideoHours);

    /// <summary>ElevenLabs' documented limit: 10 hours of audio in one file.</summary>
    public static TranscriptionLimits ElevenLabs { get; } = new(TimeSpan.FromHours(10));

    /// <summary>
    /// The limits <paramref name="options"/> give: ElevenLabs:LongestFileHours, held within the 1 to 24 hours the check takes,
    /// or ElevenLabs' own for a value that is no number, so that a part which skipped the check can never hold a limit that is
    /// zero, negative or endless.
    /// </summary>
    public static TranscriptionLimits From(ElevenLabsOptions options)
    {
        var hours = options.LongestFileHours;
        return double.IsFinite(hours)
            ? new(TimeSpan.FromHours(Math.Clamp(hours, ElevenLabsOptions.ShortestLongestFileHours, ElevenLabsOptions.LargestLongestFileHours)))
            : ElevenLabs;
    }

    /// <summary>The longest recording sent in one upload: the file limit less half an hour. A longer one is refused before anything is prepared.</summary>
    public TimeSpan LongestRecording => LongestFile - RecordingMargin;

    /// <summary>The longest a YouTube video is recorded for at most: the file limit less an hour, and never below a quarter of an hour.</summary>
    public TimeSpan LongestVideo => LongestFile - VideoMargin is var video && video > ShortestVideo ? video : ShortestVideo;

    /// <summary>
    /// When the safety stop comes for <paramref name="stopAfterHours"/> (Recording:StopAfterHours): after that many hours, or at
    /// <see cref="LongestRecording"/> when that is earlier, so that a recording stays short enough to send; null for 0, which
    /// turns the stop off, and for a value that is no number.
    /// </summary>
    public TimeSpan? SafetyStop(double stopAfterHours) =>
        !(stopAfterHours > 0) ? null
        : stopAfterHours < LongestRecording.TotalHours ? TimeSpan.FromHours(stopAfterHours)
        : LongestRecording;

    /// <summary>
    /// The longest a video is recorded for, for <paramref name="longestVideoHours"/> (YouTube:LongestVideoHours): that many hours,
    /// or <see cref="LongestVideo"/> when that is shorter, as it is for a value the check would refuse (none, or no number).
    /// </summary>
    public TimeSpan VideoLimit(double longestVideoHours) =>
        longestVideoHours > 0 && longestVideoHours < LongestVideo.TotalHours ? TimeSpan.FromHours(longestVideoHours) : LongestVideo;

    /// <summary>True when <paramref name="stopAfterHours"/> comes later than <see cref="LongestRecording"/>, so the safety stop is held there.</summary>
    public bool HoldsTheStop(double stopAfterHours) => stopAfterHours > LongestRecording.TotalHours;

    /// <summary>True when <paramref name="longestVideoHours"/> is longer than <see cref="LongestVideo"/>, so a video's recording is held there.</summary>
    public bool HoldsTheVideo(double longestVideoHours) => longestVideoHours > LongestVideo.TotalHours;
}
