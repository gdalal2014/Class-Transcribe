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

    /// <summary>The registered recording factory over <paramref name="loopback"/> and <paramref name="sessions"/>, with <paramref name="microphones"/> (none unless given) and <paramref name="options"/> (the defaults unless given).</summary>
    private TrackSetFactory Factory(ILoopbackCaptureProvider loopback, IMicrophoneSessions sessions, FakeMicrophoneProvider? microphones = null, RecordingOptions? options = null) =>
        new(loopback, microphones ?? new FakeMicrophoneProvider(), sessions, new FakeMicrophoneUsageRecord(), new FakeProcessNames(), new FakeOwnProcesses(Laptop.OwnProcess),
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
        public IAudioCapture CreateCapture(CaptureTarget target) => captures.CreateCapture(target);

        /// <summary>Nothing to release: the test disposes the listings itself.</summary>
        public void Dispose() { }
    }
}
