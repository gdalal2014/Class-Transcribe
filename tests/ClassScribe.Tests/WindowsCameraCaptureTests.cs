using System.Runtime.InteropServices;
using ClassScribe.Camera;
using ClassScribe.Camera.Windows;

namespace ClassScribe.Tests;

/// <summary>
/// The real MediaCapture behind ClassScribe's capture, never initialised (category WindowsCamera, run with the suite; the
/// probe fix's rule 6): a MediaCapture that is made and never initialised touches no device, so no light comes on and no
/// security program is asked. These tests measure that Windows still refuses before InitializeAsync exactly what the fake
/// refuses, so that a change in Windows shows, that the real open refuses in a test run before anything is asked of
/// Windows, and that a camera Windows does not list is an error rather than another camera. InitializeAsync is never
/// called.
/// </summary>
[TestFixture, Platform("Win"), Category("WindowsCamera")]
public class WindowsCameraCaptureTests
{
    /// <summary>
    /// On a real MediaCapture before InitializeAsync, Failed and the stream's state may be registered, while the
    /// exclusive-control event and FrameSources are refused with 0xC00D36B6 MF_E_NOT_INITIALIZED, as finding 7 showed and
    /// as <see cref="FakeCameraCapture"/> refuses them; ClassScribe's route registers neither before it.
    /// </summary>
    [Test]
    public void Windows_refuses_before_InitializeAsync_what_the_fake_refuses()
    {
        using var capture = new WindowsCameraCapture(LaptopCameras.Obsbot.Id, "a camera never opened");

        Assert.DoesNotThrow(() => capture.AddFailed(_ => { }));
        Assert.DoesNotThrow(() => capture.AddStreamStateChanged(_ => { }));
        var exclusive = Assert.Catch(() => capture.AddExclusiveControlChanged(_ => { }));
        var sources = Assert.Catch(() => capture.FindColourSource());

        Assert.That(exclusive!.HResult, Is.EqualTo(CameraHresults.NotInitialized), exclusive.ToString());
        Assert.That(sources!.HResult, Is.EqualTo(CameraHresults.NotInitialized), sources.ToString());
        Assert.That(CameraRoute.NeedInitialization, Does.Contain("CaptureDeviceExclusiveControlStatusChanged +=").And.Contain("FrameSources"));
    }

    /// <summary>
    /// The real open refuses in a test run before anything is asked of Windows (the device guard), naming the fake to use
    /// instead, so that a test that forgets the fake fails at once rather than lighting the camera.
    /// </summary>
    [Test]
    public void The_real_open_refuses_in_a_test_run()
    {
        using var capture = new WindowsCameraCapture(LaptopCameras.Obsbot.Id, "a camera never opened");

        var refused = Assert.Throws<InvalidOperationException>(() => capture.InitializeShared(cpuFrames: true));

        Assert.That(refused!.Message, Does.Contain("MediaCapture.InitializeAsync").And.Contain("FakeCameraCapture"));
    }

    /// <summary>
    /// A camera Windows does not list is an error, never Windows' default camera in its place (the probe fix's rule 3):
    /// finding the source group of a camera id no device has throws, naming the camera, though this computer lists other
    /// cameras whose groups a fallback would take. Listing the source groups opens nothing, and InitializeAsync is never
    /// called.
    /// </summary>
    [Test]
    public void A_camera_Windows_does_not_list_is_an_error_never_the_default_camera()
    {
        using var capture = new WindowsCameraCapture(@"\\?\no such camera", "OBSBOT");

        var refused = Assert.Throws<InvalidOperationException>(capture.FindSourceGroup);

        Assert.That(refused!.Message, Is.EqualTo("Windows no longer lists the camera \"OBSBOT\"; is it unplugged? Windows' default camera is never opened in its place"));
    }

    /// <summary>
    /// The open asks Windows for the camera shared and read only, video alone, never with exclusive control (Gulzar's decision
    /// of 21 October: his face sometimes shows in the class app, which must keep the camera): the settings InitializeAsync is
    /// given, made here without a device. A record job's frames come in CPU memory; the other jobs leave the memory to Windows.
    /// </summary>
    [TestCase(true, global::Windows.Media.Capture.MediaCaptureMemoryPreference.Cpu)]
    [TestCase(false, global::Windows.Media.Capture.MediaCaptureMemoryPreference.Auto)]
    public void The_camera_is_only_ever_asked_for_shared(bool cpuFrames, global::Windows.Media.Capture.MediaCaptureMemoryPreference memory)
    {
        var settings = WindowsCameraCapture.SettingsFor(group: null, cpuFrames);

        Assert.That(settings.SharingMode, Is.EqualTo(global::Windows.Media.Capture.MediaCaptureSharingMode.SharedReadOnly));
        Assert.That(settings.StreamingCaptureMode, Is.EqualTo(global::Windows.Media.Capture.StreamingCaptureMode.Video));
        Assert.That(settings.MemoryPreference, Is.EqualTo(memory));
    }

    /// <summary>The fake's refusal is the same exception type and code Windows gives, so the route's tests stand for Windows.</summary>
    [Test]
    public void The_fake_refuses_with_Windows_own_exception_and_code()
    {
        var fake = new FakeCameraCapture();

        var refused = Assert.Throws<COMException>(() => fake.AddExclusiveControlChanged(_ => { }));

        Assert.That(refused!.HResult, Is.EqualTo(CameraHresults.NotInitialized));
        Assert.That(fake.TooEarly, Is.EqualTo(new[] { nameof(ICameraCapture.AddExclusiveControlChanged) }));
    }
}
