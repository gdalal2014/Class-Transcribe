using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClassScribe.Transcription;

/// <summary>
/// A recording's glossary record, &lt;base&gt;.glossary.json beside it (architecture 8.2, item 6): the glossary decided once,
/// when the recording started, which every later transcription of it reuses, and what each transcript sent. It is written by
/// the run at its start, in either home, and added to by each transcript; a recording made before it has none, and gets one
/// at its first transcription. It is read leniently, as run files are: a field this build does not know is passed over, and
/// kept when the record is written back.
/// </summary>
/// <param name="Version">The record's version: <see cref="CurrentVersion"/> when this build wrote it.</param>
/// <param name="Decided">The glossary decided when the recording started.</param>
/// <param name="Sent">What each transcript sent, in the order they were made.</param>
public sealed record GlossaryRecord(int Version, GlossaryDecision Decided, IReadOnlyList<GlossarySent> Sent)
{
    /// <summary>The version this build writes.</summary>
    public const int CurrentVersion = 1;

    /// <summary>
    /// Fields of the record this build does not know, as a later build wrote them, kept so that a record this build writes
    /// back loses none of them; null when there were none.
    /// </summary>
    [JsonExtensionData]
    // A setter, not init: the generated reader fills an init-only property through the constructor, where extension data
    // cannot go.
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>The glossary decided when a recording started.</summary>
/// <param name="Setting">The value that named it, as written where it came from, such as %APPDATA%\ClassScribe\keyterms-islamic-sciences.txt; empty, or none, for no glossary.</param>
/// <param name="Path">The file as a full path; empty for no glossary.</param>
/// <param name="From">Where the value came from, in words: ElevenLabs:KeytermsFile, the saved class "Mantiq", or transcribe --keyterms.</param>
/// <param name="Terms">How many terms the file gave then; 0 when it was not there, or for no glossary.</param>
/// <param name="Sha256">The file's SHA-256 then, in lowercase hexadecimal; null when it was not there, or for no glossary.</param>
/// <param name="Utc">When it was decided.</param>
public sealed record GlossaryDecision(string Setting, string Path, string From, int Terms, string? Sha256, DateTimeOffset Utc)
{
    /// <summary>Fields this build does not know, kept as they were written; null when there were none.</summary>
    [JsonExtensionData]
    // A setter, not init, as in GlossaryRecord.
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>What one transcript of the recording sent.</summary>
/// <param name="Copy">The transcript's copy: 1 for &lt;base&gt;.md, 2 for &lt;base&gt; (2).md and so on.</param>
/// <param name="Path">The glossary sent, as a full path; empty for none.</param>
/// <param name="Terms">How many terms went up.</param>
/// <param name="Sha256">The file's SHA-256 as sent; null for none.</param>
/// <param name="Utc">When the transcript was saved.</param>
/// <param name="From">
/// Where the glossary came from for this transcript, in words: as decided when the recording started, or transcribe
/// --keyterms; null in a record of a build that did not write it.
/// </param>
public sealed record GlossarySent(int Copy, string Path, int Terms, string? Sha256, DateTimeOffset Utc, string? From = null)
{
    /// <summary>Fields this build does not know, kept as they were written; null when there were none.</summary>
    [JsonExtensionData]
    // A setter, not init, as in GlossaryRecord.
    public Dictionary<string, JsonElement>? Extra { get; set; }
}
