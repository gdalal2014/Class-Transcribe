using System.Text;
using Microsoft.Extensions.Logging;

namespace ClassScribe.Transcription;

/// <summary>
/// Reads the glossary file: one term per line, blank lines and lines starting with # ignored.
/// Terms that break ElevenLabs' rules are skipped with a warning instead of failing the upload.
/// </summary>
public sealed class KeytermLoader(ILogger<KeytermLoader> logger)
{
    /// <summary>Most terms ElevenLabs accepts in one request.</summary>
    public const int MaxTerms = 1000;

    /// <summary>A term must be shorter than this many characters.</summary>
    public const int MaxLength = 50;

    /// <summary>Most words allowed in one term.</summary>
    public const int MaxWords = 5;

    private static readonly char[] ForbiddenCharacters = ['<', '>', '{', '}', '[', ']', '\\'];
    private static readonly char[] WordSeparators = [' ', '\t', '-'];

    /// <summary>Loads the valid, distinct terms from <paramref name="path"/>. A missing file gives an empty list.</summary>
    public IReadOnlyList<string> Load(string path)
    {
        if (!File.Exists(path))
        {
            logger.LogInformation("No keyterms file at {Path}; transcribing without a glossary", path);
            return [];
        }

        var candidates = File.ReadLines(path, Encoding.UTF8)
            .Select((line, index) => (Term: line.Trim(), Line: index + 1))
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

        if (term.Split(WordSeparators, StringSplitOptions.RemoveEmptyEntries).Length > MaxWords)
            return $"it has more than {MaxWords} words";

        return null;
    }
}
