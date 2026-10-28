using ClassScribe.Audio;
using ClassScribe.Commands;
using ClassScribe.Recorder;
using ClassScribe.Runs;
using ClassScribe.Storage;
using Microsoft.Extensions.Logging;

namespace ClassScribe.Tests;

/// <summary>
/// Errors nobody caught, as a test plays them: <see cref="Crash"/> gives the warning to every run registered for it, in the
/// order they registered, as .NET does as such an error takes the process down; and the test can see whether anyone still
/// listens.
/// </summary>
public sealed class FakeCrashWarnings : ICrashWarnings
{
    private readonly Lock _gate = new();
    private readonly List<Action<object>> _registered = [];

    /// <summary>How many warnings are registered now.</summary>
    public int Registered
    {
        get { lock (_gate) return _registered.Count; }
    }

    /// <inheritdoc />
    public IDisposable Register(Action<object> onCrash)
    {
        lock (_gate) _registered.Add(onCrash);
        return new Registration(this, onCrash);
    }

    /// <summary>An error nobody caught, <paramref name="error"/>, takes the process down: every warning registered is given, in order.</summary>
    public void Crash(Exception error)
    {
        Action<object>[] warnings;
        lock (_gate) warnings = [.. _registered];
        foreach (var warn in warnings) warn(error);
    }

    /// <summary>One warning; disposing it, once or more, takes it off the list.</summary>
    private sealed class Registration(FakeCrashWarnings owner, Action<object> onCrash) : IDisposable
    {
        private int _disposed;

        /// <summary>Takes the warning off the list, the first time only.</summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
            lock (owner._gate) owner._registered.Remove(onCrash);
        }
    }
}

/// <summary>
/// Makes recordings through another factory and hands every call on to them, letting the test act just before each call:
/// to read the run file at the moment the run flushes or stops the recording, for example, or to make a call fail.
/// </summary>
public sealed class WatchedRecordingFactory(IRecordingFactory inner) : IRecordingFactory
{
    /// <summary>
    /// Called just before each call on a recording made here, with the call's name ("Start", "Stop", "Flush",
    /// "GetSnapshot" and so on), on the thread that makes it. It may throw, as the recording itself could.
    /// </summary>
    public Action<string>? BeforeCall { get; set; }

    /// <inheritdoc />
    public IRecording Create(SourcePlan source) => new Watched(inner.Create(source), this);

    /// <summary>Tells the test of the call named <paramref name="call"/>, before it is made.</summary>
    private void Before(string call) => BeforeCall?.Invoke(call);

    /// <summary>A recording that tells its factory's test of each call before it hands the call on.</summary>
    private sealed class Watched(IRecording recording, WatchedRecordingFactory factory) : IRecording
    {
        /// <inheritdoc />
        public TimeSpan Position => recording.Position;

        /// <inheritdoc />
        public event EventHandler<TimelineNote>? NoteWorthy
        {
            add => recording.NoteWorthy += value;
            remove => recording.NoteWorthy -= value;
        }

        /// <inheritdoc />
        public void Start(RecordingFiles files, TimeSpan offset)
        {
            factory.Before(nameof(Start));
            recording.Start(files, offset);
        }

        /// <inheritdoc />
        public RecordingSummary Stop()
        {
            factory.Before(nameof(Stop));
            return recording.Stop();
        }

        /// <inheritdoc />
        public void Pause()
        {
            factory.Before(nameof(Pause));
            recording.Pause();
        }

        /// <inheritdoc />
        public void Resume()
        {
            factory.Before(nameof(Resume));
            recording.Resume();
        }

        /// <inheritdoc />
        public bool? SetMicrophone(bool on)
        {
            factory.Before(nameof(SetMicrophone));
            return recording.SetMicrophone(on);
        }

        /// <inheritdoc />
        public RecordingSnapshot GetSnapshot()
        {
            factory.Before(nameof(GetSnapshot));
            return recording.GetSnapshot();
        }

        /// <inheritdoc />
        public void Reopen(Track track, string reason)
        {
            factory.Before(nameof(Reopen));
            recording.Reopen(track, reason);
        }

        /// <inheritdoc />
        public void Flush()
        {
            factory.Before(nameof(Flush));
            recording.Flush();
        }

        /// <inheritdoc />
        public void Dispose()
        {
            factory.Before(nameof(Dispose));
            recording.Dispose();
        }
    }
}

/// <summary>
/// A file on a disk that has stalled: each write waits until the test lets it go on with <see cref="Release"/>, and the
/// test can wait for the first write to arrive. Handed to a <see cref="RecorderLog"/>, it shows who waits for the log to
/// reach its file and who does not. Disposing it lets every write go on.
/// </summary>
public sealed class StalledStream : Stream
{
    private readonly ManualResetEventSlim _released = new();
    private readonly ManualResetEventSlim _writeArrived = new();

    /// <inheritdoc />
    public override bool CanRead => false;

    /// <inheritdoc />
    public override bool CanSeek => false;

    /// <inheritdoc />
    public override bool CanWrite => true;

    /// <inheritdoc />
    public override long Length => throw new NotSupportedException();

    /// <inheritdoc />
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    /// <summary>Lets every write that waits, and every later one, go on.</summary>
    public void Release() => _released.Set();

    /// <summary>Waits up to <paramref name="patience"/> for a write to arrive; true when one did.</summary>
    public bool WaitForAWrite(TimeSpan patience) => _writeArrived.Wait(patience);

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count) => Stall();

    /// <inheritdoc />
    public override void Write(ReadOnlySpan<byte> buffer) => Stall();

    /// <inheritdoc />
    public override void Flush() => _released.Wait();

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <summary>Lets every write go on, so that nothing waits on a file that is gone.</summary>
    protected override void Dispose(bool disposing)
    {
        Release();
        base.Dispose(disposing);
    }

    /// <summary>A write arrives, and waits until the test lets it go on.</summary>
    private void Stall()
    {
        _writeArrived.Set();
        _released.Wait();
    }
}

/// <summary>
/// Takes how a run ended, as the window or the recorder shows it at the end, and keeps each outcome with whether the
/// results were to be opened, in order; then passes it on to the shower given, when there is one, so that a test can see
/// both what was shown and what that shower did with it.
/// </summary>
public sealed class FakeResultShower(IResultShower? inner = null) : IResultShower
{
    private readonly Lock _gate = new();
    private readonly List<(RunOutcome Outcome, bool Open)> _shown = [];

    /// <summary>Every outcome shown, with whether the results were to be opened, in order.</summary>
    public IReadOnlyList<(RunOutcome Outcome, bool Open)> Shown
    {
        get { lock (_gate) return [.. _shown]; }
    }

    /// <summary>The outcome shown last, or null before the first.</summary>
    public RunOutcome? Last
    {
        get { lock (_gate) return _shown.Count == 0 ? null : _shown[^1].Outcome; }
    }

    /// <inheritdoc />
    public void Show(RunOutcome outcome, bool open)
    {
        lock (_gate) _shown.Add((outcome, open));
        inner?.Show(outcome, open);
    }
}

/// <summary>
/// A logger that hands every line to each of the loggers given, in order: so that a test can keep the lines of a class to
/// look at, and have them reach a <see cref="RecorderLog"/> as well, as the app's loggers do through
/// <see cref="RecorderLogProvider"/>.
/// </summary>
public sealed class TeeLogger<T>(params ILogger[] loggers) : ILogger<T>
{
    /// <summary>Starts no scope: the loggers this hands lines to show none.</summary>
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    /// <summary>True when any of the loggers takes lines of <paramref name="logLevel"/>.</summary>
    public bool IsEnabled(LogLevel logLevel) => loggers.Any(logger => logger.IsEnabled(logLevel));

    /// <summary>Hands the line to each logger, in order.</summary>
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        foreach (var logger in loggers) logger.Log(logLevel, eventId, state, exception, formatter);
    }
}
