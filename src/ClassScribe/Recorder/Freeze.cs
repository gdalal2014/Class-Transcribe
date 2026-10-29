namespace ClassScribe.Recorder;

/// <summary>
/// A time when ClassScribe did not run: the laptop slept, or it stayed awake while Windows held ClassScribe up, as Modern
/// Standby's Desktop Activity Moderator does with desktop programs (a debugger does the same).
/// </summary>
/// <param name="FromUtc">When ClassScribe was last seen running before it, by the time of day.</param>
/// <param name="ToUtc">When ClassScribe was first seen running again, by the time of day.</param>
/// <param name="FromTimestamp">
/// Where the stretch to leave out of a track that follows the clock starts, on the steady clock: the last tick before the
/// freeze plus <c>FreezeWatch.Margin</c>, so that the audio Windows still held at its start is kept.
/// </param>
/// <param name="ToTimestamp">Where that stretch ends: the first tick after the freeze less the margin, so that the audio Windows held on waking is kept.</param>
/// <param name="Slept">True when the laptop slept or hibernated; false when it stayed awake and only ClassScribe was held up.</param>
public sealed record Freeze(DateTimeOffset FromUtc, DateTimeOffset ToUtc, long FromTimestamp, long ToTimestamp, bool Slept);
