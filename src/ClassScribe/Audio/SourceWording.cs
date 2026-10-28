namespace ClassScribe.Audio;

/// <summary>The words a recording session's messages use for what its source records, so that each message names it rightly.</summary>
/// <param name="Thing">
/// What cannot be captured when capturing fails, as in "Cannot capture an output device right now": an output device, the
/// app "chrome", or, for a microphone, the microphone.
/// </param>
/// <param name="Capturing">
/// What is being captured, as in "Could not start capturing system audio yet": system audio, the sound of "chrome", or the
/// microphone.
/// </param>
public sealed record SourceWording(string Thing, string Capturing);
