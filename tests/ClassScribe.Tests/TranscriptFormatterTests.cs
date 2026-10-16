using ClassScribe.Settings;
using ClassScribe.Storage;
using ClassScribe.Transcription;
using Microsoft.Extensions.Options;

namespace ClassScribe.Tests;

/// <summary>Layout of the transcript, a Markdown document.</summary>
[TestFixture]
public class TranscriptFormatterTests
{
    private static readonly string Blank = Environment.NewLine + Environment.NewLine;
    private static readonly string Title = "# Kalam" + Blank;

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

    /// <summary>The document opens with the title; a long pause starts a new paragraph with its own position.</summary>
    [Test]
    public void A_long_pause_starts_a_new_paragraph_with_its_own_timestamp()
    {
        var transcript = Transcript("Hello world. Next part.",
            Word("Hello", 0.0, 0.4), Space(0.4, 0.5), Word("world.", 0.5, 1.0), Space(1.0, 65.0),
            Word("Next", 65.0, 65.3), Space(65.3, 65.4), Word("part.", 65.4, 66.0));

        var text = Formatter().Format(transcript, "Kalam", recordedUtc: null);

        Assert.That(text, Is.EqualTo($"{Title}[00:00:00] Hello world.{Blank}[00:01:05] Next part.{Environment.NewLine}"));
    }

    /// <summary>When the recording's start time is known, it is shown under the title.</summary>
    [Test]
    public void The_recording_time_is_shown_under_the_title()
    {
        var transcript = Transcript("Hello world.", Word("Hello", 0.0, 0.4), Space(0.4, 0.5), Word("world.", 0.5, 1.0));

        var text = Formatter().Format(transcript, "Kalam", new DateTimeOffset(2026, 10, 15, 20, 55, 27, TimeSpan.Zero));

        Assert.That(text, Is.EqualTo($"{Title}Recorded 15 October 2026, 20:55 UTC.{Blank}[00:00:00] Hello world.{Environment.NewLine}"));
    }

    /// <summary>Positions can be left out.</summary>
    [Test]
    public void Timestamps_can_be_switched_off()
    {
        var transcript = Transcript("Hello world.", Word("Hello", 0.0, 0.4), Space(0.4, 0.5), Word("world.", 0.5, 1.0));

        var text = Formatter(new TranscriptOptions { IncludeTimestamps = false }).Format(transcript, "Kalam", null);

        Assert.That(text, Is.EqualTo($"{Title}Hello world.{Environment.NewLine}"));
    }

    /// <summary>A paragraph that has grown long is split at the next sentence end.</summary>
    [Test]
    public void A_long_paragraph_is_split_at_the_next_sentence_end()
    {
        var transcript = Transcript("One two. Three four. Five.",
            Word("One", 0.0, 0.2), Space(0.2, 0.3), Word("two.", 0.3, 0.5), Space(0.5, 0.6),
            Word("Three", 0.6, 0.8), Space(0.8, 0.9), Word("four.", 0.9, 1.1), Space(1.1, 1.2),
            Word("Five.", 1.2, 1.4));

        var text = Formatter(new TranscriptOptions { IncludeTimestamps = false, MaxParagraphCharacters = 12 }).Format(transcript, "Kalam", null);

        Assert.That(text, Is.EqualTo($"{Title}One two. Three four.{Blank}Five.{Environment.NewLine}"));
    }

    /// <summary>
    /// A long paragraph is also split after a sentence that ends in a closing quote or bracket (a plain
    /// apostrophe, or a German “ that looks like an opening quote, included), in an ellipsis, in Arabic
    /// or Urdu punctuation, or in the East Asian full stop, question mark or exclamation mark.
    /// </summary>
    [TestCase("said.\"")]
    [TestCase("said.'")]
    [TestCase("said?)")]
    [TestCase("said!”")]
    [TestCase("said.’")]
    [TestCase("said.»")]
    [TestCase("gut.“")]
    [TestCase("said…")]
    [TestCase("قال؟")]
    [TestCase("قال۔")]
    [TestCase("了。")]
    [TestCase("what？")]
    [TestCase("好！")]
    public void A_long_paragraph_is_split_after_any_sentence_end(string lastWord)
    {
        var transcript = Transcript($"He {lastWord} Then.",
            Word("He", 0.0, 0.2), Space(0.2, 0.3), Word(lastWord, 0.3, 0.6), Space(0.6, 0.7), Word("Then.", 0.7, 1.0));

        var text = Formatter(new TranscriptOptions { IncludeTimestamps = false, MaxParagraphCharacters = 5 }).Format(transcript, "Kalam", null);

        Assert.That(text, Is.EqualTo($"{Title}He {lastWord}{Blank}Then.{Environment.NewLine}"));
    }

    /// <summary>A closing bracket without a sentence end before it, as in a tag such as (laughs), does not end a sentence.</summary>
    [Test]
    public void A_closing_bracket_alone_does_not_end_a_sentence()
    {
        var transcript = Transcript("He (laughs) then.",
            Word("He", 0.0, 0.2), Space(0.2, 0.3), Word("(laughs)", 0.3, 0.6), Space(0.6, 0.7), Word("then.", 0.7, 1.0));

        var text = Formatter(new TranscriptOptions { IncludeTimestamps = false, MaxParagraphCharacters = 5 }).Format(transcript, "Kalam", null);

        Assert.That(text, Is.EqualTo($"{Title}He (laughs) then.{Environment.NewLine}"));
    }

    /// <summary>A pause setting of zero or less turns pause breaks off, rather than putting every word in a paragraph of its own.</summary>
    [TestCase(0.0)]
    [TestCase(-1.0)]
    public void A_pause_setting_of_zero_or_less_turns_pause_breaks_off(double pause)
    {
        var transcript = Transcript("One two. Three.",
            Word("One", 0.0, 0.2), Space(0.2, 5.0), Word("two.", 5.0, 5.4), Space(5.4, 5.5), Word("Three.", 5.5, 6.0));

        var text = Formatter(new TranscriptOptions { IncludeTimestamps = false, ParagraphPauseSeconds = pause }).Format(transcript, "Kalam", null);

        Assert.That(text, Is.EqualTo($"{Title}One two. Three.{Environment.NewLine}"));
    }

    /// <summary>A paragraph length of zero or less turns the length limit off, rather than starting a paragraph at every sentence.</summary>
    [TestCase(0)]
    [TestCase(-5)]
    public void A_length_setting_of_zero_or_less_turns_the_length_limit_off(int length)
    {
        var transcript = Transcript("One. Two. Three.",
            Word("One.", 0.0, 0.2), Space(0.2, 0.3), Word("Two.", 0.3, 0.5), Space(0.5, 0.6), Word("Three.", 0.6, 0.8));

        var text = Formatter(new TranscriptOptions { IncludeTimestamps = false, MaxParagraphCharacters = length }).Format(transcript, "Kalam", null);

        Assert.That(text, Is.EqualTo($"{Title}One. Two. Three.{Environment.NewLine}"));
    }

    /// <summary>Positions more than a day into the recording keep all their hours, in paragraphs and in mark headings.</summary>
    [Test]
    public void Positions_after_a_day_keep_all_their_hours()
    {
        const double at = 25 * 3600;
        var transcript = Transcript("Late words.", Word("Late", at, at + 0.4), Space(at + 0.4, at + 0.5), Word("words.", at + 0.5, at + 1.0));

        var text = Formatter().Format(transcript, "Kalam", null, [new ClassMark(1, TimeSpan.FromSeconds(at + 1.5), null)]);

        Assert.That(text, Is.EqualTo($"{Title}[25:00:00] Late words.{Blank}## [25:00:01] Mark 1{Environment.NewLine}"));
    }

    /// <summary>Without word timings the plain text is used.</summary>
    [Test]
    public void Without_word_timings_the_plain_text_is_used()
    {
        var text = Formatter().Format(Transcript("  Just the text.  "), "Kalam", null);

        Assert.That(text, Is.EqualTo($"{Title}Just the text.{Environment.NewLine}"));
    }

    /// <summary>A mark becomes a heading at its position, splitting the paragraph there, so it shows in a Markdown outline.</summary>
    [Test]
    public void A_mark_becomes_a_heading_at_its_position()
    {
        var transcript = Transcript("Hello world. Next part.",
            Word("Hello", 0.0, 0.4), Space(0.4, 0.5), Word("world.", 0.5, 1.0), Space(1.0, 1.2),
            Word("Next", 1.2, 1.5), Space(1.5, 1.6), Word("part.", 1.6, 2.0));

        var text = Formatter().Format(transcript, "Kalam", null, [new ClassMark(1, TimeSpan.FromSeconds(1.1), null)]);

        Assert.That(text, Is.EqualTo($"{Title}[00:00:00] Hello world.{Blank}## [00:00:01] Mark 1{Blank}[00:00:01] Next part.{Environment.NewLine}"));
    }

    /// <summary>A label typed after a mark's time in the marks file is shown in its heading; without positions the heading is bare.</summary>
    [Test]
    public void A_mark_shows_its_label()
    {
        var transcript = Transcript("Hello world.", Word("Hello", 0.0, 0.4), Space(0.4, 0.5), Word("world.", 0.5, 1.0));

        var text = Formatter(new TranscriptOptions { IncludeTimestamps = false })
            .Format(transcript, "Kalam", null, [new ClassMark(3, TimeSpan.FromSeconds(0.45), "Key definition")]);

        Assert.That(text, Is.EqualTo($"{Title}Hello{Blank}## Mark 3: Key definition{Blank}world.{Environment.NewLine}"));
    }

    /// <summary>Marks are placed by time, whatever their order in the file.</summary>
    [Test]
    public void Marks_are_placed_in_time_order()
    {
        var transcript = Transcript("One two three.",
            Word("One", 0.0, 0.3), Space(0.3, 0.4), Word("two", 0.4, 0.7), Space(0.7, 0.8), Word("three.", 0.8, 1.2));

        var text = Formatter(new TranscriptOptions { IncludeTimestamps = false })
            .Format(transcript, "Kalam", null, [new ClassMark(2, TimeSpan.FromSeconds(0.75), null), new ClassMark(1, TimeSpan.FromSeconds(0.35), null)]);

        Assert.That(text, Is.EqualTo($"{Title}One{Blank}## Mark 1{Blank}two{Blank}## Mark 2{Blank}three.{Environment.NewLine}"));
    }

    /// <summary>A mark set after the last word, during silence at the end, closes the transcript.</summary>
    [Test]
    public void Marks_after_the_last_word_close_the_transcript()
    {
        var transcript = Transcript("Hello world.", Word("Hello", 0.0, 0.4), Space(0.4, 0.5), Word("world.", 0.5, 1.0));

        var text = Formatter().Format(transcript, "Kalam", null, [new ClassMark(1, TimeSpan.FromSeconds(30), null)]);

        Assert.That(text, Is.EqualTo($"{Title}[00:00:00] Hello world.{Blank}## [00:00:30] Mark 1{Environment.NewLine}"));
    }

    /// <summary>Without word timings a mark cannot be placed, so the marks follow the text.</summary>
    [Test]
    public void Without_word_timings_marks_follow_the_text()
    {
        var text = Formatter().Format(Transcript("Just the text."), "Kalam", null, [new ClassMark(1, TimeSpan.FromSeconds(75), null)]);

        Assert.That(text, Is.EqualTo($"{Title}Just the text.{Blank}## [00:01:15] Mark 1{Environment.NewLine}"));
    }
}
