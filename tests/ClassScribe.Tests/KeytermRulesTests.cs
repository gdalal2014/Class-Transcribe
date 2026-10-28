using System.Text;
using ClassScribe.Transcription;

namespace ClassScribe.Tests;

/// <summary>
/// The rules of a glossary's terms, as a pure function of its lines (architecture 8.2): what ElevenLabs takes, how words are
/// counted, and which lines become terms, with the limits as a record. No file is read here.
/// </summary>
[TestFixture]
public class KeytermRulesTests
{
    /// <summary>Today's limits, ElevenLabs' own.</summary>
    private static readonly KeytermLimits Limits = KeytermLimits.ElevenLabs;

    /// <summary>The limits hold ElevenLabs' documented values: a thousand terms, each shorter than fifty characters, of at most five words.</summary>
    [Test]
    public void The_limits_are_ElevenLabs_own()
    {
        Assert.That(KeytermLimits.ElevenLabs, Is.EqualTo(new KeytermLimits(MaxTerms: 1000, CharacterLimit: 50, MaxWords: 5)));
    }

    /// <summary>
    /// Comments, blank lines, repeated terms (whatever their case) and terms ElevenLabs would reject are left out, each
    /// rejected term with its line and why; the rest are trimmed.
    /// </summary>
    [Test]
    public void Comments_blank_lines_duplicates_and_invalid_terms_are_dropped()
    {
        var read = KeytermRules.Read(
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
        ], Limits);

        Assert.That(read.Terms, Is.EqualTo(new[] { "kalām", "Ashʿarī", "wājib al-wujūd" }));
        Assert.That(read.Skipped.Select(skipped => (skipped.Line, skipped.Term)), Is.EqualTo(new[] { (7, "bad[term]"), (8, "one two three four five six"), (9, new string('x', 50)) }));
        Assert.That(read.Skipped[0].Problem, Is.EqualTo(@"it contains one of the characters < > { } [ ] \"));
        Assert.That(read.OverLimit, Is.False);
    }

    /// <summary>ElevenLabs takes at most a thousand terms, so only the first thousand are kept, and that is said.</summary>
    [Test]
    public void At_most_a_thousand_terms_are_kept()
    {
        var read = KeytermRules.Read(Enumerable.Range(1, 1200).Select(i => $"term{i}"), Limits);

        Assert.That(read.Terms, Has.Count.EqualTo(1000));
        Assert.That(read.Terms[999], Is.EqualTo("term1000"));
        Assert.That(read.OverLimit, Is.True);
    }

    /// <summary>A smaller limit given keeps that many terms: the limits come from the record, not from constants.</summary>
    [Test]
    public void The_limits_given_are_the_ones_used()
    {
        var read = KeytermRules.Read(["one", "two words", "three"], new KeytermLimits(MaxTerms: 2, CharacterLimit: 50, MaxWords: 1));

        Assert.That(read.Terms, Is.EqualTo(new[] { "one", "three" }));
        Assert.That(read.Skipped.Single().Problem, Does.StartWith("it has more than 1 words"));
        Assert.That(read.OverLimit, Is.False);
    }

    /// <summary>A term saved in decomposed form, as some editors and PDFs give it, is kept composed, and counts as the same term as its composed spelling.</summary>
    [Test]
    public void Terms_are_kept_in_composed_form()
    {
        var read = KeytermRules.Read(["kalām", "kalām"], Limits);

        Assert.That(read.Terms, Is.EqualTo(new[] { "kalām" }));
        Assert.That(read.Terms[0].IsNormalized(NormalizationForm.FormC), Is.True);
    }

    /// <summary>Terms of the kind the glossary holds are valid, up to five words counting the parts joined by hyphens.</summary>
    [TestCase("kalām")]
    [TestCase("mukhālafa li-l-ḥawādith")]
    [TestCase("al-Iqtiṣād fī al-Iʿtiqād")]
    [TestCase("al-Iqtisad fi al-Iʿtiqad")]
    public void Ordinary_terms_are_valid(string term)
    {
        Assert.That(KeytermRules.Validate(term, Limits), Is.Null);
    }

    /// <summary>A term must be shorter than fifty characters.</summary>
    [TestCase(49, true)]
    [TestCase(50, false)]
    public void A_term_must_be_shorter_than_fifty_characters(int length, bool valid)
    {
        Assert.That(KeytermRules.Validate(new string('a', length), Limits) is null, Is.EqualTo(valid));
    }

    /// <summary>Terms with a character ElevenLabs forbids, or with more than five words, are invalid.</summary>
    [TestCase("angle <bracket>")]
    [TestCase("curly {brace}")]
    [TestCase("back\\slash")]
    [TestCase("far too many words in here")]
    public void Terms_ElevenLabs_would_reject_are_invalid(string term)
    {
        Assert.That(KeytermRules.Validate(term, Limits), Is.Not.Null);
    }

    /// <summary>
    /// Words are counted the same way whatever joins them, so six words joined by no-break or thin spaces, zero width spaces
    /// (invisible, but text copied from a web page can hold them), en or em dashes, slashes or commas are as many as six
    /// joined by ordinary spaces.
    /// </summary>
    [TestCase("one two three four five six")]
    [TestCase("one two three four five six")]
    [TestCase("one​two​three​four​five​six")]
    [TestCase("one–two–three–four–five–six")]
    [TestCase("one—two—three—four—five—six")]
    [TestCase("a/b/c/d/e/f")]
    [TestCase("one,two,three,four,five,six")]
    public void Six_words_are_too_many_whatever_joins_them(string term)
    {
        Assert.That(KeytermRules.Validate(term, Limits), Does.Contain("more than 5 words"));
        Assert.That(KeytermRules.Words(term), Has.Count.EqualTo(6));
    }

    /// <summary>
    /// Apostrophes (' ’ ‘ and the backtick some plain spellings use) and the letters ʿayn and hamza belong to the word they
    /// are in, so plain spellings such as wa'l-Jama'a keep their word count.
    /// </summary>
    [TestCase("Ahl al-Sunna wa'l-Jama'a")]
    [TestCase("Ahl al-Sunna wa’l-Jamāʿa")]
    [TestCase("Ahl al-Sunna wa‘l-Jama‘a")]
    [TestCase("Ahl al-Sunna wa`l-Jama`a")]
    [TestCase("al-Iqtisad fi al-I'tiqad")]
    [TestCase("jāʾiz taʾwīl baqāʾ ʿaqīda ʿilm")]
    public void Apostrophes_ayn_and_hamza_stay_inside_their_word(string term)
    {
        Assert.That(KeytermRules.Validate(term, Limits), Is.Null);
    }

    /// <summary>
    /// The invisible characters that join or shape letters (the zero width non-joiner and joiner, the soft hyphen and the word
    /// joiner) and marks drawn around a letter belong to the word they are in, so five words with one of them inside the last
    /// are still five.
    /// </summary>
    [TestCase(0x200C, "zero width non-joiner")]
    [TestCase(0x200D, "zero width joiner")]
    [TestCase(0x00AD, "soft hyphen")]
    [TestCase(0x2060, "word joiner")]
    [TestCase(0x20DD, "combining enclosing circle")]
    public void Joiners_soft_hyphens_and_enclosing_marks_stay_inside_their_word(int codePoint, string name)
    {
        var term = $"one two three four fi{char.ConvertFromUtf32(codePoint)}ve";

        Assert.That(KeytermRules.Validate(term, Limits), Is.Null, name);
    }

    /// <summary>The reason given for a term with too many words shows how they were counted, since the parts joined by a hyphen count as words.</summary>
    [Test]
    public void Too_many_words_are_shown_as_they_were_counted()
    {
        Assert.That(KeytermRules.Validate("Ahl al-Sunna wa-l-Jamāʿa", Limits), Does.Contain("Ahl | al | Sunna | wa | l | Jamāʿa"));
    }

    /// <summary>A zero width space cannot be seen in the file, so when one splits a term into too many words, the reason says it is there.</summary>
    [Test]
    public void A_zero_width_space_that_splits_a_term_is_named()
    {
        var term = string.Join(char.ConvertFromUtf32(0x200B), "one", "two", "three", "four", "five", "six");

        Assert.That(KeytermRules.Validate(term, Limits), Does.Contain("zero width space"));
        Assert.That(KeytermRules.Validate("one two three four five six", Limits), Does.Not.Contain("zero width space"));
    }
}
