using ClassScribe.Storage;

namespace ClassScribe.Audio;

/// <summary>
/// The adapter of <see cref="ITrackWriterFactory"/> over <see cref="WavSegmentWriter"/>: the recording's folder is made
/// with the file system, the next free part is the first whose file is not there, and each segment is the writer's WAV
/// file. It holds nothing of its own.
/// </summary>
public sealed class WavTrackWriterFactory : ITrackWriterFactory
{
    /// <inheritdoc />
    public void CreateFolder(string folder) => Directory.CreateDirectory(folder);

    /// <inheritdoc />
    public ITrackWriter Open(RecordingFiles files, Track track, int fromNumber, int sampleRate)
    {
        // The next number is normally free. A file that is already there is never overwritten, so its number is skipped.
        var number = fromNumber;
        string path;
        while (File.Exists(path = files.Segment(number, track))) number++;

        return new WavSegmentWriter(path, sampleRate);
    }

    /// <inheritdoc />
    public void Delete(string path) => File.Delete(path);

    /// <inheritdoc />
    public long LengthOf(string path) => new FileInfo(path).Length;
}
