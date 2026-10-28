using System.Globalization;
using ClassScribe.Settings;
using ClassScribe.Transcription;
using ClassScribe.YouTube;

namespace ClassScribe.Tests;

/// <summary>
/// How much audio one file to ElevenLabs may hold, ElevenLabs:LongestFileHours (architecture 7.2), and what follows from it:
/// the longest recording, half an hour within it, which is the latest the safety stop comes and is sent with the minute a
/// recording may run past it; and the longest a YouTube video is recorded for, an hour within it. The settings it bounds are
/// held to it, never refused (question 10).
/// </summary>
[TestFixture]
public class TranscriptionLimitsTests
{
    /// <summary>The limits a file of <paramref name="hours"/> gives, as the settings would build them.</summary>
    private static TranscriptionLimits For(double hours) => TranscriptionLimits.From(new ElevenLabsOptions { LongestFileHours = hours });

    /// <summary>
    /// By default one file holds ElevenLabs' documented 10 hours, so ClassScribe sends at most 9 hours 30 minutes, and a video
    /// is recorded for at most 9 hours: today's values, unchanged.
    /// </summary>
    [Test]
    public void The_defaults_are_ElevenLabs_ten_hours_and_todays_limits()
    {
        var limits = TranscriptionLimits.From(new ElevenLabsOptions());

        Assert.That(limits, Is.EqualTo(TranscriptionLimits.ElevenLabs));
        Assert.That(TranscriptionLimits.ElevenLabs.LongestFile, Is.EqualTo(TimeSpan.FromHours(10)));
        Assert.That((limits.LongestRecording, limits.LongestVideo), Is.EqualTo((TimeSpan.FromHours(9.5), TimeSpan.FromHours(9))));
    }

    /// <summary>
    /// The safety stop comes at Recording:StopAfterHours, or at the longest recording sent when that is earlier; 0 turns it
    /// off. With a file of 5 hours, a stop of 9 hours comes at 4 hours 30 minutes.
    /// </summary>
    [TestCase(10, 9, 9)]
    [TestCase(10, 9.5, 9.5)]
    [TestCase(10, 12, 9.5)]
    [TestCase(10, 24, 9.5)]
    [TestCase(5, 9, 4.5)]
    [TestCase(5, 3, 3)]
    public void The_safety_stop_is_held_to_the_longest_recording_sent(double fileHours, double stopAfterHours, double stopsAtHours)
    {
        Assert.That(For(fileHours).SafetyStop(stopAfterHours), Is.EqualTo(TimeSpan.FromHours(stopsAtHours)));
    }

    /// <summary>
    /// A recording may run a minute past the longest recording and still be sent, and not a second more: with ElevenLabs' own
    /// 10 hours, 9 hours 31 minutes are sent and 9 hours 31 minutes and a second are not; with a file of 5 hours, 4 hours 31
    /// minutes. A recording the safety stop ended runs a moment past its stop, which this takes in.
    /// </summary>
    [TestCase(10, "09:30:00", true)]
    [TestCase(10, "09:30:01", true)]
    [TestCase(10, "09:31:00", true)]
    [TestCase(10, "09:31:01", false)]
    [TestCase(5, "04:31:00", true)]
    [TestCase(5, "04:31:01", false)]
    public void A_recording_is_sent_up_to_a_minute_past_the_longest_recording(double fileHours, string length, bool sent)
    {
        Assert.That(TranscriptionLimits.Overrun, Is.EqualTo(TimeSpan.FromMinutes(1)));
        Assert.That(For(fileHours).Sends(TimeSpan.Parse(length, CultureInfo.InvariantCulture)), Is.EqualTo(sent));
    }

    /// <summary>
    /// Wherever the safety stop comes, held by the file or not, a recording a few seconds past it is still sent, so the stop
    /// never makes a recording too long to transcribe.
    /// </summary>
    [TestCase(10, 9)]
    [TestCase(10, 9.5)]
    [TestCase(10, 12)]
    [TestCase(10, 24)]
    [TestCase(5, 9)]
    [TestCase(1, 9)]
    [TestCase(24, 24)]
    public void A_recording_a_moment_past_its_safety_stop_is_sent(double fileHours, double stopAfterHours)
    {
        var limits = For(fileHours);

        Assert.That(limits.Sends(limits.SafetyStop(stopAfterHours)!.Value + TimeSpan.FromSeconds(5)), Is.True);
    }

    /// <summary>
    /// The guard takes a video as long as the longest recording, and its downloaded audio may be up to 2 seconds longer than
    /// the video (DownloadedAudio.LengthTolerance): that audio is still sent, by the default file limit and a lowered one.
    /// </summary>
    [TestCase(10)]
    [TestCase(5)]
    public void A_downloaded_videos_audio_a_moment_past_the_longest_recording_is_sent(double fileHours)
    {
        var limits = For(fileHours);

        Assert.That(limits.Sends(limits.LongestRecording + DownloadedAudio.LengthTolerance), Is.True);
    }

    /// <summary>0 hours turns the safety stop off, whatever the file holds, as it always has.</summary>
    [Test]
    public void No_safety_stop_with_0_hours()
    {
        Assert.That(For(10).SafetyStop(0), Is.Null);
        Assert.That(For(5).SafetyStop(0), Is.Null);
    }

    /// <summary>A video is recorded for at most YouTube:LongestVideoHours, or an hour less than one file holds when that is shorter.</summary>
    [TestCase(10, 4, 4)]
    [TestCase(10, 9, 9)]
    [TestCase(10, 12, 9)]
    [TestCase(5, 4, 4)]
    [TestCase(5, 6, 4)]
    public void A_videos_longest_recording_is_held_an_hour_within_one_file(double fileHours, double longestVideoHours, double limitHours)
    {
        Assert.That(For(fileHours).VideoLimit(longestVideoHours), Is.EqualTo(TimeSpan.FromHours(limitHours)));
    }

    /// <summary>
    /// With the shortest file the settings take, an hour, a video's limit is never below a quarter of an hour, the least
    /// YouTube:LongestVideoHours takes, and stays within the half hour a recording may be: a video never stops at once.
    /// </summary>
    [Test]
    public void With_a_one_hour_file_a_video_still_records_a_quarter_of_an_hour()
    {
        var limits = For(1);

        Assert.That((limits.LongestRecording, limits.LongestVideo), Is.EqualTo((TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(15))));
        Assert.That(limits.VideoLimit(4), Is.EqualTo(TimeSpan.FromMinutes(15)));
    }

    /// <summary>
    /// A file limit outside the 1 to 24 hours the check takes, which only a part that skipped the check could hold, is held
    /// within them, and one that is no number is ElevenLabs' own: no limit is ever zero, negative or endless.
    /// </summary>
    [TestCase(0.5, 1)]
    [TestCase(-3, 1)]
    [TestCase(30, 24)]
    [TestCase(double.NaN, 10)]
    [TestCase(double.PositiveInfinity, 10)]
    public void A_file_limit_out_of_range_is_held_within_it(double hours, double heldAt)
    {
        Assert.That(For(hours).LongestFile, Is.EqualTo(TimeSpan.FromHours(heldAt)));
    }
}
