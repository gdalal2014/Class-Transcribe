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

    /// <summary>A list asked for with a cancel already made is cancelled rather than read.</summary>
    [Test]
    public void A_cancelled_listing_is_cancelled()
    {
        var provider = new WindowsCameraProvider(NullLogger<WindowsCameraProvider>.Instance);

        Assert.That(async () => await provider.GetCamerasAsync(new CancellationToken(canceled: true)), Throws.InstanceOf<OperationCanceledException>());
    }
}
