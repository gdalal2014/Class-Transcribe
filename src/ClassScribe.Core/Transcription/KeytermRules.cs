using System.Globalization;
using System.Text;

namespace ClassScribe.Transcription;

/// <summary>
/// What one request's glossary may hold (architecture 7.2 and 8.2): at most <paramref name="MaxTerms"/> terms, each shorter
/// than <paramref name="CharacterLimit"/> characters and of at most <paramref name="MaxWords"/> words. The run's settings give
/// them (ElevenLabs:MaxKeyterms, KeytermCharacterLimit and MaxKeytermWords), ElevenLabs' own (<see cref="ElevenLabs"/>) by
/// default; the rules never read settings themselves.
/// </summary>
/// <param name="MaxTerms">The most terms one request takes.</param>
/// <param name="CharacterLimit">A term must be shorter than this many characters.</param>
/// <param name="MaxWords">The most words in one term.</param>
public sealed record KeytermLimits(int MaxTerms, int CharacterLimit, int MaxWords)
{
    /// <summary>
    /// ElevenLabs' documented limits: a thousand terms, each shorter than fifty characters, of at most five words. The
    /// settings' defaults, and what a limit set above them is warned against.
    /// </summary>
    public static KeytermLimits ElevenLabs { get; } = new(1000, 50, 5);
}

/// <summary>A line of a glossary that was left out, because ElevenLabs would refuse the term on it.</summary>
/// <param name="Line">The line's number, from 1.</param>
/// <param name="Term">The term as it stands on the line, trimmed and composed.</param>
/// <param name="Problem">Why it was left out, in words.</param>
public sealed record SkippedKeyterm(int Line, string Term, string Problem);

/// <summary>What a glossary's lines give.</summary>
/// <param name="Terms">The valid, distinct terms, in their order, composed (NFC), at most the limit's number.</param>
/// <param name="Skipped">The lines left out because ElevenLabs would refuse their terms, in their order.</param>
/// <param name="OverLimit">True when there were more valid terms than the limit, so that the rest were left out.</param>
public sealed record KeytermList(IReadOnlyList<string> Terms, IReadOnlyList<SkippedKeyterm> Skipped, bool OverLimit);

/// <summary>
/// The rules of a glossary's terms, as a pure function of its lines: one term per line, blank lines and lines starting with #
/// passed over, each term trimmed and composed (NFC), a term ElevenLabs would refuse left out with its reason rather than
/// failing the upload, and a term repeated in any case kept once. Nothing is read or logged here: the caller reads the file
/// and says what was left out.
/// </summary>
public static class KeytermRules
{
    /// <summary>The apostrophes plain spellings use for ʿayn and hamza, as in wa'l-Jama'a. They belong to the word they are in.</summary>
    private const string Apostrophes = "'’‘`";

    /// <summary>
    /// U+200B ZERO WIDTH SPACE. Unicode files it with the invisible format characters, but it is a space
    /// between words, unlike the joiners and the soft hyphen.
    /// </summary>
    private const int ZeroWidthSpace = 0x200B;

    /// <summary>The characters ElevenLabs refuses in a term.</summary>
    private static readonly char[] ForbiddenCharacters = ['<', '>', '{', '}', '[', ']', '\\'];

    /// <summary>
    /// The terms <paramref name="lines"/> give under <paramref name="limits"/>: the valid, distinct ones in their order, the
    /// lines left out with why, and whether there were more valid terms than the limit takes.
    /// </summary>
    public static KeytermList Read(IEnumerable<string> lines, KeytermLimits limits)
    {
        var terms = new List<string>();
        var skipped = new List<SkippedKeyterm>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var number = 0;
        foreach (var line in lines)
        {
            number++;
            var term = line.Trim().Normalize(NormalizationForm.FormC);
            if (term.Length == 0 || term.StartsWith('#')) continue;

            if (Validate(term, limits) is { } problem)
            {
                skipped.Add(new SkippedKeyterm(number, term, problem));
                continue;
            }

            if (!seen.Add(term)) continue;
            if (terms.Count == limits.MaxTerms) return new KeytermList(terms, skipped, OverLimit: true);

            terms.Add(term);
        }

        return new KeytermList(terms, skipped, OverLimit: false);
    }

    /// <summary>Returns what is wrong with a term under <paramref name="limits"/>, or null when ElevenLabs will accept it.</summary>
    public static string? Validate(string term, KeytermLimits limits)
    {
        if (term.Length >= limits.CharacterLimit)
            return $"it must be shorter than {limits.CharacterLimit} characters";

        if (term.IndexOfAny(ForbiddenCharacters) >= 0)
            return "it contains one of the characters < > { } [ ] \\";

        var words = Words(term);
        if (words.Count > limits.MaxWords)
        {
            // It cannot be seen in the file, so without this the split would make no sense.
            var unseen = term.Contains((char)ZeroWidthSpace) ? ", and so does the invisible zero width space in it" : "";
            return $"it has more than {limits.MaxWords} words ({string.Join(" | ", words)}); a hyphen, dash, slash or other mark between letters starts a new word{unseen}";
        }

        return null;
    }

    /// <summary>
    /// Splits a term into words, counting as many as ElevenLabs might, since a term it finds too long fails the whole request.
    /// A word is a run of letters, digits, combining marks, apostrophes and invisible format characters such as joiners, so
    /// ʿ, ʾ and ' stay inside it; anything else (a space of any kind, the invisible zero width space included, a hyphen or
    /// other dash, a slash, a comma) separates words.
    /// </summary>
    public static IReadOnlyList<string> Words(string term)
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
    /// True for a character that belongs inside a word: a letter (ʿayn and hamza included), a digit, a combining mark, an
    /// apostrophe, or an invisible format character such as the zero width joiner and non-joiner, the soft hyphen or the word
    /// joiner. The zero width space is not one: it separates words. That follows Unicode's rules for word boundaries, which
    /// ignore format characters inside a word but break at a zero width space (https://www.unicode.org/reports/tr29/#Word_Boundaries).
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
