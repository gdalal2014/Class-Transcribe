using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ClassScribe.Transcription;
using Microsoft.Extensions.Logging;

namespace ClassScribe.Storage;

/// <summary>
/// The numbered copies of a recording's transcript, the .md of each copy, and of ElevenLabs' answer, the .json beside it
/// (see <see cref="RecordingFiles.Transcript"/> and <see cref="RecordingFiles.Json"/>): the adapter of
/// <see cref="ITranscriptCopies"/>. A new copy takes the first number that has neither file, and claims each file by
/// creating it only when no file of that name is there (<see cref="FileMode.CreateNew"/>), so nothing is ever overwritten,
/// not even by another ClassScribe saving a transcript of the same recording at the same moment: whichever comes second
/// takes the next number. Both files are written in UTF-8 without a byte order mark.
/// </summary>
public sealed class TranscriptCopies : ITranscriptCopies
{
    /// <summary>ERROR_FILE_EXISTS as an HRESULT: what creating a file reports when a file of that name is there.</summary>
    private const int FileExists = unchecked((int)0x80070050);

    /// <summary>ERROR_ALREADY_EXISTS as an HRESULT, which Windows reports instead for some names.</summary>
    private const int AlreadyExists = unchecked((int)0x800700B7);

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <inheritdoc />
    public TranscriptFiles Save(RecordingFiles files, string json, Func<string> transcript, ILogger logger) =>
        Save(files, json, transcript, logger, WriteAll);

    /// <summary>
    /// Saves as <see cref="Save(RecordingFiles, string, Func{string}, ILogger)"/> does, with <paramref name="write"/> as
    /// the step that writes each file once it is made, so that a test can have that step fail, as a full disk does.
    /// </summary>
    internal TranscriptFiles Save(RecordingFiles files, string json, Func<string> transcript, ILogger logger, Action<FileStream, byte[]> write)
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
                ex.Data[ITranscriptCopies.SavedAnswerKey] = answerPath;
                throw;
            }

            logger.LogInformation("The transcript's name {Path} was taken at that moment; saving the transcript and its answer as the next copy instead", transcriptPath);
            Remove(answerPath, "a second copy of ElevenLabs' answer, which is saved with its transcript under the next number", logger);
        }
    }

    /// <inheritdoc />
    public string SaveRebuilt(RecordingFiles files, string markdown, ILogger logger) => SaveRebuilt(files, markdown, logger, WriteAll);

    /// <summary>
    /// Saves as <see cref="SaveRebuilt(RecordingFiles, string, ILogger)"/> does, with <paramref name="write"/> as the step
    /// that writes the file once it is made, so that a test can have that step fail, as a full disk does.
    /// </summary>
    internal string SaveRebuilt(RecordingFiles files, string markdown, ILogger logger, Action<FileStream, byte[]> write)
    {
        var bytes = Utf8NoBom.GetBytes(markdown);
        for (var copy = FirstFree(files, 1); ; copy = FirstFree(files, copy + 1))
        {
            var path = files.Transcript(copy);
            if (TryCreate(path, bytes, write, logger)) return path;

            logger.LogInformation("Copy {Copy} of the transcript of {Recording} was taken at that moment; saving the next one instead", copy, files.BaseName);
        }
    }

    /// <inheritdoc />
    public string? NewestAnswer(RecordingFiles files) => Copies(files, ".json").LastOrDefault();

    /// <inheritdoc />
    public IReadOnlyList<string> Transcripts(RecordingFiles files) => Copies(files, ".md");

    /// <inheritdoc />
    public string? LineUnderTitle(string transcriptPath)
    {
        using var reader = new StreamReader(new FileStream(transcriptPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
        reader.ReadLine();   // the title
        string? line;
        do line = reader.ReadLine(); while (line is { Length: 0 });
        return line;
    }

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
