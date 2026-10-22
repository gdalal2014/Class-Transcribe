using ClassScribe.Drive;
using ClassScribe.Settings;
using ClassScribe.Storage;

namespace ClassScribe.Tests;

/// <summary>
/// A YouTube video's recording on Google Drive (design 2.10, with Gulzar's answer 6): YouTube:DriveUpload, not Drive:Upload,
/// decides what of it goes up, everything by default as for a class; its source file goes with it; and free-space passes
/// over a video's audio that was never sent, with a line, since it is not on Drive.
/// </summary>
[TestFixture]
public class VideoDriveTests
{
    private static readonly DateTimeOffset BatchStart = new(2026, 10, 18, 14, 2, 11, TimeSpan.Zero);
    private GoogleWorld _world = null!;
    private RecordingFiles _video = null!;

    /// <summary>A signed-in world and one downloaded video's recording, with its transcript, answer, log and source file.</summary>
    [SetUp]
    public void SetUp()
    {
        _world = new GoogleWorld();
        _world.SignedIn();
        _video = _world.WriteRecording(@"Risala\2026-10-18", "Risala", BatchStart, 3000, ".md", ".json", ".log", ".source.json");
    }

    /// <summary>Deletes the world's folders.</summary>
    [TearDown]
    public void TearDown() => _world.Dispose();

    /// <summary>The names of the files whose uploads began, in order.</summary>
    private IReadOnlyList<string> Started() => [.. _world.Google.Uploaded.Select(file => file.Name)];

    /// <summary>By default a video's recording goes up whole, its audio and source file included, whatever Drive:Upload says of classes.</summary>
    [Test]
    public async Task By_default_a_videos_recording_goes_up_whole()
    {
        _world.Options.Set(new DriveOptions { Upload = DriveUploadScope.WithoutAudio });

        await _world.RecordingUpload().UploadAsync(_video, _ => Task.CompletedTask, CancellationToken.None);

        Assert.That(Started(), Does.Contain($"{_video.BaseName}.wav"));
        Assert.That(Started(), Does.Contain($"{_video.BaseName}.source.json"));
    }

    /// <summary>With YouTube:DriveUpload WithoutAudio, a video's .wav stays on the laptop alone, while a class's audio goes up as Drive:Upload says.</summary>
    [Test]
    public async Task Without_audio_a_videos_wav_stays_while_a_classs_goes_up()
    {
        _world.YouTube.Set(new YouTubeOptions { DriveUpload = DriveUploadScope.WithoutAudio });
        var lesson = _world.WriteRecording(@"Risala\2026-10-18 (2)", "Risala", BatchStart.AddHours(5), 3000, ".md");

        await _world.RecordingUpload().UploadAsync(_video, _ => Task.CompletedTask, CancellationToken.None);
        await _world.RecordingUpload().UploadAsync(lesson, _ => Task.CompletedTask, CancellationToken.None);

        Assert.That(Started(), Does.Not.Contain($"{_video.BaseName}.wav"));
        Assert.That(Started(), Does.Contain($"{_video.BaseName}.source.json"));
        Assert.That(Started(), Does.Contain($"{lesson.BaseName}.wav"));
    }

    /// <summary>
    /// Free-space passes over a video's recording whose audio was never sent, with a line that says so and what to do,
    /// deleting nothing and refusing nothing, so that it does not make the command's exit code 1.
    /// </summary>
    [Test]
    public async Task Free_space_passes_over_a_videos_audio_that_was_never_sent()
    {
        _world.YouTube.Set(new YouTubeOptions { DriveUpload = DriveUploadScope.WithoutAudio });
        await _world.RecordingUpload().UploadAsync(_video, _ => Task.CompletedTask, CancellationToken.None);

        var freed = await _world.Freer().FreeAsync(_video, CancellationToken.None);

        Assert.That(freed.Deleted, Is.Empty);
        Assert.That(freed.Problem, Is.Null);
        Assert.That(File.Exists(_video.Segment(1)), Is.True);
        Assert.That(_world.Lines, Has.Some.Contains(
            $"{_video.BaseName} is a YouTube video's recording; its audio is not on Drive (YouTube:DriveUpload), so it stays. Delete it by hand if you need the room; the video can be fetched again."));
    }

    /// <summary>A video's audio that did go up, sent before the setting changed, is freed as a class's is.</summary>
    [Test]
    public async Task A_videos_audio_that_went_up_is_freed_as_a_classs()
    {
        await _world.RecordingUpload().UploadAsync(_video, _ => Task.CompletedTask, CancellationToken.None);
        _world.YouTube.Set(new YouTubeOptions { DriveUpload = DriveUploadScope.WithoutAudio });

        var freed = await _world.Freer().FreeAsync(_video, CancellationToken.None);

        Assert.That(freed.Deleted, Is.EqualTo([$"{_video.BaseName}.wav"]));
    }
}
