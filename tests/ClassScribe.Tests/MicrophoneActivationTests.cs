using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using ClassScribe.Audio;
using ClassScribe.Audio.Microphones;
using ClassScribe.Audio.Windows;
using Microsoft.Extensions.Logging;
using NAudio.Wave;

namespace ClassScribe.Tests;

/// <summary>
/// How a microphone's audio client is opened: unprocessed audio (RAW) asked for through ClassScribe's own IAudioClient2
/// declaration before the stream is opened, and Windows' sound processing (Default) taken instead when the microphone
/// does not offer RAW, when Windows refuses it, when there is no IAudioClient2, or at the attempt after the stream
/// failed to open with RAW set; and the line that says how the microphone is recorded, written once the stream has
/// opened. The audio client is a stand-in (see TestDoubles.Capture.cs): no microphone is opened, and nothing is
/// recorded or played.
/// </summary>
[TestFixture, Platform("Win")]
[SupportedOSPlatform("windows")]
public sealed class MicrophoneActivationTests
{
    /// <summary>The endpoint id of the fake microphone array.</summary>
    private const string ArrayId = "{0.0.1.00000000}.{array}";

    /// <summary>The endpoint id of a fake USB microphone.</summary>
    private const string UsbId = "{0.0.1.00000000}.{usb}";

    /// <summary>AUDCLNT_E_RAW_MODE_UNSUPPORTED: Windows' answer when the endpoint cannot give unprocessed audio.</summary>
    private const int RawModeUnsupported = unchecked((int)0x88890027);

    /// <summary>E_INVALIDARG, which Windows answers for client properties it does not accept.</summary>
    private const int InvalidArgument = unchecked((int)0x80070057);

    /// <summary>AUDCLNT_E_UNSUPPORTED_FORMAT: a stream that could not open in the format asked for.</summary>
    private const int UnsupportedFormat = unchecked((int)0x88890008);

    /// <summary>AUDCLNT_E_DEVICE_IN_USE: another program holds the microphone exclusively, whatever the processing asked for.</summary>
    private const int DeviceInUse = unchecked((int)0x8889000A);

    /// <summary>D5's line 2 for the array recorded without Windows' sound processing.</summary>
    private const string ArrayRaw = "Microphone: \"Microphone Array\", without Windows' sound processing (RAW)";

    private static readonly WaveFormat Mix = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);

    private RecordingLogger<MicrophoneActivation> _log = null!;
    private FakePacketSource _packets = null!;

    /// <summary>Creates a log to inspect and the packets the captures read, which are none.</summary>
    [SetUp]
    public void SetUp()
    {
        _log = new RecordingLogger<MicrophoneActivation>();
        _packets = new FakePacketSource();
    }

    /// <summary>Frees the packets' memory; every capture started by a test has been stopped by then.</summary>
    [TearDown]
    public void TearDown() => _packets.Dispose();

    /// <summary>A factory over a fake activator that answers every endpoint with <paramref name="activate"/>, whose captures read the test's packets.</summary>
    private MicrophoneActivation Factory(Func<string, ActivatedClient> activate) => new(new FakeAudioClientActivator(activate), _log, _packets);

    /// <summary>The lines that say how a microphone is recorded, each with its level, in order.</summary>
    private IReadOnlyList<(LogLevel Level, string Message)> HowRecorded =>
        [.. _log.Entries.Where(entry => entry.Message.StartsWith("Microphone: ", StringComparison.Ordinal))];

    /// <summary>
    /// Opens the stream of <paramref name="capture"/>, made around <paramref name="client"/>, and closes it again at
    /// once, so that the factory learns that it opened; nothing is read.
    /// </summary>
    private static async Task OpenAndCloseAsync(IAudioCapture capture, FakeAudioClient client)
    {
        capture.Start();
        capture.Stop();
        await Wait.UntilAsync(() => client.StopCalls == 1);
    }

    /// <summary>
    /// With RAW asked for and offered, the client's properties are set before the stream is opened: 16 bytes, not
    /// offloaded, the Other category and the RAW option, through ClassScribe's own declaration. The capture says it
    /// records without Windows' sound processing, and once the stream has opened the log says so too, before any audio.
    /// </summary>
    [Test]
    public async Task RAW_is_asked_for_through_the_own_declaration_before_the_stream_is_opened()
    {
        var client = new FakeAudioClient2(Mix);
        var factory = Factory(_ => new ActivatedClient(client, RawSupported: true));

        var capture = (WasapiAudioCapture)factory.Open(ArrayId, "Microphone Array", MicrophoneProcessing.Raw);
        var propertiesSetBeforeOpening = client.ClientProperties is not null && client.InitializeCalls == 0;
        await OpenAndCloseAsync(capture, client);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(propertiesSetBeforeOpening, Is.True, "the client's properties were set before the stream was opened");
            Assert.That(client.ClientProperties!.Value.Size, Is.EqualTo(16u));
            Assert.That(client.ClientProperties.Value.IsOffload, Is.Zero);
            Assert.That(client.ClientProperties.Value.Category, Is.EqualTo(AudioClient2.CategoryOther));
            Assert.That(client.ClientProperties.Value.Options, Is.EqualTo(AudioClient2.StreamOptionsRaw));
            Assert.That(capture.Processing, Is.EqualTo(MicrophoneProcessing.Raw));
            Assert.That(capture.DeviceId, Is.EqualTo(ArrayId));
            Assert.That(capture.DeviceName, Is.EqualTo("Microphone Array"));
            Assert.That(capture.DeliversContinuously, Is.True);
            Assert.That(HowRecorded, Is.EqualTo(new[] { (LogLevel.Information, ArrayRaw) }));
        }
    }

    /// <summary>A microphone that does not report RAW support is opened with Windows' sound processing, and its properties are never set.</summary>
    [Test]
    public async Task A_microphone_that_does_not_offer_RAW_is_opened_with_Windows_processing()
    {
        var client = new FakeAudioClient2(Mix);
        var factory = Factory(_ => new ActivatedClient(client, RawSupported: false));

        var capture = (WasapiAudioCapture)factory.Open(UsbId, "OBSBOT", MicrophoneProcessing.Raw);
        await OpenAndCloseAsync(capture, client);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(client.SetClientPropertiesCalls, Is.Zero);
            Assert.That(capture.Processing, Is.EqualTo(MicrophoneProcessing.Default));
            Assert.That(HowRecorded, Is.EqualTo(new[] { (LogLevel.Information, "Microphone: \"OBSBOT\", with Windows' sound processing (RAW is not offered by this microphone)") }));
        }
    }

    /// <summary>When Windows refuses RAW with one of its two documented answers, the same client opens with Windows' sound processing, and the line gives the code.</summary>
    [TestCase(RawModeUnsupported, "0x88890027")]
    [TestCase(InvalidArgument, "0x80070057")]
    public async Task RAW_refused_by_Windows_falls_back_to_Windows_processing(int result, string code)
    {
        var client = new FakeAudioClient2(Mix) { ClientPropertiesResult = result };
        var factory = Factory(_ => new ActivatedClient(client, RawSupported: true));

        var capture = (WasapiAudioCapture)factory.Open(ArrayId, "Microphone Array", MicrophoneProcessing.Raw);
        await OpenAndCloseAsync(capture, client);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(client.SetClientPropertiesCalls, Is.EqualTo(1));
            Assert.That(capture.Processing, Is.EqualTo(MicrophoneProcessing.Default));
            Assert.That(HowRecorded, Is.EqualTo(new[] { (LogLevel.Information, $"Microphone: \"Microphone Array\", with Windows' sound processing (RAW was not available: {code})") }));
            Assert.That(_log.Problems, Is.Empty, "a documented refusal is no problem");
        }
    }

    /// <summary>An answer Windows does not document for RAW is still taken as a refusal, but warned about, so that it is seen.</summary>
    [Test]
    public async Task An_undocumented_refusal_of_RAW_is_warned_about_and_falls_back()
    {
        var client = new FakeAudioClient2(Mix) { ClientPropertiesResult = unchecked((int)0x80004005) };   // E_FAIL
        var factory = Factory(_ => new ActivatedClient(client, RawSupported: true));

        var capture = (WasapiAudioCapture)factory.Open(ArrayId, "Microphone Array", MicrophoneProcessing.Raw);
        await OpenAndCloseAsync(capture, client);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(capture.Processing, Is.EqualTo(MicrophoneProcessing.Default));
            Assert.That(HowRecorded, Is.EqualTo(new[] { (LogLevel.Warning, "Microphone: \"Microphone Array\", with Windows' sound processing (RAW was not available: 0x80004005)") }));
        }
    }

    /// <summary>A client without IAudioClient2, as on a Windows before 8 or a driver of its own, opens with Windows' sound processing.</summary>
    [Test]
    public async Task Without_IAudioClient2_the_microphone_is_opened_with_Windows_processing()
    {
        var client = new FakeAudioClient(Mix);
        var factory = Factory(_ => new ActivatedClient(client, RawSupported: true));

        var capture = (WasapiAudioCapture)factory.Open(ArrayId, "Microphone Array", MicrophoneProcessing.Raw);
        await OpenAndCloseAsync(capture, client);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(capture.Processing, Is.EqualTo(MicrophoneProcessing.Default));
            Assert.That(HowRecorded, Is.EqualTo(new[] { (LogLevel.Information, "Microphone: \"Microphone Array\", with Windows' sound processing (RAW was not available: Windows offers no IAudioClient2 for this microphone)") }));
        }
    }

    /// <summary>Default asked for is what is opened, without touching the client's properties, even when RAW is offered.</summary>
    [Test]
    public async Task Windows_processing_asked_for_leaves_the_clients_properties_alone()
    {
        var client = new FakeAudioClient2(Mix);
        var factory = Factory(_ => new ActivatedClient(client, RawSupported: true));

        var capture = (WasapiAudioCapture)factory.Open(ArrayId, "Microphone Array", MicrophoneProcessing.Default);
        await OpenAndCloseAsync(capture, client);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(client.SetClientPropertiesCalls, Is.Zero);
            Assert.That(capture.Processing, Is.EqualTo(MicrophoneProcessing.Default));
            Assert.That(HowRecorded, Is.EqualTo(new[] { (LogLevel.Information, "Microphone: \"Microphone Array\", with Windows' sound processing") }));
        }
    }

    /// <summary>
    /// The line that says how the microphone is recorded is written once the stream has opened, not at the open, so
    /// that it never claims a recording that then fails to start. It is at Information the first time and whenever it
    /// changes, as when the recording moves to another microphone and back, and at Debug when a reopening (V on again,
    /// or the class app opening the microphone again) would only repeat the last line.
    /// </summary>
    [Test]
    public async Task How_the_microphone_is_recorded_is_said_once_the_stream_opens_and_not_repeated()
    {
        var clients = new List<FakeAudioClient2>();
        var factory = Factory(_ =>
        {
            var client = new FakeAudioClient2(Mix);
            clients.Add(client);
            return new ActivatedClient(client, RawSupported: true);
        });

        var first = factory.Open(ArrayId, "Microphone Array", MicrophoneProcessing.Raw);
        var saidAtTheOpen = HowRecorded;
        await OpenAndCloseAsync(first, clients[0]);
        await OpenAndCloseAsync(factory.Open(ArrayId, "Microphone Array", MicrophoneProcessing.Raw), clients[1]);
        await OpenAndCloseAsync(factory.Open(UsbId, "OBSBOT", MicrophoneProcessing.Raw), clients[2]);
        await OpenAndCloseAsync(factory.Open(ArrayId, "Microphone Array", MicrophoneProcessing.Raw), clients[3]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(saidAtTheOpen, Is.Empty, "nothing is said until the stream has opened");
            Assert.That(HowRecorded, Is.EqualTo(new[]
            {
                (LogLevel.Information, ArrayRaw),
                (LogLevel.Debug, ArrayRaw),
                (LogLevel.Information, "Microphone: \"OBSBOT\", without Windows' sound processing (RAW)"),
                (LogLevel.Information, ArrayRaw)
            }));
        }
    }

    /// <summary>
    /// When the stream fails to open after RAW was set, the failure reaches the caller as before and nothing claims
    /// that the microphone is recorded; the endpoint is remembered, and the next attempt on it opens with Windows'
    /// sound processing without asking for RAW again, saying so once it has opened; another microphone still gets RAW.
    /// </summary>
    [Test]
    public async Task After_the_stream_fails_to_open_with_RAW_set_the_next_attempt_uses_Windows_processing()
    {
        var clients = new List<FakeAudioClient2>();
        var factory = Factory(_ =>
        {
            var client = new FakeAudioClient2(Mix) { InitializeResult = clients.Count == 0 ? UnsupportedFormat : 0 };
            clients.Add(client);
            return new ActivatedClient(client, RawSupported: true);
        });

        var first = factory.Open(ArrayId, "Microphone Array", MicrophoneProcessing.Raw);
        var failure = Assert.Throws<COMException>(first.Start);
        var saidAfterTheFailure = HowRecorded;
        var second = (WasapiAudioCapture)factory.Open(ArrayId, "Microphone Array", MicrophoneProcessing.Raw);
        await OpenAndCloseAsync(second, clients[1]);
        var other = (WasapiAudioCapture)factory.Open(UsbId, "OBSBOT", MicrophoneProcessing.Raw);
        await OpenAndCloseAsync(other, clients[2]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(failure!.HResult, Is.EqualTo(UnsupportedFormat), "the failure reaches the caller as it is");
            Assert.That(saidAfterTheFailure, Is.Empty, "a stream that did not open is not said to be recorded");
            Assert.That(clients[0].SetClientPropertiesCalls, Is.EqualTo(1));
            Assert.That(clients[1].SetClientPropertiesCalls, Is.Zero, "RAW is not asked for again on that microphone");
            Assert.That(second.Processing, Is.EqualTo(MicrophoneProcessing.Default));
            Assert.That(other.Processing, Is.EqualTo(MicrophoneProcessing.Raw), "another microphone still gets RAW");
            Assert.That(_log.Problems, Does.Contain("The microphone \"Microphone Array\" could not be opened without Windows' sound processing (0x88890008); the next attempt opens it with Windows' sound processing"));
            Assert.That(HowRecorded, Is.EqualTo(new[]
            {
                (LogLevel.Information, "Microphone: \"Microphone Array\", with Windows' sound processing (RAW was set, but the microphone could not be opened with it: 0x88890008)"),
                (LogLevel.Information, "Microphone: \"OBSBOT\", without Windows' sound processing (RAW)")
            }));
        }
    }

    /// <summary>
    /// A stream that fails to open for a reason that has nothing to do with RAW, such as another program holding the
    /// microphone exclusively, does not give RAW up: the next attempt asks for it again, and the window hears how the
    /// microphone is recorded once, when it opens, not at the attempt that failed.
    /// </summary>
    [Test]
    public async Task A_failure_unrelated_to_RAW_does_not_give_RAW_up()
    {
        var clients = new List<FakeAudioClient2>();
        var factory = Factory(_ =>
        {
            var client = new FakeAudioClient2(Mix) { InitializeResult = clients.Count == 0 ? DeviceInUse : 0 };
            clients.Add(client);
            return new ActivatedClient(client, RawSupported: true);
        });

        var first = factory.Open(ArrayId, "Microphone Array", MicrophoneProcessing.Raw);
        Assert.Throws<COMException>(first.Start);
        var second = (WasapiAudioCapture)factory.Open(ArrayId, "Microphone Array", MicrophoneProcessing.Raw);
        await OpenAndCloseAsync(second, clients[1]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(clients[1].SetClientPropertiesCalls, Is.EqualTo(1), "RAW asked for again");
            Assert.That(second.Processing, Is.EqualTo(MicrophoneProcessing.Raw));
            Assert.That(HowRecorded, Is.EqualTo(new[] { (LogLevel.Information, ArrayRaw) }), "said once, when the microphone opened");
            Assert.That(_log.Problems, Is.Empty);
        }
    }

    /// <summary>A microphone Windows cannot activate, because it has gone, fails the open with Windows' failure, noted in the log.</summary>
    [Test]
    public void A_microphone_that_cannot_be_activated_fails_the_open()
    {
        var factory = Factory(_ => throw new COMException("The device is not present.", FakeResults.DeviceInvalidated));

        var failure = Assert.Throws<COMException>(() => factory.Open(ArrayId, "Microphone Array", MicrophoneProcessing.Raw));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(failure!.HResult, Is.EqualTo(FakeResults.DeviceInvalidated));
            Assert.That(_log.Entries, Does.Contain((LogLevel.Debug, "Could not open the microphone \"Microphone Array\" (0x88890004)")));
        }
    }
}
