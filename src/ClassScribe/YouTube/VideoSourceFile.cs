using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClassScribe.YouTube;

/// <summary>
/// JSON for a video's source file, generated at compile time: camelCase names, written indented and with every character as
/// it is (an Arabic title stays readable), and a field the reader does not know passed over. YouTube's oEmbed answer is read
/// with it too, its fields named as YouTube writes them.
/// </summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(VideoSource))]
[JsonSerializable(typeof(ConversionNote))]
[JsonSerializable(typeof(OEmbedAnswer))]
public sealed partial class YouTubeJson : JsonSerializerContext
{
    /// <summary>The context with the options the source file is written with.</summary>
    public static YouTubeJson Files { get; } = new(new JsonSerializerOptions(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    });
}

/// <summary>Reads and writes a video's source file (see <see cref="VideoSource"/>).</summary>
public static class VideoSourceFile
{
    /// <summary>
    /// Writes <paramref name="source"/> whole as the file at <paramref name="path"/>: into a file of its own in the same folder,
    /// flushed to the disk, then moved into its place in one step, so that a reader finds the whole old file or the whole new
    /// one. Nothing of a write that failed is left beside it; the failure is thrown.
    /// </summary>
    public static void Write(string path, VideoSource source)
    {
        var unfinished = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var file = new FileStream(unfinished, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(file, source, YouTubeJson.Files.VideoSource);
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

    /// <summary>
    /// The source file at <paramref name="path"/>; null when there is none. A file that is not a video's source, or lacks what
    /// every source has (its kind, its video's ID, its link, its title and how it was obtained), is refused with an
    /// <see cref="InvalidDataException"/> naming the file. A field this build does not know is passed over.
    /// </summary>
    /// <exception cref="InvalidDataException">The file is not a video's source.</exception>
    /// <exception cref="IOException">The file cannot be read.</exception>
    public static VideoSource? Read(string path)
    {
        if (!File.Exists(path)) return null;

        VideoSource? source;
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            source = JsonSerializer.Deserialize(file, YouTubeJson.Files.VideoSource);
        }
        catch (JsonException ex)
        {
            // Handed on as what it means here, a source file that cannot be read, with the reader's own failure inside.
            throw new InvalidDataException($"The video's source file {path} cannot be read: {ex.Message}", ex);
        }

        return source is { Kind: VideoSource.YouTubeKind, VideoId: not null, Url: not null, Title: not null, Obtained: not null }
            ? source
            : throw new InvalidDataException($"The video's source file {path} cannot be read: it is not the source of a YouTube video.");
    }
}
