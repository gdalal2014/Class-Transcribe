using ClassScribe.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClassScribe.Audio;

/// <summary>What a finished recording produced.</summary>
public sealed record RecordingResult(string Folder, IReadOnlyList<string> SegmentPaths, TimeSpan Duration);

/// <summary>A snapshot of a running recording.</summary>
public sealed record RecordingStatus(TimeSpan Captured, TimeSpan SinceLastSound, string DeviceName, int SegmentCount);

/// <summary>
/// Records the default output device into WAV segment files. The header of the open segment is
/// rewritten every few seconds, so a crash keeps the audio. A new segment is started when Windows
/// switches output device, when the capture or the disk fails, or when a segment grows too large.
/// </summary>
/// <remarks>
/// Two locks, never taken in the other order: the lifecycle semaphore allows one start, stop or
/// restart at a time, and the gate guards the state the capture thread shares. A capture is never
/// released while the gate is held, because releasing it waits for the capture thread.
/// </remarks>
public sealed class RecordingSession(
    ILoopbackCaptureProvider provider,
    IOptions<RecordingOptions> options,
    TimeProvider time,
    ILogger<RecordingSession> logger) : IDisposable
{
    /// <summary>Samples at or above this level (out of 32767) count as audible sound.</summary>
    private const int SoundThreshold = 100;

    /// <summary>Pause between attempts to open a capture, and the least time between two replacements of a capture that keeps ending.</summary>
    private static readonly TimeSpan RestartRetryDelay = TimeSpan.FromSeconds(2);

    /// <summary>When writing fails twice in a row, audio is dropped for this long before a new segment file is tried.</summary>
    private static readonly TimeSpan WriteRetryDelay = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How often the session looks up the default device by itself. Windows normally announces a switch
    /// at once; this is the safety net for an announcement that never arrives, because recording a
    /// device that has gone quiet would lose the rest of the class without any error.
    /// </summary>
    private static readonly TimeSpan DeviceCheckInterval = TimeSpan.FromSeconds(3);

    private readonly RecordingOptions _options = options.Value;
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly List<string> _segments = [];

    private IAudioCapture? _capture;
    private ITimer? _deviceCheck;
    private bool _captureEnded;
    private WavSegmentWriter? _writer;
    private RecordingResult? _result;
    private byte[] _convertBuffer = [];
    private string _folder = "";
    private string _deviceName = "";
    private TimeSpan _closedDuration;
    private DateTimeOffset _lastFlush;
    private DateTimeOffset _lastSound;
    private DateTimeOffset _writeRetryAt;
    private int _writeFailures;
    private long? _lastEndedRestart;
    private bool _started;
    private volatile bool _stopping;
    private int _restartQueued;
    private int _failedRestarts;

    private long MaxSegmentBytes => Math.Max(1, _options.MaxSegmentMegabytes) * 1024L * 1024L;

    private TimeSpan FlushInterval => TimeSpan.FromSeconds(Math.Max(1, _options.FlushSeconds));

    /// <summary>Starts capturing the default output device into <paramref name="folder"/>.</summary>
    public void Start(string folder)
    {
        _lifecycle.Wait();
        try
        {
            if (_started)
                throw new InvalidOperationException("This recording session has already been started.");

            _started = true;
            _folder = folder;
            Directory.CreateDirectory(folder);
            _lastSound = time.GetUtcNow();
            provider.DefaultDeviceChanged += OnDefaultDeviceChanged;
            try
            {
                StartCapture();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Could not start capturing system audio");
                provider.DefaultDeviceChanged -= OnDefaultDeviceChanged;
                throw;
            }

            _deviceCheck = time.CreateTimer(_ => QueueRestart(), null, DeviceCheckInterval, DeviceCheckInterval);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    /// <summary>Stops capturing, finalises the files and returns what was recorded. Safe to call twice.</summary>
    public RecordingResult Stop()
    {
        _lifecycle.Wait();
        try
        {
            if (_result is not null) return _result;

            IAudioCapture? capture;
            lock (_gate)
            {
                _stopping = true;
                capture = _capture;
            }

            provider.DefaultDeviceChanged -= OnDefaultDeviceChanged;
            _deviceCheck?.Dispose();

            try
            {
                // Waits for the capture thread, so the last buffers are still written.
                ReleaseCapture(capture);
            }
            catch (Exception ex)
            {
                // Deliberately not rethrown: the audio is already in the segment files, and a device that
                // will not shut down cleanly must not stand between the recording and its transcript.
                logger.LogError(ex, "The audio device did not shut down cleanly; the recorded audio is kept");
            }

            lock (_gate)
            {
                _capture = null;
                CloseSegmentLocked();
                _result = new RecordingResult(_folder, [.. _segments], _closedDuration);
            }

            return _result;
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    /// <summary>Reports how much has been captured and how long ago sound was last heard.</summary>
    public RecordingStatus GetStatus()
    {
        lock (_gate)
        {
            var captured = _closedDuration + (_writer?.Duration ?? TimeSpan.Zero);
            return new RecordingStatus(captured, time.GetUtcNow() - _lastSound, _deviceName, _segments.Count);
        }
    }

    /// <summary>Writes the current header to disk right away. Used when the process is about to exit.</summary>
    public void Flush()
    {
        lock (_gate)
        {
            _writer?.Flush();
        }
    }

    /// <summary>Stops the recording if it is still running.</summary>
    public void Dispose()
    {
        if (_started && _result is null) Stop();
    }

    /// <summary>Opens a capture of the current default device. Its segment file is created when its first audio arrives.</summary>
    private void StartCapture()
    {
        var capture = provider.CreateForDefaultDevice();
        try
        {
            if (!PcmConverter.IsSupported(capture.Format))
                throw new NotSupportedException(
                    $"The output device \"{capture.DeviceName}\" uses an audio format this app cannot read ({capture.Format}).");

            capture.DataAvailable += OnDataAvailable;
            capture.Stopped += OnCaptureStopped;
            lock (_gate)
            {
                _capture = capture;
                _captureEnded = false;
                _deviceName = capture.DeviceName;
            }

            capture.Start();
        }
        catch
        {
            lock (_gate)
            {
                if (ReferenceEquals(_capture, capture)) _capture = null;
            }

            ReleaseCapture(capture);
            throw;
        }

        logger.LogInformation("Capturing \"{Device}\" at {SampleRate} Hz", capture.DeviceName, capture.Format.SampleRate);
    }

    /// <summary>Converts incoming audio to mono 16-bit and appends it to the open segment. Runs on the capture thread.</summary>
    private void OnDataAvailable(object? sender, AudioDataEventArgs e)
    {
        if (e.ByteCount <= 0) return;

        lock (_gate)
        {
            if (_capture is null || !ReferenceEquals(sender, _capture)) return;

            var format = _capture.Format;
            var needed = e.ByteCount / format.BytesPerFrame * 2;
            if (_convertBuffer.Length < needed) _convertBuffer = new byte[needed];

            var written = PcmConverter.ToMono16(e.Buffer.AsSpan(0, e.ByteCount), format, _convertBuffer);
            var mono = _convertBuffer.AsSpan(0, written);
            if (mono.IsEmpty) return;

            var now = time.GetUtcNow();
            if (PcmConverter.Peak(mono) >= SoundThreshold) _lastSound = now;

            // While the disk keeps failing, the audio is dropped until the next attempt is due.
            if (_writer is null && now < _writeRetryAt) return;

            try
            {
                var writer = _writer ?? OpenSegmentLocked(format.SampleRate);
                writer.Write(mono);

                if (now - _lastFlush >= FlushInterval)
                {
                    writer.Flush();
                    _lastFlush = now;
                    _writeFailures = 0;   // the disk has just shown that it works
                }

                // The next audio to arrive opens the next segment.
                if (writer.DataBytes >= MaxSegmentBytes) CloseSegmentLocked();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Deliberately not rethrown: the device is fine, so the capture keeps running, and what is
                // already on disk stays usable. The recording continues in a fresh file, because a write
                // that failed half-way can leave this one cut in the middle of a sample.
                if (++_writeFailures == 1)
                {
                    logger.LogError(ex, "Could not write the recording to {Folder}; a moment of audio is lost. Continuing in a new file", _folder);
                }
                else
                {
                    // A disk that fails again at once (a full one, typically) is given a pause, so this does not fill the folder with stubs.
                    _writeRetryAt = now + WriteRetryDelay;
                    logger.LogError(ex, "Still cannot write the recording to {Folder}; audio is being lost. Trying again in {Seconds} seconds", _folder, WriteRetryDelay.TotalSeconds);
                }

                CloseSegmentLocked();
            }
        }
    }

    /// <summary>Notes that the capture ended without having been asked to, and has it replaced. Runs on the capture thread.</summary>
    private void OnCaptureStopped(object? sender, CaptureStoppedEventArgs e)
    {
        lock (_gate)
        {
            if (_stopping || _captureEnded || !ReferenceEquals(sender, _capture)) return;
            _captureEnded = true;
        }

        if (e.Error is null)
            logger.LogWarning("Audio capture stopped by itself; restarting it");
        else
            logger.LogWarning(e.Error, "Audio capture failed; restarting it");

        QueueRestart();
    }

    /// <summary>Has the capture checked against the new default device. Runs on a thread that belongs to Windows.</summary>
    private void OnDefaultDeviceChanged(object? sender, EventArgs e) => QueueRestart();

    /// <summary>
    /// Schedules one restart check on the thread pool. Requests made before it begins are merged into it.
    /// The check itself decides whether anything needs doing, so asking too often costs next to nothing.
    /// </summary>
    private void QueueRestart()
    {
        if (_stopping || Interlocked.Exchange(ref _restartQueued, 1) == 1) return;
        _ = Task.Run(RestartAsync);
    }

    /// <summary>Runs one restart check and schedules another when this one could not finish the job.</summary>
    private async Task RestartAsync()
    {
        TimeSpan? retryIn = null;
        await _lifecycle.WaitAsync();
        try
        {
            Interlocked.Exchange(ref _restartQueued, 0);
            if (_stopping) return;

            retryIn = SwitchToDefaultDevice();
            if (retryIn is null) _failedRestarts = 0;
        }
        catch (Exception ex)
        {
            // This is a background task with nobody to rethrow to, so the failure is logged and tried again below.
            retryIn = RestartRetryDelay;
            if (_failedRestarts++ % 15 == 0)
                logger.LogWarning(ex, "Cannot capture an output device right now; trying again every {Seconds} seconds", RestartRetryDelay.TotalSeconds);
        }
        finally
        {
            _lifecycle.Release();
        }

        if (retryIn is { } delay && !_stopping)
        {
            await Task.Delay(delay);
            QueueRestart();
        }
    }

    /// <summary>
    /// Replaces the capture when there is none, when it has ended, or when it records a device that is
    /// no longer the default one. Otherwise it does nothing, so several notifications about one change
    /// cost a single restart. Returns null when the session is settled, or how long to wait before
    /// this has to be tried again.
    /// </summary>
    private TimeSpan? SwitchToDefaultDevice()
    {
        IAudioCapture? old;
        bool ended;
        lock (_gate)
        {
            old = _capture;
            ended = _captureEnded;
        }

        if (ended)
        {
            // A capture that ends again right after being replaced must not become a tight loop of restarts.
            if (_lastEndedRestart is { } last && time.GetElapsedTime(last) < RestartRetryDelay) return RestartRetryDelay;

            _lastEndedRestart = time.GetTimestamp();
        }
        else if (old is not null)
        {
            // Woken by a device notification or by the regular check. Usually nothing has changed for this recording.
            var defaultId = provider.GetDefaultDeviceId();
            if (defaultId is null || string.Equals(defaultId, old.DeviceId, StringComparison.OrdinalIgnoreCase)) return null;

            logger.LogInformation("Windows switched to another output device; following it");
        }

        try
        {
            // Waits for the capture thread, so the old device's last buffers still reach its segment.
            ReleaseCapture(old);
        }
        finally
        {
            lock (_gate)
            {
                _capture = null;
                _captureEnded = false;
                CloseSegmentLocked();
            }
        }

        StartCapture();
        return null;
    }

    /// <summary>Stops and disposes a capture. Never call this with <see cref="_gate"/> held.</summary>
    private void ReleaseCapture(IAudioCapture? capture)
    {
        if (capture is null) return;

        // Detached first: from here on the capture ends because it was asked to, which is nothing to react to.
        capture.Stopped -= OnCaptureStopped;
        try
        {
            capture.Stop();
            capture.Dispose();
        }
        finally
        {
            capture.DataAvailable -= OnDataAvailable;
        }
    }

    /// <summary>Creates the next segment file. Call with <see cref="_gate"/> held.</summary>
    private WavSegmentWriter OpenSegmentLocked(int sampleRate)
    {
        // The next number is normally free. A file that is already there is never overwritten, so its number is skipped.
        var number = _segments.Count + 1;
        string path;
        while (File.Exists(path = Path.Combine(_folder, $"recording-{number:000}.wav"))) number++;

        var writer = new WavSegmentWriter(path, sampleRate);
        _writer = writer;
        _segments.Add(path);
        _lastFlush = time.GetUtcNow();
        return writer;
    }

    /// <summary>
    /// Finalises the open segment. Call with <see cref="_gate"/> held. This never throws: a segment that
    /// cannot be finalised (on a full disk, say) still holds everything up to its last save, and losing
    /// its last seconds must not cost the rest of the recording.
    /// </summary>
    private void CloseSegmentLocked()
    {
        if (_writer is null) return;

        var writer = _writer;
        var name = Path.GetFileName(writer.Path);
        _writer = null;
        _closedDuration += writer.Duration;
        try
        {
            writer.Dispose();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogError(ex, "Could not finish writing {Segment}; the audio up to its last save is kept", name);
            return;
        }

        if (writer.DataBytes > 0) return;

        // Only reached when the very first write to a new file failed: nothing in it is worth keeping.
        _segments.Remove(writer.Path);
        try
        {
            File.Delete(writer.Path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not remove the empty file {Segment}", name);
        }
    }
}
