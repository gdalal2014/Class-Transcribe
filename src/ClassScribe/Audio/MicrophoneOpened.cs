using ClassScribe.Audio.Microphones;

namespace ClassScribe.Audio;

/// <summary>A capture of a microphone that was just opened: which microphone, and how Windows processes its audio.</summary>
/// <param name="Microphone">The microphone, as Windows lists it.</param>
/// <param name="Processing">The processing the capture ended up with: RAW, or Default when RAW was asked for but not available.</param>
public sealed record MicrophoneOpened(Microphone Microphone, MicrophoneProcessing Processing);
