using ClassScribe.Recorder;
using ClassScribe.YouTube;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClassScribe.Tests;

/// <summary>
/// The pace of a batch's requests (design 3.4), on the steady clock moved by hand: reads at most one every 6 seconds;
/// downloads 10 to 20 seconds after the last ended; at most 60 begun in any hour. And the wait while a class records
/// (design 2.5), which a video's own run never holds up.
/// </summary>
[TestFixture]
public class BatchPaceTests
{
    /// <summary>A second read waits until 6 seconds after the first began; the first waits for nothing.</summary>
    [Test]
    public async Task Reads_are_at_most_one_every_six_seconds()
    {
        var time = new ManualTimeProvider(TestRuns.Start);
        var pace = new BatchPace(time, new Random(1));

        await pace.BeforeReadAsync(CancellationToken.None);
        time.Advance(TimeSpan.FromSeconds(2));
        var second = pace.BeforeReadAsync(CancellationToken.None);

        Assert.That(second.IsCompleted, Is.False);
        Assert.That(time.UntilNextTimer, Is.EqualTo(TimeSpan.FromSeconds(4)));
        time.Advance(TimeSpan.FromSeconds(4));
        await second.WaitAsync(TimeSpan.FromSeconds(10));
    }

    /// <summary>A download waits 10 to 20 seconds after the last one ended, drawn at random; the first waits for nothing.</summary>
    [Test]
    public async Task A_download_waits_ten_to_twenty_seconds_after_the_last()
    {
        var time = new ManualTimeProvider(TestRuns.Start);
        var pace = new BatchPace(time, new Random(7));

        await pace.BeforeDownloadAsync(CancellationToken.None);
        pace.DownloadEnded();
        var next = pace.BeforeDownloadAsync(CancellationToken.None);

        Assert.That(next.IsCompleted, Is.False);
        Assert.That(time.UntilNextTimer, Is.InRange(BatchPace.ShortestPause, BatchPace.LongestPause));
        time.Advance(time.UntilNextTimer!.Value);
        await next.WaitAsync(TimeSpan.FromSeconds(10));
    }

    /// <summary>No more than 60 downloads begin in any hour: the 61st waits until the first is an hour old.</summary>
    [Test]
    public async Task At_most_sixty_downloads_begin_in_an_hour()
    {
        var time = new ManualTimeProvider(TestRuns.Start);
        var pace = new BatchPace(time, new Random(3));
        for (var i = 0; i < BatchPace.MostDownloadsAnHour; i++)
        {
            var begun = pace.BeforeDownloadAsync(CancellationToken.None);
            while (!begun.IsCompleted) time.Advance(time.UntilNextTimer ?? TimeSpan.FromMilliseconds(1));
            await begun;
            time.Advance(TimeSpan.FromSeconds(1));
            pace.DownloadEnded();
        }

        var start = time.GetTimestamp();
        var last = pace.BeforeDownloadAsync(CancellationToken.None);
        while (!last.IsCompleted) time.Advance(time.UntilNextTimer ?? TimeSpan.FromMilliseconds(1));
        await last;

        Assert.That(time.GetElapsedTime(start), Is.GreaterThan(TimeSpan.FromMinutes(30)), "the 61st waited for the first to be an hour old");
    }

    /// <summary>A cancel ends a wait at once.</summary>
    [Test]
    public void A_cancel_ends_a_wait()
    {
        var time = new ManualTimeProvider(TestRuns.Start);
        var pace = new BatchPace(time, new Random(1));
        pace.BeforeReadAsync(CancellationToken.None).Wait();
        using var cancel = new CancellationTokenSource();

        var waiting = pace.BeforeReadAsync(cancel.Token);
        cancel.Cancel();

        Assert.That(async () => await waiting, Throws.InstanceOf<OperationCanceledException>());
    }

    /// <summary>
    /// The batch waits while a class records, saying so once, and goes on once it has stopped, looking every 30 seconds; a
    /// video's own run never holds it up.
    /// </summary>
    [Test]
    public async Task The_batch_waits_while_a_class_records_but_not_for_a_video()
    {
        using var runtime = new TestRuntime();
        var log = new RecordingLogger<ClassWait>();
        var wait = new ClassWait(runtime.Journal, runtime.Time, log);
        var video = TestRuns.Record(TestRuns.Id(2), RunState.Recording) with { Request = TestRuns.Request(TestRuns.Id(2), "Risala") with { Video = YouTubeTestRuns.Video() } };
        runtime.Journal.Write(runtime.Journal.RunFile(video.RunId), video);
        runtime.Processes.Start(TestRuns.ProcessId, TestRuns.ProcessStart);

        await wait.WhileAClassRecordsAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.That(log.Messages, Is.Empty, "a video's run never holds the batch up");

        var lesson = TestRuns.Record(TestRuns.Id(3), RunState.Recording, name: "Kalam");
        runtime.Journal.Write(runtime.Journal.RunFile(lesson.RunId), lesson);
        var made = runtime.Time.TimersMade;
        var waiting = wait.WhileAClassRecordsAsync(CancellationToken.None);
        await Wait.UntilAsync(() => runtime.Time.TimersMade > made, timeoutSeconds: 10);

        Assert.That(waiting.IsCompleted, Is.False);
        Assert.That(log.Messages, Is.EqualTo(["YouTube: waiting while \"Kalam\" records; the videos go on after it"]));

        runtime.Journal.Write(runtime.Journal.RunFile(lesson.RunId), lesson with { State = RunState.Finished });
        runtime.Time.Advance(ClassWait.Poll);
        await waiting.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.That(log.Messages[^1], Is.EqualTo("YouTube: going on, now that \"Kalam\" has stopped recording"));
    }
}
