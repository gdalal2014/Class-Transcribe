namespace ClassScribe.Runs;

/// <summary>How often a run's file is written while the run lasts, alike in every home of a run.</summary>
public static class RunTimings
{
    /// <summary>How often the run file's heartbeat is written while recording.</summary>
    public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(10);
}