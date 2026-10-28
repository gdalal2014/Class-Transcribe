using System.Globalization;
using ClassScribe.Settings;

namespace ClassScribe.Transcription;

/// <summary>
/// Estimates what a transcript cost and words it for the log. ElevenLabs' answer carries no price, so the estimate is the
/// audio it counted (its audio_duration_secs, or else the length of the WAV file ClassScribe uploaded) at the price the
/// settings give, which is the API price list's (https://elevenlabs.io/pricing/api): $0.22 an hour for Scribe v2, plus
/// $0.05 an hour when keyterms are sent. ElevenLabs says only that it bills per audio minute, so the line says "about".
/// Dollars are never turned into credits: the in-app credit rate and the API's dollar rate cannot be reconciled from
/// ElevenLabs' pages, so credits come only from the account itself (see <see cref="UsageReport"/>).
/// </summary>
public static class TranscriptionCost
{
    /// <summary>How far ElevenLabs' count and the file's length may differ before the line says whose number it uses.</summary>
    public static readonly TimeSpan CountsMayDifferBy = TimeSpan.FromMinutes(1);

    /// <summary>The words when neither ElevenLabs nor the file says how much audio there was.</summary>
    public const string NotEstimated = "ElevenLabs did not say how much audio it counted, and the file's length is not known here, so the cost is not estimated; the usage page on elevenlabs.io has it";

    /// <summary>
    /// The estimate for a transcript of <paramref name="counted"/> seconds of audio, as ElevenLabs counted them (null when it
    /// did not say), of a file <paramref name="fileLength"/> long (null when that is not known), sent with
    /// <paramref name="keyterms"/> glossary terms, at the prices in <paramref name="settings"/>. ElevenLabs' count is the
    /// one charged for, so it wins when both are known.
    /// </summary>
    public static CostEstimate Estimate(TimeSpan? counted, TimeSpan? fileLength, int keyterms, ElevenLabsOptions settings)
    {
        var differ = counted is { } byElevenLabs && fileLength is { } byFile && (byElevenLabs - byFile).Duration() > CountsMayDifferBy;
        return new CostEstimate(counted ?? fileLength, fileLength, differ, keyterms, Finite(settings.PricePerHourUsd), Finite(settings.KeytermsPricePerHourUsd));
    }

    /// <summary>
    /// The line for the log, in the invariant culture: how much audio ElevenLabs transcribed, in minutes rounded to the
    /// nearest (in seconds under a minute) and as hh:mm:ss, and about how much that comes to at the API price, with the price
    /// an hour and what the keyterms add when they were sent; the audio alone when the price is 0; and that the cost is not
    /// estimated when the audio's length is not known at all.
    /// </summary>
    public static string Describe(CostEstimate estimate)
    {
        if (estimate.Audio is not { } audio) return NotEstimated;

        var length = audio < TimeSpan.FromMinutes(1)
            ? Count((long)audio.TotalSeconds, "second")
            : Count((long)Math.Round(audio.TotalMinutes, MidpointRounding.AwayFromZero), "minute");
        var counted = estimate.CountedByElevenLabs && estimate.FileLength is { } file
            ? $"{Durations.Format(audio)}, counted by ElevenLabs; the file holds {Durations.Format(file)}"
            : Durations.Format(audio);
        var transcribed = $"ElevenLabs transcribed {length} of audio ({counted})";

        if (estimate.Dollars is not { } dollars)
            return $"{transcribed}; no price is estimated ({ElevenLabsOptions.SectionName}:{nameof(ElevenLabsOptions.PricePerHourUsd)} is 0)";

        var amount = dollars < 0.005 ? "less than a cent" : $"about {Money(dollars)}";
        var keyterms = estimate.Keyterms > 0 && estimate.KeytermsPricePerHour > 0
            ? $" plus {Price(estimate.KeytermsPricePerHour)} an hour for the {Count(estimate.Keyterms, "keyterm")}"
            : "";
        return $"{transcribed}: {amount} at the API price, {Price(estimate.PricePerHour)} an hour{keyterms}";
    }

    /// <summary>A sum in dollars to the cent, such as $0.59.</summary>
    private static string Money(double dollars) => string.Create(CultureInfo.InvariantCulture, $"${dollars:0.00}");

    /// <summary>A price an hour, to the cent, or to a tenth of a cent when the setting goes that far, such as $0.22 or $0.044.</summary>
    private static string Price(double price) => string.Create(CultureInfo.InvariantCulture, $"${price:0.00##}");

    /// <summary>A number with its unit, singular for one, such as "1 minute" or "131 minutes".</summary>
    private static string Count(long number, string unit) => string.Create(CultureInfo.InvariantCulture, $"{number} {unit}{(number == 1 ? "" : "s")}");

    /// <summary>A price as a finite number; NaN or infinity, which the settings check refuses, counts as 0 should it reach here.</summary>
    private static double Finite(double price) => double.IsFinite(price) ? price : 0;
}
