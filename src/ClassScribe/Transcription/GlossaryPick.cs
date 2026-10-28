using System.Text.Json.Serialization;
using ClassScribe.Runs;

namespace ClassScribe.Transcription;

/// <summary>
/// The glossary a run or a transcript takes, before it is read: the value that named it, the file, and where it came from.
/// A run's request carries it from the window to the recorder (see <see cref="RecordRequest.Glossary"/>), which has no saved
/// classes to decide by.
/// </summary>
/// <param name="Setting">The value as written where it came from, such as %APPDATA%\ClassScribe\keyterms-islamic-sciences.txt; empty, or none, for no glossary.</param>
/// <param name="Path">The file as a full path; empty for no glossary.</param>
/// <param name="From">Where the value came from, in words: ElevenLabs:KeytermsFile, the saved class "Mantiq", or transcribe --keyterms.</param>
/// <param name="ClassName">The saved class it came from, for the advice when its file is not there; null otherwise.</param>
/// <param name="Warning">What the start says about how it was decided, such as two classes of the course that disagree; null for nothing.</param>
public sealed record GlossaryPick(string Setting, string Path, string From,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ClassName = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Warning = null);

/// <summary>What a transcript sends, and what its entry in the recording's record says (see <see cref="GlossaryChoice.Sent"/>).</summary>
/// <param name="Path">The glossary's file as a full path; empty for none.</param>
/// <param name="From">Where it came from, in words: as decided when the recording started, or the step that decided it now.</param>
/// <param name="Terms">The terms that go up.</param>
/// <param name="Sha256">The file's SHA-256 as read; null for none, or a file that could not be read.</param>
/// <param name="Decided">The decision the recording's record holds, or would hold when it is written; null when its record cannot be read, which is then left as it is.</param>
public sealed record GlossaryUse(string Path, string From, IReadOnlyList<string> Terms, string? Sha256, GlossaryDecision? Decided);
