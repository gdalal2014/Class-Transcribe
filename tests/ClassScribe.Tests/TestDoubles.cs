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

/// <summary>A clock the test moves by hand. Timers made from it fire when the clock is moved past their time.</summary>
public sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
{
    private readonly Lock _gate = new();
    private readonly List<ManualTimer> _timers = [];
    private DateTimeOffset _now = start;

    /// <inheritdoc />
    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate) return _now;
    }

    /// <inheritdoc />
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        timer.Change(dueTime, period);
        lock (_gate) _timers.Add(timer);
        return timer;
    }

    /// <summary>Moves the clock forward, then runs every timer that has come due, once.</summary>
    public void Advance(TimeSpan by)
    {
        List<ManualTimer> due;
        lock (_gate)
        {
            _now += by;
            due = [.. _timers.Where(timer => timer.Due <= _now)];
            foreach (var timer in due) timer.Due = timer.Period > TimeSpan.Zero ? _now + timer.Period : DateTimeOffset.MaxValue;
        }

        foreach (var timer in due) timer.Run();
    }

    /// <summary>A timer that only the test's clock can fire.</summary>
    private sealed class ManualTimer(ManualTimeProvider clock, TimerCallback callback, object? state) : ITimer
    {
        /// <summary>When the timer fires next.</summary>
        public DateTimeOffset Due { get; set; }

        /// <summary>The time between firings; zero or less for a timer that fires once.</summary>
        public TimeSpan Period { get; private set; }

        /// <summary>Calls the timer's callback.</summary>
        public void Run() => callback(state);

        /// <inheritdoc />
        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (clock._gate)
            {
                Due = dueTime < TimeSpan.Zero ? DateTimeOffset.MaxValue : clock._now + dueTime;
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
public sealed class FakeAudioCapture(AudioFormat format, string deviceName = "Fake Speakers") : IAudioCapture
{
    private volatile bool _started;
    private volatile bool _disposed;

    /// <summary>The name doubles as the identifier: two fakes with one name stand for the same device.</summary>
    public string DeviceId { get; } = deviceName;

    /// <inheritdoc />
    public string DeviceName { get; } = deviceName;

    /// <inheritdoc />
    public AudioFormat Format { get; } = format;

    /// <summary>Audio the device still holds when it is asked to stop. It is delivered during Dispose.</summary>
    public byte[]? FinalAudio { get; init; }

    /// <summary>When set, Dispose throws this, as a driver that will not shut down cleanly would.</summary>
    public Exception? DisposeFailure { get; init; }

    /// <summary>True once Start was called.</summary>
    public bool Started => _started;

    /// <summary>True once Dispose was called.</summary>
    public bool Disposed => _disposed;

    /// <inheritdoc />
    public event EventHandler<AudioDataEventArgs>? DataAvailable;

    /// <inheritdoc />
    public event EventHandler<CaptureStoppedEventArgs>? Stopped;

    /// <inheritdoc />
    public void Start() => _started = true;

    /// <inheritdoc />
    public void Stop() { }

    /// <inheritdoc />
    public void Dispose()
    {
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
/// Hands out prepared fake captures in order. The default device is the one the latest capture was
/// made for, until a test switches it.
/// </summary>
public sealed class FakeCaptureProvider : ILoopbackCaptureProvider
{
    private readonly Lock _gate = new();
    private readonly Queue<FakeAudioCapture> _queued = new();
    private readonly List<FakeAudioCapture> _created = [];
    private string? _defaultDeviceId;
    private int _failedCreations;
    private int _defaultDeviceQueries;

    /// <summary>Captures handed out so far.</summary>
    public IReadOnlyList<FakeAudioCapture> Created
    {
        get { lock (_gate) return [.. _created]; }
    }

    /// <summary>How often a capture was asked for while none was queued.</summary>
    public int FailedCreations => Volatile.Read(ref _failedCreations);

    /// <summary>How often the default device was looked up.</summary>
    public int DefaultDeviceQueries => Volatile.Read(ref _defaultDeviceQueries);

    /// <inheritdoc />
    public event EventHandler? DefaultDeviceChanged;

    /// <summary>Queues the capture the next call will return.</summary>
    public FakeAudioCapture Enqueue(FakeAudioCapture capture)
    {
        lock (_gate) _queued.Enqueue(capture);
        return capture;
    }

    /// <inheritdoc />
    public string? GetDefaultDeviceId()
    {
        Interlocked.Increment(ref _defaultDeviceQueries);
        lock (_gate) return _defaultDeviceId;
    }

    /// <inheritdoc />
    public IAudioCapture CreateForDefaultDevice()
    {
        lock (_gate)
        {
            if (!_queued.TryDequeue(out var capture))
            {
                Interlocked.Increment(ref _failedCreations);
                throw new InvalidOperationException("No output device.");
            }

            _created.Add(capture);
            _defaultDeviceId = capture.DeviceId;
            return capture;
        }
    }

    /// <summary>
    /// Simulates Windows switching to another output device, which the next capture then records.
    /// With <paramref name="announce"/> off, the switch happens without the usual notification.
    /// </summary>
    public FakeAudioCapture SwitchDefaultDevice(FakeAudioCapture capture, bool announce = true)
    {
        lock (_gate)
        {
            _queued.Enqueue(capture);
            _defaultDeviceId = capture.DeviceId;
        }

        if (announce) DefaultDeviceChanged?.Invoke(this, EventArgs.Empty);
        return capture;
    }

    /// <summary>Simulates a device notification that changes nothing for the recording. Windows sends those too.</summary>
    public void RaiseDefaultDeviceChanged() => DefaultDeviceChanged?.Invoke(this, EventArgs.Empty);

    /// <summary>Simulates Windows reporting that no output device is left.</summary>
    public void RemoveDefaultDevice()
    {
        lock (_gate) _defaultDeviceId = null;
        DefaultDeviceChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <inheritdoc />
    public void Dispose() { }
}

/// <summary>Stop and cancel signals the test raises by hand.</summary>
public sealed class FakeUserSignals : IUserSignals
{
    private readonly TaskCompletionSource _stop = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <inheritdoc />
    public Task StopRequested => _stop.Task;

    /// <inheritdoc />
    public CancellationToken Cancellation => CancellationToken.None;

    /// <inheritdoc />
    public void RequestStop() => _stop.TrySetResult();

    /// <inheritdoc />
    public void ListenForStopKey() { }
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
        preparer => preparer.Prepare(It.IsAny<IReadOnlyList<string>>(), It.IsAny<string>());

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

    /// <summary>A preparer that writes a small placeholder where the upload file should go.</summary>
    public static Mock<IAudioPreparer> AudioPreparer()
    {
        var preparer = new Mock<IAudioPreparer>();
        preparer.Setup(Calls.AnyPreparation)
            .Callback((IReadOnlyList<string> _, string outputPath) => File.WriteAllBytes(outputPath, [1, 2, 3]));
        return preparer;
    }
}

/// <summary>What the stub handler saw of one request.</summary>
public sealed record CapturedRequest(HttpMethod Method, Uri? Uri, string? ApiKey, string? ContentType, string Body);

/// <summary>An HTTP handler that answers from a queue and records each request, body included.</summary>
public sealed class StubHttpHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpResponseMessage>> _answers = new();

    /// <summary>Requests received so far.</summary>
    public List<CapturedRequest> Requests { get; } = [];

    /// <summary>Queues a response.</summary>
    public void Enqueue(HttpStatusCode status, string body) =>
        _answers.Enqueue(() => new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });

    /// <summary>Queues a failure, such as a broken connection.</summary>
    public void EnqueueFailure(Exception failure) => _answers.Enqueue(() => throw failure);

    /// <summary>Queues a failure that is only made when the request arrives, for failures that depend on that moment.</summary>
    public void EnqueueFailure(Func<Exception> failure) => _answers.Enqueue(() => throw failure());

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        var apiKey = request.Headers.TryGetValues("xi-api-key", out var values) ? values.Single() : null;
        Requests.Add(new CapturedRequest(request.Method, request.RequestUri, apiKey, request.Content?.Headers.ContentType?.ToString(), body));
        return _answers.Dequeue()();
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
