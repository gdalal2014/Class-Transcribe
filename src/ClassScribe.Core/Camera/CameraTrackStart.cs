using ClassScribe.Runs;

namespace ClassScribe.Camera;

/// <summary>Where a lesson's camera track begins: the camera, the recording's files, the position on the run's clock, and the cameras' offsets.</summary>
/// <param name="Camera">Which camera, and the picture's size, resolved by the window.</param>
/// <param name="Folder">The recording's folder.</param>
/// <param name="BaseName">The recording's base name, such as Tajwid 4_2026-10-19_14-00-02Z.</param>
/// <param name="StartPosition">Where on the run's clock the camera's piece begins: 0, or the length a continued lesson goes on from.</param>
/// <param name="Offsets">
/// Recording:CameraOffsets as the run's settings have it: a part of a camera's name to how many milliseconds its picture is
/// moved against the sound; the longest part in the chosen camera's name applies (see <see cref="CameraOffsets.For"/>).
/// </param>
public sealed record CameraTrackStart(CameraChoice Camera, string Folder, string BaseName, TimeSpan StartPosition, IReadOnlyDictionary<string, int>? Offsets = null);

/// <summary>Something about the lesson's camera that the run says while it lasts.</summary>
/// <param name="Alert">True for an Alert (the picture is not being recorded); false for a Notice.</param>
/// <param name="Short">A few words for the title.</param>
/// <param name="Text">The whole sentence, as the log says it.</param>
public sealed record CameraConcern(bool Alert, string Short, string Text);

/// <summary>A lesson's camera as the run sees it now (teach-video 6.13).</summary>
/// <param name="Name">The camera's name, or the name part asked for while none is found.</param>
/// <param name="Size">The picture's size, such as 720p.</param>
/// <param name="Capturing">True while the camera is open and its frames arrive.</param>
/// <param name="Dark">True while the picture has been black for 10 seconds.</param>
/// <param name="FramesPerSecond">The frames written a second, as the camera process said last.</param>
/// <param name="Frames">The frames written into this run's pieces.</param>
/// <param name="Dropped">The frames dropped in this run's pieces.</param>
/// <param name="Concern">What is wrong now, in words, while it lasts; null when nothing is.</param>
/// <param name="ContainerId">The camera's container, which a webcam's microphone shares; null before the camera is found, or when Windows does not say.</param>
/// <param name="GivenUp">True once the picture is given up for the rest of the run.</param>
public sealed record CameraTrackState(string Name, string? Size, bool Capturing, bool Dark, double FramesPerSecond, long Frames, long Dropped, CameraConcern? Concern,
    Guid? ContainerId = null, bool GivenUp = false);

/// <summary>Thrown when a run asks for the camera's picture and this build cannot record it.</summary>
/// <param name="message">Why, in words.</param>
public sealed class CameraNotBuiltException(string message) : InvalidOperationException(message);

/// <summary>Recording:CameraOffsets: how far a camera's picture is moved against the sound, by a part of its name.</summary>
public static class CameraOffsets
{
    /// <summary>The longest a name part may be.</summary>
    public const int LongestPart = 200;

    /// <summary>The most an offset may move the picture, either way, in milliseconds.</summary>
    public const int MostMilliseconds = 1000;

    /// <summary>
    /// The offset for the camera <paramref name="cameraName"/>: that of the longest part in <paramref name="offsets"/> found
    /// in its name, in any case; 0 when none is, or none is set.
    /// </summary>
    public static int For(string cameraName, IReadOnlyDictionary<string, int>? offsets) =>
        offsets is null
            ? 0
            : offsets.Where(entry => entry.Key.Trim().Length > 0 && cameraName.Contains(entry.Key.Trim(), StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(entry => entry.Key.Trim().Length)
                .Select(entry => entry.Value)
                .FirstOrDefault();
}
