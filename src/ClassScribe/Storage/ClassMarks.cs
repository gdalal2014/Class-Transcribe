using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace ClassScribe.Storage;

/// <summary>
/// The marks of a recording, one per line in its marks file (see <see cref="RecordingFiles.Marks"/>): a position in the
/// recording (hh:mm:ss.fff), optionally followed by a label typed in by hand. The same file holds the notes ClassScribe
/// adds itself, such as where the recording was taken up again: the position, a tilde, a space and the note's words,
/// which an older ClassScribe reads as a mark with a label, without harm. Blank lines and lines starting with # are
/// ignored. It is the adapter of <see cref="IClassMarks"/>; its one static, the count of a recording's parts, is the port's
/// member too, implemented over it for the use cases, and called directly by Infrastructure.
/// </summary>
public sealed partial class ClassMarks(ILogger<ClassMarks> logger) : IClassMarks
{
    /// <summary>What comes between a note's position and its words, telling a note from a mark's label.</summary>
    private const string NoteSign = "~";

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// Adds a mark straight to the recording's marks file, so it survives a crash, and returns its number, counting the
    /// marks alone, not the notes. The file may have been edited by hand meanwhile, so the mark is written in the
    /// encoding it was saved in, and on a line of its own even when the editor left no line break at the end.
    /// </summary>
    public int Add(RecordingFiles files, TimeSpan position)
    {
        Append(files, PositionText(position));
        return Parse(files.Marks, logger: null).Count(mark => !mark.IsNote);
    }

    /// <summary>
    /// Adds a note at <paramref name="position"/> straight to the recording's marks file, as a mark is added, so that it
    /// survives a crash; the transcript shows it as a heading in its own words. The note keeps to one line: line breaks
    /// and runs of spaces in <paramref name="text"/> become single spaces.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="text"/> holds no words.</exception>
    public void AddNote(RecordingFiles files, TimeSpan position, string text)
    {
        var words = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (words.Length == 0) throw new ArgumentException("A note needs words.", nameof(text));

        Append(files, $"{PositionText(position)} {NoteSign} {words}");
    }

    /// <summary>
    /// Adds the note that marks where the recording was taken up again as its next part, at <paramref name="position"/>,
    /// its length on disk until then: "Resumed at 23:51 UTC, about 4 minutes not recorded". It says when, in UTC as the
    /// time under the transcript's title is, with the day when that is not the day the recording started, and about how
    /// long was not recorded in between. Each such note makes the recording one part more (see <see cref="PartsOf"/>),
    /// so its words are the ones <see cref="ClassMark.IsResume"/> knows, and change only together with them.
    /// </summary>
    public void AddResume(RecordingFiles files, TimeSpan position, DateTimeOffset resumedUtc, TimeSpan notRecorded)
    {
        var resumed = resumedUtc.UtcDateTime;
        var when = files.StartedUtc?.UtcDateTime.Date == resumed.Date
            ? resumed.ToString("'at 'HH:mm", CultureInfo.InvariantCulture)
            : resumed.ToString("'on 'd MMMM' at 'HH:mm", CultureInfo.InvariantCulture);
        AddNote(files, position, $"Resumed {when} UTC, {About(notRecorded)} not recorded");
    }

    /// <summary>
    /// Reads the recording's marks and notes, in the order they were written; the marks are numbered from 1, the notes
    /// not at all. A line that is neither is skipped with a warning.
    /// </summary>
    public IReadOnlyList<ClassMark> Read(RecordingFiles files) => Parse(files.Marks, logger);

    /// <summary>
    /// How many parts the recording was made in: one, and one more for each time it was taken up again, as its resume
    /// notes say (see <see cref="AddResume"/>). Its marks file is read without a word about lines that are no marks,
    /// which reading it for the transcript reports; a recording without one was made in one part.
    /// </summary>
    public static int PartsOf(RecordingFiles files) => ClassMark.PartsIn(Parse(files.Marks, logger: null));

    /// <inheritdoc />
    int IClassMarks.PartsOf(RecordingFiles files) => PartsOf(files);

    /// <summary>
    /// A position as the marks file writes it, hh:mm:ss.fff with as many hours as there are. A position before the start,
    /// which no recording has, is written as the start.
    /// </summary>
    private static string PositionText(TimeSpan position)
    {
        var at = position < TimeSpan.Zero ? TimeSpan.Zero : position;
        return string.Create(CultureInfo.InvariantCulture, $"{Durations.Format(at)}.{at.Milliseconds:000}");
    }

    /// <summary>
    /// About how long <paramref name="length"/> is, in words: "less than a minute", "about 4 minutes", "about 1 hour 5
    /// minutes", "about 2 hours". It is rounded to the minute.
    /// </summary>
    private static string About(TimeSpan length)
    {
        if (length < TimeSpan.FromMinutes(1)) return "less than a minute";

        var (hours, minutes) = Math.DivRem((long)Math.Round(length.TotalMinutes, MidpointRounding.AwayFromZero), 60);
        var minutesText = $"{minutes} minute{(minutes == 1 ? "" : "s")}";
        if (hours == 0) return $"about {minutesText}";

        var hoursText = $"{hours} hour{(hours == 1 ? "" : "s")}";
        return minutes == 0 ? $"about {hoursText}" : $"about {hoursText} {minutesText}";
    }

    /// <summary>
    /// Appends <paramref name="line"/> to the recording's marks file, never rewriting it, so that a crash part-way through
    /// cannot take the earlier lines with it. The line goes in the encoding the file was saved in, and on a line of its
    /// own even when an editor left no line break at the end.
    /// </summary>
    private static void Append(RecordingFiles files, string line)
    {
        using var file = new FileStream(files.Marks, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
        var (encoding, preambleLength) = DetectEncoding(file);
        var endsWithLineBreak = EndsWithLineBreak(file, encoding, preambleLength);

        // Positioned at the end before the writer is made, so it does not write a byte order mark again.
        file.Seek(0, SeekOrigin.End);
        using var writer = new StreamWriter(file, encoding);
        if (!endsWithLineBreak) writer.Write(Environment.NewLine);
        writer.Write(line + Environment.NewLine);
    }

    /// <summary>
    /// Parses the marks file; a missing file holds no marks. A line that is neither a mark nor a note is skipped, with a
    /// warning when <paramref name="logger"/> is given. The file is read while others may write to it, as a recorder
    /// adding a mark does for a moment.
    /// </summary>
    private static List<ClassMark> Parse(string path, ILogger? logger)
    {
        if (!File.Exists(path)) return [];

        var marks = new List<ClassMark>();
        var numbered = 0;
        var lineNumber = 0;
        using var reader = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        while (reader.ReadLine() is { } line)
        {
            lineNumber++;
            var text = line.Trim();
            if (text.Length == 0 || text.StartsWith('#')) continue;

            var match = MarkLine().Match(text);
            if (!match.Success)
            {
                logger?.LogWarning("Skipping line {Line} of {File} ({Text}): a mark starts with its time, such as 00:42:13", lineNumber, Path.GetFileName(path), text);
                continue;
            }

            var fraction = match.Groups["fraction"].Success ? match.Groups["fraction"].Value.PadRight(3, '0') : "0";
            var position = new TimeSpan(0,
                int.Parse(match.Groups["hours"].Value, CultureInfo.InvariantCulture),
                int.Parse(match.Groups["minutes"].Value, CultureInfo.InvariantCulture),
                int.Parse(match.Groups["seconds"].Value, CultureInfo.InvariantCulture),
                int.Parse(fraction, CultureInfo.InvariantCulture));
            var label = match.Groups["label"].Success ? match.Groups["label"].Value : null;
            marks.Add(match.Groups["note"].Success
                ? new ClassMark(0, position, label, IsNote: true)
                : new ClassMark(++numbered, position, label));
        }

        return marks;
    }

    /// <summary>
    /// The encoding the marks file was saved in, read from its byte order mark, and how many bytes that
    /// byte order mark takes: UTF-16 as Notepad can save it, otherwise UTF-8. The encodings returned
    /// write no byte order mark of their own.
    /// </summary>
    private static (Encoding Encoding, int PreambleLength) DetectEncoding(FileStream file)
    {
        Span<byte> buffer = stackalloc byte[3];
        file.Seek(0, SeekOrigin.Begin);
        ReadOnlySpan<byte> start = buffer[..file.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false)];

        if (start.StartsWith(Encoding.UTF8.Preamble)) return (Utf8NoBom, Encoding.UTF8.Preamble.Length);
        if (start.StartsWith(Encoding.Unicode.Preamble)) return (new UnicodeEncoding(bigEndian: false, byteOrderMark: false), Encoding.Unicode.Preamble.Length);
        if (start.StartsWith(Encoding.BigEndianUnicode.Preamble)) return (new UnicodeEncoding(bigEndian: true, byteOrderMark: false), Encoding.BigEndianUnicode.Preamble.Length);
        return (Utf8NoBom, 0);
    }

    /// <summary>True when the file holds nothing after its byte order mark, or its last character is a line break.</summary>
    private static bool EndsWithLineBreak(FileStream file, Encoding encoding, int preambleLength)
    {
        var width = encoding.GetByteCount("\n");
        if (file.Length - preambleLength < width) return true;

        var last = new byte[width];
        file.Seek(-width, SeekOrigin.End);
        file.ReadExactly(last);
        return encoding.GetString(last) is "\n" or "\r";
    }

    /// <summary>
    /// A line of the marks file: hours, minutes, seconds and optional milliseconds, then, for a note, a tilde and a space
    /// ("note"), and an optional label, or the note's words. The digits are 0 to 9 only: a digit of another script would
    /// pass \d but not the parser.
    /// </summary>
    [GeneratedRegex(@"^(?<hours>[0-9]{1,3}):(?<minutes>[0-5][0-9]):(?<seconds>[0-5][0-9])(?:\.(?<fraction>[0-9]{1,3}))?(?:\s+(?:(?<note>~)\s+)?(?<label>.*\S))?$", RegexOptions.CultureInvariant)]
    private static partial Regex MarkLine();
}
