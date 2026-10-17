using System.Runtime.Versioning;
using ClassScribe.Audio;
using ClassScribe.Audio.Microphones;
using ClassScribe.Audio.Timeline;
using ClassScribe.Audio.Windows;
using ClassScribe.Commands;
using ClassScribe.Recorder;
using ClassScribe.Settings;
using ClassScribe.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace ClassScribe.Tests;

/// <summary>
/// The track set: the class and your microphone recorded together on one clock, or your microphone alone in person, as
/// the record command and the recording loop drive it; and its factory, which makes the recording a run's source plan
/// asks for. The devices, the microphones, the apps' microphone sessions and the computer's clocks are simulated, and
/// the clock is moved by hand, starting ten seconds in. Packets are mono 16-bit, stamped in units of 100 nanoseconds on
/// the test's clock, as Windows stamps them.
/// </summary>
[TestFixture]
public sealed class TrackSetTests
{
    private const int Rate = 48_000;
    private static readonly AudioFormat Pcm48KMono = new(48000, 1, 16, false);
    private static readonly AudioFormat Pcm16KMono = new(16000, 1, 16, false);
    private static readonly AudioFormat Unreadable = new(48000, 1, 8, false);
    private static readonly DateTimeOffset Start = new(2026, 10, 17, 19, 0, 0, TimeSpan.Zero);
    private static readonly Microphone Array = LaptopMicrophones.Array;
    private static readonly Microphone Obsbot = LaptopMicrophones.Obsbot;

    private TempFolder _folder = null!;
    private RecordingFiles _files = null!;
    private FakeCaptureProvider _class = null!;
    private FakeMicrophoneProvider _microphones = null!;
    private FakeMicrophoneSessions _sessions = null!;
    private FakeMicrophoneUsageRecord _record = null!;
    private FakeProcessNames _names = null!;
    private ManualTimeProvider _time = null!;
    private FakeSystemTimes _times = null!;
    private FreezeWatch _freezes = null!;
    private RecordingClock _clock = null!;
    private TestLoggerFactory _logs = null!;

    /// <summary>A temporary folder and a recording in it, the simulated devices, microphones, sessions and clocks, and a recording clock on the test's clock, ten seconds in.</summary>
    [SetUp]
    public void SetUp()
    {
        _folder = new TempFolder();
        _files = new RecordingFiles(_folder.Path, "Class_2026-10-17_19-00-00Z");
        _class = new FakeCaptureProvider();
        _microphones = new FakeMicrophoneProvider().Connect(Array);
        _sessions = new FakeMicrophoneSessions();
        _record = new FakeMicrophoneUsageRecord();
        _names = new FakeProcessNames();
        _time = new ManualTimeProvider(Start);
        _time.Advance(TimeSpan.FromSeconds(10));
        _times = new FakeSystemTimes(_time);
        _freezes = new FreezeWatch(_times, _time, NullLogger<FreezeWatch>.Instance);
        _clock = new RecordingClock(_time);
        _logs = new TestLoggerFactory();
    }

    /// <summary>Deletes the temporary folder, which fails if a test left a file open.</summary>
    [TearDown]
    public void TearDown() => _folder.Dispose();

    /// <summary>The factory under test, over the simulated devices and microphones, with <paramref name="options"/> or the default settings, and <paramref name="transcript"/> or the default transcript settings.</summary>
    private TrackSetFactory Factory(RecordingOptions? options = null, TranscriptOptions? transcript = null) =>
        new(_class, _microphones, _sessions, _record, _names, new FakeOwnProcesses(Laptop.OwnProcess), _freezes,
            TestOptions.Create(options ?? new RecordingOptions()), TestOptions.Create(transcript ?? new TranscriptOptions()), _time, _logs);

    /// <summary>A plan for the class and your microphone, from <paramref name="source"/> or the device the class plays on, following <paramref name="follows"/> when given.</summary>
    private static SourcePlan ClassAndMicrophone(ClassSource? source = null, FollowRule? follows = null, string? microphone = null) =>
        new(RecordingMode.ClassAndMicrophone, source ?? new ClassSource.Automatic(), new MicrophoneChoice(microphone, follows));

    /// <summary>A plan for your microphone alone, in person.</summary>
    private static SourcePlan InPersonPlan(string? microphone = null) => new(RecordingMode.Microphone, Class: null, new MicrophoneChoice(microphone));

    /// <summary>
    /// A recording of the class and your microphone on <paramref name="clock"/> or the test's, following <paramref name="follows"/>,
    /// or the setting <paramref name="setting"/> names when none is given, with the class app's mute <paramref name="appMute"/>.
    /// </summary>
    private TrackSet WithClass(FollowRule? follows = null, ClassAppMute appMute = ClassAppMute.Unknown, string setting = "chrome", ClassSource? source = null,
        RecordingClock? clock = null) =>
        (TrackSet)Factory(new RecordingOptions { MicrophoneFollows = setting, ClassAppMute = appMute }).Create(ClassAndMicrophone(source, follows), clock ?? _clock);

    /// <summary>A recording of the class and your microphone with V alone switching the microphone: no app is followed, and the microphone starts on.</summary>
    private TrackSet VOnly(RecordingClock? clock = null) => WithClass(new FollowRule.None(), clock: clock);

    /// <summary>A recording of the class and your microphone following chrome, whose mute Windows can see, so the microphone starts on and the watch runs.</summary>
    private TrackSet FollowingChrome() => WithClass(new FollowRule.Named("chrome"), ClassAppMute.Visible);

    /// <summary>A recording of your microphone alone, in person.</summary>
    private TrackSet InPerson(string? microphone = null) => (TrackSet)Factory().Create(InPersonPlan(microphone), _clock);

    /// <summary>Connects the speakers and queues a capture of them, in <paramref name="format"/> or 48 kHz mono.</summary>
    private FakeAudioCapture ClassCapture(AudioFormat? format = null) => _class.Enqueue(new FakeAudioCapture(format ?? Pcm48KMono, "Speakers"));

    /// <summary>Queues the next capture of a microphone, <paramref name="microphone"/> or the array, in <paramref name="format"/> or 48 kHz mono, which hands over audio all the time as a microphone does.</summary>
    private FakeAudioCapture MicrophoneCapture(Microphone? microphone = null, AudioFormat? format = null)
    {
        var recorded = microphone ?? Array;
        return _microphones.Prepare(new FakeAudioCapture(format ?? Pcm48KMono, recorded.Name, recorded.Id) { DeliversContinuously = true });
    }

    /// <summary>Chrome's audio service holding an active session on <paramref name="endpointId"/>: the app is listening on that microphone.</summary>
    private void ChromeListensOn(string endpointId) => _sessions.SetSessions(endpointId, Laptop.Session(endpointId, Laptop.ChromeUtility));

    /// <summary>Now on the test's clock, in the units packets are stamped in.</summary>
    private long Now => _time.GetTimestamp();

    /// <summary>A length of <paramref name="seconds"/>, rounded to the unit.</summary>
    private static TimeSpan Seconds(double seconds) => new((long)Math.Round(seconds * TimeSpan.TicksPerSecond));

    /// <summary>Mono 16-bit audio of one sample value lasting <paramref name="milliseconds"/> at <paramref name="rate"/>: a packet.</summary>
    private static byte[] Packet(short value, int milliseconds = 10, int rate = Rate) =>
        TestAudio.Shorts(Enumerable.Repeat(value, rate * milliseconds / 1000).ToArray());

    /// <summary>How long the files hold together, from what is on disk.</summary>
    private static TimeSpan LengthOf(IReadOnlyList<string> parts) => parts.Aggregate(TimeSpan.Zero, (total, part) => total + ClassFolders.MeasureSegment(part));

    /// <summary>The samples of the files joined in order.</summary>
    private static short[] SamplesOf(IReadOnlyList<string> parts) => parts.SelectMany(part => TestAudio.ReadWav(part).Samples).ToArray();

    /// <summary>The sample index <paramref name="position"/> lands on at 48 kHz.</summary>
    private static int At(TimeSpan position) => (int)MediaTime.ToFrames(position, Rate);

    /// <summary>Counts the looks of the recording's watch from now on, as they arrive on its thread.</summary>
    private static Func<int> CountLooks(TrackSet recording)
    {
        var looks = 0;
        recording.Watch!.Looked += (_, _) => Interlocked.Increment(ref looks);
        return () => Volatile.Read(ref looks);
    }

    /// <summary>Moves the clock on by the watch's interval and waits for the look that follows, as it arrives on the watch's thread.</summary>
    private async Task NextLookAsync(Func<int> looks)
    {
        var before = looks();
        _time.Advance(ClassMicrophoneWatch.Interval);
        await Wait.UntilAsync(() => looks() > before);
    }

    /// <summary>In person the microphone is the main track: its files carry the recording's plain names, there is no microphone track beside it, and the snapshot names it as the one source.</summary>
    [Test]
    public void In_person_the_files_carry_the_recordings_plain_names()
    {
        var microphone = MicrophoneCapture();
        using var recording = InPerson();

        recording.Start(_files, TimeSpan.Zero);
        microphone.Deliver(Packet(700), Now);
        var snapshot = recording.GetSnapshot();
        var summary = recording.Stop();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(summary.MainSegments, Is.EqualTo(new[] { _files.Segment(1) }));
            Assert.That(summary.MicrophoneSegments, Is.Empty);
            Assert.That(File.Exists(_files.Segment(1, Track.Microphone)), Is.False, "no .mic.wav in person");
            Assert.That(summary.HeardAnything, Is.True);
            Assert.That(snapshot.Main.Source, Is.EqualTo(Array.Name));
            Assert.That(snapshot.Microphone, Is.Null, "in person the microphone is the main track");
            Assert.That(recording.Follows, Is.Null, "no app is followed in person");
            Assert.That(recording.Watch, Is.Null);
            Assert.That(recording.Gate, Is.Not.Null, "a gate for no app counts the time on and off with V");
            Assert.That(recording.Microphone, Is.EqualTo(Array));
            Assert.That(_logs.MessagesOf<TrackSet>(), Has.Some.Contains("1 part, 0 corrections for drift").And.None.Contains("microphone file"),
                "in person the microphone's files are the recording's parts");
        }
    }

    /// <summary>With the class, your microphone writes the .mic.wav files beside the class's .wav files, and the summary carries both.</summary>
    [Test]
    public void With_the_class_the_microphone_writes_its_own_files_beside_the_classs()
    {
        var speakers = ClassCapture();
        var microphone = MicrophoneCapture();
        using var recording = VOnly();

        recording.Start(_files, TimeSpan.Zero);
        speakers.Deliver(Packet(7), Now);
        microphone.Deliver(Packet(9), Now);
        var snapshot = recording.GetSnapshot();
        var summary = recording.Stop();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(summary.MainSegments, Is.EqualTo(new[] { _files.Segment(1) }));
            Assert.That(summary.MicrophoneSegments, Is.EqualTo(new[] { _files.Segment(1, Track.Microphone) }));
            Assert.That(snapshot.Main.Source, Is.EqualTo("Speakers"));
            Assert.That(snapshot.Microphone?.Source, Is.EqualTo(Array.Name));
            Assert.That(snapshot.Microphone?.Capturing, Is.True);
            Assert.That(recording.Microphone, Is.EqualTo(Array));
            Assert.That(recording.Processing, Is.EqualTo(MicrophoneProcessing.Raw), "the processing the capture was opened with");
        }
    }

    /// <summary>
    /// The tracks end together: at the stop each is padded to the one position taken from the clock when the stop began,
    /// so a class track at 48 kHz that played on and a microphone track at 16 kHz that went quiet are the same length
    /// within 50 ms, even when the class's driver takes its time releasing the capture and the clock moves on three
    /// seconds meanwhile: the class track ends at the stop's position, not at the later moment its release returned.
    /// </summary>
    [Test]
    public async Task Both_tracks_end_together()
    {
        using var windows = new ManualResetEventSlim();
        var speakers = _class.Enqueue(new FakeAudioCapture(Pcm48KMono, "Speakers") { DisposeGate = windows });
        var microphone = MicrophoneCapture(format: Pcm16KMono);
        using var recording = VOnly();
        try
        {
            recording.Start(_files, TimeSpan.Zero);
            speakers.Deliver(Packet(7), Now);
            microphone.Deliver(Packet(9, rate: 16_000), Now);
            _time.Advance(TimeSpan.FromSeconds(1));
            speakers.Deliver(Packet(7), Now);
            _time.Advance(TimeSpan.FromSeconds(2));

            var stopping = Task.Run(recording.Stop);
            await Wait.UntilAsync(() => microphone.Disposed);        // the microphone's track has stopped
            await Wait.UntilAsync(() => speakers.DisposeCalls == 1); // while the class's release is held inside Windows
            _time.Advance(TimeSpan.FromSeconds(3));                  // and the clock moves on meanwhile
            windows.Set();
            var summary = await stopping.WaitAsync(TimeSpan.FromSeconds(10));

            var main = LengthOf(summary.MainSegments);
            var mic = LengthOf(summary.MicrophoneSegments);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(main, Is.EqualTo(TimeSpan.FromSeconds(3)).Within(TimeSpan.FromMilliseconds(1)), "padded to the position when the stop began, not when the release returned");
                Assert.That((main - mic).Duration(), Is.LessThanOrEqualTo(TimeSpan.FromMilliseconds(50)));
                Assert.That(summary.Length, Is.EqualTo(main));
            }
        }
        finally
        {
            windows.Set();
        }
    }

    /// <summary>
    /// A mark gets the position on the clock: the offset plus the time since the first packet of any track, not the audio
    /// written, which the timer keeps a little behind the clock (here padded up to the offset, as the files held nothing
    /// before). Before the first packet the position is the offset the recording started at.
    /// </summary>
    [Test]
    public void A_mark_gets_the_position_on_the_clock()
    {
        var speakers = ClassCapture();
        MicrophoneCapture();
        using var recording = VOnly();

        recording.Start(_files, TimeSpan.FromMinutes(47));
        var before = recording.Position;
        speakers.Deliver(Packet(7), Now);
        _time.Advance(TimeSpan.FromSeconds(3));
        var position = recording.Position;
        var snapshot = recording.GetSnapshot();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(before, Is.EqualTo(TimeSpan.FromMinutes(47)), "the offset until the first packet");
            Assert.That(position, Is.EqualTo(TimeSpan.FromMinutes(47) + TimeSpan.FromSeconds(3)));
            Assert.That(snapshot.Position, Is.EqualTo(position));
            Assert.That(snapshot.Main.Written, Is.LessThan(position).And.GreaterThan(TimeSpan.FromMinutes(47)), "the audio written lags the clock a little");
        }
    }

    /// <summary>
    /// Records the class with the microphone and returns the class's file, with the microphone's capture refused by
    /// Windows' privacy settings, failing after its first audio, or working, as asked. Each run gets a folder and a clock
    /// of its own and the same packets at the same moments.
    /// </summary>
    private async Task<byte[]> ClassFileAsync(bool microphoneRefused = false, bool microphoneFailsMidRun = false)
    {
        using var folder = new TempFolder();
        var files = new RecordingFiles(folder.Path, "Class_2026-10-17_19-00-00Z");
        var speakers = ClassCapture();
        _microphones.CaptureFailure = microphoneRefused ? LaptopMicrophones.AccessDenied() : null;
        var microphone = microphoneRefused ? null : MicrophoneCapture();
        using var recording = VOnly(new RecordingClock(_time));

        recording.Start(files, TimeSpan.Zero);
        speakers.Deliver(Packet(7), Now);
        microphone?.Deliver(Packet(9), Now);
        if (microphoneFailsMidRun)
        {
            microphone!.Fail(new IOException("The microphone was unplugged."));
            await Wait.UntilAsync(() => microphone.Disposed);
        }

        _time.Advance(TimeSpan.FromMilliseconds(500));
        speakers.Deliver(Packet(8), Now);
        _time.Advance(TimeSpan.FromMilliseconds(10));
        var summary = recording.Stop();
        return File.ReadAllBytes(summary.MainSegments.Single());
    }

    /// <summary>A microphone Windows refuses at the start, or one that fails mid-run, leaves the class's file exactly as it would be with a working microphone: the tracks are recorded apart.</summary>
    [Test]
    public async Task The_microphone_failing_leaves_the_class_file_byte_identical()
    {
        var working = await ClassFileAsync();
        var refused = await ClassFileAsync(microphoneRefused: true);
        var failed = await ClassFileAsync(microphoneFailsMidRun: true);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(refused, Is.EqualTo(working));
            Assert.That(failed, Is.EqualTo(working));
            Assert.That(_logs.Problems, Has.Some.Contains("Could not start capturing the microphone yet"));
        }
    }

    /// <summary>
    /// Records the class with the microphone and returns the microphone's file, its length on disk and the summary, with
    /// the class's device in a format ClassScribe cannot read, or working, as asked. The microphone's packet comes first,
    /// so it sets the origin either way. The length is read before the folder goes.
    /// </summary>
    private (byte[] File, TimeSpan OnDisk, RecordingSummary Summary) MicrophoneFile(bool classUnreadable)
    {
        using var folder = new TempFolder();
        var files = new RecordingFiles(folder.Path, "Class_2026-10-17_19-00-00Z");
        var speakers = ClassCapture(classUnreadable ? Unreadable : null);
        var microphone = MicrophoneCapture();
        using var recording = VOnly(new RecordingClock(_time));

        recording.Start(files, TimeSpan.Zero);
        microphone.Deliver(Packet(9), Now);
        if (!classUnreadable) speakers.Deliver(Packet(7), Now);
        _time.Advance(TimeSpan.FromMilliseconds(500));
        microphone.Deliver(Packet(8), Now);
        _time.Advance(TimeSpan.FromMilliseconds(10));
        var summary = recording.Stop();
        return (File.ReadAllBytes(summary.MicrophoneSegments.Single()), LengthOf(summary.MicrophoneSegments), summary);
    }

    /// <summary>
    /// The reverse: a class device ClassScribe cannot read leaves the microphone's file exactly as it would be with the
    /// class recorded, and the start does not fail. The summary's length is then the microphone track's, the longer one,
    /// since the class recorded nothing.
    /// </summary>
    [Test]
    public void The_class_failing_leaves_the_microphone_file_byte_identical()
    {
        var (working, _, _) = MicrophoneFile(classUnreadable: false);
        var (unreadable, onDisk, summary) = MicrophoneFile(classUnreadable: true);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(unreadable, Is.EqualTo(working));
            Assert.That(summary.MainSegments, Is.Empty, "the class recorded nothing");
            Assert.That(onDisk, Is.GreaterThan(TimeSpan.FromMilliseconds(500)));
            Assert.That(summary.Length, Is.EqualTo(onDisk), "the longer track's length: the microphone's");
        }
    }

    /// <summary>
    /// A microphone in a format ClassScribe cannot read is logged, with the class still being recorded, and the run goes
    /// on with the class alone: the summary has the class's file and no microphone file. The watch, started when the
    /// capture opened, is stopped again, and the recording no longer describes the microphone: there is no gate to sum
    /// up, no microphone and no processing to name, and V says there is no microphone.
    /// </summary>
    [Test]
    public async Task A_microphone_that_cannot_be_recorded_does_not_stop_the_class()
    {
        ChromeListensOn(Array.Id);
        var speakers = ClassCapture();
        var microphone = MicrophoneCapture(format: Unreadable);
        using var recording = FollowingChrome();
        var looks = CountLooks(recording);

        recording.Start(_files, TimeSpan.Zero);
        var looksAtStart = looks();
        var vOn = recording.SetMicrophone(true);
        speakers.Deliver(Packet(7), Now);
        _time.Advance(TimeSpan.FromSeconds(1));
        await Task.Delay(50);
        var looksLater = looks();
        var summary = recording.Stop();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_logs.Problems, Has.Some.Contains("The microphone cannot be recorded").And.Some.Contains("the class is still being recorded"));
            Assert.That(summary.MainSegments, Has.Count.EqualTo(1));
            Assert.That(summary.MicrophoneSegments, Is.Empty);
            Assert.That(summary.HeardAnything, Is.False, "7 is below the sound threshold");
            Assert.That(microphone.Disposed, Is.True, "the capture that could not be read is released");
            Assert.That(File.Exists(_files.Segment(1, Track.Microphone)), Is.False, "no microphone file is written, not even silence");
            Assert.That(looksAtStart, Is.LessThanOrEqualTo(1), "at most the first look, before the format was refused");
            Assert.That(looksLater, Is.EqualTo(looksAtStart), "the watch looks no more for a track that is left out");
            Assert.That(recording.Gate, Is.Null, "no gate to sum up a track that was not recorded");
            Assert.That(recording.Microphone, Is.Null);
            Assert.That(recording.Processing, Is.Null);
            Assert.That(vOn, Is.Null, "V has no microphone to switch");
            Assert.That(_microphones.Opened, Has.Count.EqualTo(1), "V opened nothing");
        }
    }

    /// <summary>Only when neither track can be recorded does the start fail, and both failures are in the log; the stop that follows sums nothing up.</summary>
    [Test]
    public void Both_tracks_unreadable_fail_the_start()
    {
        ClassCapture(Unreadable);
        MicrophoneCapture(format: Unreadable);
        using var recording = VOnly();

        Assert.Throws<NotSupportedException>(() => recording.Start(_files, TimeSpan.Zero));
        var summary = recording.Stop();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_logs.Problems, Has.Some.Contains("The class cannot be recorded").And.Some.Contains("The microphone cannot be recorded"));
            Assert.That(summary.MainSegments, Is.Empty);
            Assert.That(summary.MicrophoneSegments, Is.Empty);
            Assert.That(summary.Length, Is.EqualTo(TimeSpan.Zero));
            Assert.That(_logs.MessagesOf<TrackSet>(), Has.None.StartsWith("Recorded "), "nothing was recorded, so there is nothing to sum up");
        }
    }

    /// <summary>A recording is started once; a second start is refused.</summary>
    [Test]
    public void A_recording_is_started_once()
    {
        ClassCapture();
        MicrophoneCapture();
        using var recording = VOnly();

        recording.Start(_files, TimeSpan.Zero);

        Assert.Throws<InvalidOperationException>(() => recording.Start(_files, TimeSpan.Zero));
    }

    /// <summary>
    /// The tracks stop in parallel, each bounded by its session's own patience: when Windows holds up a look at the class's
    /// devices for good, the microphone's track stops at once, and the whole stop answers once the class's patience has
    /// run out, with both tracks' files in the summary.
    /// </summary>
    [Test]
    public async Task A_parallel_stop_stays_bounded_when_one_driver_hangs()
    {
        using var windows = new ManualResetEventSlim();
        var speakers = ClassCapture();
        var microphone = MicrophoneCapture();
        using var recording = VOnly();
        try
        {
            recording.Start(_files, TimeSpan.Zero);
            speakers.Deliver(Packet(7), Now);
            microphone.Deliver(Packet(9), Now);
            _class.HoldNextListing(windows);                     // the next look at the class's devices is held up inside Windows
            _class.RaiseDevicesChanged();
            await Wait.UntilAsync(() => _class.ListingHeld);
            var timers = _time.ActiveTimers;

            var stopping = Task.Run(recording.Stop);
            await Wait.UntilAsync(() => microphone.Disposed);    // the microphone's track has stopped while the class's waits
            await Wait.UntilAsync(() => _time.ActiveTimers == timers - 1, timeoutSeconds: 10);   // the class's stop waits, with its patience on the clock
            var microphoneStoppedFirst = !stopping.IsCompleted;
            _time.Advance(TimeSpan.FromSeconds(20));
            var summary = await stopping.WaitAsync(TimeSpan.FromSeconds(10));

            using (Assert.EnterMultipleScope())
            {
                Assert.That(microphoneStoppedFirst, Is.True);
                Assert.That(summary.MainSegments, Has.Count.EqualTo(1));
                Assert.That(summary.MicrophoneSegments, Has.Count.EqualTo(1));
                Assert.That(_logs.Problems, Has.Some.Contains("stops without waiting"));
            }
        }
        finally
        {
            windows.Set();
        }
    }

    /// <summary>
    /// V switches your microphone off and on: off releases its capture and disarms the gate, the snapshot says off; on
    /// arms the gate and opens a capture again. The class is untouched either way.
    /// </summary>
    [Test]
    public async Task V_switches_the_microphone_off_and_on()
    {
        var speakers = ClassCapture();
        var first = MicrophoneCapture();
        using var recording = VOnly();

        recording.Start(_files, TimeSpan.Zero);
        speakers.Deliver(Packet(7), Now);
        var off = recording.SetMicrophone(false);
        await Wait.UntilAsync(() => first.Disposed);
        var whileOff = recording.GetSnapshot();
        var armedWhileOff = recording.Gate!.Armed;
        var second = MicrophoneCapture();
        var on = recording.SetMicrophone(true);
        await Wait.UntilAsync(() => second.Started);
        var whileOn = recording.GetSnapshot();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(off, Is.False);
            Assert.That(on, Is.True);
            Assert.That(whileOff.Microphone?.Off, Is.True);
            Assert.That(whileOff.Microphone?.Capturing, Is.False);
            Assert.That(armedWhileOff, Is.False);
            Assert.That(whileOn.Microphone?.Off, Is.False);
            Assert.That(whileOn.Microphone?.Capturing, Is.True);
            Assert.That(recording.Gate.Armed, Is.True);
            Assert.That(speakers.Disposed, Is.False, "the class is untouched");
        }
    }

    /// <summary>In person V switches the microphone too, which is the main track there: off releases its capture, and the snapshot's main track says off.</summary>
    [Test]
    public async Task In_person_V_switches_the_main_track()
    {
        var microphone = MicrophoneCapture();
        using var recording = InPerson();

        recording.Start(_files, TimeSpan.Zero);
        var off = recording.SetMicrophone(false);
        await Wait.UntilAsync(() => microphone.Disposed);
        var snapshot = recording.GetSnapshot();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(off, Is.False);
            Assert.That(snapshot.Main.Off, Is.True);
            Assert.That(snapshot.Microphone, Is.Null);
            Assert.That(recording.Gate!.Armed, Is.False);
        }
    }

    /// <summary>
    /// With an app whose mute Windows cannot see (Recording:ClassAppMute Invisible or Unknown), the microphone starts
    /// switched off: nothing is opened, the gate is not armed and the snapshot says off; the session still looks, so the
    /// recording knows which microphone it would record, and the watch runs on it from the start. V arms the gate and
    /// opens that microphone.
    /// </summary>
    [TestCase(ClassAppMute.Invisible)]
    [TestCase(ClassAppMute.Unknown)]
    public async Task With_an_app_whose_mute_cannot_be_seen_the_microphone_starts_off(ClassAppMute appMute)
    {
        ClassCapture();
        var microphone = MicrophoneCapture();
        using var recording = WithClass(new FollowRule.Named("chrome"), appMute);

        recording.Start(_files, TimeSpan.Zero);
        var atStart = recording.GetSnapshot();
        var openedAtStart = _microphones.Opened.Count;
        var stateAtStart = recording.Gate!.State;
        var watchAtStart = recording.Watch!.EndpointId;
        var wouldRecordAtStart = recording.WouldRecord;
        var recordedAtStart = recording.Microphone;
        recording.SetMicrophone(true);
        await Wait.UntilAsync(() => microphone.Started);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(openedAtStart, Is.Zero, "no microphone is opened until V");
            Assert.That(atStart.Microphone?.Off, Is.True);
            Assert.That(stateAtStart, Is.EqualTo(MicrophoneGateState.Off));
            Assert.That(watchAtStart, Is.EqualTo(Array.Id), "the watch runs from the start, on the microphone the look picked");
            Assert.That(wouldRecordAtStart, Is.EqualTo(Array));
            Assert.That(recordedAtStart, Is.Null, "nothing is recorded until V");
            Assert.That(_logs.Messages, Has.Some.Contains("starts switched off"));
            Assert.That(recording.Gate.Armed, Is.True);
            Assert.That(recording.Microphone, Is.EqualTo(Array));
            Assert.That(recording.Watch.EndpointId, Is.EqualTo(Array.Id));
            Assert.That(_microphones.Opened, Has.Count.EqualTo(1));
        }
    }

    /// <summary>
    /// With the class app's mute unknown, the watch runs from the start although the microphone is off and nothing is
    /// opened: the class app letting go of the microphone for a second and taking it again is seen, and the gate reports
    /// it as evidence before V is ever pressed, so that a class in which Gulzar never speaks still measures the page.
    /// </summary>
    [Test]
    public async Task With_the_microphone_off_the_watch_still_sees_the_class_app_let_go_of_the_microphone()
    {
        ChromeListensOn(Array.Id);
        ClassCapture();
        using var recording = WithClass(new FollowRule.Named("chrome"), ClassAppMute.Unknown);
        var looks = CountLooks(recording);
        string? evidence = null;
        recording.Gate!.Evidence += (_, text) => Volatile.Write(ref evidence, text);

        recording.Start(_files, TimeSpan.Zero);
        await Wait.UntilAsync(() => looks() >= 1);                           // chrome listening
        _sessions.SetSessions(Array.Id);                                     // chrome lets go of the microphone, its process alive
        for (var look = 0; look < 5; look++) await NextLookAsync(looks);    // for 1.25 s
        ChromeListensOn(Array.Id);                                           // and takes it again
        await NextLookAsync(looks);
        await Wait.UntilAsync(() => Volatile.Read(ref evidence) is not null);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(evidence, Does.StartWith("chrome let go of the microphone at ").And.Contain("set Recording:ClassAppMute to Visible"));
            Assert.That(recording.Gate.Summary().Cycles, Is.EqualTo(1));
            Assert.That(_microphones.Opened, Is.Empty, "nothing was opened: the microphone is off until V");
            Assert.That(recording.Gate.Armed, Is.False);
            Assert.That(recording.WouldRecord, Is.EqualTo(Array), "the microphone the watch follows, which V would open");
            Assert.That(recording.MicrophoneWhy, Is.EqualTo("Windows' default microphone"));
            Assert.That(recording.Microphone, Is.Null);
        }
    }

    /// <summary>
    /// With the class app's mute unknown, the default, the first look of the run's checks at the real recording gives the
    /// start lines right: the microphone line names the microphone V will open, with the chooser's reason, as switched off
    /// until V rather than waited for, and the warning about it (Vantage's noise cancelling on, for the array) comes at
    /// the start, not at V.
    /// </summary>
    [Test]
    public void With_the_apps_mute_unknown_the_start_lines_name_the_microphone_V_will_open()
    {
        ClassCapture();
        MicrophoneCapture();
        var registry = new FakeRegistryValues()
            .Set(MicrophoneAdvice.VantageKey, MicrophoneAdvice.VantageValue, "1")
            .Set(MicrophoneAdvice.DuckingKey, MicrophoneAdvice.DuckingValue, 3);
        var options = new RecordingOptions { ClassAppMute = ClassAppMute.Unknown, StatusIntervalSeconds = 3600 };
        using var recording = WithClass(appMute: ClassAppMute.Unknown);
        var monitor = new RecordingMonitor(_freezes, new FakeFreeSpace(), TestOptions.Create(options), new MicrophoneAdvice(registry, NullLogger<MicrophoneAdvice>.Instance), _class,
            new BluetoothMicrophoneWatch(new FakeMicrophoneSessions(), new FakeMicrophoneProvider(), new FakeOwnProcesses(), _time, NullLogger<BluetoothMicrophoneWatch>.Instance), _time);
        var request = TestRuns.Request(TestRuns.Id()) with { Source = ClassAndMicrophone() };
        var context = new MonitorContext(request, _folder.Path, _time.GetUtcNow(), _time.GetTimestamp(), TimeSpan.Zero, recording);

        recording.Start(_files, TimeSpan.Zero);
        var first = monitor.Tick(recording.GetSnapshot(), context);

        var lines = first.Lines.Select(line => line.Text).ToList();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(lines[0], Does.StartWith("Your microphone is recorded too"));
            Assert.That(lines[1], Does.StartWith("ClassScribe has not been told whether chrome lets go of the microphone"));
            Assert.That(lines[2], Is.EqualTo("Following: chrome (Recording:MicrophoneFollows)"));
            Assert.That(lines[3], Is.EqualTo($"Microphone: \"{Array.Name}\" (Windows' default microphone), switched off until you press V; RAW will be asked for"));
            Assert.That(lines[4], Does.StartWith("Lenovo Vantage's microphone noise cancelling is on"));
            Assert.That(lines, Has.None.Contains("waiting for"));
            Assert.That(first.Attention.Short, Is.EqualTo("1 warning at the start"));
            Assert.That(_microphones.Opened, Is.Empty);
        }
    }

    /// <summary>
    /// The processing the recording reports is the opened Windows capture's, not what was asked for: when Windows refuses
    /// RAW and the capture opens with its sound processing, the tap notes Default, which is what the run's start line says.
    /// </summary>
    [Test, Platform("Win")]
    [SupportedOSPlatform("windows")]
    public void The_processing_reported_is_the_opened_captures_not_the_one_asked_for()
    {
        using var packets = new FakePacketSource();
        var client = new FakeAudioClient(new WaveFormatExtensible(48000, 32, 2));
        var opened = WasapiAudioCapture.ForMicrophone(new AudioClient(client), Array.Id, Array.Name, MicrophoneProcessing.Default, NullLogger.Instance, opened: null, packets);
        _microphones.Prepare(opened);
        var tap = new MicrophoneProviderTap(_microphones);

        var capture = tap.CreateCapture(Array, MicrophoneProcessing.Raw);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(capture, Is.SameAs(opened));
            Assert.That(tap.Last, Is.EqualTo(new MicrophoneOpened(Array, MicrophoneProcessing.Default)), "what the capture reports, not what was asked for");
            Assert.That(_microphones.Opened, Is.EqualTo(new[] { (Array.Id, MicrophoneProcessing.Raw) }), "RAW was asked for");
        }
    }

    /// <summary>
    /// Once the microphone's track is left out, the snapshot has no microphone track: there is nothing to wait for and
    /// nothing to say about it, so the run's checks leave it alone rather than waiting for a microphone every 30 seconds.
    /// </summary>
    [Test]
    public void A_microphone_that_was_left_out_is_not_in_the_snapshot()
    {
        var speakers = ClassCapture();
        MicrophoneCapture(format: Unreadable);
        using var recording = VOnly();

        recording.Start(_files, TimeSpan.Zero);
        speakers.Deliver(Packet(7), Now);
        var snapshot = recording.GetSnapshot();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(snapshot.Microphone, Is.Null, "nothing is waited for from a track that is not recorded");
            Assert.That(snapshot.Main.Source, Is.EqualTo("Speakers"));
            Assert.That(recording.WouldRecord, Is.Null);
            Assert.That(recording.MutedInWindows, Is.Null);
        }
    }

    /// <summary>
    /// The factory makes the recording each mode asks for: the class alone as before; the class and the microphone on one
    /// clock, following the app the setting names in device mode, the recorded app in app mode, or what the choice says;
    /// and in person the microphone alone, following no app.
    /// </summary>
    [Test]
    public void The_factory_makes_a_recording_per_mode()
    {
        var factory = Factory(new RecordingOptions { MicrophoneFollows = "chrome" });
        using var classAlone = factory.Create(new SourcePlan(RecordingMode.Class, new ClassSource.Automatic(), Microphone: null));
        using var deviceMode = (TrackSet)factory.Create(ClassAndMicrophone());
        using var appMode = (TrackSet)factory.Create(ClassAndMicrophone(new ClassSource.App("msedge")));
        using var chosen = (TrackSet)factory.Create(ClassAndMicrophone(new ClassSource.App("msedge"), new FollowRule.Any()));
        using var none = (TrackSet)Factory(new RecordingOptions { MicrophoneFollows = "none" }).Create(ClassAndMicrophone());
        using var inPerson = (TrackSet)factory.Create(InPersonPlan());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(classAlone, Is.InstanceOf<ClassRecording>());
            Assert.That(deviceMode.Follows, Is.EqualTo(new FollowRule.Named("chrome")), "the setting, in device mode");
            Assert.That(deviceMode.Watch, Is.Not.Null);
            Assert.That(deviceMode.Gate?.Armed, Is.False, "the class app's mute is Unknown by default");
            Assert.That(appMode.Follows, Is.EqualTo(new FollowRule.Named("msedge")), "the recorded app, in app mode");
            Assert.That(chosen.Follows, Is.EqualTo(new FollowRule.Any()), "the choice wins");
            Assert.That(none.Follows, Is.EqualTo(new FollowRule.None()));
            Assert.That(none.Watch, Is.Null, "nothing to watch when no app is followed");
            Assert.That(none.Gate?.Armed, Is.True);
            Assert.That(inPerson.Follows, Is.Null);
            Assert.That(inPerson.Watch, Is.Null);
            Assert.That(inPerson.Gate?.Armed, Is.True);
            Assert.That(_microphones.Listings, Is.Zero, "nothing is looked at before a recording starts");
            Assert.That(_class.DeviceQueries, Is.Zero);
        }
    }

    /// <summary>Each recording the factory makes has a clock of its own, so two recordings never share positions.</summary>
    [Test]
    public void Each_recording_has_a_clock_of_its_own()
    {
        var speakers = ClassCapture();
        MicrophoneCapture();
        var factory = Factory(new RecordingOptions { MicrophoneFollows = "none" });
        using var one = factory.Create(ClassAndMicrophone());
        using var other = factory.Create(ClassAndMicrophone());

        one.Start(_files, TimeSpan.Zero);
        other.Start(new RecordingFiles(Path.Combine(_folder.Path, "other"), "Fiqh_2026-10-17_19-00-00Z"), TimeSpan.FromMinutes(5));
        speakers.Deliver(Packet(7), Now);
        _time.Advance(TimeSpan.FromSeconds(1));

        Assert.That(one.Position, Is.EqualTo(TimeSpan.FromSeconds(1)));
        Assert.That(other.Position, Is.EqualTo(TimeSpan.FromMinutes(5)), "no packet of its own yet");
    }

    /// <summary>The factory's recording of the class alone notes a long stretch with nothing playing, as the transcript settings ask.</summary>
    [Test]
    public void The_class_alone_notes_long_stretches_with_nothing_playing()
    {
        var speakers = ClassCapture();
        using var recording = Factory(transcript: new TranscriptOptions { ShowGapsLongerThanSeconds = 2 })
            .Create(new SourcePlan(RecordingMode.Class, new ClassSource.Automatic(), Microphone: null));

        recording.Start(_files, TimeSpan.Zero);
        speakers.Deliver(Packet(7), Now);
        _time.Advance(TimeSpan.FromSeconds(5));
        speakers.Deliver(Packet(7), Now);
        recording.Stop();

        Assert.That(_logs.MessagesOf<RecordingSession>(), Has.Some.Contains("with nothing playing").And.Some.Contains("left out here"));
    }

    /// <summary>
    /// The watch starts on the microphone the session opens and follows it to another: when the class app is found
    /// listening on another microphone, the recording moves to that one, as the chooser's first rule says, and the watch
    /// is pointed at it.
    /// </summary>
    [Test]
    public async Task A_move_to_the_class_apps_microphone_re_points_the_watch()
    {
        _microphones.Connect(Obsbot);
        ChromeListensOn(Obsbot.Id);
        ClassCapture();
        var array = MicrophoneCapture();
        var obsbot = MicrophoneCapture(Obsbot);
        using var recording = FollowingChrome();

        recording.Start(_files, TimeSpan.Zero);
        var startedOn = recording.Watch!.EndpointId;
        await Wait.UntilAsync(() => obsbot.Started);
        await Wait.UntilAsync(() => recording.Watch.EndpointId == Obsbot.Id);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(startedOn, Is.EqualTo(Array.Id), "the watch starts on the microphone opened first");
            Assert.That(array.Disposed, Is.True);
            Assert.That(recording.Microphone, Is.EqualTo(Obsbot));
            Assert.That(_logs.Messages, Has.Some.Contains("the microphone the class app uses"));
        }
    }

    /// <summary>
    /// The watch's looks reach the gate, which decides the microphone's packets: one between two looks that found chrome
    /// listening is written at its place; one before a look that found it gone is dropped, and its time is silence.
    /// </summary>
    [Test]
    public async Task The_looks_decide_which_microphone_packets_are_written()
    {
        ChromeListensOn(Array.Id);
        var speakers = ClassCapture();
        var microphone = MicrophoneCapture();
        using var recording = FollowingChrome();

        recording.Start(_files, TimeSpan.Zero);
        var looks = CountLooks(recording);
        await Wait.UntilAsync(() => _sessions.ReadAlls >= 1);
        var origin = Now;
        speakers.Deliver(Packet(7), origin);
        microphone.Deliver(Packet(7), origin + Seconds(0.1).Ticks);
        _time.Advance(TimeSpan.FromMilliseconds(250));
        await Wait.UntilAsync(() => looks() >= 1);
        await Wait.UntilAsync(() => recording.GetSnapshot().Microphone?.Written >= Seconds(0.11));
        _sessions.SetSessions(Array.Id);                         // chrome lets go of the microphone
        _time.Advance(TimeSpan.FromMilliseconds(250));
        await Wait.UntilAsync(() => looks() >= 2);
        await Wait.UntilAsync(() => recording.Gate!.State == MicrophoneGateState.NotOpened);
        microphone.Deliver(Packet(9), Now - Seconds(0.1).Ticks);  // between the open look and the closed one
        var summary = recording.Stop();

        var samples = SamplesOf(summary.MicrophoneSegments);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(samples[At(Seconds(0.1))..At(Seconds(0.11))], Is.All.EqualTo(7));
            Assert.That(samples, Has.None.EqualTo(9), "dropped: the look after it found chrome gone");
            Assert.That(samples, Has.Length.EqualTo(At(Seconds(0.5))), "padded to the stop");
        }
    }

    /// <summary>
    /// Stopping makes a last look, so the held packets are judged by it, stops the watch, and then finishes the tracks;
    /// the watch looks no more. The looks are counted from before the start, so the watch's first look, on its own
    /// thread, is counted before the stop and never lands between the counts.
    /// </summary>
    [Test]
    public async Task Stopping_makes_a_last_look_and_stops_the_watch()
    {
        ChromeListensOn(Array.Id);
        ClassCapture();
        MicrophoneCapture();
        using var recording = FollowingChrome();
        var looks = CountLooks(recording);

        recording.Start(_files, TimeSpan.Zero);
        await Wait.UntilAsync(() => looks() >= 1);
        var before = looks();
        recording.Stop();
        var atStop = looks();
        _time.Advance(TimeSpan.FromSeconds(1));
        await Task.Delay(50);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(before, Is.EqualTo(1), "the first look, on the watch's thread");
            Assert.That(atStop, Is.EqualTo(before + 1), "one last look at the stop");
            Assert.That(looks(), Is.EqualTo(atStop), "and none after it");
            Assert.That(recording.Watch!.Failure, Is.Null);
        }
    }

    /// <summary>
    /// V on makes a fresh look before the microphone opens again, so the first packets are judged by it and not by a
    /// stale look from before V off: chrome let go of the microphone while it was off, and right after V on the gate
    /// already says so. The look is made on V's own thread, before the capture is opened again. The first look is
    /// waited for at the gate, not only at the counter, which hears it first; and the fresh look is stamped later than
    /// it, as a real look is, since the gate keeps the later of two looks with one stamp whichever arrived last.
    /// </summary>
    [Test]
    public async Task V_on_makes_a_fresh_look_before_the_microphone_opens_again()
    {
        ChromeListensOn(Array.Id);
        ClassCapture();
        var first = MicrophoneCapture();
        using var recording = FollowingChrome();
        var looks = CountLooks(recording);
        var openedAtTheLastLook = -1;
        recording.Watch!.Looked += (_, _) => openedAtTheLastLook = _microphones.Opened.Count;

        recording.Start(_files, TimeSpan.Zero);
        await Wait.UntilAsync(() => looks() >= 1);
        await Wait.UntilAsync(() => recording.Gate!.State == MicrophoneGateState.Recording);   // the gate has taken the first look
        recording.SetMicrophone(false);
        await Wait.UntilAsync(() => first.Disposed);
        _sessions.SetSessions(Array.Id);                        // chrome lets go of the microphone while it is off
        _time.Advance(TimeSpan.FromMilliseconds(1));            // the fresh look carries a later stamp than the first
        var second = MicrophoneCapture();
        var before = looks();
        var stateBefore = recording.Gate!.State;
        recording.SetMicrophone(true);
        var atV = looks();
        var stateAtV = recording.Gate.State;
        await Wait.UntilAsync(() => second.Started);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(stateBefore, Is.EqualTo(MicrophoneGateState.Off));
            Assert.That(atV, Is.EqualTo(before + 1), "one fresh look, made inside V on");
            Assert.That(openedAtTheLastLook, Is.EqualTo(1), "the look came before the capture was opened again");
            Assert.That(_microphones.Opened, Has.Count.EqualTo(2));
            Assert.That(stateAtV, Is.EqualTo(MicrophoneGateState.NotOpened), "judged by the fresh look: chrome has let go, whatever the look before V off said");
            Assert.That(recording.Gate.Armed, Is.True);
        }
    }

    /// <summary>
    /// A sleep is cut out of the clock: the session's look for a time ClassScribe did not run reaches the freeze watch,
    /// and the freeze it finds is cut from the positions, so nothing is padded across it and a mark after it is right.
    /// </summary>
    [Test]
    public void A_sleep_is_cut_out_of_the_clock()
    {
        var speakers = ClassCapture();
        MicrophoneCapture();
        using var recording = VOnly();

        recording.Start(_files, TimeSpan.Zero);
        _freezes.Tick();
        speakers.Deliver(Packet(7), Now);
        _time.Advance(TimeSpan.FromSeconds(1));
        _freezes.Tick();                                         // 11 s: the last tick before the sleep
        _times.Sleep(TimeSpan.FromSeconds(18));                  // 29 s: the padding timers fire once on waking and ask for a look
        var position = recording.Position;
        var summary = recording.Stop();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_clock.TimeCut, Is.EqualTo(TimeSpan.FromSeconds(15)), "from 12.5 s to 27.5 s: the sleep less the margins");
            Assert.That(position, Is.EqualTo(TimeSpan.FromSeconds(4)));
            Assert.That(LengthOf(summary.MainSegments), Is.EqualTo(TimeSpan.FromSeconds(4)).Within(TimeSpan.FromMilliseconds(1)), "not padded across the sleep");
        }
    }

    /// <summary>Pausing pauses the clock and both tracks: the position stays where it was, the snapshot says paused, and after P again the position carries on from there.</summary>
    [Test]
    public void Pausing_pauses_the_clock_and_both_tracks()
    {
        var speakers = ClassCapture();
        var microphone = MicrophoneCapture();
        using var recording = VOnly();

        recording.Start(_files, TimeSpan.Zero);
        speakers.Deliver(Packet(7), Now);
        _time.Advance(TimeSpan.FromSeconds(1));
        recording.Pause();
        _time.Advance(TimeSpan.FromSeconds(5));
        microphone.Deliver(Packet(9), Now);                     // stamped inside the pause: dropped
        var paused = recording.GetSnapshot();
        recording.Resume();
        _time.Advance(TimeSpan.FromSeconds(1));
        var resumed = recording.GetSnapshot();
        var summary = recording.Stop();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(paused.Paused, Is.True);
            Assert.That(paused.Position, Is.EqualTo(TimeSpan.FromSeconds(1)));
            Assert.That(_clock.TimePaused, Is.EqualTo(TimeSpan.FromSeconds(5)));
            Assert.That(resumed.Paused, Is.False);
            Assert.That(resumed.Position, Is.EqualTo(TimeSpan.FromSeconds(2)));
            Assert.That(SamplesOf(summary.MicrophoneSegments), Has.None.EqualTo(9));
        }
    }

    /// <summary>
    /// A recording continued as its next part counts on from its length, and each track starts from its own length on
    /// disk: the shorter track is padded up to the offset, so both end together again.
    /// </summary>
    [Test]
    public void A_continued_recording_counts_on_from_its_length_and_each_track_from_its_own()
    {
        TestAudio.WriteWav(_files.Segment(1), 16000, new short[160_000]);                      // 10 s of class recorded before
        TestAudio.WriteWav(_files.Segment(1, Track.Microphone), 16000, new short[128_000]);    // 8 s of microphone: the crash cut it short
        var speakers = ClassCapture();
        MicrophoneCapture();
        using var recording = VOnly();

        recording.Start(_files, TimeSpan.FromSeconds(10));
        var before = recording.Position;
        speakers.Deliver(Packet(7), Now);
        _time.Advance(TimeSpan.FromSeconds(3));
        var summary = recording.Stop();

        var classTotal = TimeSpan.FromSeconds(10) + LengthOf(summary.MainSegments);
        var microphoneTotal = TimeSpan.FromSeconds(8) + LengthOf(summary.MicrophoneSegments);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(before, Is.EqualTo(TimeSpan.FromSeconds(10)));
            Assert.That(summary.MainSegments, Is.EqualTo(new[] { _files.Segment(2) }), "the next free part");
            Assert.That(summary.MicrophoneSegments, Is.EqualTo(new[] { _files.Segment(2, Track.Microphone) }));
            Assert.That((classTotal - microphoneTotal).Duration(), Is.LessThanOrEqualTo(TimeSpan.FromMilliseconds(50)), "the microphone caught up");
            Assert.That(classTotal, Is.EqualTo(TimeSpan.FromSeconds(13)).Within(TimeSpan.FromMilliseconds(1)));
        }
    }

    /// <summary>
    /// The summary says what this run recorded: the longer track's length, both tracks' files, and that something audible
    /// reached either of them. Stopping again answers the same at once. The line in the log counts the recording's parts
    /// as the transcript counts them, from the class track, and names the microphone's files apart.
    /// </summary>
    [Test]
    public void The_summary_says_what_this_run_recorded()
    {
        var speakers = ClassCapture();
        var microphone = MicrophoneCapture();
        using var recording = VOnly();

        recording.Start(_files, TimeSpan.Zero);
        speakers.Deliver(Packet(0), Now);                       // the class silent
        microphone.Deliver(Packet(700), Now);                   // the microphone heard
        var summary = recording.Stop();
        var again = recording.Stop();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(summary.HeardAnything, Is.True, "the microphone heard something");
            Assert.That(summary.Length, Is.EqualTo(Seconds(0.01)));
            Assert.That(again, Is.SameAs(summary));
            Assert.That(speakers.Disposed, Is.True);
            Assert.That(microphone.Disposed, Is.True);
            Assert.That(_logs.MessagesOf<TrackSet>(), Has.Some.Contains("Recorded 00:00:00 in this run: 00:00:00 paused")
                .And.Some.Contains("1 part and 1 microphone file, 0 corrections for drift"));
        }
    }

    /// <summary>Flushing brings both tracks' files on disk up to date at once, as before the process ends.</summary>
    [Test]
    public void Flushing_brings_both_files_up_to_date()
    {
        var speakers = ClassCapture();
        var microphone = MicrophoneCapture();
        using var recording = VOnly();

        recording.Start(_files, TimeSpan.Zero);
        speakers.Deliver(Packet(7), Now);
        microphone.Deliver(Packet(9), Now);
        var before = (TestAudio.ReadWav(_files.Segment(1)).Samples.Length, TestAudio.ReadWav(_files.Segment(1, Track.Microphone)).Samples.Length);
        recording.Flush();
        var after = (TestAudio.ReadWav(_files.Segment(1)).Samples.Length, TestAudio.ReadWav(_files.Segment(1, Track.Microphone)).Samples.Length);

        Assert.That(before, Is.EqualTo((0, 0)));
        Assert.That(after, Is.EqualTo((480, 480)));
    }

    /// <summary>A reopen replaces the capture of the track named at that track's next look, and leaves the other track alone.</summary>
    [Test]
    public async Task A_reopen_replaces_the_capture_of_the_track_named()
    {
        var speakers = ClassCapture();
        var secondSpeakers = ClassCapture();
        var microphone = MicrophoneCapture();
        var secondMicrophone = MicrophoneCapture();
        using var recording = VOnly();

        recording.Start(_files, TimeSpan.Zero);
        recording.Reopen(Track.Microphone, "no audio has arrived for 5 seconds");
        _microphones.RaiseChanged();                            // the microphone's next look comes at once
        await Wait.UntilAsync(() => secondMicrophone.Started);
        var classUntouched = !speakers.Disposed && !secondSpeakers.StartEntered;
        recording.Reopen(Track.Main, "no audio has arrived for 10 seconds");
        _class.RaiseDevicesChanged();
        await Wait.UntilAsync(() => secondSpeakers.Started);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(microphone.Disposed, Is.True);
            Assert.That(classUntouched, Is.True);
            Assert.That(speakers.Disposed, Is.True);
        }
    }

    /// <summary>In person there is no microphone track beside the main one: a reopen of it changes nothing, which the log notes.</summary>
    [Test]
    public async Task In_person_a_reopen_of_the_microphone_track_changes_nothing()
    {
        var microphone = MicrophoneCapture();
        var second = MicrophoneCapture();
        using var recording = InPerson();

        recording.Start(_files, TimeSpan.Zero);
        recording.Reopen(Track.Microphone, "no audio has arrived for 5 seconds");
        _microphones.RaiseChanged();
        await Task.Delay(100);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(microphone.Disposed, Is.False);
            Assert.That(second.StartEntered, Is.False);
            Assert.That(_logs.MessagesOf<TrackSet>(), Has.Some.Contains("no microphone track"));
        }
    }

    /// <summary>Disposing a running recording stops it all: the captures are released, the files finished, the timers and the notifications let go, and the watch's thread ended.</summary>
    [Test]
    public async Task Disposing_stops_everything()
    {
        ChromeListensOn(Array.Id);
        var speakers = ClassCapture();
        var microphone = MicrophoneCapture();
        var recording = FollowingChrome();

        recording.Start(_files, TimeSpan.Zero);
        var looks = CountLooks(recording);
        await Wait.UntilAsync(() => _sessions.ReadAlls >= 1);
        speakers.Deliver(Packet(7), Now);
        recording.Dispose();
        var afterDispose = looks();
        _time.Advance(TimeSpan.FromSeconds(1));
        await Task.Delay(50);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(speakers.Disposed, Is.True);
            Assert.That(microphone.Disposed, Is.True);
            Assert.That(TestAudio.ReadWav(_files.Segment(1)).Samples[..480], Is.All.EqualTo(7));
            Assert.That(_time.ActiveTimers, Is.Zero, "the looks, the padding and the watch have ended");
            Assert.That(_class.HasListeners, Is.False);
            Assert.That(_microphones.HasListeners, Is.False);
            Assert.That(looks(), Is.EqualTo(afterDispose), "the watch looks no more");
        }
    }
}
