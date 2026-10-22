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

    /// <summary>Sends the sound's end, <paramref name="end"/>, to a record job, ahead of its stop (see <see cref="CameraProtocol.End"/>).</summary>
    public static ValueTask SendEndAsync(LineChannel channel, CameraStop end, CancellationToken cancellationToken) =>
        channel.SendAsync(CameraProtocol.End, end, CameraJson.Default.CameraStop, cancellationToken);

    /// <summary>Sends the recording clock's whole state to a record job (see <see cref="CameraProtocol.Clock"/>).</summary>
    public static ValueTask SendClockAsync(LineChannel channel, Audio.Timeline.ClockState clock, CancellationToken cancellationToken) =>
        channel.SendAsync(CameraProtocol.Clock, clock, CameraJson.Default.ClockState, cancellationToken);

    /// <summary>Sends a line that needs no body: <see cref="CameraProtocol.Off"/>, <see cref="CameraProtocol.On"/> or <see cref="CameraProtocol.Cancel"/>.</summary>
    public static ValueTask SendSignalAsync(LineChannel channel, string type, CancellationToken cancellationToken) =>
        channel.SendAsync(type, new CameraEmpty(), CameraJson.Default.CameraEmpty, cancellationToken);

    /// <summary>
    /// The message <paramref name="envelope"/> holds, as its type says: a <see cref="CameraLog"/>, <see cref="CameraFailed"/>,
    /// <see cref="CameraOpened"/> and so on; null for a type this build does not know, which a reader passes over. Throws a
    /// <see cref="JsonException"/> for a body that is not its type's, none or one that is not an object included.
    /// </summary>
    public static object? Read(Envelope envelope)
    {
        Func<JsonElement, object?>? read = envelope.Type switch
        {
            CameraProtocol.Log => body => body.Deserialize(CameraJson.Default.CameraLog),
            CameraProtocol.Failed => body => body.Deserialize(CameraJson.Default.CameraFailed),
            CameraProtocol.Opened => body => body.Deserialize(CameraJson.Default.CameraOpened),
            CameraProtocol.Status => body => body.Deserialize(CameraJson.Default.CameraStatus),
            CameraProtocol.Problem => body => body.Deserialize(CameraJson.Default.CameraProblem),
            CameraProtocol.Piece => body => body.Deserialize(CameraJson.Default.CameraPiece),
            CameraProtocol.Progress => body => body.Deserialize(CameraJson.Default.CameraProgress),
            CameraProtocol.Made => body => body.Deserialize(CameraJson.Default.CameraMade),
            _ => null
        };
        if (read is null) return null;

        // A line without a body holds an undefined element, which Deserialize refuses with an InvalidOperationException, and a
        // null body would read as no message at all: both are a body that is not the type's.
        return envelope.Body.ValueKind == JsonValueKind.Object
            ? read(envelope.Body)
            : throw new JsonException($"The camera's \"{envelope.Type}\" line has no body that is an object.");
    }
}
