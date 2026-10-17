using System.Diagnostics;
using System.Runtime.Versioning;
using ClassScribe.Desktop;
using ClassScribe.Desktop.Windows;

namespace ClassScribe.Tests;

/// <summary>Naming the program behind a process id, asked of Windows about this process and about processes that do not exist.</summary>
[TestFixture, Platform("Win"), Category("WindowsProcess")]
[SupportedOSPlatform("windows")]
public sealed class ProcessNamesTests
{
    /// <summary>This process is named by its program, without .exe, and its program's full path.</summary>
    [Test]
    public void This_process_is_named_by_its_program()
    {
        using var current = Process.GetCurrentProcess();

        var image = new ProcessNames().Describe(Environment.ProcessId);

        Assert.That(image, Is.EqualTo(new ProcessImage(current.ProcessName, Environment.ProcessPath)));
    }

    /// <summary>No process, Windows' own sounds, and a process id that no process has, give no name.</summary>
    [Test]
    public void A_process_that_does_not_exist_gives_no_name()
    {
        var names = new ProcessNames();
        var running = Process.GetProcesses().Select(process =>
        {
            using (process) return process.Id;
        }).ToHashSet();
        var unused = Enumerable.Range(1, int.MaxValue / 4).Select(n => n * 4 + 1).First(id => !running.Contains(id));

        Assert.Multiple(() =>
        {
            Assert.That(names.Describe(0), Is.Null);
            Assert.That(names.Describe(-1), Is.Null);
            Assert.That(names.Describe(unused), Is.Null);
        });
    }
}

/// <summary>Telling ClassScribe's own processes from other apps'.</summary>
[TestFixture]
public class OwnProcessesTests
{
    /// <summary>This process is ClassScribe's own, whatever it is called; so is any process of the same program, and any named ClassScribe, however it was started.</summary>
    [TestCase(777, null, true)]
    [TestCase(777, "chrome", true)]
    [TestCase(778, "ClassScribe", true)]
    [TestCase(778, "classscribe", true)]
    [TestCase(778, "dotnet", true)]
    [TestCase(778, "chrome", false)]
    [TestCase(778, null, false)]
    [TestCase(0, null, false)]
    public void Own_processes_are_this_one_and_those_of_the_same_program(int processId, string? processName, bool own)
    {
        var processes = new OwnProcesses(777, "dotnet");

        Assert.That(processes.IsOwn(processId, processName), Is.EqualTo(own));
    }

    /// <summary>Made for the app, it names this process and its program.</summary>
    [Test]
    public void Made_for_the_app_it_knows_this_process()
    {
        var processes = new OwnProcesses();

        Assert.Multiple(() =>
        {
            Assert.That(processes.IsOwn(Environment.ProcessId, null), Is.True);
            Assert.That(processes.IsOwn(Environment.ProcessId + 1, Path.GetFileNameWithoutExtension(Environment.ProcessPath)), Is.True);
            Assert.That(processes.IsOwn(Environment.ProcessId + 1, "chrome"), Is.False);
        });
    }
}
