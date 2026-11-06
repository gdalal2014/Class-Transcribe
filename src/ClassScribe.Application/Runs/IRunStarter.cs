namespace ClassScribe.Runs;

/// <summary>
/// Starts a run in a scope of its own, as the menu does (architecture 4.3): the one call a YouTube batch makes to record
/// each of its videos as it plays. The window's runner answers it, resolving the starter of runs in a fresh scope, so that
/// the run gets its own Ctrl+C signals, the settings as the gate last accepted them, and a fresh recording.
/// </summary>
public interface IRunStarter
{
    /// <summary>Records as <paramref name="request"/> says, started from <paramref name="origin"/>, and transcribes when it asks. Returns the exit code.</summary>
    Task<int> RecordAsync(RecordRequest request, StartOrigin origin);
}
