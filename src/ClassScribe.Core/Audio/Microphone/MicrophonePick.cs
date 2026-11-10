namespace ClassScribe.Core.Audio.Microphones;

/// <summary>The outcome of one look at the microphones.</summary>
/// <param name="Chosen">The microphone to record, or null while there is none to record.</param>
/// <param name="Why">Why that one, or what is waited for, in words for the log.</param>
/// <param name="FellBack">True while another microphone stands in for the chosen one, which has gone.</param>
/// <param name="InsteadOf">
/// The part of a name the microphone was asked for by (--mic, menu choice 7 or Recording:Microphone) when
/// <paramref name="Chosen"/> is recorded in its place because the class app uses it; null when the microphone chosen is
/// the one asked for, when none was asked for, and while another stands in. A wait for the class app's microphone
/// carries it too, so that a moment's absence of that microphone neither clears nor repeats what is said about it.
/// </param>
/// <param name="WaitingOn">
/// While the chosen microphone is gone and nothing stands in for it: that microphone, waited for, which nothing opens
/// (<paramref name="Chosen"/> is null); null otherwise, as before anything has been chosen.
/// </param>
public sealed record MicrophonePick(Microphone? Chosen, string Why, bool FellBack, string? InsteadOf = null, Microphone? WaitingOn = null);

/// <summary>
/// The microphone a recording names, with the chooser's words for it, read as one, so that a line never pairs one
/// microphone's name with another's reason: the microphone recorded now, or else the one the last look picked, which V
/// or the next capture would open, or else the one it waits for while that one is gone and nothing stands in.
/// </summary>
/// <param name="Microphone">The microphone, as Windows lists it (as last listed, for one waited for).</param>
/// <param name="Why">Why that microphone, in the chooser's words at the look that picked it; null when that look picked another, and for one waited for, and the line then leaves the reason out.</param>
/// <param name="InsteadOf">The part of a name asked for that this microphone is recorded in place of, because the class app uses it (see <see cref="MicrophonePick.InsteadOf"/>); null otherwise.</param>
/// <param name="Waiting">
/// True when it is only waited for (<see cref="MicrophonePick.WaitingOn"/>): gone, with nothing standing in, so neither
/// recorded nor about to be; a run names it as waited for, and gives its advice once it is picked again.
/// </param>
public sealed record NamedMicrophone(Microphone Microphone, string? Why, string? InsteadOf = null, bool Waiting = false);
