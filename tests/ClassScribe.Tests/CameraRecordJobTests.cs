using ClassScribe.Audio.Timeline;
using ClassScribe.Camera;
using ClassScribe.Storage;

namespace ClassScribe.Tests;

/// <summary>
/// The record job (teach-video 6.6 to 6.12), run by the real <see cref="CameraHost"/> over an in-memory pipe, its captures
/// made by <see cref="FakeCameraCaptureFactory"/>, its piece by <see cref="FakePieceWriterFactory"/>, and its time on the
/// manual clock, on which a real <see cref="RecordingClock"/> stands for the recorder's: the camera opened shared, its
/// frames placed on the clock by the clock lines (a pause's line arriving late still leaves its frames out), P letting the
/// camera go and taking it again, the offset, the stop at the sound's end, a lost camera's black frame and its reopening, a
/// camera that gives no picture or is refused, a dark picture, the hardware encoder's fallback, a failed write, a piece
/// already there, a cancel and a starter gone. Nothing is opened and nothing is encoded.
/// </summary>
[TestFixture]
public sealed class CameraRecordJobTests
{
    private static readonly string JobId = TestRuns.Id(9);
    private const string BaseName = "Tajwid 4_2026-10-19_14-00-02Z";
    private static readonly TimeSpan Step = CameraRecordJob.LookEvery;
    private static readonly TimeSpan FrameTime = TimeSpan.FromTicks(333_333);
    private static readonly TimeSpan Latency = TimeSpan.FromMilliseconds(10);

    private ManualTimeProvider _time = null!;
    private FakeCameraCaptureFactory _captures = null!;
    private FakePieceWriterFactory _writers = null!;
    private TempFolder _folder = null!;
    private RecordingClock _clock = null!;

    /// <summary>A new clock, ten seconds in, the recorder's clock on it, new fakes and a temporary recording folder for each test.</summary>
    [SetUp]
    public void SetUp()
    {
        _time = new ManualTimeProvider(TestRuns.Start);
        _time.Advance(TimeSpan.FromSeconds(10));
        _clock = new RecordingClock(_time);
        _captures = new FakeCameraCaptureFactory();
        _writers = new FakePieceWriterFactory();
        _folder = new TempFolder();
    }

    /// <summary>Deletes the temporary folder.</summary>
    [TearDown]
    public void TearDown() => _folder.Dispose();

    /// <summary>
    /// A record job opens the camera shared, its frames in CPU memory, converted to the picture's size; says what it records
    /// and how; places each frame where the clock puts its stamp; and at the stop lets the camera go, writes the waiting
    /// frames, lets the last last to the sound's end, finishes the piece, says so, and ends with 0.
    /// </summary>
    [Test]
    public async Task A_lesson_s_frames_are_written_where_the_clock_puts_them_and_the_piece_ends_with_the_sound()
    {
        await using var process = new FakeCameraProcess();
        var (host, seen) = await StartAsync(process, Record());
        await _time.AdvanceUntilAsync(() => seen.Of<CameraOpened>().Count == 1, Step);
        var origin = await BeginTheClockAsync(process);

        var stamps = await PlayAsync(_captures.Last!, TimeSpan.FromSeconds(2));
        var end = _clock.Now();
        await StopAsync(process, host, end);

        var writer = _writers.Made.Single();
        var capture = _captures.Last!;
        Assert.That(await host, Is.EqualTo(CameraProtocol.DoneExitCode));
        Assert.That(writer.Path, Is.EqualTo(Path.Combine(_folder.Path, $"{BaseName}.cam.mp4")));
        Assert.That(writer.Settings, Is.EqualTo(PieceSettings.For(CameraSize.P720)));
        Assert.That(writer.Samples[0], Is.EqualTo(new WrittenSample(TimeSpan.Zero, true, 16)), "the black frame at 0 first");
        Assert.That(writer.Samples.Skip(1).Select(sample => sample.At), Is.EqualTo(stamps.Select(stamp => new TimeSpan(stamp - origin))));
        Assert.That(writer.FinishedAt, Is.EqualTo(end));
        Assert.That(writer.Disposed, Is.True);
        Assert.That(seen.Of<CameraPiece>().Single(), Is.EqualTo(new CameraPiece(writer.Path, 0, end.TotalSeconds)));
        Assert.That(capture.ConvertedTo, Is.EqualTo(CameraSize.P720));
        Assert.That(capture.CpuFrames, Is.True);
        Assert.That(capture.Calls, Does.Contain(nameof(ICameraCapture.AddFrameReader)).And.Not.Contain(nameof(ICameraCapture.AddFrameArrived)));
        Assert.That(capture.TooEarly, Is.Empty);
        Assert.That(capture.Disposed, Is.True);
        Assert.That(seen.Of<CameraOpened>().Single(), Is.EqualTo(new CameraOpened(LaptopCameras.Obsbot.Name, 1280, 720, 30, "hardware encoder (Fake H.264 Encoder)")));
        Assert.That(seen.Logs[0], Is.EqualTo($"Recording the camera \"{LaptopCameras.Obsbot.Name}\" shared into {BaseName}.cam.mp4, from 00:00:00 on the clock: 1280x720 at 30 frames a second, 5 Mbit/s, H.264 level 3.1, hardware encoder (Fake H.264 Encoder)"));
        Assert.That(seen.Logs[^1], Is.EqualTo($"The camera's piece {BaseName}.cam.mp4 is finished: {Durations.Format(end)}, 60 frames written, 0 dropped"));
        Assert.That(seen.Of<CameraProblem>(), Is.Empty);
    }

    /// <summary>A continued lesson's piece is named by its start on the clock and written from it.</summary>
    [Test]
    public async Task A_continued_lesson_s_piece_begins_at_its_start_on_the_clock()
    {
        _clock.SetOffset(TimeSpan.FromMinutes(10));
        await using var process = new FakeCameraProcess();
        var (host, seen) = await StartAsync(process, Record(startSeconds: 600));
        await _time.AdvanceUntilAsync(() => seen.Of<CameraOpened>().Count == 1, Step);
        var origin = await BeginTheClockAsync(process);

        var stamps = await PlayAsync(_captures.Last!, TimeSpan.FromSeconds(1));
        var end = _clock.Now();
        await StopAsync(process, host, end);

        var writer = _writers.Made.Single();
        Assert.That(Path.GetFileName(writer.Path), Is.EqualTo($"{BaseName}.cam@00-10-00.000.mp4"));
        Assert.That(writer.Samples[1].At, Is.EqualTo(new TimeSpan(stamps[0] - origin)));
        Assert.That(writer.FinishedAt, Is.EqualTo(end - TimeSpan.FromMinutes(10)));
        Assert.That(seen.Of<CameraPiece>().Single().StartSeconds, Is.EqualTo(600));
    }

    /// <summary>
    /// P: the clock line of a pause, sent a quarter of a second after the pause began, still leaves out the frames taken
    /// meanwhile, since each waits half a second; off lets the camera go, on opens it again, and the picture carries on
    /// where the clock does after the pause.
    /// </summary>
    [Test]
    public async Task A_pause_leaves_its_frames_out_lets_the_camera_go_and_takes_it_again()
    {
        await using var process = new FakeCameraProcess();
        var (host, seen) = await StartAsync(process, Record());
        await _time.AdvanceUntilAsync(() => seen.Of<CameraOpened>().Count == 1, Step);
        var origin = await BeginTheClockAsync(process);
        var first = _captures.Last!;
        var before = await PlayAsync(first, TimeSpan.FromSeconds(1));

        _clock.Pause();
        var pausedAt = _clock.Snapshot().PauseStart!.Value;
        var during = await PlayAsync(first, TimeSpan.FromSeconds(0.25));
        await CameraLines.SendClockAsync(process.Channel, _clock.Snapshot(), CancellationToken.None);
        await CameraLines.SendSignalAsync(process.Channel, CameraProtocol.Off, CancellationToken.None);
        await _time.AdvanceUntilAsync(() => first.Disposed, Step, atMost: TimeSpan.FromSeconds(1));
        _time.Advance(TimeSpan.FromSeconds(20));

        _clock.Resume();
        await CameraLines.SendClockAsync(process.Channel, _clock.Snapshot(), CancellationToken.None);
        await CameraLines.SendSignalAsync(process.Channel, CameraProtocol.On, CancellationToken.None);
        await _time.AdvanceUntilAsync(() => _captures.Made.Count == 2 && _captures.Last!.Streaming, Step, atMost: TimeSpan.FromSeconds(1));
        var after = await PlayAsync(_captures.Last!, TimeSpan.FromSeconds(1));
        await StopAsync(process, host, _clock.Now());

        var written = _writers.Made.Single().Samples.Skip(1).Select(sample => sample.At).ToList();
        var paused = _clock.TimePaused;
        Assert.That(during.All(stamp => stamp > pausedAt), Is.True);
        Assert.That(written, Is.EqualTo(before.Select(stamp => new TimeSpan(stamp - origin)).Concat(after.Select(stamp => new TimeSpan(stamp - origin) - paused))));
        Assert.That(seen.Logs, Does.Contain($"Let go of the camera \"{LaptopCameras.Obsbot.Name}\" for the pause"));
        Assert.That(seen.Of<CameraOpened>(), Has.Count.EqualTo(2));
    }

    /// <summary>The camera's offset moves its picture against the sound, and the start says so.</summary>
    [Test]
    public async Task The_offset_moves_the_picture_and_is_said()
    {
        await using var process = new FakeCameraProcess();
        var (host, seen) = await StartAsync(process, Record(offsetMilliseconds: -60));
        await _time.AdvanceUntilAsync(() => seen.Of<CameraOpened>().Count == 1, Step);
        var origin = await BeginTheClockAsync(process);
        _time.Advance(TimeSpan.FromMilliseconds(100));

        var stamps = await PlayAsync(_captures.Last!, TimeSpan.FromSeconds(1));
        await StopAsync(process, host, _clock.Now());

        Assert.That(_writers.Made.Single().Samples[1].At, Is.EqualTo(new TimeSpan(stamps[0] - origin) - TimeSpan.FromMilliseconds(60)));
        Assert.That(seen.Logs, Does.Contain($"The picture is placed 60 ms earlier for \"{LaptopCameras.Obsbot.Name}\" (Recording:CameraOffsets)"));
    }

    /// <summary>
    /// Frames that stop for 3 seconds (unplugged, folded) leave a black frame at that position, let the camera go, are said,
    /// and the camera is opened again 2 seconds later into the same piece.
    /// </summary>
    [Test]
    public async Task A_lost_camera_leaves_a_black_frame_and_is_opened_again_into_the_same_piece()
    {
        await using var process = new FakeCameraProcess();
        var (host, seen) = await StartAsync(process, Record());
        await _time.AdvanceUntilAsync(() => seen.Of<CameraOpened>().Count == 1, Step);
        await BeginTheClockAsync(process);
        var first = _captures.Last!;
        await PlayAsync(first, TimeSpan.FromSeconds(1));

        var stopped = await _time.AdvanceUntilAsync(() => seen.Of<CameraProblem>().Count == 1, Step, atMost: TimeSpan.FromSeconds(4));
        var again = await _time.AdvanceUntilAsync(() => seen.Of<CameraOpened>().Count == 2, Step, atMost: TimeSpan.FromSeconds(3));
        await PlayAsync(_captures.Last!, TimeSpan.FromSeconds(1));
        await StopAsync(process, host, _clock.Now());

        var samples = _writers.Made.Single().Samples;
        var black = samples.Skip(1).Single(sample => sample.Black);
        Assert.That(seen.Of<CameraProblem>().Single(), Is.EqualTo(new CameraProblem(CameraProblemKind.Lost, CameraProblems.Stopped)));
        Assert.That(stopped, Is.GreaterThanOrEqualTo(CameraRecordJob.StoppedAfter));
        Assert.That(again, Is.GreaterThanOrEqualTo(CameraRecordJob.RetryEvery));
        Assert.That(first.Disposed, Is.True);
        Assert.That(black.At, Is.GreaterThan(TimeSpan.FromSeconds(3.9)).And.LessThan(TimeSpan.FromSeconds(4.2)), "the black frame where the picture was found gone");
        Assert.That(samples.SkipWhile(sample => !sample.Black || sample.At == TimeSpan.Zero).Skip(1).Count(), Is.GreaterThan(25), "the picture goes on after it, in the same piece");
        Assert.That(_writers.Made, Has.Count.EqualTo(1));
    }

    /// <summary>A camera that opens but sends no picture for 5 seconds is said, as a security program's block would look, and opened again.</summary>
    [Test]
    public async Task A_camera_that_gives_no_picture_is_said_and_opened_again()
    {
        await using var process = new FakeCameraProcess();
        var (host, seen) = await StartAsync(process, Record());
        await _time.AdvanceUntilAsync(() => seen.Of<CameraOpened>().Count == 1, Step);

        var waited = await _time.AdvanceUntilAsync(() => seen.Of<CameraProblem>().Count == 1, Step, atMost: TimeSpan.FromSeconds(6));
        await _time.AdvanceUntilAsync(() => _captures.Made.Count == 2, Step, atMost: TimeSpan.FromSeconds(3));
        await CancelAsync(process, host);

        Assert.That(seen.Of<CameraProblem>().Single(), Is.EqualTo(new CameraProblem(CameraProblemKind.NoPicture, CameraRecordJob.NoPictureWords)));
        Assert.That(waited, Is.GreaterThanOrEqualTo(CameraRecordJob.FirstFrameLimit));
        Assert.That(_captures.Made[0].Capture.Disposed, Is.True);
    }

    /// <summary>A camera refused is said once, with the cause, and tried again every 2 seconds until it opens; the piece is there all along.</summary>
    [Test]
    public async Task A_refused_camera_is_said_once_and_tried_again_every_2_seconds()
    {
        _captures.Prepare = (capture, index) =>
        {
            if (index < 2) capture.Failures[nameof(ICameraCapture.InitializeShared)] = new UnauthorizedAccessException("Access is denied.") { HResult = CameraHresults.AccessDenied };
        };
        await using var process = new FakeCameraProcess();
        var (host, seen) = await StartAsync(process, Record());

        var moved = await _time.AdvanceUntilAsync(() => seen.Of<CameraOpened>().Count == 1, Step, atMost: TimeSpan.FromSeconds(6));
        await CancelAsync(process, host);

        Assert.That(seen.Of<CameraProblem>().Single().Kind, Is.EqualTo(CameraProblemKind.Denied));
        Assert.That(seen.Of<CameraProblem>().Single().Words, Does.Contain("E_ACCESSDENIED").And.Contain(CameraRoute.InitializeForPixels));
        Assert.That(moved, Is.GreaterThanOrEqualTo(CameraRecordJob.RetryEvery * 2));
        Assert.That(_captures.Made, Has.Count.EqualTo(3));
        Assert.That(File.Exists(_writers.Made.Single().Path), Is.True);
    }

    /// <summary>A picture black for 10 seconds is said once, and the status says dark until it brightens.</summary>
    [Test]
    public async Task A_dark_picture_is_said_once_and_shown_until_it_brightens()
    {
        await using var process = new FakeCameraProcess();
        var (host, seen) = await StartAsync(process, Record());
        await _time.AdvanceUntilAsync(() => seen.Of<CameraOpened>().Count == 1, Step);
        await BeginTheClockAsync(process);

        await PlayAsync(_captures.Last!, TimeSpan.FromSeconds(10.5), brightness: 10);
        await _time.AdvanceUntilAsync(() => seen.Of<CameraStatus>().Any(status => status.Dark), Step, atMost: TimeSpan.FromSeconds(2));
        await PlayAsync(_captures.Last!, TimeSpan.FromSeconds(2), brightness: 140);
        var statuses = seen.Of<CameraStatus>();
        await StopAsync(process, host, _clock.Now());

        Assert.That(seen.Of<CameraProblem>().Single(), Is.EqualTo(new CameraProblem(CameraProblemKind.Dark, CameraRecordJob.DarkWords)));
        Assert.That(statuses[^1].Dark, Is.False, "bright again");
        Assert.That(statuses.Count(status => status.Dark), Is.GreaterThan(0));
    }

    /// <summary>A hardware encoder that refuses its start gives way to Windows' software encoder once, with a line, in a piece made afresh.</summary>
    [Test]
    public async Task A_refused_hardware_encoder_gives_way_to_the_software_encoder()
    {
        _writers.HardwareRefuses = new System.Runtime.InteropServices.COMException("Unspecified error", unchecked((int)0x80004005));
        await using var process = new FakeCameraProcess();
        var (host, seen) = await StartAsync(process, Record());
        await _time.AdvanceUntilAsync(() => seen.Of<CameraOpened>().Count == 1, Step);
        await CancelAsync(process, host);

        Assert.That(_writers.Made.Single().Settings.Software, Is.True);
        Assert.That(seen.Of<CameraOpened>().Single().Encoder, Does.StartWith("software encoder"));
        Assert.That(seen.Of<CameraLog>(), Does.Contain(new CameraLog("Warning",
            "The hardware encoder could not start (0x80004005 E_FAIL (Windows: \"Unspecified error\")); the camera's picture is encoded by Windows' software encoder instead")));
    }

    /// <summary>A job that asks for the software encoder, as a restart after the hardware one failed does, gets it from the start.</summary>
    [Test]
    public async Task A_job_that_asks_for_the_software_encoder_gets_it()
    {
        await using var process = new FakeCameraProcess();
        var (host, seen) = await StartAsync(process, Record() with { SoftwareEncoder = true });
        await _time.AdvanceUntilAsync(() => seen.Of<CameraOpened>().Count == 1, Step);
        await CancelAsync(process, host);

        Assert.That(_writers.Made.Single().Settings.Software, Is.True);
    }

    /// <summary>
    /// A write that fails (the disk full) finishes the piece as far as it goes, lets the camera go, says the problem and why,
    /// and ends with 1, for the starter to start a new piece.
    /// </summary>
    [Test]
    public async Task A_failed_write_finishes_the_piece_says_why_and_ends_with_1()
    {
        _writers.Prepare = writer => writer.FailWrite = (5, new IOException("There is not enough space on the disk."));
        await using var process = new FakeCameraProcess();
        var (host, seen) = await StartAsync(process, Record());
        await _time.AdvanceUntilAsync(() => seen.Of<CameraOpened>().Count == 1, Step);
        await BeginTheClockAsync(process);

        await PlayAsync(_captures.Last!, TimeSpan.FromSeconds(1));
        await _time.AdvanceUntilAsync(() => host.IsCompleted, Step, atMost: TimeSpan.FromSeconds(2));
        await seen.Reading;

        var writer = _writers.Made.Single();
        Assert.That(await host, Is.EqualTo(CameraProtocol.FailedExitCode));
        Assert.That(writer.FinishedAt, Is.Not.Null);
        Assert.That(writer.Samples, Has.Count.EqualTo(5));
        Assert.That(_captures.Last!.Disposed, Is.True);
        Assert.That(seen.Of<CameraProblem>().Single(), Is.EqualTo(new CameraProblem(CameraProblemKind.Disk, "writing its picture failed (IOException: There is not enough space on the disk.)")));
        Assert.That(seen.Of<CameraFailed>().Single().Why, Is.EqualTo($"Writing the camera's piece {BaseName}.cam.mp4 failed: IOException: There is not enough space on the disk."));
    }

    /// <summary>A piece of that name already there is never written over: the job fails, ends with 1, and opens nothing.</summary>
    [Test]
    public async Task A_piece_already_there_is_never_written_over()
    {
        var path = new RecordingFiles(_folder.Path, BaseName).CameraPiece(TimeSpan.Zero);
        File.WriteAllText(path, "an earlier piece");
        await using var process = new FakeCameraProcess();
        var (host, seen) = await StartAsync(process, Record());

        Assert.That(await host.WaitAsync(TimeSpan.FromSeconds(10)), Is.EqualTo(CameraProtocol.FailedExitCode));
        await seen.Reading;
        Assert.That(seen.Of<CameraFailed>().Single().Why, Is.EqualTo($"The camera's piece {BaseName}.cam.mp4 is there already, and ClassScribe never writes over a piece"));
        Assert.That(File.ReadAllText(path), Is.EqualTo("an earlier piece"));
        Assert.That(_captures.Made, Is.Empty);
        Assert.That(_writers.Made, Is.Empty);
    }

    /// <summary>A cancel finishes the piece as far as it goes, the last frame lasting a frame's time, and ends with 130.</summary>
    [Test]
    public async Task A_cancel_finishes_the_piece_as_far_as_it_goes()
    {
        await using var process = new FakeCameraProcess();
        var (host, seen) = await StartAsync(process, Record());
        await _time.AdvanceUntilAsync(() => seen.Of<CameraOpened>().Count == 1, Step);
        await BeginTheClockAsync(process);
        await PlayAsync(_captures.Last!, TimeSpan.FromSeconds(1));

        var code = await CancelAsync(process, host);

        var writer = _writers.Made.Single();
        Assert.That(code, Is.EqualTo(CameraProtocol.CancelledExitCode));
        Assert.That(writer.FinishedAt, Is.EqualTo(writer.Samples[^1].At + TimeSpan.FromSeconds(1.0 / 30)));
        Assert.That(writer.Samples, Has.Count.EqualTo(31));
        Assert.That(_captures.Last!.Disposed, Is.True);
    }

    /// <summary>The starter's end of the lines closing (the recorder gone) finishes the piece as far as it goes, and ends with 130.</summary>
    [Test]
    public async Task A_starter_gone_finishes_the_piece()
    {
        await using var process = new FakeCameraProcess();
        var (host, seen) = await StartAsync(process, Record());
        await _time.AdvanceUntilAsync(() => seen.Of<CameraOpened>().Count == 1, Step);
        await BeginTheClockAsync(process);
        await PlayAsync(_captures.Last!, TimeSpan.FromSeconds(1));

        await process.Channel.DisposeAsync();
        await _time.AdvanceUntilAsync(() => host.IsCompleted, Step);

        Assert.That(await host, Is.EqualTo(CameraProtocol.CancelledExitCode));
        Assert.That(_writers.Made.Single().FinishedAt, Is.Not.Null);
        Assert.That(_captures.Last!.Disposed, Is.True);
    }

    /// <summary>A frame without a stamp is placed by the time it arrived.</summary>
    [Test]
    public async Task A_frame_without_a_stamp_is_placed_by_its_arrival()
    {
        await using var process = new FakeCameraProcess();
        var (host, seen) = await StartAsync(process, Record());
        await _time.AdvanceUntilAsync(() => seen.Of<CameraOpened>().Count == 1, Step);
        var origin = await BeginTheClockAsync(process);
        _time.Advance(TimeSpan.FromMilliseconds(400));

        var arrival = MediaTime.Now(_time);
        _captures.Last!.Deliver(null);
        await StopAsync(process, host, _clock.Now() + TimeSpan.FromSeconds(1));

        Assert.That(_writers.Made.Single().Samples[1].At, Is.EqualTo(new TimeSpan(arrival - origin)));
    }

    /// <summary>The status says how many frames were written and how many a second, and whether the camera records now; while it is let go for a pause it does not.</summary>
    [Test]
    public async Task The_status_says_the_frames_the_rate_and_whether_the_camera_records()
    {
        await using var process = new FakeCameraProcess();
        var (host, seen) = await StartAsync(process, Record());
        await _time.AdvanceUntilAsync(() => seen.Of<CameraOpened>().Count == 1, Step);
        await BeginTheClockAsync(process);
        await PlayAsync(_captures.Last!, TimeSpan.FromSeconds(3));
        var recording = seen.Of<CameraStatus>()[^1];

        await CameraLines.SendSignalAsync(process.Channel, CameraProtocol.Off, CancellationToken.None);
        await _time.AdvanceUntilAsync(() => _captures.Last!.Disposed, Step);
        var count = seen.Of<CameraStatus>().Count;
        await _time.AdvanceUntilAsync(() => seen.Of<CameraStatus>().Count > count + 1, Step);
        var paused = seen.Of<CameraStatus>()[^1];
        await CancelAsync(process, host);

        Assert.That(recording.Capturing, Is.True);
        Assert.That(recording.FramesPerSecond, Is.InRange(25, 35));
        Assert.That(recording.Frames, Is.GreaterThan(40));
        Assert.That(paused.Capturing, Is.False);
    }

    /// <summary>A record job's job: the OBSBOT at 720p into the temporary folder, from <paramref name="startSeconds"/> on the clock.</summary>
    private CameraJob Record(double startSeconds = 0, int offsetMilliseconds = 0) =>
        new(CameraProtocol.Version, CameraJobKind.Record, LaptopCameras.Obsbot.Id, LaptopCameras.Obsbot.Name, "720p", offsetMilliseconds, _folder.Path, BaseName, startSeconds);

    /// <summary>Starts the real host over <paramref name="process"/>'s own end, on the manual clock, sends <paramref name="job"/> and reads every line it sends.</summary>
    private async Task<(Task<int> Host, CameraLinesSeen Seen)> StartAsync(FakeCameraProcess process, CameraJob job)
    {
        var camera = new CameraHost { Time = _time, Captures = _captures, Writers = _writers };
        var host = Task.Run(() => camera.RunAsync(JobId, process.ProcessStream, process.ProcessStream));
        var seen = new CameraLinesSeen(process.Channel);
        await CameraLines.SendJobAsync(process.Channel, job, CancellationToken.None);
        return (host, seen);
    }

    /// <summary>Begins the recorder's clock now, as the first packet of sound does, and sends its line; returns the origin.</summary>
    private async Task<long> BeginTheClockAsync(FakeCameraProcess process)
    {
        var origin = MediaTime.Now(_time);
        _clock.Begin(origin);
        await CameraLines.SendClockAsync(process.Channel, _clock.Snapshot(), CancellationToken.None);
        return origin;
    }

    /// <summary>
    /// Plays the camera for <paramref name="length"/> at 30 frames a second, each frame stamped a little before it arrives,
    /// as a camera's are, giving the job's loop room to run; returns the stamps handed over.
    /// </summary>
    private async Task<List<long>> PlayAsync(FakeCameraCapture capture, TimeSpan length, byte brightness = 128)
    {
        var stamps = new List<long>();
        for (var played = TimeSpan.Zero; played + FrameTime <= length + TimeSpan.FromTicks(10); played += FrameTime)
        {
            _time.Advance(FrameTime);
            var stamp = MediaTime.Now(_time) - Latency.Ticks;
            if (capture.Deliver(stamp, brightness)) stamps.Add(stamp);
            if (stamps.Count % 3 == 0) await Task.Delay(2);
        }

        await Task.Delay(5);
        return stamps;
    }

    /// <summary>Sends the clock's last line and the stop at <paramref name="end"/>, as the recorder's stop does, and waits for the job to end.</summary>
    private async Task StopAsync(FakeCameraProcess process, Task<int> host, TimeSpan end)
    {
        await CameraLines.SendClockAsync(process.Channel, _clock.Snapshot(), CancellationToken.None);
        await CameraLines.SendStopAsync(process.Channel, new CameraStop(end.TotalSeconds), CancellationToken.None);
        await _time.AdvanceUntilAsync(() => host.IsCompleted, Step);
    }

    /// <summary>Sends a cancel, waits for the job to end, and returns its exit code.</summary>
    private async Task<int> CancelAsync(FakeCameraProcess process, Task<int> host)
    {
        await CameraLines.SendSignalAsync(process.Channel, CameraProtocol.Cancel, CancellationToken.None);
        await _time.AdvanceUntilAsync(() => host.IsCompleted, Step);
        return await host;
    }
}
