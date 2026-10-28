using System.Diagnostics;
using ClassScribe.Audio;
using ClassScribe.Commands;
using ClassScribe.Recorder;
using ClassScribe.Runs;
using ClassScribe.YouTube;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClassScribe.Tests;

/// <summary>
/// A YouTube video's run in the recorder (design 2.5 and 4.5): it listens for its yield beside its stop event, and a class
/// that sets it stops the video, unconfirmed, as a class comes first; a class's run never listens for one. The events are
/// real, named after a fresh run id; the video's window is this test's own process, alive, with its window played.
/// </summary>
public partial class RecorderHostTests
{
    /// <summary>The video window's titles and closes, played.</summary>
    private FakeVideoWindows _videoWindows = null!;

    /// <summary>When this test's process started, as the window's browser process it stands for.</summary>
    private static DateTimeOffset ThisProcessStarted
    {
        get
        {
            using var process = Process.GetCurrentProcess();
            return new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
        }
    }

    /// <summary>A request to record a YouTube video of the course Risala as it plays, its window this test's own process, written as the batch's window writes it.</summary>
    private RecordRequest WriteVideoRequest()
    {
        var request = new RecordRequest(RunIds.New(_time.GetUtcNow()), "Risala", Transcribe: false, new SourcePlan(RecordingMode.Class, new ClassSource.App("chrome"), Microphone: null),
            _folder.Path, ContinueChoice.New, ContinueBaseName: null, Minutes: null, Open: false,
            YouTubeTestRuns.Video() with { Obtained = VideoSource.Recorded, BrowserProcessId = Environment.ProcessId, BrowserStartedUtc = ThisProcessStarted });
        _runtime.Journal.WriteRequest(request, Snapshot());
        _runId = request.RunId;
        _videoWindows.Show(Environment.ProcessId, "The creed, part 1 - YouTube");
        return request;
    }

    /// <summary>
    /// A video's run listens for its yield beside its stop event: a class that sets it stops the video, which ends with a class
    /// as its cause, its end not confirmed, its source file saying so, and its window asked to close.
    /// </summary>
    [Test]
    public async Task A_videos_run_gives_way_to_a_class_through_its_yield_event()
    {
        var request = WriteVideoRequest();

        var run = Host().RunAsync(request.RunId, apiKey: null);
        await StartedAsync(run);
        Assert.That(StopSignal.TrySetYield(request.RunId, NullLogger.Instance), Is.True, "a video's run listens for a class");

        Assert.That(await EndedAsync(run), Is.Zero);
        var record = RunFile(request.RunId);
        Assert.That(record.EndedBy, Is.EqualTo(RunEndCause.ClassStarted));
        var source = VideoSourceFile.Read(new Storage.RecordingFiles(record.Folder!, record.BaseName!).Source)!;
        Assert.That((source.Ended, source.EndConfirmed), Is.EqualTo(("stopped so that a class could record", false)));
        Assert.That(_videoWindows.ClosesOf(Environment.ProcessId), Is.EqualTo(1));
        Assert.That(LoggedLines(), Has.Some.EndsWith("A class is starting, so this video's recording gives way to it"));
    }

    /// <summary>A class's run never listens for a yield: setting one reaches nothing, and the class records on until it is stopped.</summary>
    [Test]
    public async Task A_classs_run_never_listens_for_a_yield()
    {
        var request = WriteRequest();

        var run = Host().RunAsync(request.RunId, apiKey: null);
        await StartedAsync(run);

        Assert.That(StopSignal.TrySetYield(request.RunId, NullLogger.Instance), Is.False);
        StopFromAnotherWindow(request.RunId);
        Assert.That(await EndedAsync(run), Is.Zero);
        Assert.That(RunFile(request.RunId).EndedBy, Is.EqualTo(RunEndCause.Q));
    }
}
