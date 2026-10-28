namespace ClassScribe.Recorder;

/// <summary>
/// Whether the process a run file names still runs, and when the computer last started. A run file names the process that
/// records the run by its id and its start time, because Windows gives an id to a new process once the old one has ended:
/// the id alone could keep a run that ended long ago looking alive.
/// </summary>
public interface IProcessLiveness
{
    /// <summary>
    /// How far apart two readings of one process's start time may be: the one a run file holds, and the one Windows gives
    /// now. Both come from Windows' record of when it made the process, so they agree to the tick; the leeway only guards
    /// against a rounding on the way.
    /// </summary>
    static TimeSpan StartTimeTolerance => TimeSpan.FromSeconds(2);

    /// <summary>True when <paramref name="actual"/> and <paramref name="recorded"/> are within <see cref="StartTimeTolerance"/> of each other, either way: the same process.</summary>
    static bool StartedTogether(DateTimeOffset actual, DateTimeOffset recorded) => (actual - recorded).Duration() <= StartTimeTolerance;

    /// <summary>
    /// True when a process with the id <paramref name="processId"/> runs now and started at <paramref name="startedUtc"/>,
    /// within <see cref="StartTimeTolerance"/>: the start time as <see cref="System.Diagnostics.Process.StartTime"/> gives
    /// it, in UTC, which is what a run file holds.
    /// </summary>
    bool IsAlive(int processId, DateTimeOffset startedUtc);

    /// <summary>When the computer last started, in UTC. A heartbeat before it means Windows restarted after it.</summary>
    DateTimeOffset LastBootUtc();
}
