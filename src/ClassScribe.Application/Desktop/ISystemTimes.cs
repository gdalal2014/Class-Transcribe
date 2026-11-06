using ClassScribe.Recorder;

namespace ClassScribe.Desktop;

/// <summary>
/// The computer's clocks, as <see cref="FreezeWatch"/> reads them to find a time when ClassScribe did not run,
/// and to tell whether the laptop slept then or stayed awake while Windows held ClassScribe up.
/// </summary>
public interface ISystemTimes
{
    /// <summary>How many counts of <see cref="Timestamp"/> make a second.</summary>
    long TimestampFrequency { get; }

    /// <summary>
    /// The steady clock (<see cref="TimeProvider.GetTimestamp"/>, Windows' performance counter). It counts the time the
    /// laptop sleeps, and Windows setting its time of day does not move it.
    /// </summary>
    long Timestamp();

    /// <summary>The time of day, in UTC, for the log.</summary>
    DateTimeOffset UtcNow();

    /// <summary>Windows' interrupt time, in units of 100 nanoseconds since Windows started. It counts the time the laptop sleeps.</summary>
    long InterruptTime100ns();

    /// <summary>
    /// Windows' unbiased interrupt time, in units of 100 nanoseconds. It leaves out the time the laptop sleeps or
    /// hibernates, so it falls behind <see cref="InterruptTime100ns"/> by exactly that time.
    /// </summary>
    long UnbiasedInterruptTime100ns();
}
