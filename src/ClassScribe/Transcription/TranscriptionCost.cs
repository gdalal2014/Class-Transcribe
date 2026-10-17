using System.Globalization;
using ClassScribe.Settings;

namespace ClassScribe.Transcription;

/// <summary>
/// What one transcript is estimated to have cost, from the audio ElevenLabs counted and the published API price (see
/// <see cref="TranscriptionCost"/>).
/// </summary>
/// <param name="Audio">The audio the price is reckoned on: what ElevenLabs said it counted, or else the upload file's length; null when neither is known.</param>
/// <param name="FileLength">The upload file's length, when ClassScribe wrote it; null for a file uploaded as it was.</param>
/// <param name="CountedByElevenLabs">True when ElevenLabs' count and the file's length differ by more than a minute, so that the line says whose number it uses.</param>
/// <param name="Keyterms">How many glossary terms were sent; keyterm prompting is charged when there is at least one.</param>
/// <param name="PricePerHour">ElevenLabs:PricePerHourUsd: the API price of an hour of audio, in dollars; 0 or less turns the price off.</param>
/// <param name="KeytermsPricePerHour">ElevenLabs:KeytermsPricePerHourUsd: what keyterm prompting adds an hour, in dollars.</param>
public sealed record CostEstimate(TimeSpan? Audio, TimeSpan? FileLength, bool CountedByElevenLabs, int Keyterms, double PricePerHour, double KeytermsPricePerHour)
{
    /// <summary>The estimate in dollars, from the exact seconds; null when the audio's length is not known or the price is off.</summary>
    public double? Dollars => Audio is { } audio && PricePerHour > 0
        ? audio.TotalHours * (PricePerHour + (Keyterms > 0 && KeytermsPricePerHour > 0 ? KeytermsPricePerHour : 0))
        : null;
}

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
