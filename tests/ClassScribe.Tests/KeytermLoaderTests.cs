using System.Text;
using ClassScribe.Settings;
using ClassScribe.Transcription;

namespace ClassScribe.Tests;

/// <summary>Reading and validating the glossary file, and finding it: beside the program as before, or in the user's own folder first.</summary>
[TestFixture]
public class KeytermLoaderTests
{
    private TempFolder _folder = null!;
    private RecordingLogger<KeytermLoader> _log = null!;
    private KeytermLoader _loader = null!;

    /// <summary>
    /// Creates a temporary folder and the loader under test, reading beside the program with no user folder, as before,
    /// with a logger that keeps what it is told.
    /// </summary>
    [SetUp]
    public void SetUp()
    {
        _folder = new TempFolder();
        _log = new RecordingLogger<KeytermLoader>();
        _loader = new KeytermLoader(new SettingsPlaces(AppContext.BaseDirectory, userFolder: null), _log);
    }

    /// <summary>Deletes the temporary folder. That fails if a test left a file open, which is the point.</summary>
    [TearDown]
    public void TearDown() => _folder.Dispose();

    /// <summary>Comments, blank lines, repeated terms (whatever their case) and terms ElevenLabs would reject are left out; the rest are trimmed.</summary>
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

    /// <summary>
    /// A glossary file that is not there gives no terms, with a warning that names the path: the file
    /// ships with the app, so its absence means the transcript is made without the Arabic terms.
    /// </summary>
    [Test]
    public void A_missing_file_means_no_glossary_and_a_warning()
    {
        var path = _folder.File("nope.txt");

        Assert.That(_loader.Load(path), Is.Empty);
        Assert.That(_log.Problems, Has.Count.EqualTo(1));
        Assert.That(_log.Problems[0], Does.Contain(path));
    }

    /// <summary>An empty KeytermsFile setting turns the glossary off on purpose, so it is not warned about.</summary>
    [Test]
    public void No_glossary_set_means_none_without_a_warning()
    {
        Assert.That(_loader.Load(""), Is.Empty);
        Assert.That(_log.Problems, Is.Empty);
    }

    /// <summary>ElevenLabs takes at most a thousand terms, so only the first thousand are sent.</summary>
    [Test]
    public void At_most_a_thousand_terms_are_sent()
    {
        var path = _folder.File("many.txt");
        File.WriteAllLines(path, Enumerable.Range(1, 1200).Select(i => $"term{i}"));

        var terms = _loader.Load(path);

        Assert.That(terms, Has.Count.EqualTo(1000));
        Assert.That(terms[999], Is.EqualTo("term1000"));
    }

    /// <summary>A term saved in decomposed form, as some editors and PDFs give it, is sent composed, and counts as the same term as its composed spelling.</summary>
    [Test]
    public void Terms_are_sent_in_composed_form()
    {
        var path = _folder.File("keyterms.txt");
        File.WriteAllLines(path, ["kalām", "kalām"], Encoding.UTF8);

        Assert.That(_loader.Load(path), Is.EqualTo(new[] { "kalām" }));
    }

    /// <summary>Terms of the kind the glossary holds are valid, up to five words counting the parts joined by hyphens.</summary>
    [TestCase("kalām")]
    [TestCase("mukhālafa li-l-ḥawādith")]
    [TestCase("al-Iqtiṣād fī al-Iʿtiqād")]
    [TestCase("al-Iqtisad fi al-Iʿtiqad")]
    public void Ordinary_terms_are_valid(string term)
    {
        Assert.That(KeytermLoader.Validate(term), Is.Null);
    }

    /// <summary>A term must be shorter than fifty characters.</summary>
    [TestCase(49, true)]
    [TestCase(50, false)]
    public void A_term_must_be_shorter_than_fifty_characters(int length, bool valid)
    {
        Assert.That(KeytermLoader.Validate(new string('a', length)) is null, Is.EqualTo(valid));
    }

    /// <summary>Terms with a character ElevenLabs forbids, or with more than five words, are invalid.</summary>
    [TestCase("angle <bracket>")]
    [TestCase("curly {brace}")]
    [TestCase("back\\slash")]
    [TestCase("far too many words in here")]
    public void Terms_ElevenLabs_would_reject_are_invalid(string term)
    {
        Assert.That(KeytermLoader.Validate(term), Is.Not.Null);
    }

    /// <summary>
    /// Words are counted the same way whatever joins them, so six words joined by no-break or thin
    /// spaces, zero width spaces (invisible, but text copied from a web page can hold them), en or em
    /// dashes, slashes or commas are as many as six joined by ordinary spaces.
    /// </summary>
    [TestCase("one two three four five six")]
    [TestCase("one two three four five six")]
    [TestCase("one\u200Btwo\u200Bthree\u200Bfour\u200Bfive\u200Bsix")]
    [TestCase("one–two–three–four–five–six")]
    [TestCase("one—two—three—four—five—six")]
    [TestCase("a/b/c/d/e/f")]
    [TestCase("one,two,three,four,five,six")]
    public void Six_words_are_too_many_whatever_joins_them(string term)
    {
        Assert.That(KeytermLoader.Validate(term), Does.Contain("more than 5 words"));
    }

    /// <summary>
    /// Apostrophes (' ’ ‘ and the backtick some plain spellings use) and the letters ʿayn and hamza
    /// belong to the word they are in, so plain spellings such as wa'l-Jama'a keep their word count.
    /// </summary>
    [TestCase("Ahl al-Sunna wa'l-Jama'a")]
    [TestCase("Ahl al-Sunna wa’l-Jamāʿa")]
    [TestCase("Ahl al-Sunna wa‘l-Jama‘a")]
    [TestCase("Ahl al-Sunna wa`l-Jama`a")]
    [TestCase("al-Iqtisad fi al-I'tiqad")]
    [TestCase("jāʾiz taʾwīl baqāʾ ʿaqīda ʿilm")]
    public void Apostrophes_ayn_and_hamza_stay_inside_their_word(string term)
    {
        Assert.That(KeytermLoader.Validate(term), Is.Null);
    }

    /// <summary>
    /// The invisible characters that join or shape letters (the zero width non-joiner and joiner, the
    /// soft hyphen and the word joiner) and marks drawn around a letter belong to the word they are in,
    /// so five words with one of them inside the last are still five.
    /// </summary>
    [TestCase(0x200C, "zero width non-joiner")]
    [TestCase(0x200D, "zero width joiner")]
    [TestCase(0x00AD, "soft hyphen")]
    [TestCase(0x2060, "word joiner")]
    [TestCase(0x20DD, "combining enclosing circle")]
    public void Joiners_soft_hyphens_and_enclosing_marks_stay_inside_their_word(int codePoint, string name)
    {
        var term = $"one two three four fi{char.ConvertFromUtf32(codePoint)}ve";

        Assert.That(KeytermLoader.Validate(term), Is.Null, name);
    }

    /// <summary>The reason given for a term with too many words shows how they were counted, since the parts joined by a hyphen count as words.</summary>
    [Test]
    public void Too_many_words_are_shown_as_they_were_counted()
    {
        Assert.That(KeytermLoader.Validate("Ahl al-Sunna wa-l-Jamāʿa"), Does.Contain("Ahl | al | Sunna | wa | l | Jamāʿa"));
    }

    /// <summary>A zero width space cannot be seen in the file, so when one splits a term into too many words, the reason says it is there.</summary>
    [Test]
    public void A_zero_width_space_that_splits_a_term_is_named()
    {
        var term = string.Join(char.ConvertFromUtf32(0x200B), "one", "two", "three", "four", "five", "six");

        Assert.That(KeytermLoader.Validate(term), Does.Contain("zero width space"));
        Assert.That(KeytermLoader.Validate("one two three four five six"), Does.Not.Contain("zero width space"));
    }

    /// <summary>A relative glossary path is found next to the app, whatever folder the app was started from, when the settings are read there.</summary>
    [Test]
    public void A_relative_glossary_path_is_found_next_to_the_app()
    {
        Assert.That(_loader.ResolvePath("keyterms.txt"), Is.EqualTo(Path.Combine(AppContext.BaseDirectory, "keyterms.txt")));
    }

    /// <summary>Environment variables in the glossary path, such as %USERPROFILE%, are expanded, and quotes around it are ignored.</summary>
    [Test]
    public void Environment_variables_in_the_glossary_path_are_expanded()
    {
        Environment.SetEnvironmentVariable("CLASSSCRIBE_TEST_GLOSSARY", _folder.Path);
        try
        {
            Assert.That(_loader.ResolvePath(@"%CLASSSCRIBE_TEST_GLOSSARY%\terms.txt"), Is.EqualTo(_folder.File("terms.txt")));
            Assert.That(_loader.ResolvePath(@" ""%CLASSSCRIBE_TEST_GLOSSARY%\terms.txt"" "), Is.EqualTo(_folder.File("terms.txt")));
        }
        finally
        {
            Environment.SetEnvironmentVariable("CLASSSCRIBE_TEST_GLOSSARY", null);
        }
    }

    /// <summary>An empty glossary setting resolves to no path, which the loader reads as no glossary.</summary>
    [Test]
    public void An_empty_glossary_setting_resolves_to_no_path()
    {
        Assert.That(_loader.ResolvePath("  "), Is.Empty);
    }

    /// <summary>
    /// With the user's own settings read, a relative glossary name is taken from the user's folder first and beside the
    /// program second, and when it is in neither place the warning names both, and says where to put the file.
    /// </summary>
    [Test]
    public void With_the_users_settings_read_the_glossary_is_looked_for_in_the_users_folder_first()
    {
        using var program = new TempFolder();
        File.WriteAllText(_folder.File("appsettings.json"), "{}");
        var loader = new KeytermLoader(new SettingsPlaces(program.Path, _folder.Path), _log);

        Assert.That(loader.ResolvePath("keyterms.txt"), Is.EqualTo(program.File("keyterms.txt")), "not in the user's folder, so beside the program");
        Assert.That(loader.Load(loader.ResolvePath("keyterms.txt")), Is.Empty);
        Assert.That(_log.Problems, Is.EqualTo(new[]
        {
            $"No glossary at {_folder.File("keyterms.txt")} or beside ClassScribe.exe, so the transcript is made without its terms. Put keyterms.txt in the first folder, or set ElevenLabs:KeytermsFile to where it is"
        }));

        File.WriteAllLines(program.File("keyterms.txt"), ["shipped"], Encoding.UTF8);
        Assert.That(loader.Load(loader.ResolvePath("keyterms.txt")), Is.EqualTo(new[] { "shipped" }), "the shipped glossary, second");

        File.WriteAllLines(_folder.File("keyterms.txt"), ["kalām"], Encoding.UTF8);
        Assert.That(loader.ResolvePath("keyterms.txt"), Is.EqualTo(_folder.File("keyterms.txt")));
        Assert.That(loader.Load(loader.ResolvePath("keyterms.txt")), Is.EqualTo(new[] { "kalām" }), "the user's own, first");
    }

    /// <summary>A glossary named by a path of its own that is not there is warned about as before, by that path alone, whichever settings file was read.</summary>
    [Test]
    public void A_missing_glossary_elsewhere_is_named_by_its_path()
    {
        using var program = new TempFolder();
        File.WriteAllText(_folder.File("appsettings.json"), "{}");
        var loader = new KeytermLoader(new SettingsPlaces(program.Path, _folder.Path), _log);
        var elsewhere = _folder.File(Path.Combine("Glossaries", "terms.txt"));

        Assert.That(loader.Load(loader.ResolvePath(elsewhere)), Is.Empty);

        Assert.That(_log.Problems, Has.Count.EqualTo(1));
        Assert.That(_log.Problems[0], Does.StartWith($"No glossary at {elsewhere}, so").And.Not.Contain("or beside"));
    }

    /// <summary>Every term in the glossary that ships with the app is valid, so none is skipped with a warning.</summary>
    [Test]
    public void The_shipped_starter_glossary_is_entirely_valid()
    {
        var shipped = Path.Combine(AppContext.BaseDirectory, "keyterms.txt");
        var listed = File.ReadLines(shipped).Select(l => l.Trim()).Count(l => l.Length > 0 && !l.StartsWith('#'));

        var terms = _loader.Load(shipped);

        Assert.That(terms, Has.Count.EqualTo(listed));
        Assert.That(terms, Is.Not.Empty);
        Assert.That(_log.Problems, Is.Empty);
    }

    /// <summary>The glossary that ships with the app is UTF-8 without a byte order mark, in composed (NFC) form, as ElevenLabs gets it.</summary>
    [Test]
    public void The_shipped_starter_glossary_is_utf8_without_a_byte_order_mark_and_composed()
    {
        var bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "keyterms.txt"));
        var text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes);

        Assert.That(bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble), Is.False);
        Assert.That(text.IsNormalized(NormalizationForm.FormC), Is.True);
    }
}
