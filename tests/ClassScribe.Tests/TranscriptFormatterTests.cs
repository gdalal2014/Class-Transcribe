using ClassScribe.Settings;
using ClassScribe.Transcription;
using Microsoft.Extensions.Options;

namespace ClassScribe.Tests;

/// <summary>Layout of the transcript text file.</summary>
[TestFixture]
public class TranscriptFormatterTests
{
    private static readonly string Blank = Environment.NewLine + Environment.NewLine;

    /// <summary>Creates the formatter under test.</summary>
    private static TranscriptFormatter Formatter(TranscriptOptions? options = null) =>
        new(Options.Create(options ?? new TranscriptOptions()));

    /// <summary>Builds a transcript from its text and words.</summary>
    private static TranscriptionResult Transcript(string text, params TranscriptWord[] words) =>
        new(text, "en", 1.0, null, null, words, "{}");

    /// <summary>A spoken word with its timing.</summary>
    private static TranscriptWord Word(string text, double start, double end) => new(text, start, end, "word", null);

    /// <summary>The spacing between two words, which carries the pause.</summary>
    private static TranscriptWord Space(double start, double end) => new(" ", start, end, "spacing", null);

    [Test]
    public void A_long_pause_starts_a_new_paragraph_with_its_own_timestamp()
    {
        var transcript = Transcript("Hello world. Next part.",
            Word("Hello", 0.0, 0.4), Space(0.4, 0.5), Word("world.", 0.5, 1.0), Space(1.0, 65.0),
            Word("Next", 65.0, 65.3), Space(65.3, 65.4), Word("part.", 65.4, 66.0));

        var text = Formatter().Format(transcript);

        Assert.That(text, Is.EqualTo($"[00:00:00] Hello world.{Blank}[00:01:05] Next part.{Environment.NewLine}"));
    }

    [Test]
    public void Timestamps_can_be_switched_off()
    {
        var transcript = Transcript("Hello world.", Word("Hello", 0.0, 0.4), Space(0.4, 0.5), Word("world.", 0.5, 1.0));

        var text = Formatter(new TranscriptOptions { IncludeTimestamps = false }).Format(transcript);

        Assert.That(text, Is.EqualTo("Hello world." + Environment.NewLine));
    }

    [Test]
    public void A_long_paragraph_is_split_at_the_next_sentence_end()
    {
        var transcript = Transcript("One two. Three four. Five.",
            Word("One", 0.0, 0.2), Space(0.2, 0.3), Word("two.", 0.3, 0.5), Space(0.5, 0.6),
            Word("Three", 0.6, 0.8), Space(0.8, 0.9), Word("four.", 0.9, 1.1), Space(1.1, 1.2),
            Word("Five.", 1.2, 1.4));

        var text = Formatter(new TranscriptOptions { IncludeTimestamps = false, MaxParagraphCharacters = 12 }).Format(transcript);

        Assert.That(text, Is.EqualTo($"One two. Three four.{Blank}Five.{Environment.NewLine}"));
    }

    [Test]
    public void Without_word_timings_the_plain_text_is_used()
    {
        var text = Formatter().Format(Transcript("  Just the text.  "));

        Assert.That(text, Is.EqualTo("Just the text." + Environment.NewLine));
    }
}
