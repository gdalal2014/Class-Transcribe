namespace ClassScribe.Storage;

/// <summary>
/// The marks of a recording, one per line in its marks file (architecture 4.3; see <see cref="RecordingFiles.Marks"/>): a
/// position in the recording (hh:mm:ss.fff), optionally followed by a label typed in by hand. The same file holds the notes
/// ClassScribe adds itself, such as where the recording was taken up again: the position, a tilde, a space and the note's
/// words, which an older ClassScribe reads as a mark with a label, without harm. Blank lines and lines starting with # are
/// ignored.
/// </summary>
public interface IClassMarks
{
    /// <summary>
    /// Adds a mark straight to the recording's marks file, so it survives a crash, and returns its number, counting the
    /// marks alone, not the notes. The file may have been edited by hand meanwhile, so the mark is written in the
    /// encoding it was saved in, and on a line of its own even when the editor left no line break at the end.
    /// </summary>
    int Add(RecordingFiles files, TimeSpan position);

    /// <summary>
    /// Adds a note at <paramref name="position"/> straight to the recording's marks file, as a mark is added, so that it
    /// survives a crash; the transcript shows it as a heading in its own words. The note keeps to one line: line breaks
    /// and runs of spaces in <paramref name="text"/> become single spaces.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="text"/> holds no words.</exception>
    void AddNote(RecordingFiles files, TimeSpan position, string text);

    /// <summary>
    /// Adds the note that marks where the recording was taken up again as its next part, at <paramref name="position"/>,
    /// its length on disk until then: "Resumed at 23:51 UTC, about 4 minutes not recorded". It says when, in UTC as the
    /// time under the transcript's title is, with the day when that is not the day the recording started, and about how
    /// long was not recorded in between. Each such note makes the recording one part more (see <see cref="PartsOf"/>),
    /// so its words are the ones <see cref="ClassMark.IsResume"/> knows, and change only together with them.
    /// </summary>
    void AddResume(RecordingFiles files, TimeSpan position, DateTimeOffset resumedUtc, TimeSpan notRecorded);

    /// <summary>
    /// Reads the recording's marks and notes, in the order they were written; the marks are numbered from 1, the notes
    /// not at all. A line that is neither is skipped with a warning.
    /// </summary>
    IReadOnlyList<ClassMark> Read(RecordingFiles files);

    /// <summary>
    /// How many parts the recording was made in: one, and one more for each time it was taken up again, as its resume
    /// notes say (see <see cref="AddResume"/>). Its marks file is read without a word about lines that are no marks,
    /// which reading it for the transcript reports; a recording without one was made in one part.
    /// </summary>
    int PartsOf(RecordingFiles files);
}
