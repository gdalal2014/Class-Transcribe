using ClassScribe.Audio;
using ClassScribe.Desktop;
using ClassScribe.Storage;

namespace ClassScribe.Drive;

/// <summary>
/// The recordings' audio kept on Google Drive alone, as the upload manifests tell it (<see cref="IUploadManifests"/>): a
/// segment whose copy on Drive was checked against Drive's size and MD5, and which is no longer in the recording's folder,
/// whether ClassScribe freed it or it was deleted by hand. The length comes from what was measured when it went up. It asks
/// Google nothing. One for the process.
/// </summary>
public sealed class DriveCopies(IUploadManifests manifests, IRecordingFolder recordings, IFileProbe probe) : IAudioOnDrive
{
    /// <inheritdoc />
    public AudioOnDrive? Find(RecordingFiles files)
    {
        if (manifests.Read(files) is not { } manifest) return null;

        var audio = manifest.Files.Where(file => RecordingUpload.IsAudio(file.Name) && file.IsVerified).ToList();
        if (!audio.Any(file => !probe.FileExists(Path.Combine(files.Folder, file.Name)))) return null;

        var microphone = audio.Where(IsMicrophone).ToList();
        var mainSeconds = audio.Except(microphone).Sum(file => file.Seconds ?? 0);
        var microphoneSeconds = microphone.Sum(file => file.Seconds ?? 0);
        var allOfIt = recordings.FindSegments(files).Count == 0 && recordings.FindSegments(files, Track.Microphone).Count == 0;
        return new AudioOnDrive(TimeSpan.FromSeconds(Math.Max(mainSeconds, microphoneSeconds)), microphone.Count > 0, manifest.DriveLocation, manifest.DriveLink, allOfIt);
    }

    /// <summary>True for a segment of the microphone's track: a .mic.wav file.</summary>
    private static bool IsMicrophone(ManifestFile file) => file.Name.EndsWith(".mic.wav", StringComparison.OrdinalIgnoreCase);
}
