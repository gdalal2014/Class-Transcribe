namespace ClassScribe.Audio.Microphones;

/// <summary>The outcome of one look at the microphones.</summary>
/// <param name="Chosen">The microphone to record, or null while there is none to record.</param>
/// <param name="Why">Why that one, or what is waited for, in words for the log.</param>
/// <param name="FellBack">True while another microphone stands in for the chosen one, which has gone.</param>
public sealed record MicrophonePick(Microphone? Chosen, string Why, bool FellBack);
