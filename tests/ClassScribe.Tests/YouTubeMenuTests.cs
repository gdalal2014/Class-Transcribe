using ClassScribe.Commands;
using ClassScribe.Settings;
using ClassScribe.Transcription;
using ClassScribe.YouTube;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClassScribe.Tests;

/// <summary>
/// Menu choice Y (design 1.3): the links pasted, one or more on a line, or a dragged list file; an empty line ends the list;
/// a line that holds no link the batch takes is named and left out; the course, which Enter takes again from the last batch;
/// T or O; Esc going back at every step. The batch then plans and asks, over yt-dlp played.
/// </summary>
[TestFixture]
public class YouTubeMenuTests
{
    private YouTubeWorld _world = null!;
    private YouTubeMenuMemory _memory = null!;

    /// <summary>A world with one public video, and a memory of no course yet.</summary>
    [SetUp]
    public void SetUp()
    {
        _world = new YouTubeWorld();
        _world.Tools.With("abcdefghijk", YtDlpReadTests.Info("abcdefghijk", "The creed, part 1", duration: 3), 3);
        _memory = new YouTubeMenuMemory();
    }

    /// <summary>Deletes the world's folders.</summary>
    [TearDown]
    public void TearDown() => _world.Dispose();

    /// <summary>The menu over the world's batch.</summary>
    private YouTubeMenu Menu() =>
        new(new YouTubeCommand(_world.Batch(), null!, new FakeUserSignals(), _world.Ui, _world.YouTube, _world.Log<YouTubeCommand>()), _world.Ui, _world.ClassFolders,
            _memory, new KeytermLoader(new SettingsPlaces(AppContext.BaseDirectory, userFolder: null), NullLogger<KeytermLoader>.Instance), _world.ElevenLabs);

    /// <summary>
    /// Links pasted, a line without one named and left out, the course typed, T: the batch plans, asks YES, downloads into
    /// the course and transcribes; the course is remembered for the next batch.
    /// </summary>
    [Test]
    public async Task Pasted_links_a_course_and_T_run_the_batch()
    {
        _world.Ui.TypeLine("https://youtu.be/abcdefghijk  hello");
        _world.Ui.TypeLine("");
        _world.Ui.TypeLine("Risala");
        _world.Ui.Type('d');
        _world.Ui.Type('t');
        _world.Ui.TypeLine("YES");

        var exitCode = await Menu().RunAsync().WaitAsync(TimeSpan.FromMinutes(1));

        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(_world.Screen, Has.Some.StartsWith("Left out: Line 1 of what you pasted: \"hello\" is not a YouTube link"));
        Assert.That(_world.Screen, Has.Some.EqualTo("1 video. The course they belong to:"));
        Assert.That(_world.Screen, Has.Some.StartsWith("Then transcribe each one? T transcribes (about $0.22 an hour of audio)"));
        Assert.That(_world.Tools.Downloads, Is.EqualTo(["abcdefghijk"]));
        Assert.That(_world.Transcriber.Asked, Has.Count.EqualTo(1));
        Assert.That(_memory.LastCourse, Is.EqualTo("Risala"));
    }

    /// <summary>A dragged list file is read as one; Enter at the course takes the last one used; O fetches the audio alone.</summary>
    [Test]
    public async Task A_dragged_list_file_Enter_for_the_last_course_and_O()
    {
        var list = _world.Recordings.File("links.txt");
        File.WriteAllText(list, "# one video\nhttps://youtu.be/abcdefghijk\n");
        _memory.LastCourse = "Risala";
        _world.Ui.TypeLine($"\"{list}\"");
        _world.Ui.TypeLine("");
        _world.Ui.TypeLine("");
        _world.Ui.Type('d');
        _world.Ui.Type('o');
        _world.Ui.TypeLine("YES");

        var exitCode = await Menu().RunAsync().WaitAsync(TimeSpan.FromMinutes(1));

        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(_world.Screen, Has.Some.EqualTo("1 video. The course they belong to (Enter takes \"Risala\", the last one used):"));
        Assert.That(Directory.Exists(Path.Combine(_world.Recordings.Path, "Risala")), Is.True);
        Assert.That(_world.Transcriber.Asked, Is.Empty);
    }

    /// <summary>Esc goes back at every step: nothing is read or fetched.</summary>
    [TestCase(0)]
    [TestCase(1)]
    [TestCase(2)]
    public async Task Esc_goes_back_at_every_step(int step)
    {
        if (step >= 1) _world.Ui.TypeLine("https://youtu.be/abcdefghijk");
        if (step >= 1) _world.Ui.TypeLine("");
        if (step >= 2) _world.Ui.TypeLine("Risala");
        if (step < 2) _world.Ui.TypeLine(null);
        _world.Ui.Type('\u001b');

        var exitCode = await Menu().RunAsync().WaitAsync(TimeSpan.FromMinutes(1));

        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(_world.Tools.Calls, Is.Empty);
    }

    /// <summary>A list without a link the batch takes does nothing.</summary>
    [Test]
    public async Task No_link_does_nothing()
    {
        _world.Ui.TypeLine("https://www.youtube.com/@teacher");
        _world.Ui.TypeLine("");

        Assert.That(await Menu().RunAsync().WaitAsync(TimeSpan.FromMinutes(1)), Is.EqualTo(0));
        Assert.That(_world.Screen, Has.Some.EqualTo("No link was given; nothing to do."));
        Assert.That(_world.Tools.Calls, Is.Empty);
    }
}

/// <summary>
/// Menu choice Y, recording the videos as they play (design 1.3, R): the question R or D; a playlist's or a live link left out
/// with its reason, which only D takes; the record path's batch asking Y to start; and nothing to do without a video's link.
/// </summary>
[TestFixture]
public class YouTubeMenuRecordTests
{
    private VideoWorld _world = null!;

    /// <summary>A world with one video's title at YouTube.</summary>
    [SetUp]
    public void SetUp()
    {
        _world = new VideoWorld();
        _world.Title("The creed, part 1");
    }

    /// <summary>Deletes the world's folders.</summary>
    [TearDown]
    public void TearDown() => _world.Dispose();

    /// <summary>Runs the menu over the world's record path, the clock moving on until it is done.</summary>
    private async Task<int> RunMenuAsync()
    {
        var command = new YouTubeCommand(null!, null!, _world.Signals, _world.Ui, _world.YouTube, _world.Log<YouTubeCommand>(), _world.Batch());
        var running = new YouTubeMenu(command, _world.Ui, _world.ClassFolders, new YouTubeMenuMemory(), new KeytermLoader(new SettingsPlaces(AppContext.BaseDirectory, userFolder: null), NullLogger<KeytermLoader>.Instance), _world.ElevenLabs).RunAsync();
        await _world.Runtime.Time.AdvanceUntilAsync(() => running.IsCompleted, TimeSpan.FromMilliseconds(500), timeoutSeconds: 60);
        return await running;
    }

    /// <summary>
    /// R records as they play: the question names both ways; a playlist's link is left out with its reason; the video is
    /// recorded once Y starts, into the recordings folder of the settings, and transcribed.
    /// </summary>
    [Test]
    public async Task R_records_the_videos_as_they_play_and_leaves_a_playlist_out()
    {
        _world.Ui.TypeLine("https://youtu.be/abcdefghijk https://www.youtube.com/playlist?list=PLxyz");
        _world.Ui.TypeLine("");
        _world.Ui.TypeLine("Risala");
        _world.Ui.Type('r', 't', 'y');

        Assert.That(await RunMenuAsync(), Is.Zero);

        var screen = _world.Screen;
        Assert.That(screen, Is.SupersetOf(new[]
        {
            "YouTube videos, each transcribed like a class.",
            "How should ClassScribe get their audio?",
            "  R  Record each as it plays, in a Chrome window of ClassScribe's own. You press play there, and it takes",
            "     as long as the videos do. For any video you can watch.",
            "  D  Download the audio with yt-dlp: fast, but only for videos you uploaded or whose owner allows",
            "     downloading, and never your course's.",
            "Left out: https://www.youtube.com/playlist?list=PLxyz is a playlist's link, which only D takes; paste each video's own link to record it as it plays.",
            "Y starts; any other key goes back."
        }));
        Assert.That(_world.Started.Single().Video!.VideoId, Is.EqualTo("abcdefghijk"));
        Assert.That(_world.Transcriber.Asked, Has.Count.EqualTo(1));
    }

    /// <summary>R with no video's own link left does nothing, and says so.</summary>
    [Test]
    public async Task R_without_a_videos_own_link_does_nothing()
    {
        _world.Ui.TypeLine("https://www.youtube.com/playlist?list=PLxyz");
        _world.Ui.TypeLine("");
        _world.Ui.TypeLine("Risala");
        _world.Ui.Type('r');

        Assert.That(await RunMenuAsync(), Is.Zero);
        Assert.That(_world.Screen, Has.Some.EqualTo("No video to record; nothing to do."));
        Assert.That(_world.Http.Requests, Is.Empty);
    }

    /// <summary>R with more than 10 videos is refused, with what to do: recording takes as long as the videos play.</summary>
    [Test]
    public async Task R_with_more_than_ten_videos_is_refused()
    {
        _world.Ui.TypeLine(string.Join(' ', Enumerable.Range(0, VideoBatch.MostVideos + 1).Select(i => $"https://youtu.be/v{i:0000000000}")));
        _world.Ui.TypeLine("");
        _world.Ui.TypeLine("Risala");
        _world.Ui.Type('r');

        Assert.That(await RunMenuAsync(), Is.Zero);
        Assert.That(_world.Screen, Has.Some.EqualTo(
            "ClassScribe records at most 10 videos at a time as they play, each for as long as it plays; these are 11. Paste fewer, or download them with D."));
        Assert.That(_world.Http.Requests, Is.Empty);
    }
}
