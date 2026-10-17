using System.Security.AccessControl;
using System.Text;
using Microsoft.Extensions.Logging;

namespace ClassScribe.Recorder;

/// <summary>One line of the recorder's log, numbered, so that a window can ask for the lines after the last one it showed.</summary>
/// <param name="Seq">The line's number: 1 for the first line of the log, and one more for each line after it.</param>
/// <param name="Utc">When the line was written.</param>
/// <param name="Level">How much the line matters, as it was logged.</param>
/// <param name="Text">
/// The line as the console would show it, without the line break at its end. A line about a failure goes on over one more
/// line for each exception in its chain, as it does in the console.
/// </param>
public sealed record LogEntry(long Seq, DateTimeOffset Utc, LogLevel Level, string Text);

/// <summary>
/// The log of the process that records: what every logger writes, through <see cref="RecorderLogProvider"/>, kept in three
/// places. The last <see cref="Capacity"/> lines stay in memory, numbered, for the windows that attach: a window asks for
/// the lines after the last one it showed (<see cref="Since"/>) and follows the new ones as they come
/// (<see cref="Written"/>). Every line also goes to a file: the recording's own log beside its audio, once the run has
/// named it (<see cref="WriteFileTo"/>), with the lines written before it; until then, the start-up file
/// (<paramref name="startupFile"/>, recorder.log in %LOCALAPPDATA%\ClassScribe), where a failure to start is found, when
/// the log was given one.
/// </summary>
/// <remarks>
/// <para>
/// Writing a line never waits. Not for the disk: the files are written in the background, on the thread pool and one batch
/// at a time, from a queue that holds at most <see cref="FileQueueCapacity"/> lines, and a line the file cannot take is
/// counted (<see cref="LostFileLines"/>), for the run to report at its end. And not for a window: the followers are handed
/// the lines in the background too, one line at a time and in the order of their numbers. So neither a stalled disk nor a
/// frozen window can hold up the capture threads, which log too. The price is that a follower gets a line after
/// <see cref="Write"/> has returned, possibly after whatever the writing thread did next: code that must know the
/// followers have every line written so far, as the link does before it sends a run's end, calls
/// <see cref="WaitForFollowers"/>, and <see cref="Flush"/> does the same for the files.
/// </para>
/// <para>
/// The recorder runs one recording, so its recording's log gets every line since the process started. A window logs other
/// work before and between the recordings it runs itself, so it marks where each run begins (<see cref="KeepFromNow"/>),
/// and the recording's log gets only the lines from there on.
/// </para>
/// <para>
/// The log notes its own failures in itself, once each, as errors: the first follower that fails on a line, and the first
/// file that refuses lines, which <see cref="FirstFileFailure"/> also keeps. These notes reach the followers and the files,
/// but not a console, so the run names <see cref="FirstFileFailure"/> itself when it reports the lost lines at its end.
/// </para>
/// <para>
/// The log takes no logger, because the loggers write to it: a log that took one would depend on itself through the logger
/// factory, a cycle in the container. For the same reason a follower must never log from inside its handler.
/// </para>
/// </remarks>
/// <param name="time">The clock that stamps each line.</param>
/// <param name="startupFile">
/// Where the lines go until the recording's log is named: recorder.log for the recorder. Null for a window, which keeps
/// those lines in memory only, for the recording's log.
/// </param>
public sealed class RecorderLog(TimeProvider time, string? startupFile = null) : IDisposable
{
    /// <summary>How many lines the log holds in memory for the windows. A class makes a few hundred.</summary>
    public const int Capacity = 20_000;

    /// <summary>How many lines may wait for a file that is slow to take them. The lines beyond these are counted and not written.</summary>
    public const int FileQueueCapacity = 10_000;

    /// <summary>
    /// The size, in bytes, from which the start-up file is turned over before more lines are added to it: it becomes the
    /// older generation (recorder.old.log), in place of the one before, and a new start-up file is begun.
    /// </summary>
    public const long StartupFileLimit = 1024 * 1024;

    /// <summary>The start-up file's name. The recorder keeps it in %LOCALAPPDATA%\ClassScribe.</summary>
    public const string StartupFileName = "recorder.log";

    /// <summary>How long <see cref="Dispose"/> waits, unless told otherwise, for the lines still waiting to reach the file.</summary>
    internal static readonly TimeSpan DefaultDrainPatience = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The text of the note written when a follower fails on a line. Only the first failure is noted (see
    /// <see cref="HandTo"/>).
    /// </summary>
    private const string FollowerFailed =
        "Could not show a line of this log in a ClassScribe window; the line is kept in this log, and later failures like this are not noted";

    /// <summary>
    /// How the note about a file that refused lines begins, and <see cref="FirstFileFailure"/> with it; the file's path
    /// follows. Only the first failure is noted (see <see cref="Append"/>).
    /// </summary>
    private const string FileFailed = "Could not write the log file";

    /// <summary>What the note about a file that refused lines says after the file's path.</summary>
    private const string FileFailedSoLost = "lines that do not reach it are counted as lost, and later failures like this are not noted";

    /// <summary>
    /// How many times opening a file, or turning the start-up file over, is tried before it counts as failed: a virus
    /// scanner, as Bitdefender here, may hold a file for a moment after it was written.
    /// </summary>
    private const int Tries = 3;

    /// <summary>The pause between two tries.</summary>
    private static readonly TimeSpan PauseBetweenTries = TimeSpan.FromMilliseconds(100);

    /// <summary>UTF-8 without a byte order mark.</summary>
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>Where the lines go until the recording's log is named; null when they are only held in memory until then.</summary>
    private readonly string? _startupFile = startupFile;

    /// <summary>Guards the lines in memory and their numbers, the followers, and the queue of work for the files.</summary>
    private readonly Lock _gate = new();

    /// <summary>The lines held in memory: the line numbered n is at (n - 1) modulo <see cref="Capacity"/>.</summary>
    private readonly LogEntry[] _ring = new LogEntry[Capacity];

    /// <summary>The work for the files, in order.</summary>
    private readonly Queue<FileWork> _work = new();

    /// <summary>
    /// Who waits for the followers (<see cref="WaitForFollowers"/>), each with the number of the last line it waits for, in
    /// the order they came, which is also the order of those numbers.
    /// </summary>
    private readonly Queue<(long UpTo, TaskCompletionSource Done)> _waitingForFollowers = new();

    /// <summary>The number the last line got; 0 before the first.</summary>
    private long _last;

    /// <summary>Who follows the log.</summary>
    private Action<LogEntry>? _followers;

    /// <summary>The number of the last line handed on to the followers, or passed over because nobody followed.</summary>
    private long _handedOn;

    /// <summary>True while the lines are being handed on, in the background.</summary>
    private bool _handing;

    /// <summary>1 once a follower has failed on a line; only that first failure is noted.</summary>
    private int _followerFailed;

    /// <summary>How many lines wait in <see cref="_work"/>.</summary>
    private int _waitingLines;

    /// <summary>How many lines did not reach the file they were for.</summary>
    private int _lost;

    /// <summary>Why a file first refused lines, once one has; only that first failure is noted.</summary>
    private string? _firstFileFailure;

    /// <summary>True while the files are being written, in the background.</summary>
    private bool _writing;

    /// <summary>The recording's log, from <see cref="WriteFileTo"/> until <see cref="EndFile"/>; null while there is none.</summary>
    private string? _named;

    /// <summary>The number of the first line not in a recording's log: the lines from it on are kept for the next one.</summary>
    private long _keptFrom = 1;

    /// <summary>True once the log is disposed: no line reaches a file after that.</summary>
    private bool _disposed;

    // Only the writing of the files, in the background, uses the fields below. It runs once at a time, and it takes the lock
    // each time it takes work, which makes what one run left visible to the next, whichever thread of the pool that is on.

    /// <summary>The file the lines go to now, while it is open.</summary>
    private Stream? _file;

    /// <summary>The path of the file the lines go to now: the recording's log, the start-up file, or none.</summary>
    private string? _target = startupFile;

    /// <summary>True while the lines go to the start-up file, which is turned over when it is full.</summary>
    private bool _toStartup = true;

    /// <summary>The start-up file's size, as far as this log knows: its size when opened, plus what was added since.</summary>
    private long _startupBytes;

    /// <summary>Where the lines go until the recording's log is named, or null when they are only kept in memory until then.</summary>
    public string? StartupFile => _startupFile;

    /// <summary>
    /// How many lines did not reach the file they were for: lines a full queue could not take, lines the disk refused, and
    /// lines written after the log was disposed. They are still in memory, while they are among the last
    /// <see cref="Capacity"/>.
    /// </summary>
    public int LostFileLines
    {
        get
        {
            lock (_gate) return _lost;
        }
    }

    /// <summary>
    /// Why a file first refused lines: the file and Windows' message, as in "Could not write the log file &lt;path&gt;:
    /// There is not enough space on the disk."; null while no file has refused any. The log notes that failure itself, as
    /// an error line, once. The run names this with <see cref="LostFileLines"/> at its end as well, because a window that
    /// records shows what the run logs, but not the log's own lines.
    /// </summary>
    public string? FirstFileFailure
    {
        get
        {
            lock (_gate) return _firstFileFailure;
        }
    }

    /// <summary>Opens a file to add lines at its end. Tests put a file of their own in its place.</summary>
    internal Func<string, Stream> OpenFile { get; init; } = OpenForAppending;

    /// <summary>Moves a file to a new name, replacing a file of that name, to turn the start-up file over. Tests watch it here.</summary>
    internal Action<string, string> MoveFile { get; init; } = MoveReplacing;

    /// <summary>How long <see cref="Dispose"/> waits for the lines still waiting to reach the file.</summary>
    internal TimeSpan DrainPatience { get; init; } = DefaultDrainPatience;

    /// <summary>
    /// Raised once for each line written after the handler was added, in the order of the lines' numbers, in the background on
    /// the thread pool, one line at a time; a handler added while lines are being handed on may also get the last of those. A
    /// window's link follows the log this way and asks for the earlier lines with <see cref="Since"/>; a line that reaches
    /// it both ways, it knows by its number and drops the second time. A line comes after <see cref="Write"/> has returned,
    /// possibly after whatever the writing thread did next, such as ending the run: so the link calls
    /// <see cref="WaitForFollowers"/> before it sends a run's end, for the run's last lines to go first. The handler must
    /// not log: what it logged would come back to it as a line. A handler that throws is noted in the log, once, and is
    /// still handed the lines after.
    /// </summary>
    public event Action<LogEntry>? Written
    {
        add
        {
            lock (_gate) _followers += value;
        }
        remove
        {
            lock (_gate) _followers -= value;
        }
    }

    /// <summary>
    /// Adds a line: numbered, stamped with the time, held in memory, queued for the file it goes to, and handed on to the
    /// followers. Never waits for the disk or for a follower, so the capture threads can log too.
    /// </summary>
    /// <param name="level">How much the line matters.</param>
    /// <param name="text">The line as the console would show it, without a line break at its end.</param>
    public void Write(LogLevel level, string text)
    {
        bool handOn, writeFiles;
        lock (_gate)
        {
            var line = new LogEntry(++_last, time.GetUtcNow(), level, text);
            _ring[Slot(line.Seq)] = line;

            // With nobody following there is nobody to hand the line to, and a follower that comes later gets only the
            // lines after it.
            if (_followers is null) _handedOn = line.Seq;
            handOn = _followers is not null && !_handing;
            if (handOn) _handing = true;

            writeFiles = QueueForFile(line);
        }

        if (handOn) ThreadPool.UnsafeQueueUserWorkItem(static log => log.HandOn(), this, preferLocal: false);
        if (writeFiles) StartWriting();
    }

    /// <summary>
    /// The lines after the one numbered <paramref name="seq"/> that the log still holds, oldest first; the number of the
    /// oldest line held (1 before the first line: the number the first line will get); and the number of the last line
    /// written (0 before the first), all read at one moment. A window that saw the lines up to <paramref name="seq"/> passes
    /// it (0 when it saw none). An oldest number beyond the next one the window expected means the lines between were cut
    /// from memory. With these lines the window has every line up to <c>LastSeq</c>, so a line handed on through
    /// <see cref="Written"/> whose number is not above <c>LastSeq</c> is one it has already. A <paramref name="seq"/> beyond
    /// <c>LastSeq</c> is a number this log never gave, such as one a window kept from another run: no lines come back, and
    /// <c>LastSeq</c>, not <paramref name="seq"/>, is the last line the window has.
    /// </summary>
    public (IReadOnlyList<LogEntry> Lines, long OldestSeq, long LastSeq) Since(long seq)
    {
        lock (_gate)
        {
            var oldest = OldestHeld();
            return (seq >= _last ? [] : Copy(Math.Max(seq + 1, oldest), _last), oldest, _last);
        }
    }

    /// <summary>
    /// Waits until the followers have been handed every line written before this call, for at most
    /// <paramref name="patience"/>. Returns false when that time ran out first, as it does while a follower is held up.
    /// The lines are handed on after <see cref="Write"/> has returned, so the link calls this before it sends a run's end,
    /// for the run's last lines to reach the windows ahead of it. It must not be called from inside a follower's handler,
    /// nor while holding a lock that a follower takes: it would wait for itself until the patience ran out.
    /// </summary>
    public bool WaitForFollowers(TimeSpan patience)
    {
        TaskCompletionSource done;
        lock (_gate)
        {
            // Nothing is being handed on: every line was handed on, or passed over because nobody followed.
            if (!_handing) return true;

            done = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _waitingForFollowers.Enqueue((_last, done));
        }

        return done.Task.Wait(patience);
    }

    /// <summary>
    /// Marks where the next recording's log begins: none of the lines written before this call is carried over into it.
    /// A window calls this when a run in it begins, so that what the window did before, such as the transcription of
    /// another class an hour earlier or the menu between two runs, stays out of the recording's log, while the run's own
    /// first lines, written before its folder was known, are carried over. The recorder does not call it: it runs one
    /// recording, and every line since its process started belongs to that recording. While a recording's log is named,
    /// its lines go there whatever this says, and <see cref="EndFile"/> marks the next start itself.
    /// </summary>
    public void KeepFromNow()
    {
        lock (_gate) _keptFrom = _last + 1;
    }

    /// <summary>
    /// Names the recording's log, &lt;base&gt;.log beside the recording: from now on the lines go there, added at its end, so
    /// that a run continuing the recording adds to what the earlier run wrote. The lines written before, from the latest of
    /// the start, the end of the last recording's log and the mark of <see cref="KeepFromNow"/>, go there first, as far as
    /// they are still held; the start-up file has those already. Naming another file while one is named moves the lines
    /// after this to the new one.
    /// </summary>
    /// <exception cref="ObjectDisposedException">The log is disposed.</exception>
    public void WriteFileTo(string path)
    {
        bool writeFiles;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            // The lines written while a recording's log was named are in that log; only the lines since none was are kept.
            IReadOnlyList<LogEntry> earlier = _named is null ? Copy(Math.Max(_keptFrom, OldestHeld()), _last) : [];
            _named = path;
            _keptFrom = _last + 1;
            writeFiles = Queue(new FileWork.Begin(path, earlier));
        }

        if (writeFiles) StartWriting();
    }

    /// <summary>
    /// Ends the recording's log, as a run does when it is over, and lets go of the file: the lines after this go to the
    /// start-up file, when there is one, and are kept for the next recording's log, unless <see cref="KeepFromNow"/> marks
    /// a later start. Nothing happens when no recording's log is named, or the log is disposed.
    /// </summary>
    public void EndFile()
    {
        bool writeFiles;
        lock (_gate)
        {
            if (_disposed || _named is null) return;
            _named = null;
            _keptFrom = _last + 1;
            writeFiles = Queue(new FileWork.End());
        }

        if (writeFiles) StartWriting();
    }

    /// <summary>
    /// Waits until every line written before this call has reached its file or been counted as lost, for at most
    /// <paramref name="patience"/>. Returns false when that time ran out first, as it does while the disk is stalled.
    /// </summary>
    public bool Flush(TimeSpan patience)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool writeFiles;
        lock (_gate)
        {
            if (_work.Count == 0 && !_writing) return true;
            writeFiles = Queue(new FileWork.Mark(done));
        }

        if (writeFiles) StartWriting();
        return done.Task.Wait(patience);
    }

    /// <summary>
    /// Writes the lines still waiting and closes the file, waiting at most a few seconds for a stalled disk, so that the
    /// process can always end. Lines written afterwards are still held in memory and handed on, but reach no file: they are
    /// counted as lost.
    /// </summary>
    public void Dispose()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool writeFiles;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            writeFiles = Queue(new FileWork.Close());
            Queue(new FileWork.Mark(done));
        }

        if (writeFiles) StartWriting();
        done.Task.Wait(DrainPatience);
    }

    /// <summary>
    /// Opens a log file to add lines at its end, making it, and its folder, when they do not exist yet. Others may read it,
    /// add to it, rename it and delete it meanwhile, and nothing is held back in a buffer: each write goes to Windows at once.
    /// On Windows the file is opened with the right to add to it and no right to write anywhere else, so Windows puts every
    /// write at the end of the file, wherever this stream thinks it is: two processes adding lines at once, as two recorders
    /// that start together do with recorder.log, never write over each other's lines.
    /// </summary>
    internal static Stream OpenForAppending(string path)
    {
        if (Path.GetDirectoryName(Path.GetFullPath(path)) is { Length: > 0 } folder) Directory.CreateDirectory(folder);

        const FileShare others = FileShare.ReadWrite | FileShare.Delete;
        if (OperatingSystem.IsWindows())
            return new FileInfo(path).Create(FileMode.Append, FileSystemRights.AppendData, others, bufferSize: 1, FileOptions.None, fileSecurity: null);

        return new FileStream(path, FileMode.Append, FileAccess.Write, others, bufferSize: 1);
    }

    /// <summary>Moves the file at <paramref name="from"/> to <paramref name="to"/>, replacing a file there.</summary>
    internal static void MoveReplacing(string from, string to) => File.Move(from, to, overwrite: true);

    /// <summary>Where the line numbered <paramref name="seq"/> is held.</summary>
    private static int Slot(long seq) => (int)((seq - 1) % Capacity);

    /// <summary>The number of the oldest line held, or 1 before the first line. Call with the lock held.</summary>
    private long OldestHeld() => _last > Capacity ? _last - Capacity + 1 : 1;

    /// <summary>The lines numbered <paramref name="from"/> to <paramref name="to"/>, which must be held. Call with the lock held.</summary>
    private LogEntry[] Copy(long from, long to)
    {
        if (from > to) return [];

        var lines = new LogEntry[to - from + 1];
        for (var seq = from; seq <= to; seq++) lines[seq - from] = _ring[Slot(seq)];
        return lines;
    }

    /// <summary>
    /// Queues the line for the file it goes to: the recording's log once it is named, else the start-up file. With neither,
    /// the line is only held in memory, for the recording's log to come. A line that cannot be queued is counted as lost.
    /// Returns true when the writing must be started. Call with the lock held.
    /// </summary>
    private bool QueueForFile(LogEntry line)
    {
        if (_named is null && _startupFile is null) return false;

        if (_disposed || _waitingLines >= FileQueueCapacity)
        {
            _lost++;
            return false;
        }

        _waitingLines++;
        return Queue(new FileWork.Line(line));
    }

    /// <summary>Adds work for the files. Returns true when the writing must be started, as it is not running. Call with the lock held.</summary>
    private bool Queue(FileWork work)
    {
        _work.Enqueue(work);
        if (_writing) return false;

        _writing = true;
        return true;
    }

    /// <summary>Starts writing the files, in the background.</summary>
    private void StartWriting() => ThreadPool.UnsafeQueueUserWorkItem(static log => log.WriteFiles(), this, preferLocal: false);

    /// <summary>
    /// Hands the lines written since the last time on to the followers, in the order of their numbers, until none is left,
    /// and lets go of whoever waits for lines that have been handed on. Runs in the background on the thread pool, once at
    /// a time.
    /// </summary>
    private void HandOn()
    {
        while (true)
        {
            LogEntry[] lines;
            Action<LogEntry>[] followers;
            lock (_gate)
            {
                // The lines up to this number have been handed on, or passed over because nobody followed.
                StopWaiting(_handedOn);
                if (_followers is null || _handedOn >= _last)
                {
                    _handedOn = _last;
                    _handing = false;
                    StopWaiting(_last);
                    return;
                }

                // A follower held up for longer than the log holds lines misses the ones cut meanwhile.
                lines = Copy(Math.Max(_handedOn + 1, OldestHeld()), _last);
                _handedOn = _last;
                followers = [.. _followers.GetInvocationList().Cast<Action<LogEntry>>()];
            }

            foreach (var line in lines)
            foreach (var follower in followers)
                HandTo(follower, line);
        }
    }

    /// <summary>Lets go of whoever waits for the followers to be handed the lines up to <paramref name="handedOn"/>. Call with the lock held.</summary>
    private void StopWaiting(long handedOn)
    {
        while (_waitingForFollowers.TryPeek(out var waiting) && waiting.UpTo <= handedOn)
            _waitingForFollowers.Dequeue().Done.TrySetResult();
    }

    /// <summary>Hands one line to one follower. A follower that fails is noted in the log, the first time, and is still handed the lines after.</summary>
    private void HandTo(Action<LogEntry> follower, LogEntry line)
    {
        try
        {
            follower(line);
        }
        catch (Exception ex)
        {
            // Deliberately not rethrown: the lines are handed on in the background, on the thread pool, where an exception
            // nobody catches ends the process, and the recording with it. The line is still held for the windows to ask for,
            // and the other followers have it. The first failure is written into the log itself, so that it is on record in the
            // file; later ones are not, since that note is handed to the same follower, which could fail on it again, without end.
            if (Interlocked.Exchange(ref _followerFailed, 1) == 0)
                Write(LogLevel.Error, RecorderLogProvider.Text(LogLevel.Error, FollowerFailed, ex));
        }
    }

    /// <summary>Does the work queued for the files, in order, until none is left. Runs in the background on the thread pool, once at a time.</summary>
    private void WriteFiles()
    {
        while (true)
        {
            FileWork[] work;
            lock (_gate)
            {
                if (_work.Count == 0)
                {
                    _writing = false;
                    return;
                }

                work = [.. _work];
                _work.Clear();
                _waitingLines -= work.Count(item => item is FileWork.Line);
            }

            Do(work);
        }
    }

    /// <summary>Does the work taken from the queue: the lines are added in batches, one for each stretch between changes of file.</summary>
    private void Do(FileWork[] work)
    {
        List<LogEntry> lines = [];
        foreach (var item in work)
        {
            switch (item)
            {
                case FileWork.Line line:
                    lines.Add(line.Entry);
                    break;

                case FileWork.Begin begin:
                    Append(lines);
                    lines = [.. begin.Earlier];
                    GoTo(begin.Path);
                    break;

                case FileWork.End:
                    Append(lines);
                    lines = [];
                    GoTo(null);
                    break;

                case FileWork.Close:
                    Append(lines);
                    lines = [];
                    CloseFile();
                    _target = null;
                    break;

                case FileWork.Mark mark:
                    Append(lines);
                    lines = [];
                    mark.Done.TrySetResult();
                    break;
            }
        }

        Append(lines);
    }

    /// <summary>Lets go of the file the lines went to, and sends the lines after to the recording's log at <paramref name="named"/>, or, when null, to the start-up file.</summary>
    private void GoTo(string? named)
    {
        CloseFile();
        _target = named ?? _startupFile;
        _toStartup = named is null;
    }

    /// <summary>
    /// Adds the lines at the end of the file they go to now, in one write, opening the file when it is not open. The lines
    /// the file does not take are counted as lost.
    /// </summary>
    private void Append(List<LogEntry> lines)
    {
        if (lines.Count == 0) return;

        if (_target is not { } path)
        {
            // No line is queued while there is no file to take it; should one be, it is counted rather than dropped unseen.
            lock (_gate) _lost += lines.Count;
            return;
        }

        try
        {
            var bytes = Encode(lines);
            if (_toStartup && _file is not null && _startupBytes >= StartupFileLimit) CloseFile();
            _file ??= Open(path);
            _file.Write(bytes);
            _file.Flush();
            _startupBytes += bytes.Length;
        }
        catch (Exception ex)
        {
            // Deliberately not rethrown: the files are written in the background, on the thread pool, where an exception nobody
            // catches ends the process, and the recording with it; the log on disk is a record for later and must never cost
            // the audio. The lines are counted as lost, which the run reports at its end, and they stay in memory for the
            // windows. The file is let go, so that the next lines open it again, once the disk takes them. The first failure
            // is kept (FirstFileFailure) and noted in the log itself, with the file and the exception, so that the count is
            // not left without a reason; later ones are not, so that a disk that keeps failing cannot fill the log with notes.
            bool first;
            lock (_gate)
            {
                _lost += lines.Count;
                first = _firstFileFailure is null;
                if (first) _firstFileFailure = $"{FileFailed} {path}: {ex.Message}";
            }

            CloseFile();
            if (first) Write(LogLevel.Error, RecorderLogProvider.Text(LogLevel.Error, $"{FileFailed} {path}; {FileFailedSoLost}", ex));
        }
    }

    /// <summary>
    /// Opens the file the lines go to now, trying again after a pause while another program holds it. The start-up file is
    /// turned over first when it has reached its limit. The last try's failure is thrown.
    /// </summary>
    private Stream Open(string path)
    {
        if (_toStartup)
        {
            _startupBytes = SizeOf(path);
            if (_startupBytes >= StartupFileLimit && TurnOver(path)) _startupBytes = 0;
        }

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return OpenFile(path);
            }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && attempt < Tries)
            {
                // Deliberately not rethrown: a virus scanner may hold the file for a moment, so it is tried again after a
                // pause; the last try's failure is not caught here, and the lines then count as lost.
                Thread.Sleep(PauseBetweenTries);
            }
        }
    }

    /// <summary>Lets go of the open file, if one is open.</summary>
    private void CloseFile()
    {
        var file = _file;
        _file = null;
        file?.Dispose();
    }

    /// <summary>The size of the file at <paramref name="path"/>, or 0 when there is none.</summary>
    private static long SizeOf(string path)
    {
        var file = new FileInfo(path);
        return file.Exists ? file.Length : 0;
    }

    /// <summary>
    /// Makes the full start-up file the older generation, recorder.old.log beside recorder.log, in place of the one before,
    /// trying again after a pause while another program holds either. Returns false when it could not.
    /// </summary>
    private bool TurnOver(string path)
    {
        var older = Path.Combine(Path.GetDirectoryName(path) ?? "", $"{Path.GetFileNameWithoutExtension(path)}.old{Path.GetExtension(path)}");
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                MoveFile(path, older);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Deliberately not rethrown: turning the file over only keeps it small. While another program holds either
                // file, it is tried again after a pause; after the last try the lines go on into the full file, and the next
                // lines try again.
                if (attempt == Tries) return false;
                Thread.Sleep(PauseBetweenTries);
            }
        }
    }

    /// <summary>The lines as the file holds them: each on a line of its own, in UTF-8 without a byte order mark, so that the files of several runs join into one.</summary>
    private static byte[] Encode(List<LogEntry> lines)
    {
        var text = new StringBuilder();
        foreach (var line in lines) text.Append(line.Text).Append(Environment.NewLine);
        return Utf8.GetBytes(text.ToString());
    }

    /// <summary>What the writing of the files is asked to do, in order.</summary>
    private abstract record FileWork
    {
        /// <summary>Adds a line to the file the lines go to now.</summary>
        /// <param name="Entry">The line.</param>
        public sealed record Line(LogEntry Entry) : FileWork;

        /// <summary>From here on, the lines go to the recording's log, after the lines kept for it.</summary>
        /// <param name="Path">The recording's log.</param>
        /// <param name="Earlier">The lines written before it was named and kept for it.</param>
        public sealed record Begin(string Path, IReadOnlyList<LogEntry> Earlier) : FileWork;

        /// <summary>The recording's log is over: the lines go to the start-up file again, when there is one.</summary>
        public sealed record End : FileWork;

        /// <summary>The log is disposed: let go of the file.</summary>
        public sealed record Close : FileWork;

        /// <summary>Tells whoever waits that everything queued before this is done.</summary>
        /// <param name="Done">Completed when it is.</param>
        public sealed record Mark(TaskCompletionSource Done) : FileWork;
    }
}
