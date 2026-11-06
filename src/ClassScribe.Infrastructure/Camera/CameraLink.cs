using ClassScribe.Audio.Timeline;
using ClassScribe.Link;

namespace ClassScribe.Camera;

/// <summary>
/// The adapter of <see cref="ICameraLink"/>: the lines of <see cref="CameraProtocol"/> as <see cref="CameraLines"/> sends and
/// reads them, over one <see cref="LineChannel"/>, the starter's end of the camera process's standard input and output. The
/// channel belongs to the process that holds this link, which closes it.
/// </summary>
/// <param name="channel">The lines to and from the process.</param>
public sealed class CameraLink(LineChannel channel) : ICameraLink
{
    /// <inheritdoc />
    public ValueTask SendJobAsync(CameraJob job, CancellationToken cancellationToken) => CameraLines.SendJobAsync(channel, job, cancellationToken);

    /// <inheritdoc />
    public ValueTask SendStopAsync(CameraStop stop, CancellationToken cancellationToken) => CameraLines.SendStopAsync(channel, stop, cancellationToken);

    /// <inheritdoc />
    public ValueTask SendEndAsync(CameraStop end, CancellationToken cancellationToken) => CameraLines.SendEndAsync(channel, end, cancellationToken);

    /// <inheritdoc />
    public ValueTask SendClockAsync(ClockState clock, CancellationToken cancellationToken) => CameraLines.SendClockAsync(channel, clock, cancellationToken);

    /// <inheritdoc />
    public ValueTask SendSignalAsync(string type, CancellationToken cancellationToken) => CameraLines.SendSignalAsync(channel, type, cancellationToken);

    /// <inheritdoc />
    public async ValueTask<CameraLine?> ReceiveAsync(CancellationToken cancellationToken) =>
        await channel.ReceiveAsync(cancellationToken) is { } envelope ? new CameraLine(envelope.Type, CameraLines.Read(envelope)) : null;
}
