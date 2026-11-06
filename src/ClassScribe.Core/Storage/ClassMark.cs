using System.Text.RegularExpressions;

namespace ClassScribe.Storage;

/// <summary>A moment marked with M while recording, or a note ClassScribe made at a moment of the recording.</summary>
/// <param name="Number">The mark's number, counting from 1 in the order the marks were set; 0 for a note, which has none.</param>
/// <param name="Position">Where the mark or note falls in the recording, and so in the transcript.</param>
/// <param name="Label">For a mark, text typed after the time in marks.txt by hand, if any; for a note, its words.</param>
/// <param name="IsNote">
/// True for a note, such as "Resumed at 23:51 UTC, about 4 minutes not recorded": the transcript shows it as a heading in
/// its own words, without a number, and it does not count as a mark.
/// </param>
public sealed partial record ClassMark(int Number, TimeSpan Position, string? Label, bool IsNote = false)
{
    /// <summary>
    /// True for the note that marks where the recording was taken up again as its next part, in the words
    /// <c>IClassMarks.AddResume</c> writes; each one makes the recording one part more. A note typed by hand in
    /// other words is no resume, even one that starts the same way, such as "Resumed after the break".
    /// </summary>
    public bool IsResume => IsNote && Label is { } words && ResumeWords().IsMatch(words);

    /// <summary>How many parts a recording with these marks and notes was made in: one, and one more for each resume note.</summary>
    public static int PartsIn(IReadOnlyList<ClassMark> marks) => 1 + marks.Count(mark => mark.IsResume);

    /// <summary>
    /// The words of a resume note exactly as <c>IClassMarks.AddResume</c> writes them: "Resumed at 23:51 UTC, about
    /// 4 minutes not recorded", with "on 16 October at" for "at" on a later day, and "less than a minute", "about 1 hour 5
    /// minutes" or "about 2 hours" for other gaps.
    /// </summary>
    [GeneratedRegex(@"^Resumed (?:at|on [0-9]{1,2} [A-Z][a-z]+ at) [0-9]{2}:[0-9]{2} UTC, (?:less than a minute|about [0-9]+ (?:minutes?|hours?(?: [0-9]+ minutes?)?)) not recorded$", RegexOptions.CultureInvariant)]
    private static partial Regex ResumeWords();
}
