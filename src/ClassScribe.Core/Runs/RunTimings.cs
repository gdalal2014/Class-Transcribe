namespace ClassScribe.Runs;

/// <summary>How often a run's file is written while the run lasts, and how long it is kept once it has nothing left to tell, alike in every home of a run.</summary>
public static class RunTimings
{
    /// <summary>How often the run file's heartbeat is written while recording.</summary>
    public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(10);

    /// <summary>How long a run that has nothing left to report is kept after its last update: 30 days.</summary>
    public static readonly TimeSpan KeepSeenRunsFor = TimeSpan.FromDays(30);
}