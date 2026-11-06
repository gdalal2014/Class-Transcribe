namespace ClassScribe.Audio;

/// <summary>One track of a recording, written to segment files of its own.</summary>
public enum Track
{
    /// <summary>
    /// The main track: the class, or your microphone in person. Its files carry the recording's plain names, such as
    /// Kalam_2026-10-15_23-00-02Z.wav and Kalam_2026-10-15_23-00-02Z_part2.wav.
    /// </summary>
    Main,

    /// <summary>Your microphone, recorded with the class. Its files end in .mic.wav.</summary>
    Microphone
}
