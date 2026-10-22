using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClassScribe.Commands;

namespace ClassScribe.Camera;

/// <summary>
/// The camera process's role and the lines it speaks (teach-video 6.2, amendment 24): ClassScribe started as
/// <c>ClassScribe.exe --camera-process &lt;job id&gt;</c>, by a window or a recorder, in a kill-on-close job of the starter's,
/// talking over its standard input and output in lines of UTF-8 JSON of at most 64 KB, through the link's
/// <see cref="Link.LineChannel"/>: no named pipe, so nothing to name or guard. Frames never cross, only control and status.
/// To the camera process: the job first, then for a record job <see cref="Clock"/>, <see cref="Off"/> and <see cref="On"/>
/// (P), <see cref="Stop"/> and <see cref="Cancel"/>. From it: <see cref="Opened"/>, <see cref="Status"/> once a second,
/// <see cref="Problem"/>, <see cref="Piece"/>, <see cref="Progress"/>, <see cref="Made"/> or <see cref="Failed"/>, and
/// <see cref="Log"/> lines, which the starter writes into its own log under the category Camera. Its exit codes are
/// internal: 0 done, 1 failed, 4 a job it cannot take, 130 cancelled. It takes the hold and the check jobs, opening the camera
/// only shared; a record or finish job is answered with <see cref="Failed"/>, and ends with 4.
/// </summary>
public static class CameraProtocol
{
    /// <summary>The version of the lines this build speaks; a job of another version is not taken.</summary>
    public const int Version = 1;

    /// <summary>What makes ClassScribe a camera process, followed by the job's id.</summary>
    public const string ProcessSwitch = "--camera-process";

    /// <summary>The camera process's exit code when its job is done.</summary>
    public const int DoneExitCode = 0;

    /// <summary>The camera process's exit code when its job failed.</summary>
    public const int FailedExitCode = 1;

    /// <summary>The camera process's exit code for a job it cannot take: none, one it cannot read, one of another version, or one this build has not built.</summary>
    public const int JobNotTakenExitCode = 4;

    /// <summary>The camera process's exit code when its job was cancelled.</summary>
    public const int CancelledExitCode = 130;

    /// <summary>The job, the first line to the camera process (see <see cref="CameraJob"/>).</summary>
    public const string Job = "job";

    /// <summary>The recording clock's state, sent whenever it changes (slice 7: ClockState); its body is not defined in this build.</summary>
    public const string Clock = "clock";

    /// <summary>Let the camera go during a pause (P), so that its light goes off; no body.</summary>
    public const string Off = "off";

    /// <summary>Take the camera again after a pause; no body.</summary>
    public const string On = "on";

    /// <summary>Stop at an end position (see <see cref="CameraStop"/>).</summary>
    public const string Stop = "stop";

    /// <summary>Give the job up at once, keeping what is written; no body.</summary>
    public const string Cancel = "cancel";

    /// <summary>The camera opened (see <see cref="CameraOpened"/>).</summary>
    public const string Opened = "opened";

    /// <summary>Once a second while recording (see <see cref="CameraStatus"/>).</summary>
    public const string Status = "status";

    /// <summary>Something the run should say (see <see cref="CameraProblem"/>).</summary>
    public const string Problem = "problem";

    /// <summary>A piece is finished (see <see cref="CameraPiece"/>).</summary>
    public const string Piece = "piece";

    /// <summary>How far a video's making has got (see <see cref="CameraProgress"/>).</summary>
    public const string Progress = "progress";

    /// <summary>The lesson's video is made (see <see cref="CameraMade"/>).</summary>
    public const string Made = "made";

    /// <summary>The job failed, or was not taken (see <see cref="CameraFailed"/>).</summary>
    public const string Failed = "failed";

    /// <summary>A line for the starter's log (see <see cref="CameraLog"/>).</summary>
    public const string Log = "log";

    /// <summary>
    /// True when <paramref name="args"/> start ClassScribe as a camera process: exactly <see cref="ProcessSwitch"/> and a
    /// job id that is well formed, as a run id is (<see cref="RunIds.IsWellFormed"/>), in <paramref name="jobId"/>. Anything
    /// else is the command line's, so <c>ClassScribe.exe --camera-process OBSBOT</c> is a mistake with the help, never a
    /// hidden process waiting on its standard input.
    /// </summary>
    public static bool IsCameraProcessStart(string[] args, [NotNullWhen(true)] out string? jobId)
    {
        if (args is [var switchWord, var id] && switchWord == ProcessSwitch && RunIds.IsWellFormed(id))
        {
            jobId = id;
            return true;
        }

        jobId = null;
        return false;
    }
}

/// <summary>What a camera process is asked to do (teach-video 6.2, camera-first 3.4).</summary>
public enum CameraJobKind
{
    /// <summary>Record a run's picture into a piece, placed on the run's clock (slice 7).</summary>
    Record,

    /// <summary>Make a lesson's video from its pieces and its sound (slice 8).</summary>
    Finish,

    /// <summary>The camera check: open the camera, shared, for a few seconds, as a lesson would, and report each call and the frames that came.</summary>
    Check,

    /// <summary>Hold a webcam's picture open, shared, while its microphone is recorded, keeping none of it, so that the camera cannot fall asleep (camera-first 3.4).</summary>
    Hold
}

/// <summary>A camera process's job: the first line it reads. Each kind uses the fields it needs; the rest stay null.</summary>
/// <param name="Protocol">The version of the lines the starter speaks (see <see cref="CameraProtocol.Version"/>). It stays first, and its name never changes.</param>
/// <param name="Kind">What to do.</param>
/// <param name="CameraId">The camera, pinned by Windows' id; null for a finish job.</param>
/// <param name="CameraName">The camera's name, for the lines it writes.</param>
/// <param name="Resolution">The picture's size as <see cref="CameraSize.Text"/> writes it, such as 720p, resolved by the window; null for a hold job.</param>
/// <param name="OffsetMilliseconds">How far the camera's picture is moved against the sound (Recording:CameraOffsets), resolved by the window.</param>
/// <param name="Folder">The recording's folder, for a record or finish job.</param>
/// <param name="BaseName">The recording's base name, such as Tajwid 4_2026-10-19_14-00-02Z, for a record or finish job.</param>
/// <param name="StartSeconds">Where on the run's clock a record job's piece begins, in seconds.</param>
/// <param name="StillPath">Where a check job saves its one picture.</param>
public sealed record CameraJob(int Protocol, CameraJobKind Kind, string? CameraId = null, string? CameraName = null, string? Resolution = null,
    int OffsetMilliseconds = 0, string? Folder = null, string? BaseName = null, double StartSeconds = 0, string? StillPath = null);

/// <summary>Stop a record job at <paramref name="EndSeconds"/> on the run's clock, the last frame lasting to it, and make the lesson's video after it when <paramref name="MakeVideo"/>.</summary>
/// <param name="EndSeconds">The end position on the run's clock, in seconds: where the sound tracks end.</param>
/// <param name="MakeVideo">True to make the lesson's video once the piece is finished.</param>
public sealed record CameraStop(double EndSeconds, bool MakeVideo = false);

/// <summary>The camera opened: what it records.</summary>
/// <param name="Name">The camera's name.</param>
/// <param name="Width">The picture's width.</param>
/// <param name="Height">The picture's height.</param>
/// <param name="FramesPerSecond">The frame rate it keeps.</param>
/// <param name="Encoder">The encoder: hardware or software, by name.</param>
public sealed record CameraOpened(string Name, int Width, int Height, double FramesPerSecond, string Encoder);

/// <summary>A record job's state, once a second.</summary>
/// <param name="Frames">Frames written so far.</param>
/// <param name="Dropped">Frames dropped so far.</param>
/// <param name="SinceLastFrameSeconds">Seconds since the last frame came.</param>
/// <param name="Dark">True while the picture has been black for 10 seconds.</param>
public sealed record CameraStatus(long Frames, long Dropped, double SinceLastFrameSeconds, bool Dark);

/// <summary>What went wrong with the camera, which the run says in words.</summary>
public enum CameraProblemKind
{
    /// <summary>Another app holds the camera.</summary>
    InUse,

    /// <summary>Windows' privacy settings, or a security program, deny the camera.</summary>
    Denied,

    /// <summary>The camera stopped sending its picture, or was unplugged.</summary>
    Lost,

    /// <summary>The picture is black.</summary>
    Dark,

    /// <summary>The encoder failed.</summary>
    Encoder,

    /// <summary>The disk failed or is full.</summary>
    Disk
}

/// <summary>A problem with the camera, with its words.</summary>
/// <param name="Kind">What kind of problem.</param>
/// <param name="Words">What to say, in plain words.</param>
public sealed record CameraProblem(CameraProblemKind Kind, string Words);

/// <summary>A piece is finished.</summary>
/// <param name="Path">The piece's file.</param>
/// <param name="StartSeconds">Where on the run's clock it begins.</param>
/// <param name="EndSeconds">Where it ends.</param>
public sealed record CameraPiece(string Path, double StartSeconds, double EndSeconds);

/// <summary>How far the making of a lesson's video has got.</summary>
/// <param name="Percent">From 0 to 100.</param>
public sealed record CameraProgress(int Percent);

/// <summary>The lesson's video is made.</summary>
/// <param name="Path">The video's file.</param>
/// <param name="LengthSeconds">Its length.</param>
/// <param name="Bytes">Its size.</param>
public sealed record CameraMade(string Path, double LengthSeconds, long Bytes);

/// <summary>The job failed, or was not taken, and why.</summary>
/// <param name="Why">Why, in plain words.</param>
public sealed record CameraFailed(string Why);

/// <summary>A line for the starter's log, under the category Camera.</summary>
/// <param name="Level">The level, by name: Debug, Information, Warning or Error.</param>
/// <param name="Text">The line.</param>
public sealed record CameraLog(string Level, string Text);

/// <summary>
/// JSON for the camera process's lines, generated at compile time: camelCase names, choices by name, read strictly as the
/// link reads its messages (a field a later build adds needs a default), unknown fields passed over.
/// </summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, UseStringEnumConverter = true, RespectNullableAnnotations = true, RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(CameraJob))]
[JsonSerializable(typeof(CameraStop))]
[JsonSerializable(typeof(CameraOpened))]
[JsonSerializable(typeof(CameraStatus))]
[JsonSerializable(typeof(CameraProblem))]
[JsonSerializable(typeof(CameraPiece))]
[JsonSerializable(typeof(CameraProgress))]
[JsonSerializable(typeof(CameraMade))]
[JsonSerializable(typeof(CameraFailed))]
[JsonSerializable(typeof(CameraLog))]
public sealed partial class CameraJson : JsonSerializerContext;
