namespace ClassScribe.Transcription;

/// <summary>What one transcript cost the account, read from its credits before and after it.</summary>
/// <param name="Credits">The credits the transcript used.</param>
/// <param name="Left">The credits left this period afterwards; null when the account has no limit.</param>
public sealed record UsageCharge(long Credits, long? Left);
