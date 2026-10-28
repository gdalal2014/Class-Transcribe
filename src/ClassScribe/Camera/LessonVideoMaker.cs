using System.Globalization;
using System.Text.Json;
using ClassScribe.Commands;
using ClassScribe.Runs;
using ClassScribe.Storage;
using Microsoft.Extensions.Logging;

namespace ClassScribe.Camera;

/// <summary>
/// Makes a lesson's video (teach-video 6.9, slice 8) through a camera process of its own with a finish job, for the recorder
/// once a lesson has stopped (beside its transcript), and for the window's finish-video, menu choice 3's V and the report's
/// V: one path for all. It sends the job, writes the process's log lines into its own log under the category Camera, hands
/// on how far it has got, and gives the process <see cref="ProgressLimit"/> without a progress line before it ends it, so
/// that nothing waits on a stuck video for long. The caller's cancel (Ctrl+C after the stop) is sent as cancel, and a
/// process that does not end within <see cref="CancelWait"/> is ended. It says the end: the video with its length and size
/// and a word to watch it before publishing it, or why it was not made and how to make it again, free. It never throws but
/// for an argument; the sound and the transcript never depend on it.
/// </summary>
/// <param name="starter">Starts the camera process.</param>
/// <param name="time">The clock the limits are measured on.</param>
/// <param name="loggers">Makes the log, under the category Camera.</param>
public sealed class LessonVideoMaker(ICameraProcessStarter starter, TimeProvider time, ILoggerFactory loggers)
{
    /// <summary>How long the camera process may go without a progress line, while it makes the video, before it is ended.</summary>
    public static readonly TimeSpan ProgressLimit = TimeSpan.FromMinutes(2);

    /// <summary>How long a cancelled camera process may take to end before it is ended.</summary>
    public static readonly TimeSpan CancelWait = TimeSpan.FromSeconds(15);

    /// <summary>How long a line to the camera process may take to go out.</summary>
    public static readonly TimeSpan SendLimit = TimeSpan.FromSeconds(5);

    /// <summary>How long letting the camera process go may take before the maker goes on without it.</summary>
    public static readonly TimeSpan DisposeWait = TimeSpan.FromSeconds(5);

    /// <summary>The log, under the category Camera, as the camera's other lines are.</summary>
    private readonly ILogger _logger = loggers.CreateLogger("Camera");

    /// <summary>
    /// Makes the video of <paramref name="files"/>, keeping its camera pieces when <paramref name="keepPieces"/> (the video
    /// goes to Google Drive), and returns what became of it; how far it has got, from 0 to 100, goes to
    /// <paramref name="progress"/>. <paramref name="cancellationToken"/> cancels it.
    /// </summary>
    public async Task<LessonVideoResult> MakeAsync(RecordingFiles files, bool keepPieces, CancellationToken cancellationToken, Action<int>? progress = null)
    {
        ICameraProcess process;
        try
        {
            process = starter.Start(RunIds.New(time.GetUtcNow()));
        }
        catch (Exception ex)
        {
            // Deliberately not rethrown: a camera process Windows does not start (a Win32Exception, or the device guard in a test
            // that forgot its fake) costs the video only; it is said, with how to make it later.
            return Said(files, new LessonVideoResult(LessonVideoOutcome.Failed, Why: $"Windows did not start ClassScribe's camera process ({ex.Message.TrimEnd('.')})"));
        }

        _logger.LogDebug("Started a camera process for the lesson's video of {Recording}: process {ProcessId}", files.BaseName, process.ProcessId);
        LessonVideoResult result;
        try
        {
            result = await RunAsync(process, files, keepPieces, cancellationToken, progress);
        }
        finally
        {
            await LetGoAsync(process);
        }

        return Said(files, result);
    }

    /// <summary>The job sent, then the process's lines read until it says made or failed, ends, goes quiet too long, or is cancelled.</summary>
    private async Task<LessonVideoResult> RunAsync(ICameraProcess process, RecordingFiles files, bool keepPieces, CancellationToken cancellationToken, Action<int>? progress)
    {
        var job = new CameraJob(CameraProtocol.Version, CameraJobKind.Finish, Folder: files.Folder, BaseName: files.BaseName, KeepPieces: keepPieces);
        try
        {
            await CameraLines.SendJobAsync(process.Channel, job, CancellationToken.None).AsTask().WaitAsync(SendLimit, time);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or TimeoutException)
        {
            // Deliberately not rethrown: a camera process that will not take its job is one that failed; it is ended, and that
            // is the answer.
            process.Kill();
            return new LessonVideoResult(LessonVideoOutcome.Failed, Why: $"ClassScribe's camera process did not take its job ({ex.Message.TrimEnd('.')})");
        }

        using var quiet = new CancellationTokenSource(ProgressLimit, time);
        using var reading = CancellationTokenSource.CreateLinkedTokenSource(quiet.Token, cancellationToken);
        LessonVideoResult? said = null;
        try
        {
            while (await process.Channel.ReceiveAsync(reading.Token) is { } line)
            {
                switch (CameraLines.Read(line))
                {
                    case CameraProgress step:
                        quiet.CancelAfter(ProgressLimit);
                        progress?.Invoke(Math.Clamp(step.Percent, 0, 100));
                        break;
                    case CameraLog log:
                        Log(log);
                        break;
                    case CameraMade made:
                        said = new LessonVideoResult(LessonVideoOutcome.Made, made.Path, TimeSpan.FromSeconds(made.LengthSeconds), made.Bytes);
                        break;
                    case CameraFailed failed:
                        said = new LessonVideoResult(LessonVideoOutcome.Failed, Why: failed.Why);
                        break;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Deliberately not rethrown: the caller's cancel is the answer, Cancelled, once the camera process has been told.
            return await CancelAsync(process, files);
        }
        catch (OperationCanceledException)
        {
            // Deliberately not rethrown: a camera process silent for ProgressLimit is stuck; it is ended, and that is the answer.
            process.Kill();
            return new LessonVideoResult(LessonVideoOutcome.Failed,
                Why: string.Create(CultureInfo.InvariantCulture, $"ClassScribe's camera process did not say how far it had got for {ProgressLimit.TotalMinutes:0} minutes, so it was ended"));
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException or ObjectDisposedException)
        {
            // Deliberately not rethrown: lines that end or cannot be read end the reading as the process's end does; what it said
            // before stands.
            _logger.LogDebug(ex, "The camera process's lines for the lesson's video of {Recording} ended", files.BaseName);
        }

        return said ?? new LessonVideoResult(LessonVideoOutcome.Failed,
            Why: $"ClassScribe's camera process ended before it said whether the video was made (exit {process.ExitCode?.ToString(CultureInfo.InvariantCulture) ?? "not known"})");
    }

    /// <summary>Tells the camera process to cancel, waits at most <see cref="CancelWait"/> for its end, then ends it; returns Cancelled.</summary>
    private async Task<LessonVideoResult> CancelAsync(ICameraProcess process, RecordingFiles files)
    {
        try
        {
            await CameraLines.SendSignalAsync(process.Channel, CameraProtocol.Cancel, CancellationToken.None).AsTask().WaitAsync(SendLimit, time);
            using var waiting = new CancellationTokenSource(CancelWait, time);
            while (await process.Channel.ReceiveAsync(waiting.Token) is { } line)
            {
                if (CameraLines.Read(line) is CameraLog log) Log(log);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or TimeoutException or OperationCanceledException or JsonException or InvalidDataException)
        {
            // Deliberately not rethrown: a camera process that does not take the cancel, or does not end in time, is ended; the
            // cancel stands either way.
            _logger.LogDebug(ex, "The camera process making the video of {Recording} did not end at the cancel, so it was ended", files.BaseName);
            process.Kill();
        }

        return new LessonVideoResult(LessonVideoOutcome.Cancelled);
    }

    /// <summary>Lets <paramref name="process"/> go, waiting at most <see cref="DisposeWait"/>; one that cannot be let go ends with ClassScribe at the latest.</summary>
    private async Task LetGoAsync(ICameraProcess process)
    {
        try
        {
            await process.DisposeAsync().AsTask().WaitAsync(DisposeWait, time);
        }
        catch (TimeoutException)
        {
            // Deliberately not rethrown: the process's job ends it with ClassScribe at the latest, and the video's end is known.
            _logger.LogDebug("The camera process could not be let go within {Seconds} seconds; it ends with ClassScribe at the latest", DisposeWait.TotalSeconds);
        }
    }

    /// <summary>A line of the camera process's, at its level: Information and above in its own words, Debug with where it comes from.</summary>
    private void Log(CameraLog log)
    {
        switch (log.Level)
        {
            case "Error":
                _logger.LogError("{Text}", log.Text);
                break;
            case "Warning":
                _logger.LogWarning("{Text}", log.Text);
                break;
            case "Information":
                _logger.LogInformation("{Text}", log.Text);
                break;
            default:
                _logger.LogDebug("Camera process: {Text}", log.Text);
                break;
        }
    }

    /// <summary>Says the end of <paramref name="result"/> for <paramref name="files"/>, and returns it.</summary>
    private LessonVideoResult Said(RecordingFiles files, LessonVideoResult result)
    {
        switch (result.Outcome)
        {
            case LessonVideoOutcome.Made:
                _logger.LogInformation("{Line}", MadeLine(result));
                break;
            case LessonVideoOutcome.Cancelled:
                _logger.LogWarning("The lesson's video was cancelled; the camera's pieces are kept. To make it later, free: {Command}", CommandOf(files));
                break;
            default:
                var why = result.Why ?? "it was not made";
                _logger.LogWarning("The lesson's video was not made: {Why}", why.Contains("finish-video", StringComparison.Ordinal)
                    ? Sentence.Of(why)
                    : $"{Sentence.Of(why)} To make it again, free: {CommandOf(files)}");
                break;
        }

        return result;
    }

    /// <summary>The end line of a video made (teach-video 6.13): its file, its length and size, with its sound, and a word to watch it first.</summary>
    public static string MadeLine(LessonVideoResult made) =>
        $"The lesson's video: {System.IO.Path.GetFileName(made.Path)} ({Durations.Format(made.Length)}, {Sizes.Describe(made.Bytes)}), with its sound. Watch it before you publish it.";

    /// <summary>The command that makes the video of <paramref name="files"/> again, by one of its files.</summary>
    private static string CommandOf(RecordingFiles files) =>
        CommandLine.FinishVideo(files.FindSegments().FirstOrDefault() ?? files.FindSegments(Audio.Track.Microphone).FirstOrDefault() ?? files.Folder);
}
