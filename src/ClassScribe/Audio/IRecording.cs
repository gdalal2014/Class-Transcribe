using ClassScribe.Commands;
using ClassScribe.Storage;

namespace ClassScribe.Audio;

/// <summary>
/// A recording as the record command and the recording loop drive it: from phase 2 one session for the class, from phase
/// 3 the class and the microphone on one clock. Each track has its own capture, files and failure handling, so a track
/// that fails never stops the other.
/// </summary>
public interface IRecording : IDisposable
{
    /// <summary>Where the recording is now: the offset it was started at plus what this run has recorded. A mark set now gets this position.</summary>
    TimeSpan Position { get; }

    /// <summary>Raised for something worth a note in the transcript, such as a long stretch of silence left out of the recording.</summary>
    event EventHandler<TimelineNote>? NoteWorthy;

    /// <summary>
    /// Starts recording into <paramref name="files"/> at <paramref name="offset"/>: the length the recording already has
    /// on disk when this run continues it, and zero for a new one. When there is nothing to record yet, the recording
    /// waits for it. Throws <see cref="NotSupportedException"/> when no track can be recorded in a format ClassScribe reads.
    /// </summary>
    void Start(RecordingFiles files, TimeSpan offset);

    /// <summary>Stops recording, finishes the files and says what this run recorded. Safe to call twice; the second call answers at once.</summary>
    RecordingSummary Stop();

    /// <summary>Pauses: nothing is recorded until <see cref="Resume"/>.</summary>
    void Pause();

    /// <summary>Records again after <see cref="Pause"/>.</summary>
    void Resume();

    /// <summary>
    /// Switches your microphone off or on (V), keeping its place on the clock. Returns whether it is on now, or null when
    /// the recording has no microphone, which a recording of the class alone never has.
    /// </summary>
    bool? SetMicrophone(bool on) => null;

    /// <summary>Takes a look at the recording now, for the checks made once a second.</summary>
    RecordingSnapshot GetSnapshot();

    /// <summary>
    /// Replaces the capture of <paramref name="track"/> at the next look, because it stopped handing over audio although
    /// it should; <paramref name="reason"/> says why, for the log.
    /// </summary>
    void Reopen(Track track, string reason);

    /// <summary>Brings the files on disk up to date now, as before the process ends.</summary>
    void Flush();
}

/// <summary>Makes the recording a run's source plan asks for.</summary>
public interface IRecordingFactory
{
    /// <summary>A recording, not started yet, of what <paramref name="source"/> names.</summary>
    IRecording Create(SourcePlan source);
}
