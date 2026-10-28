using ClassScribe.Commands;

namespace ClassScribe.Recorder;

/// <summary>What a window hands the recorder it starts, as the recorder's host is built from it.</summary>
/// <param name="RunId">The run (see <see cref="RunIds"/>).</param>
/// <param name="Request">What to record.</param>
/// <param name="Settings">The settings to record with, by name, as the window checked them: never the API key, which only the environment carries.</param>
public sealed record RecorderStart(string RunId, RecordRequest Request, IReadOnlyDictionary<string, string?> Settings);
