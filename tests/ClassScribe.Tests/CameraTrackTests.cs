using System.ComponentModel;
using ClassScribe.Audio.Timeline;
using ClassScribe.Camera;
using ClassScribe.Commands;
using Microsoft.Extensions.Logging;

namespace ClassScribe.Tests;

/// <summary>
/// A lesson's camera track in the recorder (teach-video 6.2, 6.6 to 6.8, 6.10 to 6.13), over played camera processes on the
/// manual clock: the record job it starts, the clock lines it sends (at once when nudged, else within a quarter of a second),
/// P's off and on after the clock's line, the stop that waits for the piece at most 15 seconds, the restart rule and its
/// limit, the camera not connected, giving the picture up, and the camera process's problems as the run's words. Nothing is
/// started and no camera is opened.
/// </summary>
[TestFixture]
public sealed class CameraTrackTests
{
    private static readonly TimeSpan Step = CameraTrack.LookEvery;

    private ManualTimeProvider _time = null!;
    private RecordingClock _clock = null!;
    private FakeCameraProvider _cameras = null!;
    private FakeCameraProcessStarter _starter = null!;
    private RecordingLogger<CameraTrack> _log = null!;
    private CameraTrack? _track;

    /// <summary>A clock ten seconds in, the recorder's clock on it, the laptop's cameras, a starter that plays each process, and a log.</summary>
    [SetUp]
    public void SetUp()
    {
        _time = new ManualTimeProvider(TestRuns.Start);
        _time.Advance(TimeSpan.FromSeconds(10));
        _clock = new RecordingClock(_time);
        _cameras = new FakeCameraProvider();
        _starter = new FakeCameraProcessStarter { PlayEach = true };
        _log = new RecordingLogger<CameraTrack>();
    }

    /// <summary>Stops the track, if a test left it running.</summary>
    [TearDown]
    public async Task TearDown()
    {
        if (_track is not null) await _track.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
    }

    /// <summary>
    /// The record job names the camera the plan asks for by its id and name, with the picture's size, the longest offset
    /// that names it, the recording's folder and base name, and where the piece begins; the clock's state follows at once.
    /// </summary>
    [Test]
    public async Task The_job_names_the_camera_the_size_the_offset_the_recording_and_the_piece_s_start()
    {
        var played = await StartAsync(offsets: new Dictionary<string, int> { ["OBSBOT"] = -60, ["obsbot meet"] = -40, ["Integrated"] = -30 }, start: TimeSpan.FromMinutes(12));

        await _time.AdvanceUntilAsync(() => played.Clocks.Count > 0, Step);

        Assert.That(played.Job, Is.EqualTo(new CameraJob(CameraProtocol.Version, CameraJobKind.Record, LaptopCameras.Obsbot.Id, LaptopCameras.Obsbot.Name, "720p", -40,
            @"C:\Tajwid\2026-10-22", "Tajwid_2026-10-22_14-00-00Z", 720)));
        Assert.That(played.Clocks[0].Version, Is.EqualTo(_clock.Snapshot().Version));
        Assert.That(_track!.State.Name, Is.EqualTo(LaptopCameras.Obsbot.Name));
        Assert.That(_track.State.ContainerId, Is.EqualTo(LaptopCameras.ObsbotContainer));
    }

    /// <summary>The camera's start line names it, where it is, its size, its bitrate and its encoder, and the light, once it opens; a later opening says nothing more.</summary>
    [Test]
    public async Task The_start_line_comes_once_the_camera_opens()
    {
        var played = await StartAsync();

        await played.OpenedAsync();
        await played.OpenedAsync();
        await _time.AdvanceUntilAsync(() => _log.Entries.Any(entry => entry.Message.StartsWith("Camera: ", StringComparison.Ordinal)), Step);
        await Task.Delay(50);

        Assert.That(_log.Messages.Where(message => message.StartsWith("Camera: ", StringComparison.Ordinal)), Is.EqualTo(new[]
        {
            $"Camera: \"{LaptopCameras.Obsbot.Name}\" (USB), 1280x720 at 30 frames a second, 5 Mbit/s, hardware encoder (Fake H.264 Encoder). Its light is on while it records; P lets it go during a pause"
        }));
    }

    /// <summary>
    /// The start line says the frames a second the camera sends as it runs, shared with other apps, which the piece keeps; and
    /// that it keeps up to 30 a second when Windows does not say the camera's rate.
    /// </summary>
    [TestCase(15, "1280x720 at 15 frames a second")]
    [TestCase(0, "1280x720 at up to 30 frames a second")]
    public async Task The_start_line_says_the_camera_s_own_frame_rate(double framesPerSecond, string words)
    {
        var played = await StartAsync();

        await played.OpenedAsync(framesPerSecond: framesPerSecond);
        await WaitAsync(() => _log.Messages.Any(message => message.StartsWith("Camera: ", StringComparison.Ordinal)));

        Assert.That(_log.Messages.Single(message => message.StartsWith("Camera: ", StringComparison.Ordinal)),
            Is.EqualTo($"Camera: \"{LaptopCameras.Obsbot.Name}\" (USB), {words}, 5 Mbit/s, hardware encoder (Fake H.264 Encoder). Its light is on while it records; P lets it go during a pause"));
    }

    /// <summary>
    /// P sends the clock's pause and then off, at once, without waiting for the track's next look; P again the clock and on.
    /// So the pause reaches the camera process before the frames it leaves out are placed.
    /// </summary>
    [Test]
    public async Task A_pause_sends_the_clock_then_off_at_once_and_a_resume_the_clock_then_on()
    {
        var played = await StartAsync();
        _clock.Begin(MediaTime.Now(_time));
        await _time.AdvanceUntilAsync(() => played.Clocks.Any(clock => clock.Origin is not null), Step);

        _clock.Pause();
        _track!.Pause();
        await WaitAsync(() => played.Received.Count >= 2 && played.Received[^1] == CameraProtocol.Off);
        var paused = played.Received;
        _time.Advance(TimeSpan.FromSeconds(30));
        _clock.Resume();
        _track.Resume();
        await WaitAsync(() => played.Received[^1] == CameraProtocol.On);

        Assert.That(paused.TakeLast(2), Is.EqualTo(new[] { CameraProtocol.Clock, CameraProtocol.Off }));
        Assert.That(played.Received.TakeLast(2), Is.EqualTo(new[] { CameraProtocol.Clock, CameraProtocol.On }));
        Assert.That(played.Clocks[^2].IsPaused, Is.True);
        Assert.That(played.Clocks[^1].IsPaused, Is.False);
    }

    /// <summary>A cut nudges its clock line out at once; a change without a nudge goes out within a quarter of a second.</summary>
    [Test]
    public async Task A_cut_goes_out_at_once_and_any_change_within_a_quarter_second()
    {
        var played = await StartAsync();
        await _time.AdvanceUntilAsync(() => played.Clocks.Count == 1, Step);

        _clock.Begin(MediaTime.Now(_time));
        var moved = await _time.AdvanceUntilAsync(() => played.Clocks.Count == 2, TimeSpan.FromMilliseconds(50));
        _clock.Cut(MediaTime.Now(_time) - TimeSpan.FromSeconds(5).Ticks, MediaTime.Now(_time) - TimeSpan.FromSeconds(1).Ticks);
        _track!.Nudge();
        await WaitAsync(() => played.Clocks.Count == 3);

        Assert.That(moved, Is.LessThanOrEqualTo(CameraTrack.LookEvery * 2), "within the track's next look, as the manual clock can tell it");
        Assert.That(played.Clocks[^1].Excluded, Has.Count.EqualTo(1));
        Assert.That(played.Clocks[^1].Version, Is.EqualTo(_clock.Snapshot().Version));
    }

    /// <summary>P, P again and a nudge never wait: they return at once even while the track's loop is held inside a start Windows is slow to make.</summary>
    [Test]
    public async Task Pause_resume_and_a_nudge_never_wait_for_the_track()
    {
        using var held = new ManualResetEventSlim();
        _starter.HoldStart(0, held);
        try
        {
            _track = new CameraTrack(Start(), _clock, _cameras, _starter, _time, _log);
            _track.Begin();
            await WaitAsync(() => _starter.StartsEntered == 1);

            var calls = Task.Run(() =>
            {
                _track.Pause();
                _track.Resume();
                _track.Nudge();
            });

            Assert.That(calls.Wait(TimeSpan.FromSeconds(2)), Is.True);
        }
        finally
        {
            held.Set();
        }
    }

    /// <summary>
    /// The stop sends the clock's last line and the end, where the sound ends, and waits for the piece, which is said; the
    /// process is let go.
    /// </summary>
    [Test]
    public async Task The_stop_sends_the_last_clock_and_the_end_and_waits_for_the_piece()
    {
        var played = await StartAsync();
        _clock.Begin(MediaTime.Now(_time));
        _time.Advance(TimeSpan.FromSeconds(40));
        var end = _clock.Now();

        await _track!.StopAsync(end, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.That(played.Stop, Is.EqualTo(new CameraStop(end.TotalSeconds)));
        Assert.That(played.Received.TakeLast(2), Is.EqualTo(new[] { CameraProtocol.Clock, CameraProtocol.Stop }));
        Assert.That(played.Clocks[^1].Version, Is.EqualTo(_clock.Snapshot().Version));
        Assert.That(_log.Messages, Does.Contain("The camera's piece Tajwid_2026-10-22_14-00-00Z.cam.mp4 is finished, from 00:00:00 to 00:00:40 on the clock"));
        Assert.That(played.Process.Killed, Is.False);
    }

    /// <summary>
    /// The sound's end goes to the camera process at once, without waiting for the track's next look and before any stop, so
    /// that no frame after it is written while the sound is being stopped and saved.
    /// </summary>
    [Test]
    public async Task The_sound_s_end_goes_to_the_camera_process_at_once()
    {
        var played = await StartAsync();
        _clock.Begin(MediaTime.Now(_time));
        _time.Advance(TimeSpan.FromSeconds(4));
        var end = _clock.Now();

        _track!.EndAt(end);
        await WaitAsync(() => played.End is not null);

        Assert.That(played.End, Is.EqualTo(new CameraStop(end.TotalSeconds)));
        Assert.That(played.StopReceived, Is.False, "the stop follows once the sound is saved");
    }

    /// <summary>
    /// A camera process that stops reading its input, so that a line to it never goes out, is ended 5 seconds into that
    /// line, which breaks its pipe; the stop then returns, and never waited on the line any longer.
    /// </summary>
    [Test]
    public async Task A_camera_process_that_stops_reading_its_input_is_ended_and_the_stop_returns()
    {
        var played = await StartAsync();
        _clock.Begin(MediaTime.Now(_time));
        await _time.AdvanceUntilAsync(() => played.Clocks.Any(clock => clock.Origin is not null), Step);

        played.Process.InputStuck = true;
        var stopping = _track!.StopAsync(_clock.Now(), CancellationToken.None);
        await WaitAsync(() => _time.UntilNextTimer == CameraTrack.SendLimit);
        var moved = await _time.AdvanceUntilAsync(() => stopping.IsCompleted, TimeSpan.FromSeconds(1));
        await stopping;

        Assert.That(moved, Is.EqualTo(CameraTrack.SendLimit));
        Assert.That(played.Process.Killed, Is.True);
        Assert.That(_log.Problems, Has.Some.EqualTo("The camera process did not take a line within 5 seconds, so it was ended"));
    }

    /// <summary>
    /// A camera process that cannot be let go (it cannot be ended, so its error output never closes) holds up the stop at
    /// most 5 seconds after its piece was said.
    /// </summary>
    [Test]
    public async Task A_camera_process_that_cannot_be_let_go_holds_up_the_stop_at_most_5_seconds()
    {
        var played = await StartAsync();
        played.Process.DisposeHangs = true;

        var stopping = _track!.StopAsync(_clock.Now(), CancellationToken.None);
        await WaitAsync(() => played.Process.DisposeEntered && _time.UntilNextTimer == CameraTrack.DisposeWait);
        var moved = await _time.AdvanceUntilAsync(() => stopping.IsCompleted, TimeSpan.FromSeconds(1));
        await stopping;

        Assert.That(moved, Is.EqualTo(CameraTrack.DisposeWait));
        Assert.That(_log.Problems.Single(), Is.EqualTo("The camera process could not be let go within 5 seconds; it ends with ClassScribe at the latest"));
    }

    /// <summary>Frames that come but cannot be written are an Alert in the run's words, naming what comes.</summary>
    [Test]
    public async Task Frames_that_cannot_be_written_are_an_Alert_in_the_run_s_words()
    {
        var played = await StartAsync();

        await played.ProblemAsync(CameraProblemKind.Unwritable, "its frames come at 1920x1080, not the 1280x720 asked for");
        await WaitAsync(() => _track!.State.Concern is not null);

        Assert.That(_track!.State.Concern, Is.EqualTo(new CameraConcern(true, "CAMERA NOT WRITTEN",
            $"The camera \"{LaptopCameras.Obsbot.Name}\" sends its picture, but it cannot be written: its frames come at 1920x1080, not the 1280x720 asked for. ClassScribe opens it again every 2 seconds. The sound records on")));
    }

    /// <summary>A camera process that does not finish its piece within 15 seconds of the stop is ended, and the stop returns; the sound never waited longer.</summary>
    [Test]
    public async Task A_camera_process_that_hangs_at_the_stop_is_ended_after_15_seconds()
    {
        var played = await StartAsync();
        played.HangOnStop = true;

        var stopping = _track!.StopAsync(_clock.Now(), CancellationToken.None);
        await WaitAsync(() => played.Stop is not null && _time.UntilNextTimer == CameraTrack.StopWait);
        var moved = await _time.AdvanceUntilAsync(() => stopping.IsCompleted, TimeSpan.FromSeconds(1));
        await stopping;

        Assert.That(moved, Is.EqualTo(CameraTrack.StopWait));
        Assert.That(played.Process.Killed, Is.True);
        Assert.That(_log.Problems.Single(), Does.StartWith("The camera process did not finish its piece within 15 seconds of the stop"));
    }

    /// <summary>
    /// A camera process that ends by itself while recording gets a new one 2 seconds later, its piece from the clock's
    /// position then; meanwhile the run is told the camera restarts.
    /// </summary>
    [Test]
    public async Task A_camera_process_that_ends_is_started_again_from_the_clock_s_position()
    {
        var first = await StartAsync();
        _clock.Begin(MediaTime.Now(_time));
        _time.Advance(TimeSpan.FromSeconds(30));

        first.Process.Exit(1);
        await WaitAsync(() => _track!.State.Concern?.Short == "CAMERA RESTARTING");
        var position = _clock.Now();
        var moved = await _time.AdvanceUntilAsync(() => _starter.Played.Count == 2 && _starter.Played[1].Job is not null, TimeSpan.FromMilliseconds(250));
        var second = _starter.Played[1];

        Assert.That(moved, Is.GreaterThanOrEqualTo(CameraTrack.RestartAfter));
        Assert.That(second.Job!.StartSeconds, Is.GreaterThanOrEqualTo(position.TotalSeconds));
        Assert.That(second.Job.SoftwareEncoder, Is.False);
        Assert.That(_track!.State.Concern!.Alert, Is.True);
    }

    /// <summary>After the camera process said its encoder failed, the next one asks for Windows' software encoder.</summary>
    [Test]
    public async Task After_an_encoder_failure_the_next_process_uses_the_software_encoder()
    {
        var first = await StartAsync();

        await first.ProblemAsync(CameraProblemKind.Encoder, "writing its picture failed (0x80004005 E_FAIL)");
        await first.FailedAsync("Writing the camera's piece failed");
        await first.Process.EndAsync(1);
        await _time.AdvanceUntilAsync(() => _starter.Played.Count == 2 && _starter.Played[1].Job is not null, TimeSpan.FromMilliseconds(250));

        Assert.That(_starter.Played[1].Job!.SoftwareEncoder, Is.True);
    }

    /// <summary>The third time the camera stops working within ten minutes, the picture is given up for the rest of the run, with an Alert, and no other process starts.</summary>
    [Test]
    public async Task The_third_failure_within_ten_minutes_gives_the_picture_up()
    {
        await StartAsync();
        for (var failure = 1; failure <= 3; failure++)
        {
            await WaitAsync(() => _starter.Played.Count == failure && _starter.Played[^1].Job is not null);
            _starter.Played[^1].Process.Exit(1);
            if (failure < 3) await _time.AdvanceUntilAsync(() => _starter.Played.Count == failure + 1, TimeSpan.FromMilliseconds(250));
        }

        await WaitAsync(() => _track!.State.GivenUp);
        _time.Advance(TimeSpan.FromMinutes(1));
        await Task.Delay(50);

        Assert.That(_starter.Played, Has.Count.EqualTo(3));
        Assert.That(_track!.State.Concern, Is.EqualTo(new CameraConcern(true, CameraTrack.GivenUpShort, CameraTrack.GivenUpText)));
        Assert.That(_log.Problems, Does.Contain(CameraTrack.GivenUpText));
    }

    /// <summary>Failures more than ten minutes apart do not add up: each restarts the camera.</summary>
    [Test]
    public async Task Failures_ten_minutes_apart_do_not_add_up()
    {
        await StartAsync();
        for (var failure = 1; failure <= 3; failure++)
        {
            await WaitAsync(() => _starter.Played.Count == failure && _starter.Played[^1].Job is not null);
            _starter.Played[^1].Process.Exit(1);
            await _time.AdvanceUntilAsync(() => _starter.Played.Count == failure + 1, TimeSpan.FromMilliseconds(250));
            _time.Advance(TimeSpan.FromMinutes(6));
        }

        Assert.That(_track!.State.GivenUp, Is.False);
        Assert.That(_starter.Played, Has.Count.EqualTo(4));
    }

    /// <summary>A camera process Windows will not start counts as the camera stopping working, and is tried again.</summary>
    [Test]
    public async Task A_camera_process_that_will_not_start_is_tried_again()
    {
        _starter.StartFailure = new Win32Exception(5, "Windows did not start the camera process: Access is denied (error 5)");
        _track = new CameraTrack(Start(), _clock, _cameras, _starter, _time, _log);
        _track.Begin();

        await WaitAsync(() => _track.State.Concern is not null);
        Assert.That(_track.State.Concern!.Text, Does.StartWith("Windows did not start ClassScribe's camera process (Windows did not start the camera process: Access is denied (error 5)); ClassScribe starts it again in 2 seconds"));
        _starter.StartFailure = null;
        await _time.AdvanceUntilAsync(() => _starter.Played.Count == 1, TimeSpan.FromMilliseconds(250));
    }

    /// <summary>
    /// The camera process's problems become the run's words with the camera's name: lost and a blocked picture as Alerts, a
    /// dark picture as a Notice; a picture back clears them, and a dark one only once it brightens.
    /// </summary>
    [Test]
    public async Task Problems_become_the_run_s_words_and_a_picture_back_clears_them()
    {
        var played = await StartAsync();

        await played.ProblemAsync(CameraProblemKind.Lost, CameraProblems.Stopped);
        await WaitAsync(() => _track!.State.Concern is not null);
        var lost = _track!.State.Concern;
        await played.RecordStatusAsync(30);
        await WaitAsync(() => _track.State.Concern is null);
        await played.ProblemAsync(CameraProblemKind.NoPicture, CameraRecordJob.NoPictureWords);
        await WaitAsync(() => _track.State.Concern is not null);
        var blocked = _track.State.Concern;
        await played.RecordStatusAsync(60);
        await played.ProblemAsync(CameraProblemKind.Dark, CameraRecordJob.DarkWords);
        await WaitAsync(() => _track.State.Concern is not null);
        await played.RecordStatusAsync(90, dark: true);
        await Task.Delay(50);
        var dark = _track.State;
        await played.RecordStatusAsync(120);
        await WaitAsync(() => _track.State.Concern is null);

        Assert.That(lost, Is.EqualTo(new CameraConcern(true, "CAMERA LOST",
            $"The camera \"{LaptopCameras.Obsbot.Name}\" stopped sending its picture; the sound records on. Plug it back in")));
        Assert.That(blocked, Is.EqualTo(new CameraConcern(true, "CAMERA GIVES NO PICTURE",
            $"The camera \"{LaptopCameras.Obsbot.Name}\" gave no picture. Windows allows it, so a security program may be blocking ClassScribe (Bitdefender: Privacy > Video & Audio Protection > Webcam Protection). The sound records on")));
        Assert.That(dark.Concern, Is.EqualTo(new CameraConcern(false, CameraTrack.DarkShort, "The camera's picture has been black for 10 seconds: is its lens covered or folded?")));
        Assert.That(dark.Dark, Is.True);
        Assert.That(_log.Messages, Does.Contain($"The camera \"{LaptopCameras.Obsbot.Name}\" is recording its picture again"));
        Assert.That(_track.State.Frames, Is.EqualTo(120));
    }

    /// <summary>A camera not connected at the start is said, and looked for every 5 seconds; there is no stand-in camera.</summary>
    [Test]
    public async Task A_camera_not_connected_is_looked_for_every_5_seconds()
    {
        _cameras.Connected = [LaptopCameras.Integrated];
        _track = new CameraTrack(Start(), _clock, _cameras, _starter, _time, _log);
        _track.Begin();
        await WaitAsync(() => _track.State.Concern is not null);
        var concern = _track.State.Concern;

        _cameras.Connected = LaptopCameras.All;
        var moved = await _time.AdvanceUntilAsync(() => _starter.Played.Count == 1, Step);

        Assert.That(concern, Is.EqualTo(new CameraConcern(true, "CAMERA NOT CONNECTED",
            "The camera \"OBSBOT\" is not connected, so its picture is not recording; ClassScribe looks for it every 5 seconds. The sound records on")));
        Assert.That(moved, Is.GreaterThanOrEqualTo(CameraTrack.FindEvery));
        await WaitAsync(() => _starter.Played[0].Job is not null);
        Assert.That(_starter.Played[0].Job!.CameraId, Is.EqualTo(LaptopCameras.Obsbot.Id), "never the Integrated Camera in its place");
    }

    /// <summary>Giving the picture up, as for a nearly full disk, stops the camera process and starts no other; the stop then has nothing to wait for.</summary>
    [Test]
    public async Task Giving_the_picture_up_stops_the_process_and_starts_no_other()
    {
        var played = await StartAsync();

        _track!.GiveUp("Little room is left on the disk");
        await WaitAsync(() => played.Process.ExitCode is not null);
        _time.Advance(TimeSpan.FromMinutes(1));
        await _track.StopAsync(_clock.Now(), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.That(played.StopReceived, Is.True);
        Assert.That(_starter.Played, Has.Count.EqualTo(1));
        Assert.That(_track.State.GivenUp, Is.True);
        Assert.That(_track.State.Concern, Is.EqualTo(new CameraConcern(true, CameraTrack.GivenUpShort, "Little room is left on the disk")));
        Assert.That(_log.Problems.Count(problem => problem == "Little room is left on the disk"), Is.EqualTo(1), "the track says why, once");
    }

    /// <summary>A lesson of the OBSBOT at 720p into a recording, from <paramref name="start"/> on the clock, with <paramref name="offsets"/>.</summary>
    private static CameraTrackStart Start(IReadOnlyDictionary<string, int>? offsets = null, TimeSpan start = default) =>
        new(new CameraChoice("OBSBOT", "720p"), @"C:\Tajwid\2026-10-22", "Tajwid_2026-10-22_14-00-00Z", start, offsets);

    /// <summary>Starts the track and waits for its first camera process to read its job.</summary>
    private async Task<PlayedCameraProcess> StartAsync(IReadOnlyDictionary<string, int>? offsets = null, TimeSpan start = default)
    {
        _track = new CameraTrack(Start(offsets, start), _clock, _cameras, _starter, _time, _log);
        _track.Begin();
        await WaitAsync(() => _starter.Played.Count == 1 && _starter.Played[0].Job is not null);
        return _starter.Played[0];
    }

    /// <summary>Waits for <paramref name="condition"/> on the real clock, without moving the manual one, at most ten seconds.</summary>
    private static async Task WaitAsync(Func<bool> condition)
    {
        var deadline = Environment.TickCount64 + 10_000;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline) throw new TimeoutException("The expected condition was not met within 10 seconds.");
            await Task.Delay(5);
        }
    }
}
