using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using ClassScribe.Audio;
using ClassScribe.Audio.Windows;
using ClassScribe.Commands;
using ClassScribe.Settings;
using ClassScribe.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace ClassScribe.Tests;

/// <summary>The recording session, driven by a fake capture device, and what its arrival watch takes from Windows' capture of an app.</summary>
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
    private RecordingLogger<CaptureChooser> _chooserLog = null!;
    private RecordingFiles _files = null!;

    /// <summary>Creates a temporary folder, a recording in it, the simulated devices, a clock moved by hand and logs to inspect.</summary>
    [SetUp]
    public void SetUp()
    {
        _folder = new TempFolder();
        _files = new RecordingFiles(_folder.Path, "Class_2026-10-11_14-00-00Z");
        _provider = new FakeCaptureProvider();
        _time = new ManualTimeProvider(new DateTimeOffset(2026, 10, 11, 14, 0, 0, TimeSpan.Zero));
        _log = new RecordingLogger<RecordingSession>();
        _chooserLog = new RecordingLogger<CaptureChooser>();
    }

    /// <summary>Deletes the temporary folder. That fails if a test left a file open, which is the point.</summary>
    [TearDown]
    public void TearDown() => _folder.Dispose();

    /// <summary>
    /// Creates the session under test, on the test's manual clock unless <paramref name="time"/> gives another. It records
    /// through a loopback source of the simulated devices, what the settings name, as a run's request takes it from the
    /// menu's choice: Recording:App, Recording:Device, or else the device the class plays on.
    /// </summary>
    private RecordingSession CreateSession(RecordingOptions? options = null, TimeProvider? time = null)
    {
        var settings = TestOptions.Create(options ?? new RecordingOptions());
        time ??= _time;
        var source = new SourceChoice(settings).Current.Class!;
        return new RecordingSession(new LoopbackSource(_provider, new CaptureChooser(time, source, _chooserLog)), settings, time, _log);
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

    /// <summary>A capture of an app's sound, which like Windows' process loopback hands over audio all the time, silence included.</summary>
    private static FakeAudioCapture AppCapture(int processId, string name = "chrome") =>
        new(Pcm16KMono, name, $"process:{processId}") { DeliversContinuously = true };

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

    /// <summary>
    /// When the recorded app closes, its capture stays open and records silence, as Windows' capture of an app does. That
    /// is said once, not at every look, and the capture is not replaced. When the app plays again from a new process, the
    /// recording follows it.
    /// </summary>
    [Test]
    public async Task An_app_that_closes_is_said_once_and_followed_when_it_plays_again()
    {
        var chrome = _provider.AddApp(4242, "chrome", "Speakers", level: 0.3f, AppCapture(4242))!;
        using var session = CreateSession(new RecordingOptions { App = "chrome" });

        session.Start(_files);
        chrome.Deliver(TestAudio.Shorts(1, 2));
        _provider.RemoveApp(4242);                           // the app closes
        for (var look = 0; look < 3; look++)
        {
            await TickAsync();
            await Task.Delay(50);                            // the look ends before the next one
        }

        var afterItClosed = session.GetStatus();
        var again = _provider.AddApp(4343, "chrome", "Speakers", level: 0.3f, AppCapture(4343))!;
        await TickUntilAsync(() => again.Started);
        again.Deliver(TestAudio.Shorts(3, 4));
        var result = session.Stop();

        Assert.That(_chooserLog.Problems.Where(problem => problem == "\"chrome\" has closed; recording silence until it plays again"), Has.Exactly(1).Items);
        Assert.That(afterItClosed.WaitingFor, Is.Null, "its capture is still open");
        Assert.That(chrome.Disposed, Is.True, "and is let go once the app plays again");
        Assert.That(result.SegmentPaths, Has.Count.EqualTo(2));
        Assert.That(TestAudio.ReadWav(result.SegmentPaths[1]).Samples, Is.EqualTo(new short[] { 3, 4 }));
    }

    /// <summary>
    /// In app mode the messages name the app rather than an output device: when its sound cannot be captured at the start,
    /// and when it cannot be captured again after its capture failed.
    /// </summary>
    [Test]
    public async Task In_app_mode_the_messages_name_the_app()
    {
        _provider.AddApp(4242, "chrome", "Speakers", level: 0.3f);              // playing, but its sound cannot be captured yet
        using var session = CreateSession(new RecordingOptions { App = "chrome" });

        session.Start(_files);
        var first = _provider.AddApp(4242, "chrome", "Speakers", level: 0.3f, AppCapture(4242))!;   // a moment later it can
        await TickUntilAsync(() => first.Started);
        first.Fail(new IOException("The app's sound went away."));           // and then it cannot again
        await Wait.UntilAsync(() => _log.Problems.Any(problem => problem.StartsWith("Cannot capture", StringComparison.Ordinal)));
        session.Stop();

        Assert.That(_log.Problems, Has.Some.EqualTo("Could not start capturing the sound of \"chrome\" yet; trying again every second"));
        Assert.That(_log.Problems, Has.Some.EqualTo("Cannot capture the app \"chrome\" right now; trying again every second"));
        Assert.That(_log.Problems, Has.None.Contains("output device"));
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
    /// marks set later still point at the right moment; after the break the recording goes on in a new file. The status
    /// says that writing fails from the failed save on, through the pause, until the first write after the break works.
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

        var paused = session.GetStatus();
        session.Resume();
        capture.Deliver(Samples(1600, 400));                // a tenth of a second after the break
        var writing = session.GetStatus();
        var result = session.Stop();

        Assert.That(paused.Captured, Is.EqualTo(TimeSpan.FromSeconds(1.5)), "all of it reached Windows, so all of it counts");
        Assert.That(paused.WriteFailing, Is.True, "the failed save shows at once, not only after the break");
        Assert.That(writing.WriteFailing, Is.False, "the first write after the break works");
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

    /// <summary>
    /// Sound that arrives while paused is measured, though not written: the status says that sound keeps arriving, as from
    /// a class that plays on during the pause, and a short quiet, such as between two sentences, does not end that. Silence
    /// alone is no such sign, and once recording again there is nothing to say.
    /// </summary>
    [Test]
    public void Sound_that_arrives_while_paused_is_measured_but_not_written()
    {
        var capture = _provider.Enqueue(new FakeAudioCapture(Pcm16KMono));
        using var session = CreateSession();

        session.Start(_files);
        capture.Deliver(TestAudio.Shorts(1, 2));
        var recording = session.GetStatus();
        session.Pause();
        capture.Deliver(Samples(1600, 0));                   // silence while paused
        var silent = session.GetStatus();
        capture.Deliver(Samples(1600, 5000));                // sound while paused
        var sound = session.GetStatus();
        _time.Advance(TimeSpan.FromSeconds(4));              // a quiet moment between two sentences
        var quietMoment = session.GetStatus();
        _time.Advance(TimeSpan.FromSeconds(2));              // and then nothing more
        var over = session.GetStatus();
        capture.Deliver(Samples(1600, 5000));
        session.Resume();
        var resumed = session.GetStatus();
        capture.Deliver(TestAudio.Shorts(3, 4));
        var result = session.Stop();

        Assert.That(recording.SoundWhilePaused, Is.False);
        Assert.That(silent.SoundWhilePaused, Is.False, "silence is no sign of the class");
        Assert.That(sound.SoundWhilePaused, Is.True);
        Assert.That(quietMoment.SoundWhilePaused, Is.True, "a short quiet does not end it");
        Assert.That(over.SoundWhilePaused, Is.False);
        Assert.That(resumed.SoundWhilePaused, Is.False, "recording again, nothing plays unrecorded");
        Assert.That(sound.Captured, Is.EqualTo(recording.Captured), "nothing heard during the pause counts as recorded");
        Assert.That(TestAudio.ReadWav(result.SegmentPaths.Single()).Samples, Is.EqualTo(new short[] { 1, 2, 3, 4 }), "and none of it was written");
    }

    /// <summary>
    /// Each pause starts with no sound heard: sound heard during the pause before, however recent, does not carry over into
    /// the next one, which measures its own.
    /// </summary>
    [Test]
    public void Each_pause_starts_with_no_sound_heard()
    {
        var capture = _provider.Enqueue(new FakeAudioCapture(Pcm16KMono));
        using var session = CreateSession();

        session.Start(_files);
        session.Pause();
        capture.Deliver(Samples(1600, 5000));                // sound during the first pause
        var firstPause = session.GetStatus();
        session.Resume();
        session.Pause();                                     // a second pause at once, with the clock where it was
        var secondPause = session.GetStatus();
        capture.Deliver(Samples(1600, 5000));                // and sound during it
        var soundAgain = session.GetStatus();
        session.Stop();

        Assert.That(firstPause.SoundWhilePaused, Is.True);
        Assert.That(secondPause.SoundWhilePaused, Is.False, "the sound of the first pause is not carried over");
        Assert.That(soundAgain.SoundWhilePaused, Is.True, "the second pause measures its own");
    }

    /// <summary>
    /// A move of the sound to another device is followed while paused too, so that the recording goes on from the device
    /// the class plays on once the pause ends. Nothing heard during the pause is written, from either device.
    /// </summary>
    [Test]
    public async Task Sound_moving_to_another_device_is_followed_while_paused()
    {
        var speakers = _provider.Enqueue(new FakeAudioCapture(Pcm16KMono, "Speakers"));
        using var session = CreateSession();

        session.Start(_files);
        speakers.Deliver(TestAudio.Shorts(1, 2));
        session.Pause();
        var headphones = _provider.SwitchDefaultDevice(new FakeAudioCapture(Pcm16KMono, "Headphones"), level: 0.2f);
        await TickUntilAsync(() => headphones.Started);
        headphones.Deliver(Samples(1600, 5000));            // heard during the pause: measured, not written
        var paused = session.GetStatus();
        session.Resume();
        headphones.Deliver(TestAudio.Shorts(3, 4));
        var result = session.Stop();

        Assert.That(speakers.Disposed, Is.True);
        Assert.That(paused.Paused, Is.True);
        Assert.That(paused.DeviceName, Is.EqualTo("Headphones"));
        Assert.That(paused.SoundWhilePaused, Is.True, "the sound on the new device is measured");
        Assert.That(result.SegmentPaths, Has.Count.EqualTo(2));
        Assert.That(TestAudio.ReadWav(result.SegmentPaths[0]).Samples, Is.EqualTo(new short[] { 1, 2 }));
        Assert.That(TestAudio.ReadWav(result.SegmentPaths[1]).Samples, Is.EqualTo(new short[] { 3, 4 }));
        Assert.That(_log.Messages, Has.Some.Contains("Sound is playing on \"Headphones\"; recording it instead of \"Speakers\""));
    }

    /// <summary>
    /// A device that keeps playing during a pause is kept, however loud another one is: its sound is measured while none
    /// of it is written, so a class that plays on through the break holds the recording where it is.
    /// </summary>
    [Test]
    public async Task A_device_that_keeps_playing_during_a_pause_is_kept()
    {
        var speakers = _provider.Enqueue(new FakeAudioCapture(Pcm16KMono, "Speakers"), level: 0.3f);
        var headphones = _provider.Enqueue(new FakeAudioCapture(Pcm16KMono, "Headphones"));
        using var session = CreateSession();

        session.Start(_files);
        session.Pause();
        _provider.SetLevel("Headphones", 0.9f);              // something loud starts on the headphones
        for (var second = 0; second < 5; second++)
        {
            speakers.Deliver(TestAudio.Shorts(0, 5000));   // while the class plays on through the break
            await TickAsync();
            await Task.Delay(50);
        }

        var result = session.Stop();

        Assert.That(headphones.Started, Is.False);
        Assert.That(session.GetStatus().DeviceName, Is.EqualTo("Speakers"));
        Assert.That(result.SegmentPaths, Is.Empty, "nothing was written during the pause");
    }

    /// <summary>
    /// When Windows no longer lists the recorded device, though its capture has not ended, the next look takes another
    /// device, here the default one since nothing plays, and the recording moves there into a new part. It does so while
    /// paused as at any other time, so that the recording goes on from a device that is there once the pause ends; nothing
    /// heard during the pause is written.
    /// </summary>
    [TestCase(false)]
    [TestCase(true)]
    public async Task A_device_windows_no_longer_lists_is_left_for_the_default_one(bool paused)
    {
        var speakers = _provider.Enqueue(new FakeAudioCapture(Pcm16KMono, "Speakers"));
        using var session = CreateSession();

        session.Start(_files);
        speakers.Deliver(TestAudio.Shorts(1, 2));
        if (paused) session.Pause();
        _provider.RemoveDevices();                           // Windows lists the speakers no more, though their capture goes on
        var headphones = _provider.Enqueue(new FakeAudioCapture(Pcm16KMono, "Headphones"));   // and the headphones are the default device now
        await TickUntilAsync(() => headphones.Started);
        headphones.Deliver(TestAudio.Shorts(9, 9));          // kept unless paused
        if (paused) session.Resume();
        headphones.Deliver(TestAudio.Shorts(3, 4));
        var result = session.Stop();

        Assert.That(speakers.Disposed, Is.True, "the capture of the device that has gone is let go");
        Assert.That(result.SegmentPaths, Has.Count.EqualTo(2));
        Assert.That(TestAudio.ReadWav(result.SegmentPaths[0]).Samples, Is.EqualTo(new short[] { 1, 2 }));
        Assert.That(TestAudio.ReadWav(result.SegmentPaths[1]).Samples, Is.EqualTo(paused ? new short[] { 3, 4 } : new short[] { 9, 9, 3, 4 }));
    }

    /// <summary>
    /// The result says whether any sound reached the files: not when only silence was written, nor when sound came only
    /// during a pause; yes once a moment of sound was written.
    /// </summary>
    [Test]
    public void The_result_says_whether_any_sound_was_recorded()
    {
        RecordingResult Record(string folder, Action<FakeAudioCapture, RecordingSession> play)
        {
            var capture = _provider.Enqueue(new FakeAudioCapture(Pcm16KMono));
            using var session = CreateSession();
            session.Start(new RecordingFiles(Path.Combine(_folder.Path, folder), "Class_2026-10-11_14-00-00Z"));
            play(capture, session);
            return session.Stop();
        }

        var silence = Record("silence", (capture, _) => capture.Deliver(Samples(1600, 0)));
        var onlyWhilePaused = Record("paused", (capture, session) =>
        {
            capture.Deliver(Samples(1600, 0));
            session.Pause();
            capture.Deliver(Samples(1600, 5000));
            session.Resume();
            capture.Deliver(Samples(1600, 0));
        });
        var sound = Record("sound", (capture, _) =>
        {
            capture.Deliver(Samples(1600, 0));
            capture.Deliver(TestAudio.Shorts(0, 5000, 0));
        });

        Assert.That(silence.SegmentPaths, Has.Count.EqualTo(1), "silence is written, as it arrives");
        Assert.That(silence.HeardSound, Is.False);
        Assert.That(onlyWhilePaused.HeardSound, Is.False);
        Assert.That(sound.HeardSound, Is.True);
    }

    /// <summary>
    /// A recording that stops without waiting for a device change Windows holds up still says that sound was recorded
    /// before it, so its transcript is not given up for nothing heard.
    /// </summary>
    [Test]
    public async Task A_stop_that_cannot_wait_still_says_that_sound_was_recorded()
    {
        using var windows = new ManualResetEventSlim();
        var first = _provider.Enqueue(new FakeAudioCapture(Pcm16KMono));
        _provider.Enqueue(new FakeAudioCapture(Pcm16KMono));
        using var session = CreateSession();
        try
        {
            session.Start(_files);
            first.Deliver(TestAudio.Shorts(0, 5000, 0));
            _provider.HoldNextCreation(windows);                        // Windows holds up opening the replacement
            first.Fail(new IOException("The device was unplugged."));
            await Wait.UntilAsync(() => _provider.CreationHeld);
            var result = await StopWhileWindowsHoldsUpAsync(session);

            Assert.That(_log.Problems, Has.Some.Contains("stops without waiting"));
            Assert.That(result.HeardSound, Is.True);
        }
        finally
        {
            windows.Set();
        }
    }

    /// <summary>
    /// The status says while writing fails, as on a full disk or a drive that has gone: from the first write that fails,
    /// through the pause given to a disk that keeps failing, until a write works again.
    /// </summary>
    [Test]
    public void The_status_says_while_writing_fails()
    {
        var capture = _provider.Enqueue(new FakeAudioCapture(Pcm16KMono));
        var classFolder = Path.Combine(_folder.Path, "class");
        using var session = CreateSession();

        session.Start(new RecordingFiles(classFolder, "Class_2026-10-11_14-00-00Z"));
        var atStart = session.GetStatus();
        Directory.Delete(classFolder);                       // the drive goes away
        capture.Deliver(TestAudio.Shorts(1, 2));            // cannot be written
        var failed = session.GetStatus();
        capture.Deliver(TestAudio.Shorts(3, 4));            // nor this: the disk keeps failing, so it is given a pause
        var stillFailing = session.GetStatus();
        Directory.CreateDirectory(classFolder);              // the drive is back
        _time.Advance(TimeSpan.FromSeconds(31));             // and the pause is over
        capture.Deliver(TestAudio.Shorts(5, 6));
        var writing = session.GetStatus();
        var result = session.Stop();

        Assert.That(atStart.WriteFailing, Is.False);
        Assert.That(failed.WriteFailing, Is.True);
        Assert.That(stillFailing.WriteFailing, Is.True);
        Assert.That(writing.WriteFailing, Is.False, "a write that works ends it");
        Assert.That(TestAudio.ReadWav(result.SegmentPaths.Single()).Samples, Is.EqualTo(new short[] { 5, 6 }));
    }

    /// <summary>
    /// The session looks as often as its source asks, not once a second whatever the source, and its messages say how
    /// often it tries again. A source that looks every ten seconds, as one for a microphone will, stands in here.
    /// </summary>
    [Test]
    public async Task The_session_looks_as_often_as_its_source_asks()
    {
        _provider.ConnectDevice("Speakers");                 // connected, but it cannot be opened
        var settings = TestOptions.Create(new RecordingOptions());
        var source = new PacedSource(new LoopbackSource(_provider, new CaptureChooser(_time, new ClassSource.Automatic(), _chooserLog)), TimeSpan.FromSeconds(10));
        using var session = new RecordingSession(source, settings, _time, _log);

        session.Start(_files);
        var atStart = _provider.DeviceQueries;
        _time.Advance(TimeSpan.FromSeconds(9));
        await Task.Delay(100);                               // a look set off too early would show up meanwhile
        var beforeTenSeconds = _provider.DeviceQueries;
        _time.Advance(TimeSpan.FromSeconds(1));
        await Wait.UntilAsync(() => _provider.DeviceQueries == atStart + 1);
        await Wait.UntilAsync(() => _provider.FailedCreations == 2);
        session.Stop();

        Assert.That(beforeTenSeconds, Is.EqualTo(atStart), "no look before ten seconds");
        Assert.That(_log.Problems, Has.Some.EqualTo("Could not start capturing system audio yet; trying again every 10 seconds"));
    }

    /// <summary>
    /// The session owns its source and lets go of it when it is disposed, after stopping, whether it was started or not;
    /// stopping alone keeps it.
    /// </summary>
    [TestCase(false)]
    [TestCase(true)]
    public void A_session_lets_go_of_its_source_when_it_is_disposed(bool started)
    {
        _provider.Enqueue(new FakeAudioCapture(Pcm16KMono));
        var source = new PacedSource(new LoopbackSource(_provider, new CaptureChooser(_time, new ClassSource.Automatic(), _chooserLog)), TimeSpan.FromSeconds(1));
        var session = new RecordingSession(source, TestOptions.Create(new RecordingOptions()), _time, _log);

        if (started)
        {
            session.Start(_files);
            session.Stop();
        }

        var beforeDisposing = source.Disposed;
        session.Dispose();

        Assert.That(beforeDisposing, Is.False);
        Assert.That(source.Disposed, Is.True);
        Assert.That(_provider.HasListeners, Is.False);
    }

    /// <summary>A capture source that looks as often as it is told, notes whether it was disposed, and otherwise is the source it wraps.</summary>
    private sealed class PacedSource(ICaptureSource inner, TimeSpan checkInterval) : ICaptureSource
    {
        /// <summary>True once the source was disposed.</summary>
        public bool Disposed { get; private set; }

        /// <inheritdoc />
        public event EventHandler? Changed
        {
            add => inner.Changed += value;
            remove => inner.Changed -= value;
        }

        /// <inheritdoc />
        public SourceWording Wording => inner.Wording;

        /// <summary>As often as the test said.</summary>
        public TimeSpan CheckInterval => checkInterval;

        /// <inheritdoc />
        public string WaitingFor => inner.WaitingFor;

        /// <inheritdoc />
        public float? RecordedLevel => inner.RecordedLevel;

        /// <inheritdoc />
        public CaptureTarget? Choose(string? recordedId, TimeSpan? sinceRecordedSound) => inner.Choose(recordedId, sinceRecordedSound);

        /// <inheritdoc />
        public IAudioCapture Open(CaptureTarget target) => inner.Open(target);

        /// <inheritdoc />
        public string DescribeMove(CaptureTarget chosen, string from) => inner.DescribeMove(chosen, from);

        /// <summary>Notes that the source was disposed, and disposes the one it wraps.</summary>
        public void Dispose()
        {
            Disposed = true;
            inner.Dispose();
        }
    }

    /// <summary>
    /// An app's sound arrives all the time, silence included. So the time since its last packet stays at zero while it
    /// delivers, even through silence, and grows once nothing arrives any more: the sign of a capture that is still open
    /// but has stopped working. Until the first packet it counts from the start of the capture.
    /// </summary>
    [Test]
    public void A_continuous_source_that_stops_shows_the_time_since_its_last_packet_growing()
    {
        var chrome = _provider.AddApp(4242, "chrome", "Speakers", level: 0.3f, AppCapture(4242))!;
        using var session = CreateSession(new RecordingOptions { App = "chrome" });

        session.Start(_files);
        var atStart = session.GetStatus();
        _time.Advance(TimeSpan.FromSeconds(2));
        var beforeTheFirstPacket = session.GetStatus();
        chrome.Deliver(Samples(1600, 0));                    // silence, which an app's capture hands over too
        var delivering = session.GetStatus();
        _time.Advance(TimeSpan.FromSeconds(3));              // then nothing arrives any more
        var stalled = session.GetStatus();
        _time.Advance(TimeSpan.FromSeconds(4));
        var later = session.GetStatus();
        session.Stop();

        Assert.That(atStart.DeliversContinuously, Is.True);
        Assert.That(atStart.SinceLastPacket, Is.EqualTo(TimeSpan.Zero));
        Assert.That(beforeTheFirstPacket.SinceLastPacket, Is.EqualTo(TimeSpan.FromSeconds(2)));
        Assert.That(delivering.SinceLastPacket, Is.EqualTo(TimeSpan.Zero));
        Assert.That(delivering.SinceLastSound, Is.EqualTo(TimeSpan.FromSeconds(2)), "silence arrived, but no sound");
        Assert.That(stalled.SinceLastPacket, Is.EqualTo(TimeSpan.FromSeconds(3)));
        Assert.That(later.SinceLastPacket, Is.EqualTo(TimeSpan.FromSeconds(7)));
        Assert.That(later.DeliversContinuously, Is.True);
    }

    /// <summary>
    /// A capture that hands over nothing while nothing plays, as an output device's loopback does (the fake's default
    /// stands for it), is reported as not continuous: a gap between its packets is no fault in itself. The status still
    /// gives the time since its last packet, on the steady clock, and the device's level, which together show a capture
    /// that has stopped although its device plays.
    /// </summary>
    [Test]
    public void A_capture_that_does_not_deliver_continuously_is_reported_so()
    {
        var speakers = _provider.Enqueue(new FakeAudioCapture(Pcm16KMono, "Speakers"), level: 0.4f);
        using var session = CreateSession();

        session.Start(_files);
        speakers.Deliver(TestAudio.Shorts(0, 5000));
        _time.StepWallClock(TimeSpan.FromSeconds(3));        // the time of day alone moves: no look is due
        var afterAClockStep = session.GetStatus();
        _time.Advance(TimeSpan.FromSeconds(3));              // and the device plays, but nothing arrives
        var status = session.GetStatus();
        session.Stop();

        Assert.That(status.DeliversContinuously, Is.False);
        Assert.That(afterAClockStep.SinceLastPacket, Is.EqualTo(TimeSpan.Zero), "timed on the steady clock");
        Assert.That(status.SinceLastPacket, Is.EqualTo(TimeSpan.FromSeconds(3)));
        Assert.That(status.RecordedLevel, Is.EqualTo(0.4f));
    }

    /// <summary>
    /// Windows' capture of an app's sound, through process loopback, says that it delivers continuously: it hands over
    /// silence while the app plays nothing, so the arrival watch may expect its packets all the time. It is made around a
    /// stand-in for Windows' audio client that refuses every call, and making it calls none, so no audio device is
    /// touched. It is not disposed, because that would hand the stand-in to COM to release. The capture of an output
    /// device can only be made around a real device (see <see cref="Windows_capture_of_an_output_device_does_not_deliver_continuously"/>).
    /// </summary>
    [Test, Platform("Win")]
    [SupportedOSPlatform("windows")]
    public void Windows_capture_of_an_app_delivers_continuously()
    {
        var client = new Mock<IAudioClient>(MockBehavior.Strict);

        var capture = WasapiAudioCapture.ForApp(new AudioClient(client.Object), "process:4242", "chrome", NullLogger.Instance);

        Assert.That(capture.DeliversContinuously, Is.True);
    }

    /// <summary>
    /// Windows' capture of an output device, through device loopback, says that it does not deliver continuously: it hands
    /// over nothing while nothing plays on the device, so the arrival watch must not take a quiet stretch of the class for
    /// a stalled capture. It is made around the default output device, as a device's capture can only be, and is never
    /// started: making it opens the device's audio client only to read its format, so nothing is recorded or played.
    /// </summary>
    [Test, Platform("Win"), Category("WindowsAudio")]
    [SupportedOSPlatform("windows")]
    public void Windows_capture_of_an_output_device_does_not_deliver_continuously()
    {
        using var enumerator = new MMDeviceEnumerator();
        Assume.That(enumerator.HasDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia), Is.True, "this computer has no output device");

        using var capture = WasapiAudioCapture.ForOutputDevice(enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia), "the default output device", NullLogger.Instance);

        Assert.That(capture.DeliversContinuously, Is.False);
    }

    /// <summary>While nothing is captured, nothing is said about arriving audio: there is no capture for it to come from.</summary>
    [Test]
    public void While_nothing_is_captured_there_is_no_time_since_a_packet()
    {
        using var session = CreateSession(new RecordingOptions { App = "chrome" });   // chrome is not playing

        session.Start(_files);
        _time.Advance(TimeSpan.FromSeconds(10));
        var status = session.GetStatus();
        session.Stop();

        Assert.That(status.WaitingFor, Is.Not.Null);
        Assert.That(status.SinceLastPacket, Is.Null);
        Assert.That(status.DeliversContinuously, Is.False);
        Assert.That(status.RecordedLevel, Is.Null);
    }

    /// <summary>
    /// A capture that failed leaves nothing behind in the status about arriving audio while its replacement cannot be
    /// opened yet: not that it delivered continuously, nor the level of the app the last look picked but could not open.
    /// Only an open capture has such facts.
    /// </summary>
    [Test]
    public async Task A_failed_capture_leaves_no_arrival_facts_behind()
    {
        var chrome = _provider.AddApp(4242, "chrome", "Speakers", level: 0.3f, AppCapture(4242))!;
        using var session = CreateSession(new RecordingOptions { App = "chrome" });

        session.Start(_files);
        var open = session.GetStatus();
        chrome.Fail(new IOException("The app's sound went away."));   // and no capture of it can be opened again yet
        await Wait.UntilAsync(() => _provider.FailedCreations > 0);
        var waiting = session.GetStatus();
        session.Stop();

        Assert.That(open.DeliversContinuously, Is.True);
        Assert.That(open.RecordedLevel, Is.EqualTo(0.3f));
        Assert.That(waiting.WaitingFor, Is.EqualTo("\"chrome\" to play sound"));
        Assert.That(waiting.SinceLastPacket, Is.Null);
        Assert.That(waiting.DeliversContinuously, Is.False);
        Assert.That(waiting.RecordedLevel, Is.Null, "the app still plays at 0.3, but nothing captures it");
    }

    /// <summary>
    /// Audio that arrives during a pause is not kept, but it shows that the capture works: the time since the last packet
    /// does not grow through a pause while audio keeps arriving.
    /// </summary>
    [Test]
    public void Audio_that_arrives_while_paused_still_counts_as_arriving()
    {
        var chrome = _provider.AddApp(4242, "chrome", "Speakers", level: 0.3f, AppCapture(4242))!;
        using var session = CreateSession(new RecordingOptions { App = "chrome" });

        session.Start(_files);
        session.Pause();
        _time.Advance(TimeSpan.FromSeconds(10));
        chrome.Deliver(Samples(160, 0));
        var paused = session.GetStatus();
        var result = session.Stop();

        Assert.That(paused.Paused, Is.True);
        Assert.That(paused.SinceLastPacket, Is.EqualTo(TimeSpan.Zero));
        Assert.That(result.SegmentPaths, Is.Empty, "and none of it was kept");
    }

    /// <summary>The level Windows' meter shows for the recorded device is read again at each look, once a second, and given in the status.</summary>
    [Test]
    public async Task The_recorded_device_s_level_is_read_at_each_look()
    {
        _provider.Enqueue(new FakeAudioCapture(Pcm16KMono, "Speakers"), level: 0.25f);
        using var session = CreateSession();

        session.Start(_files);
        var atStart = session.GetStatus().RecordedLevel;
        _provider.SetLevel("Speakers", 0.5f);
        await TickAsync();
        await Wait.UntilAsync(() => session.GetStatus().RecordedLevel == 0.5f);
        _provider.SetLevel("Speakers", 0.125f);
        await TickAsync();
        await Wait.UntilAsync(() => session.GetStatus().RecordedLevel == 0.125f);
        session.Stop();

        Assert.That(atStart, Is.EqualTo(0.25f), "read by the look at the start");
    }

    /// <summary>
    /// The level Windows' meter shows for the recorded app's sound is read again at each look and given in the status.
    /// Once the app has closed, its capture stays open and the level is not known.
    /// </summary>
    [Test]
    public async Task The_recorded_app_s_level_is_read_at_each_look()
    {
        _provider.ConnectDevice("Speakers", level: 0.9f);
        _provider.AddApp(4242, "chrome", "Speakers", level: 0.3f, AppCapture(4242));
        using var session = CreateSession(new RecordingOptions { App = "chrome" });

        session.Start(_files);
        var atStart = session.GetStatus().RecordedLevel;
        _provider.SetAppLevel(4242, 0.6f);
        await TickAsync();
        await Wait.UntilAsync(() => session.GetStatus().RecordedLevel == 0.6f);
        _provider.RemoveApp(4242);                           // the app closes
        await TickAsync();
        await Wait.UntilAsync(() => session.GetStatus().RecordedLevel is null);
        var afterItClosed = session.GetStatus();
        session.Stop();

        Assert.That(atStart, Is.EqualTo(0.3f), "the app's own level, not its device's");
        Assert.That(afterItClosed.WaitingFor, Is.Null, "its capture is still open");
    }

    /// <summary>
    /// The looks made while paused still look at the devices, as they follow the sound to another device during a break
    /// (see <see cref="Sound_moving_to_another_device_is_followed_while_paused"/>), so the level Windows' meter shows for
    /// the recorded device is read at each of them too, and given in the status as at any other time.
    /// </summary>
    [Test]
    public async Task While_paused_the_level_is_still_read_at_each_look()
    {
        _provider.Enqueue(new FakeAudioCapture(Pcm16KMono, "Speakers"), level: 0.25f);
        using var session = CreateSession();

        session.Start(_files);
        session.Pause();
        _provider.SetLevel("Speakers", 0.5f);
        await TickAsync();                                   // a look while paused
        await Wait.UntilAsync(() => session.GetStatus().RecordedLevel == 0.5f);
        var paused = session.GetStatus();
        session.Stop();

        Assert.That(paused.Paused, Is.True);
        Assert.That(paused.RecordedLevel, Is.EqualTo(0.5f));
    }

    /// <summary>
    /// A look that fails, as Windows' listings do while its audio service restarts, leaves no level in the status rather
    /// than the one an earlier look read: what plays now is not known. The capture stays open meanwhile, and the next look
    /// that works reads the level again.
    /// </summary>
    [Test]
    public async Task A_look_that_fails_leaves_no_level()
    {
        _provider.Enqueue(new FakeAudioCapture(Pcm16KMono, "Speakers"), level: 0.4f);
        using var session = CreateSession();

        session.Start(_files);
        var before = session.GetStatus();
        _provider.FailListings(new COMException("The audio service is restarting."));
        await TickAsync();
        await Wait.UntilAsync(() => _log.Problems.Any(problem => problem.StartsWith("Cannot capture")));   // the look has failed
        var failed = session.GetStatus();
        _provider.FailListings(null);
        await TickAsync();
        await Wait.UntilAsync(() => session.GetStatus().RecordedLevel is not null);
        var after = session.GetStatus();
        session.Stop();

        Assert.That(before.RecordedLevel, Is.EqualTo(0.4f));
        Assert.That(failed.WaitingFor, Is.Null, "the capture is still open");
        Assert.That(failed.RecordedLevel, Is.Null);
        Assert.That(after.RecordedLevel, Is.EqualTo(0.4f), "read again by the next look that works");
    }

    /// <summary>
    /// A capture that is still open but has stopped delivering is replaced, on request, at the next look: not at once,
    /// and in a new part, as after a failure. The reason is written to the log. It works the same during a pause, so
    /// that the recording can go on once the pause ends.
    /// </summary>
    [TestCase(false)]
    [TestCase(true)]
    public async Task Reopen_replaces_the_capture_at_the_next_look(bool paused)
    {
        var first = _provider.AddApp(4242, "chrome", "Speakers", level: 0.3f, AppCapture(4242))!;
        var second = _provider.AddApp(4242, "chrome", "Speakers", level: 0.3f, AppCapture(4242))!;   // ready to open the app again
        using var session = CreateSession(new RecordingOptions { App = "chrome" });

        session.Start(_files);
        first.Deliver(TestAudio.Shorts(1, 2));
        if (paused) session.Pause();
        session.Reopen("no audio has arrived for 5 seconds");
        await Task.Delay(100);                               // a capture replaced before the next look would show up meanwhile
        var beforeTheLook = second.StartEntered;
        await TickAsync();
        await Wait.UntilAsync(() => second.Started);
        var reopened = session.GetStatus();
        second.Deliver(TestAudio.Shorts(9, 9));              // kept unless paused
        if (paused) session.Resume();
        second.Deliver(TestAudio.Shorts(3, 4));
        var result = session.Stop();

        Assert.That(beforeTheLook, Is.False, "replaced at the next look, not at once");
        Assert.That(first.DisposeCalls, Is.EqualTo(1), "the old capture is released once");
        Assert.That(reopened.SinceLastPacket, Is.EqualTo(TimeSpan.Zero), "the new capture counts from its own start");
        Assert.That(result.SegmentPaths, Has.Count.EqualTo(2), "the new capture records into a new part");
        Assert.That(TestAudio.ReadWav(result.SegmentPaths[0]).Samples, Is.EqualTo(new short[] { 1, 2 }));
        Assert.That(TestAudio.ReadWav(result.SegmentPaths[1]).Samples, Is.EqualTo(paused ? new short[] { 3, 4 } : new short[] { 9, 9, 3, 4 }));
        Assert.That(_log.Messages, Has.Some.Contains("no audio has arrived for 5 seconds"));
        Assert.That(_log.Problems, Is.Empty, "the caller warns; for the session this is routine");
    }

    /// <summary>
    /// A reopen is for the capture it was asked for. When that capture is replaced for another reason first, here because
    /// it failed, the capture that replaced it is not replaced again for it.
    /// </summary>
    [Test]
    public async Task A_reopen_is_for_the_capture_it_was_asked_for()
    {
        var first = _provider.AddApp(4242, "chrome", "Speakers", level: 0.3f, AppCapture(4242))!;
        var second = _provider.AddApp(4242, "chrome", "Speakers", level: 0.3f, AppCapture(4242))!;
        var spare = _provider.AddApp(4242, "chrome", "Speakers", level: 0.3f, AppCapture(4242))!;
        using var session = CreateSession(new RecordingOptions { App = "chrome" });

        session.Start(_files);
        session.Reopen("no audio has arrived for 5 seconds");
        first.Fail(new IOException("The app's sound went away."));   // replaced at once, before the next look
        await Wait.UntilAsync(() => second.Started);
        for (var look = 0; look < 3; look++)
        {
            await TickAsync();
            await Task.Delay(50);                            // a replacement would show up meanwhile
        }

        second.Deliver(TestAudio.Shorts(5, 6));
        var result = session.Stop();

        Assert.That(spare.StartEntered, Is.False);
        Assert.That(second.DisposeCalls, Is.EqualTo(1), "released by the stop alone");
        Assert.That(TestAudio.ReadWav(result.SegmentPaths.Single()).Samples, Is.EqualTo(new short[] { 5, 6 }));
    }

    /// <summary>A reopen asked for while nothing is captured, or after the recording has stopped, changes nothing.</summary>
    [Test]
    public async Task A_reopen_with_no_capture_open_changes_nothing()
    {
        using var session = CreateSession(new RecordingOptions { Device = "Pixel Buds" });   // not connected yet

        session.Start(_files);
        session.Reopen("no audio has arrived for 5 seconds");
        var buds = _provider.Enqueue(new FakeAudioCapture(Pcm16KMono, "Pixel Buds"));
        var spare = _provider.Enqueue(new FakeAudioCapture(Pcm16KMono, "Pixel Buds"));
        await TickUntilAsync(() => buds.Started);            // found by the next look
        for (var look = 0; look < 2; look++)
        {
            await TickAsync();
            await Task.Delay(50);                            // a replacement would show up meanwhile
        }

        buds.Deliver(TestAudio.Shorts(1, 2));
        var result = session.Stop();
        session.Reopen("no audio has arrived for 5 seconds");
        await Task.Delay(100);

        Assert.That(spare.StartEntered, Is.False, "the open capture was not asked about, so it is kept");
        Assert.That(_provider.Created, Is.EqualTo(new[] { buds }), "and nothing is opened after the stop");
        Assert.That(TestAudio.ReadWav(result.SegmentPaths.Single()).Samples, Is.EqualTo(new short[] { 1, 2 }));
    }

    /// <summary>
    /// A session recording the class alone, as today, that notes a stretch of at least <paramref name="noteGapsLongerThan"/>
    /// with nothing playing, made through the constructor that names the track and its placement.
    /// </summary>
    private RecordingSession CreateContiguousSession(TimeSpan noteGapsLongerThan)
    {
        var settings = TestOptions.Create(new RecordingOptions());
        var source = new LoopbackSource(_provider, new CaptureChooser(_time, new ClassSource.Automatic(), _chooserLog));
        return new RecordingSession(source, new TrackSpec(Track.Main, TrackPlacement.Contiguous, noteGapsLongerThan), null, settings, _time, _log);
    }

    /// <summary>A packet's stamp <paramref name="seconds"/> after the test clock's zero, in its units of 100 nanoseconds.</summary>
    private static long Stamp(double seconds) => (long)Math.Round(seconds * TimeSpan.TicksPerSecond);

    /// <summary>Mono 16-bit audio at 16 kHz lasting <paramref name="milliseconds"/>, of one sample value.</summary>
    private static byte[] Audio16K(short value, int milliseconds) => Samples(16 * milliseconds, value);

    /// <summary>
    /// In a recording of the class alone, silence while nothing plays is not written, as ever; but where the stamps show
    /// that at least the given time passed between one packet's end and the next packet, a note says about how much was
    /// left out, at the position the recording had reached, so that the transcript can show it.
    /// </summary>
    [Test]
    public void A_long_stretch_with_nothing_playing_is_noted_where_it_was_left_out()
    {
        var capture = _provider.Enqueue(new FakeAudioCapture(Pcm16KMono));
        using var session = CreateContiguousSession(TimeSpan.FromSeconds(60));
        var notes = new List<TimelineNote>();
        session.NoteWorthy += (_, note) => notes.Add(note);

        session.Start(_files);
        capture.Deliver(Audio16K(1, 500), Stamp(10));            // ends at 10.5 s
        capture.Deliver(Audio16K(2, 500), Stamp(10.5));          // follows on: nothing left out
        capture.Deliver(Audio16K(3, 500), Stamp(201));           // 190 s later: about 3 minutes left out
        capture.Deliver(Audio16K(4, 500), Stamp(261.5));         // 60 s later: just long enough
        var result = session.Stop();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Duration, Is.EqualTo(TimeSpan.FromSeconds(2)), "nothing was written for the gaps");
            Assert.That(notes, Has.Count.EqualTo(2));
            Assert.That(notes[0], Is.EqualTo(new TimelineNote(TimeSpan.FromSeconds(1), "About 3 minutes with nothing playing were left out here")));
            Assert.That(notes[1], Is.EqualTo(new TimelineNote(TimeSpan.FromSeconds(1.5), "About 1 minute with nothing playing was left out here")));
        }
    }

    /// <summary>A stretch shorter than the setting is not noted, and a setting of zero turns the notes off altogether.</summary>
    [TestCase(60, 59.9, 0)]
    [TestCase(0, 600, 0)]
    [TestCase(10, 45, 1)]
    public void Shorter_stretches_and_a_setting_of_zero_give_no_note(int noteFromSeconds, double gapSeconds, int expectedNotes)
    {
        var capture = _provider.Enqueue(new FakeAudioCapture(Pcm16KMono));
        using var session = CreateContiguousSession(TimeSpan.FromSeconds(noteFromSeconds));
        var notes = new List<TimelineNote>();
        session.NoteWorthy += (_, note) => notes.Add(note);

        session.Start(_files);
        capture.Deliver(Audio16K(1, 100), Stamp(10));
        capture.Deliver(Audio16K(2, 100), Stamp(10.1 + gapSeconds));
        session.Stop();

        Assert.That(notes, Has.Count.EqualTo(expectedNotes));
        if (expectedNotes > 0) Assert.That(notes[0].Text, Is.EqualTo("About 45 seconds with nothing playing were left out here"));
    }

    /// <summary>The time paused with P is not a stretch with nothing playing: the first packet after the pause starts the count afresh.</summary>
    [Test]
    public void A_pause_is_not_noted_as_nothing_playing()
    {
        var capture = _provider.Enqueue(new FakeAudioCapture(Pcm16KMono));
        using var session = CreateContiguousSession(TimeSpan.FromSeconds(60));
        var notes = new List<TimelineNote>();
        session.NoteWorthy += (_, note) => notes.Add(note);

        session.Start(_files);
        capture.Deliver(Audio16K(1, 100), Stamp(10));
        session.Pause();
        capture.Deliver(Audio16K(2, 100), Stamp(100));           // dropped: paused
        session.Resume();
        capture.Deliver(Audio16K(3, 100), Stamp(900));
        capture.Deliver(Audio16K(4, 100), Stamp(1000));          // but after the pause the count runs again
        session.Stop();

        Assert.That(notes, Has.Count.EqualTo(1));
        Assert.That(notes[0].Text, Does.StartWith("About 2 minutes"));
    }

    /// <summary>Packets without a stamp, as a capture that gives none hands over, tell nothing about time, so they are never noted.</summary>
    [Test]
    public void Packets_without_stamps_give_no_note()
    {
        var capture = _provider.Enqueue(new FakeAudioCapture(Pcm16KMono));
        using var session = CreateContiguousSession(TimeSpan.FromSeconds(60));
        var notes = new List<TimelineNote>();
        session.NoteWorthy += (_, note) => notes.Add(note);

        session.Start(_files);
        capture.Deliver(Audio16K(1, 100));
        _time.Advance(TimeSpan.FromMinutes(10));
        capture.Deliver(Audio16K(2, 100));
        session.Stop();

        Assert.That(notes, Is.Empty);
    }

    /// <summary>
    /// The status says when the capture has handed over nothing but exact zeros for two minutes, as a microphone muted in
    /// Windows or blocked by its privacy settings gives, and stops saying so as soon as any other sample arrives.
    /// </summary>
    [Test]
    public void The_status_says_when_only_exact_zeros_have_arrived_for_two_minutes()
    {
        var capture = _provider.Enqueue(new FakeAudioCapture(Pcm16KMono) { DeliversContinuously = true });
        using var session = CreateSession();

        session.Start(_files);
        var beforeAnything = session.GetStatus();
        capture.Deliver(TestAudio.Shorts(0, 0, 0));
        _time.Advance(TimeSpan.FromSeconds(119));
        capture.Deliver(TestAudio.Shorts(0, 0, 0));
        var afterTwoMinutesLessASecond = session.GetStatus();
        _time.Advance(TimeSpan.FromSeconds(1));
        capture.Deliver(TestAudio.Shorts(0, 0, 0));
        var afterTwoMinutes = session.GetStatus();
        capture.Deliver(TestAudio.Shorts(0, 1, 0));
        var afterASample = session.GetStatus();
        session.Stop();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(beforeAnything.OnlyZeros, Is.False, "nothing has arrived yet");
            Assert.That(afterTwoMinutesLessASecond.OnlyZeros, Is.False);
            Assert.That(afterTwoMinutes.OnlyZeros, Is.True);
            Assert.That(afterASample.OnlyZeros, Is.False, "a sample of 1 is not an exact zero, whatever the sound threshold says");
        }
    }

    /// <summary>A capture that stayed silent and was then replaced counts its zeros afresh: the time is the new capture's.</summary>
    [Test]
    public async Task A_new_capture_counts_its_zeros_afresh()
    {
        var first = _provider.Enqueue(new FakeAudioCapture(Pcm16KMono, "Speakers") { DeliversContinuously = true });
        using var session = CreateSession();

        session.Start(_files);
        first.Deliver(TestAudio.Shorts(0, 0));
        _time.Advance(TimeSpan.FromMinutes(3));
        first.Deliver(TestAudio.Shorts(0, 0));
        var before = session.GetStatus();
        var second = _provider.Enqueue(new FakeAudioCapture(Pcm16KMono, "Speakers") { DeliversContinuously = true });
        first.Fail(new IOException("gone"));
        await TickUntilAsync(() => second.Started);
        second.Deliver(TestAudio.Shorts(0, 0));
        var after = session.GetStatus();
        session.Stop();

        Assert.That(before.OnlyZeros, Is.True);
        Assert.That(after.OnlyZeros, Is.False);
    }
}
