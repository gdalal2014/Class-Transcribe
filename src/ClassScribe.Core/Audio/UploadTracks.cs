namespace ClassScribe.Audio;

/// <summary>
/// The tracks of a recording as they go into the upload: the main track, which is the class, or your microphone in
/// person, and your microphone's track when it was recorded with the class; each as its segment files in recording
/// order, and the microphone's empty when the recording has no such track.
/// </summary>
/// <param name="Main">The segments of the main track, in recording order.</param>
/// <param name="Microphone">The segments of your microphone's track, in recording order; empty when there is none.</param>
public sealed record UploadTracks(IReadOnlyList<string> Main, IReadOnlyList<string> Microphone);

/// <summary>The audio files were read without trouble, but they hold nothing to transcribe.</summary>
public sealed class NoAudioException(string message) : Exception(message);
