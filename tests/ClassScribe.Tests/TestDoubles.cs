using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Net;
using System.Text;
using ClassScribe.Audio;
using ClassScribe.Commands;
using ClassScribe.Transcription;
using Microsoft.Extensions.Logging;
using Moq;

namespace ClassScribe.Tests;

/// <summary>A temporary folder that is deleted when the test ends.</summary>
public sealed class TempFolder : IDisposable
{
    /// <summary>Creates the folder.</summary>
    public TempFolder()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ClassScribeTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    /// <summary>Full path of the folder.</summary>
    public string Path { get; }

    /// <summary>Returns the path of a file inside the folder.</summary>
    public string File(string name) => System.IO.Path.Combine(Path, name);

    /// <summary>
    /// Deletes the folder. A file a test left open makes this fail, which exposes leaked handles. A few
    /// attempts are made first, because on Windows a virus scanner may hold a brand-new file for a moment.
    /// </summary>
    public void Dispose()
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                Directory.Delete(Path, recursive: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < 5)
            {
                Thread.Sleep(100 * attempt);
            }
        }
    }
}

/// <summary>
/// A clock the test moves by hand. Timers made from it fire when the clock is moved past their time.
/// Like the computer's clock it has two faces: the time of day (<see cref="GetUtcNow"/>), which Windows
/// may set, and a steady count (<see cref="GetTimestamp"/>) that only moves forward and that timers follow.
/// <see cref="Advance"/> moves both; <see cref="StepWallClock"/> moves the time of day alone.
/// </summary>
public sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
{
    private readonly Lock _gate = new();
    private readonly List<ManualTimer> _timers = [];
    private DateTimeOffset _now = start;
    private TimeSpan _elapsed;

    /// <summary>Timers made from this clock that have not been disposed.</summary>
    public int ActiveTimers
    {
        get { lock (_gate) return _timers.Count; }
    }

    /// <inheritdoc />
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    /// <inheritdoc />
    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate) return _now;
    }

    /// <summary>The steady count, in ticks of 100 nanoseconds since the clock was made.</summary>
    public override long GetTimestamp()
    {
        lock (_gate) return _elapsed.Ticks;
    }

    /// <inheritdoc />
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        timer.Change(dueTime, period);
        lock (_gate) _timers.Add(timer);
        return timer;
    }

    /// <summary>
    /// Moves the clock forward, then runs every timer that has come due, once. A negative step only sets
    /// the time of day back, as <see cref="StepWallClock"/> does, because the steady count never runs backwards.
    /// </summary>
    public void Advance(TimeSpan by)
    {
        List<ManualTimer> due;
        lock (_gate)
        {
            _now += by;
            if (by > TimeSpan.Zero) _elapsed += by;
            due = [.. _timers.Where(timer => timer.Due <= _elapsed)];
            foreach (var timer in due) timer.Due = timer.Period > TimeSpan.Zero ? _elapsed + timer.Period : TimeSpan.MaxValue;
        }

        foreach (var timer in due) timer.Run();
    }

    /// <summary>Sets the time of day forward or back, as Windows does when it corrects its clock. The steady count and the timers do not notice.</summary>
    public void StepWallClock(TimeSpan by)
    {
        lock (_gate) _now += by;
    }

    /// <summary>A timer that only the test's clock can fire.</summary>
    private sealed class ManualTimer(ManualTimeProvider clock, TimerCallback callback, object? state) : ITimer
    {
        /// <summary>When the timer fires next, on the steady count; <see cref="TimeSpan.MaxValue"/> when it is not set to fire.</summary>
        public TimeSpan Due { get; set; }

        /// <summary>The time between firings; zero or less for a timer that fires once.</summary>
        public TimeSpan Period { get; private set; }

        /// <summary>Calls the timer's callback.</summary>
        public void Run() => callback(state);

        /// <inheritdoc />
        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (clock._gate)
            {
                Due = dueTime < TimeSpan.Zero ? TimeSpan.MaxValue : clock._elapsed + dueTime;
                Period = period;
            }

            return true;
        }

        /// <inheritdoc />
        public void Dispose()
        {
            lock (clock._gate) clock._timers.Remove(this);
        }

        /// <inheritdoc />
        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>Keeps what was logged, so a test can check what was reported as a problem.</summary>
public sealed class RecordingLogger<T> : ILogger<T>
{
    private readonly ConcurrentQueue<(LogLevel Level, string Message)> _entries = new();

    /// <summary>The messages logged as warnings or errors, in order.</summary>
    public IReadOnlyList<string> Problems => [.. _entries.Where(entry => entry.Level >= LogLevel.Warning).Select(entry => entry.Message)];

    /// <summary>Every message logged, whatever its level, in order.</summary>
    public IReadOnlyList<string> Messages => [.. _entries.Select(entry => entry.Message)];

    /// <inheritdoc />
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    /// <inheritdoc />
    public bool IsEnabled(LogLevel logLevel) => true;

    /// <inheritdoc />
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        _entries.Enqueue((logLevel, formatter(state, exception)));
}

/// <summary>
/// A capture the test feeds by hand. Like the real one, it reports that it stopped when it is
/// disposed, after handing over whatever it still held.
/// </summary>
public sealed class FakeAudioCapture(AudioFormat format, string deviceName = "Fake Speakers", string? deviceId = null) : IAudioCapture
{
    private volatile bool _startEntered;
    private volatile bool _started;
    private volatile bool _disposed;
    private int _disposeCalls;

    /// <summary>The name doubles as the identifier unless one is given: two fakes with one name stand for the same device.</summary>
    public string DeviceId { get; } = deviceId ?? deviceName;

    /// <inheritdoc />
    public string DeviceName { get; } = deviceName;

    /// <inheritdoc />
    public AudioFormat Format { get; } = format;

    /// <summary>Audio the device still holds when it is asked to stop. It is delivered during Dispose.</summary>
    public byte[]? FinalAudio { get; init; }

    /// <summary>When set, Dispose throws this, as a driver that will not shut down cleanly would.</summary>
    public Exception? DisposeFailure { get; init; }

    /// <summary>When set, Start throws this, as a device that cannot be opened at that moment would.</summary>
    public Exception? StartFailure { get; init; }

    /// <summary>When set, Start waits until it is signalled, as a driver that has stopped answering would.</summary>
    public ManualResetEventSlim? StartGate { get; init; }

    /// <summary>When set, Dispose waits until it is signalled, as a driver that has stopped answering would. It is counted in <see cref="DisposeCalls"/> before it waits.</summary>
    public ManualResetEventSlim? DisposeGate { get; init; }

    /// <summary>True once Start was entered, even while it still waits at <see cref="StartGate"/>.</summary>
    public bool StartEntered => _startEntered;

    /// <summary>True once Start has returned without failing.</summary>
    public bool Started => _started;

    /// <summary>True once Dispose was called.</summary>
    public bool Disposed => _disposed;

    /// <summary>How many times Dispose was called. A capture is meant to be released once.</summary>
    public int DisposeCalls => Volatile.Read(ref _disposeCalls);

    /// <inheritdoc />
    public event EventHandler<AudioDataEventArgs>? DataAvailable;

    /// <inheritdoc />
    public event EventHandler<CaptureStoppedEventArgs>? Stopped;

    /// <inheritdoc />
    public void Start()
    {
        _startEntered = true;
        StartGate?.Wait();
        if (StartFailure is not null) throw StartFailure;
        _started = true;
    }

    /// <inheritdoc />
    public void Stop() { }

    /// <inheritdoc />
    public void Dispose()
    {
        Interlocked.Increment(ref _disposeCalls);
        DisposeGate?.Wait();
        if (_disposed) return;
        _disposed = true;
        if (_started && FinalAudio is { } audio) Deliver(audio);
        if (_started) Stopped?.Invoke(this, new CaptureStoppedEventArgs(null));
        if (DisposeFailure is not null) throw DisposeFailure;
    }

    /// <summary>Hands audio to whoever is listening, as the capture thread would.</summary>
    public void Deliver(byte[] audio) => DataAvailable?.Invoke(this, new AudioDataEventArgs(audio, audio.Length));

    /// <summary>Ends the capture with an error, as a device that disappears would.</summary>
    public void Fail(Exception error) => Stopped?.Invoke(this, new CaptureStoppedEventArgs(error));
}

/// <summary>
/// The output devices a test connects, each with the level Windows' meter would show for it, the apps
/// with sound open on them, and the fake captures handed out in order. The first device connected is
/// the default one until a test says otherwise. A capture's device name doubles as its identifier.
/// </summary>
public sealed class FakeCaptureProvider : ILoopbackCaptureProvider
{
    private readonly Lock _gate = new();
    private readonly List<string> _connected = [];
    private readonly Dictionary<string, float> _levels = [];
    private readonly Dictionary<int, PlayingApp> _apps = [];
    private readonly Dictionary<string, Queue<FakeAudioCapture>> _queued = [];
    private readonly List<FakeAudioCapture> _created = [];
    private string? _defaultDeviceId;
    private int _failedCreations;
    private int _deviceQueries;
    private ManualResetEventSlim? _listingHold;
    private ManualResetEventSlim? _creationHold;
    private volatile bool _listingHeld;
    private volatile bool _creationHeld;

    /// <summary>Captures handed out so far.</summary>
    public IReadOnlyList<FakeAudioCapture> Created
    {
        get { lock (_gate) return [.. _created]; }
    }

    /// <summary>How often a capture was asked for while none was prepared for that device.</summary>
    public int FailedCreations => Volatile.Read(ref _failedCreations);

    /// <summary>How often the devices or the apps were looked up: once per check the session makes.</summary>
    public int DeviceQueries => Volatile.Read(ref _deviceQueries);

    /// <summary>True while anyone listens for <see cref="DevicesChanged"/>.</summary>
    public bool HasListeners => DevicesChanged is not null;

    /// <summary>True once a look at the devices held up by <see cref="HoldNextListing"/> has begun to wait.</summary>
    public bool ListingHeld => _listingHeld;

    /// <summary>True once a capture held up by <see cref="HoldNextCreation"/> has begun to wait.</summary>
    public bool CreationHeld => _creationHeld;

    /// <inheritdoc />
    public event EventHandler? DevicesChanged;

    /// <summary>
    /// Connects the capture's device, if it is not yet, and queues the capture as the next one made for
    /// it. The first device connected becomes the default one. A level given here is what the meter shows for the device.
    /// </summary>
    public FakeAudioCapture Enqueue(FakeAudioCapture capture, float? level = null)
    {
        lock (_gate)
        {
            Connect(capture.DeviceId, level);
            Queue(capture);
        }

        return capture;
    }

    /// <summary>Connects a device with no capture prepared for it, so every attempt to open it fails. The first device connected becomes the default one.</summary>
    public void ConnectDevice(string deviceId, float level = 0)
    {
        lock (_gate) Connect(deviceId, level);
    }

    /// <summary>Sets what Windows' meter shows for a device: 0 for silence, more while sound plays on it.</summary>
    public void SetLevel(string deviceId, float level)
    {
        lock (_gate) _levels[deviceId] = level;
    }

    /// <summary>Gives an app sound open on a device, playing at the given level, and queues the capture made for it, if one is given.</summary>
    public FakeAudioCapture? AddApp(int processId, string name, string deviceName, float level = 0, FakeAudioCapture? capture = null)
    {
        lock (_gate)
        {
            _apps[processId] = new PlayingApp(processId, name, deviceName, level);
            if (capture is not null) Queue(capture);
        }

        return capture;
    }

    /// <summary>Takes an app's sound away, as when the app closes.</summary>
    public void RemoveApp(int processId)
    {
        lock (_gate) _apps.Remove(processId);
    }

    /// <summary>
    /// Simulates Windows switching its default output device to the capture's device, which plays at the
    /// given level from now on. With <paramref name="announce"/> off, the switch happens without the usual notification.
    /// </summary>
    public FakeAudioCapture SwitchDefaultDevice(FakeAudioCapture capture, float level = 0, bool announce = true)
    {
        lock (_gate)
        {
            Connect(capture.DeviceId, level);
            Queue(capture);
            _defaultDeviceId = capture.DeviceId;
        }

        if (announce) DevicesChanged?.Invoke(this, EventArgs.Empty);
        return capture;
    }

    /// <summary>Simulates a device notification that changes nothing for the recording. Windows sends those too.</summary>
    public void RaiseDevicesChanged() => DevicesChanged?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// Makes the next look at the output devices wait until <paramref name="gate"/> is signalled, as a call
    /// that Windows holds up would. The look is counted in <see cref="DeviceQueries"/> before it waits.
    /// </summary>
    public void HoldNextListing(ManualResetEventSlim gate) => Volatile.Write(ref _listingHold, gate);

    /// <summary>Makes the next request for a capture wait until <paramref name="gate"/> is signalled before the capture is made, as a call that Windows holds up would.</summary>
    public void HoldNextCreation(ManualResetEventSlim gate) => Volatile.Write(ref _creationHold, gate);

    /// <summary>Simulates Windows reporting that no output device is left.</summary>
    public void RemoveDevices()
    {
        lock (_gate)
        {
            _connected.Clear();
            _defaultDeviceId = null;
        }

        DevicesChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <inheritdoc />
    public IReadOnlyList<OutputDevice> GetOutputDevices()
    {
        Interlocked.Increment(ref _deviceQueries);
        if (Interlocked.Exchange(ref _listingHold, null) is { } hold)
        {
            _listingHeld = true;
            hold.Wait();
        }

        lock (_gate) return [.. _connected.Select(id => new OutputDevice(id, id, id == _defaultDeviceId, _levels.GetValueOrDefault(id)))];
    }

    /// <inheritdoc />
    public IReadOnlyList<PlayingApp> GetPlayingApps()
    {
        Interlocked.Increment(ref _deviceQueries);
        lock (_gate) return [.. _apps.Values];
    }

    /// <inheritdoc />
    public IAudioCapture CreateCapture(CaptureTarget target)
    {
        if (Interlocked.Exchange(ref _creationHold, null) is { } hold)
        {
            _creationHeld = true;
            hold.Wait();
        }

        lock (_gate)
        {
            if (!_queued.TryGetValue(target.Id, out var queue) || !queue.TryDequeue(out var capture))
            {
                Interlocked.Increment(ref _failedCreations);
                throw new InvalidOperationException($"No capture is prepared for {target.Id}.");
            }

            _created.Add(capture);
            return capture;
        }
    }

    /// <inheritdoc />
    public void Dispose() { }

    /// <summary>Adds the device to the connected ones. Call with the gate held.</summary>
    private void Connect(string deviceId, float? level)
    {
        if (!_connected.Contains(deviceId)) _connected.Add(deviceId);
        if (level is { } known) _levels[deviceId] = known;
        _defaultDeviceId ??= deviceId;
    }

    /// <summary>Queues a capture for its device. Call with the gate held.</summary>
    private void Queue(FakeAudioCapture capture)
    {
        if (!_queued.TryGetValue(capture.DeviceId, out var queue)) _queued[capture.DeviceId] = queue = new Queue<FakeAudioCapture>();
        queue.Enqueue(capture);
    }
}

/// <summary>Stop and cancel signals the test raises by hand.</summary>
public sealed class FakeUserSignals : IUserSignals
{
    private readonly TaskCompletionSource _stop = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _cancellation = new();

    /// <inheritdoc />
    public Task StopRequested => _stop.Task;

    /// <inheritdoc />
    public CancellationToken Cancellation => _cancellation.Token;

    /// <inheritdoc />
    public void RequestStop() => _stop.TrySetResult();

    /// <summary>Cancels whatever is still running, such as the upload, as a later Ctrl+C does.</summary>
    public void Cancel() => _cancellation.Cancel();
}

/// <summary>
/// A console the test types into. Keys and lines are queued in advance, and everything the app shows is
/// kept. A key read with nothing queued waits until the test types more, or until it is cancelled.
/// Keys queued with <see cref="Type"/> are pressed each after its prompt; keys queued with
/// <see cref="TypeAhead"/> were pressed while nothing read the keyboard, such as during an upload, so
/// they come first and <see cref="DiscardPendingKeys"/> drops them.
/// </summary>
public sealed class FakeConsoleUi : IConsoleUi
{
    private readonly Lock _gate = new();
    private readonly Queue<ConsoleKeyInfo> _typedAhead = new();
    private readonly Queue<ConsoleKeyInfo> _keys = new();
    private readonly Queue<string?> _lines = new();
    private readonly ConcurrentQueue<string> _output = new();
    private TaskCompletionSource _typed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <inheritdoc />
    public bool IsInteractive { get; init; } = true;

    /// <summary>Everything shown so far, as one text.</summary>
    public string Screen => string.Join(Environment.NewLine, _output);

    /// <summary>Queues key presses: letters and digits as they are, '\r' for Enter, '\u001b' for Esc and '\u0003' for Ctrl+C.</summary>
    public void Type(params char[] keys) => Enqueue(_keys, keys);

    /// <summary>
    /// Queues key presses made while nothing reads the keyboard, such as during an upload. They wait in
    /// the console, so they are read before the keys queued with <see cref="Type"/>, unless they are discarded first.
    /// </summary>
    public void TypeAhead(params char[] keys) => Enqueue(_typedAhead, keys);

    /// <summary>Queues a line, as typed or dragged in and followed by Enter. Null stands for Esc.</summary>
    public void TypeLine(string? line)
    {
        lock (_gate) _lines.Enqueue(line);
    }

    /// <inheritdoc />
    public void DiscardPendingKeys()
    {
        lock (_gate) _typedAhead.Clear();
    }

    /// <inheritdoc />
    public async Task<ConsoleKeyInfo?> ReadKeyAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            Task typed;
            lock (_gate)
            {
                if (_typedAhead.TryDequeue(out var early)) return early;
                if (_keys.TryDequeue(out var key)) return key;
                typed = _typed.Task;
            }

            try
            {
                await typed.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return null;
            }
        }
    }

    /// <inheritdoc />
    public Task<string?> ReadLineAsync(CancellationToken cancellationToken)
    {
        lock (_gate) return Task.FromResult(_lines.TryDequeue(out var line) ? line : null);
    }

    /// <inheritdoc />
    public void Write(string text) => _output.Enqueue(text);

    /// <inheritdoc />
    public void WriteLine(string text = "") => _output.Enqueue(text);

    /// <summary>Adds key presses to one of the queues and wakes a read that waits for them.</summary>
    private void Enqueue(Queue<ConsoleKeyInfo> queue, char[] keys)
    {
        lock (_gate)
        {
            foreach (var key in keys) queue.Enqueue(ToKeyInfo(key));
            _typed.TrySetResult();
            _typed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    /// <summary>Turns a character into the key press that types it.</summary>
    private static ConsoleKeyInfo ToKeyInfo(char key) => key switch
    {
        '\r' => new(key, ConsoleKey.Enter, false, false, false),
        '\u001b' => new(key, ConsoleKey.Escape, false, false, false),
        '\u0003' => new(key, ConsoleKey.C, false, false, true),
        >= '0' and <= '9' => new(key, ConsoleKey.D0 + (key - '0'), false, false, false),
        >= 'a' and <= 'z' => new(key, ConsoleKey.A + (key - 'a'), false, false, false),
        >= 'A' and <= 'Z' => new(key, ConsoleKey.A + (key - 'A'), true, false, false),
        _ => throw new ArgumentOutOfRangeException(nameof(key), key, "No key press defined for this character.")
    };
}

/// <summary>
/// The calls the Moq mocks are set up with and checked for, written once. The two services with a
/// single call are mocked; the audio device, the clock and the HTTP transport are the small
/// hand-written stand-ins in this file, because tests drive those step by step.
/// </summary>
public static class Calls
{
    /// <summary>Any request for a transcript.</summary>
    public static readonly Expression<Func<ITranscriptionClient, Task<TranscriptionResult>>> AnyTranscription =
        client => client.TranscribeAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>());

    /// <summary>Any request to build an upload file.</summary>
    public static readonly Expression<Action<IAudioPreparer>> AnyPreparation =
        preparer => preparer.Prepare(It.IsAny<IReadOnlyList<string>>(), It.IsAny<string>(), It.IsAny<CancellationToken>());

    /// <summary>A request for the transcript of one particular file.</summary>
    public static Expression<Func<ITranscriptionClient, Task<TranscriptionResult>>> TranscriptionOf(string audioPath) =>
        client => client.TranscribeAsync(audioPath, It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>());
}

/// <summary>Ready-made mocks of the services around the code under test.</summary>
public static class Mocks
{
    /// <summary>The transcript the mocked service answers with.</summary>
    public static TranscriptionResult Transcript { get; } = new(
        "Hello world.", "en", 0.99, 1.0, "id-1",
        [new TranscriptWord("Hello", 0.0, 0.4, "word", null), new TranscriptWord(" ", 0.4, 0.5, "spacing", null), new TranscriptWord("world.", 0.5, 1.0, "word", null)],
        """{"text":"Hello world."}""");

    /// <summary>A transcription service that answers every request with <see cref="Transcript"/>.</summary>
    public static Mock<ITranscriptionClient> TranscriptionClient()
    {
        var client = new Mock<ITranscriptionClient>();
        client.Setup(Calls.AnyTranscription).ReturnsAsync(Transcript);
        return client;
    }

    /// <summary>
    /// A preparer that writes a small placeholder where the upload file should go and, like the real one,
    /// fails rather than replace a file that is already there.
    /// </summary>
    public static Mock<IAudioPreparer> AudioPreparer()
    {
        var preparer = new Mock<IAudioPreparer>();
        preparer.Setup(Calls.AnyPreparation)
            .Callback((IReadOnlyList<string> _, string outputPath, CancellationToken _) =>
            {
                using var upload = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write);
                upload.Write([1, 2, 3]);
            });
        return preparer;
    }
}

/// <summary>What the stub handler saw of one request. <see cref="ContentLength"/> is null when the length was not known before sending.</summary>
public sealed record CapturedRequest(HttpMethod Method, Uri? Uri, string? ApiKey, string? ContentType, long? ContentLength, string Body);

/// <summary>An HTTP handler that answers from a queue and records each request, body included.</summary>
public sealed class StubHttpHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpResponseMessage>> _answers = new();

    /// <summary>Requests received so far.</summary>
    public List<CapturedRequest> Requests { get; } = [];

    /// <summary>Runs as each request reaches the handler, before its body is read, so a test can look at what had happened by then.</summary>
    public Action? OnSend { get; set; }

    /// <summary>Queues a response.</summary>
    public void Enqueue(HttpStatusCode status, string body) =>
        _answers.Enqueue(() => new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });

    /// <summary>Queues a response whose headers arrive but whose body never does: reading it waits until the read is cancelled.</summary>
    public void EnqueueStalledBody(HttpStatusCode status) =>
        _answers.Enqueue(() => new HttpResponseMessage(status) { Content = new StreamContent(new StalledStream()) });

    /// <summary>Queues a failure, such as a broken connection.</summary>
    public void EnqueueFailure(Exception failure) => _answers.Enqueue(() => throw failure);

    /// <summary>Queues a failure that is only made when the request arrives, for failures that depend on that moment.</summary>
    public void EnqueueFailure(Func<Exception> failure) => _answers.Enqueue(() => throw failure());

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        OnSend?.Invoke();

        // Taken before the body is read, so it shows whether the length was known up front.
        var contentLength = request.Content?.Headers.ContentLength;
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        var apiKey = request.Headers.TryGetValues("xi-api-key", out var values) ? values.Single() : null;
        Requests.Add(new CapturedRequest(request.Method, request.RequestUri, apiKey, request.Content?.Headers.ContentType?.ToString(), contentLength, body));
        return _answers.Dequeue()();
    }

    /// <summary>A response body that never arrives, as from a server that stalls after sending the headers.</summary>
    private sealed class StalledStream : Stream
    {
        /// <inheritdoc />
        public override bool CanRead => true;

        /// <inheritdoc />
        public override bool CanSeek => false;

        /// <inheritdoc />
        public override bool CanWrite => false;

        /// <inheritdoc />
        public override long Length => throw new NotSupportedException();

        /// <inheritdoc />
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        /// <inheritdoc />
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException("Only an asynchronous read can wait to be cancelled.");

        /// <summary>Waits until the read is cancelled; no data ever comes.</summary>
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }

        /// <inheritdoc />
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        /// <inheritdoc />
        public override void Flush() { }

        /// <inheritdoc />
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        /// <inheritdoc />
        public override void SetLength(long value) => throw new NotSupportedException();

        /// <inheritdoc />
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

/// <summary>Helpers for building and reading test audio.</summary>
public static class TestAudio
{
    /// <summary>Encodes interleaved 32-bit float samples.</summary>
    public static byte[] Floats(params float[] samples)
    {
        var bytes = new byte[samples.Length * 4];
        for (var i = 0; i < samples.Length; i++)
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * 4, 4), samples[i]);

        return bytes;
    }

    /// <summary>Encodes 16-bit samples.</summary>
    public static byte[] Shorts(params short[] samples)
    {
        var bytes = new byte[samples.Length * 2];
        for (var i = 0; i < samples.Length; i++)
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 2, 2), samples[i]);

        return bytes;
    }

    /// <summary>Builds a mono 16-bit sine tone.</summary>
    public static short[] Tone(int sampleRate, double seconds, double frequency, double amplitude) =>
        Enumerable.Range(0, (int)(sampleRate * seconds))
            .Select(i => (short)Math.Round(Math.Sin(2 * Math.PI * frequency * i / sampleRate) * amplitude * short.MaxValue))
            .ToArray();

    /// <summary>Writes a mono 16-bit WAV file with the recorder's own writer.</summary>
    public static void WriteWav(string path, int sampleRate, short[] samples)
    {
        using var writer = new WavSegmentWriter(path, sampleRate);
        writer.Write(Shorts(samples));
    }

    /// <summary>Reads the header fields and samples of a mono 16-bit WAV file, even while it is still being written.</summary>
    public static (int SampleRate, int Channels, int BitsPerSample, short[] Samples) ReadWav(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var file = new byte[stream.Length];
        stream.ReadExactly(file);

        var dataBytes = (int)BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(40, 4));
        var samples = new short[dataBytes / 2];
        for (var i = 0; i < samples.Length; i++)
            samples[i] = BinaryPrimitives.ReadInt16LittleEndian(file.AsSpan(44 + i * 2, 2));

        return (
            (int)BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(24, 4)),
            BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(22, 2)),
            BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(34, 2)),
            samples);
    }
}

/// <summary>Waits for something another thread does.</summary>
public static class Wait
{
    /// <summary>Polls until the condition holds, or fails after the given number of seconds.</summary>
    public static async Task UntilAsync(Func<bool> condition, int timeoutSeconds = 5)
    {
        var deadline = Environment.TickCount64 + timeoutSeconds * 1000L;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline) throw new TimeoutException($"The expected condition was not met within {timeoutSeconds} seconds.");
            await Task.Delay(10);
        }
    }
}
