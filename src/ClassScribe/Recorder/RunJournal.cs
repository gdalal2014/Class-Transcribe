using System.Text.Json;
using ClassScribe.Commands;
using ClassScribe.Settings;
using ClassScribe.Storage;
using Microsoft.Extensions.Logging;

namespace ClassScribe.Recorder;

/// <summary>
/// The run files in ClassScribe's own folder (see <see cref="RuntimeFolder.Runs"/>): the request a window hands the recorder
/// it starts, and one file per run, which the process that records the run keeps up to date (see <see cref="RunHandle"/>)
/// and every window reads. A file is always written whole: into a file of its own beside it, flushed to the disk, then
/// moved into its place in one step, so that a reader finds the whole of the old file or the whole of the new one, never
/// part of one and never none, even after a power cut. Another program holding a file for a moment, as a virus scanner
/// does, is waited out: each read or write is tried three more times, 100 ms apart. Files are read sharing everything.
/// </summary>
public sealed class RunJournal(RuntimeFolder folder, IProcessLiveness liveness, TimeProvider time, ILogger<RunJournal> logger)
{
    /// <summary>How long a run that has nothing left to report is kept after its last update: 30 days.</summary>
    public static readonly TimeSpan KeepSeenRunsFor = TimeSpan.FromDays(30);

    /// <summary>How a run file's name ends: the run's id, then this.</summary>
    private const string RunFileEnding = ".json";

    /// <summary>How a request file's name ends: the run's id, then this.</summary>
    private const string RequestFileEnding = ".request.json";

    /// <summary>How the name of a file being written ends: the file's own name, a random part, then this.</summary>
    private const string UnfinishedEnding = ".tmp";

    /// <summary>How many times a file another program holds is tried again.</summary>
    private const int Retries = 3;

    /// <summary>The pause before a file another program holds is tried again.</summary>
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(100);

    /// <summary>How the journal waits before it tries a file again: the thread sleeps, unless a test plays the wait.</summary>
    private readonly Action<TimeSpan> _pause = Thread.Sleep;

    /// <summary>
    /// A journal that waits with <paramref name="pause"/> before it tries a file again instead of sleeping, so that a test
    /// can act at that moment, as the program that holds the file would.
    /// </summary>
    internal RunJournal(RuntimeFolder folder, IProcessLiveness liveness, TimeProvider time, ILogger<RunJournal> logger, Action<TimeSpan> pause)
        : this(folder, liveness, time, logger) => _pause = pause;

    /// <summary>What a look at a run file found.</summary>
    private enum Finding
    {
        /// <summary>A run file this build reads.</summary>
        Run,

        /// <summary>No file.</summary>
        Missing,

        /// <summary>A file that another program held through every try.</summary>
        Held,

        /// <summary>A file that is not a run file: not JSON, incomplete, or named after another run.</summary>
        Unreadable,

        /// <summary>A run file of a later layout, written by a later build, which is that build's to read.</summary>
        LaterLayout
    }

    /// <summary>The journal's log, for the runs it hands out.</summary>
    internal ILogger Logger => logger;

    /// <summary>The run file of run <paramref name="runId"/>. Throws an <see cref="ArgumentException"/> for anything but a run id as ClassScribe makes them.</summary>
    public string RunFile(string runId) => Path.Combine(folder.Runs, Checked(runId) + RunFileEnding);

    /// <summary>The request file of run <paramref name="runId"/>. Throws an <see cref="ArgumentException"/> for anything but a run id as ClassScribe makes them.</summary>
    public string RequestFile(string runId) => Path.Combine(folder.Runs, Checked(runId) + RequestFileEnding);

    /// <summary>
    /// Writes the request file of <paramref name="request"/>'s run, before its recorder starts: the request, and the
    /// settings to record with, by setting name, from <see cref="SettingsReloader.Snapshot"/>, which leaves the API key out.
    /// The recorder takes the key from its own environment, and the file never holds a key the settings handed over carry:
    /// as a guard against a caller that hands on more than the snapshot, such as the whole of the settings, a setting that
    /// holds the key by name (ElevenLabs:ApiKey, or ELEVENLABS_API_KEY as the host keeps that variable, in any letter case)
    /// is left out, and so is any other setting whose value holds the key one of those gives. Each mistake is logged by the
    /// setting's name. A failure to write the file is logged and thrown.
    /// </summary>
    public void WriteRequest(RecordRequest request, IReadOnlyDictionary<string, string?> settings)
    {
        var path = RequestFile(request.RunId);
        List<string> keys = [.. settings.Where(setting => HoldsTheApiKey(setting.Key) && !string.IsNullOrWhiteSpace(setting.Value)).Select(setting => setting.Value!.Trim())];
        var kept = new Dictionary<string, string?>(settings.Count, StringComparer.Ordinal);
        foreach (var (name, value) in settings)
        {
            if (HoldsTheApiKey(name) || (value is not null && keys.Exists(key => value.Contains(key, StringComparison.Ordinal))))
            {
                logger.LogWarning("The settings handed to the recorder held the API key, in {Setting}; it was left out of the request, since the recorder takes the key from its own environment only", name);
                continue;
            }

            kept[name] = value;
        }

        try
        {
            WriteWhole(path, JsonSerializer.SerializeToUtf8Bytes(new RunRequestFile(RunRequestFile.CurrentVersion, request, kept), RecorderJson.Default.RunRequestFile));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not write the request file {Path}", path);
            throw;
        }

        logger.LogDebug("Wrote the request of run {RunId} to {Path}", request.RunId, path);
    }

    /// <summary>
    /// Takes the request of run <paramref name="runId"/>, as its recorder does when it starts: reads the request file and
    /// deletes it, so it is taken once. Null when there is none. A file that cannot be read as this run's request is
    /// deleted all the same, since it can never be used, and refused with an <see cref="InvalidDataException"/> that names
    /// it and says why. A file another program holds through every try is logged and its failure thrown.
    /// </summary>
    public (RecordRequest Request, IReadOnlyDictionary<string, string?> Settings)? TakeRequest(string runId)
    {
        var path = RequestFile(runId);
        byte[]? bytes;
        try
        {
            bytes = ReadWhole(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not read the request file {Path}", path);
            throw;
        }

        if (bytes is null)
        {
            logger.LogDebug("There is no request for run {RunId} in {Path}", runId, path);
            return null;
        }

        TryDelete(path);
        RunRequestFile file;
        try
        {
            file = ReadRequest(bytes, path, runId);
        }
        catch (InvalidDataException ex)
        {
            logger.LogWarning(ex, "Could not take the request of run {RunId}", runId);
            throw;
        }

        logger.LogDebug("Took the request of run {RunId} from {Path}", runId, path);
        return (file.Request, file.Settings);
    }

    /// <summary>
    /// Begins the file of the run <paramref name="record"/> describes, as the process that records it, and returns the
    /// handle through which that process keeps it up to date. The file is written as given, in this build's layout, with
    /// the heartbeat's time now. A failure to write it is logged and thrown: the caller decides what a run without its
    /// file means.
    /// </summary>
    public RunHandle Begin(RunRecord record)
    {
        var path = RunFile(record.RunId);
        if (record.Request is null) throw new ArgumentException("A run file needs the request its run was started with.", nameof(record));

        var begun = record with { Version = RunRecord.CurrentVersion, HeartbeatUtc = time.GetUtcNow() };
        try
        {
            Write(path, begun);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not write the run file {Path}", path);
            throw;
        }

        logger.LogDebug("Began the run file {Path} ({State})", path, begun.State);
        return new RunHandle(this, path, begun, time);
    }

    /// <summary>
    /// The runs in the runs folder, newest first, each with whether its process runs. Whatever is not a run file this build
    /// can read is passed over: request files, writes under way, files that are not run files, and run files of a later
    /// layout. A run file another program holds through every try is passed over this time, with a warning. Empty without
    /// a runs folder, and, with a warning, when the folder cannot be looked at.
    /// </summary>
    public IReadOnlyList<RunView> List()
    {
        var runs = new List<RunView>();
        foreach (var (path, runId) in RunFiles())
        {
            if (Look(path, runId) is (Finding.Run, { } record)) runs.Add(View(record));
        }

        return [.. runs.OrderByDescending(run => run.Record.StartedUtc).ThenByDescending(run => run.Record.RunId, StringComparer.Ordinal)];
    }

    /// <summary>The run <paramref name="runId"/>, with whether its process runs; null when it has no run file this build can read.</summary>
    public RunView? Find(string runId)
    {
        var path = RunFile(runId);
        return Look(path, runId) is (Finding.Run, { } record) ? View(record) : null;
    }

    /// <summary>
    /// Marks run <paramref name="runId"/> seen, once a window has shown its outcome or dealt with its unexpected end, so that
    /// no start of ClassScribe reports it again. Nothing else in the file changes. A run whose process still records or
    /// transcribes is left alone: its recorder owns its file until the run is over. A run without a file this build can
    /// read is left as it is. A failure to write the mark is logged and goes no further: the run is reported once more.
    /// </summary>
    public void MarkSeen(string runId)
    {
        var path = RunFile(runId);
        if (Look(path, runId) is not (Finding.Run, { } record))
        {
            logger.LogDebug("There is no run file to mark seen at {Path}", path);
            return;
        }

        if (record.Seen) return;
        if (record.State is not (RunState.Finished or RunState.Refused or RunState.Failed) && View(record).IsAlive)
        {
            logger.LogDebug("Run {RunId} is still under way, and its recorder keeps its file; it is not marked seen now", runId);
            return;
        }

        try
        {
            Write(path, record with { Seen = true });
            logger.LogDebug("Marked run {RunId} seen", runId);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Deliberately not rethrown: the mark only spares the next start a report; without it the run is reported once
            // more, which does no harm, and the window that marks it has nothing better to do about it.
            logger.LogWarning(ex, "Could not mark run {RunId} seen in {Path}; the next start of ClassScribe may report it again", runId, path);
        }
    }

    /// <summary>
    /// Marks run <paramref name="runId"/> seen, as <see cref="MarkSeen"/> does, but only when its file says the run is over:
    /// finished, refused or failed. A recorder that Windows ended sends its windows the end and leaves its file recording,
    /// so that the next start of ClassScribe reports the recording and offers to continue it; a window that was shown that
    /// end must not mark the run seen, or the report would be lost.
    /// </summary>
    public void MarkSeenIfOver(string runId)
    {
        var path = RunFile(runId);
        if (Look(path, runId) is (Finding.Run, { } record) && record.State is not (RunState.Finished or RunState.Refused or RunState.Failed))
        {
            logger.LogDebug("Run {RunId} is left for the next start to report: its file still says {State}", runId, record.State);
            return;
        }

        MarkSeen(runId);
    }

    /// <summary>
    /// Deletes what is older than <paramref name="olderThan"/> and has nothing left to tell, as a recorder does when it
    /// starts: runs whose process is gone and that need no report (seen ones, refused starts, and starts whose recorder
    /// died before it recorded), by their last update; and, by their files' times, requests no recorder took, writes cut
    /// short, and run files that cannot be read. Runs that need a report are kept however old, and so are run files of a
    /// later layout; anything else in the folder is left alone. Pruning is housekeeping: a file that cannot be deleted now,
    /// or a runs folder that cannot be looked at, is logged, and the next start tries again.
    /// </summary>
    public void Prune(TimeSpan olderThan)
    {
        var now = time.GetUtcNow();
        var deleted = 0;
        foreach (var path in FilesInRuns())
        {
            var name = Path.GetFileName(path);
            bool old;
            if (IsRunFile(name, out var runId))
            {
                old = Look(path, runId) switch
                {
                    (Finding.Run, { } record) => now - record.HeartbeatUtc > olderThan && View(record) is { IsAlive: false, NeedsReport: false },
                    (Finding.Unreadable, _) => now - LastWritten(path) > olderThan,
                    _ => false
                };
            }
            else
            {
                var leftover = name.EndsWith(UnfinishedEnding, StringComparison.OrdinalIgnoreCase)
                    || (name.EndsWith(RequestFileEnding, StringComparison.OrdinalIgnoreCase) && RunIds.IsWellFormed(name[..^RequestFileEnding.Length]));
                old = leftover && now - LastWritten(path) > olderThan;
            }

            if (old && TryDelete(path)) deleted++;
        }

        if (deleted > 0) logger.LogDebug("Deleted {Count} old files from {Folder}", deleted, folder.Runs);
    }

    /// <summary>Writes <paramref name="record"/> whole as the run file at <paramref name="path"/>. Throws the last try's failure.</summary>
    internal void Write(string path, RunRecord record) => WriteWhole(path, JsonSerializer.SerializeToUtf8Bytes(record, RecorderJson.Default.RunRecord));

    /// <summary>True when <paramref name="name"/> is a setting that holds the API key by name: ElevenLabs:ApiKey, or the ELEVENLABS_API_KEY variable as the host keeps it, in any letter case.</summary>
    private static bool HoldsTheApiKey(string name) =>
        name.Equals(ElevenLabsOptions.ApiKeySetting, StringComparison.OrdinalIgnoreCase)
        || name.Equals(ElevenLabsOptions.ApiKeyVariable, StringComparison.OrdinalIgnoreCase);

    /// <summary><paramref name="runId"/> when it is a run id as ClassScribe makes them; otherwise an <see cref="ArgumentException"/>, before it names any file.</summary>
    private static string Checked(string runId) =>
        RunIds.IsWellFormed(runId) ? runId : throw new ArgumentException($"\"{runId}\" is not a ClassScribe run id.", nameof(runId));

    /// <summary>True when <paramref name="name"/> is a run file's name, a run id and .json, with the run id in <paramref name="runId"/>.</summary>
    private static bool IsRunFile(string name, out string runId)
    {
        runId = name.EndsWith(RunFileEnding, StringComparison.Ordinal) ? name[..^RunFileEnding.Length] : "";
        return RunIds.IsWellFormed(runId);
    }

    /// <summary>When the file at <paramref name="path"/> was last written, in UTC.</summary>
    private static DateTimeOffset LastWritten(string path) => new(File.GetLastWriteTimeUtc(path), TimeSpan.Zero);

    /// <summary>
    /// True when everything a reader relies on is there: a field the file lacks reads as null even where its type says it
    /// never is (see <see cref="RecorderJson"/>).
    /// </summary>
    private static bool IsComplete(RecordRequest? request) =>
        request is { RunId: not null, Name: not null, Source: not null, OutputRoot: not null };

    /// <summary>The request in <paramref name="bytes"/>, read from <paramref name="path"/>, when it is run <paramref name="runId"/>'s; otherwise an <see cref="InvalidDataException"/> saying why.</summary>
    private static RunRequestFile ReadRequest(byte[] bytes, string path, string runId)
    {
        RunRequestFile? file;
        try
        {
            file = JsonSerializer.Deserialize(bytes, RecorderJson.Default.RunRequestFile);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            // Handed on as what it means here, a request file that cannot be read, with the reader's own failure inside.
            throw new InvalidDataException($"The request file {path} cannot be read: {ex.Message}", ex);
        }

        var problem = file switch
        {
            null => "it holds nothing",
            { Version: not RunRequestFile.CurrentVersion } => $"it is laid out as version {file.Version}, and this build reads version {RunRequestFile.CurrentVersion}",
            { Settings: null } => "it holds no settings",
            _ when !IsComplete(file.Request) => "it holds no complete request",
            _ when file.Request.RunId != runId => $"it holds the request of run {file.Request.RunId}",
            _ => null
        };

        return problem is null ? file! : throw new InvalidDataException($"The request file {path} cannot be read: {problem}");
    }

    /// <summary>The run files in the runs folder, with their run ids (see <see cref="FilesInRuns"/>).</summary>
    private List<(string Path, string RunId)> RunFiles()
    {
        var runFiles = new List<(string Path, string RunId)>();
        foreach (var path in FilesInRuns())
        {
            if (IsRunFile(Path.GetFileName(path), out var runId)) runFiles.Add((path, runId));
        }

        return runFiles;
    }

    /// <summary>
    /// The files in the runs folder: none without it, and none, with a warning, when it cannot be looked at, so that a
    /// broken folder never keeps a window from its menu or a recorder from recording.
    /// </summary>
    private string[] FilesInRuns()
    {
        try
        {
            return Directory.GetFiles(folder.Runs);
        }
        catch (DirectoryNotFoundException ex)
        {
            // Deliberately not rethrown: without a runs folder there are no runs yet.
            logger.LogDebug(ex, "There is no runs folder at {Folder}", folder.Runs);
            return [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Deliberately not rethrown: the runs are bookkeeping, and a folder that cannot be looked at must not keep
            // ClassScribe from its menu, from transcribing, or from recording, which says for itself when it cannot write
            // there (RuntimeFolder.DescribeWriteProblem). The next look tries again.
            logger.LogWarning(ex, "Could not look at the runs in {Folder}; a recording that ended unexpectedly cannot be reported until it can be", folder.Runs);
            return [];
        }
    }

    /// <summary>Reads the run file at <paramref name="path"/>, named after <paramref name="runId"/>, and says what it is, with the run when it is one.</summary>
    private (Finding Finding, RunRecord? Record) Look(string path, string runId)
    {
        byte[]? bytes;
        try
        {
            bytes = ReadWhole(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Deliberately not rethrown: one run file that another program holds must not hide the others, nor keep a
            // window from its menu; the next look reads it again.
            logger.LogWarning(ex, "Could not read the run file {Path}; it is passed over this time", path);
            return (Finding.Held, null);
        }

        if (bytes is null) return (Finding.Missing, null);

        RunRecord? record;
        try
        {
            record = JsonSerializer.Deserialize(bytes, RecorderJson.Default.RunRecord);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            // Deliberately not rethrown: a file that is not a run file is passed over, as a reader of RecorderJson does,
            // and pruning deletes it once it is old.
            logger.LogDebug(ex, "Passing over {Path}: it is not a run file", path);
            return (Finding.Unreadable, null);
        }

        var (finding, problem) = record switch
        {
            { Version: > RunRecord.CurrentVersion } => (Finding.LaterLayout, $"it is laid out as version {record.Version}, by a later build"),
            null or { Version: < 1 } => (Finding.Unreadable, "it has no layout version"),
            { RunId: null } or { ExePath: null } or { Build: null } => (Finding.Unreadable, "it is incomplete"),
            _ when !IsComplete(record.Request) => (Finding.Unreadable, "its request is incomplete"),
            _ when record.RunId != runId => (Finding.Unreadable, $"it holds run {record.RunId}"),
            _ => (Finding.Run, null)
        };

        if (finding == Finding.Run) return (Finding.Run, record);

        logger.LogDebug("Passing over {Path}: {Problem}", path, problem);
        return (finding, null);
    }

    /// <summary><paramref name="record"/>, with whether the process it names still runs.</summary>
    private RunView View(RunRecord record) => new(record, liveness.IsAlive(record.ProcessId, record.ProcessStartedUtc));

    /// <summary>
    /// The whole of the file at <paramref name="path"/>, or null when there is no such file. It is opened sharing
    /// everything, and held only while it is read. Another program holding it without sharing is waited out three times,
    /// 100 ms apart; the last try's failure is thrown.
    /// </summary>
    private byte[]? ReadWhole(string path)
    {
        for (var retry = 0; ; retry++)
        {
            try
            {
                using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize: 1);
                using var bytes = new MemoryStream();
                file.CopyTo(bytes);
                return bytes.ToArray();
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                // Deliberately not rethrown: no file is the answer, that there is no such run or request.
                return null;
            }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && retry < Retries)
            {
                // Deliberately not rethrown: another program, such as a virus scanner, holds the file for a moment, so it is
                // read again shortly. The last try's failure is handed on.
                logger.LogDebug(ex, "{Path} is held by another program; reading it again in {Milliseconds} ms", path, RetryDelay.TotalMilliseconds);
                _pause(RetryDelay);
            }
        }
    }

    /// <summary>
    /// Writes <paramref name="bytes"/> as the whole of the file at <paramref name="path"/>: into a file of its own beside it,
    /// flushed to the disk, then moved into its place, replacing the old file in one step. A move Windows refuses because
    /// another program has the file open, even a window reading it, is tried again three times, 100 ms apart; the last
    /// try's failure is thrown. Nothing is left over from a try that failed.
    /// </summary>
    private void WriteWhole(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        for (var retry = 0; ; retry++)
        {
            var unfinished = $"{path}.{Guid.NewGuid():N}{UnfinishedEnding}";
            try
            {
                using (var file = new FileStream(unfinished, FileMode.CreateNew, FileAccess.Write, FileShare.None, bufferSize: 1))
                {
                    file.Write(bytes);
                    file.Flush(flushToDisk: true);
                }

                File.Move(unfinished, path, overwrite: true);
                return;
            }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && retry < Retries)
            {
                // Deliberately not rethrown: another program has the file open for a moment, which makes Windows refuse to
                // replace it, so it is written again shortly. The last try's failure is handed on.
                Discard(unfinished);
                logger.LogDebug(ex, "{Path} is held by another program; writing it again in {Milliseconds} ms", path, RetryDelay.TotalMilliseconds);
                _pause(RetryDelay);
            }
            catch
            {
                Discard(unfinished);
                throw;
            }
        }
    }

    /// <summary>Deletes the unfinished write at <paramref name="unfinished"/>, if it is there.</summary>
    private void Discard(string unfinished)
    {
        try
        {
            File.Delete(unfinished);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Deliberately not rethrown: the write's own failure is the one to report, and an unfinished write left behind
            // is deleted by pruning once it is old.
            logger.LogDebug(ex, "Could not delete the unfinished write {Path}", unfinished);
        }
    }

    /// <summary>
    /// Deletes the file at <paramref name="path"/>, waiting out another program that holds it three times, 100 ms apart.
    /// True once it is gone; false, with a warning, when it is still there: it is tried again later.
    /// </summary>
    private bool TryDelete(string path)
    {
        for (var retry = 0; ; retry++)
        {
            try
            {
                File.Delete(path);
                return true;
            }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && retry < Retries)
            {
                // Deliberately not rethrown: another program holds the file for a moment, so it is deleted again shortly.
                logger.LogDebug(ex, "{Path} is held by another program; deleting it again in {Milliseconds} ms", path, RetryDelay.TotalMilliseconds);
                _pause(RetryDelay);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Deliberately not rethrown: a file that stays behind holds nothing secret, and the next prune deletes it.
                logger.LogWarning(ex, "Could not delete {Path}; it is deleted at a later start", path);
                return false;
            }
        }
    }
}

/// <summary>
/// One run's file, as the process that records the run keeps it up to date: recording into its files (the mark that a
/// recording is under way, so that a run whose process is gone is known to have ended unexpectedly), a heartbeat every 10
/// seconds, the state as it moves on, what ended the recording (taken back when that end did not come), and the outcome.
/// Each write brings the heartbeat's time up to date. A write that fails is reported once and goes no further, since a
/// recording must never end because its run file cannot be written; what it held goes with the next write that can be
/// made, and letting the handle go is a last chance for it. Once the run has ended, nothing more changes; once the handle
/// is let go, nothing more is written, and a run let go without an outcome keeps its file as it last was. It is safe to use
/// from several threads. One run has one handle, which every part of the run that writes shares.
/// </summary>
public sealed class RunHandle : IDisposable
{
    private readonly Lock _gate = new();
    private readonly RunJournal _journal;
    private readonly TimeProvider _time;
    private RunRecord _record;

    /// <summary>True once the run has ended: nothing more changes.</summary>
    private bool _ended;

    /// <summary>True once the handle was let go: nothing more is written.</summary>
    private bool _letGo;

    /// <summary>True while the last write failed, so that a failure is reported once and its end noted once.</summary>
    private bool _failing;

    /// <summary>A handle on the run file at <paramref name="filePath"/>, just written as <paramref name="record"/>.</summary>
    internal RunHandle(RunJournal journal, string filePath, RunRecord record, TimeProvider time)
    {
        _journal = journal;
        _time = time;
        _record = record;
        FilePath = filePath;
    }

    /// <summary>The run file.</summary>
    public string FilePath { get; }

    /// <summary>The run as the handle last wrote it, or tried to.</summary>
    public RunRecord Current
    {
        get { lock (_gate) return _record; }
    }

    /// <summary>The run records into <paramref name="files"/>: from now on, should its process be gone, it ended unexpectedly.</summary>
    public void Recording(RecordingFiles files) =>
        Update(record => record with { State = RunState.Recording, BaseName = files.BaseName, Folder = files.Folder });

    /// <summary>The heartbeat, every 10 seconds: where the recording is, and how much of the upload is done while uploading (null otherwise).</summary>
    public void Heartbeat(TimeSpan position, int? uploadPercent = null) =>
        Update(record => record with { Position = position, UploadPercent = uploadPercent });

    /// <summary>
    /// The run moves on to <paramref name="state"/>: starting, recording, stopping or transcribing. A run reaches its last
    /// state only with its outcome (see <see cref="End"/>); asking for one here throws an <see cref="ArgumentException"/>.
    /// </summary>
    public void SetState(RunState state)
    {
        if (state is RunState.Finished or RunState.Refused or RunState.Failed)
            throw new ArgumentException($"A run becomes {state} only with its outcome, through End.", nameof(state));

        Update(record => record with { State = state });
    }

    /// <summary>
    /// What ended the recording, in the words of <see cref="RunEndCause"/>, such as Windows when it asks to end the session.
    /// The last cause given is kept: it is the one that ended the process, should it end before the run does. Fit for the
    /// handlers that have only seconds left. A cause whose end did not come is taken back with <see cref="WithdrawEndedBy"/>.
    /// </summary>
    public void SetEndedBy(string cause)
    {
        if (string.IsNullOrWhiteSpace(cause)) throw new ArgumentException("What ended the recording is needed.", nameof(cause));

        Update(record => record with { EndedBy = cause });
    }

    /// <summary>
    /// Takes back <paramref name="cause"/> as what ended the recording, when the end it announced did not come: Windows asked
    /// to end the session and then went on, as when Gulzar cancels a restart that ClassScribe made Windows ask him about. The
    /// run file then names no cause again, so that a later unexpected end is not put down to Windows. A cause given since,
    /// such as Q pressed twice, is kept, and then nothing is written.
    /// </summary>
    public void WithdrawEndedBy(string cause)
    {
        if (string.IsNullOrWhiteSpace(cause)) throw new ArgumentException("What ended the recording is needed.", nameof(cause));

        Update(record => record.EndedBy == cause ? record with { EndedBy = null } : null);
    }

    /// <summary>
    /// The run is over, as <paramref name="outcome"/> says. Its last state follows from how it ended: Refused for a start
    /// refused because another recording ran, Failed for a recorder that failed, Finished otherwise; nothing uploads any
    /// more, so the heartbeat's upload percent goes. Nothing more is written after this.
    /// </summary>
    public void End(RunOutcome outcome)
    {
        var state = outcome.Kind switch
        {
            OutcomeKind.Refused => RunState.Refused,
            OutcomeKind.RecorderFailed => RunState.Failed,
            _ => RunState.Finished
        };

        Update(record => record with { State = state, Outcome = outcome, UploadPercent = null }, end: true);
    }

    /// <summary>
    /// Lets the run file go: nothing more is written to it after this. When the last write failed, what it held, such as the
    /// outcome, is written once more first, its last chance; otherwise the file stays as it was last written.
    /// </summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_letGo) return;

            _letGo = true;
            if (_failing) WriteLocked();
        }
    }

    /// <summary>
    /// Changes the run with <paramref name="change"/>, brings the heartbeat's time up to date, and writes the file; ends the
    /// run when <paramref name="end"/> is true. A change that gives null leaves the run as it is, and nothing is written.
    /// </summary>
    private void Update(Func<RunRecord, RunRecord?> change, bool end = false)
    {
        lock (_gate)
        {
            if (_ended || _letGo)
            {
                _journal.Logger.LogDebug("Run {RunId} is over; its run file is no longer written", _record.RunId);
                return;
            }

            if (change(_record) is not { } changed) return;

            _record = changed with { HeartbeatUtc = _time.GetUtcNow() };
            _ended = end;
            WriteLocked();
        }
    }

    /// <summary>Writes the run as it is now. A failure is reported once, and goes no further. Call with the lock held.</summary>
    private void WriteLocked()
    {
        try
        {
            _journal.Write(FilePath, _record);
            if (!_failing) return;

            _failing = false;
            _journal.Logger.LogInformation("The run file {Path} is up to date again", FilePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Deliberately not rethrown: the run file is bookkeeping, and a recording must never end because it cannot be
            // written. What this write held goes with the next write that can be made, or when the run is let go, and the
            // failure is reported once, not at every heartbeat.
            if (_failing)
            {
                _journal.Logger.LogDebug(ex, "Still could not bring the run file {Path} up to date", FilePath);
                return;
            }

            _failing = true;
            _journal.Logger.LogWarning(ex,
                "Could not bring the run file {Path} up to date ({Reason}). The recording itself is not affected; the file is written again with the next update, and until then a start of ClassScribe may report this run as it last was",
                FilePath, ex.Message);
        }
    }
}
