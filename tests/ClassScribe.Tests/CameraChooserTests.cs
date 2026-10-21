using ClassScribe.Audio.Microphones;
using ClassScribe.Camera;

namespace ClassScribe.Tests;

/// <summary>
/// The camera list and the camera choice (teach-video 6.4): only colour cameras are listed, each in its place; a name part
/// picks a camera, virtual ones included; without one a USB camera comes first, then the built-in front camera, never a
/// virtual one; and a webcam's microphone is matched to its camera by container, never the laptop's array to its camera.
/// </summary>
[TestFixture]
public class CameraChooserTests
{
    /// <summary>The laptop's devices list as its three colour cameras, each in its place; the infrared camera never shows.</summary>
    [Test]
    public void Only_colour_cameras_are_listed_each_in_its_place()
    {
        var listed = Cameras.Listed(LaptopCameras.AllFacts, LaptopCameras.ColourSources);

        Assert.That(listed, Is.EqualTo(LaptopCameras.All));
    }

    /// <summary>A laptop's own camera usually sits on an internal USB bus: its panel, not its id, makes it built in.</summary>
    [Test]
    public void A_camera_at_the_front_of_the_screen_is_built_in_even_on_an_internal_USB_bus()
    {
        var listed = Cameras.Listed([LaptopCameras.IntegratedFacts], LaptopCameras.ColourSources);

        Assert.That(listed.Single().Place, Is.EqualTo(CameraPlace.BuiltInFront));
        Assert.That(listed.Single().IsBuiltIn, Is.True);
    }

    /// <summary>A camera at the back is built in at the back; one Windows says nothing about is other.</summary>
    [Test]
    public void The_back_camera_and_a_camera_of_no_known_place()
    {
        VideoDeviceFacts back = new(@"\\?\ACPI#back", "Rear Camera", CameraPanel.Back, null);
        VideoDeviceFacts other = new(@"\\?\PCI#other", "Capture Card", CameraPanel.Unknown, null);

        var listed = Cameras.Listed([back, other], new HashSet<string>([back.Id, other.Id]));

        Assert.That(listed.Select(camera => camera.Place), Is.EqualTo(new[] { CameraPlace.BuiltInBack, CameraPlace.Other }));
    }

    /// <summary>Without a name part a plugged-in USB camera is chosen before the built-in one.</summary>
    [Test]
    public void Without_a_name_a_USB_camera_comes_first() =>
        Assert.That(Cameras.Choose(LaptopCameras.All, namePart: null), Is.EqualTo(LaptopCameras.Obsbot));

    /// <summary>Without a USB camera, the built-in front camera; never the virtual one, which is listed first here.</summary>
    [Test]
    public void Without_a_USB_camera_the_built_in_front_camera_never_a_virtual_one() =>
        Assert.That(Cameras.Choose([LaptopCameras.Pixel, LaptopCameras.Integrated], namePart: "  "), Is.EqualTo(LaptopCameras.Integrated));

    /// <summary>With only a virtual camera and nothing named, none is chosen.</summary>
    [Test]
    public void A_virtual_camera_alone_is_never_chosen_unnamed() =>
        Assert.That(Cameras.Choose([LaptopCameras.Pixel], namePart: null), Is.Null);

    /// <summary>A name part picks the camera whose name holds it, in any case, a virtual one included.</summary>
    [TestCase("obsbot", "OBSBOT Meet Flip StreamCamera")]
    [TestCase(" Integrated ", "Integrated Camera")]
    [TestCase("PIXEL", "Pixel 10 Pro XL")]
    public void A_name_part_picks_its_camera(string namePart, string expected) =>
        Assert.That(Cameras.Choose(LaptopCameras.All, namePart)?.Name, Is.EqualTo(expected));

    /// <summary>A name part that names no camera chooses none: the run waits for it rather than take another angle.</summary>
    [Test]
    public void A_name_that_names_no_camera_chooses_none() =>
        Assert.That(Cameras.Choose(LaptopCameras.All, "Logitech"), Is.Null);

    /// <summary>The OBSBOT's microphone belongs to the OBSBOT's camera, by their shared container, and the other way round.</summary>
    [Test]
    public void A_webcam_microphone_is_matched_to_its_camera_by_container()
    {
        var obsbot = LaptopMicrophones.Obsbot with { ContainerId = LaptopCameras.ObsbotContainer };

        Assert.That(Cameras.CameraOf(obsbot, LaptopCameras.All), Is.EqualTo(LaptopCameras.Obsbot));
        Assert.That(Cameras.MicrophoneOf(LaptopCameras.Obsbot, [LaptopMicrophones.Array, obsbot]), Is.EqualTo(obsbot));
    }

    /// <summary>
    /// The laptop's array and its built-in camera share the computer's own container; the array is never taken for a
    /// webcam's microphone, even if a built-in camera carried another container it shares.
    /// </summary>
    [Test]
    public void The_laptop_array_is_never_matched_to_the_built_in_camera()
    {
        var array = LaptopMicrophones.Array with { ContainerId = Cameras.ThisComputer };
        var sharedContainer = Guid.NewGuid();
        var oddBuiltIn = LaptopCameras.Integrated with { ContainerId = sharedContainer };

        Assert.That(Cameras.CameraOf(array, LaptopCameras.All), Is.Null);
        Assert.That(Cameras.CameraOf(array with { ContainerId = sharedContainer }, [oddBuiltIn]), Is.Null);
        Assert.That(Cameras.MicrophoneOf(LaptopCameras.Integrated, [array]), Is.Null);
    }

    /// <summary>
    /// The computer's own container never ties a microphone to a camera, even one placed as USB: an internal camera whose
    /// panel Windows does not report is placed by its \\?\USB# id, as this laptop's Integrated Camera would be, and the array,
    /// in the same container, is still not its microphone.
    /// </summary>
    [Test]
    public void The_computers_own_container_ties_no_microphone_to_a_camera_placed_as_USB()
    {
        var array = LaptopMicrophones.Array with { ContainerId = Cameras.ThisComputer };
        var integratedWithoutPanel = LaptopCameras.Integrated with { Place = CameraPlace.Usb };

        Assert.That(Cameras.CameraOf(array, [integratedWithoutPanel]), Is.Null);
        Assert.That(Cameras.MicrophoneOf(integratedWithoutPanel, [array]), Is.Null);
    }

    /// <summary>A microphone of no camera, or whose container Windows does not say, belongs to none.</summary>
    [Test]
    public void A_microphone_without_a_camera_belongs_to_none()
    {
        Assert.That(Cameras.CameraOf(LaptopMicrophones.PixelBuds with { ContainerId = Guid.NewGuid() }, LaptopCameras.All), Is.Null);
        Assert.That(Cameras.CameraOf(LaptopMicrophones.PixelBuds with { ContainerId = null }, LaptopCameras.All), Is.Null);
        Assert.That(Cameras.CameraOf(LaptopMicrophones.PixelBuds with { ContainerId = Guid.Empty }, [LaptopCameras.Obsbot with { ContainerId = Guid.Empty }]), Is.Null);
    }

    /// <summary>The lists name each camera's place in words.</summary>
    [Test]
    public void Each_camera_is_described_with_its_place()
    {
        Assert.That(LaptopCameras.Integrated.Described, Is.EqualTo("Integrated Camera  (built in, front)"));
        Assert.That(LaptopCameras.Obsbot.Described, Is.EqualTo("OBSBOT Meet Flip StreamCamera  (USB)"));
        Assert.That(LaptopCameras.Pixel.Described, Does.StartWith("Pixel 10 Pro XL  (virtual"));
        Assert.That(new CameraDevice("x", "Capture Card", CameraPlace.Other, null).Described, Is.EqualTo("Capture Card"));
    }
}
