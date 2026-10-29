namespace ClassScribe.Transcription;

/// <summary>
/// What one transcript is estimated to have cost, from the audio ElevenLabs counted and the published API price (see
/// <c>TranscriptionCost</c>).
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
