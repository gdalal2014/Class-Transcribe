using System.Text;
using ClassScribe.Transcription;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClassScribe.Tests;

/// <summary>Reading and validating the glossary file.</summary>
[TestFixture]
public class KeytermLoaderTests
{
    private TempFolder _folder = null!;
    private KeytermLoader _loader = null!;

    /// <summary>Creates a temporary folder and the loader under test.</summary>
    [SetUp]
    public void SetUp()
    {
        _folder = new TempFolder();
        _loader = new KeytermLoader(NullLogger<KeytermLoader>.Instance);
    }

    /// <summary>Deletes the temporary folder. That fails if a test left a file open, which is the point.</summary>
    [TearDown]
    public void TearDown() => _folder.Dispose();

    [Test]
    public void Comments_blank_lines_duplicates_and_invalid_terms_are_dropped()
    {
        var path = _folder.File("keyterms.txt");
        File.WriteAllLines(path,
        [
            "# Core terms",
            "",
            "kalām",
            "  Ashʿarī  ",
            "Kalām",
            "wājib al-wujūd",
            "bad[term]",
            "one two three four five six",
            new string('x', 50)
        ], Encoding.UTF8);

        var terms = _loader.Load(path);

        Assert.That(terms, Is.EqualTo(new[] { "kalām", "Ashʿarī", "wājib al-wujūd" }));
    }

    [Test]
    public void A_missing_file_means_no_glossary()
    {
        Assert.That(_loader.Load(_folder.File("nope.txt")), Is.Empty);
    }

    [Test]
    public void At_most_a_thousand_terms_are_sent()
    {
        var path = _folder.File("many.txt");
        File.WriteAllLines(path, Enumerable.Range(1, 1200).Select(i => $"term{i}"));

        var terms = _loader.Load(path);

        Assert.That(terms, Has.Count.EqualTo(1000));
        Assert.That(terms[999], Is.EqualTo("term1000"));
    }

    [TestCase("kalām")]
    [TestCase("mukhālafa li-l-ḥawādith")]
    [TestCase("al-Iqtiṣād fī al-Iʿtiqād")]
    public void Ordinary_terms_are_valid(string term)
    {
        Assert.That(KeytermLoader.Validate(term), Is.Null);
    }

    [TestCase(49, true)]
    [TestCase(50, false)]
    public void A_term_must_be_shorter_than_fifty_characters(int length, bool valid)
    {
        Assert.That(KeytermLoader.Validate(new string('a', length)) is null, Is.EqualTo(valid));
    }

    [TestCase("angle <bracket>")]
    [TestCase("curly {brace}")]
    [TestCase("back\\slash")]
    [TestCase("far too many words in here")]
    public void Terms_ElevenLabs_would_reject_are_invalid(string term)
    {
        Assert.That(KeytermLoader.Validate(term), Is.Not.Null);
    }

    [Test]
    public void The_shipped_starter_glossary_is_entirely_valid()
    {
        var shipped = Path.Combine(AppContext.BaseDirectory, "keyterms.txt");
        var listed = File.ReadLines(shipped).Select(l => l.Trim()).Count(l => l.Length > 0 && !l.StartsWith('#'));

        var terms = _loader.Load(shipped);

        Assert.That(terms, Has.Count.EqualTo(listed));
        Assert.That(terms, Is.Not.Empty);
    }
}
