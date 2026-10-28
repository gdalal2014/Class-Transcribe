using ClassScribe.Settings;
using ClassScribe.Storage;
using ClassScribe.Transcription;
using ClassScribe.YouTube;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClassScribe.Tests;

/// <summary>
/// The plan of a download batch (design 3.6, with Gulzar's answers 5 and 6): the cost at the API price with the glossary,
/// the time of the downloads, the room the audio takes at YouTube's own rate (about 318 MB an hour at 44.1 kHz, 346 MB at
/// 48 kHz), and what goes to Google Drive, the audio included by default.
/// </summary>
[TestFixture]
public class DownloadPlanTests
{
    /// <summary>A ready video of <paramref name="hours"/> hours whose best m4a audio is at <paramref name="rate"/> Hz.</summary>
    private static BatchVideo Ready(int number, double hours, int rate = 44100)
    {
        var info = YtDlpRead.Info(
            $$"""{"id":"abcdefghijk","title":"Part {{number}}","duration":{{(hours * 3600).ToString(System.Globalization.CultureInfo.InvariantCulture)}},"availability":"public","channel":"Your Channel","formats":[{"ext":"m4a","acodec":"mp4a.40.2","vcodec":"none","asr":{{rate}}}]}""",
            "x");
        return new BatchVideo(number, "abcdefghijk", null) { Info = info, Verdict = new GuardVerdict(GuardVerdictKind.Ready, "ready", VideoSource.ByYes) };
    }

    /// <summary>The cost is the batch's length at the API price plus the glossary's, worded beside the line after each transcript.</summary>
    [Test]
    public void The_cost_is_the_length_at_the_api_price_with_the_glossary()
    {
        var prices = new ElevenLabsOptions();

        Assert.That(DownloadPlan.CostOf(TimeSpan.FromMinutes(581), transcribe: true, keyterms: 75, prices), Is.EqualTo(": about $2.61 to transcribe ($0.22 an hour plus $0.05 for the glossary of 75 terms)."));
        Assert.That(DownloadPlan.CostOf(TimeSpan.FromHours(10), transcribe: true, keyterms: 0, prices), Is.EqualTo(": about $2.20 to transcribe ($0.22 an hour)."));
        Assert.That(DownloadPlan.CostOf(TimeSpan.FromHours(10), transcribe: false, keyterms: 75, prices), Is.EqualTo(". Nothing is sent to ElevenLabs."));
        Assert.That(DownloadPlan.CostOf(TimeSpan.FromHours(10), transcribe: true, keyterms: 75, new ElevenLabsOptions { PricePerHourUsd = 0 }),
            Is.EqualTo("; no price is estimated (ElevenLabs:PricePerHourUsd is 0)."));
    }

    /// <summary>
    /// The audio keeps YouTube's own rate, mono 16-bit: an hour at 44.1 kHz takes about 318 MB (317.5 MB of 1,000,000 bytes),
    /// at 48 kHz about 346 MB; the staging beside it is the largest one download, its WAV and its compressed audio.
    /// </summary>
    [Test]
    public void The_room_is_the_audio_at_youtubes_own_rate()
    {
        var (hourAt44, stagingAt44) = DownloadPlan.BytesOf([Ready(1, 1)]);
        var (hourAt48, _) = DownloadPlan.BytesOf([Ready(1, 1, 48000)]);

        Assert.That(hourAt44, Is.EqualTo(3600L * 44100 * 2 + 44));
        Assert.That(hourAt44 / 1_000_000.0, Is.EqualTo(317.5).Within(0.1));
        Assert.That(hourAt48 / 1_000_000.0, Is.EqualTo(345.6).Within(0.1));
        Assert.That(stagingAt44, Is.EqualTo(hourAt44 + 3600L * 20_000));
    }

    /// <summary>Two drives: each must hold its part; one drive must hold both. Unknown room never refuses.</summary>
    [Test]
    public void The_room_fits_on_each_drive_or_on_one()
    {
        Assert.That(new PlanRoom(100, 50, @"C:\", 120, @"C:\", 999).Fits, Is.False, "one drive holds the audio and the staging together");
        Assert.That(new PlanRoom(100, 50, @"C:\", 150, @"C:\", 999).Fits, Is.True);
        Assert.That(new PlanRoom(100, 50, @"D:\", 120, @"C:\", 49).Fits, Is.False, "ClassScribe's own folder's drive holds the staging");
        Assert.That(new PlanRoom(100, 50, @"D:\", 100, @"C:\", 50).Fits, Is.True);
        Assert.That(new PlanRoom(100, 50, @"D:\", null, @"C:\", null).Fits, Is.True);
    }

    /// <summary>
    /// The recordings folder's drive keeps room for a class after the batch, as long as a class's start warns below (3 hours
    /// of its track at 48 kHz and its upload copy, about 1.3 GB): audio that fits only by leaving less does not fit, and the
    /// plan says why.
    /// </summary>
    [Test]
    public void The_room_keeps_room_for_a_class()
    {
        Assert.That(DownloadPlan.KeptForAClass, Is.EqualTo(TimeSpan.FromHours(3)));
        Assert.That(DownloadPlan.ClassReserveBytes, Is.EqualTo(3L * 3600 * (48_000 * 2 + 16_000 * 2)));
        Assert.That(new PlanRoom(100, 50, @"C:\", 159, @"C:\", 999, ClassReserve: 10).Fits, Is.False, "the audio and its staging fit, but not with a class's room kept");
        Assert.That(new PlanRoom(100, 50, @"C:\", 160, @"C:\", 999, ClassReserve: 10).Fits, Is.True);
        Assert.That(new PlanRoom(100, 50, @"D:\", 109, @"C:\", 999, ClassReserve: 10).Fits, Is.False, "the room for a class is kept on the recordings folder's drive");
        Assert.That(new PlanRoom(0, 0, @"C:\", 0, @"C:\", 0, ClassReserve: 10).Fits, Is.True, "nothing to download needs no room");

        var lines = DownloadPlan.FetchLines([Ready(1, 1)], transcribe: true, new PlanRoom(158_760_044, 230_760_044, @"C:\", 1_000_000_000, @"C:\", 1_000_000_000, DownloadPlan.ClassReserveBytes));

        Assert.That(lines[^1], Is.EqualTo("There is not room enough for the audio and its download while keeping room for a 3-hour class (about 1.3 GB): free some room, or download fewer videos at a time."));
    }

    /// <summary>
    /// While videos wait for his word, the plan says how many, their length and their cost, after what is ready, which may be
    /// nothing yet; never "Nothing to download." while any waits.
    /// </summary>
    [Test]
    public void Videos_that_wait_for_his_word_are_counted_and_costed()
    {
        var unlisted = Ready(1, 3);
        unlisted.Verdict = new GuardVerdict(GuardVerdictKind.NeedsWord, "needs your word", null);
        var other = Ready(2, 3);
        other.Verdict = new GuardVerdict(GuardVerdictKind.NeedsWord, "needs your word", null);

        Assert.That(DownloadPlan.ReadyLines([unlisted, other], transcribe: true, keyterms: 0, new ElevenLabsOptions()), Is.EqualTo(new[]
        {
            "Ready: 0 videos.",
            "Waiting for your word: 2 videos, 6 h 0 min: about $1.32 to transcribe ($0.22 an hour)."
        }));
        Assert.That(DownloadPlan.ReadyLines([], transcribe: true, keyterms: 0, new ElevenLabsOptions()), Is.EqualTo(new[] { "Nothing to download." }));
    }

    /// <summary>Each download is reckoned 15 seconds and a minute an hour of audio; more than 60 wait for the hour.</summary>
    [Test]
    public void The_downloads_time_is_reckoned_with_the_hourly_limit()
    {
        Assert.That(DownloadPlan.DownloadTime([.. Enumerable.Range(1, 9).Select(number => Ready(number, 1))]), Is.EqualTo(TimeSpan.FromSeconds(9 * 75)));
        Assert.That(DownloadPlan.DownloadTime([.. Enumerable.Range(1, 61).Select(number => Ready(number, 0.1))]), Is.EqualTo(TimeSpan.FromHours(1)));
    }

    /// <summary>
    /// The plan's lines: the table, the cost of what is ready and of what waits for his word, the account, the downloads'
    /// time, the room, Drive, and the channels that could be named.
    /// </summary>
    [Test]
    public void The_plans_lines()
    {
        var unlisted = Ready(2, 0.5);
        unlisted.Verdict = new GuardVerdict(GuardVerdictKind.NeedsWord, "needs your word", null);
        IReadOnlyList<BatchVideo> videos = [Ready(1, 1), unlisted];

        var lines = DownloadPlan.Lines(videos, links: 1, ["Risala, the creed"], transcribe: true, keyterms: 75, new ElevenLabsOptions(),
            "ElevenLabs account: 43,362 of 100,000 credits used this period; 56,638 left", new PlanRoom(158_760_044, 230_760_044, @"C:\", 2_000_000_000, @"C:\", 2_000_000_000),
            "Google Drive: everything, the audio included (YouTube:DriveUpload), about 151 MB of audio.",
            new ClassFolders(TestOptions.Create(new RecordingOptions()), TimeProvider.System, NullLogger<ClassFolders>.Instance));

        Assert.That(lines[0], Is.EqualTo("Read 2 videos from 1 link (1 playlist, \"Risala, the creed\"):"));
        Assert.That(lines[2], Does.StartWith("  01  Part 1  Your Channel  01:00:00  public").And.EndWith("ready"));
        Assert.That(lines[3], Does.StartWith("  02  Part 2").And.EndWith("needs your word"));
        Assert.That(lines.Skip(4), Is.EqualTo(new[]
        {
            "Ready: 1 video, 1 h 0 min: about $0.27 to transcribe ($0.22 an hour plus $0.05 for the glossary of 75 terms).",
            "Waiting for your word: 1 video, 30 min: about $0.14 to transcribe ($0.22 an hour plus $0.05 for the glossary of 75 terms).",
            "ElevenLabs account: 43,362 of 100,000 credits used this period; 56,638 left.",
            "1 download: about 2 minutes, 10 to 20 seconds apart, depending on your connection; then the transcripts, a few minutes each.",
            "Room: about 151 MB on C:\\ for the audio (mono, at YouTube's own rate), with 1.7 GB to spare.",
            "Google Drive: everything, the audio included (YouTube:DriveUpload), about 151 MB of audio.",
            "Channels: Your Channel (add one to YouTube:MayDownloadChannels if it is yours, or its owner allows downloading)."
        }));
    }

    /// <summary>The account's standing for the plan, in UsageReport's words: read, refused with what to allow, unavailable, or nothing asked.</summary>
    [Test]
    public void The_accounts_line_is_in_usage_reports_words()
    {
        var usage = new UsageReport(new FakeElevenLabsAccount(), ZonedTimeProvider.EasternSummer, new RecordingLogger<UsageReport>());

        Assert.That(usage.Describe(FakeElevenLabsAccount.Read(43_362)), Is.EqualTo("ElevenLabs account: 43,362 of 100,000 credits used this period; 56,638 left"));
        Assert.That(usage.Describe(new AccountReading(AccountReadingKind.Refused, Status: 401)), Does.StartWith("ElevenLabs would not show the account's credits to this key (HTTP 401)").And.EndWith(UsageReport.RefusedAdvice));
        Assert.That(usage.Describe(new AccountReading(AccountReadingKind.Unavailable, Reason: "timed out")), Is.EqualTo("The account's credits could not be read (timed out); nothing else is affected"));
        Assert.That(usage.Describe(AccountReading.Off), Is.Null);
    }

    /// <summary>The usage report keeps what a transcript cost, once it read the credits before and after it, for the batch's projection.</summary>
    [Test]
    public async Task The_usage_report_keeps_what_a_transcript_cost()
    {
        var account = new FakeElevenLabsAccount().Answer(FakeElevenLabsAccount.Read(1_000), FakeElevenLabsAccount.Read(6_200));
        var usage = new UsageReport(account, TimeProvider.System, new RecordingLogger<UsageReport>());

        usage.Begin(CancellationToken.None);
        await usage.ReportAsync(CancellationToken.None);

        Assert.That(usage.Charged, Is.EqualTo(new UsageCharge(5_200, 93_800)));
    }
}
