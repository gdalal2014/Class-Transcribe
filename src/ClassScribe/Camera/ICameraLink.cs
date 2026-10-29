using ClassScribe.Audio.Timeline;

namespace ClassScribe.Camera;

/// <summary>One line from the camera process, as its record: a <see cref="CameraLog"/>, <see cref="CameraOpened"/>, <see cref="CameraPiece"/> and so on (see <see cref="CameraProtocol"/>).</summary>
/// <param name="Type">The line's type, as the protocol names it.</param>
/// <param name="Message">The message the line holds; null for a type this build does not know, which a reader passes over.</param>
public sealed record CameraLine(string Type, object? Message);

/// <summary>
/// The camera process, message by message (architecture 4.3): the job, a stop, the sound's end, the clock's state and a
/// signal sent to it; what it says received, each line as its record. The lines are those of <see cref="CameraProtocol"/>;
/// how they travel (UTF-8 JSON over the process's standard input and output) is the adapter's. A message longer than a
/// line (<c>LinkProtocol.MaxLineBytes</c>, 64 KB) throws an <see cref="ArgumentException"/> and is not sent; a send to
/// a process that is gone, or whose lines are closed, throws the stream's own exception; no send checks its type.
/// </summary>
public interface ICameraLink
{
    /// <summary>Sends <paramref name="job"/>, the first line a camera process reads.</summary>
    ValueTask SendJobAsync(CameraJob job, CancellationToken cancellationToken);

    /// <summary>Sends <paramref name="stop"/> to a record job.</summary>
    ValueTask SendStopAsync(CameraStop stop, CancellationToken cancellationToken);

    /// <summary>Sends the sound's end, <paramref name="end"/>, to a record job, ahead of its stop (see <see cref="CameraProtocol.End"/>).</summary>
    ValueTask SendEndAsync(CameraStop end, CancellationToken cancellationToken);

    /// <summary>Sends the recording clock's whole state to a record job (see <see cref="CameraProtocol.Clock"/>).</summary>
    ValueTask SendClockAsync(ClockState clock, CancellationToken cancellationToken);

    /// <summary>Sends a line that needs no body: <see cref="CameraProtocol.Off"/>, <see cref="CameraProtocol.On"/> or <see cref="CameraProtocol.Cancel"/>.</summary>
    ValueTask SendSignalAsync(string type, CancellationToken cancellationToken);

    /// <summary>
    /// Receives the next line, waiting for it as long as it takes, as its record; null at the end of the lines, as when the
    /// process has ended. A line whose body is not its type's throws a <see cref="System.Text.Json.JsonException"/>, and one
    /// that is no message of the link an <see cref="InvalidDataException"/>. A receive cancelled by
    /// <paramref name="cancellationToken"/> throws an <see cref="OperationCanceledException"/> and loses nothing.
    /// </summary>
    ValueTask<CameraLine?> ReceiveAsync(CancellationToken cancellationToken);
}
