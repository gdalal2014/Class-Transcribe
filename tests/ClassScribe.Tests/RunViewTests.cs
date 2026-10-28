using ClassScribe.Recorder;
using ClassScribe.Runs;
using ClassScribe.Storage;

namespace ClassScribe.Tests;

/// <summary>Which recording a run records (architecture 3.15 a: RunView.IsRunOf in Core, from the list).</summary>
[TestFixture]
public class RunViewTests
{
    /// <summary>A run records the recording its file names, by its folder and base name in any letter case, a trailing separator or not.</summary>
    [Test]
    public void A_run_records_the_recording_its_file_names()
    {
        var run = TestRuns.Record(TestRuns.Id(1), RunState.Recording) with { Folder = @"C:\Recordings\Mantiq\2026-10-17\", BaseName = "Mantiq_2026-10-17_14-04-42Z" };

        Assert.That(RunView.IsRunOf(run, new RecordingFiles(@"C:\Recordings\Mantiq\2026-10-17", "Mantiq_2026-10-17_14-04-42Z")), Is.True);
        Assert.That(RunView.IsRunOf(run, new RecordingFiles(@"c:\recordings\mantiq\2026-10-17", "MANTIQ_2026-10-17_14-04-42Z")), Is.True);
    }

    /// <summary>Another base name or another folder is another recording, and a run whose files are not known yet records none.</summary>
    [Test]
    public void Another_recording_or_none_known_is_not_the_runs()
    {
        var run = TestRuns.Record(TestRuns.Id(1), RunState.Recording) with { Folder = @"C:\Recordings\Mantiq\2026-10-17", BaseName = "Mantiq_2026-10-17_14-04-42Z" };
        var starting = run with { Folder = null, BaseName = null };

        Assert.That(RunView.IsRunOf(run, new RecordingFiles(@"C:\Recordings\Mantiq\2026-10-17", "Mantiq_2026-10-17_16-00-00Z")), Is.False);
        Assert.That(RunView.IsRunOf(run, new RecordingFiles(@"C:\Recordings\Mantiq\2026-10-17 (2)", "Mantiq_2026-10-17_14-04-42Z")), Is.False);
        Assert.That(RunView.IsRunOf(starting, new RecordingFiles(@"C:\Recordings\Mantiq\2026-10-17", "Mantiq_2026-10-17_14-04-42Z")), Is.False);
    }
}