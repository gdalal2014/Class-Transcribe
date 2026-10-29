namespace ClassScribe.YouTube;

/// <summary>A video's source file, &lt;base&gt;.source.json beside its recording (architecture 4.3; see <see cref="VideoSource"/>), read and written whole.</summary>
public interface IVideoSources
{
    /// <summary>
    /// Writes <paramref name="source"/> whole as the file at <paramref name="path"/>: into a file of its own in the same folder,
    /// flushed to the disk, then moved into its place in one step, so that a reader finds the whole old file or the whole new
    /// one. Nothing of a write that failed is left beside it; the failure is thrown.
    /// </summary>
    void Write(string path, VideoSource source);

    /// <summary>
    /// The source file at <paramref name="path"/>; null when there is none. A file that is not a video's source, or lacks what
    /// every source has (its kind, its video's ID, its link, its title and how it was obtained), is refused with an
    /// <see cref="InvalidDataException"/> naming the file. A field this build does not know is passed over.
    /// </summary>
    /// <exception cref="InvalidDataException">The file is not a video's source.</exception>
    /// <exception cref="IOException">The file cannot be read.</exception>
    VideoSource? Read(string path);
}
