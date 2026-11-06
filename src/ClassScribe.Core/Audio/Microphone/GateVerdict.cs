namespace ClassScribe.Audio.Microphones;

/// <summary>What the gate says about one of the microphone's packets.</summary>
public enum GateVerdict
{
    /// <summary>Write it: the looks on both sides of its stamp found the followed app listening and Windows not muting.</summary>
    Write,

    /// <summary>Drop it: a look on either side found the gate closed, the looks on either side were too far apart, or it waited too long for one.</summary>
    Drop,

    /// <summary>Hold it: the look after its stamp has not happened yet.</summary>
    Undecided
}

/// <summary>Decides, packet by packet, which of the microphone's packets are written.</summary>
public interface IPacketGate
{
    /// <summary>The verdict for a packet whose first frame was recorded at <paramref name="stamp100ns"/> on the performance counter, in 100-nanosecond units.</summary>
    GateVerdict Judge(long stamp100ns);

    /// <summary>Raised when something changed the verdicts, such as a look arriving: the held packets are judged again.</summary>
    event EventHandler? Changed;
}

/// <summary>The microphone track's run, in numbers, for the end-of-class summary.</summary>
/// <param name="Heard">How long your voice was being recorded: the gate open with the microphone on.</param>
/// <param name="Stretches">How many stretches that was.</param>
/// <param name="Muted">How long the microphone was on but the followed app was not listening, had not opened it, used another one, or could not be read.</param>
/// <param name="Off">How long the microphone was switched off with V, or not yet switched on with it.</param>
/// <param name="MutedInWindows">How long the microphone was muted in Windows, whatever the app was doing: that mute closes the gate before anything else is looked at.</param>
/// <param name="Flickers">How many times the app's listening changed and changed back within a second, which the log leaves out.</param>
/// <param name="Cycles">How many times the app stopped listening for at least a second, while its process lived on, and then listened again: what an app whose mute Windows can see does on mute and unmute.</param>
public sealed record GateSummary(TimeSpan Heard, int Stretches, TimeSpan Muted, TimeSpan Off, TimeSpan MutedInWindows, int Flickers, int Cycles);
