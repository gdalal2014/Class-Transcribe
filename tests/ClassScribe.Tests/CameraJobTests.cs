using ClassScribe.Camera;

namespace ClassScribe.Tests;

/// <summary>
/// The camera process's two jobs, run by the real <see cref="CameraHost"/> over an in-memory pipe, its captures made by
/// <see cref="FakeCameraCaptureFactory"/> and its time on the manual clock: the hold keeps a webcam's picture open, shared,
/// with nothing converted and every frame released unread, tells its starter of the first frame and once a second, says a
/// refusal once and tries again every 5 seconds, opens the camera again when its picture stops, and lets it go at the stop;
/// the check opens the camera at the picture's size and reports every call. Nothing is opened.
/// </summary>
[TestFixture]
public class CameraJobTests
{
    /// <summary>A well-formed job id.</summary>
    private static readonly string JobId = TestRuns.Id(7);

    /// <summary>The steps the manual clock moves by: the hold's own look.</summary>
    private static readonly TimeSpan Step = CameraHoldJob.LookEvery;

    /// <summary>The clock the jobs run on, new for each test.</summary>
    private ManualTimeProvider _time = null!;

    /// <summary>Makes the captures, new for each test.</summary>
    private FakeCameraCaptureFactory _captures = null!;

    /// <summary>A new clock and a new factory for each test: NUnit keeps one instance of the fixture for all its tests.</summary>
    [SetUp]
    public void NewClockAndCaptures()
    {
        _time = new ManualTimeProvider(TestRuns.Start);
        _captures = new FakeCameraCaptureFactory();
    }

    /// <summary>
    /// A hold opens the camera shared, its reader converting nothing; it says so, tells its starter of the first frame at
    /// once and then once a second, and at the stop lets the camera go and ends with 0.
    /// </summary>
    [Test]
    public async Task A_hold_opens_the_camera_shared_tells_of_its_frames_and_lets_it_go_at_the_stop()
    {
        await using var process = new FakeCameraProcess();
        var (host, seen) = await StartAsync(process, Hold());

        await _time.AdvanceUntilAsync(() => seen.Of<CameraOpened>().Count == 1, Step);
        var capture = _captures.Last!;
        capture.Frames();
        await _time.AdvanceUntilAsync(() => seen.Of<CameraStatus>().Any(status => status.Frames == 1), Step, atMost: Step * 2);
        var statuses = seen.Of<CameraStatus>().Count;
        await _time.AdvanceUntilAsync(() =>
        {
            capture.Frames();
            return seen.Of<CameraStatus>().Count >= statuses + 3;
        }, Step, atMost: TimeSpan.FromSeconds(4));

        await process.Channel.SendAsync(CameraProtocol.Stop, new CameraStop(0), CameraJson.Default.CameraStop, CancellationToken.None);
        await _time.AdvanceUntilAsync(() => host.IsCompleted, Step);

        Assert.That(await host, Is.EqualTo(CameraProtocol.DoneExitCode));
        Assert.That(capture.ConvertedTo, Is.Null, "no subtype and no size: nothing converted");
        Assert.That(capture.Disposed, Is.True);
        Assert.That(capture.TooEarly, Is.Empty);
        Assert.That(_captures.Made.Single().CameraId, Is.EqualTo(LaptopCameras.Obsbot.Id));
        Assert.That(seen.Logs[0], Is.EqualTo("Holding the camera \"OBSBOT Meet Flip StreamCamera\" open, shared, its frames released unread"));
        Assert.That(seen.Logs, Does.Contain("Camera call 4. MediaCapture.InitializeAsync (SharedReadOnly, video only): OK"));
        Assert.That(seen.Logs[^1], Is.EqualTo("Let go of the camera \"OBSBOT Meet Flip StreamCamera\""));
        Assert.That(seen.Of<CameraOpened>().Single().Encoder, Does.Contain("frames released unread"));
        Assert.That(seen.Of<CameraProblem>(), Is.Empty);
    }

    /// <summary>
    /// A camera refused (Windows' privacy settings or a security program) is said once, with the call and its code, and
    /// tried again every 5 seconds, until it opens.
    /// </summary>
    [Test]
    public async Task A_refused_camera_is_said_once_and_tried_again_every_5_seconds()
    {
        _captures.Prepare = (capture, index) =>
        {
            if (index < 2) capture.Failures[nameof(ICameraCapture.InitializeShared)] = new UnauthorizedAccessException("Access is denied.") { HResult = CameraHresults.AccessDenied };
        };
        await using var process = new FakeCameraProcess();
        var (host, seen) = await StartAsync(process, Hold());

        var moved = await _time.AdvanceUntilAsync(() => seen.Of<CameraOpened>().Count == 1, Step, atMost: TimeSpan.FromSeconds(12));
        await process.Channel.DisposeAsync();
        await _time.AdvanceUntilAsync(() => host.IsCompleted, Step);

        var problem = seen.Of<CameraProblem>().Single();
        Assert.That(problem.Kind, Is.EqualTo(CameraProblemKind.Denied));
        Assert.That(problem.Words, Is.EqualTo("Windows' camera privacy settings or a security program (such as Bitdefender's Webcam Protection) refused it (MediaCapture.InitializeAsync (SharedReadOnly, video only) failed: 0x80070005 E_ACCESSDENIED (Windows: \"Access is denied.\"))"));
        Assert.That(_captures.Made, Has.Count.EqualTo(3));
        Assert.That(moved, Is.GreaterThanOrEqualTo(CameraHoldJob.RetryEvery * 2));
        Assert.That(_captures.Made.Take(2).Select(made => made.Capture.Disposed), Has.All.True, "each refused capture let go");
        Assert.That(await host, Is.EqualTo(CameraProtocol.DoneExitCode), "the starter's end closing ends the hold");
        Assert.That(_captures.Last!.Disposed, Is.True);
    }

    /// <summary>
    /// A picture that stops for 3 seconds (a folded lens, the camera unplugged) is said, the camera let go, and opened again
    /// 5 seconds later; the frames count on across the opens.
    /// </summary>
    [Test]
    public async Task A_picture_that_stops_is_said_and_the_camera_opened_again()
    {
        await using var process = new FakeCameraProcess();
        var (host, seen) = await StartAsync(process, Hold());
        await _time.AdvanceUntilAsync(() => seen.Of<CameraOpened>().Count == 1, Step);
        var first = _captures.Last!;
        first.Frames(10);

        var stopped = await _time.AdvanceUntilAsync(() => seen.Of<CameraProblem>().Count == 1, Step, atMost: TimeSpan.FromSeconds(4));
        var again = await _time.AdvanceUntilAsync(() => seen.Of<CameraOpened>().Count == 2, Step, atMost: TimeSpan.FromSeconds(6));
        _captures.Last!.Frames(5);
        await _time.AdvanceUntilAsync(() => seen.Of<CameraStatus>().Any(status => status.Frames == 15), Step, atMost: TimeSpan.FromSeconds(2));
        await process.Channel.DisposeAsync();
        await _time.AdvanceUntilAsync(() => host.IsCompleted, Step);

        Assert.That(seen.Of<CameraProblem>().Single(), Is.EqualTo(new CameraProblem(CameraProblemKind.Lost, CameraProblems.Stopped)));
        Assert.That(stopped, Is.GreaterThanOrEqualTo(CameraHoldJob.StoppedAfter));
        Assert.That(again, Is.GreaterThanOrEqualTo(CameraHoldJob.RetryEvery));
        Assert.That(first.Disposed, Is.True);
        Assert.That(_captures.Made, Has.Count.EqualTo(2));
    }

    /// <summary>An open that brings no frame at all is given time to wake the camera, then opened again.</summary>
    [Test]
    public async Task An_open_without_a_first_frame_is_given_time_then_opened_again()
    {
        await using var process = new FakeCameraProcess();
        var (host, seen) = await StartAsync(process, Hold());
        await _time.AdvanceUntilAsync(() => seen.Of<CameraOpened>().Count == 1, Step);

        var waited = await _time.AdvanceUntilAsync(() => seen.Of<CameraProblem>().Count == 1, Step, atMost: TimeSpan.FromSeconds(11));
        await process.Channel.DisposeAsync();
        await _time.AdvanceUntilAsync(() => host.IsCompleted, Step);

        Assert.That(waited, Is.GreaterThanOrEqualTo(CameraHoldJob.FirstFrameLimit));
    }

    /// <summary>A hold or a check that names no camera is not taken: nothing is made, exit 4.</summary>
    [TestCase(CameraJobKind.Hold)]
    [TestCase(CameraJobKind.Check)]
    public async Task A_job_that_names_no_camera_is_not_taken(CameraJobKind kind)
    {
        await using var process = new FakeCameraProcess();
        var (host, seen) = await StartAsync(process, new CameraJob(CameraProtocol.Version, kind, CameraId: null, CameraName: "OBSBOT"));

        Assert.That(await host, Is.EqualTo(CameraProtocol.JobNotTakenExitCode));
        await seen.Reading;
        Assert.That(seen.Of<CameraFailed>().Single().Why, Is.EqualTo($"The {kind} job names no camera"));
        Assert.That(_captures.Made, Is.Empty);
    }

    /// <summary>
    /// The check opens the camera shared, its reader converting to the asked size, reports every call as a log line, then
    /// the frames counted over its seconds, lets the camera go and ends with 0.
    /// </summary>
    [Test]
    public async Task The_check_reports_every_call_and_the_frames()
    {
        await using var process = new FakeCameraProcess();
        var (host, seen) = await StartAsync(process, Check("1080p"));

        await _time.AdvanceUntilAsync(() => seen.Of<CameraOpened>().Count == 1, Step);
        await _time.AdvanceUntilAsync(() =>
        {
            _captures.Last!.Frames(7);
            return host.IsCompleted;
        }, Step);

        Assert.That(await host, Is.EqualTo(CameraProtocol.DoneExitCode));
        await seen.Reading;
        Assert.That(_captures.Last!.ConvertedTo, Is.EqualTo(CameraSize.P1080));
        Assert.That(seen.Of<CameraOpened>().Single(), Is.EqualTo(new CameraOpened("Integrated Camera", 1920, 1080, 0,
            "none: MJPG 1920x1080 at 30 frames a second, read as NV12 1920x1080")));
        Assert.That(seen.Of<CameraStatus>().Single().Frames, Is.GreaterThan(0));
        Assert.That(seen.Of<CameraLog>().Select(log => log.Text), Does.Contain("4. MediaCapture.InitializeAsync (SharedReadOnly, video only): OK"));
        Assert.That(seen.Logs[^1], Does.StartWith("14. MediaCapture.Dispose (letting go): OK"), "the closing calls go on from the opening ones");
        Assert.That(_captures.Last.Disposed, Is.True);
    }

    /// <summary>A check whose open is refused names the call with its code at Warning, says why in words, and ends with 1.</summary>
    [Test]
    public async Task A_refused_check_names_the_call_and_ends_with_1()
    {
        _captures.Prepare = (capture, _) => capture.Failures[nameof(ICameraCapture.InitializeShared)] = new UnauthorizedAccessException("Access is denied.") { HResult = CameraHresults.AccessDenied };
        await using var process = new FakeCameraProcess();
        var (host, seen) = await StartAsync(process, Check());

        await _time.AdvanceUntilAsync(() => host.IsCompleted, Step);
        await seen.Reading;

        Assert.That(await host, Is.EqualTo(CameraProtocol.FailedExitCode));
        Assert.That(seen.Of<CameraLog>(), Does.Contain(new CameraLog("Warning", "4. MediaCapture.InitializeAsync (SharedReadOnly, video only): failed: 0x80070005 E_ACCESSDENIED (Windows: \"Access is denied.\")")));
        Assert.That(seen.Of<CameraFailed>().Single().Why, Does.StartWith("The camera \"Integrated Camera\" could not be opened: Windows' camera privacy settings or a security program"));
        Assert.That(_captures.Last!.Disposed, Is.True);
    }

    /// <summary>A check whose camera opens but sends no picture says so, and ends with 1.</summary>
    [Test]
    public async Task A_check_without_a_picture_ends_with_1()
    {
        await using var process = new FakeCameraProcess();
        var (host, seen) = await StartAsync(process, Check());

        await _time.AdvanceUntilAsync(() => host.IsCompleted, Step);
        await seen.Reading;

        Assert.That(await host, Is.EqualTo(CameraProtocol.FailedExitCode));
        Assert.That(seen.Of<CameraFailed>().Single().Why, Is.EqualTo("The camera \"Integrated Camera\" opened but sent no picture in 5 seconds; is its lens folded, or is it asleep?"));
        Assert.That(_captures.Last!.Disposed, Is.True);
    }

    /// <summary>A hold job for the OBSBOT.</summary>
    private static CameraJob Hold() => new(CameraProtocol.Version, CameraJobKind.Hold, LaptopCameras.Obsbot.Id, LaptopCameras.Obsbot.Name);

    /// <summary>A check job for the Integrated Camera at <paramref name="resolution"/>.</summary>
    private static CameraJob Check(string resolution = "720p") => new(CameraProtocol.Version, CameraJobKind.Check, LaptopCameras.Integrated.Id, LaptopCameras.Integrated.Name, resolution);

    /// <summary>Starts the real host over <paramref name="process"/>'s own end, on the manual clock, sends <paramref name="job"/> and reads every line it sends.</summary>
    private async Task<(Task<int> Host, CameraLinesSeen Seen)> StartAsync(FakeCameraProcess process, CameraJob job)
    {
        var host = Task.Run(() => new CameraHost { Time = _time, Captures = _captures }.RunAsync(JobId, process.ProcessStream, process.ProcessStream));
        var seen = new CameraLinesSeen(process.Channel);
        await CameraLines.SendJobAsync(process.Channel, job, CancellationToken.None);
        return (host, seen);
    }
}
