using System.Globalization;
using System.Text;
using ClassScribe.Settings;
using Microsoft.Extensions.Logging;

namespace ClassScribe.Transcription;

/// <summary>
/// Reads the glossary file: one term per line, blank lines and lines starting with # ignored.
/// Terms that break ElevenLabs' rules are skipped with a warning instead of failing the upload.
/// The file is looked for where the settings were read from first, then beside the program (see <see cref="ResolvePath"/>).
/// </summary>
public sealed class KeytermLoader(SettingsPlaces places, ILogger<KeytermLoader> logger)
{
    /// <summary>Most terms ElevenLabs accepts in one request.</summary>
    public const int MaxTerms = 1000;

    /// <summary>A term must be shorter than this many characters.</summary>
    public const int MaxLength = 50;

    /// <summary>Most words allowed in one term.</summary>
    public const int MaxWords = 5;

    /// <summary>The apostrophes plain spellings use for ʿayn and hamza, as in wa'l-Jama'a. They belong to the word they are in.</summary>
    private const string Apostrophes = "'’‘`";

    /// <summary>
    /// U+200B ZERO WIDTH SPACE. Unicode files it with the invisible format characters, but it is a space
    /// between words, unlike the joiners and the soft hyphen.
    /// </summary>
    private const int ZeroWidthSpace = 0x200B;

    private static readonly char[] ForbiddenCharacters = ['<', '>', '{', '}', '[', ']', '\\'];

    /// <summary>
    /// Where the configured glossary file is: environment variables such as %USERPROFILE% expanded, quotes around the
    /// path ignored, and a relative path taken from the folder the settings were read from when the file is there, else
    /// from the program's own folder: the user's own glossary first, the shipped one second (see
    /// <see cref="SettingsPlaces.ResolveFile"/>). An empty setting gives an empty path, which <see cref="Load"/> reads as
    /// no glossary.
    /// </summary>
    public string ResolvePath(string configured) => places.ResolveFile(configured);

    /// <summary>
    /// Loads the valid, distinct terms from <paramref name="path"/>, in composed (NFC) form. A missing file gives an
    /// empty list and a warning, which names both places when the user's own folder was looked in first; an empty path
    /// means no glossary was wanted.
    /// </summary>
    public IReadOnlyList<string> Load(string path)
    {
        if (path.Length == 0)
        {
            logger.LogInformation("No glossary is set in ElevenLabs:KeytermsFile; transcribing without one");
            return [];
        }

        if (!File.Exists(path))
        {
            if (places.Origin == SettingsOrigin.UserFolder && string.Equals(Path.GetDirectoryName(path), places.ProgramFolder, StringComparison.OrdinalIgnoreCase))
            {
                var name = Path.GetFileName(path);
                logger.LogWarning(
                    "No glossary at {UserPath} or beside ClassScribe.exe, so the transcript is made without its terms. Put {Name} in the first folder, or set ElevenLabs:KeytermsFile to where it is",
                    Path.Combine(places.UserFolder!, name), name);
            }
            else
            {
                logger.LogWarning(
                    "No glossary at {Path}, so the transcript is made without its terms. Keep keyterms.txt next to ClassScribe.exe, or set ElevenLabs:KeytermsFile to where it is",
                    path);
            }

            return [];
        }

        var candidates = File.ReadLines(path, Encoding.UTF8)
            .Select((line, index) => (Term: line.Trim().Normalize(NormalizationForm.FormC), Line: index + 1))
            .Where(x => x.Term.Length > 0 && !x.Term.StartsWith('#'));

        var terms = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (term, line) in candidates)
        {
            if (Validate(term) is { } problem)
            {
                logger.LogWarning("Skipping keyterm on line {Line} ({Term}): {Problem}", line, term, problem);
                continue;
            }

            if (!seen.Add(term)) continue;

            if (terms.Count == MaxTerms)
            {
                logger.LogWarning("The keyterms file has more than {MaxTerms} terms; the rest are ignored", MaxTerms);
                break;
            }

            terms.Add(term);
        }

        return terms;
    }

    /// <summary>Returns what is wrong with a term, or null when ElevenLabs will accept it.</summary>
    public static string? Validate(string term)
    {
        if (term.Length >= MaxLength)
            return $"it must be shorter than {MaxLength} characters";

        if (term.IndexOfAny(ForbiddenCharacters) >= 0)
            return "it contains one of the characters < > { } [ ] \\";

        var words = Words(term);
        if (words.Count > MaxWords)
        {
            // It cannot be seen in the file, so without this the split would make no sense.
            var unseen = term.Contains((char)ZeroWidthSpace) ? ", and so does the invisible zero width space in it" : "";
            return $"it has more than {MaxWords} words ({string.Join(" | ", words)}); a hyphen, dash, slash or other mark between letters starts a new word{unseen}";
        }

        return null;
    }

    /// <summary>
    /// Splits a term into words, counting as many as ElevenLabs might, since a term it finds too long
    /// fails the whole request. A word is a run of letters, digits, combining marks, apostrophes and
    /// invisible format characters such as joiners, so ʿ, ʾ and ' stay inside it; anything else (a
    /// space of any kind, the invisible zero width space included, a hyphen or other dash, a slash, a
    /// comma) separates words.
    /// </summary>
    private static List<string> Words(string term)
    {
        var words = new List<string>();
        var word = new StringBuilder();
        foreach (var rune in term.EnumerateRunes())
        {
            if (IsWordPart(rune))
            {
                word.Append(rune.ToString());
            }
            else if (word.Length > 0)
            {
                words.Add(word.ToString());
                word.Clear();
            }
        }

        if (word.Length > 0) words.Add(word.ToString());
        return words;
    }

    /// <summary>
    /// True for a character that belongs inside a word: a letter (ʿayn and hamza included), a digit, a
    /// combining mark, an apostrophe, or an invisible format character such as the zero width joiner and
    /// non-joiner, the soft hyphen or the word joiner. The zero width space is not one: it separates
    /// words. That follows Unicode's rules for word boundaries, which ignore format characters inside a
    /// word but break at a zero width space (https://www.unicode.org/reports/tr29/#Word_Boundaries).
    /// </summary>
    private static bool IsWordPart(Rune rune) =>
        Rune.IsLetterOrDigit(rune)
        || Rune.GetUnicodeCategory(rune) switch
        {
            UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark => true,
            UnicodeCategory.Format => rune.Value != ZeroWidthSpace,
            _ => false
        }
        || (rune.IsBmp && Apostrophes.Contains((char)rune.Value));
}
