namespace ClassScribe.Audio;

/// <summary>What a recording holds: the class alone, the class and your microphone, or your microphone alone, in person.</summary>
public enum RecordingMode
{
    /// <summary>The class alone, from an output device or one app, as ClassScribe has always recorded it.</summary>
    Class,

    /// <summary>The class and your microphone, each on a track of its own, both on one clock, mixed into one upload.</summary>
    ClassAndMicrophone,

    /// <summary>Your microphone alone, recording the room, for a class in person.</summary>
    Microphone
}
