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
            _memory, new KeytermLoader(NullLogger<KeytermLoader>.Instance), _world.ElevenLabs);

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
