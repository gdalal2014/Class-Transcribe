using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ClassScribe.Transcription;
using Microsoft.Extensions.Logging;

namespace ClassScribe.Storage;

/// <summary>
/// The numbered copies of a recording's transcript, the .md of each copy, and of ElevenLabs' answer, the .json beside it
/// (see <see cref="RecordingFiles.Transcript"/> and <see cref="RecordingFiles.Json"/>). A new copy takes the first number
/// that has neither file, and claims each file by creating it only when no file of that name is there
/// (<see cref="FileMode.CreateNew"/>), so nothing is ever overwritten, not even by another ClassScribe saving a transcript
/// of the same recording at the same moment: whichever comes second takes the next number. Both files are written in
/// UTF-8 without a byte order mark.
/// </summary>
public static class TranscriptCopies
{
    /// <summary>
    /// The <see cref="Exception.Data"/> key under which a failure of
    /// <see cref="Save(RecordingFiles, string, Func{string}, ILogger)"/> gives the path of ElevenLabs' answer, when the
    /// answer was saved before the failure, so that whoever reports it can point there.
    /// </summary>
    public const string SavedAnswerKey = "ClassScribe.SavedAnswer";

    /// <summary>ERROR_FILE_EXISTS as an HRESULT: what creating a file reports when a file of that name is there.</summary>
    private const int FileExists = unchecked((int)0x80070050);

    /// <summary>ERROR_ALREADY_EXISTS as an HRESULT, which Windows reports instead for some names.</summary>
    private const int AlreadyExists = unchecked((int)0x800700B7);

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// Saves ElevenLabs' answer <paramref name="json"/>, and the transcript <paramref name="transcript"/> makes from it,
    /// as the next copy of the recording's transcript, and returns where they went. The answer is saved first, since it
    /// has been paid for, and the transcript is made only then, once, so that an answer whose transcript cannot be made is
    /// on disk all the same. When the transcript's name is taken at that moment, by another ClassScribe, the answer moves
    /// on with the transcript to the next free copy. A failure that comes after the answer was saved carries the answer's
    /// path in its <see cref="Exception.Data"/> under <see cref="SavedAnswerKey"/>.
    /// </summary>
    public static TranscriptFiles Save(RecordingFiles files, string json, Func<string> transcript, ILogger logger) =>
        Save(files, json, transcript, logger, WriteAll);

    /// <summary>
    /// Saves as <see cref="Save(RecordingFiles, string, Func{string}, ILogger)"/> does, with <paramref name="write"/> as
    /// the step that writes each file once it is made, so that a test can have that step fail, as a full disk does.
    /// </summary>
    internal static TranscriptFiles Save(RecordingFiles files, string json, Func<string> transcript, ILogger logger, Action<FileStream, byte[]> write)
    {
        var answer = Utf8NoBom.GetBytes(json);
        byte[]? markdown = null;
        for (var copy = FirstFree(files, 1); ; copy = FirstFree(files, copy + 1))
        {
            var answerPath = files.Json(copy);
            if (!TryCreate(answerPath, answer, write, logger))
            {
                logger.LogInformation("Copy {Copy} of the transcript of {Recording} was taken at that moment; saving the next one instead", copy, files.BaseName);
                continue;
            }

            var transcriptPath = files.Transcript(copy);
            try
            {
                markdown ??= Utf8NoBom.GetBytes(transcript());
                if (TryCreate(transcriptPath, markdown, write, logger)) return new TranscriptFiles(transcriptPath, answerPath);
            }
            catch (Exception ex)
            {
                // Rethrown with the place of the answer, which is saved and paid for, so that whoever reports the failure can point there.
                ex.Data[SavedAnswerKey] = answerPath;
                throw;
            }

            logger.LogInformation("The transcript's name {Path} was taken at that moment; saving the transcript and its answer as the next copy instead", transcriptPath);
            Remove(answerPath, "a second copy of ElevenLabs' answer, which is saved with its transcript under the next number", logger);
        }
    }

    /// <summary>
    /// Saves a transcript made again from a saved answer, without an answer of its own, as the first copy that has neither
    /// a transcript nor an answer, and returns its path. An answer kept without its transcript, or a transcript made by
    /// hand, keeps its number to itself.
    /// </summary>
    public static string SaveRebuilt(RecordingFiles files, string markdown, ILogger logger) => SaveRebuilt(files, markdown, logger, WriteAll);

    /// <summary>
    /// Saves as <see cref="SaveRebuilt(RecordingFiles, string, ILogger)"/> does, with <paramref name="write"/> as the step
    /// that writes the file once it is made, so that a test can have that step fail, as a full disk does.
    /// </summary>
    internal static string SaveRebuilt(RecordingFiles files, string markdown, ILogger logger, Action<FileStream, byte[]> write)
    {
        var bytes = Utf8NoBom.GetBytes(markdown);
        for (var copy = FirstFree(files, 1); ; copy = FirstFree(files, copy + 1))
        {
            var path = files.Transcript(copy);
            if (TryCreate(path, bytes, write, logger)) return path;

            logger.LogInformation("Copy {Copy} of the transcript of {Recording} was taken at that moment; saving the next one instead", copy, files.BaseName);
        }
    }

    /// <summary>
    /// ElevenLabs' answer of the recording's newest transcript: the .json of the highest copy that has one, counted as a
    /// number, or null when there is none. An answer kept as unread (<see cref="RecordingFiles.UnreadAnswer"/>) is never
    /// it: such an answer was set aside because it could not be read, or could not be saved in its place.
    /// </summary>
    public static string? NewestAnswer(RecordingFiles files) => Copies(files, ".json").LastOrDefault();

    /// <summary>
    /// The recording's transcripts on disk: the .md of every copy that has one, in the order of their numbers, made when
    /// the recording was transcribed or rebuilt later. Empty when its folder holds none, or is not there.
    /// </summary>
    public static IReadOnlyList<string> Transcripts(RecordingFiles files) => Copies(files, ".md");

    /// <summary>
    /// The recording's files of every copy that ends in <paramref name="extension"/> (.json for the answers, .md for the
    /// transcripts), in the order of their numbers, counted as numbers. Another recording's files, an answer kept as
    /// unread and other files whose names start the same way are left out.
    /// </summary>
    private static List<string> Copies(RecordingFiles files, string extension)
    {
        if (!Directory.Exists(files.Folder)) return [];

        var copy = new Regex($"^{Regex.Escape(files.BaseName)}(?: \\((?<copy>[0-9]{{1,9}})\\))?{Regex.Escape(extension)}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return Directory.EnumerateFiles(files.Folder, $"{files.BaseName}*{extension}")
            .Select(file => (File: file, Match: copy.Match(Path.GetFileName(file))))
            .Where(found => found.Match.Success)
            .OrderBy(found => found.Match.Groups["copy"].Success ? int.Parse(found.Match.Groups["copy"].Value, CultureInfo.InvariantCulture) : 1)
            .Select(found => found.File)
            .ToList();
    }

    /// <summary>The first copy from <paramref name="from"/> on that has neither a transcript nor an answer.</summary>
    private static int FirstFree(RecordingFiles files, int from)
    {
        var copy = from;
        while (File.Exists(files.Transcript(copy)) || File.Exists(files.Json(copy))) copy++;
        return copy;
    }

    /// <summary>
    /// Writes all of <paramref name="bytes"/> to <paramref name="file"/>, the step every save takes once a file is made.
    /// The file has no buffer of its own, so the text goes to Windows in one write, which reports any failure.
    /// </summary>
    private static void WriteAll(FileStream file, byte[] bytes) => file.Write(bytes);

    /// <summary>
    /// Creates <paramref name="path"/> and has <paramref name="write"/> put <paramref name="bytes"/> in it, or returns
    /// false when a file of that name is already there. A file that cannot be written in full is removed again, so that
    /// no half-written answer or transcript passes for a whole one, and the error that stopped the write is rethrown.
    /// </summary>
    private static bool TryCreate(string path, byte[] bytes, Action<FileStream, byte[]> write, ILogger logger)
    {
        FileStream file;
        try
        {
            // No buffer of its own (bufferSize 0), so that a write's failure is reported by the write itself.
            file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, bufferSize: 0);
        }
        catch (IOException ex) when (ex.HResult is FileExists or AlreadyExists)
        {
            // Deliberately not rethrown: the name is taken, by an earlier copy or by another ClassScribe at this moment,
            // which is no failure; the caller goes on to the next copy.
            return false;
        }

        try
        {
            using (file) write(file, bytes);
            return true;
        }
        catch
        {
            Remove(path, "which could not be written in full", logger);
            throw;
        }
    }

    /// <summary>
    /// Removes a file this class has just made, <paramref name="what"/> saying what it is. One that cannot be removed is
    /// left, with a warning that says so.
    /// </summary>
    private static void Remove(string path, string what, ILogger logger)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Deliberately not rethrown: what led here is what to report, a failed write or a transcript saved under the
            // next number; this file is only left over, and the warning names it.
            logger.LogWarning(ex, "Could not remove {Path}, {What}; it can be deleted by hand", path, what);
        }
    }
}
