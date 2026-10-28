using ClassScribe.Audio;
using ClassScribe.Audio.Microphones;
using ClassScribe.Audio.Timeline;
using ClassScribe.Camera;
using ClassScribe.Commands;
using ClassScribe.Recorder;
using ClassScribe.Runs;
using ClassScribe.Settings;
using ClassScribe.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClassScribe.Tests;

/// <summary>
/// The accelerated soak of a lesson with the camera (teach-video 11, slice 7): a lesson in person simulated on a clock moved
/// by hand through the real track set, the real camera recorder and its camera track, and the real camera process's record
/// job, its camera and its piece writer played by fakes. The microphone hands over a steady level with a click at each
/// mark; the camera 5 frames a second, a bright frame at each mark, taken at the click's very stamp. Through the run P
/// pauses for 20 seconds, the laptop sleeps for 18, and the camera is unplugged for 5. Afterwards: each mark's bright frame
/// lies in the one piece where its click lies in the sound, so the picture stays with the sound through the pause, the cut
/// sleep and the lost camera; no frame of the pause or the sleep is written; a black frame follows the picture where it was
/// lost; and the piece ends where the sound ends. No camera or microphone is opened, nothing is encoded or played.
/// </summary>
[TestFixture]
public sealed class CameraSoakTests
{
    private const int Rate = 16_000;
    private static readonly AudioFormat Pcm16KMono = new(Rate, 1, 16, false);
    private static readonly DateTimeOffset Start = new(2026, 10, 22, 14, 0, 0, TimeSpan.Zero);
    private static readonly Microphone Obsbot = LaptopMicrophones.Obsbot with { ContainerId = LaptopCameras.ObsbotContainer };

    /// <summary>One step of the simulation: a packet of the microphone's.</summary>
    private static readonly TimeSpan Step = TimeSpan.FromMilliseconds(10);

    /// <summary>
    /// The camera's frames, every twentieth step: 5 a second, so that every mark falls on a frame, and the job's queue of
    /// frames never fills while the simulation runs ahead of it on the clock moved by hand.
    /// </summary>
    private const int StepsPerFrame = 20;

    /// <summary>A mark every 20 seconds of steps.</summary>
    private const int StepsPerMark = 2000;

    /// <summary>The microphone's steady level, and its click at a mark.</summary>
    private const short Level = 500;
    private const short Click = 20_000;

    /// <summary>The brightness of the camera's picture, and of a mark's frame.</summary>
    private const byte Grey = 100;
    private const byte Bright = 235;

    private TempFolder _folder = null!;
    private RecordingFiles _files = null!;
    private FakeMicrophoneProvider _microphones = null!;
    private ManualTimeProvider _time = null!;
    private FakeSystemTimes _times = null!;
    private FreezeWatch _freezes = null!;
    private FakeCameraCaptureFactory _captures = null!;
    private FakePieceWriterFactory _writers = null!;
    private FakeCameraProcessStarter _starter = null!;

    /// <summary>A recording in a temporary folder, the OBSBOT's microphone, the clocks ten seconds in, and a starter that runs the real camera host over the fakes for each camera process.</summary>
    [SetUp]
    public void SetUp()
    {
        _folder = new TempFolder();
        _files = new RecordingFiles(_folder.Path, "Tajwid_2026-10-22_14-00-00Z");
        _microphones = new FakeMicrophoneProvider().Connect(Obsbot);
        _time = new ManualTimeProvider(Start);
        _time.Advance(TimeSpan.FromSeconds(10));
        _times = new FakeSystemTimes(_time);
        _freezes = new FreezeWatch(_times, _time, NullLogger<FreezeWatch>.Instance);
        _captures = new FakeCameraCaptureFactory();
        _writers = new FakePieceWriterFactory();
        _starter = new FakeCameraProcessStarter
        {
            Run = async (process, jobId) =>
            {
                var host = new CameraHost { Time = _time, Captures = _captures, Writers = _writers, CallLimit = TimeSpan.FromHours(1) };
                process.Exited(await host.RunAsync(jobId, process.ProcessStream, process.ProcessStream));
            }
        };
    }

    /// <summary>Deletes the temporary folder, which fails if the run left a file open.</summary>
    [TearDown]
    public void TearDown() => _folder.Dispose();

    /// <summary>
    /// Three simulated minutes of a lesson: P at the first minute for 20 seconds, the laptop asleep for 18 seconds at 1:40, the
    /// camera unplugged for 5 seconds at 2:20; then the stop. Each mark's bright frame lies where its click lies in the sound,
    /// to within a packet; the black frame follows the last frame before the camera was lost; no frame lies in the pause or
    /// the sleep; the piece ends with the sound; one piece holds it all.
    /// </summary>
    [Test]
    public async Task A_lesson_s_picture_stays_with_its_sound_through_a_pause_a_sleep_and_a_lost_camera()
    {
        var factory = new TrackSetFactory(new FakeCaptureProvider(), _microphones, new FakeMicrophoneSessions(), new FakeMicrophoneUsageRecord(), new FakeProcessNames(),
            new FakeOwnProcesses(Laptop.OwnProcess), new ClassFolders(TestOptions.Create(new RecordingOptions()), _time, NullLogger<ClassFolders>.Instance), _freezes, TestOptions.Create(new RecordingOptions()), TestOptions.Create(new TranscriptOptions()), _time,
            NullLoggerFactory.Instance, cameraRecorder: new CameraRecorder(new FakeCameraProvider(), _starter, _time, NullLoggerFactory.Instance));
        var plan = new SourcePlan(RecordingMode.Microphone, null, new MicrophoneChoice("OBSBOT"), new CameraChoice("OBSBOT", "320x240"));
        var microphone = _microphones.Prepare(new FakeAudioCapture(Pcm16KMono, Obsbot.Name, Obsbot.Id) { DeliversContinuously = true });
        using var recording = (TrackSet)factory.Create(plan, new RecordingClock(_time));
        recording.Start(_files, TimeSpan.Zero);

        var marks = 0;
        var lostAt = -1;
        for (var step = 1; step <= 18_000; step++)
        {
            var stamp = _time.GetTimestamp();
            var mark = step % StepsPerMark == 0 && !recording.GetSnapshot().Paused;
            if (mark) marks++;
            microphone.Deliver(Packet(mark ? Click : Level), stamp);
            var unplugged = step is > 14_000 and <= 14_500;
            if (step == 14_001) lostAt = step;
            if (step % StepsPerFrame == 0 && !unplugged) _captures.Last?.Deliver(stamp, mark ? Bright : Grey);

            _time.Advance(Step);
            if (step % 100 == 0)
            {
                _freezes.Tick();
                await Task.Delay(2);
            }

            switch (step)
            {
                case 6_000:
                    recording.Pause();
                    break;
                case 8_000:
                    recording.Resume();
                    break;
                case 10_000:
                    _freezes.Tick();
                    _times.Sleep(TimeSpan.FromSeconds(18));
                    break;
            }
        }

        await Task.Delay(50);
        var summary = recording.Stop();

        var writer = _writers.Made.Single();
        var samples = writer.Samples;
        var bright = samples.Where(sample => sample.Brightness == Bright).Select(sample => sample.At).ToList();
        var clicks = ClickPositions(summary.MainSegments);
        Assert.That(lostAt, Is.EqualTo(14_001));
        Assert.That(clicks, Has.Count.EqualTo(marks), "every click in the sound");
        Assert.That(bright, Has.Count.EqualTo(marks), "every mark's frame in the piece");
        Assert.That(bright.Zip(clicks, (frame, click) => (frame - click).Duration()).Max(), Is.LessThanOrEqualTo(Step), "each mark's frame where its click is");
        Assert.That(samples.Count(sample => sample.Black && sample.At > TimeSpan.Zero), Is.GreaterThanOrEqualTo(2), "the sleep and the unplugging each leave a black frame");
        Assert.That(writer.FinishedAt, Is.EqualTo(summary.Length).Within(TimeSpan.FromMilliseconds(1)), "the piece ends with the sound");
        Assert.That(SamplesAreInOrder(samples), Is.True);
        Assert.That(_captures.Made, Has.Count.GreaterThanOrEqualTo(4), "opened at the start, after the pause, after the sleep, and after the camera came back");
        Assert.That(_captures.Made.Select(made => made.Capture.Disposed), Has.All.True);
    }

    /// <summary>
    /// A stop whose sound takes two seconds to let go of its microphone, the camera streaming all the while: the frames taken
    /// after the sound's end never reach the piece, which ends where the sound ends.
    /// </summary>
    [Test]
    public async Task The_piece_ends_with_the_sound_though_the_sound_takes_seconds_to_stop()
    {
        var factory = new TrackSetFactory(new FakeCaptureProvider(), _microphones, new FakeMicrophoneSessions(), new FakeMicrophoneUsageRecord(), new FakeProcessNames(),
            new FakeOwnProcesses(Laptop.OwnProcess), new ClassFolders(TestOptions.Create(new RecordingOptions()), _time, NullLogger<ClassFolders>.Instance), _freezes, TestOptions.Create(new RecordingOptions()), TestOptions.Create(new TranscriptOptions()), _time,
            NullLoggerFactory.Instance, cameraRecorder: new CameraRecorder(new FakeCameraProvider(), _starter, _time, NullLoggerFactory.Instance));
        var plan = new SourcePlan(RecordingMode.Microphone, null, new MicrophoneChoice("OBSBOT"), new CameraChoice("OBSBOT", "320x240"));
        using var letGo = new ManualResetEventSlim();
        var microphone = _microphones.Prepare(new FakeAudioCapture(Pcm16KMono, Obsbot.Name, Obsbot.Id) { DeliversContinuously = true, DisposeGate = letGo });
        using var recording = (TrackSet)factory.Create(plan, new RecordingClock(_time));
        recording.Start(_files, TimeSpan.Zero);
        for (var step = 1; step <= 1_000; step++) await PlayAsync(step, microphone);

        var stopping = Task.Run(recording.Stop);
        await Wait.UntilAsync(() => microphone.DisposeCalls > 0);
        await Task.Delay(50);
        for (var step = 1; step <= 200; step++) await PlayAsync(step, microphone: null);
        letGo.Set();
        var summary = await stopping.WaitAsync(TimeSpan.FromSeconds(20));

        var writer = _writers.Made.Single();
        Assert.That(writer.Samples.Count(sample => !sample.Black), Is.GreaterThan(40), "the picture of the ten seconds before the stop");
        Assert.That(writer.Samples.Max(sample => sample.At), Is.LessThanOrEqualTo(summary.Length), "no frame after the sound's end");
        Assert.That(writer.FinishedAt, Is.EqualTo(summary.Length).Within(TimeSpan.FromMilliseconds(1)), "the piece ends with the sound");
    }

    /// <summary>
    /// One step of a simulation: a packet of <paramref name="microphone"/>'s when given, a frame of the camera's every
    /// <see cref="StepsPerFrame"/> steps, then the clock moved on a step, with a moment every hundred milliseconds for the
    /// camera's track and job to run.
    /// </summary>
    private async Task PlayAsync(int step, FakeAudioCapture? microphone)
    {
        var stamp = _time.GetTimestamp();
        microphone?.Deliver(Packet(Level), stamp);
        if (step % StepsPerFrame == 0) _captures.Last?.Deliver(stamp, Grey);
        _time.Advance(Step);
        if (step % 10 != 0) return;

        _freezes.Tick();
        await Task.Delay(2);
    }

    /// <summary>A packet of the microphone at one value, 10 ms long.</summary>
    private static byte[] Packet(short value) => TestAudio.Shorts(Enumerable.Repeat(value, Rate / 100).ToArray());

    /// <summary>Where each click begins in the sound, in order.</summary>
    private static List<TimeSpan> ClickPositions(IReadOnlyList<string> parts)
    {
        var samples = parts.SelectMany(part => TestAudio.ReadWav(part).Samples).ToArray();
        var clicks = new List<TimeSpan>();
        for (var index = 0; index < samples.Length; index++)
        {
            // The sound is written through its float conversion, so a click reads back a step below its value.
            if (samples[index] < Click / 2 || index > 0 && samples[index - 1] >= Click / 2) continue;
            clicks.Add(MediaTime.FromFrames(index, Rate));
        }

        return clicks;
    }

    /// <summary>True when every sample comes after the one before it.</summary>
    private static bool SamplesAreInOrder(IReadOnlyList<WrittenSample> samples) => samples.Zip(samples.Skip(1), (before, after) => after.At > before.At).All(inOrder => inOrder);
}
