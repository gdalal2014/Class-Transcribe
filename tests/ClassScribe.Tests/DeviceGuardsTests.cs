using System.Runtime.InteropServices;
using ClassScribe.Audio.Windows;
using ClassScribe.Desktop;

namespace ClassScribe.Tests;

/// <summary>
/// The guard that keeps every test from a real camera or microphone (teach-video section 11): the test run turns it on,
/// a refusal names the fake to use, and Windows' activation of a microphone's audio client refuses a capture endpoint while
/// it is on. The camera process's start is pinned in <see cref="CameraProcessStarterTests.The_real_start_is_refused_in_a_test"/>.
/// </summary>
[TestFixture]
public class DeviceGuardsTests
{
    /// <summary>The test run has the guard on before the first test, as TestGuards turns it on.</summary>
    [Test]
    public void The_test_run_refuses_the_real_devices() =>
        Assert.That(DeviceGuards.RefusingTheRealDevices, Is.True);

    /// <summary>A refusal says what the test asked for and which fake to use instead.</summary>
    [Test]
    public void A_refusal_names_what_was_asked_and_the_fake()
    {
        var refused = Assert.Throws<InvalidOperationException>(() => DeviceGuards.RefuseInTests("the real camera", "FakeCameraProcess"));

        Assert.That(refused!.Message, Does.Contain("the real camera"));
        Assert.That(refused.Message, Does.Contain("FakeCameraProcess"));
    }

    /// <summary>
    /// Windows' activator refuses a capture endpoint, a microphone, in a test, naming FakeAudioClientActivator; an output
    /// device, which the activator's own tests stand in for a microphone, passes.
    /// </summary>
    [Test]
    public void Windows_activation_refuses_a_microphone_and_lets_an_output_device_pass()
    {
        var refused = Assert.Throws<InvalidOperationException>(() => WindowsAudioClientActivator.RefuseCaptureInTests(1));

        Assert.That(refused!.Message, Does.Contain("FakeAudioClientActivator"));
        Assert.DoesNotThrow(() => WindowsAudioClientActivator.RefuseCaptureInTests(0));
    }

    /// <summary>
    /// The direction the guard reads is Windows' own: the default microphone reads as a capture endpoint and the default
    /// output device as one that plays. Only the endpoints' IMMEndpoint is read; nothing is activated or opened.
    /// </summary>
    [Test]
    [Category("WindowsAudio")]
    public void The_guard_reads_the_direction_of_Windows_own_endpoints()
    {
        var enumerator = CoreAudio.CreateEnumerator();
        try
        {
            Assert.That(FlowOfDefault(enumerator, CoreAudio.Render), Is.EqualTo(CoreAudio.Render).Or.Null, "the default output device plays");
            Assert.That(FlowOfDefault(enumerator, CoreAudio.Capture), Is.EqualTo(CoreAudio.Capture).Or.Null, "the default microphone records");
        }
        finally
        {
            CoreAudio.Release(enumerator);
        }
    }

    /// <summary>The direction of the default endpoint of <paramref name="flow"/>, as the guard reads it; null when this computer has none.</summary>
    private static int? FlowOfDefault(IMMDeviceEnumeratorRaw enumerator, int flow)
    {
        var found = enumerator.GetDefaultAudioEndpoint(flow, CoreAudio.Console, out var device);
        if (found == CoreAudio.NotFound) return null;

        Marshal.ThrowExceptionForHR(found);
        try
        {
            return WindowsAudioClientActivator.DataFlowOf(device);
        }
        finally
        {
            CoreAudio.Release(device);
        }
    }
}
