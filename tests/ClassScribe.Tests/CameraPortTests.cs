using System.Text.Json;
using ClassScribe.Audio.Microphones;
using ClassScribe.Audio.Timeline;
using ClassScribe.Camera;
using ClassScribe.Link;
using ClassScribe.Recorder;
using ClassScribe.Settings;
using ClassScribe.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace ClassScribe.Tests;

/// <summary>
/// The contracts of the camera's ports (architecture 4.3, slice 3c) over their real adapters: the camera process's lines,
/// message by message (<see cref="ICameraLink"/>, over the real link on an in-memory pipe, the test speaking as the process),
/// and a recording's hold on a webcam's picture (<see cref="IWebcamHolds"/>, over the fake cameras and camera process
/// starter); and that both hosts resolve the holds to the adapter they hold. Nothing starts a process or opens a camera.
/// </summary>
[TestFixture]
public class CameraPortTests
{
    private TempFolder _temp = null!;
    private TestRuntime _runtime = null!;

    /// <summary>A temporary folder of the test's own, and ClassScribe's own folder in another.</summary>
    [SetUp]
    public void SetUp()
    {
        _temp = new TempFolder();
        _runtime = new TestRuntime();
    }

    /// <summary>Deletes the folders.</summary>
    [TearDown]
    public void TearDown()
    {
        _runtime.Dispose();
        _temp.Dispose();
    }

    /// <summary>
    /// The link through the port: the job, a stop, the sound's end, the clock's state and a signal each reach the process as
    /// the line the protocol names, with its body; what the process writes comes back as its record, a type this build does
    /// not know as a line without a message, and the end of the lines as null.
    /// </summary>
    [Test]
    public async Task The_link_sends_the_lines_as_the_protocol_names_them_and_receives_them_as_records()
    {
        using var pipe = new DuplexStreamPair();
        ICameraLink link = new CameraLink(new LineChannel(pipe.WindowEnd));
        await using var process = new LineChannel(pipe.RecorderEnd);
        var job = new CameraJob(CameraProtocol.Version, CameraJobKind.Check, "camera-id", "A camera", "1280x720");
        var clock = new ClockState(0, 12345, [], null, 1);

        await link.SendJobAsync(job, CancellationToken.None);
        await link.SendStopAsync(new CameraStop(12.5), CancellationToken.None);
        await link.SendEndAsync(new CameraStop(10), CancellationToken.None);
        await link.SendClockAsync(clock, CancellationToken.None);
        await link.SendSignalAsync(CameraProtocol.Off, CancellationToken.None);
        List<(string Type, object? Message)> sent = [];
        for (var i = 0; i < 5; i++)
        {
            var line = (await process.ReceiveAsync(CancellationToken.None))!;
            sent.Add((line.Type, line.Type == CameraProtocol.Job ? line.Body.Deserialize(CameraJson.Default.CameraJob)
                : line.Type is CameraProtocol.Stop or CameraProtocol.End ? line.Body.Deserialize(CameraJson.Default.CameraStop)
                : line.Type == CameraProtocol.Clock ? line.Body.Deserialize(CameraJson.Default.ClockState)
                : line.Body.ValueKind.ToString()));
        }

        await process.SendAsync(CameraProtocol.Log, new CameraLog("Information", "Opened"), CameraJson.Default.CameraLog, CancellationToken.None);
        await process.SendAsync("later", new CameraEmpty(), CameraJson.Default.CameraEmpty, CancellationToken.None);
        var log = await link.ReceiveAsync(CancellationToken.None);
        var unknown = await link.ReceiveAsync(CancellationToken.None);
        pipe.Break();
        var end = await link.ReceiveAsync(CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(sent.Select(line => line.Type), Is.EqualTo(new[] { CameraProtocol.Job, CameraProtocol.Stop, CameraProtocol.End, CameraProtocol.Clock, CameraProtocol.Off }));
            Assert.That(sent[0].Message, Is.EqualTo(job));
            Assert.That(sent[1].Message, Is.EqualTo(new CameraStop(12.5)));
            Assert.That(sent[2].Message, Is.EqualTo(new CameraStop(10)));
            Assert.That(((ClockState)sent[3].Message!).Origin, Is.EqualTo(12345));
            Assert.That(sent[4].Message, Is.EqualTo("Object"), "a signal carries an empty object");
            Assert.That(log, Is.EqualTo(new CameraLine(CameraProtocol.Log, new CameraLog("Information", "Opened"))));
            Assert.That(unknown, Is.EqualTo(new CameraLine("later", null)));
            Assert.That(end, Is.Null);
        });
    }

    /// <summary>
    /// The holds through the port: a hold with the setting off follows a webcam's microphone to its webcam, waits for nothing
    /// before the capture opens, says it holds nothing, starts no camera process, and lets go.
    /// </summary>
    [Test]
    public void A_hold_follows_the_microphone_to_its_webcam_and_starts_nothing_while_the_setting_is_off()
    {
        var starter = new FakeCameraProcessStarter { PlayEach = true };
        IWebcamHolds holds = new WebcamHolds(new FakeCameraProvider(), starter, new ManualTimeProvider(TestRuns.Start), new TestLoggerFactory());
        var obsbot = LaptopMicrophones.Obsbot with { ContainerId = LaptopCameras.ObsbotContainer };

        using var hold = holds.Create(wakeOn: false);
        var before = hold.State;
        hold.Follow(obsbot);
        hold.BeforeOpen(obsbot);
        var followed = hold.State;
        hold.Stop();

        Assert.Multiple(() =>
        {
            Assert.That(before, Is.Null);
            Assert.That(followed, Is.EqualTo(new WebcamState(LaptopCameras.Obsbot.Name, Holding: false, HasFrames: false)));
            Assert.That(starter.Started, Is.Empty);
        });
    }

    /// <summary>Both hosts resolve the holds to the very adapter they hold.</summary>
    [Test]
    public void Both_hosts_resolve_the_holds_to_the_adapter_they_hold()
    {
        File.WriteAllText(_temp.File(SettingsFile.FileName), "{}");
        var places = new SettingsPlaces(_temp.Path, userFolder: null);
        using var window = AppHost.Build(places, "test-key", services =>
            services.AddSingleton(_runtime.Folder).AddSingleton(new RoamingFolder(_temp.File("Roaming"))));
        var runId = TestRuns.Id();
        using var log = new RecorderLog(_runtime.Time);
        using var recorder = AppHost.BuildRecorder(new RecorderStart(runId, TestRuns.Request(runId), new Dictionary<string, string?>()), "test-key", log, _runtime.Folder, places, _runtime.Time);

        Assert.Multiple(() =>
        {
            foreach (var (name, services) in new[] { ("the window", window.Services), ("the recorder", recorder.Services) })
                Assert.That(services.GetRequiredService<IWebcamHolds>(), Is.SameAs(services.GetRequiredService<WebcamHolds>()), $"{name}: the holds");
        });
    }
}
