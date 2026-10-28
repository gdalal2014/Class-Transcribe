using ClassScribe.Storage;

namespace ClassScribe.Transcription;

/// <summary>A glossary file as it was read: where it is, its lines, and the SHA-256 of its bytes.</summary>
/// <param name="Path">The file, as a full path.</param>
/// <param name="Lines">Its lines, read as UTF-8 (a byte-order mark passed over), in their order.</param>
/// <param name="Sha256">The SHA-256 of the file's bytes, in lowercase hexadecimal, which tells a changed file from the same one.</param>
public sealed record GlossaryText(string Path, IReadOnlyList<string> Lines, string Sha256);

/// <summary>
/// The port to the files of the glossary (architecture 8.2): where a value that names a glossary points, the glossary file's
/// text, and a recording's glossary record. What the lines mean is <see cref="KeytermRules"/>'s, and which glossary a run or a
/// transcript takes is the glossary's choice's.
/// </summary>
public interface IGlossaryFiles
{
    /// <summary>
    /// The file <paramref name="configured"/> names, as a full path: environment variables such as %APPDATA% expanded, quotes
    /// around it ignored, and a relative path taken from the folder of the settings in force. An empty value gives an empty
    /// path, which means no glossary.
    /// </summary>
    string Resolve(string configured);

    /// <summary>True when a file is at <paramref name="path"/>, which is not read: for the menu's header, which names a class whose glossary is not there.</summary>
    bool Exists(string path);

    /// <summary>The glossary at <paramref name="path"/>, read whole; null when there is no file there.</summary>
    /// <exception cref="IOException">The file is there but cannot be read.</exception>
    /// <exception cref="UnauthorizedAccessException">The file may not be read.</exception>
    GlossaryText? Read(string path);

    /// <summary>The glossary record of <paramref name="files"/> (see <see cref="RecordingFiles.Glossary"/>); null when it has none.</summary>
    /// <exception cref="InvalidDataException">The file is there but holds no glossary record.</exception>
    /// <exception cref="IOException">The file cannot be read.</exception>
    /// <exception cref="UnauthorizedAccessException">The file may not be read.</exception>
    GlossaryRecord? ReadRecord(RecordingFiles files);

    /// <summary>Writes <paramref name="record"/> whole as the glossary record of <paramref name="files"/>, replacing the one there.</summary>
    /// <exception cref="IOException">The file cannot be written.</exception>
    /// <exception cref="UnauthorizedAccessException">The file may not be written.</exception>
    void WriteRecord(RecordingFiles files, GlossaryRecord record);
}
