using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using ClassScribe.Audio.Microphones;
using ClassScribe.Audio.Windows;
using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;

namespace ClassScribe.Tests;

/// <summary>
/// ClassScribe's own declaration of IAudioClient2 against the real Windows audio stack, on the default output device's
/// audio client, which is never initialised: no stream is opened, nothing is played, and no microphone is touched. The
/// declaration exists because NAudio 2.2.1 declares IAudioClient2 as inheriting IAudioClient, and built-in COM interop
/// ignores interface inheritance, so NAudio's SetClientProperties sits in the slot of IAudioClient's GetBufferSize.
/// </summary>
[TestFixture, Platform("Win"), Category("WindowsAudio")]
[SupportedOSPlatform("windows")]
public sealed class AudioClient2SlotTests
{
    /// <summary>AUDCLNT_E_NOT_INITIALIZED: what GetBufferSize answers on a client whose stream is not open.</summary>
    private const int NotInitialized = unchecked((int)0x88890001);

    /// <summary>AUDCLNT_E_RAW_MODE_UNSUPPORTED: Windows' answer when the endpoint cannot give unprocessed audio.</summary>
    private const int RawModeUnsupported = unchecked((int)0x88890027);

    /// <summary>
    /// On a client that is not initialised, SetClientProperties through ClassScribe's declaration reaches its own slot and
    /// succeeds (or is refused for the endpoint, never mistaken for another call), while NAudio's declaration lands on
    /// GetBufferSize, which answers "not initialised".
    /// </summary>
    [Test]
    public void SetClientProperties_reaches_its_slot_through_the_own_declaration_and_GetBufferSize_through_NAudios()
    {
        var properties = AudioClientPropertiesNative.Raw;
        WithTheDefaultOutputDevicesClient(client =>
        {
            var ours = AudioClient2.SetClientProperties((IAudioClient2Native)client, properties);

            // NAudio's declaration takes the pointer itself; its call is not PreserveSig, so a failure arrives as an exception.
            var pointer = Marshal.AllocHGlobal(Marshal.SizeOf<AudioClientPropertiesNative>());
            COMException? naudio;
            try
            {
                Marshal.StructureToPtr(properties, pointer, fDeleteOld: false);
                naudio = Assert.Throws<COMException>(() => ((NAudio.CoreAudioApi.Interfaces.IAudioClient2)client).SetClientProperties(pointer));
            }
            finally
            {
                Marshal.FreeHGlobal(pointer);
            }

            using (Assert.EnterMultipleScope())
            {
                Assert.That(ours, Is.Zero.Or.EqualTo(RawModeUnsupported), "ClassScribe's declaration: S_OK, or RAW refused for the endpoint");
                Assert.That(naudio!.HResult, Is.EqualTo(NotInitialized), "NAudio's declaration called GetBufferSize");
            }
        });
    }

    /// <summary>The client properties are laid out as Windows lays out AudioClientProperties: 16 bytes, the size first.</summary>
    [Test]
    public void The_client_properties_are_laid_out_as_Windows_lays_them_out()
    {
        var raw = AudioClientPropertiesNative.Raw;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Marshal.SizeOf<AudioClientPropertiesNative>(), Is.EqualTo(16));
            Assert.That(raw.Size, Is.EqualTo(16u));
            Assert.That(raw.IsOffload, Is.Zero);
            Assert.That(raw.Category, Is.EqualTo(AudioClient2.CategoryOther));
            Assert.That(raw.Options, Is.EqualTo(AudioClient2.StreamOptionsRaw));
            Assert.That(Marshal.OffsetOf<AudioClientPropertiesNative>(nameof(AudioClientPropertiesNative.Options)), Is.EqualTo((IntPtr)12));
        }
    }

    /// <summary>
    /// Windows' activator finds an endpoint by its id, reads from its property store whether it offers unprocessed audio,
    /// and activates its audio client without initialising it. The default output device stands in for a microphone, so
    /// that no microphone is touched; its client is released afterwards.
    /// </summary>
    [Test]
    public void The_Windows_activator_activates_a_client_on_an_endpoint_without_opening_a_stream()
    {
        using var enumerator = new MMDeviceEnumerator();
        Assume.That(enumerator.HasDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia), Is.True, "this computer has no output device");
        using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        var activator = new WindowsAudioClientActivator();

        var activated = activator.Activate(device.ID);
        try
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(Marshal.IsComObject(activated.Client), Is.True);
                Assert.That(activated.Client, Is.InstanceOf<NAudio.CoreAudioApi.Interfaces.IAudioClient>());
                Assert.That(activated.Client, Is.InstanceOf<IAudioClient2Native>());
            }
        }
        finally
        {
            CoreAudio.Release(activated.Client);
        }
    }

    /// <summary>An endpoint id Windows does not know fails the activation with Windows' "not found", and nothing is left behind.</summary>
    [Test]
    public void An_endpoint_that_does_not_exist_cannot_be_activated()
    {
        var activator = new WindowsAudioClientActivator();

        var failure = Assert.Throws<COMException>(() => activator.Activate("{0.0.1.00000000}.{00000000-0000-0000-0000-000000000000}"));

        Assert.That(failure!.HResult, Is.EqualTo(CoreAudio.NotFound));
    }

    /// <summary>
    /// The whole factory over Windows: the default output device's client, standing in for a microphone, opens as a capture
    /// in the microphone form with its mix format read, RAW asked for and either used or declined, and is released on
    /// Dispose. The capture is never started, so no stream is opened, and the line saying how it is recorded, which waits
    /// for the stream to open, is never said.
    /// </summary>
    [Test]
    public void The_factory_over_Windows_opens_a_capture_without_starting_a_stream()
    {
        using var enumerator = new MMDeviceEnumerator();
        Assume.That(enumerator.HasDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia), Is.True, "this computer has no output device");
        using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        var log = new RecordingLogger<MicrophoneActivation>();
        var factory = new MicrophoneActivation(log);

        using var capture = (WasapiAudioCapture)factory.Open(device.ID, device.FriendlyName, MicrophoneProcessing.Raw);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(capture.DeviceId, Is.EqualTo(device.ID));
            Assert.That(capture.DeviceName, Is.EqualTo(device.FriendlyName));
            Assert.That(capture.DeliversContinuously, Is.True);
            Assert.That(capture.Format.SampleRate, Is.GreaterThan(0));
            Assert.That(capture.Processing, Is.Not.Null);
            Assert.That(log.Entries.Select(entry => entry.Message), Has.None.StartsWith("Microphone: "), "the line saying how it is recorded waits for the stream to open");
            Assert.That(log.Entries.Where(entry => entry.Level >= LogLevel.Error), Is.Empty);
        }
    }

    /// <summary>Runs <paramref name="check"/> with the default output device's audio client, activated and never initialised, and releases it after.</summary>
    private static void WithTheDefaultOutputDevicesClient(Action<object> check)
    {
        var enumerator = CoreAudio.CreateEnumerator();
        try
        {
            var result = enumerator.GetDefaultAudioEndpoint(CoreAudio.Render, CoreAudio.Multimedia, out var device);
            Assume.That(result, Is.Not.EqualTo(CoreAudio.NotFound), "this computer has no output device");
            CoreAudio.Check(result);
            try
            {
                var interfaceId = AudioClient2.AudioClientId;
                CoreAudio.Check(device.Activate(ref interfaceId, CoreAudio.AllContexts, IntPtr.Zero, out var client));
                try
                {
                    check(client);
                }
                finally
                {
                    CoreAudio.Release(client);
                }
            }
            finally
            {
                CoreAudio.Release(device);
            }
        }
        finally
        {
            CoreAudio.Release(enumerator);
        }
    }
}
