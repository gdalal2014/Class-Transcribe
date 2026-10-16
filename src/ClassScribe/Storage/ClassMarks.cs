using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace ClassScribe.Storage;

/// <summary>A moment marked with M while recording.</summary>
/// <param name="Number">The mark's number, counting from 1 in the order the marks were set.</param>
/// <param name="Position">Where the mark falls in the recording, and so in the transcript.</param>
/// <param name="Label">Text typed after the time in marks.txt by hand, if any.</param>
public sealed record ClassMark(int Number, TimeSpan Position, string? Label);

/// <summary>
/// The marks of a recording, one per line in its marks file (see <see cref="RecordingFiles.Marks"/>):
/// a position in the recording (hh:mm:ss.fff), optionally followed by a label typed in by hand. Blank
/// lines and lines starting with # are ignored.
/// </summary>
public sealed partial class ClassMarks(ILogger<ClassMarks> logger)
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// Adds a mark straight to the recording's marks file, so it survives a crash, and returns its number.
    /// The file may have been edited by hand meanwhile, so the mark is written in the encoding it was saved
    /// in, and on a line of its own even when the editor left no line break at the end.
    /// </summary>
    public int Add(RecordingFiles files, TimeSpan position)
    {
        var line = string.Create(CultureInfo.InvariantCulture, $"{Durations.Format(position)}.{position.Milliseconds:000}");

        // Appended, never rewritten, so a crash part-way through cannot take the earlier marks with it.
        using (var file = new FileStream(files.Marks, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read))
        {
            var (encoding, preambleLength) = DetectEncoding(file);
            var endsWithLineBreak = EndsWithLineBreak(file, encoding, preambleLength);

            // Positioned at the end before the writer is made, so it does not write a byte order mark again.
            file.Seek(0, SeekOrigin.End);
            using var writer = new StreamWriter(file, encoding);
            if (!endsWithLineBreak) writer.Write(Environment.NewLine);
            writer.Write(line + Environment.NewLine);
        }

        return Parse(files.Marks, reportProblems: false).Count;
    }

    /// <summary>Reads the recording's marks, in the order they were set. A line that is not a mark is skipped with a warning.</summary>
    public IReadOnlyList<ClassMark> Read(RecordingFiles files) => Parse(files.Marks, reportProblems: true);

    /// <summary>Parses the marks file; a missing file holds no marks.</summary>
    private List<ClassMark> Parse(string path, bool reportProblems)
    {
        if (!File.Exists(path)) return [];

        var marks = new List<ClassMark>();
        var lineNumber = 0;
        foreach (var line in File.ReadLines(path))
        {
            lineNumber++;
            var text = line.Trim();
            if (text.Length == 0 || text.StartsWith('#')) continue;

            var match = MarkLine().Match(text);
            if (!match.Success)
            {
                if (reportProblems)
                    logger.LogWarning("Skipping line {Line} of {File} ({Text}): a mark starts with its time, such as 00:42:13", lineNumber, Path.GetFileName(path), text);
                continue;
            }

            var fraction = match.Groups["fraction"].Success ? match.Groups["fraction"].Value.PadRight(3, '0') : "0";
            var position = new TimeSpan(0,
                int.Parse(match.Groups["hours"].Value, CultureInfo.InvariantCulture),
                int.Parse(match.Groups["minutes"].Value, CultureInfo.InvariantCulture),
                int.Parse(match.Groups["seconds"].Value, CultureInfo.InvariantCulture),
                int.Parse(fraction, CultureInfo.InvariantCulture));
            var label = match.Groups["label"].Success ? match.Groups["label"].Value : null;
            marks.Add(new ClassMark(marks.Count + 1, position, label));
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

    /// <summary>A mark's line: hours, minutes, seconds and optional milliseconds, then an optional label.</summary>
    [GeneratedRegex(@"^(?<hours>\d{1,3}):(?<minutes>[0-5]\d):(?<seconds>[0-5]\d)(?:\.(?<fraction>\d{1,3}))?(?:\s+(?<label>.*\S))?$", RegexOptions.CultureInvariant)]
    private static partial Regex MarkLine();
}
