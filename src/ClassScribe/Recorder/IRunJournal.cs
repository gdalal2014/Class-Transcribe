using ClassScribe.Runs;

namespace ClassScribe.Recorder;

/// <summary>
/// The run files in ClassScribe's own folder (architecture 4.3; see <see cref="IRuntimeFolder.Runs"/>): the request a window
/// hands the recorder it starts, and one file per run, which the process that records the run keeps up to date through the
/// handle this gives (<see cref="IRunHandle"/>) and every window reads. A file is always written whole, so that a reader
/// finds the whole of the old file or the whole of the new one, never part of one and never none, even after a power cut.
/// Another program holding a file for a moment, as a virus scanner does, is waited out: each read or write is tried a few
/// more times, a moment apart. Files are read sharing everything.
/// </summary>
public interface IRunJournal
{
    /// <summary>The run file of run <paramref name="runId"/>. Throws an <see cref="ArgumentException"/> for anything but a run id as ClassScribe makes them.</summary>
    string RunFile(string runId);

    /// <summary>The request file of run <paramref name="runId"/>. Throws an <see cref="ArgumentException"/> for anything but a run id as ClassScribe makes them.</summary>
    string RequestFile(string runId);

    /// <summary>
    /// Writes the request file of <paramref name="request"/>'s run, before its recorder starts: the request, and the
    /// settings to record with, by setting name, as the settings snapshot gives them, which leaves the API key out. The
    /// recorder takes the key from its own environment, and the file never holds a key the settings handed over carry: as a
    /// guard against a caller that hands on more than the snapshot, such as the whole of the settings, a setting that holds
    /// the key by name (ElevenLabs:ApiKey, or ELEVENLABS_API_KEY as the host keeps that variable, in any letter case) is left
    /// out, and so is any other setting whose value holds the key one of those gives. Each mistake is logged by the setting's
    /// name. A failure to write the file is logged and thrown.
    /// </summary>
    void WriteRequest(RecordRequest request, IReadOnlyDictionary<string, string?> settings);

    /// <summary>
    /// Takes the request of run <paramref name="runId"/>, as its recorder does when it starts: reads the request file and
    /// deletes it, so it is taken once. Null when there is none. A file that cannot be read as this run's request is
    /// deleted all the same, since it can never be used, and refused with an <see cref="InvalidDataException"/> that names
    /// it and says why. A file another program holds through every try is logged and its failure thrown.
    /// </summary>
    (RecordRequest Request, IReadOnlyDictionary<string, string?> Settings)? TakeRequest(string runId);

    /// <summary>
    /// Begins the file of the run <paramref name="record"/> describes, as the process that records it, and returns the
    /// handle through which that process keeps it up to date. The file is written as given, in this build's layout, with
    /// the heartbeat's time now. A failure to write it is logged and thrown: the caller decides what a run without its
    /// file means.
    /// </summary>
    IRunHandle Begin(RunRecord record);

    /// <summary>
    /// The runs in the runs folder, newest first, each with whether its process runs. Whatever is not a run file this build
    /// can read is passed over: request files, writes under way, files that are not run files, and run files of a later
    /// layout. A run file another program holds through every try is passed over this time, with a warning. Empty without
    /// a runs folder, and, with a warning, when the folder cannot be looked at.
    /// </summary>
    IReadOnlyList<RunView> List();

    /// <summary>The run <paramref name="runId"/>, with whether its process runs; null when it has no run file this build can read.</summary>
    RunView? Find(string runId);

    /// <summary>
    /// Marks run <paramref name="runId"/> seen, once a window has shown its outcome or dealt with its unexpected end, so that
    /// no start of ClassScribe reports it again. Nothing else in the file changes. A run whose process still records or
    /// transcribes is left alone: its recorder owns its file until the run is over. A run without a file this build can
    /// read is left as it is. A failure to write the mark is logged and goes no further: the run is reported once more.
    /// </summary>
    void MarkSeen(string runId);

    /// <summary>
    /// Marks run <paramref name="runId"/> seen, as <see cref="MarkSeen"/> does, but only when its file says the run is over:
    /// finished, refused or failed. A recorder that Windows ended sends its windows the end and leaves its file recording,
    /// so that the next start of ClassScribe reports the recording and offers to continue it; a window that was shown that
    /// end must not mark the run seen, or the report would be lost.
    /// </summary>
    void MarkSeenIfOver(string runId);

    /// <summary>
    /// Deletes what is older than <paramref name="olderThan"/> and has nothing left to tell, as a recorder does when it
    /// starts: runs whose process is gone and that need no report (seen ones, refused starts, and starts whose recorder
    /// died before it recorded), by their last update; and, by their files' times, requests no recorder took, writes cut
    /// short, and run files that cannot be read. Runs that need a report are kept however old, and so are run files of a
    /// later layout; anything else in the folder is left alone. Pruning is housekeeping: a file that cannot be deleted now,
    /// or a runs folder that cannot be looked at, is logged, and the next start tries again.
    /// </summary>
    void Prune(TimeSpan olderThan);
}
