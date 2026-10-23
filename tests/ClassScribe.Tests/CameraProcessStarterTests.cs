using System.Collections;
using System.ComponentModel;
using ClassScribe.Camera;
using ClassScribe.Camera.Windows;
using ClassScribe.Desktop.Windows;
using ClassScribe.Recorder;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClassScribe.Tests;

/// <summary>
/// The start of a camera process (teach-video 6.2, amendment 24), over a fake of Windows' start: this very program with
/// --camera-process and the job id, no window, its three pipes, a kill-on-close job, an environment without the API key,
/// ClassScribe's own folder; its lines read through the channel; a refused start and a job id that is not one; and the real
/// start, which the device guard refuses in a test, naming FakeCameraProcess, before anything starts.
/// </summary>
[TestFixture]
public class CameraProcessStarterTests
{
    /// <summary>The program the tests' starter runs as.</summary>
    private const string Exe = @"C:\Users\gdala\AppData\Local\Programs\ClassScribe\ClassScribe.exe";

    /// <summary>ClassScribe's own folder, as the tests' starter has it.</summary>
    private const string Folder = @"C:\Users\gdala\AppData\Local\ClassScribe";

    /// <summary>A well-formed job id.</summary>
    private static readonly string JobId = TestRuns.Id(6);

    /// <summary>This process's environment as the tests' starter sees it: the key, an ElevenLabs setting and a plain variable.</summary>
    private static IDictionary Environment() => new Hashtable
    {
        ["ELEVENLABS_API_KEY"] = "dummy-key-never-real",
        ["ElevenLabs__BaseUrl"] = "http://localhost:5001/",
        ["PATH"] = @"C:\Windows\System32"
    };

    /// <summary>A starter over <paramref name="api"/>, as the exe at <paramref name="program"/>, unguarded, as a test of the starter itself needs.</summary>
    private static CameraProcessStarter Starter(FakeProcessCreationApi api, string program = Exe) =>
        new(api, new ThisProgram(program, @"C:\Users\gdala\AppData\Local\Programs\ClassScribe\"), Folder, guarded: false, Environment, NullLogger.Instance);

    /// <summary>
    /// The launch: this program with --camera-process and the job id, no window, a Unicode environment without the key or
    /// any ElevenLabs setting, the three standard handles, a job of its own, and ClassScribe's own folder.
    /// </summary>
    [Test]
    public async Task The_camera_process_is_this_program_in_a_job_of_its_own_without_the_key()
    {
        var api = new FakeProcessCreationApi();

        await using var process = Starter(api).Start(JobId);

        var launch = api.Launches.Single();
        Assert.That(launch.Application, Is.EqualTo(Exe));
        Assert.That(launch.CommandLine, Is.EqualTo($"\"{Exe}\" --camera-process {JobId}"));
        Assert.That(launch.Flags, Is.EqualTo(ProcessCreationFlags.NoWindow | ProcessCreationFlags.UnicodeEnvironment));
        Assert.That(launch.WorkingFolder, Is.EqualTo(Folder));
        Assert.That(launch.InheritHandles, Is.True);
        Assert.That(launch.Handles, Is.Not.Null, "its three pipes, and no other handle");
        Assert.That(launch.Job, Is.Not.Null, "a kill-on-close job of its own");
        Assert.That(launch.Environment!.Keys, Has.None.EqualTo("ELEVENLABS_API_KEY").IgnoreCase.And.None.StartsWith("ElevenLabs__"));
        Assert.That(launch.Environment["PATH"], Is.EqualTo(@"C:\Windows\System32"));
        Assert.That(process.ProcessId, Is.EqualTo(api.Next.ProcessId));
    }

    /// <summary>Under dotnet, the camera process is dotnet running ClassScribe's dll from the app's folder.</summary>
    [Test]
    public async Task Under_dotnet_it_is_dotnet_running_the_dll()
    {
        var api = new FakeProcessCreationApi();

        await using var process = Starter(api, @"C:\Program Files\dotnet\dotnet.exe").Start(JobId);

        Assert.That(api.Launches.Single().CommandLine,
            Is.EqualTo($"\"C:\\Program Files\\dotnet\\dotnet.exe\" \"C:\\Users\\gdala\\AppData\\Local\\Programs\\ClassScribe\\ClassScribe.dll\" --camera-process {JobId}"));
    }

    /// <summary>What the camera process writes to its standard output reaches the starter through the channel, as lines.</summary>
    [Test]
    public async Task Its_lines_are_read_through_the_channel()
    {
        var api = new FakeProcessCreationApi
        {
            OnStart = launch => FakeProcessCreationApi.WriteTo(launch.Handles!.Output, "{\"type\":\"failed\",\"body\":{\"why\":\"not built\"}}\n")
        };

        await using var process = Starter(api).Start(JobId);
        var line = await process.Channel.ReceiveAsync(CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.That(CameraLines.Read(line!), Is.EqualTo(new CameraFailed("not built")));
    }

    /// <summary>A start Windows refuses is thrown with Windows' error; a job id that is not one is refused before anything starts.</summary>
    [Test]
    public void A_refused_start_and_a_job_id_that_is_not_one_are_thrown()
    {
        var api = new FakeProcessCreationApi { Refusal = 5 };

        var refused = Assert.Throws<Win32Exception>(() => Starter(api).Start(JobId));
        var notAnId = Assert.Throws<ArgumentException>(() => Starter(api).Start("OBSBOT --record"));

        Assert.That(refused!.NativeErrorCode, Is.EqualTo(5));
        Assert.That(refused.Message, Does.StartWith("Windows did not start the camera process"));
        Assert.That(notAnId!.Message, Does.Contain("is not a job id"));
        Assert.That(api.Launches, Has.Count.EqualTo(1), "the id that is not one never reached Windows");
    }

    /// <summary>The real starter, as the container makes it, refuses in a test before anything starts, naming FakeCameraProcess.</summary>
    [Test]
    public void The_real_start_is_refused_in_a_test()
    {
        using var runtime = new TestRuntime();
        var starter = new CameraProcessStarter(runtime.Folder, NullLoggerFactory.Instance);

        var refused = Assert.Throws<InvalidOperationException>(() => starter.Start(JobId));

        Assert.That(refused!.Message, Does.Contain("camera process").And.Contain("FakeCameraProcess"));
    }

    /// <summary>The fake starter hands out fake processes by job id, and refuses an id that is not one, as the real one does.</summary>
    [Test]
    public async Task The_fake_starter_hands_out_fake_processes()
    {
        var starter = new FakeCameraProcessStarter();

        await using var process = starter.Start(JobId);

        Assert.That(starter.Started.Single().JobId, Is.EqualTo(JobId));
        Assert.That(process, Is.SameAs(starter.Started.Single().Process));
        Assert.Throws<ArgumentException>(() => starter.Start("not-an-id"));
    }
}
