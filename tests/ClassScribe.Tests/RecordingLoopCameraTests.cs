using ClassScribe.Audio;
using ClassScribe.Camera;
using ClassScribe.Commands;
using ClassScribe.Runs;
using ClassScribe.Storage;

namespace ClassScribe.Tests;

/// <summary>
/// The loop's side of a lesson with the camera's picture (teach-video 6.14): when the checks find too little room on the disk
/// for the sound and the picture together, the loop lets the camera go, through the recording, and the sound records on.
/// </summary>
public partial class RecordingLoopTests
{
    /// <summary>
    /// With room on the disk for less than 30 minutes of the sound and the picture together, the loop lets the camera go once,
    /// with the room's words, which the camera's track says; the sound records on, neither stopped nor paused.
    /// </summary>
    [Test]
    public async Task With_little_room_the_loop_lets_the_camera_go_once_and_the_sound_records_on()
    {
        _space.Bytes = 1_000_000_000;
        var camera = new FakeCameraRecording();
        var sound = new FakeRecording(RecordingMode.Microphone);
        var lesson = _request with { Source = new SourcePlan(RecordingMode.Microphone, null, new MicrophoneChoice("OBSBOT"), new CameraChoice("OBSBOT", "720p")) };

        await RunAsync(new CameraLesson(sound, camera), request: lesson);
        await TickAsync(3);
        var calls = sound.Calls;
        await StopAsync();

        var drive = Path.GetPathRoot(_files.Folder);
        Assert.That(camera.LetGo, Is.EqualTo(new[]
        {
            $"Little room is left on {drive} (1.0 GB free), so the camera is let go: the rest of this lesson is recorded without its picture. The sound records on"
        }));
        Assert.That(calls, Has.None.EqualTo("Stop").And.None.EqualTo("Pause"), "the sound records on");
        Assert.That(_log.Messages, Has.None.StartsWith("Little room is left"), "the camera's track says it, once");
    }

    /// <summary>A lesson's recording as a test drives it: the sound of a <see cref="FakeRecording"/>, and the camera of a <see cref="FakeCameraRecording"/>.</summary>
    /// <param name="sound">The sound's recording.</param>
    /// <param name="camera">The camera, as the run sees it.</param>
    private sealed class CameraLesson(FakeRecording sound, FakeCameraRecording camera) : IRecording, ICameraRecording
    {
        /// <inheritdoc />
        public TimeSpan Position => sound.Position;

        /// <inheritdoc />
        public event EventHandler<TimelineNote>? NoteWorthy
        {
            add => sound.NoteWorthy += value;
            remove => sound.NoteWorthy -= value;
        }

        /// <inheritdoc />
        public CameraTrackState? Camera => camera.Camera;

        /// <inheritdoc />
        public void LetGoOfTheCamera(string why) => camera.LetGoOfTheCamera(why);

        /// <inheritdoc />
        public void Start(RecordingFiles files, TimeSpan offset) => sound.Start(files, offset);

        /// <inheritdoc />
        public RecordingSummary Stop() => sound.Stop();

        /// <inheritdoc />
        public void Pause() => sound.Pause();

        /// <inheritdoc />
        public void Resume() => sound.Resume();

        /// <inheritdoc />
        public bool? SetMicrophone(bool on) => sound.SetMicrophone(on);

        /// <inheritdoc />
        public RecordingSnapshot GetSnapshot() => sound.GetSnapshot();

        /// <inheritdoc />
        public void Reopen(Track track, string reason) => sound.Reopen(track, reason);

        /// <inheritdoc />
        public void Flush() => sound.Flush();

        /// <inheritdoc />
        public void Dispose() => sound.Dispose();
    }
}
