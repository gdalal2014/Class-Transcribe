using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClassScribe.Link;

/// <summary>
/// JSON for the link's messages, generated at compile time. Names are written in camelCase, and choices and states by name,
/// as in the run files (see <see cref="Recorder.RecorderJson"/>), so that both ends agree whatever order a build lists them
/// in. A field the reader does not know is passed over, so that a later build can add one without a new protocol. Unlike
/// the run files, a message is read strictly: a field it needs that is missing, or null where it may not be, makes it
/// unreadable, and the reader passes the message over rather than act on half of it. A field added later must therefore
/// have a default, as <see cref="Hello.Owner"/> does, so that a message from an earlier build still reads.
/// </summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, UseStringEnumConverter = true, AllowOutOfOrderMetadataProperties = true,
    RespectNullableAnnotations = true, RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(Envelope))]
[JsonSerializable(typeof(Hello))]
[JsonSerializable(typeof(Welcome))]
[JsonSerializable(typeof(Refused))]
[JsonSerializable(typeof(LogLine))]
[JsonSerializable(typeof(StateUpdate))]
[JsonSerializable(typeof(End))]
[JsonSerializable(typeof(KeyPress))]
[JsonSerializable(typeof(Bye))]
public sealed partial class LinkJson : JsonSerializerContext;
