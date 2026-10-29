using System.Diagnostics;
using ClassScribe.Commands;
using ClassScribe.Desktop;
using ClassScribe.Desktop.Windows;
using ClassScribe.Recorder;
using ClassScribe.Runs;
using ClassScribe.Settings;
using ClassScribe.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace ClassScribe.Tests;

/// <summary>
/// The contract of the port of this program (architecture 4.3, slice 3b4) over its real adapter: <see cref="IThisProgram"/>
/// over <see cref="ThisProgram"/>, which reads this process's facts from Windows as the use cases read them before; the
/// command lines printed for later over it (<see cref="CommandLine"/>), the same strings as the explicit forms give for
/// those facts; and that the window's host and the recorder's resolve the port and the command lines to one instance each.
/// </summary>
[TestFixture]
public class ProgramPortTests
{
    private const string Recording = @"C:\Users\gdala\ClassScribe\Kalam\Kalam_2026-10-14_19-30-00Z.wav";

    /// <summary>The adapter reads this process's program path and folder, its id, and its start as Windows records it, in UTC.</summary>
    [Test]
    public void The_adapter_gives_this_process_s_program_id_and_start()
    {
        IThisProgram program = ThisProgram.Current;

        using var process = Process.GetCurrentProcess();
        Assert.Multiple(() =>
        {
            Assert.That(program.ProcessPath, Is.EqualTo(Environment.ProcessPath));
            Assert.That(program.AppFolder, Is.EqualTo(AppContext.BaseDirectory));
            Assert.That(program.ProcessId, Is.EqualTo(Environment.ProcessId));
            Assert.That(program.StartedUtc, Is.EqualTo(new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero)));
            Assert.That(program.StartedUtc.Offset, Is.EqualTo(TimeSpan.Zero));
        });
    }

    /// <summary>A program given to the adapter for a test keeps its path and folder, and still carries this process's id and start.</summary>
    [Test]
    public void A_program_given_for_a_test_keeps_its_path_and_this_process_s_facts()
    {
        IThisProgram program = new ThisProgram(@"C:\Program Files\ClassScribe\ClassScribe.exe", @"C:\Program Files\ClassScribe\");

        Assert.Multiple(() =>
        {
            Assert.That(program.ProcessPath, Is.EqualTo(@"C:\Program Files\ClassScribe\ClassScribe.exe"));
            Assert.That(program.AppFolder, Is.EqualTo(@"C:\Program Files\ClassScribe\"));
            Assert.That(program.ProcessId, Is.EqualTo(Environment.ProcessId));
            Assert.That(program.StartedUtc, Is.EqualTo(ThisProgram.Current.StartedUtc));
        });
    }

    /// <summary>
    /// The command lines printed for later, over the port, are the explicit forms for the port's path and folder: the
    /// published exe by its full path, or dotnet running the dll from the app's folder, and the dll with dotnet when
    /// Windows does not say which program runs.
    /// </summary>
    [Test]
    public void The_command_lines_over_the_port_are_the_explicit_forms_for_its_program()
    {
        var exe = new CommandLine(new ThisProgram(@"C:\Program Files\ClassScribe\ClassScribe.exe", @"C:\Program Files\ClassScribe\"));
        var dotnet = new CommandLine(new ThisProgram(@"C:\Program Files\dotnet\dotnet.exe", @"D:\Repos\Class-Transcribe\src\ClassScribe\bin\Debug\net10.0\"));
        var unknown = new CommandLine(new ThisProgram(null, @"D:\Repos\Class-Transcribe\src\ClassScribe\bin\Debug\net10.0\"));
        var request = TestRuns.Request(TestRuns.Id(1), "Kalam") with { OutputRoot = @"D:\Classes" };

        Assert.Multiple(() =>
        {
            Assert.That(exe.Transcribe(Recording), Is.EqualTo(@"& ""C:\Program Files\ClassScribe\ClassScribe.exe"" transcribe ""C:\Users\gdala\ClassScribe\Kalam\Kalam_2026-10-14_19-30-00Z.wav"""));
            Assert.That(exe.Transcribe(Recording, again: true), Is.EqualTo(CommandLine.Transcribe(Recording, @"C:\Program Files\ClassScribe\ClassScribe.exe", @"C:\Program Files\ClassScribe\", again: true)));
            Assert.That(exe.Rebuild(Recording), Is.EqualTo(CommandLine.Rebuild(Recording, @"C:\Program Files\ClassScribe\ClassScribe.exe", @"C:\Program Files\ClassScribe\")));
            Assert.That(exe.FinishVideo(Recording), Does.EndWith(@" finish-video ""C:\Users\gdala\ClassScribe\Kalam\Kalam_2026-10-14_19-30-00Z.wav"""));
            Assert.That(exe.SettingsSet("Drive:LessonVideos", "true"), Does.EndWith(@" settings set Drive:LessonVideos ""true"""));
            Assert.That(exe.DriveUpload(null), Does.EndWith(" drive upload"));
            Assert.That(exe.DriveUpload(Recording), Does.EndWith(@" drive upload ""C:\Users\gdala\ClassScribe\Kalam\Kalam_2026-10-14_19-30-00Z.wav"""));
            Assert.That(exe.YouTube(["https://www.youtube.com/watch?v=abcdefghijk"], "Risala", @"D:\Classes"),
                Is.EqualTo(CommandLine.YouTube(["https://www.youtube.com/watch?v=abcdefghijk"], "Risala", @"D:\Classes", @"C:\Program Files\ClassScribe\ClassScribe.exe", @"C:\Program Files\ClassScribe\")));
            Assert.That(exe.RecordClass("Mantiq"), Does.EndWith(@" record --class ""Mantiq"""));
            Assert.That(exe.RecordClass("Mantiq", transcribe: false), Does.EndWith(@" record-only --class ""Mantiq"""));
            Assert.That(exe.Record(request), Is.EqualTo(CommandLine.Record(request, @"C:\Program Files\ClassScribe\ClassScribe.exe", @"C:\Program Files\ClassScribe\")));
            Assert.That(dotnet.Rebuild(Recording), Does.StartWith(@"dotnet ""D:\Repos\Class-Transcribe\src\ClassScribe\bin\Debug\net10.0\ClassScribe.dll"" rebuild "));
            Assert.That(unknown.Rebuild(Recording), Is.EqualTo(dotnet.Rebuild(Recording)));
        });
    }

    /// <summary>
    /// Both hosts resolve the port to a program of this process, one for the process, and the command lines to one instance
    /// over it, so that every command printed in a run names the program the run came from.
    /// </summary>
    [Test]
    public void Both_hosts_resolve_the_port_and_the_command_lines_once_for_the_process()
    {
        using var temp = new TempFolder();
        using var runtime = new TestRuntime();
        File.WriteAllText(temp.File(SettingsFile.FileName), "{}");
        var places = new SettingsPlaces(temp.Path, userFolder: null);
        using var window = AppHost.Build(places, "test-key", services => services.AddSingleton(runtime.Folder).AddSingleton(new RoamingFolder(temp.File("Roaming"))));
        var runId = TestRuns.Id();
        using var log = new RecorderLog(runtime.Time);
        using var recorder = AppHost.BuildRecorder(new RecorderStart(runId, TestRuns.Request(runId), new Dictionary<string, string?>()), "test-key", log, runtime.Folder, places, runtime.Time);

        Assert.Multiple(() =>
        {
            foreach (var (name, services) in new[] { ("the window", window.Services), ("the recorder", recorder.Services) })
            {
                var program = services.GetRequiredService<IThisProgram>();
                Assert.That(program, Is.InstanceOf<ThisProgram>(), $"{name}: the adapter");
                Assert.That((program.ProcessPath, program.AppFolder, program.ProcessId), Is.EqualTo((Environment.ProcessPath, AppContext.BaseDirectory, Environment.ProcessId)), $"{name}: this process");
                Assert.That(services.GetRequiredService<IThisProgram>(), Is.SameAs(program), $"{name}: one for the process");
                var commandLine = services.GetRequiredService<CommandLine>();
                Assert.That(services.GetRequiredService<CommandLine>(), Is.SameAs(commandLine), $"{name}: one for the process");
                Assert.That(commandLine.Rebuild(Recording), Is.EqualTo(CommandLine.Rebuild(Recording, Environment.ProcessPath, AppContext.BaseDirectory)), $"{name}: this program's commands");
            }
        });
    }
}
