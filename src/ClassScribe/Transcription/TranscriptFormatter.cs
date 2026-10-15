using System.Globalization;
using System.Text;
using ClassScribe.Settings;
using ClassScribe.Storage;
using Microsoft.Extensions.Options;

namespace ClassScribe.Transcription;

/// <summary>
/// Lays a transcript out as readable paragraphs. A new paragraph starts after a long pause, or at a
/// sentence end once the paragraph has grown long. Marks set while recording become lines of their own
/// at their position. Each paragraph can carry its position in the recording.
/// </summary>
public sealed class TranscriptFormatter(IOptions<TranscriptOptions> options)
{
    private const string SpacingType = "spacing";

    private static readonly string Blank = Environment.NewLine + Environment.NewLine;

    /// <summary>Formats the transcript as plain text, with the class's marks, if any, at their positions.</summary>
    public string Format(TranscriptionResult result, IReadOnlyList<ClassMark>? marks = null)
    {
        var settings = options.Value;
        var pending = new Queue<ClassMark>((marks ?? []).OrderBy(mark => mark.Position));

        if (!result.Words.Any(w => w.Type != SpacingType && w.Start is not null))
        {
            // Without word timings a mark cannot be placed, so the marks follow the text.
            var parts = pending
                .Select(mark => Line(mark.Position.TotalSeconds, MarkText(mark), settings))
                .Prepend(result.Text.Trim())
                .Where(part => part.Length > 0);
            return string.Join(Blank, parts) + Environment.NewLine;
        }

        var paragraphs = new List<Paragraph>();
        double? lastEnd = null;
        foreach (var word in result.Words)
        {
            var isSpacing = word.Type == SpacingType;
            if (!isSpacing && word.Start is { } start)
            {
                while (pending.TryPeek(out var mark) && mark.Position.TotalSeconds <= start)
                    paragraphs.Add(MarkParagraph(pending.Dequeue()));
            }

            if (!isSpacing && StartsNewParagraph(paragraphs, word, lastEnd, settings))
                paragraphs.Add(new Paragraph(word.Start ?? lastEnd ?? 0, new StringBuilder(), IsMark: false));

            // Spacing before the first word, or right after a mark, belongs to no paragraph.
            if (paragraphs.Count == 0 || paragraphs[^1].IsMark) continue;

            paragraphs[^1].Text.Append(word.Text);
            if (!isSpacing && word.End is { } end) lastEnd = end;
        }

        // Marks set after the last word, during silence at the end, close the transcript.
        while (pending.TryDequeue(out var mark)) paragraphs.Add(MarkParagraph(mark));

        var lines = paragraphs
            .Select(p => (p.Start, Text: p.Text.ToString().Trim()))
            .Where(p => p.Text.Length > 0)
            .Select(p => Line(p.Start, p.Text, settings));

        return string.Join(Blank, lines) + Environment.NewLine;
    }

    /// <summary>Decides whether the word should open a new paragraph. The first word, and the first after a mark, always do.</summary>
    private static bool StartsNewParagraph(List<Paragraph> paragraphs, TranscriptWord word, double? lastEnd, TranscriptOptions settings)
    {
        if (paragraphs.Count == 0 || paragraphs[^1].IsMark) return true;

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

    /// <summary>A paragraph that holds only a mark.</summary>
    private static Paragraph MarkParagraph(ClassMark mark) => new(mark.Position.TotalSeconds, new StringBuilder(MarkText(mark)), IsMark: true);

    /// <summary>The text a mark shows: its number, and its label when one was typed.</summary>
    private static string MarkText(ClassMark mark) =>
        mark.Label is { Length: > 0 } label ? $"*** Mark {mark.Number}: {label} ***" : $"*** Mark {mark.Number} ***";

    /// <summary>A paragraph's line, starting with its position when timestamps are on.</summary>
    private static string Line(double start, string text, TranscriptOptions settings) =>
        settings.IncludeTimestamps ? $"[{FormatTime(start)}] {text}" : text;

    /// <summary>Formats seconds as hh:mm:ss.</summary>
    private static string FormatTime(double seconds) =>
        TimeSpan.FromSeconds(Math.Max(0, seconds)).ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);

    /// <summary>A paragraph being built: where it starts, its text so far, and whether it is a mark.</summary>
    private sealed record Paragraph(double Start, StringBuilder Text, bool IsMark);
}
