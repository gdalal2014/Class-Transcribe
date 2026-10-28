namespace ClassScribe.Commands;

/// <summary>What a microphone check is asked to do: mic-check on the command line, or menu choice 8, which asks for neither.</summary>
/// <param name="StopAfter">Stop by itself after this long (--seconds); null to watch until Q or Ctrl+C.</param>
/// <param name="LogFolder">The folder the log goes to, as a full path (--log-folder); null for the mic-check folder under Recording:OutputFolder.</param>
public sealed record MicCheckRequest(TimeSpan? StopAfter, string? LogFolder);

/// <summary>
/// One microphone check, resolved in a scope of its own by the runner, as <see cref="IRecordRun"/> is for a recording: the
/// check command implements it, and reads Ctrl+C through the signals of its scope.
/// </summary>
public interface IMicCheckRun
{
    /// <summary>Runs the check <paramref name="request"/> describes and returns the exit code.</summary>
    Task<int> RunAsync(MicCheckRequest request);
}
