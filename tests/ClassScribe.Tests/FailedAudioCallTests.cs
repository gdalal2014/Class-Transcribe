using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using ClassScribe.Audio;
using ClassScribe.Audio.Microphones;
using ClassScribe.Audio.Timeline;
using ClassScribe.Audio.Windows;
using ClassScribe.Commands;
using ClassScribe.Recorder;
using ClassScribe.Settings;
using ClassScribe.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClassScribe.Tests;

/// <summary>
/// The Zoom crash of 18 October. With the Zoom app in a meeting, one of Windows' audio calls answered E_INVALIDARG, "the
/// parameter is incorrect", which .NET's own mapping turned into an ArgumentException that none of the readers' catches
/// expected: the devices command stopped with "Value does not fall within the expected range", and in a recording the same
/// answer would have stopped the watch on the class app's microphone for the rest of the run and, in app mode, kept the
/// class from being captured at all. Every failed result now reaches the readers as a COMException, and one bad session
/// costs only itself. These tests play that answer through the real readers, over fakes of Windows' objects, and show that a
/// recording keeps its class audio and your voice. Nothing is opened, played or recorded.
/// </summary>
[TestFixture, Platform("Win")]
[SupportedOSPlatform("windows")]
public sealed class FailedAudioCallTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 18, 19, 0, 0, TimeSpan.Zero);
    private static readonly AudioFormat Pcm48KMono = new(48000, 1, 16, false);
    private static readonly Microphone Array = LaptopMicrophones.Array;

    /// <summary>The fake speakers' identifier.</summary>
    private const string SpeakersId = "{speakers}";

    /// <summary>The Zoom app's process that plays the meeting.</summary>
    private const int ZoomMeeting = 6000;

    /// <summary>Another of the Zoom app's processes with sound open, whose session Windows would not hand out.</summary>
    private const int ZoomHelper = 6001;

    /// <summary>Chrome, playing something else.</summary>
    private const int Chrome = 5150;

    private TempFolder _folder = null!;
    private RecordingFiles _files = null!;
    private ManualTimeProvider _time = null!;
    private TestLoggerFactory _logs = null!;

    /// <summary>A temporary folder and a recording in it, the clock ten seconds in, and the logs.</summary>
    [SetUp]
    public void SetUp()
    {
        _folder = new TempFolder();
        _files = new RecordingFiles(_folder.Path, "Sughra_2026-10-18_19-00-00Z");
        _time = new ManualTimeProvider(Start);
        _time.Advance(TimeSpan.FromSeconds(10));
        _logs = new TestLoggerFactory();
    }

    /// <summary>Deletes the temporary folder, which fails if a test left a file open.</summary>
    [TearDown]
    public void TearDown() => _folder.Dispose();

    /// <summary>
    /// Every failed result is thrown as a COMException that carries the result, whatever .NET's own mapping would have
    /// made of it: E_INVALIDARG is no longer an ArgumentException, E_OUTOFMEMORY no OutOfMemoryException, E_NOINTERFACE
    /// no InvalidCastException, and so on, so that every reader's COMException catch applies to them all.
    /// </summary>
    [TestCase(unchecked((int)0x80070057))]   // E_INVALIDARG
    [TestCase(unchecked((int)0x8007000E))]   // E_OUTOFMEMORY
    [TestCase(unchecked((int)0x80004002))]   // E_NOINTERFACE
    [TestCase(unchecked((int)0x80070005))]   // E_ACCESSDENIED
    [TestCase(unchecked((int)0x80004003))]   // E_POINTER
    [TestCase(unchecked((int)0x80004001))]   // E_NOTIMPL
    [TestCase(unchecked((int)0x80004005))]   // E_FAIL
    [TestCase(unchecked((int)0x80070490))]   // E_NOTFOUND
    [TestCase(unchecked((int)0x88890004))]   // AUDCLNT_E_DEVICE_INVALIDATED
    [TestCase(unchecked((int)0x88890010))]   // AUDCLNT_E_SERVICE_NOT_RUNNING
    public void Every_failed_result_is_a_COMException_with_its_result(int result)
    {
        var failure = Assert.Throws<COMException>(() => CoreAudio.Check(result));

        Assert.That(failure!.HResult, Is.EqualTo(result));
        Assert.That(failure.Message, Is.Not.Empty);
    }

    /// <summary>
    /// E_INVALIDARG is said in Windows' own words, "The parameter is incorrect." on an English Windows, rather than in
    /// .NET's "Value does not fall within the expected range.", which named no audio call and no result on 18 October.
    /// </summary>
    [Test]
    public void E_INVALIDARG_is_said_in_Windows_own_words()
    {
        var failure = Assert.Throws<COMException>(() => CoreAudio.Check(FakeResults.InvalidArgument));

        Assert.That(failure!.Message, Is.EqualTo(Marshal.GetPInvokeErrorMessage(FakeResults.InvalidArgument)));
        Assert.That(failure.Message, Does.Not.Contain("expected range"));
    }

    /// <summary>
    /// A failure notes which of Windows' calls answered with it, named from the checked expression (the outermost call,
    /// past a cast), and the words a listing gives for it start with that call, so that the next time a call fails with
    /// the Zoom app in a meeting, the devices command and the microphone check say which. An expression that calls
    /// nothing, such as a variable holding a result, names no call.
    /// </summary>
    [TestCase("control.GetState(out var state)", "GetState")]
    [TestCase("((IAudioSessionManager2Raw)activated).GetSessionEnumerator(out list)", "GetSessionEnumerator")]
    [TestCase("device.Activate(ref interfaceId, CoreAudio.AllContexts, IntPtr.Zero, out activated)", "Activate")]
    [TestCase("result", null)]
    public void A_failure_names_the_call_it_came_from(string expression, string? call)
    {
        var failure = Assert.Throws<COMException>(() => CoreAudio.Check(FakeResults.InvalidArgument, expression))!;

        Assert.Multiple(() =>
        {
            Assert.That(AudioFailures.CallOf(failure), Is.EqualTo(call));
            Assert.That(AudioFailures.Describe(failure), Is.EqualTo(call is null ? $"{InvalidArgumentWords} (0x80070057)" : $"{call}: {InvalidArgumentWords} (0x80070057)"));
            Assert.That(failure.Message, Is.EqualTo(Marshal.GetPInvokeErrorMessage(FakeResults.InvalidArgument)), "Windows' own words, as before");
        });
    }

    /// <summary>The compiler hands the checked expression over by itself, so every check names its call without being told.</summary>
    [Test]
    public void A_checked_call_is_named_without_being_told()
    {
        var session = new FakeAudioSession(ZoomMeeting, 0.5f) { StateFailure = FakeResults.InvalidArgument };

        var failure = Assert.Throws<COMException>(() => CoreAudio.Check(session.GetState(out _)))!;

        Assert.That(AudioFailures.CallOf(failure), Is.EqualTo("GetState"));
    }

    /// <summary>
    /// The Zoom app recorded by itself: when the listing cannot read the Zoom app's only session, so that the app is
    /// missing from the apps with sound, the app has not been seen to close, so no warning says that it has; the
    /// capture already open goes on, and one line at Information says why, naming the call that failed. Before, the
    /// warning <c>"zoom" has closed; recording silence until it plays again</c> came while the Zoom app was open and
    /// recorded, and again each time its session came back and failed again.
    /// </summary>
    [Test, Category("WindowsAudio")]
    public void A_recorded_Zoom_app_whose_only_session_cannot_be_read_is_not_said_to_have_closed()
    {
        var processes = new Dictionary<int, string> { [ZoomMeeting] = "Zoom", [Chrome] = "chrome" };
        var speakers = new FakeOutputDevice(SpeakersId, 0.5f,
            new FakeAudioSession(ZoomMeeting, 0.6f) { HandOutFailure = FakeResults.InvalidArgument }, new FakeAudioSession(Chrome, 0.3f));
        using var listings = new WasapiLoopbackCaptureProvider(NullLogger<WasapiLoopbackCaptureProvider>.Instance,
            () => new FakeDeviceEnumerator(speakers) { DefaultId = SpeakersId }, _ => "Speakers", processes.GetValueOrDefault);
        var log = new RecordingLogger<CaptureChooser>();
        var chooser = new CaptureChooser(_time, new ClassSource.App("zoom"), log);
        var recorded = $"process:{ZoomMeeting}";

        var first = chooser.Choose(listings, recorded, sinceRecordedSound: null);
        var second = chooser.Choose(listings, recorded, sinceRecordedSound: null);

        Assert.Multiple(() =>
        {
            Assert.That((first, second), Is.EqualTo(((CaptureTarget?)null, (CaptureTarget?)null)), "nothing to move to: the open capture goes on");
            Assert.That(log.Problems, Is.Empty, "no \"has closed\" warning");
            Assert.That(log.Entries, Is.EqualTo(new[]
            {
                (LogLevel.Information, "\"zoom\" is not among the apps with sound at this look, but ClassScribe could not read an audio session on \"Speakers\" " +
                    $"(GetSession: {InvalidArgumentWords} (0x80070057)), which may be its; its capture goes on")
            }));
        });
    }

    /// <summary>A success is not a failure: S_OK, S_FALSE, and AUDCLNT_S_NO_SINGLE_PROCESS, which a session that spans processes answers.</summary>
    [TestCase(0)]
    [TestCase(1)]
    [TestCase(0x0889000D)]
    public void A_success_throws_nothing(int result) => Assert.DoesNotThrow(() => CoreAudio.Check(result));

    /// <summary>
    /// The watch on the class app's microphone keeps looking, four times a second, while the Zoom app's session on the
    /// microphone answers E_INVALIDARG at every look: it finds chrome listening each time, and never stops. On 18 October
    /// the same answer would have stopped it for the rest of the run, and your voice with it.
    /// </summary>
    [Test]
    public async Task The_microphone_watch_keeps_looking_beside_a_session_that_answers_E_INVALIDARG()
    {
        using var reader = SessionsReader(Laptop.ArrayId, Laptop.ArrayName);
        var log = new RecordingLogger<ClassMicrophoneWatch>();
        using var watch = new ClassMicrophoneWatch(reader, new FakeMicrophoneUsageRecord(), new FakeProcessNames(), new FollowRule.Named("chrome"),
            new FakeOwnProcesses(Laptop.OwnProcess), _time, log);
        var looks = new List<WatchLook>();
        watch.Looked += (_, look) =>
        {
            lock (looks) looks.Add(look);
        };
        Exception? failed = null;
        watch.Failed += (_, exception) => failed = exception;

        watch.Start(Laptop.ArrayId);
        for (var look = 2; look <= 8; look++)
        {
            await Wait.UntilAsync(() => Count(looks) >= look - 1);
            _time.Advance(ClassMicrophoneWatch.Interval);
        }

        await Wait.UntilAsync(() => Count(looks) >= 8);
        watch.Stop();

        WatchLook[] seen;
        lock (looks) seen = [.. looks];
        Assert.Multiple(() =>
        {
            Assert.That(failed, Is.Null);
            Assert.That(watch.Failure, Is.Null);
            Assert.That(seen.Select(look => look.Listening), Has.All.EqualTo(AppListening.Yes));
            Assert.That(seen.Select(look => string.Join(",", look.ListeningApps)), Has.All.EqualTo("chrome"), "the Zoom app's session was left out");
            Assert.That(log.Problems, Is.Empty);
        });
    }

    /// <summary>
    /// A recording of the Zoom app's sound starts capturing it and keeps its audio while two of the Zoom app's sessions
    /// answer E_INVALIDARG, one when the listing asks for it and one from its level meter: the first is left out, the
    /// second is listed at silence, and the app is recorded from its process. On 18 October the listing failed as a whole
    /// at every look, so the app would never have been captured.
    /// </summary>
    [Test, Category("WindowsAudio")]
    public void A_recording_of_the_Zoom_app_captures_it_beside_sessions_that_answer_E_INVALIDARG()
    {
        var processes = new Dictionary<int, string> { [ZoomMeeting] = "Zoom", [ZoomHelper] = "Zoom", [Chrome] = "chrome" };
        var speakers = new FakeOutputDevice(SpeakersId, 0.5f,
            new FakeAudioSession(ZoomHelper, 0.2f) { HandOutFailure = FakeResults.InvalidArgument },
            new FakeAudioSession(ZoomMeeting, 0.6f) { PeakFailure = FakeResults.InvalidArgument },
            new FakeAudioSession(Chrome, 0.3f));
        using var listings = new WasapiLoopbackCaptureProvider(NullLogger<WasapiLoopbackCaptureProvider>.Instance,
            () => new FakeDeviceEnumerator(speakers) { DefaultId = SpeakersId }, _ => "Speakers", processes.GetValueOrDefault);
        var captures = new FakeCaptureProvider();
        var zoom = captures.AddApp(ZoomMeeting, "Zoom", "Speakers", capture: new FakeAudioCapture(Pcm48KMono, "Zoom", $"process:{ZoomMeeting}") { DeliversContinuously = true })!;
        using var recording = Factory(new WindowsListedCaptures(listings, captures), new FakeMicrophoneSessions())
            .Create(new SourcePlan(RecordingMode.Class, new ClassSource.App("zoom"), Microphone: null));

        recording.Start(_files, TimeSpan.Zero);
        zoom.Deliver(TestAudio.Shorts(1000, -1000, 500));
        recording.Stop();

        Assert.Multiple(() =>
        {
            Assert.That(captures.Created, Is.EqualTo(new[] { zoom }), "the Zoom app's process was captured");
            Assert.That(TestAudio.ReadWav(_files.Segment(1)).Samples, Is.EqualTo(new short[] { 1000, -1000, 500 }));
            Assert.That(_logs.Problems, Is.Empty, "no \"Could not start capturing\"");
        });
    }

    /// <summary>
    /// With the class and your microphone, following chrome, your voice is recorded while the Zoom app's session on the
    /// microphone answers E_INVALIDARG at every look: the watch, reading through the real sessions reader, finds chrome
    /// listening, the gate opens, and the packet is written at its place beside the class. The watch never stops.
    /// </summary>
    [Test]
    public async Task Your_voice_is_recorded_beside_a_session_that_answers_E_INVALIDARG()
    {
        using var reader = SessionsReader(Array.Id, Array.Name);
        var theClass = new FakeCaptureProvider();
        var speakers = theClass.Enqueue(new FakeAudioCapture(Pcm48KMono, "Speakers"));
        var microphones = new FakeMicrophoneProvider().Connect(Array);
        var microphone = microphones.Prepare(new FakeAudioCapture(Pcm48KMono, Array.Name, Array.Id) { DeliversContinuously = true });
        using var recording = (TrackSet)Factory(theClass, reader, microphones, new RecordingOptions { MicrophoneFollows = "chrome", ClassAppMute = ClassAppMute.Visible })
            .Create(new SourcePlan(RecordingMode.ClassAndMicrophone, new ClassSource.Automatic(), new MicrophoneChoice(null, new FollowRule.Named("chrome"))),
                new RecordingClock(_time));
        var looks = 0;
        recording.Watch!.Looked += (_, _) => Interlocked.Increment(ref looks);

        recording.Start(_files, TimeSpan.Zero);
        await Wait.UntilAsync(() => Volatile.Read(ref looks) >= 1);
        var origin = _time.GetTimestamp();
        speakers.Deliver(Packet(7), origin);
        microphone.Deliver(Packet(9), origin + TimeSpan.FromSeconds(0.1).Ticks);
        _time.Advance(ClassMicrophoneWatch.Interval);
        await Wait.UntilAsync(() => Volatile.Read(ref looks) >= 2);
        await Wait.UntilAsync(() => recording.GetSnapshot().Microphone?.Written >= TimeSpan.FromSeconds(0.11));
        var gate = recording.Gate!.State;
        var summary = recording.Stop();

        var samples = summary.MicrophoneSegments.SelectMany(part => TestAudio.ReadWav(part).Samples).ToArray();
        Assert.Multiple(() =>
        {
            Assert.That(samples, Has.Some.EqualTo(9), "your voice was written");
            Assert.That(gate, Is.EqualTo(MicrophoneGateState.Recording));
            Assert.That(recording.Watch.Failure, Is.Null, "the watch never stopped");
            Assert.That(_logs.Problems, Has.None.Contains("microphone watch stopped"));
        });
    }

    /// <summary>
    /// The Zoom app's own launcher: the class recorded from the Zoom app, and your microphone following it. The Zoom app's
    /// session on the microphone answers E_INVALIDARG when asked for its state at every look, so the sessions alone cannot
    /// say whether it listens; while Windows' usage record says the Zoom app is using the microphone, the record stands in
    /// for the state, the gate opens, and your voice is written beside the class. On 20 October the watch read nothing
    /// known at every look, and your voice was dropped for the whole class.
    /// </summary>
    [Test]
    public async Task Following_the_Zoom_app_whose_session_cannot_be_read_your_voice_is_recorded_while_the_usage_record_says_it_uses_the_microphone()
    {
        var record = new FakeMicrophoneUsageRecord();
        record.Entries.Add(FakeMicrophoneUsageRecord.Desktop("Zoom.exe", inUse: true));
        using var run = ZoomAppWithYourMicrophone(record);

        var (samples, gate, why) = await run.RecordAsync(_time);

        Assert.Multiple(() =>
        {
            Assert.That(samples, Has.Some.EqualTo(9), "your voice was written");
            Assert.That(gate, Is.EqualTo(MicrophoneGateState.Recording), why);
            Assert.That(run.Recording.Watch!.Failure, Is.Null, "the watch never stopped");
            Assert.That(_logs.Messages, Has.Some.StartsWith("Windows would not give the state of zoom's session on"), "said why the record decides");
        });
    }

    /// <summary>
    /// The same, with nothing in Windows' usage record about the Zoom app: nothing is known of its listening, so nothing
    /// from the microphone is written, and the gate says, in the words that become the run's Alert after 10 seconds
    /// (CANNOT READ THE MICROPHONE SESSIONS), whose session could not be read and which of Windows' calls failed. The
    /// watch keeps looking, so that your voice is recorded again as soon as the session can be read.
    /// </summary>
    [Test]
    public async Task Following_the_Zoom_app_whose_session_cannot_be_read_nothing_is_written_without_the_usage_record_and_the_gate_says_whose()
    {
        using var run = ZoomAppWithYourMicrophone(new FakeMicrophoneUsageRecord());

        var (samples, gate, why) = await run.RecordAsync(_time);

        Assert.Multiple(() =>
        {
            Assert.That(samples, Has.None.EqualTo(9), "nothing of your voice was written");
            Assert.That(gate, Is.EqualTo(MicrophoneGateState.Unreadable));
            Assert.That(why, Is.EqualTo($"cannot read zoom's microphone session: session 2 of 2 (Zoom, pid 9090) not readable: GetState: {InvalidArgumentWords} (0x80070057)"));
            Assert.That(run.Recording.Watch!.Failure, Is.Null, "the watch never stopped");
        });
    }

    /// <summary>Windows' own words for E_INVALIDARG, "The parameter is incorrect." on an English Windows.</summary>
    private static string InvalidArgumentWords => Marshal.GetPInvokeErrorMessage(FakeResults.InvalidArgument).Trim();

    /// <summary>
    /// A recording as the Zoom app's launcher makes it: the class from the Zoom app's process, your microphone (the
    /// array) following the Zoom app, read through the real sessions reader, on which the Zoom app's session answers
    /// E_INVALIDARG when asked for its state; <paramref name="record"/> is Windows' usage record. The class app's mute
    /// is set Visible, so that the gate is armed from the start.
    /// </summary>
    private ZoomRun ZoomAppWithYourMicrophone(FakeMicrophoneUsageRecord record)
    {
        var reader = SessionsReader(Array.Id, Array.Name);
        var theClass = new FakeCaptureProvider();
        theClass.ConnectDevice("Speakers");
        var zoom = theClass.AddApp(ZoomMeeting, "Zoom", "Speakers", capture: new FakeAudioCapture(Pcm48KMono, "Zoom", $"process:{ZoomMeeting}") { DeliversContinuously = true })!;
        var microphones = new FakeMicrophoneProvider().Connect(Array);
        var microphone = microphones.Prepare(new FakeAudioCapture(Pcm48KMono, Array.Name, Array.Id) { DeliversContinuously = true });
        var recording = (TrackSet)Factory(theClass, reader, microphones, new RecordingOptions { ClassAppMute = ClassAppMute.Visible }, record)
            .Create(new SourcePlan(RecordingMode.ClassAndMicrophone, new ClassSource.App("zoom"), new MicrophoneChoice(null, Follows: null)), new RecordingClock(_time));
        return new ZoomRun(reader, recording, zoom, microphone, _files);
    }

    /// <summary>
    /// The sessions reader over a fake of Windows' enumerator with one microphone, <paramref name="endpointId"/> named
    /// <paramref name="name"/>, on which chrome's audio service is listening and the Zoom app holds a session that answers
    /// E_INVALIDARG when asked for its state.
    /// </summary>
    private static WasapiMicrophoneSessions SessionsReader(string endpointId, string name)
    {
        var endpoint = new FakeMicrophoneEndpoint(endpointId, name,
            new FakeCaptureSessionControl("chrome", Laptop.ChromeUtility),
            new FakeCaptureSessionControl("zoom", Laptop.Zoom) { StateFailure = FakeResults.InvalidArgument });
        return new WasapiMicrophoneSessions(new FakeProcessNames(), NullLogger<WasapiMicrophoneSessions>.Instance, () => new FakeCaptureEnumerator(endpoint),
            device => ((FakeMicrophoneEndpoint)device).Name);
    }

    /// <summary>
    /// The registered recording factory over <paramref name="loopback"/> and <paramref name="sessions"/>, with
    /// <paramref name="microphones"/> (none unless given), <paramref name="options"/> (the defaults unless given) and
    /// <paramref name="record"/>, Windows' usage record (an empty one unless given).
    /// </summary>
    private TrackSetFactory Factory(ILoopbackCaptureProvider loopback, IMicrophoneSessions sessions, FakeMicrophoneProvider? microphones = null, RecordingOptions? options = null,
        FakeMicrophoneUsageRecord? record = null) =>
        new(loopback, microphones ?? new FakeMicrophoneProvider(), sessions, record ?? new FakeMicrophoneUsageRecord(), new FakeProcessNames(), new FakeOwnProcesses(Laptop.OwnProcess),
            new FreezeWatch(new FakeSystemTimes(_time), _time, NullLogger<FreezeWatch>.Instance), TestOptions.Create(options ?? new RecordingOptions()),
            TestOptions.Create(new TranscriptOptions()), _time, _logs);

    /// <summary>Ten milliseconds of mono 16-bit audio at 48 kHz, every sample <paramref name="value"/>: a packet.</summary>
    private static byte[] Packet(short value) => TestAudio.Shorts(Enumerable.Repeat(value, 480).ToArray());

    /// <summary>How many looks have arrived, read under the list's lock, as the watch's thread adds to it.</summary>
    private static int Count(List<WatchLook> looks)
    {
        lock (looks) return looks.Count;
    }

    /// <summary>
    /// A recording of the class from the Zoom app with your microphone following it, as <see cref="ZoomAppWithYourMicrophone"/>
    /// makes it, with the sessions reader it reads through and the two captures a test feeds.
    /// </summary>
    /// <param name="Reader">The real sessions reader, over fakes of Windows' objects.</param>
    /// <param name="Recording">The recording.</param>
    /// <param name="ClassSound">The capture of the Zoom app's sound.</param>
    /// <param name="Microphone">The capture of your microphone.</param>
    /// <param name="Files">Where the recording writes.</param>
    private sealed record ZoomRun(WasapiMicrophoneSessions Reader, TrackSet Recording, FakeAudioCapture ClassSound, FakeAudioCapture Microphone, RecordingFiles Files)
        : IDisposable
    {
        /// <summary>
        /// Starts the recording, hands over a packet of the class and, a tenth of a second later, one of your voice, lets
        /// the watch look for a second and a half, four times a second, and stops. Returns your microphone's samples, and
        /// the gate's state with its words just before the stop.
        /// </summary>
        public async Task<(short[] Samples, MicrophoneGateState Gate, string? Why)> RecordAsync(ManualTimeProvider time)
        {
            var looks = 0;
            Recording.Watch!.Looked += (_, _) => Interlocked.Increment(ref looks);

            Recording.Start(Files, TimeSpan.Zero);
            await Wait.UntilAsync(() => Volatile.Read(ref looks) >= 1);
            var origin = time.GetTimestamp();
            ClassSound.Deliver(Packet(7), origin);
            Microphone.Deliver(Packet(9), origin + TimeSpan.FromSeconds(0.1).Ticks);
            for (var look = 2; look <= 7; look++)
            {
                time.Advance(ClassMicrophoneWatch.Interval);
                await Wait.UntilAsync(() => Volatile.Read(ref looks) >= look);
            }

            var (gate, why) = Recording.Gate!.Describe();
            var summary = Recording.Stop();
            return ([.. summary.MicrophoneSegments.SelectMany(part => TestAudio.ReadWav(part).Samples)], gate, why);
        }

        /// <summary>Disposes the recording and the reader.</summary>
        public void Dispose()
        {
            Recording.Dispose();
            Reader.Dispose();
        }
    }

    /// <summary>
    /// Windows' output devices and apps as the real device check lists them, over fakes of Windows' objects, with the
    /// captures handed out by a test's provider: the listing is the code under test, and nothing is opened.
    /// </summary>
    private sealed class WindowsListedCaptures(WasapiLoopbackCaptureProvider listings, FakeCaptureProvider captures) : ILoopbackCaptureProvider
    {
        /// <inheritdoc />
        public event EventHandler? DevicesChanged
        {
            add => listings.DevicesChanged += value;
            remove => listings.DevicesChanged -= value;
        }

        /// <inheritdoc />
        public IReadOnlyList<OutputDevice> GetOutputDevices() => listings.GetOutputDevices();

        /// <inheritdoc />
        public IReadOnlyList<PlayingApp> GetPlayingApps() => listings.GetPlayingApps();

        /// <inheritdoc />
        public PlayingAppsListing GetPlayingAppsListing() => listings.GetPlayingAppsListing();

        /// <inheritdoc />
        public IAudioCapture CreateCapture(CaptureTarget target) => captures.CreateCapture(target);

        /// <summary>Nothing to release: the test disposes the listings itself.</summary>
        public void Dispose() { }
    }
}
