using System.Text.Json;
using System.Text.Json.Nodes;
using ClassScribe.Recorder;

namespace ClassScribe.Tests;

/// <summary>
/// Run files with a YouTube video's request (design 4.5): the request's video is written and read back, and a run file
/// written before videos existed, without the field, still reads, as a class's.
/// </summary>
[TestFixture]
public class VideoRunRecordTests
{
    /// <summary>A run file with a video is read back with it, and its run is a video's.</summary>
    [Test]
    public void A_run_file_with_a_video_is_read_back_with_it()
    {
        using var runtime = new TestRuntime();
        var id = TestRuns.Id();
        var record = TestRuns.Record(id) with { Request = TestRuns.Request(id) with { Video = YouTubeTestRuns.Video() } };

        runtime.Journal.Write(runtime.Journal.RunFile(id), record);
        var read = runtime.Journal.Find(id);

        Assert.That(read!.Record.Request.Video, Is.EqualTo(YouTubeTestRuns.Video()));
        Assert.That(read.IsVideo, Is.True);
        Assert.That(File.ReadAllText(runtime.Journal.RunFile(id)), Does.Contain("\"video\":{\"url\":\"https://www.youtube.com/watch?v=abcdefghijk\""));
    }

    /// <summary>A run file written without the video's field, as before videos, reads as a class's run.</summary>
    [Test]
    public void A_run_file_without_the_field_reads_as_a_classs()
    {
        using var runtime = new TestRuntime();
        var id = TestRuns.Id();
        var json = JsonNode.Parse(JsonSerializer.Serialize(TestRuns.Record(id), RecorderJson.Default.RunRecord))!;
        json["request"]!.AsObject().Remove("video");
        Directory.CreateDirectory(runtime.Folder.Runs);
        File.WriteAllText(runtime.Journal.RunFile(id), json.ToJsonString());

        var read = runtime.Journal.Find(id);

        Assert.That(read!.Record.Request.Video, Is.Null);
        Assert.That(read.IsVideo, Is.False);
    }
}
