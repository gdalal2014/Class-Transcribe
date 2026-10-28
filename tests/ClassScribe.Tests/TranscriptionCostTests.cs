using System.Globalization;
using ClassScribe.Audio;
using ClassScribe.Settings;
using ClassScribe.Transcription;

namespace ClassScribe.Tests;

/// <summary>
/// The cost estimate logged after a transcript: the audio ElevenLabs counted, or the file's length in its place, at the
/// API price, with the keyterms' surcharge when a glossary was sent; the words when the price is off or nothing is known;
/// and the same words whatever the computer's culture.
/// </summary>
[TestFixture]
public class TranscriptionCostTests
{
    /// <summary>Today's class: 02:11:24, which is 7,884 seconds, or 131.4 minutes.</summary>
    private static readonly TimeSpan TodaysClass = TimeSpan.FromSeconds(7884);

    private static readonly ElevenLabsOptions Defaults = new();

    private const string WithKeyterms = "ElevenLabs transcribed 131 minutes of audio (02:11:24): about $0.59 at the API price, $0.22 an hour plus $0.05 an hour for the 61 keyterms";
    private const string WithoutKeyterms = "ElevenLabs transcribed 131 minutes of audio (02:11:24): about $0.48 at the API price, $0.22 an hour";

    /// <summary>The line for a transcript of <paramref name="counted"/> seconds as ElevenLabs counted them, a file of <paramref name="file"/>, and <paramref name="keyterms"/> glossary terms.</summary>
    private static string Describe(TimeSpan? counted, TimeSpan? file, int keyterms, ElevenLabsOptions? settings = null) =>
        TranscriptionCost.Describe(TranscriptionCost.Estimate(counted, file, keyterms, settings ?? Defaults));

    /// <summary>Today's class with the 61 keyterms: 2.19 hours at $0.22 plus $0.05 an hour, about 59 cents.</summary>
    [Test]
    public void Todays_class_with_the_glossary_comes_to_about_59_cents()
    {
        Assert.That(Describe(TodaysClass, TodaysClass, 61), Is.EqualTo(WithKeyterms));
    }

    /// <summary>Without keyterms the base price alone counts: about 48 cents, and no surcharge is named.</summary>
    [Test]
    public void Without_keyterms_the_base_price_alone_counts()
    {
        Assert.That(Describe(TodaysClass, TodaysClass, 0), Is.EqualTo(WithoutKeyterms));
    }

    /// <summary>The dollars come from the exact seconds, not the rounded minutes.</summary>
    [Test]
    public void The_dollars_come_from_the_exact_seconds()
    {
        var estimate = TranscriptionCost.Estimate(TodaysClass, TodaysClass, 61, Defaults);

        Assert.That(estimate.Dollars, Is.EqualTo(7884 / 3600.0 * 0.27).Within(0.000001));
    }

    /// <summary>The minutes are rounded to the nearest: 59 minutes 36 seconds reads as 60 minutes, with the exact length beside it.</summary>
    [Test]
    public void The_minutes_are_rounded_to_the_nearest()
    {
        Assert.That(Describe(TimeSpan.FromSeconds(3576), null, 0), Does.StartWith("ElevenLabs transcribed 60 minutes of audio (00:59:36)"));
    }

    /// <summary>Under a minute the audio is given in seconds, and a sum under half a cent as less than a cent rather than $0.00.</summary>
    [Test]
    public void Under_a_minute_the_audio_is_in_seconds_and_a_tiny_sum_is_less_than_a_cent()
    {
        Assert.That(Describe(TimeSpan.FromSeconds(1.5), null, 0), Is.EqualTo("ElevenLabs transcribed 1 second of audio (00:00:01): less than a cent at the API price, $0.22 an hour"));
        Assert.That(Describe(TimeSpan.FromSeconds(30), null, 0), Does.StartWith("ElevenLabs transcribed 30 seconds of audio (00:00:30)"));
    }

    /// <summary>With the price set to 0 the line gives the audio alone and says which setting is 0; no sum is named.</summary>
    [Test]
    public void A_price_of_zero_logs_the_audio_without_a_price()
    {
        var line = Describe(TodaysClass, TodaysClass, 61, new ElevenLabsOptions { PricePerHourUsd = 0 });

        Assert.That(line, Is.EqualTo("ElevenLabs transcribed 131 minutes of audio (02:11:24); no price is estimated (ElevenLabs:PricePerHourUsd is 0)"));
        Assert.That(line, Does.Not.Contain("$"));
    }

    /// <summary>A keyterms price of 0 leaves the surcharge out although keyterms were sent: the base price alone, with no "plus".</summary>
    [Test]
    public void A_keyterms_price_of_zero_leaves_the_surcharge_out()
    {
        Assert.That(Describe(TodaysClass, TodaysClass, 61, new ElevenLabsOptions { KeytermsPricePerHourUsd = 0 }), Is.EqualTo(WithoutKeyterms));
    }

    /// <summary>One keyterm is one keyterm, and a price set to a tenth of a cent is shown as set.</summary>
    [Test]
    public void One_keyterm_is_singular_and_a_finer_price_is_shown_as_set()
    {
        Assert.That(Describe(TodaysClass, null, 1), Does.EndWith("plus $0.05 an hour for the 1 keyterm"));
        Assert.That(Describe(TodaysClass, null, 2, new ElevenLabsOptions { KeytermsPricePerHourUsd = 0.044 }), Does.EndWith("plus $0.044 an hour for the 2 keyterms"));
    }

    /// <summary>When neither ElevenLabs nor the file says how much audio there was, the line says the cost is not estimated and where it is.</summary>
    [Test]
    public void With_nothing_known_the_cost_is_not_estimated()
    {
        Assert.That(Describe(null, null, 61), Is.EqualTo(TranscriptionCost.NotEstimated));
        Assert.That(TranscriptionCost.Estimate(null, null, 61, Defaults).Dollars, Is.Null);
    }

    /// <summary>When ElevenLabs gave no count, the upload file's length stands in: here from a real 16 kHz WAV of two seconds.</summary>
    [Test]
    public void The_files_length_stands_in_when_ElevenLabs_gave_no_count()
    {
        using var folder = new TempFolder();
        var wav = folder.File("upload.wav");
        TestAudio.WriteWav(wav, 16000, new short[32000]);

        var line = Describe(null, new AudioFileFacts().AudioLength(wav), 0);

        Assert.That(line, Is.EqualTo("ElevenLabs transcribed 2 seconds of audio (00:00:02): less than a cent at the API price, $0.22 an hour"));
        Assert.That(Describe(null, TodaysClass, 0), Is.EqualTo(WithoutKeyterms));
    }

    /// <summary>
    /// ElevenLabs' count is the one charged for, so it wins; when it differs from the file's length by more than a minute,
    /// the line says so, with the file's length, so that a trimmed or padded answer is visible; within a minute nothing is said.
    /// </summary>
    [Test]
    public void ElevenLabs_count_wins_and_a_difference_over_a_minute_is_said()
    {
        var trimmed = Describe(TodaysClass, TimeSpan.FromSeconds(7740), 0);
        var close = Describe(TodaysClass, TimeSpan.FromSeconds(7850), 0);

        Assert.That(trimmed, Is.EqualTo("ElevenLabs transcribed 131 minutes of audio (02:11:24, counted by ElevenLabs; the file holds 02:09:00): about $0.48 at the API price, $0.22 an hour"));
        Assert.That(close, Is.EqualTo(WithoutKeyterms));
    }

    /// <summary>The words are written in the invariant culture: a German culture, which writes 0,59, changes nothing.</summary>
    [Test]
    public void The_words_are_the_same_under_a_German_culture()
    {
        var culture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
        try
        {
            Assert.That(Describe(TodaysClass, TodaysClass, 61), Is.EqualTo(WithKeyterms));
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
        }
    }
}
