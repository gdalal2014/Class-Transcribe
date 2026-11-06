using ClassScribe.Application.Desktop;
using ClassScribe.Core.Runs;
using ClassScribe.Infrastructure.Link;

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

    /// <summary>
    /// Once only the lesson's video remains (record-only, or its transcript saved), the title says how far it has got, MAKING
    /// VIDEO 40%, and the taskbar's bar fills with it, green (review 3.17); while the transcript is still made, the title
    /// follows the transcript, as before.
    /// </summary>
    [Test]
    public void The_title_says_MAKING_VIDEO_while_only_the_video_remains()
    {
        var making = Lesson(RecorderPhase.Transcribing, null) with { VideoPercent = 40 };

        using (Assert.EnterMultipleScope())
        {
            Assert.That(TitleText.For(making), Is.EqualTo("MAKING VIDEO 40% Tajwid 4 | ClassScribe"));
            Assert.That(TitleText.TaskbarFor(making), Is.EqualTo(TaskbarState.Normal));
            Assert.That(TitleText.TaskbarPercentFor(making), Is.EqualTo(40));
            Assert.That(TitleText.For(making with { VideoPercent = 140 }), Is.EqualTo("MAKING VIDEO 100% Tajwid 4 | ClassScribe"));
            Assert.That(TitleText.For(Lesson(RecorderPhase.Transcribing, null)), Is.EqualTo("TRANSCRIBING Tajwid 4 | ClassScribe"));
            Assert.That(TitleText.For(Lesson(RecorderPhase.Uploading, null) with { UploadPercent = 20, VideoPercent = 40 }), Is.EqualTo("UPLOADING 20% Tajwid 4 | ClassScribe"), "the transcript first");
        }
    }

    /// <summary>The video's percent crosses the link as a last field with a default: a state an older recorder wrote reads with none.</summary>
    [Test]
    public void The_video_s_percent_crosses_the_link_and_an_older_state_reads_without_it()
    {
        var state = Lesson(RecorderPhase.Transcribing, null) with { VideoPercent = 40 };
        var json = System.Text.Json.JsonSerializer.Serialize(state, LinkJson.Default.RecorderState);
        var older = System.Text.Json.JsonSerializer.Serialize(state with { VideoPercent = null }, LinkJson.Default.RecorderState).Replace(",\"videoPercent\":null", "");

        Assert.That(System.Text.Json.JsonSerializer.Deserialize(json, LinkJson.Default.RecorderState), Is.EqualTo(state));
        Assert.That(older, Does.Not.Contain("videoPercent"));
        Assert.That(System.Text.Json.JsonSerializer.Deserialize(older, LinkJson.Default.RecorderState)!.VideoPercent, Is.Null);
    }

    /// <summary>A state with the camera crosses the link and reads back; one an older recorder wrote, without the field, reads with no camera.</summary>
    [Test]
    public void The_camera_s_field_crosses_the_link_and_an_older_state_reads_without_it()
    {
        var state = Lesson(RecorderPhase.Recording, new CameraState("OBSBOT", true, null, "720p"));
        var json = System.Text.Json.JsonSerializer.Serialize(state, LinkJson.Default.RecorderState);
        var older = System.Text.Json.JsonSerializer.Serialize(state with { Camera = null }, LinkJson.Default.RecorderState).Replace(",\"camera\":null", "");

        Assert.That(System.Text.Json.JsonSerializer.Deserialize(json, LinkJson.Default.RecorderState), Is.EqualTo(state));
        Assert.That(older, Does.Not.Contain("camera\""));
        Assert.That(System.Text.Json.JsonSerializer.Deserialize(older, LinkJson.Default.RecorderState)!.Camera, Is.Null);
    }
}
