namespace ClassScribe.YouTube;

/// <summary>
/// A batch's folders (architecture 4.3; design 2.12), every one derived from ClassScribe's own folder, never read from a
/// setting: the feature's folder, the staging folder and each video's own staging folder in it, made and deleted here, so
/// that the batch touches no folder itself.
/// </summary>
public interface IYouTubeFolders
{
    /// <summary>The feature's folder.</summary>
    string Root { get; }

    /// <summary>The staging folder, which holds each video's read and download until it is a recording.</summary>
    string Staging { get; }

    /// <summary>The staging folder of video <paramref name="videoId"/>. Anything but a video's ID is refused before it names a folder.</summary>
    string StagingOf(string videoId);

    /// <summary>Makes the feature's folder, if it is not there.</summary>
    void MakeRoot();

    /// <summary>Makes the staging folder of video <paramref name="videoId"/>, if it is not there, and returns it.</summary>
    string MakeStaging(string videoId);

    /// <summary>Deletes the staging folder of video <paramref name="videoId"/> with everything in it; one that cannot be deleted now is the file system's error.</summary>
    void DeleteStaging(string videoId);
}
