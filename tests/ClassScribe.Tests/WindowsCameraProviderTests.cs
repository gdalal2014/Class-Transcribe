using ClassScribe.Camera;
using ClassScribe.Camera.Windows;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClassScribe.Tests;

/// <summary>
/// The real camera list (category WindowsCamera, run with the suite): Windows' device lists and frame source groups, read
/// through WinRT. Nothing is opened, so no camera's light comes on and no security program is asked; only what Windows says
/// about the cameras connected now is read.
/// </summary>
[TestFixture, Platform("Win"), Category("WindowsCamera")]
public class WindowsCameraProviderTests
{
    /// <summary>
    /// The list is read without a problem on this Windows, and each camera in it has an id, a name and a place; a camera
    /// built in is never virtual, and every id is listed once.
    /// </summary>
    [Test]
    public async Task The_cameras_are_listed_from_Windows_own_lists_without_opening_any()
    {
        var provider = new WindowsCameraProvider(NullLogger<WindowsCameraProvider>.Instance);

        var list = await provider.GetCamerasAsync(CancellationToken.None);

        Assert.That(list.Problem, Is.Null);
        Assert.That(list.Cameras.Select(camera => camera.Id), Is.Unique);
        foreach (var camera in list.Cameras)
        {
            Assert.That(camera.Id, Is.Not.Empty);
            Assert.That(camera.Name, Is.Not.Empty);
            Assert.That(camera.Place, Is.Not.EqualTo(CameraPlace.Virtual).Or.Matches<CameraPlace>(_ => camera.Id.StartsWith(@"\\?\SWD#", StringComparison.OrdinalIgnoreCase)));
        }
    }

    /// <summary>
    /// What Windows says the camera needs is read without opening anything: this Windows is new enough, the encoders are
    /// listed (an answer or none, never a failure), and Security Center's list of security programs is read or said not to
    /// be. What it found is written to the test's output, for the record of this laptop.
    /// </summary>
    [Test]
    public async Task What_Windows_says_the_camera_needs_is_read_without_opening_anything()
    {
        var system = new WindowsCameraSystem(NullLogger<WindowsCameraSystem>.Instance);

        var encoder = await system.HasH264EncoderAsync(CancellationToken.None);
        var programs = system.SecurityPrograms();

        TestContext.Out.WriteLine($"Windows {system.WindowsVersion}; H.264 encoder: {encoder?.ToString() ?? "not said"}; security programs: {(programs is null ? "not said" : string.Join(", ", programs))}");
        Assert.That(system.WindowsVersion, Is.GreaterThanOrEqualTo(CameraSetupFacts.OldestWindows));
        Assert.That(programs, Is.Null.Or.All.Not.Empty);
    }

    /// <summary>Windows' camera record is read, and has no entry for the test's own program, which never opens a camera.</summary>
    [Test]
    public void The_camera_record_is_read_and_names_no_entry_of_the_tests()
    {
        var record = new CameraUsageRecord(NullLogger<CameraUsageRecord>.Instance).Read();

        Assume.That(record.Problem, Is.Null, "this profile has no camera record");
        var own = Path.GetFileName(Environment.ProcessPath) ?? "testhost.exe";
        Assert.That(record.Apps.Where(app => app.InUse).Select(app => app.App), Has.None.EqualTo(own).IgnoreCase);
    }

    /// <summary>A list asked for with a cancel already made is cancelled rather than read.</summary>
    [Test]
    public void A_cancelled_listing_is_cancelled()
    {
        var provider = new WindowsCameraProvider(NullLogger<WindowsCameraProvider>.Instance);

        Assert.That(async () => await provider.GetCamerasAsync(new CancellationToken(canceled: true)), Throws.InstanceOf<OperationCanceledException>());
    }
}

/// <summary>
/// What the camera list answers when Windows' read fails, over a stand-in for Windows' lists: whatever Windows raises,
/// as the types WinRT maps its HRESULTs to, is a list that says why, as the microphone readers answer a failed read, since
/// the webcam hold lists the cameras inside the microphone's pick; only the caller's own cancel goes on. Nothing is read
/// from Windows.
/// </summary>
[TestFixture]
public class WindowsCameraProviderFailureTests
{
    /// <summary>
    /// A read that throws any of the types WinRT hands an HRESULT over as (file not found, cancelled by Windows with no
    /// cancel asked for, an object already disposed, a member not implemented, and the COM error it always caught) is a
    /// list with no camera and a problem naming Windows' words and the code.
    /// </summary>
    [Test]
    public async Task Whatever_Windows_raises_is_a_list_that_says_why()
    {
        Exception[] raised =
        [
            new FileNotFoundException("The system cannot find the file specified.") { HResult = unchecked((int)0x80070002) },
            new OperationCanceledException("The operation was canceled by the user.") { HResult = unchecked((int)0x800704C7) },
            new ObjectDisposedException("MediaFrameSourceGroup"),
            new NotImplementedException("Not implemented"),
            new System.Runtime.InteropServices.COMException("The device is not ready.", unchecked((int)0x80070015))
        ];

        foreach (var exception in raised)
        {
            var provider = new WindowsCameraProvider(NullLogger<WindowsCameraProvider>.Instance, _ => throw exception);

            var list = await provider.GetCamerasAsync(CancellationToken.None);

            Assert.That(list.Cameras, Is.Empty, exception.GetType().Name);
            Assert.That(list.Problem, Is.EqualTo($"Windows did not list the cameras: {exception.Message.Trim()} (0x{exception.HResult:X8})"), exception.GetType().Name);
        }
    }

    /// <summary>A read that fails asynchronously, inside the task Windows' list hands back, is answered the same way.</summary>
    [Test]
    public async Task A_read_that_fails_later_is_a_list_that_says_why()
    {
        var provider = new WindowsCameraProvider(NullLogger<WindowsCameraProvider>.Instance,
            _ => Task.FromException<IReadOnlyList<CameraDevice>>(new FileNotFoundException("The system cannot find the file specified.") { HResult = unchecked((int)0x80070002) }));

        var list = await provider.GetCamerasAsync(CancellationToken.None);

        Assert.That(list.Problem, Is.EqualTo("Windows did not list the cameras: The system cannot find the file specified. (0x80070002)"));
    }

    /// <summary>The caller's own cancel goes on as a cancel: it is the one thing the list throws.</summary>
    [Test]
    public void The_callers_own_cancel_goes_on()
    {
        using var cancel = new CancellationTokenSource();
        var provider = new WindowsCameraProvider(NullLogger<WindowsCameraProvider>.Instance, token =>
        {
            cancel.Cancel();
            token.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<CameraDevice>>([]);
        });

        Assert.That(async () => await provider.GetCamerasAsync(cancel.Token), Throws.InstanceOf<OperationCanceledException>());
    }

    /// <summary>A read that works is the list, as Windows gave it.</summary>
    [Test]
    public async Task A_read_that_works_is_the_list()
    {
        var provider = new WindowsCameraProvider(NullLogger<WindowsCameraProvider>.Instance, _ => Task.FromResult(LaptopCameras.All));

        var list = await provider.GetCamerasAsync(CancellationToken.None);

        Assert.That(list, Is.EqualTo(new CameraList(LaptopCameras.All)));
    }
}
