using ClassScribe.Storage;

namespace ClassScribe.Audio;

/// <summary>
/// Opens a track's segment files for writing (architecture 4.3): the recording's folder made, the next free part of a track
/// opened with its header, an empty segment deleted, and what a segment holds on disk. It is the one way a recording
/// session reaches the disk; the WAV writer of Infrastructure implements it.
/// </summary>
public interface ITrackWriterFactory
{
    /// <summary>Makes <paramref name="folder"/>, the recording's, when it is not there yet.</summary>
    void CreateFolder(string folder);

    /// <summary>
    /// Opens the next free segment of <paramref name="track"/> of <paramref name="files"/>, from part
    /// <paramref name="fromNumber"/> on: a part whose file is already there is never overwritten, so its number is skipped.
    /// The file is a valid, empty WAV at <paramref name="sampleRate"/> from its first moment.
    /// </summary>
    ITrackWriter Open(RecordingFiles files, Track track, int fromNumber, int sampleRate);

    /// <summary>Deletes the segment file at <paramref name="path"/>, which no audio reached; the file system's error goes to the caller.</summary>
    void Delete(string path);

    /// <summary>The length in bytes of the segment file at <paramref name="path"/>, as the file system reports it.</summary>
    /// <exception cref="System.IO.FileNotFoundException">The file is not there.</exception>
    long LengthOf(string path);
}

/// <summary>
/// One open segment of a track: mono 16-bit PCM written as it comes, each write handed to the file system at once, with
/// the header rewritten and everything pushed to the disk at each flush, so that the file stays a valid WAV while recording.
/// Disposing it finalises the header and closes the file.
/// </summary>
public interface ITrackWriter : IDisposable
{
    /// <summary>Full path of the segment file.</summary>
    string Path { get; }

    /// <summary>Sample rate of the audio in the file.</summary>
    int SampleRate { get; }

    /// <summary>Bytes of audio written so far, every one of them accepted by the file system.</summary>
    long DataBytes { get; }

    /// <summary>Appends mono 16-bit PCM samples, handing them to the file system at once. A write that fails is not counted.</summary>
    void Write(ReadOnlySpan<byte> mono16);

    /// <summary>
    /// Rewrites the header with the current sizes and pushes everything to disk. When that fails, the writer is left as it
    /// was: the next audio still goes after the audio already written, and the next flush tries the header again.
    /// </summary>
    void Flush();
}
