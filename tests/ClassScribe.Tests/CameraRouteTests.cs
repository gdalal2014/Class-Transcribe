using System.Runtime.InteropServices;
using ClassScribe.Camera;

namespace ClassScribe.Tests;

/// <summary>
/// The route of a shared open (ROOT-CAUSE.md beside the camera probe, finding 7): the calls in the documented order, against
/// a fake that refuses what Windows refuses before InitializeAsync, every call named in the log with its outcome and its
/// HRESULT, the camera let go after any failure, and no way to ask for exclusive control. Nothing is opened.
/// </summary>
[TestFixture]
public class CameraRouteTests
{
    /// <summary>The OBSBOT's id, as the camera list gives it.</summary>
    private static readonly string CameraId = LaptopCameras.Obsbot.Id;

    /// <summary>The clock the calls are timed on.</summary>
    private readonly ManualTimeProvider _time = new(TestRuns.Start);

    /// <summary>
    /// The route makes the MediaCapture, registers Failed, finds the source group and calls InitializeAsync, and only then
    /// touches what needs the device: the reporting events, FrameSources and the reader. Nothing comes too early, the
    /// format is not set (a shared open cannot set it), and frames come.
    /// </summary>
    [Test]
    public void The_route_touches_nothing_that_needs_the_device_before_InitializeAsync()
    {
        var factory = new FakeCameraCaptureFactory();
        var log = new CameraCallLog(_time);
        var frames = 0;

        var opening = CameraRoute.Open(factory, CameraId, "OBSBOT Meet Flip StreamCamera", convertTo: null, log, _ => { }, () => frames++);
        factory.Last!.Frames(3);

        Assert.That(factory.Last.Calls, Is.EqualTo(new[]
        {
            nameof(ICameraCapture.AddFailed), nameof(ICameraCapture.FindSourceGroup), nameof(ICameraCapture.InitializeShared),
            nameof(ICameraCapture.AddStreamStateChanged), nameof(ICameraCapture.AddExclusiveControlChanged), nameof(ICameraCapture.FindColourSource),
            nameof(ICameraCapture.CreateReader), nameof(ICameraCapture.UseRealtime), nameof(ICameraCapture.AddFrameArrived), nameof(ICameraCapture.StartReader)
        }));
        Assert.That(factory.Last.TooEarly, Is.Empty);
        Assert.That(factory.Made.Single().CameraId, Is.EqualTo(CameraId));
        Assert.That(frames, Is.EqualTo(3));
        Assert.That(opening.Format, Is.EqualTo("MJPG 1920x1080 at 30 frames a second"));
        Assert.That(log.FirstFailure, Is.Null);
        Assert.That(log.Calls.Select(call => call.Name), Is.EqualTo(new[]
        {
            CameraRoute.Create, CameraRoute.Failed, CameraRoute.Source, CameraRoute.Initialize, CameraRoute.StreamState, CameraRoute.Exclusive,
            CameraRoute.FrameSources, CameraRoute.SetFormat, $"{CameraRoute.Reader} (as the camera sends it, nothing converted)", CameraRoute.Realtime,
            CameraRoute.FrameArrived, CameraRoute.Start
        }));
        Assert.That(log.Calls.Single(call => call.Name == CameraRoute.SetFormat).Outcome, Is.EqualTo(CameraRoute.SetFormatNotCalled));
        Assert.That(CameraRoute.Initialize, Does.Contain("SharedReadOnly"), "the only open there is");
    }

    /// <summary>
    /// The order of 21 October, the exclusive-control event registered before InitializeAsync, fails on the fake exactly as
    /// every open of that evening failed: 0xC00D36B6 MF_E_NOT_INITIALIZED, with Windows' words, before InitializeAsync, so
    /// no camera was ever asked for anything. The fake reproduces finding 7, so the route's passing above is the fix.
    /// </summary>
    [Test]
    public void The_order_of_21_October_fails_on_the_fake_as_every_open_did()
    {
        var capture = new FakeCameraCapture();
        var log = new CameraCallLog(_time);

        var failed = Assert.Throws<CameraCallFailedException>(() =>
        {
            log.Required(CameraRoute.Failed, () => capture.AddFailed(_ => { }));
            log.Required(CameraRoute.StreamState, () => capture.AddStreamStateChanged(_ => { }));
            log.Required(CameraRoute.Exclusive, () => capture.AddExclusiveControlChanged(_ => { }));
            log.Required(CameraRoute.Source, capture.FindSourceGroup);
            log.Required(CameraRoute.Initialize, () => capture.InitializeShared(cpuFrames: false));
        });

        Assert.That(failed!.Call, Is.EqualTo(CameraRoute.Exclusive));
        Assert.That(failed.Code, Is.EqualTo(CameraHresults.NotInitialized));
        Assert.That(failed.Error, Is.EqualTo("0xC00D36B6 MF_E_NOT_INITIALIZED (Windows: \"This object needs to be initialized before the requested operation can be carried out.\")"));
        Assert.That(capture.Calls, Does.Not.Contain(nameof(ICameraCapture.InitializeShared)), "the camera was never reached");
        Assert.That(log.FirstFailure!.Text, Is.EqualTo("3. MediaCapture.CaptureDeviceExclusiveControlStatusChanged +=: failed: 0xC00D36B6 MF_E_NOT_INITIALIZED (Windows: \"This object needs to be initialized before the requested operation can be carried out.\")"));
    }

    /// <summary>
    /// A failing InitializeAsync (Windows' camera privacy switch, or a security program) is reported by name with its
    /// HRESULT, its name and Windows' words, as WinRT hands E_ACCESSDENIED over (an UnauthorizedAccessException); the camera
    /// is let go, its reader never made.
    /// </summary>
    [Test]
    public void A_failing_InitializeAsync_is_reported_by_name_and_the_camera_let_go()
    {
        var factory = new FakeCameraCaptureFactory
        {
            Prepare = (capture, _) => capture.Failures[nameof(ICameraCapture.InitializeShared)] = new UnauthorizedAccessException("Access is denied.") { HResult = CameraHresults.AccessDenied }
        };
        var log = new CameraCallLog(_time);

        var failed = Assert.Throws<CameraCallFailedException>(() => CameraRoute.Open(factory, CameraId, "OBSBOT", null, log, _ => { }, () => { }));

        Assert.That(failed!.Call, Is.EqualTo(CameraRoute.Initialize));
        Assert.That(log.FirstFailure!.Text, Is.EqualTo("4. MediaCapture.InitializeAsync (SharedReadOnly, video only): failed: 0x80070005 E_ACCESSDENIED (Windows: \"Access is denied.\")"));
        Assert.That(factory.Last!.Disposed, Is.True);
        Assert.That(factory.Last.Calls, Does.Not.Contain(nameof(ICameraCapture.CreateReader)));
        Assert.That(factory.Last.Calls, Does.Not.Contain(nameof(ICameraCapture.StopReader)), "no reader was started");
        Assert.That(log.Calls[^1].Name, Is.EqualTo(CameraRoute.Dispose));
    }

    /// <summary>The events that only report are optional: a refusal of either is recorded with its HRESULT and passed over, and the open goes on.</summary>
    [Test]
    public void A_refused_reporting_event_is_passed_over()
    {
        var factory = new FakeCameraCaptureFactory
        {
            Prepare = (capture, _) => capture.Failures[nameof(ICameraCapture.AddExclusiveControlChanged)] = new COMException("Not here.", CameraHresults.NotInitialized)
        };
        var log = new CameraCallLog(_time);

        CameraRoute.Open(factory, CameraId, "OBSBOT", null, log, _ => { }, () => { });

        var exclusive = log.Calls.Single(call => call.Name == CameraRoute.Exclusive);
        Assert.That(exclusive.Failed, Is.True);
        Assert.That(exclusive.Required, Is.False);
        Assert.That(exclusive.Text, Does.EndWith("(optional: passed over)"));
        Assert.That(log.FirstFailure, Is.Null);
        Assert.That(factory.Last!.Streaming, Is.True);
    }

    /// <summary>A camera Windows no longer lists is an error naming it, before InitializeAsync: Windows' default camera is never opened in its place.</summary>
    [Test]
    public void A_camera_no_longer_listed_is_an_error_before_InitializeAsync()
    {
        var factory = new FakeCameraCaptureFactory
        {
            Prepare = (capture, _) => capture.Failures[nameof(ICameraCapture.FindSourceGroup)] =
                new InvalidOperationException("Windows no longer lists the camera \"OBSBOT\"; is it unplugged? Windows' default camera is never opened in its place")
        };
        var log = new CameraCallLog(_time);

        var failed = Assert.Throws<CameraCallFailedException>(() => CameraRoute.Open(factory, CameraId, "OBSBOT", null, log, _ => { }, () => { }));

        Assert.That(failed!.Call, Is.EqualTo(CameraRoute.Source));
        Assert.That(failed.Code, Is.Null, "ClassScribe's own refusal, not Windows'");
        Assert.That(failed.Error, Does.StartWith("InvalidOperationException: Windows no longer lists the camera \"OBSBOT\""));
        Assert.That(factory.Last!.Calls, Does.Not.Contain(nameof(ICameraCapture.InitializeShared)));
        Assert.That(factory.Last.Disposed, Is.True);
    }

    /// <summary>A reader whose start is not Success brings no frames: the open fails with the status named, and the camera is let go.</summary>
    [Test]
    public void A_start_that_is_not_Success_fails_with_its_status()
    {
        var factory = new FakeCameraCaptureFactory { Prepare = (capture, _) => capture.StartStatus = "DeviceNotAvailable" };
        var log = new CameraCallLog(_time);

        var failed = Assert.Throws<CameraCallFailedException>(() => CameraRoute.Open(factory, CameraId, "OBSBOT", null, log, _ => { }, () => { }));

        Assert.That(failed!.Call, Is.EqualTo(CameraRoute.Start));
        Assert.That(failed.Error, Is.EqualTo("DeviceNotAvailable, not Success: no frames will come"));
        Assert.That(log.FirstFailure!.Name, Is.EqualTo($"{CameraRoute.Start}'s status"));
        Assert.That(factory.Last!.Disposed, Is.True);
    }

    /// <summary>The camera check's reader converts to NV12 at the asked size; the hold's converts nothing.</summary>
    [Test]
    public void The_reader_converts_only_when_a_size_is_asked()
    {
        var factory = new FakeCameraCaptureFactory();

        var sized = CameraRoute.Open(factory, CameraId, "OBSBOT", CameraSize.P720, new CameraCallLog(_time), _ => { }, () => { });
        var asItRuns = CameraRoute.Open(factory, CameraId, "OBSBOT", null, new CameraCallLog(_time), _ => { }, () => { });

        Assert.That(sized.Reader, Is.EqualTo("NV12 1280x720"));
        Assert.That(factory.Made[0].Capture.ConvertedTo, Is.EqualTo(CameraSize.P720));
        Assert.That(asItRuns.Reader, Is.EqualTo("the camera's own format, nothing converted"));
        Assert.That(factory.Made[1].Capture.ConvertedTo, Is.Null);
    }

    /// <summary>Letting go stops the reader and disposes the capture; a stop that fails is recorded and the capture is disposed all the same.</summary>
    [Test]
    public void Letting_go_disposes_even_when_the_stop_fails()
    {
        var factory = new FakeCameraCaptureFactory();
        var log = new CameraCallLog(_time);
        var opening = CameraRoute.Open(factory, CameraId, "OBSBOT", null, log, _ => { }, () => { });
        factory.Last!.Failures[nameof(ICameraCapture.StopReader)] = new TimeoutException("MediaFrameReader.StopAsync did not return within 5 seconds");

        CameraRoute.Close(opening.Capture, log);

        Assert.That(factory.Last.Disposed, Is.True);
        Assert.That(log.Calls[^2].Name, Is.EqualTo(CameraRoute.Stop));
        Assert.That(log.Calls[^2].Failed, Is.True);
        Assert.That(log.Calls[^1].Name, Is.EqualTo(CameraRoute.Dispose));
        Assert.That(log.Calls[^1].Failed, Is.False);
    }

    /// <summary>Each call is timed on the clock; a call under way is named until it returns.</summary>
    [Test]
    public void Each_call_is_timed_and_the_one_under_way_is_named()
    {
        var log = new CameraCallLog(_time);
        string? during = null;

        log.Required(CameraRoute.Initialize, () =>
        {
            during = log.InProgress;
            _time.Advance(TimeSpan.FromMilliseconds(412));
        });

        Assert.That(during, Is.EqualTo(CameraRoute.Initialize));
        Assert.That(log.InProgress, Is.Null);
        Assert.That(log.Calls.Single().Text, Is.EqualTo("1. MediaCapture.InitializeAsync (SharedReadOnly, video only): OK (412 ms)"));
    }

    /// <summary>An HRESULT is shown with its name where it is known for certain, and with Windows' words when there are any.</summary>
    [Test]
    public void An_HRESULT_is_described_with_its_name_and_Windows_words()
    {
        Assert.That(CameraHresults.Describe(CameraHresults.AccessDenied, "Access is denied."), Is.EqualTo("0x80070005 E_ACCESSDENIED (Windows: \"Access is denied.\")"));
        Assert.That(CameraHresults.Describe(unchecked((int)0xC00D3704), null), Is.EqualTo("0xC00D3704"), "a code not checked against the headers has no name");
        Assert.That(CameraHresults.Name(CameraHresults.NotInitialized), Is.EqualTo("MF_E_NOT_INITIALIZED"));
    }
}
