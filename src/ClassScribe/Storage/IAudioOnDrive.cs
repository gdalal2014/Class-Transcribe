namespace ClassScribe.Storage;

/// <summary>What ClassScribe knows of recordings' audio kept on Google Drive alone, from what it remembers of its uploads.</summary>
public interface IAudioOnDrive
{
    /// <summary>The audio of <paramref name="files"/> that is on Drive, checked, and gone from this laptop; null when none of it is.</summary>
    AudioOnDrive? Find(RecordingFiles files);
}

/// <summary>Knows of no audio on Drive: for a build, or a test, without Google Drive.</summary>
public sealed class NoAudioOnDrive : IAudioOnDrive
{
    /// <summary>The one there is.</summary>
    public static NoAudioOnDrive Instance { get; } = new();

    /// <inheritdoc />
    public AudioOnDrive? Find(RecordingFiles files) => null;
}
