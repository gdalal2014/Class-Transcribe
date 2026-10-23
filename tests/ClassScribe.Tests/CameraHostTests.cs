using System.Text;
using System.Text.Json;
using ClassScribe.Camera;
using ClassScribe.Commands;
using ClassScribe.Desktop;
using ClassScribe.Link;
using ClassScribe.Recorder;

namespace ClassScribe.Tests;

/// <summary>
/// The camera process's role and lines (teach-video 6.2, amendment 24): <c>--camera-process &lt;job id&gt;</c> is taken
/// only with a well-formed id, as <c>--recorder</c> now is; the host, run in-process over an in-memory pipe, reads its job
/// and, in this build, answers it with a log line and failed, opening nothing, exit 4; anything that is not a job of this
/// version is not taken either. The fake process speaks the same lines.
/// </summary>
[TestFixture]
public class CameraHostTests
{
    /// <summary>A well-formed job id.</summary>
    private static readonly string JobId = TestRuns.Id(5);

    /// <summary>Only exactly the switch and a well-formed id make a camera process; anything else is the command line's.</summary>
    [Test]
    public void Only_the_switch_and_a_well_formed_id_make_a_camera_process()
    {
        Assert.That(CameraProtocol.IsCameraProcessStart([CameraProtocol.ProcessSwitch, JobId], out var jobId), Is.True);
        Assert.That(jobId, Is.EqualTo(JobId));

        Assert.That(CameraProtocol.IsCameraProcessStart([CameraProtocol.ProcessSwitch, "OBSBOT"], out _), Is.False, "ClassScribe.exe --camera-process OBSBOT is a mistake, not a hidden host");
        Assert.That(CameraProtocol.IsCameraProcessStart([CameraProtocol.ProcessSwitch], out _), Is.False);
        Assert.That(CameraProtocol.IsCameraProcessStart([CameraProtocol.ProcessSwitch, JobId, "--no-open"], out _), Is.False);
        Assert.That(CameraProtocol.IsCameraProcessStart(["--Camera-Process", JobId], out _), Is.False, "the switch is written one way");
        Assert.That(CameraProtocol.IsCameraProcessStart(["--camera", JobId], out _), Is.False);
        Assert.That(CameraProtocol.IsCameraProcessStart([], out _), Is.False);
    }

    /// <summary>The recorder's start takes a well-formed run id only too (amendment 24): anything else is the command line's.</summary>
    [Test]
    public void The_recorders_start_takes_a_well_formed_id_only()
    {
        Assert.That(RecorderHost.IsRecorderStart([ProcessArguments.Recorder, JobId], out _), Is.True);
        Assert.That(RecorderHost.IsRecorderStart([ProcessArguments.Recorder, "Kalam"], out _), Is.False);
        Assert.That(RecorderHost.IsRecorderStart([ProcessArguments.Recorder, JobId + "\n"], out _), Is.False);
    }

    /// <summary>
    /// A finish job (the lesson's video, slice 8) is read, and answered, in this build, with a log line saying no camera was
    /// opened and failed saying the lesson's video is not built yet; the exit code is 4, a job not taken.
    /// </summary>
    [Test]
    public async Task A_finish_job_is_answered_that_this_build_takes_none_and_opens_nothing()
    {
        await using var process = new FakeCameraProcess();
        var host = Host(process);

        await CameraLines.SendJobAsync(process.Channel, new CameraJob(CameraProtocol.Version, CameraJobKind.Finish, null, "OBSBOT Meet Flip StreamCamera"), CancellationToken.None);
        var log = CameraLines.Read((await process.Channel.ReceiveAsync(CancellationToken.None))!);
        var failed = CameraLines.Read((await process.Channel.ReceiveAsync(CancellationToken.None))!);

        Assert.That(await host, Is.EqualTo(CameraProtocol.JobNotTakenExitCode));
        Assert.That(log, Is.EqualTo(new CameraLog("Information", $"Camera process for job {JobId}: a Finish job for the camera \"OBSBOT Meet Flip StreamCamera\"; no camera was opened")));
        Assert.That(failed, Is.EqualTo(new CameraFailed($"{CameraHost.FinishNotBuilt}: this camera process takes no Finish job yet, and opened no camera")));
    }

    /// <summary>A record job that names no camera, or no recording, is not taken: exit 4 or 1, nothing opened.</summary>
    [Test]
    public async Task A_record_job_without_a_camera_or_a_recording_is_not_taken()
    {
        await using (var process = new FakeCameraProcess())
        {
            var host = Host(process);
            await CameraLines.SendJobAsync(process.Channel, new CameraJob(CameraProtocol.Version, CameraJobKind.Record, null, "OBSBOT", "720p", 0, @"C:\r", "Tajwid_2026-10-19_14-00-02Z"), CancellationToken.None);
            var failed = CameraLines.Read((await process.Channel.ReceiveAsync(CancellationToken.None))!);
            Assert.That(await host, Is.EqualTo(CameraProtocol.JobNotTakenExitCode));
            Assert.That(failed, Is.EqualTo(new CameraFailed("The Record job names no camera")));
        }

        await using (var process = new FakeCameraProcess())
        {
            var host = Host(process);
            await CameraLines.SendJobAsync(process.Channel, new CameraJob(CameraProtocol.Version, CameraJobKind.Record, "id", "OBSBOT", "720p"), CancellationToken.None);
            var failed = CameraLines.Read((await process.Channel.ReceiveAsync(CancellationToken.None))!);
            Assert.That(await host, Is.EqualTo(CameraProtocol.FailedExitCode));
            Assert.That(failed, Is.EqualTo(new CameraFailed("The record job names no recording to write the camera's picture into")));
        }
    }

    /// <summary>A first line that is not a job, a job of another version, or a line that is not a message, is answered with failed and why, exit 4.</summary>
    [Test]
    public async Task Anything_but_a_job_of_this_version_is_not_taken()
    {
        Assert.That(await NotTakenAsync(channel => channel.SendAsync(CameraProtocol.Stop, new CameraStop(12.5), CameraJson.Default.CameraStop, CancellationToken.None)),
            Is.EqualTo("The first line was \"stop\", not a job"));
        Assert.That(await NotTakenAsync(channel => CameraLines.SendJobAsync(channel, new CameraJob(CameraProtocol.Version + 1, CameraJobKind.Hold), CancellationToken.None)),
            Is.EqualTo($"The job speaks version {CameraProtocol.Version + 1} of the camera's lines; this camera process speaks version {CameraProtocol.Version}"));
        Assert.That(await NotTakenAsync(channel => channel.SendAsync(CameraProtocol.Job, new CameraLog("x", "y"), CameraJson.Default.CameraLog, CancellationToken.None)),
            Does.StartWith("The job could not be read"));
    }

    /// <summary>
    /// A job line without a body, or with a body that is not an object, is answered with failed and why, exit 4, never an
    /// exception: the camera process never throws.
    /// </summary>
    [TestCase("{\"type\":\"job\"}", "The job had no body")]
    [TestCase("{\"type\":\"job\",\"body\":null}", "The job had no body")]
    [TestCase("{\"type\":\"job\",\"body\":\"Record\"}", "The job's body was not an object")]
    public async Task A_job_without_a_body_is_not_taken(string line, string why)
    {
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(line + "\n"));
        using var output = new MemoryStream();

        Assert.That(await new CameraHost().RunAsync(JobId, input, output), Is.EqualTo(CameraProtocol.JobNotTakenExitCode));

        await using var written = new LineChannel(new MemoryStream(output.ToArray()));
        Assert.That(CameraLines.Read((await written.ReceiveAsync(CancellationToken.None))!), Is.EqualTo(new CameraFailed(why)));
    }

    /// <summary>
    /// A line of a type this build knows, without a body or with one that is not an object, is a <see cref="JsonException"/>
    /// from <see cref="CameraLines.Read"/>, as its summary says, never another exception; a type it does not know is passed
    /// over.
    /// </summary>
    [Test]
    public void A_known_line_without_a_body_is_a_JSON_exception()
    {
        using var nothing = JsonDocument.Parse("null");
        using var list = JsonDocument.Parse("[1]");

        Assert.Throws<JsonException>(() => CameraLines.Read(new Envelope(CameraProtocol.Log)));
        Assert.Throws<JsonException>(() => CameraLines.Read(new Envelope(CameraProtocol.Failed, nothing.RootElement)));
        Assert.Throws<JsonException>(() => CameraLines.Read(new Envelope(CameraProtocol.Status, list.RootElement)));
        Assert.That(CameraLines.Read(new Envelope("frame")), Is.Null);
    }

    /// <summary>The starter closing its end before a job came ends the process with 4 and nothing written.</summary>
    [Test]
    public async Task No_job_before_the_input_ends_is_a_job_not_taken()
    {
        await using var process = new FakeCameraProcess();
        var host = Host(process);

        await process.Channel.DisposeAsync();

        Assert.That(await host, Is.EqualTo(CameraProtocol.JobNotTakenExitCode));
    }

    /// <summary>No job within the patience is answered with failed, and exit 4, on the manual clock.</summary>
    [Test]
    public async Task No_job_in_time_is_a_job_not_taken()
    {
        var time = new ManualTimeProvider(TestRuns.Start);
        await using var process = new FakeCameraProcess();
        var host = Task.Run(() => new CameraHost { Time = time, JobPatience = TimeSpan.FromSeconds(30) }.RunAsync(JobId, StreamOf(process), StreamOf(process)));

        await time.AdvanceUntilAsync(() => host.IsCompleted, TimeSpan.FromSeconds(5), atMost: TimeSpan.FromMinutes(1));
        var failed = CameraLines.Read((await process.Channel.ReceiveAsync(CancellationToken.None))!);

        Assert.That(await host, Is.EqualTo(CameraProtocol.JobNotTakenExitCode));
        Assert.That(failed, Is.EqualTo(new CameraFailed("No job came within the time a camera process waits for one")));
    }

    /// <summary>A job id that is not one is not taken, and nothing is read or written.</summary>
    [Test]
    public async Task A_job_id_that_is_not_one_is_not_taken()
    {
        using var input = new MemoryStream(Encoding.UTF8.GetBytes("{\"type\":\"job\",\"body\":{\"protocol\":1,\"kind\":\"Record\"}}\n"));
        using var output = new MemoryStream();

        Assert.That(await new CameraHost().RunAsync("OBSBOT", input, output), Is.EqualTo(CameraProtocol.JobNotTakenExitCode));
        Assert.That(input.Position, Is.Zero);
        Assert.That(output.Length, Is.Zero);
    }

    /// <summary>The fake process reads the job a starter sends, and its lines reach the starter, typed by <see cref="CameraLines.Read"/>; its exit is the test's.</summary>
    [Test]
    public async Task The_fake_process_speaks_the_camera_lines()
    {
        await using var process = new FakeCameraProcess();

        await CameraLines.SendJobAsync(process.Channel, new CameraJob(CameraProtocol.Version, CameraJobKind.Check, "id", "Integrated Camera", "1080p", StillPath: "still.jpg"), CancellationToken.None);
        var job = await process.ReadJobAsync();
        await process.ProcessEnd.SendAsync(CameraProtocol.Opened, new CameraOpened("Integrated Camera", 1920, 1080, 30, "hardware"), CameraJson.Default.CameraOpened, CancellationToken.None);
        var opened = CameraLines.Read((await process.Channel.ReceiveAsync(CancellationToken.None))!);
        process.Exit(CameraProtocol.DoneExitCode);

        Assert.That(job, Is.EqualTo(new CameraJob(CameraProtocol.Version, CameraJobKind.Check, "id", "Integrated Camera", "1080p", StillPath: "still.jpg")));
        Assert.That(opened, Is.EqualTo(new CameraOpened("Integrated Camera", 1920, 1080, 30, "hardware")));
        Assert.That(process.ExitCode, Is.EqualTo(0));
        Assert.That(CameraLines.Read(new Envelope("frame")), Is.Null, "a type this build does not know is passed over");
    }

    /// <summary>The host over the fake process's own end, run on the thread pool.</summary>
    private static Task<int> Host(FakeCameraProcess process) => Task.Run(() => new CameraHost().RunAsync(JobId, StreamOf(process), StreamOf(process)));

    /// <summary>The process's end of the pipe, as the host's input and output.</summary>
    private static Stream StreamOf(FakeCameraProcess process) => process.ProcessStream;

    /// <summary>What the host answers to the first line <paramref name="send"/> writes: the failed's words, after exit 4.</summary>
    private static async Task<string> NotTakenAsync(Func<LineChannel, ValueTask> send)
    {
        await using var process = new FakeCameraProcess();
        var host = Host(process);

        await send(process.Channel);
        var failed = CameraLines.Read((await process.Channel.ReceiveAsync(CancellationToken.None))!) as CameraFailed;

        Assert.That(await host, Is.EqualTo(CameraProtocol.JobNotTakenExitCode));
        return failed!.Why;
    }
}
