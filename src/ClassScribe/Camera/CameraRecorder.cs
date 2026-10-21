using ClassScribe.Commands;

namespace ClassScribe.Camera;

/// <summary>Where a lesson's camera track begins: the camera, the recording's files, and the position on the run's clock.</summary>
/// <param name="Camera">Which camera, and the picture's size, resolved by the window.</param>
/// <param name="Folder">The recording's folder.</param>
/// <param name="BaseName">The recording's base name, such as Tajwid 4_2026-10-19_14-00-02Z.</param>
/// <param name="StartPosition">Where on the run's clock the camera's piece begins: 0, or the length a continued lesson goes on from.</param>
public sealed record CameraTrackStart(CameraChoice Camera, string Folder, string BaseName, TimeSpan StartPosition);

/// <summary>
/// A lesson's camera track, beside the sound (teach-video 6.6 to 6.8, slice 7): the camera process with its record job, the
/// clock lines, P letting the camera go, and the stop that waits for the piece. Nothing implements it in this build.
/// </summary>
public interface ICameraTrack : IAsyncDisposable
{
    /// <summary>P: lets the camera go during a pause, so that its light goes off.</summary>
    void Pause();

    /// <summary>P again: takes the camera again.</summary>
    void Resume();

    /// <summary>Stops the picture at <paramref name="endPosition"/>, where the sound tracks end, and waits for the piece, at most 15 seconds.</summary>
    Task StopAsync(TimeSpan endPosition, CancellationToken cancellationToken);
}

/// <summary>
/// The camera's side of a run with a picture: the seam the camera process will fill (slice 7), after the camera probe has
/// chosen the route (the frame reader or Windows' own recorder). Until then <see cref="CameraRecorderNotBuilt"/> stands in
/// for it, so that no class can start with a half-built camera: the window refuses a run with a camera before anything
/// records, with <see cref="NotReady"/>, and the recording's factory throws if asked all the same.
/// </summary>
public interface ICameraRecorder
{
    /// <summary>Why a run cannot record the camera's picture with this build, in words without a full stop; null when it can.</summary>
    string? NotReady { get; }

    /// <summary>Starts recording the camera's picture beside the sound, as <paramref name="start"/> says.</summary>
    ICameraTrack Start(CameraTrackStart start);
}

/// <summary>Thrown when a run asks for the camera's picture and this build cannot record it.</summary>
/// <param name="message">Why, in words.</param>
public sealed class CameraNotBuiltException(string message) : InvalidOperationException(message);

/// <summary>
/// This build's camera recorder: none. The camera can be listed, checked, chosen and sized, but its picture is not recorded
/// yet, so every run with a camera is refused with <see cref="Words"/>, and <see cref="Start"/> throws.
/// </summary>
public sealed class CameraRecorderNotBuilt : ICameraRecorder
{
    /// <summary>What a refused run with the camera says.</summary>
    public const string Words = "The camera's recording is not built yet in this build of ClassScribe, so this recording cannot take the camera";

    /// <inheritdoc />
    public string? NotReady => Words;

    /// <inheritdoc />
    public ICameraTrack Start(CameraTrackStart start) => throw new CameraNotBuiltException(Words);
}
