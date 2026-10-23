using ClassScribe.Audio.Timeline;
using ClassScribe.Commands;
using Microsoft.Extensions.Logging;

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

/// <summary>
/// A lesson's camera track, beside the sound (teach-video 6.6 to 6.8, slice 7): the camera process with its record job, the
/// clock lines, P letting the camera go, and the stop that waits for the piece. Nothing that holds a session's gate may call
/// into it but <see cref="Nudge"/>, <see cref="Pause"/> and <see cref="Resume"/>, which only set a flag and wake its own loop.
/// </summary>
public interface ICameraTrack : IAsyncDisposable
{
    /// <summary>The camera as the run sees it now.</summary>
    CameraTrackState State { get; }

    /// <summary>P: lets the camera go during a pause, so that its light goes off; the clock's pause goes to the camera process first.</summary>
    void Pause();

    /// <summary>P again: takes the camera again.</summary>
    void Resume();

    /// <summary>The clock changed (a cut): its line goes to the camera process at once. Takes no lock.</summary>
    void Nudge();

    /// <summary>Lets the camera go for the rest of the run, as when the disk is nearly full, and says why: the sound records on.</summary>
    void GiveUp(string why);

    /// <summary>
    /// The sound ends at <paramref name="endPosition"/>: said to the camera process at once, before the sound is stopped, so
    /// that no frame after it is written while the sound is stopped and saved; <see cref="StopAsync"/> follows. Takes no lock.
    /// </summary>
    void EndAt(TimeSpan endPosition);

    /// <summary>Stops the picture at <paramref name="endPosition"/>, where the sound tracks end, and waits for the piece, at most 15 seconds.</summary>
    Task StopAsync(TimeSpan endPosition, CancellationToken cancellationToken);
}

/// <summary>
/// The camera's side of a run with a picture: it makes the lesson's camera track. A build that cannot record the picture says
/// why in <see cref="NotReady"/>, and the window refuses a run with a camera before anything records.
/// </summary>
public interface ICameraRecorder
{
    /// <summary>Why a run cannot record the camera's picture with this build, in words without a full stop; null when it can.</summary>
    string? NotReady { get; }

    /// <summary>Starts recording the camera's picture beside the sound, as <paramref name="start"/> says, placed on <paramref name="clock"/>.</summary>
    ICameraTrack Start(CameraTrackStart start, RecordingClock clock);
}

/// <summary>Thrown when a run asks for the camera's picture and this build cannot record it.</summary>
/// <param name="message">Why, in words.</param>
public sealed class CameraNotBuiltException(string message) : InvalidOperationException(message);

/// <summary>
/// A camera recorder that records nothing: every run with a camera is refused with <see cref="Words"/>, and
/// <see cref="Start"/> throws. It stands in where the picture cannot be recorded, as for the tests of the refusal.
/// </summary>
public sealed class CameraRecorderNotBuilt : ICameraRecorder
{
    /// <summary>What a refused run with the camera says.</summary>
    public const string Words = "The camera's recording is not built yet in this build of ClassScribe, so this recording cannot take the camera";

    /// <inheritdoc />
    public string? NotReady => Words;

    /// <inheritdoc />
    public ICameraTrack Start(CameraTrackStart start, RecordingClock clock) => throw new CameraNotBuiltException(Words);
}

/// <summary>
/// The camera recorder (slice 7): each lesson's camera track is a <see cref="CameraTrack"/>, which starts camera processes
/// with record jobs through <paramref name="starter"/>. Ready on Windows 10 version 2004 and later.
/// </summary>
/// <param name="cameras">Lists the cameras, opening none, to pin the run's camera by its id.</param>
/// <param name="starter">Starts camera processes.</param>
/// <param name="time">The clock everything is timed on.</param>
/// <param name="loggers">Makes the log under the category Camera.</param>
public sealed class CameraRecorder(ICameraProvider cameras, ICameraProcessStarter starter, TimeProvider time, ILoggerFactory loggers) : ICameraRecorder
{
    /// <summary>The log's category for the camera's lines in a run.</summary>
    public const string LogCategory = "Camera";

    /// <inheritdoc />
    public string? NotReady => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041) ? null : "The camera needs Windows 10 version 2004 (build 19041) or later";

    /// <inheritdoc />
    public ICameraTrack Start(CameraTrackStart start, RecordingClock clock)
    {
        var track = new CameraTrack(start, clock, cameras, starter, time, loggers.CreateLogger(LogCategory));
        track.Begin();
        return track;
    }
}

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
