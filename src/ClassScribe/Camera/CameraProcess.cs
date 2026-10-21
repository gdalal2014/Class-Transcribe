using System.Text.Json;
using ClassScribe.Link;

namespace ClassScribe.Camera;

/// <summary>A camera process ClassScribe started, and the lines it speaks over its standard input and output.</summary>
public interface ICameraProcess : IAsyncDisposable
{
    /// <summary>Windows' id for the process.</summary>
    int ProcessId { get; }

    /// <summary>The lines to and from the process (see <see cref="CameraProtocol"/>).</summary>
    LineChannel Channel { get; }

    /// <summary>The process's exit code once it has ended; null while it runs.</summary>
    int? ExitCode { get; }

    /// <summary>Ends the process at once, as a stop that waited too long does.</summary>
    void Kill();
}

/// <summary>
/// Starts camera processes (teach-video 6.2, amendment 24): this very program, <c>--camera-process &lt;job id&gt;</c>, in a
/// kill-on-close job of the starter's, so that it never outlives the process that started it.
/// </summary>
public interface ICameraProcessStarter
{
    /// <summary>
    /// Starts a camera process for the job <paramref name="jobId"/>, a well-formed id (<see cref="Commands.RunIds"/>); throws
    /// an <see cref="ArgumentException"/> for any other before anything starts, and a
    /// <see cref="System.ComponentModel.Win32Exception"/> when Windows does not start it.
    /// </summary>
    ICameraProcess Start(string jobId);
}

/// <summary>The lines of <see cref="CameraProtocol"/> as a starter sends and reads them.</summary>
public static class CameraLines
{
    /// <summary>Sends <paramref name="job"/>, the first line a camera process reads.</summary>
    public static ValueTask SendJobAsync(LineChannel channel, CameraJob job, CancellationToken cancellationToken) =>
        channel.SendAsync(CameraProtocol.Job, job, CameraJson.Default.CameraJob, cancellationToken);

    /// <summary>Sends <paramref name="stop"/> to a record job.</summary>
    public static ValueTask SendStopAsync(LineChannel channel, CameraStop stop, CancellationToken cancellationToken) =>
        channel.SendAsync(CameraProtocol.Stop, stop, CameraJson.Default.CameraStop, cancellationToken);

    /// <summary>
    /// The message <paramref name="envelope"/> holds, as its type says: a <see cref="CameraLog"/>, <see cref="CameraFailed"/>,
    /// <see cref="CameraOpened"/> and so on; null for a type this build does not know, which a reader passes over. Throws a
    /// <see cref="JsonException"/> for a body that is not its type's.
    /// </summary>
    public static object? Read(Envelope envelope) => envelope.Type switch
    {
        CameraProtocol.Log => envelope.Body.Deserialize(CameraJson.Default.CameraLog),
        CameraProtocol.Failed => envelope.Body.Deserialize(CameraJson.Default.CameraFailed),
        CameraProtocol.Opened => envelope.Body.Deserialize(CameraJson.Default.CameraOpened),
        CameraProtocol.Status => envelope.Body.Deserialize(CameraJson.Default.CameraStatus),
        CameraProtocol.Problem => envelope.Body.Deserialize(CameraJson.Default.CameraProblem),
        CameraProtocol.Piece => envelope.Body.Deserialize(CameraJson.Default.CameraPiece),
        CameraProtocol.Progress => envelope.Body.Deserialize(CameraJson.Default.CameraProgress),
        CameraProtocol.Made => envelope.Body.Deserialize(CameraJson.Default.CameraMade),
        _ => null
    };
}
