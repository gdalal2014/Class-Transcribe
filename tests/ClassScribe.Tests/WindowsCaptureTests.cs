using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using ClassScribe.Audio;
using ClassScribe.Audio.Microphones;
using ClassScribe.Audio.Windows;
using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace ClassScribe.Tests;

/// <summary>
/// The Windows capture classes: the capture loop over a stand-in audio client and stand-in packets (see
/// TestDoubles.Capture.cs), the provider's listing of what is heard in the room over fake devices, and, in the
/// WindowsAudio category, the real device list. Only the tests in the AudioDevice category open the computer's
/// output devices (to listen; nothing is played), so they run only when asked for by name:
/// dotnet test --filter TestCategory=AudioDevice. No test here touches a microphone.
/// </summary>
[TestFixture, Platform("Win")]
[SupportedOSPlatform("windows")]
public class WindowsCaptureTests
{
    /// <summary>The category of the tests that open a real output device.</summary>
    private const string AudioDevice = "AudioDevice";

    /// <summary>The mix format the stand-in clients report: 32-bit float, stereo, 48 kHz, with the speakers named.</summary>
    private static readonly WaveFormat Mix = new WaveFormatExtensible(48000, 32, 2);

    private RecordingLogger<WasapiLoopbackCaptureProvider> _log = null!;

    /// <summary>Creates a log to inspect.</summary>
    [SetUp]
    public void SetUp() => _log = new RecordingLogger<WasapiLoopbackCaptureProvider>();

    /// <summary>Creates a provider that only a weak reference points to. Not inlined, so no hidden local keeps it alive.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private WeakReference CreateUnreferenced() => new(new WasapiLoopbackCaptureProvider(_log));

    /// <summary>Disposes the provider behind a weak reference, if it is still there. Not inlined, for the same reason.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void DisposeTarget(WeakReference reference)
    {
        if (reference.Target is IDisposable target) target.Dispose();
    }

    /// <summary>Runs the garbage collector until everything collectable is gone.</summary>
    private static void Collect()
    {
        for (var round = 0; round < 3; round++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
    }

    /// <summary>
    /// Windows keeps only a plain pointer to the provider for its device notifications, so the provider
    /// keeps itself alive while it is registered, and lets go once it is disposed.
    /// </summary>
    [Test]
    public void The_provider_stays_alive_while_windows_may_call_it_and_is_let_go_after()
    {
        var provider = CreateUnreferenced();
        Collect();
        var aliveWhileRegistered = provider.IsAlive;
        DisposeTarget(provider);
        Collect();

        Assert.That(aliveWhileRegistered, Is.True, "collected while Windows could still call it");
        Assert.That(provider.IsAlive, Is.False, "still held after it was disposed");
    }

    /// <summary>Disposing works even when Windows has already dropped the registration for notifications: nothing is thrown at the app's exit.</summary>
    [Test]
    public void Disposing_survives_a_registration_windows_already_dropped()
    {
        var provider = new WasapiLoopbackCaptureProvider(_log);
        var enumerator = (MMDeviceEnumerator)typeof(WasapiLoopbackCaptureProvider)
            .GetField("_enumerator", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(provider)!;
        enumerator.UnregisterEndpointNotificationCallback(provider);   // the registration goes behind the provider's back

        Assert.DoesNotThrow(provider.Dispose);
    }

    /// <summary>The channel mask is read from an extensible format, which names the speaker of each channel; a plain format names none.</summary>
    [Test]
    public void The_channel_mask_is_read_from_the_mix_format()
    {
        Assert.That(WasapiAudioCapture.ChannelMaskOf(new WaveFormatExtensible(48000, 32, 6)), Is.EqualTo(0x3F));
        Assert.That(WasapiAudioCapture.ChannelMaskOf(new WaveFormatExtensible(48000, 32, 2)), Is.EqualTo(0x3));
        Assert.That(WasapiAudioCapture.ChannelMaskOf(WaveFormat.CreateIeeeFloatWaveFormat(48000, 2)), Is.Zero);
    }

    /// <summary>
    /// Every packet is handed on with its audio, its frame count, the stamp Windows gave its first frame and Windows'
    /// flags: a packet whose stamp is flagged wrong carries no stamp, a silent packet is handed on as zeros whatever its
    /// memory held, and a break is flagged as a discontinuity. Every packet is given back to Windows.
    /// </summary>
    [Test]
    public async Task Packets_are_handed_on_with_their_frames_stamps_and_flags()
    {
        var client = new FakeAudioClient(Mix);
        using var packets = new FakePacketSource();
        var blockAlign = Mix.BlockAlign;
        var first = TestAudio.Floats(0.1f, 0.2f, 0.3f, 0.4f);
        var garbage = Enumerable.Repeat((byte)0x7F, 2 * blockAlign).ToArray();
        packets.Add(first, frames: 2, qpc100ns: 5_000_000_000);
        packets.Add(TestAudio.Floats(0.5f, 0.6f), frames: 1, qpc100ns: 5_000_416_667, AudioClientBufferFlags.TimestampError);
        packets.Add(garbage, frames: 2, qpc100ns: 5_000_625_000, AudioClientBufferFlags.Silent);
        packets.Add(TestAudio.Floats(0.7f, 0.8f), frames: 1, qpc100ns: 5_001_041_667, AudioClientBufferFlags.DataDiscontinuity);
        var capture = WasapiAudioCapture.ForMicrophone(new AudioClient(client), "mic-id", "Microphone Array", MicrophoneProcessing.Raw, _log, opened: null, packets);
        var delivered = new List<(byte[] Audio, int Frames, long? Qpc, PacketFlags Flags)>();
        capture.DataAvailable += (_, packet) =>
        {
            lock (delivered) delivered.Add((packet.Buffer[..packet.ByteCount], packet.Frames, packet.Qpc100ns, packet.Flags));
        };

        capture.Start();
        await Wait.UntilAsync(() => { lock (delivered) return delivered.Count == 4; });
        capture.Stop();
        await Wait.UntilAsync(() => client.StopCalls == 1);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(delivered[0], Is.EqualTo((first, 2, (long?)5_000_000_000, PacketFlags.None)));
            Assert.That(delivered[1].Qpc, Is.Null, "a stamp flagged wrong is handed on as none");
            Assert.That(delivered[1].Flags, Is.EqualTo(PacketFlags.TimestampError));
            Assert.That(delivered[1].Frames, Is.EqualTo(1));
            Assert.That(delivered[2].Audio, Is.All.Zero, "a silent packet is handed on as zeros");
            Assert.That(delivered[2].Audio, Has.Length.EqualTo(2 * blockAlign));
            Assert.That((delivered[2].Frames, delivered[2].Qpc, delivered[2].Flags), Is.EqualTo((2, (long?)5_000_625_000, PacketFlags.Silent)));
            Assert.That(delivered[3].Flags, Is.EqualTo(PacketFlags.Discontinuity));
            Assert.That(delivered[3].Qpc, Is.EqualTo(5_001_041_667));
            Assert.That(packets.FramesReleased, Is.EqualTo(6));
            Assert.That(_log.Problems, Is.Empty);
        }
    }

    /// <summary>
    /// The microphone form opens the device's mix format converted to PCM at Windows' default quality, without loopback and
    /// without an event, with a one-second buffer, on a thread named for the microphone; it delivers continuously, and
    /// its format names the speaker of each channel as the mix format does.
    /// </summary>
    [Test]
    public async Task The_microphone_form_opens_the_mix_format_without_loopback_on_its_own_thread()
    {
        var client = new FakeAudioClient(Mix);
        using var packets = new FakePacketSource();
        var capture = WasapiAudioCapture.ForMicrophone(new AudioClient(client), "mic-id", "Microphone Array", MicrophoneProcessing.Default, _log, opened: null, packets);

        capture.Start();
        await Wait.UntilAsync(() => packets.ThreadName is not null);
        capture.Stop();
        await Wait.UntilAsync(() => client.StopCalls == 1);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(client.ShareMode, Is.EqualTo(AudioClientShareMode.Shared));
            Assert.That(client.StreamFlags, Is.EqualTo(AudioClientStreamFlags.AutoConvertPcm | AudioClientStreamFlags.SrcDefaultQuality));
            Assert.That(client.BufferDuration, Is.EqualTo(10_000_000), "one second, in 100-nanosecond units");
            Assert.That(client.Periodicity, Is.Zero);
            Assert.That(client.StreamFormat, Is.EqualTo(client.MixFormat), "the mix format itself");
            Assert.That(client.EventHandle, Is.Null, "polled, not event driven");
            Assert.That(client.StartCalls, Is.EqualTo(1));
            Assert.That(packets.ThreadName, Is.EqualTo("ClassScribe microphone"));
            Assert.That(capture.DeliversContinuously, Is.True);
            Assert.That(capture.Processing, Is.EqualTo(MicrophoneProcessing.Default));
            Assert.That(capture.Format, Is.EqualTo(new AudioFormat(48000, 2, 32, IsFloat: true, ChannelMask: 0x3)));
        }
    }

    /// <summary>
    /// The app form keeps what process loopback needs: a loopback, event-driven stream in 48 kHz 16-bit stereo, the event
    /// handle given to Windows, on the capture thread; it delivers continuously and has no microphone processing.
    /// </summary>
    [Test]
    public async Task The_app_form_opens_an_event_driven_loopback_stream()
    {
        var client = new FakeAudioClient(Mix);
        using var packets = new FakePacketSource();
        var capture = WasapiAudioCapture.ForApp(new AudioClient(client), "process:4242", "chrome", _log, packets);

        capture.Start();
        await Wait.UntilAsync(() => packets.ThreadName is not null);
        capture.Stop();
        await Wait.UntilAsync(() => client.StopCalls == 1);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(client.StreamFlags, Is.EqualTo(AudioClientStreamFlags.Loopback | AudioClientStreamFlags.EventCallback));
            Assert.That(client.StreamFormat, Is.EqualTo(new WaveFormat(48000, 16, 2)));
            Assert.That(client.EventHandle, Is.Not.Null.And.Not.EqualTo(IntPtr.Zero), "the event Windows signals packets with");
            Assert.That(packets.ThreadName, Is.EqualTo("ClassScribe capture"));
            Assert.That(capture.DeliversContinuously, Is.True);
            Assert.That(capture.Processing, Is.Null);
            Assert.That(capture.DeviceId, Is.EqualTo("process:4242"));
            Assert.That(capture.DeviceName, Is.EqualTo("chrome"));
        }
    }

    /// <summary>
    /// A stop ends the thread, which stops the stream once and reports that it stopped exactly once, with no error, after
    /// handing on what arrived last; the thread cannot be started twice.
    /// </summary>
    [Test]
    public async Task Stopping_ends_the_thread_and_reports_once_without_an_error()
    {
        var client = new FakeAudioClient(Mix);
        using var packets = new FakePacketSource();
        var capture = WasapiAudioCapture.ForMicrophone(new AudioClient(client), "mic-id", "Microphone Array", MicrophoneProcessing.Raw, _log, opened: null, packets);
        var stops = new List<Exception?>();
        var stopped = new ManualResetEventSlim();
        capture.Stopped += (_, e) =>
        {
            lock (stops) stops.Add(e.Error);
            stopped.Set();
        };

        capture.Start();
        var startedTwice = Assert.Throws<InvalidOperationException>(capture.Start);
        await Wait.UntilAsync(() => packets.ThreadName is not null);
        capture.Stop();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(stopped.Wait(TimeSpan.FromSeconds(5)), Is.True, "reported that it stopped");
            Assert.That(stops, Is.EqualTo(new Exception?[] { null }), "stopped once, without an error");
            Assert.That(client.StopCalls, Is.EqualTo(1));
            Assert.That(startedTwice!.Message, Does.Contain("already been started"));
        }
    }

    /// <summary>
    /// A failure on the capture thread, as when the device goes away, never leaves the thread: the stream is still stopped
    /// and the failure is reported through the Stopped event for the recording to move on.
    /// </summary>
    [Test]
    public void A_failure_on_the_capture_thread_is_reported_not_thrown()
    {
        var client = new FakeAudioClient(Mix);
        using var packets = new FakePacketSource { Failure = new COMException("The device has gone.", FakeResults.DeviceInvalidated) };
        var capture = WasapiAudioCapture.ForMicrophone(new AudioClient(client), "mic-id", "Microphone Array", MicrophoneProcessing.Raw, _log, opened: null, packets);
        Exception? reported = null;
        var stopped = new ManualResetEventSlim();
        capture.Stopped += (_, e) =>
        {
            reported = e.Error;
            stopped.Set();
        };

        capture.Start();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(stopped.Wait(TimeSpan.FromSeconds(5)), Is.True, "reported that it stopped");
            Assert.That(reported, Is.InstanceOf<COMException>().With.Property("HResult").EqualTo(FakeResults.DeviceInvalidated));
            Assert.That(client.StopCalls, Is.EqualTo(1), "the stream is stopped all the same");
        }
    }

    /// <summary>
    /// A stream that will not open fails Start with Windows' failure, noted at Debug, and tells whoever asked to be told
    /// the failure, which is how the microphone factory learns that RAW was the trouble; nothing is started.
    /// </summary>
    [Test]
    public void A_stream_that_will_not_open_fails_Start_and_tells_the_factory()
    {
        var client = new FakeAudioClient(Mix) { InitializeResult = FakeResults.DeviceInvalidated };
        using var packets = new FakePacketSource();
        var told = new List<Exception?>();
        var capture = WasapiAudioCapture.ForMicrophone(new AudioClient(client), "mic-id", "Microphone Array", MicrophoneProcessing.Raw, _log, told.Add, packets);

        var failure = Assert.Throws<COMException>(capture.Start);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(failure!.HResult, Is.EqualTo(FakeResults.DeviceInvalidated));
            Assert.That(told, Is.EqualTo(new[] { failure }));
            Assert.That(client.StartCalls, Is.Zero);
            Assert.That(_log.Entries, Does.Contain((LogLevel.Debug, "Windows did not open the stream for \"Microphone Array\" (0x88890004)")));
        }
    }

    /// <summary>
    /// A stream that opens tells whoever asked to be told, once and with no failure, before the stream is started and
    /// before the capture thread reads anything: that is when the microphone factory says how the microphone is recorded.
    /// </summary>
    [Test]
    public async Task A_stream_that_opens_tells_the_factory_before_anything_is_read()
    {
        var client = new FakeAudioClient(Mix);
        using var packets = new FakePacketSource();
        var told = new List<(Exception? Failure, int StartCalls, string? Reader)>();
        var capture = WasapiAudioCapture.ForMicrophone(new AudioClient(client), "mic-id", "Microphone Array", MicrophoneProcessing.Raw, _log,
            failure => told.Add((failure, client.StartCalls, packets.ThreadName)), packets);

        capture.Start();
        await Wait.UntilAsync(() => packets.ThreadName is not null);
        capture.Stop();
        await Wait.UntilAsync(() => client.StopCalls == 1);

        Assert.That(told, Is.EqualTo(new[] { ((Exception?)null, 0, (string?)null) }), "told once, without a failure, before the stream was started or read");
    }

    /// <summary>The first packet whose stamp Windows flagged wrong is noted at Debug, once; the rest only lose their stamps.</summary>
    [Test]
    public async Task The_first_stamp_flagged_wrong_is_noted_once()
    {
        var client = new FakeAudioClient(Mix);
        using var packets = new FakePacketSource();
        var audio = TestAudio.Floats(0.1f, 0.2f);
        packets.Add(audio, frames: 1, qpc100ns: 100, AudioClientBufferFlags.TimestampError);
        packets.Add(audio, frames: 1, qpc100ns: 200, AudioClientBufferFlags.TimestampError);
        packets.Add(audio, frames: 1, qpc100ns: 300);
        var capture = WasapiAudioCapture.ForMicrophone(new AudioClient(client), "mic-id", "Microphone Array", MicrophoneProcessing.Raw, _log, opened: null, packets);
        var count = 0;
        capture.DataAvailable += (_, _) => Interlocked.Increment(ref count);

        capture.Start();
        await Wait.UntilAsync(() => Volatile.Read(ref count) == 3);
        capture.Stop();
        await Wait.UntilAsync(() => client.StopCalls == 1);

        Assert.That(_log.Entries.Where(entry => entry.Message.Contains("could not stamp")), Is.EqualTo(new[]
        {
            (LogLevel.Debug, "Windows could not stamp a packet captured from \"Microphone Array\"; such packets are placed by their arrival (further ones are not noted)")
        }));
    }

    /// <summary>Windows' flags for a packet are mapped one by one onto ClassScribe's, which number them differently.</summary>
    [TestCase(AudioClientBufferFlags.None, PacketFlags.None)]
    [TestCase(AudioClientBufferFlags.Silent, PacketFlags.Silent)]
    [TestCase(AudioClientBufferFlags.DataDiscontinuity, PacketFlags.Discontinuity)]
    [TestCase(AudioClientBufferFlags.TimestampError, PacketFlags.TimestampError)]
    [TestCase(AudioClientBufferFlags.Silent | AudioClientBufferFlags.DataDiscontinuity | AudioClientBufferFlags.TimestampError, PacketFlags.Silent | PacketFlags.Discontinuity | PacketFlags.TimestampError)]
    public void Windows_flags_are_mapped_onto_the_packets_flags(AudioClientBufferFlags windows, PacketFlags expected) =>
        Assert.That(WasapiAudioCapture.FlagsOf(windows), Is.EqualTo(expected));

    /// <summary>
    /// What is heard in the room follows the endpoint's form factor: speakers, a line-level output, S/PDIF and a display's
    /// own audio are; headphones, a headset and a form factor Windows does not give are not.
    /// </summary>
    [TestCase(1u, true, TestName = "Speakers are heard in the room")]
    [TestCase(2u, true, TestName = "A line-level output is heard in the room")]
    [TestCase(8u, true, TestName = "S/PDIF is heard in the room")]
    [TestCase(9u, true, TestName = "A display's own audio is heard in the room")]
    [TestCase(3u, false, TestName = "Headphones are not heard in the room")]
    [TestCase(5u, false, TestName = "A headset is not heard in the room")]
    [TestCase(0u, false, TestName = "A network device is not heard in the room")]
    [TestCase(10u, false, TestName = "An unknown form factor is not heard in the room")]
    [TestCase(null, false, TestName = "A device without a form factor is not heard in the room")]
    public void What_is_heard_in_the_room_follows_the_form_factor(uint? formFactor, bool heard) =>
        Assert.That(WasapiLoopbackCaptureProvider.IsHeardInTheRoom(formFactor), Is.EqualTo(heard));

    /// <summary>
    /// The listing says which output devices are heard in the room from their form factors, read once per device and
    /// remembered, except for a device whose form factor Windows does not give, which is asked again; a device that is
    /// removed is asked again when it comes back.
    /// </summary>
    [Test]
    public void The_devices_heard_in_the_room_are_listed_from_their_form_factors_read_once()
    {
        var formFactors = new Dictionary<string, uint?> { ["speakers"] = 1, ["headphones"] = 3, ["hdmi"] = 9, ["unknown"] = null };
        string[] ids = ["speakers", "headphones", "hdmi", "unknown"];
        var reads = new List<string>();
        using var provider = new WasapiLoopbackCaptureProvider(_log,
            () => new FakeDeviceEnumerator([.. ids.Select(id => new FakeOutputDevice(id))]) { DefaultId = "speakers" },
            readDeviceName: id => id,
            readProcessName: _ => null,
            readFormFactor: device =>
            {
                CoreAudio.Check(device.GetId(out var id));
                reads.Add(id);
                return formFactors[id];
            });

        var devices = provider.GetOutputDevices();
        provider.GetOutputDevices();
        provider.OnDeviceRemoved("speakers");
        provider.GetOutputDevices();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(devices, Is.EqualTo(new[]
            {
                new OutputDevice("speakers", "speakers", IsDefault: true, Level: 0, HeardInTheRoom: true),
                new OutputDevice("headphones", "headphones", IsDefault: false, Level: 0, HeardInTheRoom: false),
                new OutputDevice("hdmi", "hdmi", IsDefault: false, Level: 0, HeardInTheRoom: true),
                new OutputDevice("unknown", "unknown", IsDefault: false, Level: 0, HeardInTheRoom: false)
            }));
            Assert.That(reads.Count(id => id == "headphones"), Is.EqualTo(1), "read once and remembered");
            Assert.That(reads.Count(id => id == "speakers"), Is.EqualTo(2), "read again after the device was removed");
            Assert.That(reads.Count(id => id == "unknown"), Is.EqualTo(3), "asked again while Windows gives no form factor");
        }
    }

    /// <summary>
    /// Each app is listed with the identifier of the device it plays through, beside the device's name, so that in app
    /// mode the echo check can look up whether that device is heard in the room; an app with sound open on several
    /// devices carries the device it is loudest on.
    /// </summary>
    [Test]
    public void Each_app_is_listed_with_the_id_of_the_device_it_plays_through()
    {
        var names = new Dictionary<string, string> { ["{speakers}"] = "Speakers", ["{headphones}"] = "Headphones" };
        var processes = new Dictionary<int, string> { [5150] = "chrome", [5151] = "msedge" };
        var speakers = new FakeOutputDevice("{speakers}", 0.5f, new FakeAudioSession(5150, 0.6f));
        var headphones = new FakeOutputDevice("{headphones}", 0.3f, new FakeAudioSession(5150, 0.3f), new FakeAudioSession(5151, 0.2f));
        using var provider = new WasapiLoopbackCaptureProvider(_log,
            () => new FakeDeviceEnumerator(speakers, headphones) { DefaultId = "{speakers}" },
            readDeviceName: id => names[id],
            readProcessName: processes.GetValueOrDefault,
            readFormFactor: _ => null);

        Assert.That(provider.GetPlayingApps(), Is.EquivalentTo(new[]
        {
            new PlayingApp(5150, "chrome", "Speakers", 0.6f, DeviceId: "{speakers}"),
            new PlayingApp(5151, "msedge", "Headphones", 0.2f, DeviceId: "{headphones}")
        }));
    }

    /// <summary>
    /// The real output devices are listed with what the form factor in their property stores says, read through
    /// ClassScribe's own declarations and compared with what NAudio reads from the same stores; nothing is opened.
    /// </summary>
    [Test, Category("WindowsAudio")]
    public void The_real_devices_say_whether_they_are_heard_in_the_room_from_their_property_stores()
    {
        using var provider = new WasapiLoopbackCaptureProvider(_log);
        using var naudio = new MMDeviceEnumerator();
        var devices = provider.GetOutputDevices();
        Assume.That(devices, Is.Not.Empty, "this computer has no output device");

        foreach (var device in devices)
        {
            using var same = naudio.GetDevice(device.Id);
            var key = NAudio.CoreAudioApi.PropertyKeys.PKEY_AudioEndpoint_FormFactor;
            uint? formFactor = same.Properties.Contains(key) ? Convert.ToUInt32(same.Properties[key].Value) : null;

            Assert.That(device.HeardInTheRoom, Is.EqualTo(WasapiLoopbackCaptureProvider.IsHeardInTheRoom(formFactor)), $"{device.Name} with form factor {formFactor}");
        }

        Assert.That(_log.Problems, Is.Empty);
    }

    /// <summary>The output devices are listed with their names and levels, and one of them is the default.</summary>
    [Test, Explicit("Opens the computer's output devices"), Category(AudioDevice)]
    public void The_output_devices_are_listed_with_one_default()
    {
        using var provider = new WasapiLoopbackCaptureProvider(_log);

        var devices = provider.GetOutputDevices();

        Assume.That(devices, Is.Not.Empty, "this computer has no output device");
        Assert.That(devices.Count(device => device.IsDefault), Is.EqualTo(1));
        Assert.That(devices, Has.All.Matches<OutputDevice>(device => device.Id.Length > 0 && device.Name.Length > 0 && device.Level >= 0 && device.Level <= 1));
    }

    /// <summary>
    /// A capture of the default device opens in a format the recorder reads, says which speaker each
    /// channel feeds, and on a normal stop reports that it stopped exactly once, with no error. It only
    /// listens for a moment; nothing is played.
    /// </summary>
    [Test, Explicit("Opens the computer's output devices"), Category(AudioDevice)]
    public async Task A_capture_of_the_default_device_starts_and_stops_cleanly()
    {
        using var provider = new WasapiLoopbackCaptureProvider(_log);
        var device = provider.GetOutputDevices().FirstOrDefault(device => device.IsDefault);
        Assume.That(device, Is.Not.Null, "this computer has no output device");
        var stops = new List<Exception?>();

        var capture = provider.CreateCapture(CaptureTarget.ForDevice(device!));
        capture.Stopped += (_, e) => { lock (stops) stops.Add(e.Error); };
        capture.Start();
        await Task.Delay(300);
        capture.Stop();
        capture.Dispose();

        Assert.That(PcmConverter.IsSupported(capture.Format), Is.True, capture.Format.ToString());
        Assert.That(capture.Format.ChannelMask, Is.Not.Zero, "Windows' mix format names the speaker of each channel");
        Assert.That(capture.DeviceName, Is.EqualTo(device!.Name));
        Assert.That(stops, Is.EqualTo(new Exception?[] { null }), "stopped once, without an error");
        Assert.That(_log.Problems, Is.Empty);
    }
}
