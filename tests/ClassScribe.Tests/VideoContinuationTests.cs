using ClassScribe.Commands;
using ClassScribe.Recorder;
using ClassScribe.YouTube;

namespace ClassScribe.Tests;

/// <summary>
/// A YouTube video's run is never continued (design 2.5), from any window: the request that would continue a lost recorder's
/// recording is none for a video's, as it is one for a class's.
/// </summary>
[TestFixture]
public class VideoContinuationTests
{
    /// <summary>A class's lost recording is continued as its next part; a video's is not, whatever window lost it.</summary>
    [Test]
    public void A_lost_videos_recording_is_never_continued()
    {
        var id = TestRuns.Id();
        var lost = TestRuns.Record(id, RunState.Recording) with { BaseName = "Risala_2026-10-15_23-00-02Z" };
        var video = lost with
        {
            Request = lost.Request with { Video = YouTubeTestRuns.Video() with { Obtained = VideoSource.Recorded, BrowserProcessId = 7100, BrowserStartedUtc = TestRuns.Start } }
        };

        Assert.That(AttachCommand.ContinuationOf(lost, TestRuns.Start.AddMinutes(5))!.ContinueBaseName, Is.EqualTo("Risala_2026-10-15_23-00-02Z"));
        Assert.That(AttachCommand.ContinuationOf(video, TestRuns.Start.AddMinutes(5)), Is.Null);
    }
}
