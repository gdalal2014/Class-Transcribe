using System.Text;
using ClassScribe.Settings;
using Microsoft.Extensions.Options;

namespace ClassScribe.Transcription;

/// <summary>
/// Lays a transcript out as readable paragraphs. A new paragraph starts after a long pause, or at
/// a sentence end once the paragraph has grown long. Each paragraph can carry its position in the recording.
/// </summary>
public sealed class TranscriptFormatter(IOptions<TranscriptOptions> options)
{
    private const string SpacingType = "spacing";

    /// <summary>Formats the transcript as plain text.</summary>
    public string Format(TranscriptionResult result)
    {
        var settings = options.Value;
        if (!result.Words.Any(w => w.Type != SpacingType && w.Start is not null))
            return result.Text.Trim() + Environment.NewLine;

        var paragraphs = new List<(double Start, StringBuilder Text)>();
        double? lastEnd = null;
        foreach (var word in result.Words)
        {
            var isSpacing = word.Type == SpacingType;
            if (!isSpacing && StartsNewParagraph(paragraphs, word, lastEnd, settings))
                paragraphs.Add((word.Start ?? lastEnd ?? 0, new StringBuilder()));

            if (paragraphs.Count == 0) continue;

            paragraphs[^1].Text.Append(word.Text);
            if (!isSpacing && word.End is { } end) lastEnd = end;
        }

        var lines = paragraphs
            .Select(p => (p.Start, Text: p.Text.ToString().Trim()))
            .Where(p => p.Text.Length > 0)
            .Select(p => settings.IncludeTimestamps ? $"[{FormatTime(p.Start)}] {p.Text}" : p.Text);

        return string.Join(Environment.NewLine + Environment.NewLine, lines) + Environment.NewLine;
    }

    /// <summary>Decides whether the word should open a new paragraph.</summary>
    private static bool StartsNewParagraph(List<(double Start, StringBuilder Text)> paragraphs, TranscriptWord word, double? lastEnd, TranscriptOptions settings)
    {
        if (paragraphs.Count == 0) return true;

        var pause = word.Start is { } start && lastEnd is { } end ? start - end : 0;
        if (pause >= settings.ParagraphPauseSeconds) return true;

        var current = paragraphs[^1].Text;
        return current.Length >= settings.MaxParagraphCharacters && EndsSentence(current);
    }

    /// <summary>True when the text so far ends with sentence punctuation, ignoring trailing spaces.</summary>
    private static bool EndsSentence(StringBuilder text)
    {
        for (var i = text.Length - 1; i >= 0; i--)
        {
            if (char.IsWhiteSpace(text[i])) continue;
            return text[i] is '.' or '?' or '!';
        }

        return false;
    }

    /// <summary>Formats seconds as hh:mm:ss.</summary>
    private static string FormatTime(double seconds) => TimeSpan.FromSeconds(Math.Max(0, seconds)).ToString(@"hh\:mm\:ss");
}
