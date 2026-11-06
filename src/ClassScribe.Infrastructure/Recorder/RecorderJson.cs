using System.Text.Json;
using System.Text.Json.Serialization;
using ClassScribe.Runs;

namespace ClassScribe.Recorder;

/// <summary>
/// JSON for what a run writes down and hands on, generated at compile time: run files, request files, requests and the
/// recorder's state. Names are written in camelCase, and choices and states by name rather than by number, so that a file
/// means the same to a build whose lists are ordered differently and a person can read it. Each kind of class source is
/// named by "kind", which is found wherever it stands among the source's fields. A field the reader does not know is
/// passed over, so that an older build reads what a newer one wrote. The JSON is written on one line.
/// </summary>
/// <remarks>
/// A file that cannot be read throws a <see cref="JsonException"/>: text that is not JSON, or a kind of class source, a
/// choice or a state this build does not know. A class source without its "kind" throws a
/// <see cref="NotSupportedException"/> instead, so a reader treats both as a file that cannot be read. Not everything
/// that is wrong throws: a file holding only null reads as null, and a field that is missing, or null where its type
/// says it never is, reads as null (or 0, or false), as a field added in a later build does when that build reads an
/// older file. A reader checks the fields it relies on, such as a run file's request, before it uses them.
/// </remarks>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, UseStringEnumConverter = true, AllowOutOfOrderMetadataProperties = true)]
[JsonSerializable(typeof(RecordRequest))]
[JsonSerializable(typeof(RunRecord))]
[JsonSerializable(typeof(RunRequestFile))]
[JsonSerializable(typeof(RecorderState))]
public sealed partial class RecorderJson : JsonSerializerContext;
