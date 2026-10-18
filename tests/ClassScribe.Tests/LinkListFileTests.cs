using System.Text;
using ClassScribe.YouTube;

namespace ClassScribe.Tests;

/// <summary>
/// A list of links (design 1.2): the words of the command line and of a list file, several on a line, comments and blank
/// lines passed over, a byte-order mark or none, duplicates taken once with a line that names them, and every word that is
/// no link named by where it stands.
/// </summary>
[TestFixture]
public class LinkListFileTests
{
    /// <summary>Several links on a line, comments after # at the start or after a space, and blank lines, each word with its line.</summary>
    [Test]
    public void A_files_words_are_its_links_with_their_lines()
    {
        const string text = """
            # The creed, part one and two
            https://youtu.be/abcdefghijk https://youtu.be/bcdefghijkl   # both parts

              https://www.youtube.com/watch?v=cdefghijklm#not-a-comment
            """;

        var words = LinkList.WordsOf(text, "risala-links.txt");

        Assert.That(words.Select(word => (word.Text, word.Where)), Is.EqualTo(new[]
        {
            ("https://youtu.be/abcdefghijk", "line 2 of risala-links.txt"),
            ("https://youtu.be/bcdefghijkl", "line 2 of risala-links.txt"),
            ("https://www.youtube.com/watch?v=cdefghijklm#not-a-comment", "line 4 of risala-links.txt")
        }));
    }

    /// <summary>A file written with a byte-order mark reads as one written without.</summary>
    [Test]
    public void A_byte_order_mark_changes_nothing()
    {
        using var folder = new TempFolder();
        var file = folder.File("links.txt");
        File.WriteAllText(file, "https://youtu.be/abcdefghijk\r\n", new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        var words = LinkList.ReadFile(file);

        Assert.That(words.Single().Text, Is.EqualTo("https://youtu.be/abcdefghijk"));
    }

    /// <summary>The links of the command line and the file are joined in order, a duplicate by ID taken once with a line naming it.</summary>
    [Test]
    public void Duplicates_are_taken_once_with_a_line_that_names_them()
    {
        var words = new List<LinkWord>
        {
            new("https://youtu.be/abcdefghijk", "the command line"),
            new("https://www.youtube.com/playlist?list=PLxyz", "line 1 of links.txt"),
            new("https://www.youtube.com/watch?v=bcdefghijkl", "line 2 of links.txt"),
            new("https://www.youtube.com/watch?v=abcdefghijk&t=30", "line 7 of links.txt"),
            new("https://www.youtube.com/playlist?list=PLxyz&si=1", "line 8 of links.txt")
        };

        var reading = LinkList.Read(words);

        Assert.That(reading.Problems, Is.Empty);
        Assert.That(reading.Links.Select(link => link.Id), Is.EqualTo(["abcdefghijk", "PLxyz", "bcdefghijkl"]));
        Assert.That(reading.Notes, Is.EqualTo(new[]
        {
            "Line 7 of links.txt lists the video abcdefghijk again; it is taken once",
            "Line 8 of links.txt lists the playlist PLxyz again; it is taken once"
        }));
    }

    /// <summary>A word that is no link the batch takes is named by where it stands, with its reason, and the rest are kept.</summary>
    [Test]
    public void A_word_that_is_no_link_is_named_by_where_it_stands()
    {
        var words = new List<LinkWord>
        {
            new("https://youtu.be/abcdefghijk", "line 1 of links.txt"),
            new("hello", "line 2 of links.txt"),
            new("https://www.youtube.com/embed/bcdefghijkl", "line 3 of links.txt")
        };

        var reading = LinkList.Read(words);

        Assert.That(reading.Links.Select(link => link.Id), Is.EqualTo(["abcdefghijk"]));
        Assert.That(reading.Problems, Has.Count.EqualTo(2));
        Assert.That(reading.Problems[0], Does.StartWith("Line 2 of links.txt: \"hello\" is not a YouTube link"));
        Assert.That(reading.Problems[1], Does.StartWith($"Line 3 of links.txt: {YouTubeLinks.EmbeddedPlayer}"));
    }

    /// <summary>An empty file, or one of comments alone, gives no words.</summary>
    [TestCase("")]
    [TestCase("# nothing here\n\n   \n#https://youtu.be/abcdefghijk")]
    public void A_file_without_links_gives_no_words(string text) =>
        Assert.That(LinkList.WordsOf(text, "links.txt"), Is.Empty);
}
