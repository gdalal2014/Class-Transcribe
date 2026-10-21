using System.Text.Json;
using System.Text.Json.Serialization;
using ClassScribe.Commands;

namespace ClassScribe.Recorder;

/// <summary>Where a run records.</summary>
public enum RunHome
{
    /// <summary>In a separate recorder in the background: closing a window does not stop it.</summary>
    Separate,

    /// <summary>In the window that started it: closing that window ends the recording, and the audio so far is kept.</summary>
    InWindow
}

/// <summary>Where a run is, as its run file says.</summary>
public enum RunState
{
    /// <summary>The recorder has started and not begun to record yet.</summary>
    Starting,

    /// <summary>Recording. A run file in this state whose process is gone ended unexpectedly.</summary>
    Recording,

    /// <summary>Stopping: closing the capture and finishing the files. A run file in this state whose process is gone ended unexpectedly.</summary>
    Stopping,

    /// <summary>The recording is complete and being transcribed. A run file in this state whose process is gone had its transcription interrupted.</summary>
    Transcribing,

    /// <summary>Over; the outcome says how it ended.</summary>
    Finished,

    /// <summary>Not started, because another recording was running.</summary>
    Refused,

    /// <summary>The recorder failed before it could record; the outcome says why.</summary>
    Failed
}

/// <summary>How a run ended.</summary>
public enum OutcomeKind
{
    /// <summary>Recorded and transcribed: the transcript is saved.</summary>
    Transcribed,

    /// <summary>Recorded, without a transcript, as asked (record-only, menu choice 1).</summary>
    Recorded,

    /// <summary>Nothing audible arrived, so there is nothing to transcribe.</summary>
    NothingRecorded,

    /// <summary>Recorded, but the transcript failed; the audio is kept, and the command to try again is given.</summary>
    TranscriptFailed,

    /// <summary>The transcription was cancelled with Ctrl+C; the audio is kept.</summary>
    Cancelled,

    /// <summary>The recorder failed; the message says why.</summary>
    RecorderFailed,

    /// <summary>Not started, because another recording was running.</summary>
    Refused
}

/// <summary>How a run ended, as the window showing it, or the next start of ClassScribe, reports it.</summary>
/// <param name="Kind">How it ended.</param>
/// <param name="ExitCode">The exit code the window gives for it: 0 when the recording or its transcript was saved, 1 when it failed, 130 when cancelled.</param>
/// <param name="Message">What happened, in words, as Gulzar reads it.</param>
/// <param name="TranscriptPath">The transcript, when one was saved; null otherwise.</param>
/// <param name="Folder">The recording's folder, when there is one; null otherwise.</param>
/// <param name="RetryCommand">The command that tries again, such as ClassScribe transcribe and the recording's file; null when there is nothing to try again.</param>
public sealed record RunOutcome(OutcomeKind Kind, int ExitCode, string Message, string? TranscriptPath, string? Folder, string? RetryCommand);

/// <summary>
/// A run file: what one run is doing, kept in the runs folder under %LOCALAPPDATA%\ClassScribe and named after the run's
/// id. The process that records the run writes it while the run lasts, and every window reads it; once the run is over, a
/// window marks it seen. While the run records it marks the recording as in progress, so one found recording with its
/// process gone ended unexpectedly; a heartbeat every 10 seconds says how far it got; at the end it keeps the outcome
/// until a window has shown it.
/// </summary>
/// <param name="Version">The version of the run file's layout (see <see cref="CurrentVersion"/>).</param>
/// <param name="RunId">The run's id (see <see cref="RunIds"/>).</param>
/// <param name="ProcessId">The process that records the run.</param>
/// <param name="ProcessStartedUtc">When that process started, so that another process given the same id later is not taken for it.</param>
/// <param name="ExePath">The ClassScribe.exe that records the run.</param>
/// <param name="Build">Which build of ClassScribe that is.</param>
/// <param name="Protocol">The version of the protocol its recorder speaks to windows.</param>
/// <param name="Pipe">The name of the pipe windows connect to; null for a run in a window, which has none.</param>
/// <param name="Home">Where the run records.</param>
/// <param name="Request">The request the run was started with.</param>
/// <param name="BaseName">The recording's base name, such as Kalam_2026-10-15_23-00-02Z, once its files are made; null before.</param>
/// <param name="Folder">The recording's folder, once its files are made; null before.</param>
/// <param name="State">Where the run is.</param>
/// <param name="StartedUtc">When the run started.</param>
/// <param name="HeartbeatUtc">When the run file was last brought up to date: every 10 seconds while the run lasts.</param>
/// <param name="Position">Where the recording was at the last heartbeat.</param>
/// <param name="UploadPercent">How much of the upload was done at the last heartbeat, from 0 to 100; null when not uploading.</param>
/// <param name="EndedBy">What ended the recording, as <see cref="RunEndCause"/> names it; null while nothing has.</param>
/// <param name="Outcome">How the run ended; null while it lasts.</param>
/// <param name="Seen">True once a window has shown the outcome, so that the next start does not report it again.</param>
public sealed record RunRecord(int Version, string RunId, int ProcessId, DateTimeOffset ProcessStartedUtc, string ExePath, string Build,
    int Protocol, string? Pipe, RunHome Home, RecordRequest Request, string? BaseName, string? Folder, RunState State,
    DateTimeOffset StartedUtc, DateTimeOffset HeartbeatUtc, TimeSpan Position, int? UploadPercent, string? EndedBy,
    RunOutcome? Outcome, bool Seen)
{
    /// <summary>The version of the run file's layout this build writes.</summary>
    public const int CurrentVersion = 1;

    /// <summary>
    /// Fields of the run file this build does not know, as a later build wrote them, kept so that marking a run seen, which
    /// writes the file back, loses none of them (amendment 2); null when there were none, so that records still compare equal.
    /// </summary>
    [JsonExtensionData]
    // A setter, not init: the generated reader fills an init-only property through the constructor, where extension data
    // cannot go.
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>
/// A request file: what a window hands the recorder it starts, written before the recorder starts and deleted by the
/// recorder once read. It holds the request, and the settings the window has just checked, which the recorder records with
/// instead of reading appsettings.json. It never holds the API key, which only the environment carries.
/// </summary>
/// <param name="Version">The version of the request file's layout (see <see cref="CurrentVersion"/>).</param>
/// <param name="Request">The run to record.</param>
/// <param name="Settings">The settings to record with, by setting name, such as Recording:FlushSeconds.</param>
public sealed record RunRequestFile(int Version, RecordRequest Request, IReadOnlyDictionary<string, string?> Settings)
{
    /// <summary>The version of the request file's layout this build writes.</summary>
    public const int CurrentVersion = 1;
}

/// <summary>What ended a recording, as a run file's <see cref="RunRecord.EndedBy"/> names it.</summary>
public static class RunEndCause
{
    /// <summary>Gulzar stopped it: Q twice, Ctrl+C twice, or the stop asked for from another window.</summary>
    public const string Q = "Q";

    /// <summary>The minutes given with --minutes ran out.</summary>
    public const string Minutes = "Minutes";

    /// <summary>The safety stop, after Recording:StopAfterHours.</summary>
    public const string SafetyStop = "SafetyStop";

    /// <summary>Windows shut down, restarted or signed out.</summary>
    public const string Windows = "Windows";

    /// <summary>The window of a run recording in that window was closed.</summary>
    public const string WindowClosed = "WindowClosed";

    /// <summary>A failure ended it.</summary>
    public const string Error = "Error";

    /// <summary>A YouTube video's recording: its window was closed, as Gulzar closing it does (design 4.4, rule 1). Its end is confirmed.</summary>
    public const string VideoClosed = "VideoClosed";

    /// <summary>A YouTube video's recording: its window moved on to the next video, as YouTube's Autoplay does once a video ends (rule 2). Confirmed.</summary>
    public const string VideoEnded = "VideoEnded";

    /// <summary>A YouTube video's recording gave way to a class that started (rule 4, the yield). Not confirmed: it may be cut short.</summary>
    public const string ClassStarted = "ClassStarted";

    /// <summary>A YouTube video's recording heard nothing for 2 minutes after sound (rule 5). Not confirmed: a paused video sounds the same.</summary>
    public const string VideoSilent = "VideoSilent";

    /// <summary>A YouTube video's window played nothing in its first 5 minutes (rule 6). Nothing was recorded.</summary>
    public const string NothingPlayed = "NothingPlayed";

    /// <summary>A YouTube video's recording reached YouTube:LongestVideoHours (rule 7). Not confirmed: the video may go on.</summary>
    public const string VideoLimit = "VideoLimit";
}
