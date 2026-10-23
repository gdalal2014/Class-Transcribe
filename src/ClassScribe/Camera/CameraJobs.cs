using System.Collections.Concurrent;
using System.Globalization;
using ClassScribe.Link;

namespace ClassScribe.Camera;

/// <summary>What a camera process says when a camera cannot be opened: the kind, and the cause in words.</summary>
public static class CameraProblems
{
    /// <summary>
    /// The problem for an open that failed as <paramref name="failure"/> says: Windows' camera privacy settings or a security
    /// program for E_ACCESSDENIED, another app holding the camera for a sharing violation or a busy camera, a camera not
    /// connected, or the call and its code for anything else. The words have no full stop.
    /// </summary>
    public static CameraProblem ForFailedOpen(CameraCallFailedException failure)
    {
        var detail = $"{failure.Call} failed: {failure.Error}";
        return failure.Code switch
        {
            CameraHresults.AccessDenied => new(CameraProblemKind.Denied,
                $"Windows' camera privacy settings or a security program (such as Bitdefender's Webcam Protection) refused it ({detail})"),
            CameraHresults.SharingViolation or CameraHresults.Busy => new(CameraProblemKind.InUse, $"another app may be holding it alone ({detail})"),
            CameraHresults.DeviceNotConnected => new(CameraProblemKind.Lost, $"it is not connected ({detail})"),
            null when failure.Call == CameraRoute.Source => new(CameraProblemKind.Lost, $"Windows does not list it now; is it unplugged? ({failure.Error})"),
            _ => new(CameraProblemKind.Lost, $"Windows did not open it ({detail})")
        };
    }

    /// <summary>The words of a problem when the picture stopped coming during a hold.</summary>
    public const string Stopped = "it stopped sending its picture";
}

/// <summary>
/// The hold job (camera-first 3.4): the webcam's picture kept open, shared, from before its microphone opens until the
/// recording ends, so that a webcam that sleeps by itself (the OBSBOT's Device Auto Sleep) cannot fall asleep and silence
/// its microphone. The camera is opened through <see cref="CameraRoute"/> at the lowest cost it allows: no format, no
/// subtype and no size asked for, Realtime, every frame released unread, nothing written. A status line goes to the
/// starter at the first frame of each open and once a second after, with the frames so far and the time since the last
/// one. A camera that cannot be opened is said once per cause and tried again every <see cref="RetryEvery"/>; one that
/// stops sending its picture for <see cref="StoppedAfter"/> (or sends none for <see cref="FirstFrameLimit"/> after an open)
/// is let go and opened again every <see cref="RetryEvery"/>. The job ends at the starter's stop or cancel, or when the
/// starter's end of the lines closes, letting the camera go first.
/// </summary>
/// <param name="channel">The lines to and from the starter.</param>
/// <param name="job">The job: the camera's id and name.</param>
/// <param name="captures">Makes the camera's capture.</param>
/// <param name="thread">The camera process's STA thread, on which every call on the route is made.</param>
/// <param name="time">The clock the job is timed on.</param>
/// <param name="callLimit">How long the route's calls may take before the job gives up.</param>
public sealed class CameraHoldJob(LineChannel channel, CameraJob job, ICameraCaptureFactory captures, CameraThread thread, TimeProvider time, TimeSpan callLimit)
{
    /// <summary>How often a camera that cannot be opened, or that stopped sending its picture, is opened again.</summary>
    public static readonly TimeSpan RetryEvery = TimeSpan.FromSeconds(5);

    /// <summary>How long without a frame, after frames came, before the picture counts as stopped and the camera is opened again.</summary>
    public static readonly TimeSpan StoppedAfter = TimeSpan.FromSeconds(3);

    /// <summary>How long an open may go without its first frame before the camera is opened again: a camera waking from sleep is given time.</summary>
    public static readonly TimeSpan FirstFrameLimit = TimeSpan.FromSeconds(10);

    /// <summary>How often the job looks at its frames.</summary>
    public static readonly TimeSpan LookEvery = TimeSpan.FromMilliseconds(250);

    /// <summary>How often a status line is sent while nothing new happens.</summary>
    public static readonly TimeSpan StatusEvery = TimeSpan.FromSeconds(1);

    /// <summary>What MediaCapture reported, from Windows' threads, for the next look to send as log lines.</summary>
    private readonly ConcurrentQueue<string> _events = new();

    /// <summary>The frames handed over in this job, over every open.</summary>
    private long _frames;

    /// <summary>When the last frame came, on the steady clock; 0 before the first.</summary>
    private long _lastFrame;

    /// <summary>Ends the job: the starter's stop, its end closing, or a send that found it gone.</summary>
    private CancellationTokenSource? _stop;

    /// <summary>True once a send found the starter gone: nothing more is sent.</summary>
    private volatile bool _starterGone;

    /// <summary>Runs the job and returns the camera process's exit code: 0 when stopped, 1 when the camera's thread is stuck.</summary>
    public async Task<int> RunAsync()
    {
        var name = job.CameraName ?? "the camera";
        using var stop = new CancellationTokenSource();
        _stop = stop;
        var control = ReadControlAsync(stop);
        await LogAsync("Information", $"Holding the camera \"{name}\" open, shared, its frames released unread");

        var began = time.GetTimestamp();
        CameraOpening? open = null;
        var openedAt = 0L;
        var framesAtOpen = 0L;
        var nextTry = began;
        var lastStatus = 0L;
        var statusedFirstFrame = true;
        string? lastProblem = null;
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var now = time.GetTimestamp();
                if (open is null && now >= nextTry)
                {
                    var log = new CameraCallLog(time);
                    try
                    {
                        open = await thread.RunAsync(() => CameraRoute.Open(captures, job.CameraId!, name, convertTo: null, log, _events.Enqueue, OnFrame), callLimit);
                        openedAt = time.GetTimestamp();
                        framesAtOpen = Interlocked.Read(ref _frames);
                        statusedFirstFrame = false;
                        lastProblem = null;
                        await SendLinesAsync(log, "Debug");
                        await SendAsync(CameraProtocol.Opened, new CameraOpened(name, 0, 0, 0, $"none: {open.Format}, frames released unread"), CameraJson.Default.CameraOpened);
                    }
                    catch (CameraCallFailedException failure)
                    {
                        // Deliberately not rethrown: a camera that cannot be opened now (unplugged, refused, held by another
                        // app) may be opened later; the cause goes to the starter once, the calls at Debug, and the open is
                        // tried again every RetryEvery.
                        await SendLinesAsync(log, "Debug");
                        var problem = CameraProblems.ForFailedOpen(failure);
                        if (problem.Words != lastProblem) await SendAsync(CameraProtocol.Problem, problem, CameraJson.Default.CameraProblem);
                        lastProblem = problem.Words;
                        nextTry = time.GetTimestamp() + Ticks(RetryEvery);
                    }
                }

                while (_events.TryDequeue(out var reported)) await LogAsync("Information", reported);

                now = time.GetTimestamp();
                var frames = Interlocked.Read(ref _frames);
                if (open is not null)
                {
                    var thisOpen = frames - framesAtOpen;
                    var since = time.GetElapsedTime(Math.Max(Interlocked.Read(ref _lastFrame), openedAt), now);
                    if (since >= (thisOpen > 0 ? StoppedAfter : FirstFrameLimit))
                    {
                        await CloseAsync(open);
                        open = null;
                        lastProblem = CameraProblems.Stopped;
                        await SendAsync(CameraProtocol.Problem, new CameraProblem(CameraProblemKind.Lost, CameraProblems.Stopped), CameraJson.Default.CameraProblem);
                        nextTry = now + Ticks(RetryEvery);
                    }
                }

                var firstFrame = open is not null && !statusedFirstFrame && frames > framesAtOpen;
                if (firstFrame || lastStatus == 0 || time.GetElapsedTime(lastStatus, now) >= StatusEvery)
                {
                    statusedFirstFrame |= firstFrame;
                    lastStatus = now;
                    var last = Interlocked.Read(ref _lastFrame);
                    var sinceFrame = time.GetElapsedTime(last == 0 ? began : last, now);
                    await SendAsync(CameraProtocol.Status, new CameraStatus(frames, 0, Math.Round(sinceFrame.TotalSeconds, 2), false), CameraJson.Default.CameraStatus);
                }

                await DelayAsync(LookEvery, stop.Token);
            }
        }
        catch (TimeoutException)
        {
            // Deliberately not rethrown: the camera's thread is stuck inside Windows, so the job cannot go on; it says so to
            // its starter, which starts another camera process, and ends with 1.
            await SendAsync(CameraProtocol.Failed, new CameraFailed($"A call on the camera's route did not return within {callLimit.TotalSeconds:0} seconds"),
                CameraJson.Default.CameraFailed);
            return CameraProtocol.FailedExitCode;
        }
        finally
        {
            try
            {
                if (open is not null && !thread.Abandoned) await CloseAsync(open);
            }
            catch (TimeoutException)
            {
                // Deliberately not rethrown: letting go hung inside Windows; the process ends next, and Windows lets the
                // camera go with it.
                await LogAsync("Warning", $"Letting the camera go did not return within {callLimit.TotalSeconds:0} seconds");
            }

            await stop.CancelAsync();
            await control;
        }

        await LogAsync("Information", $"Let go of the camera \"{name}\"");
        return CameraProtocol.DoneExitCode;
    }

    /// <summary>A frame came: counted, and its time kept. Runs on Windows' frame thread.</summary>
    private void OnFrame()
    {
        Interlocked.Increment(ref _frames);
        Interlocked.Exchange(ref _lastFrame, time.GetTimestamp());
    }

    /// <summary>Lets the camera go on its thread, the calls sent at Debug; a thread that is stuck is not waited for again.</summary>
    private async Task CloseAsync(CameraOpening open)
    {
        var log = new CameraCallLog(time);
        try
        {
            await thread.RunAsync(() => CameraRoute.Close(open.Capture, log), callLimit);
        }
        finally
        {
            await SendLinesAsync(log, "Debug");
        }
    }

    /// <summary>
    /// Reads the starter's lines until a stop or a cancel comes, or its end closes, then asks the job to end. A line this
    /// build does not know is passed over.
    /// </summary>
    private async Task ReadControlAsync(CancellationTokenSource stop)
    {
        try
        {
            while (await channel.ReceiveAsync(stop.Token) is { } line)
            {
                if (line.Type is CameraProtocol.Stop or CameraProtocol.Cancel) break;
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or InvalidDataException or ObjectDisposedException)
        {
            // Deliberately not rethrown: the job is ending, the starter is gone, or a line could not be read; each ends the
            // hold, which lets the camera go.
        }

        await stop.CancelAsync();
    }

    /// <summary>Waits <paramref name="delay"/> on the job's clock, or until the job is asked to end.</summary>
    private async Task DelayAsync(TimeSpan delay, CancellationToken stop)
    {
        try
        {
            await Task.Delay(delay, time, stop);
        }
        catch (OperationCanceledException)
        {
            // Deliberately not rethrown: the end of the wait is the end of the job, which the loop sees.
        }
    }

    /// <summary>Sends each call of <paramref name="log"/> as a log line at <paramref name="level"/>.</summary>
    private async Task SendLinesAsync(CameraCallLog log, string level)
    {
        foreach (var call in log.Calls) await LogAsync(level, $"Camera call {call.Text}");
    }

    /// <summary>Sends a log line (see <see cref="SendAsync{T}"/>).</summary>
    private Task LogAsync(string level, string text) => SendAsync(CameraProtocol.Log, new CameraLog(level, text), CameraJson.Default.CameraLog);

    /// <summary>
    /// Sends a line to the starter; a starter that is gone (its end of the pipe closed) ends the job instead, so that the
    /// camera is let go and nothing more is sent.
    /// </summary>
    private async Task SendAsync<T>(string type, T body, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info)
    {
        if (_starterGone) return;
        try
        {
            await channel.SendAsync(type, body, info, CancellationToken.None);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // Deliberately not rethrown: the starter is gone, so nobody is left to tell; the job ends and lets the camera go.
            _starterGone = true;
            if (_stop is { } stop) await stop.CancelAsync();
        }
    }

    /// <summary><paramref name="span"/> in the clock's ticks.</summary>
    private long Ticks(TimeSpan span) => (long)(span.TotalSeconds * time.TimestampFrequency);
}

/// <summary>
/// The check job (camera-first step 11, the camera check): the camera opened shared, its reader converting to the picture's
/// size as a lesson will, for <see cref="CameraHost.CheckFor"/>, then let go. Every call on the route is sent as a log
/// line at Information, with its outcome and HRESULT, so the check says exactly which call failed; then a status line
/// with the frames that came, and the job ends with 0 when frames came, else with failed and why, exit 1. Nothing is
/// written.
/// </summary>
/// <param name="channel">The lines to and from the starter.</param>
/// <param name="job">The job: the camera's id and name, and the picture's size.</param>
/// <param name="captures">Makes the camera's capture.</param>
/// <param name="thread">The camera process's STA thread.</param>
/// <param name="time">The clock the job is timed on.</param>
/// <param name="callLimit">How long the route's calls may take.</param>
/// <param name="streamFor">How long the frames are counted for.</param>
public sealed class CameraCheckJob(LineChannel channel, CameraJob job, ICameraCaptureFactory captures, CameraThread thread, TimeProvider time, TimeSpan callLimit,
    TimeSpan streamFor)
{
    private long _frames;
    private long _lastFrame;

    /// <summary>How many of the calls have been sent to the starter, so that the closing calls go on from the opening ones.</summary>
    private int _sent;

    /// <summary>Runs the job and returns the camera process's exit code.</summary>
    public async Task<int> RunAsync()
    {
        var name = job.CameraName ?? "the camera";
        var size = CameraSize.TryParse(job.Resolution, out var asked) ? asked : CameraSize.Default;
        var events = new ConcurrentQueue<string>();
        var log = new CameraCallLog(time);
        CameraOpening open;
        try
        {
            open = await thread.RunAsync(() => CameraRoute.Open(captures, job.CameraId!, name, size, log, events.Enqueue, OnFrame), callLimit);
        }
        catch (CameraCallFailedException failure)
        {
            // Deliberately not rethrown: the check's answer is the failure itself, call by call, which goes to the starter.
            await SendCallsAsync(log, events);
            return await FailAsync($"The camera \"{name}\" could not be opened: {CameraProblems.ForFailedOpen(failure).Words}");
        }
        catch (TimeoutException)
        {
            // Deliberately not rethrown: the camera's thread is stuck inside Windows; the check's answer is the call that
            // never returned, which goes to the starter.
            await SendCallsAsync(log, events);
            return await FailAsync($"{log.InProgress ?? "A call on the camera's route"} did not return within {callLimit.TotalSeconds:0} seconds");
        }

        await SendCallsAsync(log, events);
        await channel.SendAsync(CameraProtocol.Opened, new CameraOpened(name, size.Width, size.Height, 0, $"none: {open.Format}, read as {open.Reader}"),
            CameraJson.Default.CameraOpened, CancellationToken.None);

        var began = time.GetTimestamp();
        await Task.Delay(streamFor, time);
        var frames = Interlocked.Read(ref _frames);
        var last = Interlocked.Read(ref _lastFrame);
        var sinceFrame = time.GetElapsedTime(last == 0 ? began : last);
        await channel.SendAsync(CameraProtocol.Status, new CameraStatus(frames, 0, Math.Round(sinceFrame.TotalSeconds, 2), false), CameraJson.Default.CameraStatus,
            CancellationToken.None);

        try
        {
            await thread.RunAsync(() => CameraRoute.Close(open.Capture, log), callLimit);
        }
        catch (TimeoutException)
        {
            // Deliberately not rethrown: letting the camera go hung inside Windows; the check says which call, and the
            // process ends next, which lets the camera go.
            await SendCallsAsync(log, events);
            return await FailAsync($"{log.InProgress ?? "Letting the camera go"} did not return within {callLimit.TotalSeconds:0} seconds");
        }

        await SendCallsAsync(log, events);
        return frames > 0
            ? CameraProtocol.DoneExitCode
            : await FailAsync(string.Create(CultureInfo.InvariantCulture,
                $"The camera \"{name}\" opened but sent no picture in {streamFor.TotalSeconds:0} seconds; is its lens folded, or is it asleep?"));
    }

    /// <summary>A frame came. Runs on Windows' frame thread.</summary>
    private void OnFrame()
    {
        Interlocked.Increment(ref _frames);
        Interlocked.Exchange(ref _lastFrame, time.GetTimestamp());
    }

    /// <summary>
    /// Sends each call of <paramref name="log"/> not sent yet, then what MediaCapture reported, as log lines: at Information,
    /// a required call that failed at Warning.
    /// </summary>
    private async Task SendCallsAsync(CameraCallLog log, ConcurrentQueue<string> events)
    {
        var calls = log.Calls;
        foreach (var call in calls.Skip(_sent))
            await channel.SendAsync(CameraProtocol.Log, new CameraLog(call.Failed && call.Required ? "Warning" : "Information", call.Text), CameraJson.Default.CameraLog, CancellationToken.None);
        _sent = calls.Count;
        while (events.TryDequeue(out var reported))
            await channel.SendAsync(CameraProtocol.Log, new CameraLog("Information", reported), CameraJson.Default.CameraLog, CancellationToken.None);
    }

    /// <summary>Sends failed with <paramref name="why"/>, and returns the failed exit code.</summary>
    private async Task<int> FailAsync(string why)
    {
        await channel.SendAsync(CameraProtocol.Failed, new CameraFailed(why), CameraJson.Default.CameraFailed, CancellationToken.None);
        return CameraProtocol.FailedExitCode;
    }
}
