namespace ClassScribe.Settings;

/// <summary>When the screen is kept on while ClassScribe records and uploads (see <see cref="RecordingOptions.KeepScreenOn"/>).</summary>
public enum KeepScreenOn
{
    /// <summary>
    /// Only while the laptop runs on battery. There a laptop with Modern Standby goes into standby once the screen
    /// turns off, whatever a program asks, so keeping the screen on is what keeps it recording. On mains, keeping
    /// the laptop awake is enough, and the screen may turn off.
    /// </summary>
    OnBattery,

    /// <summary>Always, on battery and on mains.</summary>
    Always,

    /// <summary>Never. On battery Windows may then go into standby a few minutes after the last key press, and stop the recording.</summary>
    Never
}

/// <summary>
/// When ClassScribe brings back a window by itself while it records in the background and no window shows the recording
/// (see <see cref="RecordingOptions.ReopenWindow"/>). The window comes back minimized, without taking the focus, and
/// ignores every key until Enter is pressed in it.
/// </summary>
public enum ReopenWindow
{
    /// <summary>When the last window showing the recording is closed, and again whenever something needs attention.</summary>
    OnCloseAndProblems,

    /// <summary>Only when something needs attention, such as nothing being recorded, or the transcript failing.</summary>
    OnProblems,

    /// <summary>Never. The next start of ClassScribe reports what happened.</summary>
    Never
}

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

    /// <summary>
    /// When the screen is kept on while recording and uploading: <see cref="Settings.KeepScreenOn.OnBattery"/> (the
    /// default), <see cref="Settings.KeepScreenOn.Always"/> or <see cref="Settings.KeepScreenOn.Never"/>. The names are
    /// taken in any case; a number is refused.
    /// </summary>
    public KeepScreenOn KeepScreenOn { get; init; } = KeepScreenOn.OnBattery;

    /// <summary>
    /// Record in a separate ClassScribe process in the background, so that closing the window, or a key typed into it by
    /// mistake, does not end the class. False records in the window, as before: closing it then ends the recording, and
    /// the audio so far is kept.
    /// </summary>
    public bool SeparateProcess { get; init; } = true;

    /// <summary>
    /// When ClassScribe brings back a minimized window by itself while it records in the background and no window shows
    /// the recording: <see cref="Settings.ReopenWindow.OnCloseAndProblems"/> (the default),
    /// <see cref="Settings.ReopenWindow.OnProblems"/> or <see cref="Settings.ReopenWindow.Never"/>. The names are taken in
    /// any case; a number is refused.
    /// </summary>
    public ReopenWindow ReopenWindow { get; init; } = ReopenWindow.OnCloseAndProblems;

    /// <summary>
    /// While a recording or its upload runs, a restart, shutdown or sign-out started from Windows asks first, and names
    /// ClassScribe as the reason. A shutdown that Windows forces is not held up.
    /// </summary>
    public bool BlockShutdown { get; init; } = true;

    /// <summary>
    /// Offer to continue a recording of the same name as its next part when it stopped without a transcript at most this
    /// many minutes ago, as after a crash or a restart. 0 never offers; at most 1440, a day.
    /// </summary>
    public int ContinueWithinMinutes { get; init; } = 60;

    /// <summary>
    /// The safety stop: a recording stops by itself after this many hours, with a warning 15 minutes before. ElevenLabs
    /// takes at most 10 hours of audio in one file, and a recording left running by mistake must not run all night. 0
    /// turns the stop off; at most 9.5.
    /// </summary>
    public double StopAfterHours { get; init; } = 9;
}
