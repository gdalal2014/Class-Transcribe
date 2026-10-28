using System.Text.Json;
using ClassScribe.Link;
using ClassScribe.Runs;

namespace ClassScribe.Camera;

/// <summary>
/// The camera process (teach-video 6.2, amendment 24): ClassScribe started as <c>ClassScribe.exe --camera-process &lt;job
/// id&gt;</c>, which <c>Program</c> hands here before anything touches the console. It reads its job, the first line on its
/// standard input, and speaks the lines of <see cref="CameraProtocol"/> on its standard output. It takes three jobs: the
/// hold (<see cref="CameraHoldJob"/>), which keeps a webcam's picture open, shared, while its microphone is recorded; the
/// check (<see cref="CameraCheckJob"/>), which opens a camera for a few seconds and reports each call; and the record job
/// (<see cref="CameraRecordJob"/>), which writes a lesson's picture into a piece on the run's clock. Each opens the camera
/// only shared, on an STA thread of the process's own, through <see cref="CameraRoute"/>. The fourth, the finish job
/// (<see cref="CameraFinishJob"/>, slice 8), joins a lesson's pieces and its sound into its finished video, and opens no
/// camera. A job that does not come within <see cref="JobPatience"/>, a first line that is no job, a job of another version,
/// a hold, check or record job that names no camera, or a finish job that names no recording, is not taken, and ends with
/// <see cref="CameraProtocol.JobNotTakenExitCode"/>. It never throws: an output that is gone, as when its starter has ended,
/// ends it with the same code.
/// </summary>
public sealed class CameraHost
{
    /// <summary>How long it waits for its job before it gives up: its starter writes the job as soon as it has started it.</summary>
    public TimeSpan JobPatience { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>The clock the wait for the job, and the jobs, are measured on: the computer's, unless a test gives another.</summary>
    public TimeProvider Time { get; init; } = TimeProvider.System;

    /// <summary>Makes the cameras' captures: Windows' own MediaCapture unless a test gives a fake; null on a Windows too old for the camera API.</summary>
    public ICameraCaptureFactory? Captures { get; init; } =
        OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041) ? new Windows.WindowsCameraCaptureFactory() : null;

    /// <summary>Makes a record job's piece: Media Foundation's sink writer unless a test gives a fake; null where Windows has none.</summary>
    public IPieceWriterFactory? Writers { get; init; } = OperatingSystem.IsWindows() ? new Windows.MediaFoundationPieceWriterFactory() : null;

    /// <summary>Does a finish job's media work: Media Foundation unless a test gives a fake; null where Windows has none.</summary>
    public ILessonVideoMedia? Media { get; init; } = OperatingSystem.IsWindows() ? new Windows.MediaFoundationLessonVideo() : null;

    /// <summary>How long a call on the camera's route may take before the job gives up: the probe's 30 seconds.</summary>
    public TimeSpan CallLimit { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How long the camera check counts the camera's frames for.</summary>
    public TimeSpan CheckFor { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Runs the camera process for job <paramref name="jobId"/>, reading its lines from <paramref name="input"/> and writing
    /// its own to <paramref name="output"/>, and returns its exit code (see the class). A job id that is not well formed is
    /// not taken, without a line read.
    /// </summary>
    public async Task<int> RunAsync(string jobId, Stream input, Stream output)
    {
        if (!RunIds.IsWellFormed(jobId)) return CameraProtocol.JobNotTakenExitCode;

        await using var channel = new LineChannel(new JoinedStream(input, output));
        try
        {
            using var patience = new CancellationTokenSource(JobPatience, Time);
            if (await ReadJobAsync(channel, patience.Token) is not { } read) return CameraProtocol.JobNotTakenExitCode;
            if (read.Job is not { } job)
            {
                await SendFailedAsync(channel, read.Why!);
                return CameraProtocol.JobNotTakenExitCode;
            }

            if (job.Kind is CameraJobKind.Hold or CameraJobKind.Check or CameraJobKind.Record) return await RunCameraJobAsync(channel, jobId, job);
            if (job.Kind == CameraJobKind.Finish && Media is { } media) return await new CameraFinishJob(channel, job, media, Time).RunAsync();

            await channel.SendAsync(CameraProtocol.Log, new CameraLog("Information",
                $"Camera process for job {jobId}: a {job.Kind} job{(job.CameraName is { } name ? $" for the camera \"{name}\"" : "")}; no camera was opened"), CameraJson.Default.CameraLog, CancellationToken.None);
            await SendFailedAsync(channel, $"This Windows has no way to make the lesson's video (Media Foundation), so this camera process takes no {job.Kind} job, and opened no camera");
            return CameraProtocol.JobNotTakenExitCode;
        }
        catch (IOException)
        {
            // Deliberately not rethrown: the starter's ends of the pipes are gone, as when it has ended, so nobody is left to
            // tell, and the exit code says the job was not taken.
            return CameraProtocol.JobNotTakenExitCode;
        }
    }

    /// <summary>
    /// A hold or a check job, on an STA thread of the process's own: not taken when it names no camera, or when this
    /// Windows has no camera API.
    /// </summary>
    private async Task<int> RunCameraJobAsync(LineChannel channel, string jobId, CameraJob job)
    {
        if (string.IsNullOrWhiteSpace(job.CameraId))
        {
            await SendFailedAsync(channel, $"The {job.Kind} job names no camera");
            return CameraProtocol.JobNotTakenExitCode;
        }

        if (Captures is not { } captures)
        {
            await SendFailedAsync(channel, "The camera needs Windows 10 version 2004 (build 19041) or later");
            return CameraProtocol.JobNotTakenExitCode;
        }

        if (job.Kind == CameraJobKind.Record && Writers is null)
        {
            await SendFailedAsync(channel, "This Windows has no way to write the camera's picture (Media Foundation's sink writer)");
            return CameraProtocol.JobNotTakenExitCode;
        }

        using var thread = new CameraThread($"ClassScribe camera {jobId}", Time);
        return job.Kind switch
        {
            CameraJobKind.Hold => await new CameraHoldJob(channel, job, captures, thread, Time, CallLimit).RunAsync(),
            CameraJobKind.Record => await new CameraRecordJob(channel, job, captures, Writers!, thread, Time, CallLimit).RunAsync(),
            _ => await new CameraCheckJob(channel, job, captures, thread, Time, CallLimit, CheckFor).RunAsync()
        };
    }

    /// <summary>
    /// The job, the first line: null when the input ended before any line came; a reason when no job came within the
    /// patience, or the first line is not a job this build can read (another type, no body, a body that is not an object,
    /// one it cannot read, or another version). It never throws for what the line holds.
    /// </summary>
    private static async Task<(CameraJob? Job, string? Why)?> ReadJobAsync(LineChannel channel, CancellationToken patience)
    {
        Envelope? first;
        try
        {
            first = await channel.ReceiveAsync(patience);
        }
        catch (OperationCanceledException)
        {
            // Deliberately not rethrown: no job came in time, which is the answer; the starter has given up or is gone.
            return (null, "No job came within the time a camera process waits for one");
        }
        catch (InvalidDataException ex)
        {
            // Deliberately not rethrown: a line that is not a message of the camera's lines is answered as a job not taken.
            return (null, ex.Message);
        }

        if (first is null) return null;
        if (first.Type != CameraProtocol.Job) return (null, $"The first line was \"{first.Type}\", not a job");

        // A line without a body holds an undefined element, which Deserialize refuses with an InvalidOperationException
        // rather than a JsonException, so the body's kind is looked at first, as the link's readers do.
        if (first.Body.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) return (null, "The job had no body");
        if (first.Body.ValueKind != JsonValueKind.Object) return (null, "The job's body was not an object");

        CameraJob? job;
        try
        {
            job = first.Body.Deserialize(CameraJson.Default.CameraJob);
        }
        catch (JsonException ex)
        {
            // Deliberately not rethrown: a job this build cannot read is answered as a job not taken, with why.
            return (null, $"The job could not be read: {ex.Message}");
        }

        if (job is null) return (null, "The job had no body");
        return job.Protocol == CameraProtocol.Version
            ? (job, null)
            : (null, $"The job speaks version {job.Protocol} of the camera's lines; this camera process speaks version {CameraProtocol.Version}");
    }

    /// <summary>Sends <see cref="CameraProtocol.Failed"/> with <paramref name="why"/>.</summary>
    private static ValueTask SendFailedAsync(LineChannel channel, string why) =>
        channel.SendAsync(CameraProtocol.Failed, new CameraFailed(why), CameraJson.Default.CameraFailed, CancellationToken.None);
}
