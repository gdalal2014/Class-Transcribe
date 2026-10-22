using ClassScribe.Commands;
using ClassScribe.Desktop;

namespace ClassScribe.Tests;

/// <summary>
/// The title of a lesson with the camera (teach-video 6.13): CAM WAITING while its picture is not recording (not open yet,
/// in use, unplugged, restarting, given up), CAM DARK while it is black, nothing while it records; nothing while paused,
/// when the camera is let go by design; and nothing for a run without a camera. A state from an older window, without the
/// camera's field, reads with none.
/// </summary>
[TestFixture]
public sealed class TitleTextCameraTests
{
    private static readonly TimeSpan Position = new(1, 12, 40);

    /// <summary>A lesson's state in <paramref name="phase"/> with the camera as <paramref name="camera"/>.</summary>
    private static RecorderState Lesson(RecorderPhase phase, CameraState? camera) =>
        new(phase, "Tajwid 4", Position, "OBSBOT Meet Flip Microphone", null, null, null, null, Attention.None, InWindow: false, Camera: camera);

    /// <summary>The camera's words in the title, by its state.</summary>
    [TestCase(true, false, "REC 01:12:40 Tajwid 4 | ClassScribe")]
    [TestCase(false, false, "REC 01:12:40 CAM WAITING Tajwid 4 | ClassScribe")]
    [TestCase(true, true, "REC 01:12:40 CAM DARK Tajwid 4 | ClassScribe")]
    public void The_title_says_when_the_camera_waits_or_is_dark(bool capturing, bool dark, string title) =>
        Assert.That(TitleText.For(Lesson(RecorderPhase.Recording, new CameraState("OBSBOT Meet Flip StreamCamera", capturing, null, "720p", dark))), Is.EqualTo(title));

    /// <summary>While paused the camera is let go by design: the title says PAUSED alone; a run without a camera says nothing of one.</summary>
    [Test]
    public void Paused_and_without_a_camera_the_title_says_nothing_of_it()
    {
        Assert.That(TitleText.For(Lesson(RecorderPhase.Paused, new CameraState("OBSBOT", false, null))), Is.EqualTo("PAUSED 01:12:40 Tajwid 4 | ClassScribe"));
        Assert.That(TitleText.For(Lesson(RecorderPhase.Recording, null)), Is.EqualTo("REC 01:12:40 Tajwid 4 | ClassScribe"));
    }

    /// <summary>A state with the camera crosses the link and reads back; one an older recorder wrote, without the field, reads with no camera.</summary>
    [Test]
    public void The_camera_s_field_crosses_the_link_and_an_older_state_reads_without_it()
    {
        var state = Lesson(RecorderPhase.Recording, new CameraState("OBSBOT", true, null, "720p"));
        var json = System.Text.Json.JsonSerializer.Serialize(state, Link.LinkJson.Default.RecorderState);
        var older = System.Text.Json.JsonSerializer.Serialize(state with { Camera = null }, Link.LinkJson.Default.RecorderState).Replace(",\"camera\":null", "");

        Assert.That(System.Text.Json.JsonSerializer.Deserialize(json, Link.LinkJson.Default.RecorderState), Is.EqualTo(state));
        Assert.That(older, Does.Not.Contain("camera\""));
        Assert.That(System.Text.Json.JsonSerializer.Deserialize(older, Link.LinkJson.Default.RecorderState)!.Camera, Is.Null);
    }
}
