using System.Runtime;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using ClassScribe.Desktop.Windows;

namespace ClassScribe.Tests;

/// <summary>
/// A program started with three pipes and a kill-on-close job of its own (<see cref="PipedProcess"/>), the way ClassScribe
/// starts its tools and its camera process, over a stand-in for Windows' start: what it makes for a start is let go again
/// when the start throws rather than answering, as Windows' own start may (a test run's guard, or a launch it cannot set
/// up). The job and the pipes are real, the test's own; no program is started.
/// </summary>
[TestFixture, Platform("Win")]
[SupportedOSPlatform("windows")]
public class PipedProcessTests
{
    /// <summary>How many throwing starts the handle count is measured over.</summary>
    private const int Starts = 50;

    /// <summary>
    /// A start that throws leaves nothing open: the job named in the launch is closed, and over <see cref="Starts"/> such
    /// starts, with the garbage collector held off so that no finalizer closes what was left, this process holds no more
    /// handles than before (left open, the job and the three pipes' own ends would be four handles a start).
    /// </summary>
    [Test]
    public void A_start_that_throws_leaves_no_job_or_pipe_open()
    {
        var api = new FakeProcessCreationApi { OnStart = _ => throw new InvalidOperationException("Windows' start threw") };
        var folder = Environment.CurrentDirectory;
        var environment = new Dictionary<string, string> { ["PATH"] = Environment.SystemDirectory };

        Assert.Throws<InvalidOperationException>(() => PipedProcess.Start(api, "C:\\Tools\\tool.exe", "tool.exe", folder, environment, out _));
        Assert.That(api.Launches.Single().Job!.IsClosed, Is.True, "the job was closed");

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Assume.That(GC.TryStartNoGCRegion(64L * 1024 * 1024), Is.True, "The garbage collector would not hold off.");
        int added;
        try
        {
            var before = HandleCount();
            for (var i = 0; i < Starts; i++)
                Assert.Throws<InvalidOperationException>(() => PipedProcess.Start(api, "C:\\Tools\\tool.exe", "tool.exe", folder, environment, out _));

            added = HandleCount() - before;
        }
        finally
        {
            if (GCSettings.LatencyMode == GCLatencyMode.NoGCRegion) GC.EndNoGCRegion();
        }

        TestContext.Out.WriteLine($"{Starts} starts that threw: {added:+0;-0;0} handles");
        Assert.That(added, Is.LessThan(10), "nothing a start made was left open");
    }

    /// <summary>The number of handles this process has open.</summary>
    private static int HandleCount()
    {
        if (!GetProcessHandleCount(GetCurrentProcess(), out var count))
            throw new InvalidOperationException($"Windows did not give the handle count (error {Marshal.GetLastPInvokeError()}).");

        return (int)count;
    }

    /// <summary>The pseudo-handle of this process.</summary>
    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    /// <summary>Reads how many handles the process has open.</summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessHandleCount(IntPtr process, out uint count);
}
