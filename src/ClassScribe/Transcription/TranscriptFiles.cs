namespace ClassScribe.Transcription;

/// <summary>Where a transcript was saved: the Markdown document and ElevenLabs' answer next to it.</summary>
public sealed record TranscriptFiles(string TranscriptPath, string JsonPath);

/// <summary>
/// A recording is longer than ClassScribe sends in one file to ElevenLabs (see <see cref="TranscriptionLimits.Sends"/>), so
/// nothing of it was prepared or sent. Trying again would find the same length.
/// </summary>
public sealed class RecordingTooLongException(string message) : Exception(message);
