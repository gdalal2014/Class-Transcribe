using ClassScribe.Runs;
using ClassScribe.Storage;

namespace ClassScribe.Recorder;

/// <summary>
/// One run's file, as the process that records the run keeps it up to date (architecture 4.3; given by
/// <see cref="IRunJournal.Begin"/>): recording into its files (the mark that a recording is under way, so that a run whose
/// process is gone is known to have ended unexpectedly), a heartbeat every 10 seconds, the state as it moves on, what ended
/// the recording (taken back when that end did not come), and the outcome. Each write brings the heartbeat's time up to
/// date and writes the file whole, with the journal's retries. A write that fails is reported once and goes no further,
/// since a recording must never end because its run file cannot be written; what it held goes with the next write that
/// can be made, and letting the handle go is a last chance for it. Once the run has ended, nothing more changes; once the
/// handle is let go, nothing more is written, and a run let go without an outcome keeps its file as it last was. It is safe
/// to use from several threads. One run has one handle, which every part of the run that writes shares.
/// </summary>
public interface IRunHandle : IDisposable
{
    /// <summary>The run file.</summary>
    string FilePath { get; }

    /// <summary>The run as the handle last wrote it, or tried to.</summary>
    RunRecord Current { get; }

    /// <summary>The run records into <paramref name="files"/>: from now on, should its process be gone, it ended unexpectedly.</summary>
    void Recording(RecordingFiles files);

    /// <summary>The heartbeat, every 10 seconds: where the recording is, and how much of the upload is done while uploading (null otherwise).</summary>
    void Heartbeat(TimeSpan position, int? uploadPercent = null);

    /// <summary>
    /// The run moves on to <paramref name="state"/>: starting, recording, stopping or transcribing. A run reaches its last
    /// state only with its outcome (see <see cref="End"/>); asking for one here throws an <see cref="ArgumentException"/>.
    /// </summary>
    void SetState(RunState state);

    /// <summary>
    /// What ended the recording, in the words of <see cref="RunEndCause"/>, such as Windows when it asks to end the session.
    /// The last cause given is kept: it is the one that ended the process, should it end before the run does. Fit for the
    /// handlers that have only seconds left. A cause whose end did not come is taken back with <see cref="WithdrawEndedBy"/>.
    /// </summary>
    void SetEndedBy(string cause);

    /// <summary>
    /// Takes back <paramref name="cause"/> as what ended the recording, when the end it announced did not come: Windows asked
    /// to end the session and then went on, as when Gulzar cancels a restart that ClassScribe made Windows ask him about. The
    /// run file then names no cause again, so that a later unexpected end is not put down to Windows. A cause given since,
    /// such as Q pressed twice, is kept, and then nothing is written.
    /// </summary>
    void WithdrawEndedBy(string cause);

    /// <summary>
    /// The run is over, as <paramref name="outcome"/> says. Its last state follows from how it ended: Refused for a start
    /// refused because another recording ran, Failed for a recorder that failed, Finished otherwise; nothing uploads any
    /// more, so the heartbeat's upload percent goes. Nothing more is written after this.
    /// </summary>
    void End(RunOutcome outcome);
}
