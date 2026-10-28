namespace ClassScribe.Audio.Microphones;

/// <summary>One line of the microphone check's log: when it was written, what kind of line it is, and its text.</summary>
/// <param name="At">When the line was written, in local time, which the log shows to the millisecond.</param>
/// <param name="Kind">The kind of line: <see cref="Mic"/>, <see cref="Session"/>, <see cref="Privacy"/>, <see cref="Mark"/>, <see cref="Note"/>, <see cref="Error"/> or <see cref="Self"/>.</param>
/// <param name="Text">What the line says.</param>
public sealed record MicCheckLine(DateTimeOffset At, string Kind, string Text)
{
    /// <summary>A line about one microphone: at start, became active, changed, or no longer active.</summary>
    public const string Mic = "MIC";

    /// <summary>A line about one app's session on one microphone: at start, appeared, changed, or gone.</summary>
    public const string Session = "SESSION";

    /// <summary>A line from Windows' microphone usage record: its settings, and each app's use starting and stopping.</summary>
    public const string Privacy = "PRIVACY";

    /// <summary>A numbered mark, written when M is pressed.</summary>
    public const string Mark = "MARK";

    /// <summary>A remark of the check's own, such as a gap after the laptop slept, or a source that can be read again.</summary>
    public const string Note = "NOTE";

    /// <summary>Something the check could not read; it keeps trying.</summary>
    public const string Error = "ERROR";

    /// <summary>The check's own process seen using a microphone, which should never happen.</summary>
    public const string Self = "SELF";
}
