using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using ClassScribe.Audio;
using ClassScribe.Settings;
using ClassScribe.Storage;
using Microsoft.Extensions.Logging.Abstractions;
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
    private RecordingFiles _files = null!;

    /// <summary>Creates a temporary folder, a recording in it, the simulated devices, a clock moved by hand and a log to inspect.</summary>
    [SetUp]
    public void SetUp()
    {
        _folder = new TempFolder();
        _files = new RecordingFiles(_folder.Path, "Class_2026-10-11_14-00-00Z");
        _provider = new FakeCaptureProvider();
        _time = new ManualTimeProvider(new DateTimeOffset(2026, 10, 11, 14, 0, 0, TimeSpan.Zero));
        _log = new RecordingLogger<RecordingSession>();
    }

    /// <summary>Deletes the temporary folder. That fails if a test left a file open, which is the point.</summary>
    [TearDown]
    public void TearDown() => _folder.Dispose();

    /// <summary>Creates the session under test, on the test's manual clock unless <paramref name="time"/> gives another.</summary>
    private RecordingSession CreateSession(RecordingOptions? options = null, TimeProvider? time = null)
    {
        var settings = Options.Create(options ?? new RecordingOptions());
        time ??= _time;
        return new RecordingSession(_provider, new CaptureChooser(time, new SourceChoice(settings)), settings, time, _log);
    }

    /// <summary>Moves the clock on a second and waits for the device check that this sets off to begin.</summary>
    private async Task TickAsync()
    {
        var before = _provider.DeviceQueries;
        _time.Advance(TimeSpan.FromSeconds(1));
        await Wait.UntilAsync(() => _provider.DeviceQueries > before);
    }

    /// <summary>Moves the clock on a second at a time, device check by device check, until the condition holds.</summary>
    private async Task TickUntilAsync(Func<bool> condition, int maxTicks = 8)
    {
        for (var tick = 0; tick < maxTicks && !condition(); tick++)
        {
            await TickAsync();
            await Task.Delay(50);   // lets the check finish before the clock moves again
        }

        await Wait.UntilAsync(condition);
    }

    /// <summary>Mono 16-bit audio holding <paramref name="count"/> samples of one value.</summary>
    private static byte[] Samples(int count, short value) => TestAudio.Shorts(Enumerable.Repeat(value, count).ToArray());

    /// <summary>
    /// Makes the disk look full to the file being written: another handle locks everything past what is on
    /// disk now. What is there stays readable, and every write beyond it fails, as it would on a full disk.
    /// Disposing the returned stream frees the space again.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static FileStream FillDisk(string path)
    {
        var other = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        other.Lock(other.Length, int.MaxValue);
        return other;
    }

    /// <summary>
    /// Makes the whole file unwritable to the session, its header included: another handle locks all of it.
    /// Each write reaches Windows at once, so it takes this, not just a full disk, to make a save of the header
    /// fail. Disposing the returned stream lifts the lock.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static FileStream LockWholeFile(string path)
    {
        var other = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        other.Lock(0, int.MaxValue);
        return other;
    }

    /// <summary>
    /// The test's manual clock, which also runs <see cref="OnRead"/> each time the session reads its steady count.
    /// The session reads that count right after it has made a new file and before the first audio goes into it,
    /// so a test can change the disk at that very moment, which a lock taken from outside cannot do: there is no
    /// file to lock before then, and the audio is on disk at once after.
    /// </summary>
    private sealed class HookedClock(ManualTimeProvider clock) : TimeProvider
    {
        /// <summary>Runs before each reading of the steady count.</summary>
        public Action? OnRead { get; set; }

        /// <inheritdoc />
        public override long TimestampFrequency => clock.TimestampFrequency;

        /// <inheritdoc />
        public override TimeZoneInfo LocalTimeZone => clock.LocalTimeZone;

        /// <summary>Runs <see cref="OnRead"/>, then reads the manual clock's steady count.</summary>
        public override long GetTimestamp()
        {
            OnRead?.Invoke();
            return clock.GetTimestamp();
        }

        /// <inheritdoc />
        public override DateTimeOffset GetUtcNow() => clock.GetUtcNow();

        /// <inheritdoc />
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            clock.CreateTimer(callback, state, dueTime, period);
    }

    /// <summary>How many times a capture that ended again right away was held back rather than replaced at once.</summary>
    private int HeldBack() => _log.Messages.Count(message => message.Contains("ended again"));

    /// <summary>
    /// Asks the session to stop while Windows holds up a device change, and moves the clock on past the time
    /// Stop waits for that change. Returns what Stop handed back.
    /// </summary>
    private async Task<RecordingResult> StopWhileWindowsHoldsUpAsync(RecordingSession session)
    {
        var stopping = Task.Run(session.Stop);
        await Wait.UntilAsync(() => _time.ActiveTimers == 2);       // Stop waits, with its patience on the session's clock
        _time.Advance(TimeSpan.FromMinutes(1));
        return await stopping.WaitAsync(TimeSpan.FromSeconds(10));
    }

    /// <summary>Device audio is stored as mono 16-bit samples in the recording's first file.</summary>
    [Test]
    public void Captured_audio_is_stored_as_mono_16_bit()
    {
        var capture = _provider.Enqueue(new FakeAudioCapture(Float48KStereo));
        using var session = CreateSession();

        session.Start(_files);
        capture.Deliver(TestAudio.Floats(0.25f, 0.25f, -0.25f, -0.25f));
        var result = session.Stop();

        Assert.That(capture.Started, Is.True);
        Assert.That(capture.Disposed, Is.True);
        Assert.That(result.SegmentPaths, Has.Count.EqualTo(1));
        Assert.That(Path.GetFileName(result.SegmentPaths[0]), Is.EqualTo(Path.GetFileName(_files.Segment(1))));

        var wav = TestAudio.ReadWav(result.SegmentPaths[0]);
        Assert.That(wav.SampleRate, Is.EqualTo(48000));
        Assert.That(wav.Samples, Is.EqualTo(new short[] { 8192, -8192 }));
        Assert.That(result.Duration.TotalSeconds, Is.EqualTo(2 / 48000.0).Within(0.000001));
    }

    /// <summary>A recording that never received audio leaves no file behind.</summary>
    [Test]
    public void Stopping_without_any_audio_leaves_no_segment_behind()
    {
        _provider.Enqueue(new FakeAudioCapture(Float48KStereo));
        using var session = CreateSession();

        session.Start(_files);
        var result = session.Stop();

        Assert.That(result.SegmentPaths, Is.Empty);
        Assert.That(Directory.GetFiles(_folder.Path), Is.Empty);
    }

    /// <summary>Audio the device still holds when the recording stops is written before the file is closed.</summary>
    [Test]
    public void Audio_the_device_still_holds_when_stopping_is_kept()
    {
        var capture = _provider.Enqueue(new FakeAudioCapture(Pcm16KMono) { FinalAudio = TestAudio.Shorts(3, 4) });
        using var session = CreateSession();

        session.Start(_files);
        capture.Deliver(TestAudio.Shorts(1, 2));
        var result = session.Stop();

        Assert.That(TestAudio.ReadWav(result.SegmentPaths[0]).Samples, Is.EqualTo(new short[] { 1, 2, 3, 4 }));
    }

    /// <summary>When the sound moves to another device (headphones plugged in, say), the recording follows it into a new segment.</summary>
    [Test]
    public async Task Sound_moving_to_another_device_is_followed_into_a_new_segment()
    {
        var speakers = _provider.Enqueue(new FakeAudioCapture(Float48KStereo, "Speakers") { FinalAudio = TestAudio.Floats(0.125f, 0.125f) });
        using var session = CreateSession();

        session.Start(_files);
        speakers.Deliver(TestAudio.Floats(0.25f, 0.25f));
        var headphones = _provider.SwitchDefaultDevice(new FakeAudioCapture(Float44KStereo, "Headphones"), level: 0.2f);
        await TickUntilAsync(() => headphones.Started);
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
        Assert.That(_log.Messages, Has.Some.Contains("Sound is playing on \"Headphones\""));
        Assert.That(_log.Problems, Is.Empty, "following the sound is routine, not a failure to report");
    }

    /// <summary>Sound on a device Windows never announced, such as one a browser was sent to in the per-app settings, is found by the regular check.</summary>
    [Test]
    public async Task Sound_on_a_device_windows_never_announced_is_still_followed()
    {
        var speakers = _provider.Enqueue(new FakeAudioCapture(Float48KStereo, "Speakers"));
        using var session = CreateSession();

        session.Start(_files);
        var headphones = _provider.Enqueue(new FakeAudioCapture(Float44KStereo, "Headphones"), level: 0.2f);   // not the default: nothing is announced
        await TickUntilAsync(() => headphones.Started);
        headphones.Deliver(TestAudio.Floats(-0.25f, -0.25f));
        var result = session.Stop();

        Assert.That(speakers.Disposed, Is.True);
        Assert.That(result.SegmentPaths, Has.Count.EqualTo(1), "the silent device left no file behind");
        Assert.That(TestAudio.ReadWav(result.SegmentPaths[0]).Samples, Is.EqualTo(new short[] { -8192 }));
    }

    /// <summary>The device check runs every second, not just once or at another pace, and it ends with the recording.</summary>
    [Test]
    public async Task The_device_check_runs_every_second_until_the_recording_stops()
    {
        _provider.Enqueue(new FakeAudioCapture(Pcm16KMono));
        using var session = CreateSession();

        session.Start(_files);
        var atStart = _provider.DeviceQueries;
        _time.Advance(TimeSpan.FromMilliseconds(900));
        await Task.Delay(100);                                // a check set off too early would show up meanwhile
        var beforeASecond = _provider.DeviceQueries;
        _time.Advance(TimeSpan.FromMilliseconds(100));
        await Wait.UntilAsync(() => _provider.DeviceQueries == atStart + 1);
        for (var tick = 0; tick < 3; tick++) await TickAsync();
        session.Stop();

        Assert.That(beforeASecond, Is.EqualTo(atStart), "no check before a second has passed");
        Assert.That(_provider.DeviceQueries, Is.EqualTo(atStart + 4), "one check a second, again and again");
        Assert.That(_time.ActiveTimers, Is.Zero, "the check ends with the recording");
    }

    /// <summary>A device that is playing is kept, however loud another one is.</summary>
    [Test]
    public async Task A_playing_device_is_kept_when_sound_starts_elsewhere()
    {
        var speakers = _provider.Enqueue(new FakeAudioCapture(Pcm16KMono, "Speakers"), level: 0.3f);
        var headphones = _provider.Enqueue(new FakeAudioCapture(Pcm16KMono, "Headphones"));
        using var session = CreateSession();

        session.Start(_files);
        _provider.SetLevel("Headphones", 0.9f);              // something loud starts on the headphones
        for (var second = 0; second < 5; second++)
        {
            speakers.Deliver(TestAudio.Shorts(0, 5000));   // while the class keeps playing on the speakers
            await TickAsync();
            await Task.Delay(50);
        }

        var result = session.Stop();

        Assert.That(headphones.Started, Is.False);
        Assert.That(result.SegmentPaths, Has.Count.EqualTo(1));
        Assert.That(session.GetStatus().DeviceName, Is.EqualTo("Speakers"));
    }

    /// <summary>A short sound elsewhere, such as a notification, does not move the recording.</summary>
    [Test]
    public async Task A_short_sound_elsewhere_does_not_move_the_recording()
    {
        _provider.Enqueue(new FakeAudioCapture(Pcm16KMono, "Speakers"));
        var headphones = _provider.Enqueue(new FakeAudioCapture(Pcm16KMono, "Headphones"));
        using var session = CreateSession();

        session.Start(_files);
        _provider.SetLevel("Headphones", 0.5f);              // a notification sounds on the headphones
        await TickAsync();                                   // it is seen once
        await Task.Delay(50);
        _provider.SetLevel("Headphones", 0);                 // and is over by the next look
        for (var second = 0; second < 4; second++)
        {
            await TickAsync();
            await Task.Delay(50);
        }

        session.Stop();

        Assert.That(headphones.Started, Is.False);
    }

    /// <summary>A device chosen in the settings is recorded whatever is playing elsewhere.</summary>
    [Test]
    public void A_chosen_device_is_recorded_whatever_is_playing()
    {
        var speakers = _provider.Enqueue(new FakeAudioCapture(Pcm16KMono, "Speakers"), level: 0.9f);
        var headphones = _provider.Enqueue(new FakeAudioCapture(Pcm16KMono, "Headphones"));
        using var session = CreateSession(new RecordingOptions { Device = "headphones" });

        session.Start(_files);
        headphones.Deliver(TestAudio.Shorts(1, 2));
        var result = session.Stop();

        Assert.That(speakers.Started, Is.False);
        Assert.That(session.GetStatus().DeviceName, Is.EqualTo("Headphones"));
        Assert.That(TestAudio.ReadWav(result.SegmentPaths[0]).Samples, Is.EqualTo(new short[] { 1, 2 }));
    }

    /// <summary>A chosen device that is not connected is waited for, with a warning that names it, rather than another device being recorded.</summary>
    [Test]
    public async Task Recording_waits_for_a_chosen_device_that_is_not_connected()
    {
        var speakers = _provider.Enqueue(new FakeAudioCapture(Pcm16KMono, "Speakers"));
        using var session = CreateSession(new RecordingOptions { Device = "Pixel Buds" });

        session.Start(_files);
        var waiting = session.GetStatus();
        var buds = _provider.Enqueue(new FakeAudioCapture(Pcm16KMono, "Headphones (Pixel Buds Pro 2)"));
        await TickUntilAsync(() => buds.Started);            // found by the next look, a second on
        buds.Deliver(TestAudio.Shorts(1, 2));
        var recording = session.GetStatus();
        var result = session.Stop();

        Assert.That(waiting.WaitingFor, Is.EqualTo("the output device \"Pixel Buds\" to connect"));
        Assert.That(recording.WaitingFor, Is.Null);
        Assert.That(_log.Problems, Has.Some.Contains("Pixel Buds"));
        Assert.That(speakers.Started, Is.False);
        Assert.That(TestAudio.ReadWav(result.SegmentPaths.Single()).Samples, Is.EqualTo(new short[] { 1, 2 }));
    }

    /// <summary>
    /// While there is nothing to record, the session looks once a second and says so every thirty seconds.
    /// Each look is a single look: none of them sets off further looks of its own.
    /// </summary>
    [Test]
    public async Task While_there_is_nothing_to_record_it_looks_once_a_second_and_says_so_every_thirty()
    {
        _provider.Enqueue(new FakeAudioCapture(Pcm16KMono, "Speakers"));
        using var session = CreateSession(new RecordingOptions { Device = "Pixel Buds" });   // never connected

        session.Start(_files);
        for (var second = 1; second <= 60; second++) await TickAsync();
        await Task.Delay(2500);                              // retries running on their own would show up meanwhile

        Assert.That(_provider.DeviceQueries, Is.EqualTo(61), "one look at the start and one a second");
        Assert.That(_log.Problems.Where(problem => problem.Contains("Pixel Buds")), Has.Exactly(3).Items, "at the start, after thirty seconds and after sixty");
    }

    /// <summary>A device that cannot be opened is tried once a second, and the trouble is reported every thirty seconds rather than at every try.</summary>
    [Test]
    public async Task A_device_that_cannot_be_opened_is_tried_once_a_second_and_reported_every_thirty()
    {
        _provider.ConnectDevice("Speakers");                 // connected, but it cannot be opened
        using var session = CreateSession();

        session.Start(_files);
        for (var second = 1; second <= 60; second++) await TickAsync();
        await Wait.UntilAsync(() => _provider.FailedCreations == 61);
        var result = session.Stop();

        Assert.That(result.SegmentPaths, Is.Empty);
        Assert.That(_log.Problems, Has.Count.EqualTo(3), "at the start, after thirty seconds and after sixty");
        Assert.That(_log.Problems.Skip(1), Has.All.StartsWith("Cannot capture"));
    }

    /// <summary>
    /// The thirty seconds between reports only hold while one problem lasts. Once the session has recovered,
    /// a new failure is reported at once, however soon after the last report it comes.
    /// </summary>
    [Test]
    public async Task A_new_failure_after_the_session_recovered_is_reported_at_once()
    {
        var first = _provider.Enqueue(new FakeAudioCapture(Pcm16KMono));
        using var session = CreateSession();

        session.Start(_files);
        first.Fail(new IOException("The device was unplugged."));          // and it cannot be opened again yet
        await Wait.UntilAsync(() => _provider.FailedCreations == 1);
        var second = _provider.Enqueue(new FakeAudioCapture(Pcm16KMono));
        await TickUntilAsync(() => second.Started);                          // a second later it can: the session has recovered
        _time.Advance(TimeSpan.FromSeconds(2));                              // long enough not to count as a capture that keeps failing
        second.Fail(new IOException("The device was unplugged again."));    // a new failure, a few seconds after the first report
        await Wait.UntilAsync(() => _provider.FailedCreations == 2);
        session.Stop();

        Assert.That(_log.Problems.Where(problem => problem.StartsWith("Cannot capture")), Has.Exactly(2).Items, "each failure is reported as it begins");
    }

    /// <summary>A device that cannot be opened at the very start (Windows still setting it up, say) is tried again, as it would be later in the class.</summary>
    [Test]
    public async Task A_device_that_cannot_be_opened_at_the_start_is_tried_again()
    {
        _provider.Enqueue(new FakeAudioCapture(Pcm16KMono) { StartFailure = new COMException("The device is still being set up.", unchecked((int)0x88890004)) });
        var second = _provider.Enqueue(new FakeAudioCapture(Pcm16KMono));
        using var session = CreateSession();

        session.Start(_files);
        var waiting = session.GetStatus();
        await TickUntilAsync(() => second.Started);
        second.Deliver(TestAudio.Shorts(1, 2));
        var result = session.Stop();

        Assert.That(waiting.WaitingFor, Is.EqualTo("an output device"));
        Assert.That(_log.Problems, Has.Some.Contains("Could not start capturing"));
        Assert.That(TestAudio.ReadWav(result.SegmentPaths.Single()).Samples, Is.EqualTo(new short[] { 1, 2 }));
    }

    /// <summary>A chosen app is recorded from its process once it has sound open, and only that process.</summary>
    [Test]
    public async Task A_chosen_app_is_recorded_from_its_process_once_it_plays()
    {
        var speakers = _provider.Enqueue(new FakeAudioCapture(Pcm16KMono, "Speakers"), level: 0.5f);
        using var session = CreateSession(new RecordingOptions { App = "chrome" });

        session.Start(_files);
        var waiting = session.GetStatus();
        var chrome = _provider.AddApp(4242, "chrome", "Speakers", level: 0.3f, new FakeAudioCapture(Pcm16KMono, "chrome", "process:4242"))!;
        await TickUntilAsync(() => chrome.Started);
        chrome.Deliver(TestAudio.Shorts(7, 8));
        var result = session.Stop();

        Assert.That(waiting.WaitingFor, Is.EqualTo("\"chrome\" to play sound"));
        Assert.That(speakers.Started, Is.False, "the device itself is never recorded in app mode");
        Assert.That(session.GetStatus().DeviceName, Is.EqualTo("chrome"));
        Assert.That(TestAudio.ReadWav(result.SegmentPaths.Single()).Samples, Is.EqualTo(new short[] { 7, 8 }));
    }

    /// <summary>When the app's process goes and another process of the app has sound, the recording follows it into a new segment.</summary>
    [Test]
    public async Task A_chosen_app_is_followed_to_its_new_process()
    {
        _provider.Enqueue(new FakeAudioCapture(Pcm16KMono, "Speakers"));
        var first = _provider.AddApp(1, "chrome", "Speakers", level: 0, new FakeAudioCapture(Pcm16KMono, "chrome", "process:1"))!;
        using var session = CreateSession(new RecordingOptions { App = "chrome" });

        session.Start(_files);
        first.Deliver(TestAudio.Shorts(1));
        _provider.RemoveApp(1);
        var second = _provider.AddApp(2, "chrome", "Speakers", level: 0.2f, new FakeAudioCapture(Pcm16KMono, "chrome", "process:2"))!;
        await TickUntilAsync(() => second.Started);
        second.Deliver(TestAudio.Shorts(2));
        var result = session.Stop();

        Assert.That(first.Disposed, Is.True);
        Assert.That(result.SegmentPaths, Has.Count.EqualTo(2));
        Assert.That(TestAudio.ReadWav(result.SegmentPaths[1]).Samples, Is.EqualTo(new short[] { 2 }));
    }

    /// <summary>Windows sends device notifications that change nothing for the recording; they cost nothing.</summary>
    [Test]
    public async Task A_device_notification_that_changes_nothing_is_ignored()
    {
        var capture = _provider.Enqueue(new FakeAudioCapture(Pcm16KMono));
        var spare = _provider.Enqueue(new FakeAudioCapture(Pcm16KMono));
        using var session = CreateSession();

        session.Start(_files);
        capture.Deliver(TestAudio.Shorts(1, 2));
        var looks = _provider.DeviceQueries;
        _provider.RaiseDevicesChanged();
        await Wait.UntilAsync(() => _provider.DeviceQueries > looks);
        capture.Deliver(TestAudio.Shorts(3));
        var result = session.Stop();

        Assert.That(spare.Started, Is.False);
        Assert.That(result.SegmentPaths, Has.Count.EqualTo(1));
        Assert.That(TestAudio.ReadWav(result.SegmentPaths[0]).Samples, Is.EqualTo(new short[] { 1, 2, 3 }));
    }

    /// <summary>When Windows lists no output device at all, the running capture is left to decide for itself whether it is gone.</summary>
    [Test]
    public async Task A_running_capture_is_kept_when_windows_reports_no_output_device()
    {
        var capture = _provider.Enqueue(new FakeAudioCapture(Pcm16KMono));
        using var session = CreateSession();

        session.Start(_files);
        capture.Deliver(TestAudio.Shorts(1, 2));
        var looks = _provider.DeviceQueries;
        _provider.RemoveDevices();
        await Wait.UntilAsync(() => _provider.DeviceQueries > looks);
        capture.Deliver(TestAudio.Shorts(3));     // if the device really is gone, the capture says so itself
        var result = session.Stop();

        Assert.That(_provider.FailedCreations, Is.EqualTo(0));
        Assert.That(TestAudio.ReadWav(result.SegmentPaths[0]).Samples, Is.EqualTo(new short[] { 1, 2, 3 }));
    }

    /// <summary>A device change that Windows reports several ways, while the old device fails, still costs a single restart.</summary>
    [Test]
    public async Task One_device_change_reported_several_ways_costs_one_restart()
    {
        var speakers = _provider.Enqueue(new FakeAudioCapture(Float48KStereo, "Speakers"));
        using var session = CreateSession();

        session.Start(_files);
        speakers.Deliver(TestAudio.Floats(0.25f, 0.25f));
        var headphones = _provider.SwitchDefaultDevice(new FakeAudioCapture(Float44KStereo, "Headphones"));
        var spare = _provider.Enqueue(new FakeAudioCapture(Float44KStereo, "Headphones"));
        _provider.RaiseDevicesChanged();                                 // Windows repeats the notification
        speakers.Fail(new IOException("The device was unplugged."));     // and the old device fails at the same moment
        await Wait.UntilAsync(() => headphones.Started);
        var looks = _provider.DeviceQueries;
        _provider.RaiseDevicesChanged();
        await Wait.UntilAsync(() => _provider.DeviceQueries > looks);    // that one has been looked at; Stop waits for the look to end
        headphones.Deliver(TestAudio.Floats(-0.25f, -0.25f));
        var result = session.Stop();

        Assert.That(spare.Started, Is.False);
        Assert.That(_provider.Created, Has.Count.EqualTo(2));
        Assert.That(result.SegmentPaths, Has.Count.EqualTo(2));
        Assert.That(TestAudio.ReadWav(result.SegmentPaths[1]).Samples, Is.EqualTo(new short[] { -8192 }));
    }

    /// <summary>A capture that fails is replaced at once, its failure reported once, and the recording goes on in a new file.</summary>
    [Test]
    public async Task A_failed_capture_is_restarted()
    {
        var first = _provider.Enqueue(new FakeAudioCapture(Float48KStereo));
        var second = _provider.Enqueue(new FakeAudioCapture(Float48KStereo));
        using var session = CreateSession();

        session.Start(_files);
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

    /// <summary>When the device cannot be opened again after a failure, the next check tries again.</summary>
    [Test]
    public async Task A_capture_that_cannot_be_reopened_is_tried_again()
    {
        var first = _provider.Enqueue(new FakeAudioCapture(Pcm16KMono));
        using var session = CreateSession();

        session.Start(_files);
        first.Deliver(TestAudio.Shorts(1));
        first.Fail(new IOException("The device was unplugged."));
        await Wait.UntilAsync(() => _provider.FailedCreations > 0);      // no output device can be opened right now
        var second = _provider.Enqueue(new FakeAudioCapture(Pcm16KMono));
        await TickUntilAsync(() => second.Started);
        second.Deliver(TestAudio.Shorts(2));
        var result = session.Stop();

        Assert.That(result.SegmentPaths, Has.Count.EqualTo(2));
        Assert.That(TestAudio.ReadWav(result.SegmentPaths[1]).Samples, Is.EqualTo(new short[] { 2 }));
        Assert.That(_log.Problems.Where(problem => problem.StartsWith("Cannot capture")), Has.Exactly(1).Items);
    }

    /// <summary>A capture that ends again right after it was replaced is replaced again only two seconds on, by the session's clock, not in a tight loop.</summary>
    [Test]
    public async Task A_capture_that_keeps_failing_is_not_restarted_in_a_tight_loop()
    {
        var first = _provider.Enqueue(new FakeAudioCapture(Pcm16KMono));
        var second = _provider.Enqueue(new FakeAudioCapture(Pcm16KMono));
        var third = _provider.Enqueue(new FakeAudioCapture(Pcm16KMono));
        using var session = CreateSession();

        session.Start(_files);
        first.Fail(new IOException("Driver trouble."));
        await Wait.UntilAsync(() => second.Started);                     // the first failure is answered at once
        second.Fail(new IOException("Driver trouble."));
        await Wait.UntilAsync(() => HeldBack() == 1);                    // a second failure right away has to wait
        _time.Advance(TimeSpan.FromSeconds(1));
        await Wait.UntilAsync(() => HeldBack() == 2);                    // and is still held back a second later
        var afterOneSecond = third.Started;
        _time.Advance(TimeSpan.FromSeconds(1));
        await Wait.UntilAsync(() => third.Started);                      // two seconds on, it is replaced
        session.Stop();

        Assert.That(afterOneSecond, Is.False);
    }

    /// <summary>
    /// When Windows holds up a device change for good (a driver that stops answering), Q still stops the
    /// recording and keeps what was recorded. The capture Windows held up is let go, once, when Windows
    /// answers, whether it started or failed to; a failure then is no news for a recording that has ended.
    /// </summary>
    [TestCase(false)]
    [TestCase(true)]
    public async Task Stopping_does_not_wait_for_ever_for_a_device_change_windows_holds_up(bool startFails)
    {
        using var stuck = new ManualResetEventSlim();
        var first = _provider.Enqueue(new FakeAudioCapture(Pcm16KMono));
        var second = _provider.Enqueue(new FakeAudioCapture(Pcm16KMono)
        {
            StartGate = stuck,
            StartFailure = startFails ? new COMException("The device is no longer there.", unchecked((int)0x88890004)) : null
        });
        using var session = CreateSession();
        try
        {
            session.Start(_files);
            first.Deliver(TestAudio.Shorts(1, 2));
            first.Fail(new IOException("The device was unplugged."));
            await Wait.UntilAsync(() => second.StartEntered);           // the restart is now stuck inside Windows
            var result = await StopWhileWindowsHoldsUpAsync(session);
            stuck.Set();                                                // Windows answers at last
            await Wait.UntilAsync(() => second.Disposed);
            await Task.Delay(100);                                      // a second release would show up meanwhile

            Assert.That(TestAudio.ReadWav(result.SegmentPaths.Single()).Samples, Is.EqualTo(new short[] { 1, 2 }));
            Assert.That(_log.Problems, Has.Some.Contains("stops without waiting"));
            Assert.That(_log.Problems, Has.None.StartsWith("Cannot capture"), "nothing is tried again for a recording that has ended");
            Assert.That(second.DisposeCalls, Is.EqualTo(1), "the capture Windows held up is released once");
            Assert.That(_provider.Created, Has.Count.EqualTo(2), "nothing more is opened after the stop");
            Assert.That(Directory.GetFiles(_folder.Path), Has.Length.EqualTo(1), "and nothing more is written");
        }
        finally
        {
            stuck.Set();
        }
    }

    /// <summary>
    /// A capture that Windows hands over only after the recording was stopped without waiting for it is never
    /// started and records nothing: it is released at once, and only once.
    /// </summary>
    [Test]
    public async Task A_capture_windows_opens_after_the_stop_is_released_unused()
    {
        using var windows = new ManualResetEventSlim();
        var first = _provider.Enqueue(new FakeAudioCapture(Pcm16KMono));
        var late = _provider.Enqueue(new FakeAudioCapture(Pcm16KMono));
        using var session = CreateSession();
        try
        {
            session.Start(_files);
            first.Deliver(TestAudio.Shorts(1, 2));
            _provider.HoldNextCreation(windows);                        // Windows holds up opening the replacement
            first.Fail(new IOException("The device was unplugged."));
            await Wait.UntilAsync(() => _provider.CreationHeld);
            var result = await StopWhileWindowsHoldsUpAsync(session);
            windows.Set();                                              // Windows hands the capture over at last
            await Wait.UntilAsync(() => late.Disposed || late.Started);
            late.Deliver(TestAudio.Shorts(9, 9));                       // and audio arrives from it
            await Task.Delay(100);                                      // a second release would show up meanwhile

            Assert.That(late.Started, Is.False, "a recording that has ended starts nothing");
            Assert.That(late.DisposeCalls, Is.EqualTo(1), "the late capture is released once");
            Assert.That(Directory.GetFiles(_folder.Path), Has.Length.EqualTo(1), "and nothing more is written");
            Assert.That(TestAudio.ReadWav(result.SegmentPaths.Single()).Samples, Is.EqualTo(new short[] { 1, 2 }));
        }
        finally
        {
            windows.Set();
        }
    }

    /// <summary>
    /// When Windows holds up the release of a capture that is being replaced, because it failed or because the
    /// app moved to another process, and the recording is stopped meanwhile, the capture is released once, by
    /// the change that was releasing it, and no replacement is opened.
    /// </summary>
    [TestCase(false)]
    [TestCase(true)]
    public async Task A_capture_whose_release_windows_holds_up_is_released_once_and_not_replaced(bool appMoved)
    {
        using var windows = new ManualResetEventSlim();
        var first = _provider.AddApp(1, "chrome", "Speakers", level: 0.3f, new FakeAudioCapture(Pcm16KMono, "chrome", "process:1") { DisposeGate = windows })!;
        using var session = CreateSession(new RecordingOptions { App = "chrome" });
        try
        {
            session.Start(_files);
            first.Deliver(TestAudio.Shorts(1, 2));
            if (appMoved)
            {
                _provider.RemoveApp(1);
                _provider.AddApp(2, "chrome", "Speakers", level: 0.3f, new FakeAudioCapture(Pcm16KMono, "chrome", "process:2"));
                await TickAsync();                                      // the next look finds the app in its new process
            }
            else
            {
                _provider.AddApp(1, "chrome", "Speakers", level: 0.3f, new FakeAudioCapture(Pcm16KMono, "chrome", "process:1"));   // a replacement, ready
                first.Fail(new IOException("The app's sound went away."));
            }

            await Wait.UntilAsync(() => first.DisposeCalls == 1);       // releasing the old capture is now stuck inside Windows
            var result = await StopWhileWindowsHoldsUpAsync(session);
            windows.Set();                                              // Windows answers at last
            await Wait.UntilAsync(() => first.Disposed);
            await Task.Delay(100);                                      // a second release, or a replacement, would show up meanwhile

            Assert.That(first.DisposeCalls, Is.EqualTo(1), "the capture is released once");
            Assert.That(_provider.Created, Is.EqualTo(new[] { first }), "no replacement is opened after the stop");
            Assert.That(TestAudio.ReadWav(result.SegmentPaths.Single()).Samples, Is.EqualTo(new short[] { 1, 2 }));
        }
        finally
        {
            windows.Set();
        }
    }

    /// <summary>
    /// When Windows holds up a look at the devices while a capture runs, and the recording is stopped
    /// meanwhile, the running capture is released, once, as soon as the look returns. What the look found is
    /// not acted on, because the recording has ended. Stopping again meanwhile hands back the same result at once.
    /// </summary>
    [Test]
    public async Task A_capture_a_stop_could_not_wait_for_is_released_once_windows_answers()
    {
        using var windows = new ManualResetEventSlim();
        var speakers = _provider.Enqueue(new FakeAudioCapture(Pcm16KMono, "Speakers"));
        using var session = CreateSession();
        try
        {
            session.Start(_files);
            speakers.Deliver(TestAudio.Shorts(1, 2));
            _provider.Enqueue(new FakeAudioCapture(Pcm16KMono, "Headphones"), level: 0.5f);   // sound starts on the headphones
            await TickAsync();                                          // and is seen there
            _provider.HoldNextListing(windows);                         // the next look at the devices is held up inside Windows
            await TickAsync();
            await Wait.UntilAsync(() => _provider.ListingHeld);
            var result = await StopWhileWindowsHoldsUpAsync(session);
            var again = await Task.Run(session.Stop).WaitAsync(TimeSpan.FromSeconds(5));
            windows.Set();                                              // Windows answers: the sound has lasted, so it would move the recording
            await Wait.UntilAsync(() => speakers.Disposed);
            await Task.Delay(100);                                      // a second release, or a capture opened after all, would show up meanwhile

            Assert.That(again, Is.SameAs(result), "stopping again hands back the same result");
            Assert.That(speakers.DisposeCalls, Is.EqualTo(1), "the capture left running is released once");
            Assert.That(_provider.Created, Is.EqualTo(new[] { speakers }), "nothing more is opened after the stop");
            Assert.That(_log.Messages, Has.None.Contains("Sound is playing on"));
            Assert.That(TestAudio.ReadWav(result.SegmentPaths.Single()).Samples, Is.EqualTo(new short[] { 1, 2 }));
        }
        finally
        {
            windows.Set();
        }
    }

    /// <summary>A recording that reaches the size limit continues in a new file, and no audio is lost at the change.</summary>
    [Test]
    public void A_large_recording_rolls_over_to_a_new_segment()
    {
        var capture = _provider.Enqueue(new FakeAudioCapture(Pcm16KMono));
        using var session = CreateSession(new RecordingOptions { MaxSegmentMegabytes = 1 });
        var chunk = TestAudio.Shorts(Enumerable.Repeat((short)1000, 300_000).ToArray());   // 600 KB

        session.Start(_files);
        capture.Deliver(chunk);
        capture.Deliver(chunk);   // crosses 1 MB, so the next chunk belongs to a new file
        capture.Deliver(chunk);
        var result = session.Stop();

        Assert.That(result.SegmentPaths, Has.Count.EqualTo(2));
        Assert.That(new FileInfo(result.SegmentPaths[0]).Length, Is.EqualTo(44L + 1_200_000));
        Assert.That(new FileInfo(result.SegmentPaths[1]).Length, Is.EqualTo(44L + 600_000));
        Assert.That(result.Duration.TotalSeconds, Is.EqualTo(900_000 / 16000.0).Within(0.001));
    }

    /// <summary>
    /// A mark's position, the length captured when it was set, points at the same moment in the upload, to
    /// within a millisecond: across a new file for size, a move to a device with another sample rate, and
    /// the joining and resampling for upload.
    /// </summary>
    [Test]
    public async Task A_mark_points_at_the_same_moment_in_the_upload_across_files_and_devices()
    {
        var speakers = _provider.Enqueue(new FakeAudioCapture(new AudioFormat(48000, 1, 16, false), "Speakers"));
        using var session = CreateSession(new RecordingOptions { MaxSegmentMegabytes = 1 });
        var marks = new List<(TimeSpan Position, short Edge)>();
        var step = 0;

        // A staircase that climbs a thousand every half second, at the very moment each mark is set, and is
        // level everywhere else. So each mark has an edge of its own, which the upload has to show where the
        // mark says, and the height of the edge tells which mark it belongs to.
        void Play(FakeAudioCapture capture, int sampleRate, int steps)
        {
            for (var played = 0; played < steps; played++)
            {
                var edge = (short)(++step * 1000);
                capture.Deliver(Samples(sampleRate / 4, (short)(edge - 500)));
                marks.Add((session.GetStatus().Captured, edge));
                capture.Deliver(Samples(sampleRate / 4, (short)(edge + 500)));
            }
        }

        session.Start(_files);
        Play(speakers, 48000, 24);                              // the first file fills up after 22 steps
        var headphones = _provider.SwitchDefaultDevice(new FakeAudioCapture(new AudioFormat(44100, 1, 16, false), "Headphones"), level: 0.2f);
        await TickUntilAsync(() => headphones.Started);
        Play(headphones, 44100, 6);
        var result = session.Stop();
        var upload = Path.Combine(_folder.Path, "upload.wav");
        new AudioPreparer(NullLogger<AudioPreparer>.Instance).Prepare(result.SegmentPaths, upload, CancellationToken.None);
        var uploaded = TestAudio.ReadWav(upload).Samples;

        Assert.That(result.SegmentPaths, Has.Count.EqualTo(3));
        Assert.That(uploaded.Length / 16000.0, Is.EqualTo(result.Duration.TotalSeconds).Within(0.01));
        foreach (var (position, edge) in marks)
        {
            // Where the upload, 16 samples to the millisecond, first reaches the middle of the mark's edge,
            // within 50 ms either side of the mark. Not reaching it there counts as far off.
            var mark = (int)Math.Round(position.TotalSeconds * 16000);
            var risen = Enumerable.Range(mark - 800, 1600).FirstOrDefault(i => uploaded[i] >= edge, int.MaxValue);
            Assert.That((risen - mark) / 16.0, Is.EqualTo(0).Within(1), $"how far, in milliseconds, the mark at {position} is from its edge");
        }
    }

    /// <summary>The header on disk is brought up to date every few seconds, so a crash leaves a valid file.</summary>
    [Test]
    public void The_file_on_disk_stays_valid_while_recording()
    {
        var capture = _provider.Enqueue(new FakeAudioCapture(Pcm16KMono));
        using var session = CreateSession(new RecordingOptions { FlushSeconds = 5 });

        session.Start(_files);
        capture.Deliver(TestAudio.Shorts(1, 2));
        _time.Advance(TimeSpan.FromSeconds(6));
        capture.Deliver(TestAudio.Shorts(3));

        // Read the file as a crash would leave it: the session is still running.
        var onDisk = TestAudio.ReadWav(_files.Segment(1));

        Assert.That(onDisk.Samples, Is.EqualTo(new short[] { 1, 2, 3 }));
    }

    /// <summary>Setting the computer's clock back does not hold up the regular save of the file on disk: saving follows the steady clock.</summary>
    [Test]
    public void Setting_the_clock_back_does_not_hold_up_saving()
    {
        var capture = _provider.Enqueue(new FakeAudioCapture(Pcm16KMono));
        using var session = CreateSession(new RecordingOptions { FlushSeconds = 5 });

        session.Start(_files);
        capture.Deliver(TestAudio.Shorts(1, 2));
        _time.StepWallClock(TimeSpan.FromHours(-1));         // Windows sets its clock back an hour
        _time.Advance(TimeSpan.FromSeconds(6));
        capture.Deliver(TestAudio.Shorts(3));

        Assert.That(TestAudio.ReadWav(_files.Segment(1)).Samples, Is.EqualTo(new short[] { 1, 2, 3 }));
    }

    /// <summary>A single disk error costs the audio of that moment, not the capture: the recording goes on in a new file.</summary>
    [Test]
    public void A_single_disk_error_costs_a_moment_of_audio_but_not_the_capture()
    {
        var capture = _provider.Enqueue(new FakeAudioCapture(Pcm16KMono));
        var classFolder = Path.Combine(_folder.Path, "class");
        using var session = CreateSession();

        session.Start(new RecordingFiles(classFolder, "Class_2026-10-11_14-00-00Z"));
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

    /// <summary>A disk that fails twice in a row is left alone for thirty seconds before the next file is tried.</summary>
    [Test]
    public void A_disk_that_keeps_failing_is_given_a_pause_before_the_next_file()
    {
        var capture = _provider.Enqueue(new FakeAudioCapture(Pcm16KMono));
        var classFolder = Path.Combine(_folder.Path, "class");
        using var session = CreateSession();

        session.Start(new RecordingFiles(classFolder, "Class_2026-10-11_14-00-00Z"));
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

    /// <summary>Setting the computer's clock back during the pause after disk failures does not make the pause any longer.</summary>
    [Test]
    public void Setting_the_clock_back_does_not_stretch_the_pause_after_disk_failures()
    {
        var capture = _provider.Enqueue(new FakeAudioCapture(Pcm16KMono));
        var classFolder = Path.Combine(_folder.Path, "class");
        using var session = CreateSession();

        session.Start(new RecordingFiles(classFolder, "Class_2026-10-11_14-00-00Z"));
        Directory.Delete(classFolder);
        capture.Deliver(TestAudio.Shorts(1));
        capture.Deliver(TestAudio.Shorts(2));           // the second failure in a row starts the pause
        Directory.CreateDirectory(classFolder);
        _time.StepWallClock(TimeSpan.FromHours(-1));    // Windows sets its clock back an hour
        _time.Advance(TimeSpan.FromSeconds(31));
        capture.Deliver(TestAudio.Shorts(4, 5));
        var result = session.Stop();

        Assert.That(TestAudio.ReadWav(result.SegmentPaths.Single()).Samples, Is.EqualTo(new short[] { 4, 5 }));
    }

    /// <summary>
    /// When the disk has room for a new file but none for its audio, as a full disk can, the file is removed
    /// rather than left behind with nothing in it, and it is neither handed back nor counted.
    /// </summary>
    [Test, Platform("Win")]
    [SupportedOSPlatform("windows")]
    public void A_file_whose_audio_never_reached_the_disk_is_removed()
    {
        var capture = _provider.Enqueue(new FakeAudioCapture(Pcm16KMono));
        var clock = new HookedClock(_time);
        using var session = CreateSession(time: clock);
        FileStream? fullDisk = null;
        clock.OnRead = () =>
        {
            // The first reading of the clock once the file is there falls before its first audio: the disk fills up then.
            if (fullDisk is null && File.Exists(_files.Segment(1))) fullDisk = FillDisk(_files.Segment(1));
        };

        RecordingResult result;
        try
        {
            session.Start(_files);
            capture.Deliver(TestAudio.Shorts(1, 2));       // the file is made, but these two samples find no room
            result = session.Stop();
        }
        finally
        {
            fullDisk?.Dispose();
        }

        Assert.That(fullDisk, Is.Not.Null, "the disk filled up between making the file and writing to it");
        Assert.That(result.SegmentPaths, Is.Empty);
        Assert.That(result.Duration, Is.EqualTo(TimeSpan.Zero));
        Assert.That(File.Exists(_files.Segment(1)), Is.False);
        Assert.That(_log.Problems, Has.Count.EqualTo(1), "one failure, reported once");
    }

    /// <summary>
    /// When the disk fills up, the audio written before it did is kept and counted: each write went to Windows
    /// at once, so none of it was waiting in a buffer of the app's own to be lost with the failed write.
    /// </summary>
    [Test, Platform("Win")]
    [SupportedOSPlatform("windows")]
    public void When_the_disk_fills_up_the_audio_written_before_is_kept_and_counted()
    {
        var capture = _provider.Enqueue(new FakeAudioCapture(Pcm16KMono));
        using var session = CreateSession();

        session.Start(_files);
        capture.Deliver(TestAudio.Shorts(1, 2));           // the file is made, and these two samples go to Windows at once
        using (FillDisk(_files.Segment(1)))
        {
            capture.Deliver(TestAudio.Shorts(3, 4));       // the disk is full now, so these two are lost
        }

        var result = session.Stop();

        Assert.That(result.SegmentPaths, Is.EqualTo(new[] { _files.Segment(1) }));
        Assert.That(TestAudio.ReadWav(_files.Segment(1)).Samples, Is.EqualTo(new short[] { 1, 2 }));
        Assert.That(result.Duration.TotalSeconds, Is.EqualTo(2 / 16000.0).Within(0.000001));
        Assert.That(_log.Problems, Has.Count.EqualTo(1), "one failure, reported once");
    }

    /// <summary>
    /// Two write failures far apart are each treated as a first one: after a successful save in between,
    /// the next failure costs no pause, and the audio after it is recorded at once.
    /// </summary>
    [Test, Platform("Win")]
    [SupportedOSPlatform("windows")]
    public void A_write_failure_long_after_the_last_one_costs_no_pause()
    {
        var capture = _provider.Enqueue(new FakeAudioCapture(Pcm16KMono));
        using var session = CreateSession(new RecordingOptions { FlushSeconds = 1 });

        session.Start(_files);
        capture.Deliver(TestAudio.Shorts(1));
        using (FillDisk(_files.Segment(1)))
        {
            capture.Deliver(TestAudio.Shorts(5));                         // a first failure
        }

        capture.Deliver(TestAudio.Shorts(2));                             // goes to a new file at once
        _time.Advance(TimeSpan.FromSeconds(2));
        capture.Deliver(TestAudio.Shorts(3));                             // and is saved: the disk works again
        using (FillDisk(_files.Segment(2)))
        {
            capture.Deliver(TestAudio.Shorts(6));                         // a second failure, long after the first
        }

        capture.Deliver(TestAudio.Shorts(4));                             // also goes to a new file at once
        var result = session.Stop();

        Assert.That(result.SegmentPaths, Has.Count.EqualTo(3));
        Assert.That(TestAudio.ReadWav(result.SegmentPaths[0]).Samples, Is.EqualTo(new short[] { 1 }));
        Assert.That(TestAudio.ReadWav(result.SegmentPaths[1]).Samples, Is.EqualTo(new short[] { 2, 3 }));
        Assert.That(TestAudio.ReadWav(result.SegmentPaths[2]).Samples, Is.EqualTo(new short[] { 4 }));
        Assert.That(_log.Problems, Has.None.StartsWith("Still cannot write"));
    }

    /// <summary>
    /// Stopping when the file can no longer be written, not even its header, still ends the recording, reports it,
    /// and keeps and counts all the audio already handed to Windows: the header is a save behind, but the audio
    /// is on disk, and the upload reads a recording's files to their end.
    /// </summary>
    [Test, Platform("Win")]
    [SupportedOSPlatform("windows")]
    public void Stopping_when_the_file_can_no_longer_be_written_keeps_and_counts_what_reached_windows()
    {
        var capture = _provider.Enqueue(new FakeAudioCapture(Pcm16KMono));
        using var session = CreateSession(new RecordingOptions { FlushSeconds = 1 });

        session.Start(_files);
        capture.Deliver(TestAudio.Shorts(1, 2));
        _time.Advance(TimeSpan.FromSeconds(1));
        capture.Deliver(TestAudio.Shorts(3));               // saved: the header now counts three samples
        capture.Deliver(TestAudio.Shorts(4, 5));            // handed to Windows, but not yet in the header
        RecordingResult result;
        using (LockWholeFile(_files.Segment(1)))
        {
            result = session.Stop();                        // so the last save of the header fails
        }

        Assert.That(File.ReadAllBytes(result.SegmentPaths.Single())[WavSegmentWriter.HeaderSize..], Is.EqualTo(TestAudio.Shorts(1, 2, 3, 4, 5)));
        Assert.That(result.Duration.TotalSeconds, Is.EqualTo(5 / 16000.0).Within(0.000001));
        Assert.That(_log.Problems, Has.Some.Contains("Could not finish writing"));
    }

    /// <summary>
    /// When the save at a pause fails, all the audio handed to Windows before it is counted, as it is on disk, so
    /// marks set later still point at the right moment; after the break the recording goes on in a new file.
    /// </summary>
    [Test, Platform("Win")]
    [SupportedOSPlatform("windows")]
    public void A_failed_save_at_a_pause_counts_the_audio_on_disk()
    {
        var capture = _provider.Enqueue(new FakeAudioCapture(Pcm16KMono));
        using var session = CreateSession(new RecordingOptions { FlushSeconds = 1 });

        session.Start(_files);
        capture.Deliver(Samples(8000, 100));                // half a second
        _time.Advance(TimeSpan.FromSeconds(1));
        capture.Deliver(Samples(8000, 200));                // the save due now brings the header up to the whole second
        capture.Deliver(Samples(8000, 300));                // half a second more, handed to Windows at once
        using (LockWholeFile(_files.Segment(1)))
        {
            session.Pause();                                // so the save for the pause fails
        }

        var paused = session.GetStatus().Captured;
        session.Resume();
        capture.Deliver(Samples(1600, 400));                // a tenth of a second after the break
        var result = session.Stop();

        Assert.That(paused, Is.EqualTo(TimeSpan.FromSeconds(1.5)), "all of it reached Windows, so all of it counts");
        Assert.That(_log.Problems, Has.Some.Contains("for the pause"));
        Assert.That(result.SegmentPaths, Has.Count.EqualTo(2));
        Assert.That(new FileInfo(result.SegmentPaths[0]).Length, Is.EqualTo(WavSegmentWriter.HeaderSize + 48_000L));
        Assert.That(TestAudio.ReadWav(result.SegmentPaths[1]).Samples, Is.EqualTo(Enumerable.Repeat((short)400, 1600)));
        Assert.That(result.Duration.TotalSeconds, Is.EqualTo(1.6).Within(0.000001));
    }

    /// <summary>
    /// When the save as the process ends fails, here because another program holds the whole file locked for a
    /// moment, the session reports it and hands it on to its caller, and it costs no audio: what still arrives goes
    /// after what came before, not over it, and stopping brings the file on disk up to date.
    /// </summary>
    [Test, Platform("Win")]
    [SupportedOSPlatform("windows")]
    public void A_failed_save_as_the_process_ends_is_reported_and_costs_no_audio()
    {
        var capture = _provider.Enqueue(new FakeAudioCapture(Pcm16KMono));
        using var session = CreateSession();

        session.Start(_files);
        capture.Deliver(TestAudio.Shorts(1000, -1000));
        using (LockWholeFile(_files.Segment(1)))
        {
            Assert.That(session.Flush, Throws.InstanceOf<IOException>(), "the caller hears of it as well");
        }

        capture.Deliver(TestAudio.Shorts(7, -7));           // audio still arriving in the process's last moments
        var result = session.Stop();

        Assert.That(_log.Problems, Has.Some.StartsWith($"Could not bring {Path.GetFileName(_files.Segment(1))} on disk up to date"));
        Assert.That(File.ReadAllBytes(_files.Segment(1))[WavSegmentWriter.HeaderSize..], Is.EqualTo(TestAudio.Shorts(1000, -1000, 7, -7)));
        Assert.That(TestAudio.ReadWav(result.SegmentPaths.Single()).Samples, Is.EqualTo(new short[] { 1000, -1000, 7, -7 }));
        Assert.That(result.Duration.TotalSeconds, Is.EqualTo(4 / 16000.0).Within(0.000001));
    }

    /// <summary>A driver that will not shut down cleanly is reported, and the recording is kept all the same.</summary>
    [Test]
    public void A_device_that_will_not_shut_down_cleanly_does_not_cost_the_recording()
    {
        var capture = _provider.Enqueue(new FakeAudioCapture(Pcm16KMono) { DisposeFailure = new InvalidOperationException("Driver trouble.") });
        using var session = CreateSession();

        session.Start(_files);
        capture.Deliver(TestAudio.Shorts(1, 2, 3));
        var result = session.Stop();

        Assert.That(result.SegmentPaths, Has.Count.EqualTo(1));
        Assert.That(TestAudio.ReadWav(result.SegmentPaths[0]).Samples, Is.EqualTo(new short[] { 1, 2, 3 }));
        Assert.That(_log.Problems, Has.Count.EqualTo(1), "the trouble is reported even though the recording is fine");
    }

    /// <summary>A file that is already there under the next name is never overwritten; the recording takes the next free name.</summary>
    [Test]
    public void A_file_that_is_already_there_is_left_alone()
    {
        File.WriteAllText(_files.Segment(1), "precious");
        var capture = _provider.Enqueue(new FakeAudioCapture(Pcm16KMono));
        using var session = CreateSession();

        session.Start(_files);
        capture.Deliver(TestAudio.Shorts(1, 2));
        var result = session.Stop();

        Assert.That(Path.GetFileName(result.SegmentPaths.Single()), Is.EqualTo(Path.GetFileName(_files.Segment(2))));
        Assert.That(File.ReadAllText(_files.Segment(1)), Is.EqualTo("precious"));
    }

    /// <summary>The status says how long ago sound was last heard and how much has been captured.</summary>
    [Test]
    public void Status_reports_how_long_ago_sound_was_heard()
    {
        var capture = _provider.Enqueue(new FakeAudioCapture(Pcm16KMono));
        using var session = CreateSession();

        session.Start(_files);
        capture.Deliver(TestAudio.Shorts(0, 0, 0));
        _time.Advance(TimeSpan.FromSeconds(90));
        var whileSilent = session.GetStatus();
        capture.Deliver(TestAudio.Shorts(0, 5000, 0));
        var afterSound = session.GetStatus();

        Assert.That(whileSilent.SinceLastSound.TotalSeconds, Is.EqualTo(90.0).Within(0.001));
        Assert.That(afterSound.SinceLastSound.TotalSeconds, Is.EqualTo(0.0).Within(0.001));
        Assert.That(afterSound.Captured.TotalSeconds, Is.EqualTo(6 / 16000.0).Within(0.000001));
    }

    /// <summary>Setting the computer's clock forward does not make the recording look silent.</summary>
    [Test]
    public void Setting_the_clock_forward_does_not_look_like_silence()
    {
        var capture = _provider.Enqueue(new FakeAudioCapture(Pcm16KMono));
        using var session = CreateSession();

        session.Start(_files);
        capture.Deliver(TestAudio.Shorts(0, 5000));
        _time.StepWallClock(TimeSpan.FromMinutes(10));       // Windows sets its clock forward ten minutes

        Assert.That(session.GetStatus().SinceLastSound, Is.EqualTo(TimeSpan.Zero));
    }

    /// <summary>A device whose audio format cannot be read is refused at the start, and the session lets go of it and of the device notifications.</summary>
    [Test]
    public void A_device_with_an_unreadable_format_is_refused()
    {
        var capture = _provider.Enqueue(new FakeAudioCapture(new AudioFormat(8000, 1, 8, false)));
        using var session = CreateSession();

        Assert.Throws<NotSupportedException>(() => session.Start(_files));
        Assert.That(capture.Disposed, Is.True);
        Assert.That(_provider.HasListeners, Is.False);
        Assert.That(Directory.GetFiles(_folder.Path), Is.Empty);
    }

    /// <summary>Stopping a second time hands back the same result.</summary>
    [Test]
    public void Stop_can_be_called_twice()
    {
        var capture = _provider.Enqueue(new FakeAudioCapture(Pcm16KMono));
        using var session = CreateSession();

        session.Start(_files);
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

        session.Start(_files);
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

        session.Start(_files);
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

        session.Start(_files);
        capture.Deliver(TestAudio.Shorts(0, 5000));
        session.Pause();
        _time.Advance(TimeSpan.FromMinutes(15));
        session.Resume();

        Assert.That(session.GetStatus().SinceLastSound, Is.EqualTo(TimeSpan.Zero));
    }
}
