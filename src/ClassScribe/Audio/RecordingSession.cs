using ClassScribe.Settings;
using ClassScribe.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClassScribe.Audio;

/// <summary>What a finished recording produced.</summary>
public sealed record RecordingResult(string Folder, IReadOnlyList<string> SegmentPaths, TimeSpan Duration);

/// <summary>A snapshot of a running recording. <see cref="WaitingFor"/> says what is awaited while nothing can be recorded yet.</summary>
public sealed record RecordingStatus(TimeSpan Captured, TimeSpan SinceLastSound, string DeviceName, int SegmentCount, bool Paused, string? WaitingFor);

/// <summary>
/// Records an output device into WAV segment files: the device the class is playing through, or the
/// one chosen by name (see <see cref="CaptureChooser"/>). The header of the open segment is
/// rewritten every few seconds, so a crash keeps the audio. A new segment is started when the
/// recording moves to another device, when the capture or the disk fails, or when a segment grows too large.
/// </summary>
/// <remarks>
/// Two locks, never taken in the other order: the lifecycle semaphore allows one start, stop or
/// restart at a time, and the gate guards the state the capture thread shares. A capture is never
/// released while the gate is held, because releasing it waits for the capture thread.
/// Every interval is measured on the steady clock (<see cref="TimeProvider.GetTimestamp"/>), so Windows
/// setting its time of day neither stretches nor shortens one.
/// </remarks>
public sealed class RecordingSession(
    ILoopbackCaptureProvider provider,
    CaptureChooser chooser,
    IOptions<RecordingOptions> options,
    TimeProvider time,
    ILogger<RecordingSession> logger) : IDisposable
{
    /// <summary>Samples at or above this level (out of 32767) count as audible sound.</summary>
    private const int SoundThreshold = 100;

    /// <summary>The least time between two replacements of a capture that keeps ending.</summary>
    private static readonly TimeSpan RestartSpacing = TimeSpan.FromSeconds(2);

    /// <summary>When writing fails twice in a row, audio is dropped for this long before a new segment file is tried.</summary>
    private static readonly TimeSpan WriteRetryDelay = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How often the session looks at the output devices by itself, to see where sound is playing.
    /// Windows announces some changes at once; this catches the rest, such as a browser sent to another
    /// device in the per-app sound settings, which no announcement covers. The same look opens a capture
    /// that could not be opened before and looks again while there is nothing to record, so it is the one
    /// way anything is tried again: no failure schedules a retry of its own. The log says "every second".
    /// </summary>
    private static readonly TimeSpan DeviceCheckInterval = TimeSpan.FromSeconds(1);

    /// <summary>While captures keep failing, or there is still nothing to record, that is said at most this often.</summary>
    private static readonly TimeSpan ReportInterval = TimeSpan.FromSeconds(30);

    /// <summary>How long <see cref="Stop"/> waits for a device change in progress before it stops without it (see <see cref="StopWithoutWaiting"/>).</summary>
    private static readonly TimeSpan StopWaitLimit = TimeSpan.FromSeconds(20);

    private readonly RecordingOptions _options = options.Value;
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly List<string> _segments = [];

    private IAudioCapture? _capture;
    private IAudioCapture? _leftBehind;
    private ITimer? _deviceCheck;
    private bool _captureEnded;
    private WavSegmentWriter? _writer;
    private RecordingResult? _result;
    private byte[] _convertBuffer = [];
    private RecordingFiles? _files;
    private string _folder = "";
    private string _deviceName = "";
    private TimeSpan _closedDuration;
    private long _lastFlush;
    private long _lastSound;
    private long? _captureSound;
    private long? _writePauseStarted;
    private int _writeFailures;
    private long? _lastEndedRestart;
    private long? _lastReport;
    private bool _started;
    private bool _paused;
    private volatile bool _stopping;
    private int _restartQueued;

    private long MaxSegmentBytes => Math.Max(1, _options.MaxSegmentMegabytes) * 1024L * 1024L;

    private TimeSpan FlushInterval => TimeSpan.FromSeconds(Math.Max(1, _options.FlushSeconds));

    /// <summary>
    /// Starts capturing into the segment files of <paramref name="files"/>. When there is nothing to record
    /// yet, or the capture cannot be opened yet, the recording waits and the device check keeps trying;
    /// only a device in a format this app cannot read makes the start fail.
    /// </summary>
    public void Start(RecordingFiles files)
    {
        _lifecycle.Wait();
        try
        {
            if (_started)
                throw new InvalidOperationException("This recording session has already been started.");

            _started = true;
            _files = files;
            _folder = files.Folder;
            Directory.CreateDirectory(files.Folder);
            _lastSound = time.GetTimestamp();
            provider.DevicesChanged += OnDevicesChanged;
            try
            {
                if (chooser.Choose(provider, recordedId: null, sinceRecordedSound: null) is { } target)
                {
                    StartCapture(target);
                }
                else
                {
                    // Nothing to record yet: a chosen device not connected, a chosen app not playing, or no
                    // device at all. The device check keeps looking and starts the capture once it is there.
                    _lastReport = time.GetTimestamp();
                    logger.LogWarning("Nothing to record yet; waiting for {What}", chooser.WaitingFor);
                }
            }
            catch (NotSupportedException ex)
            {
                // A device in a format this app cannot read stays that way, so the run ends now rather than
                // waiting for something that will not happen.
                logger.LogError(ex, "Could not start capturing system audio");
                provider.DevicesChanged -= OnDevicesChanged;
                throw;
            }
            catch (Exception ex)
            {
                // Deliberately not rethrown: Windows may still be setting up a device that was just connected.
                // The device check tries again every second, as it does when this happens later in the class.
                _lastReport = time.GetTimestamp();
                logger.LogWarning(ex, "Could not start capturing system audio yet; trying again every second");
            }

            _deviceCheck = time.CreateTimer(_ => QueueRestart(), null, DeviceCheckInterval, DeviceCheckInterval);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    /// <summary>
    /// Stops capturing, finalises the files and returns what was recorded. A device change that Windows holds
    /// up for longer than <see cref="StopWaitLimit"/> is not waited for. Safe to call twice: a second call
    /// returns the same result at once.
    /// </summary>
    public RecordingResult Stop()
    {
        lock (_gate)
        {
            // Already stopped: even a device change that Windows is still holding up is not waited for again.
            if (_result is not null) return _result;
        }

        if (!EnterLifecycleToStop()) return StopWithoutWaiting();
        try
        {
            IAudioCapture? capture;
            lock (_gate)
            {
                if (_result is not null) return _result;
                _stopping = true;
                capture = _capture;
            }

            provider.DevicesChanged -= OnDevicesChanged;
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
                return _result = new RecordingResult(_folder, [.. _segments], _closedDuration);
            }
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
            return new RecordingStatus(captured, time.GetElapsedTime(_lastSound), _deviceName, _segments.Count, _paused, _capture is null ? chooser.WaitingFor : null);
        }
    }

    /// <summary>
    /// Stops keeping captured audio until <see cref="Resume"/>. The capture itself keeps running, so a
    /// switch of output device during the break is still followed.
    /// </summary>
    public void Pause()
    {
        lock (_gate)
        {
            if (_paused) return;
            _paused = true;

            // A break can be long, so the file on disk is brought up to date now rather than at the next save.
            try
            {
                _writer?.Flush();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Deliberately not rethrown: what reached the disk stays readable, and the next audio after
                // the break goes to a new file, as after any other write failure.
                logger.LogError(ex, "Could not bring the recording on disk up to date for the pause");
                CloseSegmentLocked(failureReported: true);
            }
        }
    }

    /// <summary>Keeps captured audio again after <see cref="Pause"/>.</summary>
    public void Resume()
    {
        lock (_gate)
        {
            if (!_paused) return;
            _paused = false;

            // The silence warning counts from now: the quiet of the break itself is no cause for one.
            _lastSound = time.GetTimestamp();
        }
    }

    /// <summary>
    /// Writes the current header to disk right away. Used when the process is about to exit. A failure is logged
    /// and handed on. The open file stays open: a failed save leaves its writer as it was, so the audio that still
    /// arrives goes after the audio already in it, and the next save, at the latest when the recording stops,
    /// brings the header up to date. Closing the file instead would leave its header behind for good.
    /// </summary>
    public void Flush()
    {
        lock (_gate)
        {
            if (_writer is not { } writer) return;
            try
            {
                writer.Flush();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogError(ex, "Could not bring {Segment} on disk up to date; the audio written to it is kept", Path.GetFileName(writer.Path));
                throw;
            }
        }
    }

    /// <summary>Stops the recording if it is still running.</summary>
    public void Dispose()
    {
        if (_started && _result is null) Stop();
    }

    /// <summary>
    /// Takes the lifecycle lock for <see cref="Stop"/>. Returns false, without it, when a device change has
    /// held it for <see cref="StopWaitLimit"/>: a driver that has stopped answering must not leave Q unanswered.
    /// </summary>
    private bool EnterLifecycleToStop()
    {
        if (_lifecycle.Wait(0)) return true;

        using var patience = new CancellationTokenSource(StopWaitLimit, time);
        try
        {
            _lifecycle.Wait(patience.Token);
            return true;
        }
        catch (OperationCanceledException) when (patience.IsCancellationRequested)
        {
            // Deliberately not rethrown: running out of patience is the answer here, not a failure. The
            // caller stops without the lock.
            return false;
        }
    }

    /// <summary>
    /// Ends the recording while a device change is stuck inside Windows, so that Q is answered and the
    /// transcript can follow. The open file is finalised and nothing more is recorded. The capture the
    /// change is working on is left to it, because releasing a capture that Windows is still busy with could
    /// take the process down. It is released once: by the change, if the change releases it on its way out
    /// (see <see cref="ClaimLocked"/>), or else as soon as the change is over (see <see cref="ReleaseLeftBehind"/>).
    /// </summary>
    private RecordingResult StopWithoutWaiting()
    {
        RecordingResult result;
        lock (_gate)
        {
            if (_result is not null) return _result;
            _stopping = true;
            _leftBehind = _capture;
            _capture = null;   // whatever it still delivers is ignored from here on
            CloseSegmentLocked();
            result = _result = new RecordingResult(_folder, [.. _segments], _closedDuration);
        }

        logger.LogError(
            "Windows has not finished changing the audio device for {Seconds} seconds, so the recording stops without waiting for it. Everything recorded so far is kept",
            StopWaitLimit.TotalSeconds);
        provider.DevicesChanged -= OnDevicesChanged;
        _deviceCheck?.Dispose();

        // The change may have ended in the meantime, and then nobody else is left to release the capture.
        if (_lifecycle.Wait(0))
        {
            _lifecycle.Release();
            ReleaseLeftBehind();
        }

        return result;
    }

    /// <summary>Opens a capture of <paramref name="target"/>. Its segment file is created when its first audio arrives.</summary>
    private void StartCapture(CaptureTarget target)
    {
        var capture = provider.CreateCapture(target);
        try
        {
            if (!PcmConverter.IsSupported(capture.Format))
                throw new NotSupportedException(
                    $"The output device \"{capture.DeviceName}\" uses an audio format this app cannot read ({capture.Format}).");

            capture.DataAvailable += OnDataAvailable;
            capture.Stopped += OnCaptureStopped;
            bool recording;
            lock (_gate)
            {
                // The recording only stops while a capture is being opened after Stop gave up waiting for it.
                recording = !_stopping;
                if (recording)
                {
                    _capture = capture;
                    _captureEnded = false;
                    _captureSound = null;
                    _deviceName = capture.DeviceName;
                }
            }

            if (recording) capture.Start();
        }
        catch
        {
            lock (_gate)
            {
                if (ReferenceEquals(_capture, capture)) _capture = null;
                ClaimLocked(capture);
            }

            ReleaseCapture(capture);
            throw;
        }

        bool kept;
        lock (_gate)
        {
            kept = ReferenceEquals(_capture, capture);
            if (!kept) ClaimLocked(capture);
        }

        if (!kept)
        {
            // The recording was stopped while Windows held this capture up, so it has nothing left to record into.
            ReleaseQuietly(capture);
            return;
        }

        logger.LogInformation("Capturing \"{Device}\" at {SampleRate} Hz", capture.DeviceName, capture.Format.SampleRate);
    }

    /// <summary>Converts incoming audio to mono 16-bit and appends it to the open segment. Runs on the capture thread.</summary>
    private void OnDataAvailable(object? sender, AudioDataEventArgs e)
    {
        if (e.ByteCount <= 0) return;

        lock (_gate)
        {
            if (_capture is null || !ReferenceEquals(sender, _capture) || _paused) return;

            var format = _capture.Format;
            var needed = e.ByteCount / format.BytesPerFrame * 2;
            if (_convertBuffer.Length < needed) _convertBuffer = new byte[needed];

            var written = PcmConverter.ToMono16(e.Buffer.AsSpan(0, e.ByteCount), format, _convertBuffer);
            var mono = _convertBuffer.AsSpan(0, written);
            if (mono.IsEmpty) return;

            var now = time.GetTimestamp();
            if (PcmConverter.Peak(mono) >= SoundThreshold)
            {
                _lastSound = now;
                _captureSound = now;
            }

            // While the disk keeps failing, the audio is dropped until the next attempt is due.
            if (_writer is null && _writePauseStarted is { } pauseStarted && time.GetElapsedTime(pauseStarted, now) < WriteRetryDelay) return;

            try
            {
                var writer = _writer ?? OpenSegmentLocked(format.SampleRate);
                writer.Write(mono);

                if (time.GetElapsedTime(_lastFlush, now) >= FlushInterval)
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
                    _writePauseStarted = now;
                    logger.LogError(ex, "Still cannot write the recording to {Folder}; audio is being lost. Trying again in {Seconds} seconds", _folder, WriteRetryDelay.TotalSeconds);
                }

                CloseSegmentLocked(failureReported: true);
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

    /// <summary>Has the output devices looked at again. Runs on a thread that belongs to Windows.</summary>
    private void OnDevicesChanged(object? sender, EventArgs e) => QueueRestart();

    /// <summary>
    /// Schedules one restart check on the thread pool. Requests made before it begins are merged into it.
    /// The check itself decides whether anything needs doing, so asking too often costs next to nothing.
    /// </summary>
    private void QueueRestart()
    {
        if (_stopping || Interlocked.Exchange(ref _restartQueued, 1) == 1) return;
        _ = Task.Run(RestartAsync);
    }

    /// <summary>
    /// Runs one restart check. It schedules nothing further: when the check could not finish the job, the
    /// device check a second later tries again, so there is never more than one check waiting to run.
    /// </summary>
    private async Task RestartAsync()
    {
        await _lifecycle.WaitAsync();

        // The check's own time, read once before anything that can be slow, so that how long Windows takes
        // to answer does not change what the check decides.
        var now = time.GetTimestamp();
        try
        {
            Interlocked.Exchange(ref _restartQueued, 0);
            if (_stopping) return;

            if (CheckDevices(now)) _lastReport = null;
        }
        catch (Exception ex)
        {
            // This is a background task with nobody to rethrow to, so the failure is logged, now and then
            // rather than at every try, and the next device check tries again. Once the recording has ended
            // (Stop gave up waiting for this check), nothing is tried again and the failure no longer matters to it.
            if (_stopping)
                logger.LogDebug(ex, "The device change the recording stopped without waiting for has failed");
            else if (TimeToReport(now))
                logger.LogWarning(ex, "Cannot capture an output device right now; trying again every second");
        }
        finally
        {
            _lifecycle.Release();

            // A capture that a stopped recording could not wait for is released once the change that held it is over.
            if (_stopping) ReleaseLeftBehind();
        }
    }

    /// <summary>
    /// Replaces the capture when there is none, when it has ended, or when the chooser finds the sound
    /// playing on another device. Otherwise it does nothing, so several notifications about one change
    /// cost a single restart. <paramref name="now"/> is when the check began, on the steady clock. Returns
    /// true when the session is settled, false when a later check has to try again.
    /// </summary>
    /// <remarks>
    /// Looking at the devices and releasing a capture are calls Windows can hold up. When the recording was
    /// stopped without waiting meanwhile (see <see cref="StopWithoutWaiting"/>), the check ends as soon as such
    /// a call returns: nothing it found is acted on and nothing more is opened for a recording that has ended.
    /// </remarks>
    private bool CheckDevices(long now)
    {
        IAudioCapture? old;
        bool ended;
        bool paused;
        long? captureSound;
        lock (_gate)
        {
            old = _capture;
            ended = _captureEnded;
            paused = _paused;
            captureSound = _captureSound;
        }

        if (ended)
        {
            // A capture that ends again right after being replaced must not become a tight loop of restarts.
            if (_lastEndedRestart is { } last && time.GetElapsedTime(last, now) < RestartSpacing)
            {
                logger.LogDebug("The capture ended again right after it was started; it is replaced once {Seconds} seconds have passed", RestartSpacing.TotalSeconds);
                return false;
            }

            _lastEndedRestart = now;
            Replace(old);
            old = null;
        }

        if (old is null)
        {
            var target = chooser.Choose(provider, recordedId: null, sinceRecordedSound: null);
            if (_stopping) return true;
            if (target is null)
            {
                if (TimeToReport(now)) logger.LogWarning("Still nothing to record; waiting for {What}", chooser.WaitingFor);
                return false;
            }

            StartCapture(target);
            return true;
        }

        // Nothing is kept while paused, so there is nothing to move; and a device Windows no longer
        // lists is left to the capture, which ends by itself once its device is really gone.
        if (paused) return true;
        var sinceSound = captureSound is { } heard ? time.GetElapsedTime(heard) : (TimeSpan?)null;
        var chosen = chooser.Choose(provider, old.DeviceId, sinceSound);
        if (_stopping || chosen is null || string.Equals(chosen.Id, old.DeviceId, StringComparison.OrdinalIgnoreCase)) return true;

        if (chooser.IsApp)
            logger.LogInformation("\"{App}\" now plays from another process; following it", chosen.Name);
        else if (chooser.ChosenDevice is not null)
            logger.LogInformation("Recording \"{Device}\", the chosen output device, instead of \"{Old}\"", chosen.Name, old.DeviceName);
        else
            logger.LogInformation("Sound is playing on \"{Device}\"; recording it instead of \"{Old}\"", chosen.Name, old.DeviceName);

        Replace(old);
        if (_stopping) return true;

        StartCapture(chosen);
        return true;
    }

    /// <summary>
    /// True when a problem that lasts is due to be reported by the check that began at <paramref name="now"/>:
    /// at once, then every <see cref="ReportInterval"/> while it lasts. A settled check starts the count afresh.
    /// </summary>
    private bool TimeToReport(long now)
    {
        if (_lastReport is { } last && time.GetElapsedTime(last, now) < ReportInterval) return false;

        _lastReport = now;
        return true;
    }

    /// <summary>Releases the capture and closes its segment, so the next capture starts a new one.</summary>
    private void Replace(IAudioCapture? old)
    {
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
                ClaimLocked(old);
                CloseSegmentLocked();
            }
        }
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

    /// <summary>Releases the capture <see cref="StopWithoutWaiting"/> had to leave behind, if there is one and nobody has claimed it.</summary>
    private void ReleaseLeftBehind()
    {
        IAudioCapture? capture;
        lock (_gate)
        {
            capture = _leftBehind;
            _leftBehind = null;
        }

        if (capture is not null) ReleaseQuietly(capture);
    }

    /// <summary>
    /// Claims the release of <paramref name="capture"/> for the caller, which releases it itself. If
    /// <see cref="StopWithoutWaiting"/> left it behind, it is no longer left to <see cref="ReleaseLeftBehind"/>,
    /// so it is released once. Call with <see cref="_gate"/> held.
    /// </summary>
    private void ClaimLocked(IAudioCapture? capture)
    {
        if (capture is not null && ReferenceEquals(_leftBehind, capture)) _leftBehind = null;
    }

    /// <summary>Releases a capture that belongs to a recording that has already stopped.</summary>
    private void ReleaseQuietly(IAudioCapture capture)
    {
        try
        {
            ReleaseCapture(capture);
        }
        catch (Exception ex)
        {
            // Deliberately not rethrown: the recording is over and its result handed out; this only tidies up after it.
            logger.LogWarning(ex, "The audio device did not shut down cleanly");
        }
    }

    /// <summary>Creates the next segment file. Call with <see cref="_gate"/> held.</summary>
    private WavSegmentWriter OpenSegmentLocked(int sampleRate)
    {
        // The next number is normally free. A file that is already there is never overwritten, so its number is skipped.
        var number = _segments.Count + 1;
        string path;
        while (File.Exists(path = _files!.Segment(number))) number++;

        var writer = new WavSegmentWriter(path, sampleRate);
        _writer = writer;
        _segments.Add(path);
        _lastFlush = time.GetTimestamp();
        return writer;
    }

    /// <summary>
    /// Finalises the open segment. Call with <see cref="_gate"/> held. This never throws: a segment that
    /// cannot be finalised (its header locked or its drive gone, say) keeps what reached the disk, and losing
    /// its last moments must not cost the rest of the recording. Only audio that reached the disk is counted,
    /// so the captured length, and the marks set from it, keep matching what will be transcribed; a file left
    /// with no audio in it is removed. <paramref name="failureReported"/> says the caller has already reported
    /// the failure that brought it here.
    /// </summary>
    private void CloseSegmentLocked(bool failureReported = false)
    {
        if (_writer is null) return;

        var writer = _writer;
        var name = Path.GetFileName(writer.Path);
        _writer = null;
        long audioBytes;
        try
        {
            writer.Dispose();
            audioBytes = writer.DataBytes;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Deliberately not rethrown (see above). The header is behind, so what counts is what the file
            // holds on disk, which is what transcribing it reads.
            if (failureReported)
                logger.LogDebug(ex, "Could not finish writing {Segment}", name);
            else
                logger.LogError(ex, "Could not finish writing {Segment}; the audio that reached the disk is kept", name);

            if (AudioBytesOnDisk(writer.Path) is not { } onDisk) return;   // cannot be looked at: left as it is, and not counted
            audioBytes = onDisk;
        }

        _closedDuration += TimeSpan.FromSeconds(audioBytes / (2.0 * writer.SampleRate));
        if (audioBytes > 0) return;

        // No audio reached the file, because the first write to it failed: nothing in it is worth keeping.
        _segments.Remove(writer.Path);
        try
        {
            File.Delete(writer.Path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Deliberately not rethrown: a file with no audio is harmless where it is, and it is no longer part of the recording.
            logger.LogWarning(ex, "Could not remove the empty file {Segment}", name);
        }
    }

    /// <summary>
    /// The bytes of audio a segment file holds on disk, in whole samples, which is what transcribing it reads;
    /// none when the file is not there. Null when the file cannot be looked at.
    /// </summary>
    private long? AudioBytesOnDisk(string path)
    {
        try
        {
            var audio = Math.Max(0, new FileInfo(path).Length - WavSegmentWriter.HeaderSize);
            return audio - audio % 2;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            // Deliberately not rethrown: a file that is not there holds no audio, which is the answer.
            logger.LogDebug(ex, "{Segment} is not on disk", Path.GetFileName(path));
            return 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Deliberately not rethrown: the drive may have gone with the failed write. Nothing is known about
            // the file then, so it is left as it is.
            logger.LogDebug(ex, "Could not look at {Segment} on disk", Path.GetFileName(path));
            return null;
        }
    }
}
