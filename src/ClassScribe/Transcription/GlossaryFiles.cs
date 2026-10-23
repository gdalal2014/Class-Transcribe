using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClassScribe.Settings;
using ClassScribe.Storage;

namespace ClassScribe.Transcription;

/// <summary>
/// The glossary's files on this computer (see <see cref="IGlossaryFiles"/>): a value that names a glossary resolved through
/// the settings' places (see <see cref="SettingsPlaces.ResolveFile"/>), the glossary read whole with the SHA-256 of its bytes,
/// and a recording's glossary record read and written through <see cref="GlossaryJson"/>, whole, so that a reader finds the
/// whole old record or the whole new one. Nothing here is logged: what a read or a failure means is said by whoever asked.
/// </summary>
/// <param name="places">Where the settings in force are read from.</param>
public sealed class GlossaryFiles(SettingsPlaces places) : IGlossaryFiles
{
    /// <inheritdoc />
    public string Resolve(string configured) => places.ResolveFile(configured);

    /// <inheritdoc />
    public GlossaryText? Read(string path)
    {
        if (path.Length == 0 || !File.Exists(path)) return null;

        var bytes = File.ReadAllBytes(path);
        var lines = new List<string>();
        using (var reader = new StreamReader(new MemoryStream(bytes), Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
        {
            while (reader.ReadLine() is { } line) lines.Add(line);
        }

        return new GlossaryText(path, lines, Convert.ToHexStringLower(SHA256.HashData(bytes)));
    }

    /// <inheritdoc />
    public GlossaryRecord? ReadRecord(RecordingFiles files)
    {
        var path = files.Glossary;
        if (!File.Exists(path)) return null;

        GlossaryRecord? record;
        try
        {
            record = JsonSerializer.Deserialize(File.ReadAllBytes(path), GlossaryJson.Files.GlossaryRecord);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"{path} is not a glossary record: {ex.Message}", ex);
        }

        if (record?.Decided is not { } decided)
            throw new InvalidDataException($"{path} is not a glossary record: it does not say which glossary was decided.");

        // A field missing from the file reads as null, whatever its type says; each is read as empty instead.
        return record with
        {
            Decided = decided with { Setting = decided.Setting ?? "", Path = decided.Path ?? "", From = decided.From ?? "" },
            Sent = [.. (record.Sent ?? []).OfType<GlossarySent>().Select(sent => sent with { Path = sent.Path ?? "" })]
        };
    }

    /// <inheritdoc />
    public void WriteRecord(RecordingFiles files, GlossaryRecord record)
    {
        var path = files.Glossary;
        var unfinished = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var file = new FileStream(unfinished, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(file, record, GlossaryJson.Files.GlossaryRecord);
                file.Flush(flushToDisk: true);
            }

            File.Move(unfinished, path, overwrite: true);
        }
        catch
        {
            if (File.Exists(unfinished)) File.Delete(unfinished);
            throw;
        }
    }
}

/// <summary>
/// JSON for a recording's glossary record, generated at compile time: camelCase names, written indented and with every
/// character as it is (a path with Arabic in it stays readable), and a field the reader does not know passed over.
/// </summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(GlossaryRecord))]
public sealed partial class GlossaryJson : JsonSerializerContext
{
    /// <summary>The context with the options the record is written with.</summary>
    public static GlossaryJson Files { get; } = new(new JsonSerializerOptions(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    });
}
