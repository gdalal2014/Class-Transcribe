using ClassScribe.Runs;

namespace ClassScribe.Commands;

/// <summary>What the start of a run with a camera decided (see <see cref="CameraStart"/>).</summary>
public abstract record CameraDecision
{
    /// <summary>Only the kinds declared here derive from it.</summary>
    private CameraDecision()
    {
    }

    /// <summary>Start the run <paramref name="Request"/> describes: with the camera, its size resolved, or the sound only.</summary>
    /// <param name="Request">The request to run.</param>
    public sealed record Go(RecordRequest Request) : CameraDecision;

    /// <summary>Esc at a question: nothing is started, and the menu comes back.</summary>
    public sealed record Back : CameraDecision;

    /// <summary>The camera cannot be used, and nobody could choose the sound only: nothing is started, with <paramref name="ExitCode"/>.</summary>
    /// <param name="ExitCode">The exit code, 1.</param>
    public sealed record Refused(int ExitCode) : CameraDecision;
}
