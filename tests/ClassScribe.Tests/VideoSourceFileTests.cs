using ClassScribe.YouTube;

namespace ClassScribe.Tests;

/// <summary>
/// The source file beside a video's recording (design 2.3): written whole and atomically, read back as written, with the
/// fields a later build may add passed over, and a file that is not one refused in words.
/// </summary>
[TestFixture]
public class VideoSourceFileTests
{
    private TempFolder _folder = null!;

    /// <summary>A temporary folder for the files.</summary>
    [SetUp]
    public void SetUp() => _folder = new TempFolder();

    /// <summary>Deletes the folder; a file left open fails that, which is the point.</summary>
    [TearDown]
    public void TearDown() => _folder.Dispose();

    /// <summary>A downloaded video's source, as the batch writes it.</summary>
    internal static VideoSource Downloaded(string videoId = "abcdefghijk", string title = "The creed, part 1") => new(
        VideoSource.YouTubeKind, YouTubeLinks.WatchUrl(videoId), videoId, title, "Your Channel", "@yourchannel", "UCxxxxxxxxxxxxxxxxxxxxxx",
        "2025-03-03", "01:02:33", "public", new VideoPlaylist("PLxyz", "Risala, the creed", 1), VideoSource.Downloaded, "yt-dlp 2026.08.19",
        new DateTimeOffset(2026, 10, 18, 14, 2, 11, TimeSpan.Zero), "You typed YES for the batch (18 Oct 2026 14:00 UTC)", VideoSource.ByYes,
        "Downloaded", EndConfirmed: true);

    /// <summary>What is written is read back as it was, with the field names of the design.</summary>
    [Test]
    public void What_is_written_is_read_back()
    {
        var path = _folder.File("Risala_2026-10-18_14-02-11Z.source.json");
        var source = Downloaded(title: "العقيدة, part 1");

        VideoSourceFile.Write(path, source);

        Assert.That(VideoSourceFile.Read(path), Is.EqualTo(source));
        var text = File.ReadAllText(path);
        Assert.That(text, Does.Contain("\"kind\": \"youtube\"").And.Contain("\"videoId\": \"abcdefghijk\"").And.Contain("\"endConfirmed\": true"));
        Assert.That(text, Does.Contain("العقيدة"), "the title is written as it is, not escaped");
    }

    /// <summary>A file written again replaces the old one whole, and nothing of the write is left beside it.</summary>
    [Test]
    public void A_file_written_again_is_replaced_whole_with_nothing_left_beside_it()
    {
        var path = _folder.File("Risala_2026-10-18_14-02-11Z.source.json");
        VideoSourceFile.Write(path, Downloaded());

        VideoSourceFile.Write(path, Downloaded() with { Ended = "Something else", EndConfirmed = false });

        Assert.That(VideoSourceFile.Read(path)!.Ended, Is.EqualTo("Something else"));
        Assert.That(Directory.GetFiles(_folder.Path).Select(Path.GetFileName), Is.EqualTo(["Risala_2026-10-18_14-02-11Z.source.json"]));
    }

    /// <summary>A field this build does not know, as a later build may add, is passed over.</summary>
    [Test]
    public void A_field_this_build_does_not_know_is_passed_over()
    {
        var path = _folder.File("a.source.json");
        File.WriteAllText(path, """{"kind":"youtube","url":"https://www.youtube.com/watch?v=abcdefghijk","videoId":"abcdefghijk","title":"T","obtained":"downloaded","obtainedUtc":"2026-10-18T14:02:11Z","later":{"x":1}}""");

        var source = VideoSourceFile.Read(path);

        Assert.That(source!.VideoId, Is.EqualTo("abcdefghijk"));
        Assert.That(source.Obtained, Is.EqualTo(VideoSource.Downloaded));
    }

    /// <summary>No file reads as none.</summary>
    [Test]
    public void No_file_reads_as_none() => Assert.That(VideoSourceFile.Read(_folder.File("none.source.json")), Is.Null);

    /// <summary>A file that is not a video's source is refused, naming the file.</summary>
    [TestCase("not json")]
    [TestCase("null")]
    [TestCase("""{"kind":"youtube"}""")]
    [TestCase("""{"kind":"vimeo","videoId":"abcdefghijk","url":"u","title":"t","obtained":"downloaded"}""")]
    public void A_file_that_is_not_a_videos_source_is_refused(string text)
    {
        var path = _folder.File("a.source.json");
        File.WriteAllText(path, text);

        var refused = Assert.Throws<InvalidDataException>(() => VideoSourceFile.Read(path));

        Assert.That(refused!.Message, Does.Contain(path));
    }
}
