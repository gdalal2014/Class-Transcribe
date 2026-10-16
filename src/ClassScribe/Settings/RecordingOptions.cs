namespace ClassScribe.Settings;

/// <summary>Settings that control how the class audio is captured and stored.</summary>
public sealed class RecordingOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Recording";

    /// <summary>
    /// Folder that receives one sub-folder per class and per transcribed file. Environment variables
    /// such as %USERPROFILE% are expanded. Empty means a ClassScribe folder in the user profile.
    /// </summary>
    public string OutputFolder { get; init; } = "";

    /// <summary>
    /// Part of the name of the output device to record, such as "Pixel Buds". Empty means whichever
    /// device the class is playing through.
    /// </summary>
    public string Device { get; init; } = "";

    /// <summary>
    /// The app whose sound to record, by the name Windows gives its process, such as "chrome" or
    /// "msedge". Its sound alone is recorded, whatever device it plays through. Empty means record a
    /// device instead.
    /// </summary>
    public string App { get; init; } = "";

    /// <summary>How often the WAV header is rewritten, so a crash loses at most this much audio.</summary>
    public int FlushSeconds { get; init; } = 5;

    /// <summary>Warn when nothing audible has been captured for this long.</summary>
    public int SilenceWarningSeconds { get; init; } = 60;

    /// <summary>How often a progress line is logged while recording.</summary>
    public int StatusIntervalSeconds { get; init; } = 60;

    /// <summary>A new segment file is started once the current one reaches this size.</summary>
    public int MaxSegmentMegabytes { get; init; } = 1500;

    /// <summary>Keep the 16 kHz upload copy after a successful transcript.</summary>
    public bool KeepUploadFile { get; init; }

    /// <summary>Open the folder in Explorer when the recording or the transcript is ready.</summary>
    public bool OpenFolderWhenDone { get; init; } = true;

    /// <summary>Open the transcript in the app Windows uses for text files when it is ready.</summary>
    public bool OpenTranscriptWhenDone { get; init; } = true;
}
