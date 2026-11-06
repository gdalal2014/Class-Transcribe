using System.Text.Json;
using System.Text.Json.Serialization;
using ClassScribe.Audio.Timeline;

namespace ClassScribe.Camera;

/// <summary>
/// JSON for the camera process's lines, generated at compile time: camelCase names, choices by name, read strictly as the
/// link reads its messages (a field a later build adds needs a default), unknown fields passed over.
/// </summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, UseStringEnumConverter = true, RespectNullableAnnotations = true, RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(CameraJob))]
[JsonSerializable(typeof(CameraStop))]
[JsonSerializable(typeof(CameraOpened))]
[JsonSerializable(typeof(CameraStatus))]
[JsonSerializable(typeof(CameraProblem))]
[JsonSerializable(typeof(CameraPiece))]
[JsonSerializable(typeof(CameraProgress))]
[JsonSerializable(typeof(CameraMade))]
[JsonSerializable(typeof(CameraFailed))]
[JsonSerializable(typeof(CameraLog))]
[JsonSerializable(typeof(CameraEmpty))]
[JsonSerializable(typeof(ClockState))]
public sealed partial class CameraJson : JsonSerializerContext;
