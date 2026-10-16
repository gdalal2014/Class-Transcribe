using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using ClassScribe.Audio;
using ClassScribe.Audio.Windows;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace ClassScribe.Tests;

/// <summary>
/// The Windows capture classes against Windows' own audio stack. Only the tests in the AudioDevice
/// category open the computer's output devices (to listen; nothing is played), so they run only when
/// asked for by name: dotnet test --filter TestCategory=AudioDevice.
/// </summary>
[TestFixture, Platform("Win")]
[SupportedOSPlatform("windows")]
public class WindowsCaptureTests
{
    /// <summary>The category of the tests that open a real output device.</summary>
    private const string AudioDevice = "AudioDevice";

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
        Assert.That(WasapiLoopbackAudioCapture.ChannelMaskOf(new WaveFormatExtensible(48000, 32, 6)), Is.EqualTo(0x3F));
        Assert.That(WasapiLoopbackAudioCapture.ChannelMaskOf(new WaveFormatExtensible(48000, 32, 2)), Is.EqualTo(0x3));
        Assert.That(WasapiLoopbackAudioCapture.ChannelMaskOf(WaveFormat.CreateIeeeFloatWaveFormat(48000, 2)), Is.Zero);
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
