using System.Globalization;
using System.Text;
using ClassScribe.Settings;
using ClassScribe.Storage;
using ClassScribe.YouTube;
using Microsoft.Extensions.Options;

namespace ClassScribe.Transcription;

/// <summary>
/// Lays a transcript out as a Markdown document: a title, the recording time when known, with the number of parts
/// when the recording was taken up again after it stopped, and readable paragraphs. A new paragraph starts after a
/// long pause, or at a sentence end once the paragraph has grown long. Marks set while recording, and the notes
/// ClassScribe made in the recording, such as where it was taken up again or where the laptop slept, become headings
/// at their position, so they show in an outline: a mark with its number, a note in its own words. Each paragraph can
/// carry its position in the recording. A YouTube video's transcript is headed by the video's title and link, and in a
/// downloaded video's each position links to that moment of the video. It lives as long as the app, and lays each
/// transcript out with the Transcript settings as they are when that transcript is made.
/// </summary>
public sealed class TranscriptFormatter(IOptionsMonitor<TranscriptOptions> options)
{
    private const string SpacingType = "spacing";

    /// <summary>The marks that end a sentence: . ? ! and the ellipsis, the Arabic question mark, the Urdu full stop, and their East Asian forms.</summary>
    private const string SentenceEnds = ".?!…؟۔。？！";

    private static readonly string Blank = Environment.NewLine + Environment.NewLine;

    /// <summary>
    /// Formats the transcript as Markdown, with the recording's marks and notes, if any, at their positions. When the time
    /// the recording started is known, the line under the title gives it, and says in how many parts the recording was
    /// made when there were <paramref name="parts"/> of them, two or more (see <see cref="ClassMarks.PartsIn"/>). A YouTube
    /// video's recording (<paramref name="video"/>) is headed by the video's title, says when it was downloaded or recorded,
    /// and names the video by its link (see <see cref="SourceLine"/>); in a downloaded video's transcript each position links
    /// to that moment on YouTube, since the positions are the video's own.
    /// </summary>
    public string Format(TranscriptionResult result, string title, DateTimeOffset? recordedUtc, IReadOnlyList<ClassMark>? marks = null, int parts = 1,
        VideoSource? video = null)
    {
        var settings = options.CurrentValue;
        var pending = new Queue<ClassMark>((marks ?? []).OrderBy(mark => mark.Position));
        var downloaded = video is { Obtained: VideoSource.Downloaded };
        var blocks = new List<string> { $"# {(video is null ? title : MarkdownText.Escape(video.Title))}" };
        if (recordedUtc is { } recorded)
        {
            var inParts = parts > 1 && !downloaded ? string.Create(CultureInfo.InvariantCulture, $", in {parts} parts") : "";
            blocks.Add($"{(downloaded ? "Downloaded" : "Recorded")} {recorded.UtcDateTime.ToString("d MMMM yyyy, HH:mm", CultureInfo.InvariantCulture)} UTC{inParts}.");
        }

        if (video is not null) blocks.Add(SourceLine(video, result, settings));

        // In a downloaded video's transcript each position links to that moment of the video on YouTube.
        Func<double, string>? moment = downloaded && settings.IncludeTimestamps ? seconds => YouTubeLinks.WatchUrl(video!.VideoId, (int)Math.Floor(seconds)) : null;

        if (!result.Words.Any(w => w.Type != SpacingType && w.Start is not null))
        {
            // Without word timings a mark or a note cannot be placed, so the marks and notes follow the text.
            var text = result.Text.Trim();
            if (text.Length > 0) blocks.Add(text);
            blocks.AddRange(pending.Select(mark => Heading(mark, settings, moment)));
            return string.Join(Blank, blocks) + Environment.NewLine;
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
                paragraphs.Add(new Paragraph(word.Start ?? lastEnd ?? 0, new StringBuilder(), Mark: null));

            // Spacing before the first word, or right after a mark, belongs to no paragraph.
            if (paragraphs.Count == 0 || paragraphs[^1].Mark is not null) continue;

            paragraphs[^1].Text.Append(word.Text);
            if (!isSpacing && word.End is { } end) lastEnd = end;
        }

        // Marks set after the last word, during silence at the end, close the transcript.
        while (pending.TryDequeue(out var mark)) paragraphs.Add(MarkParagraph(mark));

        foreach (var paragraph in paragraphs)
        {
            if (paragraph.Mark is { } mark)
            {
                blocks.Add(Heading(mark, settings, moment));
                continue;
            }

            var text = paragraph.Text.ToString().Trim();
            if (text.Length > 0) blocks.Add(Line(paragraph.Start, text, settings, moment));
        }

        return string.Join(Blank, blocks) + Environment.NewLine;
    }

    /// <summary>
    /// The line that names a YouTube video under the transcript's title: its link, with its title as the link's text, and
    /// for a download its channel, upload date, length and playlist as far as known, and the yt-dlp that fetched it, the
    /// positions being the video's own; for a recording made as it played, its channel and the recording's length, the
    /// positions being the recording's, which ads and pauses shift from the video's own times.
    /// </summary>
    private static string SourceLine(VideoSource video, TranscriptionResult result, TranscriptOptions settings)
    {
        var link = $"[{MarkdownText.Escape(video.Title)}]({video.Url})";
        if (video.Obtained != VideoSource.Downloaded)
        {
            var by = video.Channel is { Length: > 0 } author ? $", by {MarkdownText.Escape(author)}" : "";
            var length = result.AudioDurationSecs is { } seconds and > 0 ? $" ({Durations.Format(TimeSpan.FromSeconds(seconds))})" : "";
            return $"From YouTube: {link}{by}; recorded as it played in ClassScribe's window{length}. Positions are in the recording, so ads and pauses shift them from the video's own times.";
        }

        List<string> facts = [];
        if (video.Channel is { Length: > 0 } channel) facts.Add($"by {MarkdownText.Escape(channel)}");
        if (DateOnly.TryParseExact(video.UploadDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var uploaded))
            facts.Add($"uploaded {uploaded.ToString("d MMMM yyyy", CultureInfo.InvariantCulture)}");
        if (video.Duration is { Length: > 0 } duration) facts.Add($"{duration} long");
        if (video.Playlist is { } playlist)
        {
            var named = playlist.Title is { Length: > 0 } playlistTitle ? $" \"{MarkdownText.Escape(playlistTitle)}\"" : $" {playlist.Id}";
            facts.Add(string.Create(CultureInfo.InvariantCulture, $"number {playlist.Position} in the playlist{named}"));
        }

        var positions = settings.IncludeTimestamps ? " Positions are the video's own, and each links to that moment on YouTube." : "";
        var details = facts.Count > 0 ? ", " + string.Join(", ", facts) : "";
        return $"From YouTube: {link}{details}; downloaded with {video.Tool ?? "yt-dlp"}.{positions}";
    }

    /// <summary>
    /// Decides whether the word should open a new paragraph. The first word, and the first after a mark,
    /// always do. A pause or length setting of zero or less turns that kind of break off.
    /// </summary>
    private static bool StartsNewParagraph(List<Paragraph> paragraphs, TranscriptWord word, double? lastEnd, TranscriptOptions settings)
    {
        if (paragraphs.Count == 0 || paragraphs[^1].Mark is not null) return true;

        var pause = word.Start is { } start && lastEnd is { } end ? start - end : 0;
        if (settings.ParagraphPauseSeconds > 0 && pause >= settings.ParagraphPauseSeconds) return true;

        var current = paragraphs[^1].Text;
        return settings.MaxParagraphCharacters > 0 && current.Length >= settings.MaxParagraphCharacters && EndsSentence(current);
    }

    /// <summary>
    /// True when the text so far ends a sentence: with . ? ! or an ellipsis, or the Arabic, Urdu or
    /// East Asian marks for them, perhaps followed by closing quotes, closing brackets and spaces.
    /// </summary>
    private static bool EndsSentence(StringBuilder text)
    {
        for (var i = text.Length - 1; i >= 0; i--)
        {
            if (char.IsWhiteSpace(text[i]) || IsClosing(text[i])) continue;
            return SentenceEnds.Contains(text[i]);
        }

        return false;
    }

    /// <summary>True for a quote or bracket that can follow a sentence's end, such as " ’ ” » or ), and the opening-style quotes some languages close with, such as German “.</summary>
    private static bool IsClosing(char c) =>
        c is '"' or '\''
        || CharUnicodeInfo.GetUnicodeCategory(c) is UnicodeCategory.ClosePunctuation or UnicodeCategory.FinalQuotePunctuation or UnicodeCategory.InitialQuotePunctuation;

    /// <summary>A paragraph that holds only a mark or a note.</summary>
    private static Paragraph MarkParagraph(ClassMark mark) => new(mark.Position.TotalSeconds, new StringBuilder(), mark);

    /// <summary>
    /// A mark's heading, its number and, when one was typed, its label; or a note's, its words alone, without a number.
    /// Either comes after its position when positions are on, a link to that moment when <paramref name="moment"/> gives one.
    /// </summary>
    private static string Heading(ClassMark mark, TranscriptOptions settings, Func<double, string>? moment)
    {
        var text = mark.IsNote ? mark.Label ?? ""
            : mark.Label is { Length: > 0 } label ? $"Mark {mark.Number}: {label}"
            : $"Mark {mark.Number}";
        return settings.IncludeTimestamps ? $"## {Position(mark.Position.TotalSeconds, moment)} {text}" : $"## {text}";
    }

    /// <summary>A paragraph's line, starting with its position when positions are on, a link to that moment when <paramref name="moment"/> gives one.</summary>
    private static string Line(double start, string text, TranscriptOptions settings, Func<double, string>? moment) =>
        settings.IncludeTimestamps ? $"{Position(start, moment)} {text}" : text;

    /// <summary>A position as [hh:mm:ss], or as a link to that moment, [[hh:mm:ss]](link), when <paramref name="moment"/> gives one.</summary>
    private static string Position(double seconds, Func<double, string>? moment) =>
        moment is null ? $"[{FormatTime(seconds)}]" : $"[[{FormatTime(seconds)}]]({moment(seconds)})";

    /// <summary>Formats seconds as hh:mm:ss, with as many hours as there are.</summary>
    private static string FormatTime(double seconds) => Durations.Format(TimeSpan.FromSeconds(seconds));

    /// <summary>A paragraph being built: where it starts, its text so far, or the mark it stands for.</summary>
    private sealed record Paragraph(double Start, StringBuilder Text, ClassMark? Mark);
}
