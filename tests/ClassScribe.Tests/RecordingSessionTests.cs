using ClassScribe.Audio;
using ClassScribe.Settings;
using Microsoft.Extensions.Options;

namespace ClassScribe.Tests;

/// <summary>The recording session, driven by a fake capture device.</summary>
[TestFixture]
public class RecordingSessionTests
{
    private static readonly AudioFormat Float48KStereo = new(48000, 2, 32, true);
    private static readonly AudioFormat Float44KStereo = new(44100, 2, 32, true);
    private static readonly AudioFormat Pcm16KMono = new(16000, 1, 16, false);

    private TempFolder _folder = null!;
    private FakeCaptureProvider _provider = null!;
    private ManualTimeProvider _time = null!;
    private RecordingLogger<RecordingSession> _log = null!;

    /// <summary>Creates a temporary folder, the simulated devices, a clock moved by hand and a log to inspect.</summary>
    [SetUp]
    public void SetUp()
    {
        _folder = new TempFolder();
        _provider = new FakeCaptureProvider();
        _time = new ManualTimeProvider(new DateTimeOffset(2026, 10, 11, 14, 0, 0, TimeSpan.Zero));
        _log = new RecordingLogger<RecordingSession>();
    }

    /// <summary>Deletes the temporary folder. That fails if a test left a file open, which is the point.</summary>
    [TearDown]
    public void TearDown() => _folder.Dispose();

    /// <summary>Creates the session under test.</summary>
    private RecordingSession CreateSession(RecordingOptions? options = null) =>
        new(_provider, Options.Create(options ?? new RecordingOptions()), _time, _log);

    [Test]
    public void Captured_audio_is_stored_as_mono_16_bit()
    {
        var capture = _provider.Enqueue(new FakeAudioCapture(Float48KStereo));
        using var session = CreateSession();

        session.Start(_folder.Path);
        capture.Deliver(TestAudio.Floats(0.25f, 0.25f, -0.25f, -0.25f));
        var result = session.Stop();

        Assert.That(capture.Started, Is.True);
        Assert.That(capture.Disposed, Is.True);
        Assert.That(result.SegmentPaths, Has.Count.EqualTo(1));
        Assert.That(Path.GetFileName(result.SegmentPaths[0]), Is.EqualTo("recording-001.wav"));

        var wav = TestAudio.ReadWav(result.SegmentPaths[0]);
        Assert.That(wav.SampleRate, Is.EqualTo(48000));
        Assert.That(wav.Samples, Is.EqualTo(new short[] { 8192, -8192 }));
        Assert.That(result.Duration.TotalSeconds, Is.EqualTo(2 / 48000.0).Within(0.000001));
    }

    [Test]
    public void Stopping_without_any_audio_leaves_no_segment_behind()
    {
        _provider.Enqueue(new FakeAudioCapture(Float48KStereo));
        using var session = CreateSession();

        session.Start(_folder.Path);
        var result = session.Stop();

        Assert.That(result.SegmentPaths, Is.Empty);
        Assert.That(Directory.GetFiles(_folder.Path), Is.Empty);
    }

    [Test]
    public void Audio_the_device_still_holds_when_stopping_is_kept()
    {
        var capture = _provider.Enqueue(new FakeAudioCapture(Pcm16KMono) { FinalAudio = TestAudio.Shorts(3, 4) });
        using var session = CreateSession();

        session.Start(_folder.Path);
        capture.Deliver(TestAudio.Shorts(1, 2));
        var result = session.Stop();

        Assert.That(TestAudio.ReadWav(result.SegmentPaths[0]).Samples, Is.EqualTo(new short[] { 1, 2, 3, 4 }));
    }

    [Test]
    public async Task A_new_default_device_is_followed_into_a_new_segment()
    {
        var speakers = _provider.Enqueue(new FakeAudioCapture(Float48KStereo, "Speakers") { FinalAudio = TestAudio.Floats(0.125f, 0.125f) });
        using var session = CreateSession();

        session.Start(_folder.Path);
        speakers.Deliver(TestAudio.Floats(0.25f, 0.25f));
        var headphones = _provider.SwitchDefaultDevice(new FakeAudioCapture(Float44KStereo, "Headphones"));
        await Wait.UntilAsync(() => headphones.Started);
        speakers.Deliver(TestAudio.Floats(0.5f, 0.5f));      // late audio from the old device must be ignored
        headphones.Deliver(TestAudio.Floats(-0.25f, -0.25f));
        var result = session.Stop();

        Assert.That(speakers.Disposed, Is.True);
        Assert.That(result.SegmentPaths, Has.Count.EqualTo(2));
        var first = TestAudio.ReadWav(result.SegmentPaths[0]);
        var second = TestAudio.ReadWav(result.SegmentPaths[1]);
        Assert.That(first.SampleRate, Is.EqualTo(48000));
        Assert.That(first.Samples, Is.EqualTo(new short[] { 8192, 4096 }), "what the old device still held belongs to its segment");
        Assert.That(second.SampleRate, Is.EqualTo(44100));
        Assert.That(second.Samples, Is.EqualTo(new short[] { -8192 }));
        Assert.That(session.GetStatus().DeviceName, Is.EqualTo("Headphones"));
        Assert.That(_log.Problems, Is.Empty, "following a switch is routine, not a failure to report");
    }

    [Test]
    public async Task A_switch_that_windows_never_announced_is_still_followed()
    {
        var speakers = _provider.Enqueue(new FakeAudioCapture(Float48KStereo, "Speakers"));
        using var session = CreateSession();

        session.Start(_folder.Path);
        speakers.Deliver(TestAudio.Floats(0.25f, 0.25f));
        var headphones = _provider.SwitchDefaultDevice(new FakeAudioCapture(Float44KStereo, "Headphones"), announce: false);
        _time.Advance(TimeSpan.FromSeconds(4));      // the session looks up the default device by itself every few seconds
        await Wait.UntilAsync(() => headphones.Started);
        headphones.Deliver(TestAudio.Floats(-0.25f, -0.25f));
        var result = session.Stop();

        Assert.That(result.SegmentPaths, Has.Count.EqualTo(2));
        Assert.That(TestAudio.ReadWav(result.SegmentPaths[1]).Samples, Is.EqualTo(new short[] { -8192 }));
    }

    [Test]
    public async Task A_device_notification_that_changes_nothing_is_ignored()
    {
        var capture = _provider.Enqueue(new FakeAudioCapture(Pcm16KMono));
        var spare = _provider.Enqueue(new FakeAudioCapture(Pcm16KMono));
        using var session = CreateSession();

        session.Start(_folder.Path);
        capture.Deliver(TestAudio.Shorts(1, 2));
        _provider.RaiseDefaultDeviceChanged();
        await Wait.UntilAsync(() => _provider.DefaultDeviceQueries > 0);
        capture.Deliver(TestAudio.Shorts(3));
        var result = session.Stop();

        Assert.That(spare.Started, Is.False);
        Assert.That(result.SegmentPaths, Has.Count.EqualTo(1));
        Assert.That(TestAudio.ReadWav(result.SegmentPaths[0]).Samples, Is.EqualTo(new short[] { 1, 2, 3 }));
    }

    [Test]
    public async Task A_running_capture_is_kept_when_windows_reports_no_default_device()
    {
        var capture = _provider.Enqueue(new FakeAudioCapture(Pcm16KMono));
        using var session = CreateSession();

        session.Start(_folder.Path);
        capture.Deliver(TestAudio.Shorts(1, 2));
        _provider.RemoveDefaultDevice();
        await Wait.UntilAsync(() => _provider.DefaultDeviceQueries > 0);
        capture.Deliver(TestAudio.Shorts(3));     // if the device really is gone, the capture says so itself
        var result = session.Stop();

        Assert.That(_provider.FailedCreations, Is.EqualTo(0));
        Assert.That(TestAudio.ReadWav(result.SegmentPaths[0]).Samples, Is.EqualTo(new short[] { 1, 2, 3 }));
    }

    [Test]
    public async Task One_device_change_reported_several_ways_costs_one_restart()
    {
        var speakers = _provider.Enqueue(new FakeAudioCapture(Float48KStereo, "Speakers"));
        using var session = CreateSession();

        session.Start(_folder.Path);
        speakers.Deliver(TestAudio.Floats(0.25f, 0.25f));
        var headphones = _provider.SwitchDefaultDevice(new FakeAudioCapture(Float44KStereo, "Headphones"));
        var spare = _provider.Enqueue(new FakeAudioCapture(Float44KStereo, "Headphones"));
        _provider.RaiseDefaultDeviceChanged();                           // Windows repeats the notification
        speakers.Fail(new IOException("The device was unplugged."));     // and the old device fails at the same moment
        await Wait.UntilAsync(() => headphones.Started);
        _provider.RaiseDefaultDeviceChanged();
        await Task.Delay(300);                                           // time for a needless extra restart to show itself
        headphones.Deliver(TestAudio.Floats(-0.25f, -0.25f));
        var result = session.Stop();

        Assert.That(spare.Started, Is.False);
        Assert.That(_provider.Created, Has.Count.EqualTo(2));
        Assert.That(result.SegmentPaths, Has.Count.EqualTo(2));
        Assert.That(TestAudio.ReadWav(result.SegmentPaths[1]).Samples, Is.EqualTo(new short[] { -8192 }));
    }

    [Test]
    public async Task A_failed_capture_is_restarted()
    {
        var first = _provider.Enqueue(new FakeAudioCapture(Float48KStereo));
        var second = _provider.Enqueue(new FakeAudioCapture(Float48KStereo));
        using var session = CreateSession();

        session.Start(_folder.Path);
        first.Deliver(TestAudio.Floats(0.25f, 0.25f));
        first.Fail(new IOException("The device was unplugged."));
        await Wait.UntilAsync(() => second.Started);
        second.Deliver(TestAudio.Floats(0.25f, 0.25f, 0.25f, 0.25f));
        var result = session.Stop();

        Assert.That(first.Disposed, Is.True);
        Assert.That(result.SegmentPaths, Has.Count.EqualTo(2));
        Assert.That(TestAudio.ReadWav(result.SegmentPaths[1]).Samples, Is.EqualTo(new short[] { 8192, 8192 }));
        Assert.That(_log.Problems, Has.Count.EqualTo(1), "the failure is reported once");
    }

    [Test]
    public async Task A_capture_that_cannot_be_reopened_is_tried_again()
    {
        var first = _provider.Enqueue(new FakeAudioCapture(Pcm16KMono));
        using var session = CreateSession();

        session.Start(_folder.Path);
        first.Deliver(TestAudio.Shorts(1));
        first.Fail(new IOException("The device was unplugged."));
        await Wait.UntilAsync(() => _provider.FailedCreations > 0);      // no output device at all right now
        var second = _provider.Enqueue(new FakeAudioCapture(Pcm16KMono));
        await Wait.UntilAsync(() => second.Started, timeoutSeconds: 15);
        second.Deliver(TestAudio.Shorts(2));
        var result = session.Stop();

        Assert.That(result.SegmentPaths, Has.Count.EqualTo(2));
        Assert.That(TestAudio.ReadWav(result.SegmentPaths[1]).Samples, Is.EqualTo(new short[] { 2 }));
    }

    [Test]
    public async Task A_capture_that_keeps_failing_is_not_restarted_in_a_tight_loop()
    {
        var first = _provider.Enqueue(new FakeAudioCapture(Pcm16KMono));
        var second = _provider.Enqueue(new FakeAudioCapture(Pcm16KMono));
        var third = _provider.Enqueue(new FakeAudioCapture(Pcm16KMono));
        using var session = CreateSession();

        session.Start(_folder.Path);
        first.Fail(new IOException("Driver trouble."));
        await Wait.UntilAsync(() => second.Started);                     // the first failure is answered at once
        second.Fail(new IOException("Driver trouble."));
        await Task.Delay(500);

        Assert.That(third.Started, Is.False, "a second failure right away has to wait");
        await Wait.UntilAsync(() => third.Started, timeoutSeconds: 15);
        session.Stop();
    }

    [Test]
    public void A_large_recording_rolls_over_to_a_new_segment()
    {
        var capture = _provider.Enqueue(new FakeAudioCapture(Pcm16KMono));
        using var session = CreateSession(new RecordingOptions { MaxSegmentMegabytes = 1 });
        var chunk = TestAudio.Shorts(Enumerable.Repeat((short)1000, 300_000).ToArray());   // 600 KB

        session.Start(_folder.Path);
        capture.Deliver(chunk);
        capture.Deliver(chunk);   // crosses 1 MB, so the next chunk belongs to a new file
        capture.Deliver(chunk);
        var result = session.Stop();

        Assert.That(result.SegmentPaths, Has.Count.EqualTo(2));
        Assert.That(new FileInfo(result.SegmentPaths[0]).Length, Is.EqualTo(44L + 1_200_000));
        Assert.That(new FileInfo(result.SegmentPaths[1]).Length, Is.EqualTo(44L + 600_000));
        Assert.That(result.Duration.TotalSeconds, Is.EqualTo(900_000 / 16000.0).Within(0.001));
    }

    [Test]
    public void The_file_on_disk_stays_valid_while_recording()
    {
        var capture = _provider.Enqueue(new FakeAudioCapture(Pcm16KMono));
        using var session = CreateSession(new RecordingOptions { FlushSeconds = 5 });

        session.Start(_folder.Path);
        capture.Deliver(TestAudio.Shorts(1, 2));
        _time.Advance(TimeSpan.FromSeconds(6));
        capture.Deliver(TestAudio.Shorts(3));

        // Read the file as a crash would leave it: the session is still running.
        var onDisk = TestAudio.ReadWav(_folder.File("recording-001.wav"));

        Assert.That(onDisk.Samples, Is.EqualTo(new short[] { 1, 2, 3 }));
    }

    [Test]
    public void A_single_disk_error_costs_a_moment_of_audio_but_not_the_capture()
    {
        var capture = _provider.Enqueue(new FakeAudioCapture(Pcm16KMono));
        var classFolder = Path.Combine(_folder.Path, "class");
        using var session = CreateSession();

        session.Start(classFolder);
        Directory.Delete(classFolder);                  // the drive goes away
        capture.Deliver(TestAudio.Shorts(1, 2));        // cannot be written: reported in the log, not thrown at the device
        Directory.CreateDirectory(classFolder);         // the drive is back
        capture.Deliver(TestAudio.Shorts(3, 4));
        var result = session.Stop();

        Assert.That(_provider.Created, Has.Count.EqualTo(1), "the device was fine, so it is not reopened");
        Assert.That(result.SegmentPaths, Has.Count.EqualTo(1));
        Assert.That(TestAudio.ReadWav(result.SegmentPaths[0]).Samples, Is.EqualTo(new short[] { 3, 4 }));
        Assert.That(_log.Problems, Has.Count.EqualTo(1), "the lost audio is reported");
    }

    [Test]
    public void A_disk_that_keeps_failing_is_given_a_pause_before_the_next_file()
    {
        var capture = _provider.Enqueue(new FakeAudioCapture(Pcm16KMono));
        var classFolder = Path.Combine(_folder.Path, "class");
        using var session = CreateSession();

        session.Start(classFolder);
        Directory.Delete(classFolder);
        capture.Deliver(TestAudio.Shorts(1));           // first failure: tried again at once
        capture.Deliver(TestAudio.Shorts(2));           // second failure in a row: now it waits
        Directory.CreateDirectory(classFolder);
        capture.Deliver(TestAudio.Shorts(3));           // inside the pause, so this is dropped
        _time.Advance(TimeSpan.FromSeconds(31));
        capture.Deliver(TestAudio.Shorts(4, 5));
        var result = session.Stop();

        Assert.That(result.SegmentPaths, Has.Count.EqualTo(1));
        Assert.That(TestAudio.ReadWav(result.SegmentPaths[0]).Samples, Is.EqualTo(new short[] { 4, 5 }));
    }

    [Test]
    public void A_device_that_will_not_shut_down_cleanly_does_not_cost_the_recording()
    {
        var capture = _provider.Enqueue(new FakeAudioCapture(Pcm16KMono) { DisposeFailure = new InvalidOperationException("Driver trouble.") });
        using var session = CreateSession();

        session.Start(_folder.Path);
        capture.Deliver(TestAudio.Shorts(1, 2, 3));
        var result = session.Stop();

        Assert.That(result.SegmentPaths, Has.Count.EqualTo(1));
        Assert.That(TestAudio.ReadWav(result.SegmentPaths[0]).Samples, Is.EqualTo(new short[] { 1, 2, 3 }));
        Assert.That(_log.Problems, Has.Count.EqualTo(1), "the trouble is reported even though the recording is fine");
    }

    [Test]
    public void A_file_that_is_already_there_is_left_alone()
    {
        File.WriteAllText(_folder.File("recording-001.wav"), "precious");
        var capture = _provider.Enqueue(new FakeAudioCapture(Pcm16KMono));
        using var session = CreateSession();

        session.Start(_folder.Path);
        capture.Deliver(TestAudio.Shorts(1, 2));
        var result = session.Stop();

        Assert.That(Path.GetFileName(result.SegmentPaths.Single()), Is.EqualTo("recording-002.wav"));
        Assert.That(File.ReadAllText(_folder.File("recording-001.wav")), Is.EqualTo("precious"));
    }

    [Test]
    public void Status_reports_how_long_ago_sound_was_heard()
    {
        var capture = _provider.Enqueue(new FakeAudioCapture(Pcm16KMono));
        using var session = CreateSession();

        session.Start(_folder.Path);
        capture.Deliver(TestAudio.Shorts(0, 0, 0));
        _time.Advance(TimeSpan.FromSeconds(90));
        var whileSilent = session.GetStatus();
        capture.Deliver(TestAudio.Shorts(0, 5000, 0));
        var afterSound = session.GetStatus();

        Assert.That(whileSilent.SinceLastSound.TotalSeconds, Is.EqualTo(90.0).Within(0.001));
        Assert.That(afterSound.SinceLastSound.TotalSeconds, Is.EqualTo(0.0).Within(0.001));
        Assert.That(afterSound.Captured.TotalSeconds, Is.EqualTo(6 / 16000.0).Within(0.000001));
    }

    [Test]
    public void A_device_with_an_unreadable_format_is_refused()
    {
        var capture = _provider.Enqueue(new FakeAudioCapture(new AudioFormat(8000, 1, 8, false)));
        using var session = CreateSession();

        Assert.Throws<NotSupportedException>(() => session.Start(_folder.Path));
        Assert.That(capture.Disposed, Is.True);
        Assert.That(Directory.GetFiles(_folder.Path), Is.Empty);
    }

    [Test]
    public void Stop_can_be_called_twice()
    {
        var capture = _provider.Enqueue(new FakeAudioCapture(Pcm16KMono));
        using var session = CreateSession();

        session.Start(_folder.Path);
        capture.Deliver(TestAudio.Shorts(1));
        var first = session.Stop();
        var second = session.Stop();

        Assert.That(second.SegmentPaths, Is.EqualTo(first.SegmentPaths));
    }

    /// <summary>Audio that arrives while the recording is paused is not kept; the file carries on after the pause.</summary>
    [Test]
    public void Audio_that_arrives_while_paused_is_not_recorded()
    {
        var capture = _provider.Enqueue(new FakeAudioCapture(Pcm16KMono));
        using var session = CreateSession();

        session.Start(_folder.Path);
        capture.Deliver(TestAudio.Shorts(1, 2));
        session.Pause();
        var paused = session.GetStatus();
        capture.Deliver(TestAudio.Shorts(3, 4));
        session.Resume();
        capture.Deliver(TestAudio.Shorts(5, 6));
        var result = session.Stop();

        Assert.That(paused.Paused, Is.True);
        Assert.That(session.GetStatus().Paused, Is.False);
        Assert.That(result.SegmentPaths, Has.Count.EqualTo(1));
        Assert.That(TestAudio.ReadWav(result.SegmentPaths[0]).Samples, Is.EqualTo(new short[] { 1, 2, 5, 6 }));
    }

    /// <summary>Pausing brings the file on disk up to date, so a crash during a long break loses nothing before it.</summary>
    [Test]
    public void Pausing_brings_the_file_on_disk_up_to_date()
    {
        var capture = _provider.Enqueue(new FakeAudioCapture(Pcm16KMono));
        using var session = CreateSession();

        session.Start(_folder.Path);
        capture.Deliver(TestAudio.Shorts(1, 2));   // the clock has not moved, so no save has been due yet
        var path = Directory.GetFiles(_folder.Path).Single();
        var before = TestAudio.ReadWav(path).Samples;
        session.Pause();
        var after = TestAudio.ReadWav(path).Samples;

        Assert.That(before, Is.Empty);
        Assert.That(after, Is.EqualTo(new short[] { 1, 2 }));
    }

    /// <summary>After a break the silence clock starts afresh, so resuming does not set off a warning about the break itself.</summary>
    [Test]
    public void Resuming_restarts_the_silence_clock()
    {
        var capture = _provider.Enqueue(new FakeAudioCapture(Pcm16KMono));
        using var session = CreateSession();

        session.Start(_folder.Path);
        capture.Deliver(TestAudio.Shorts(0, 5000));
        session.Pause();
        _time.Advance(TimeSpan.FromMinutes(15));
        session.Resume();

        Assert.That(session.GetStatus().SinceLastSound, Is.EqualTo(TimeSpan.Zero));
    }
}
