using System.Security.Cryptography;
using System.Text;
using ClassScribe.Settings;
using ClassScribe.Transcription;

namespace ClassScribe.Tests;

/// <summary>
/// The glossary's files (architecture 8.2): where a value that names a glossary points, from the settings folder alone, and
/// the file read whole with the SHA-256 of its bytes. Every folder is a temporary one; the real user folder is never read.
/// </summary>
[TestFixture]
public class GlossaryFilesTests
{
    private TempFolder _program = null!;
    private TempFolder _user = null!;

    /// <summary>Creates a folder for the program and one for the user's settings.</summary>
    [SetUp]
    public void SetUp()
    {
        _program = new TempFolder();
        _user = new TempFolder();
    }

    /// <summary>Deletes the folders.</summary>
    [TearDown]
    public void TearDown()
    {
        _user.Dispose();
        _program.Dispose();
    }

    /// <summary>The glossary's files with the settings read beside the program, or from the user's folder once it holds a settings file.</summary>
    private GlossaryFiles Files(bool userFile = false)
    {
        if (userFile) File.WriteAllText(_user.File("appsettings.json"), "{}");
        return new GlossaryFiles(new SettingsPlaces(_program.Path, _user.Path));
    }

    /// <summary>
    /// A relative name is taken from the settings folder in force and from nowhere else: beside the program while its file is
    /// read, from the user's folder once the user's file is, and never from beside the program then, where an older build's
    /// keyterms.txt may be left over.
    /// </summary>
    [Test]
    public void A_relative_name_is_taken_from_the_settings_folder_alone()
    {
        File.WriteAllText(_program.File("keyterms.txt"), "left over");

        Assert.That(Files().Resolve("keyterms.txt"), Is.EqualTo(_program.File("keyterms.txt")), "the program's settings are read, so its folder is the settings folder");
        Assert.That(Files(userFile: true).Resolve("keyterms.txt"), Is.EqualTo(_user.File("keyterms.txt")), "the user's settings are read: never beside the program");
    }

    /// <summary>Environment variables, such as %APPDATA%, are expanded, quotes and spaces around the value ignored, and an empty value names no file.</summary>
    [Test]
    public void Variables_are_expanded_quotes_ignored_and_empty_names_nothing()
    {
        using var variable = new TemporaryVariable("CLASSSCRIBE_TEST_GLOSSARY", _user.Path);
        var files = Files();

        Assert.That(files.Resolve(@"%CLASSSCRIBE_TEST_GLOSSARY%\terms.txt"), Is.EqualTo(_user.File("terms.txt")));
        Assert.That(files.Resolve(@" ""%CLASSSCRIBE_TEST_GLOSSARY%\terms.txt"" "), Is.EqualTo(_user.File("terms.txt")));
        Assert.That(files.Resolve("  "), Is.Empty);
    }

    /// <summary>
    /// A glossary is read whole: its lines as UTF-8, a byte-order mark passed over, and the SHA-256 of its bytes, in lowercase
    /// hexadecimal, which tells a changed file from the same one.
    /// </summary>
    [Test]
    public void A_glossary_is_read_with_its_lines_and_the_hash_of_its_bytes()
    {
        var path = _user.File("mantiq.txt");
        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("# Mantiq\r\nkalām\nmanṭiq\r\n")).ToArray();
        File.WriteAllBytes(path, bytes);

        var read = Files().Read(path)!;

        Assert.That(read.Path, Is.EqualTo(path));
        Assert.That(read.Lines, Is.EqualTo(new[] { "# Mantiq", "kalām", "manṭiq" }));
        Assert.That(read.Sha256, Is.EqualTo(Convert.ToHexStringLower(SHA256.HashData(bytes))));
        File.AppendAllText(path, "tawḥīd");
        Assert.That(Files().Read(path)!.Sha256, Is.Not.EqualTo(read.Sha256), "a changed file has another hash");
    }

    /// <summary>A glossary that is not there is none; so is an empty path, which names no file.</summary>
    [Test]
    public void A_glossary_that_is_not_there_is_none()
    {
        Assert.That(Files().Read(_user.File("gone.txt")), Is.Null);
        Assert.That(Files().Read(""), Is.Null);
    }

    /// <summary>A glossary held by another program cannot be read, and says so, for the resolver to warn about.</summary>
    [Test]
    public void A_glossary_that_cannot_be_read_throws()
    {
        var path = _user.File("held.txt");
        File.WriteAllText(path, "kalām");
        using var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);

        Assert.Throws<IOException>(() => Files().Read(path));
    }

    /// <summary>The example glossary in the repository, examples\glossary, as the test project alone copies it to its output.</summary>
    private static string Example => Path.Combine(AppContext.BaseDirectory, "Examples", "keyterms-islamic-sciences.txt");

    /// <summary>Every term of the example glossary, Gulzar's, is valid, so none is skipped with a warning.</summary>
    [Test]
    public void The_example_glossary_is_entirely_valid()
    {
        var read = Files().Read(Example)!;
        var listed = read.Lines.Select(line => line.Trim()).Count(line => line.Length > 0 && !line.StartsWith('#'));

        var terms = KeytermRules.Read(read.Lines, KeytermLimits.ElevenLabs);

        Assert.That(terms.Terms, Has.Count.EqualTo(listed));
        Assert.That(terms.Terms, Is.Not.Empty);
        Assert.That(terms.Skipped, Is.Empty);
    }

    /// <summary>The example glossary is UTF-8 without a byte order mark, in composed (NFC) form, as ElevenLabs gets it.</summary>
    [Test]
    public void The_example_glossary_is_utf8_without_a_byte_order_mark_and_composed()
    {
        var bytes = File.ReadAllBytes(Example);
        var text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes);

        Assert.That(bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble), Is.False);
        Assert.That(text.IsNormalized(NormalizationForm.FormC), Is.True);
    }
}
