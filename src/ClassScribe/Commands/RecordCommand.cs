using System.Globalization;
using ClassScribe.Audio;
using ClassScribe.Desktop;
using ClassScribe.Recorder;
using ClassScribe.Settings;
using ClassScribe.Storage;
using ClassScribe.Transcription;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClassScribe.Commands;

/// <summary>
/// Records system audio until asked to stop and then, if asked to, sends the recording for a transcript.
/// While recording, Q pressed twice stops, P pauses or resumes, and M marks the moment. The settings are
/// those of the run's own scope, taken when it starts, so an edit made during the class counts from the next run.
/// The laptop is kept awake from the start of capture until the transcript is saved. Once a second the recording is
/// watched: a time when ClassScribe did not run (the laptop slept, or Windows held ClassScribe up) is reported, and so is
/// a capture that has stopped delivering audio although it should, which is then reopened.
/// </summary>
public sealed class RecordCommand(
    RecordingSession session,
    TranscriptionWorkflow workflow,
    IUserSignals signals,
    IConsoleUi ui,
    ClassFolders folders,
    ClassMarks marks,
    IDesktop desktop,
    IExitWarnings exitWarnings,
    IPowerRequests power,
    IPowerSource powerSource,
    FreezeWatch freezes,
    IOptionsSnapshot<RecordingOptions> recording,
    IOptionsSnapshot<ElevenLabsOptions> elevenLabs,
    TimeProvider time,
    ILogger<RecordCommand> logger)
{
    /// <summary>How soon after a first Q a second one stops the recording. A single Q, typed into this window by mistake, does not.</summary>
    private static readonly TimeSpan SecondQWithin = TimeSpan.FromSeconds(5);

    /// <summary>How long a capture that hands over audio all the time, silence included, as an app's does, may hand over none at all before it counts as stalled.</summary>
    private static readonly TimeSpan ContinuousArrivalLimit = TimeSpan.FromSeconds(5);

    /// <summary>How long a device's meter may show sound while no audio arrives from its capture before the capture counts as stalled.</summary>
    private static readonly TimeSpan MeterArrivalLimit = TimeSpan.FromSeconds(10);

    /// <summary>The least time between two warnings that audio has stopped arriving, each with a reopening of the capture.</summary>
    private static readonly TimeSpan ArrivalWarningSpacing = TimeSpan.FromSeconds(30);

    /// <summary>When Q was last pressed without stopping the recording, on the steady clock (<see cref="TimeProvider.GetTimestamp"/>), or null.</summary>
    private long? _firstQ;

    /// <summary>
    /// Since when, on the steady clock, ClassScribe has run without a freeze: the start of the recording loop, or the tick
    /// that handed on the last freeze. No audio arrives while ClassScribe does not run, so the arrival watch and the
    /// silence warning count from here at most.
    /// </summary>
    private long _awakeSince;

    /// <summary>Since when, on the steady clock, the recorded device's meter has shown sound at every tick, or null while it does not.</summary>
    private long? _meterSoundSince;

    /// <summary>When the last warning that audio has stopped arriving was given, on the steady clock, or null.</summary>
    private long? _lastArrivalWarning;

    /// <summary>Runs the whole class under <paramref name="name"/>: record, stop and, when <paramref name="transcribe"/> is true, transcribe. Returns the process exit code.</summary>
    public async Task<int> RunAsync(string name, bool transcribe)
    {
        // The menu points this out as well, but a recording started from the command line never shows the
        // menu. It stays a warning: a mistake in the settings must not cost the class.
        if (folders.DescribeRootProblem() is { } problem)
            logger.LogWarning("{Problem}", problem);

        var files = folders.CreateRecording(name);
        var hasKey = !string.IsNullOrWhiteSpace(elevenLabs.Value.ApiKey);
        if (transcribe && !hasKey)
            logger.LogWarning("No ElevenLabs API key found in {Variable}. The class will still be recorded, and you can transcribe it later", ElevenLabsOptions.ApiKeyVariable);

        // Held from the start of capture until the transcript is saved, or with record-only until the files are closed:
        // a laptop that sleeps when it is left alone would end the class, and the upload after it. The screen follows the
        // run's setting, and the power source as Windows reports it every 30 seconds. A refusal is logged, not thrown.
        var screen = recording.Value.KeepScreenOn;
        using var awake = power.Hold(StayAwake.RecordingReason, () => StayAwake.Needs(screen, powerSource));

        RecordingResult result;

        // Closing the window or a crash anywhere in the process ends it within seconds, so the file on
        // disk is brought up to date at once, and the audio captured up to that moment stays usable. A
        // shutdown or a sign-out sends no warning that a console program can catch; see ProcessExitWarnings.
        using (exitWarnings.Register(SaveNow))
        {
            session.Start(files);
            try
            {
                logger.LogInformation("Recording \"{Name}\" to {Folder}", files.Name, files.Folder);
                logger.LogInformation(transcribe
                    ? "Press Q twice (or Ctrl+C) to stop and transcribe, P to pause or resume, M to mark a moment. If the window is closed instead, the audio is kept"
                    : "Press Q twice (or Ctrl+C) to stop, P to pause or resume, M to mark a moment. If the window is closed instead, the audio is kept");
                logger.LogInformation("Keep the lid open and the laptop plugged in; closing the lid still puts it to sleep");
                await WaitForStopAsync(files);
            }
            finally
            {
                result = session.Stop();
            }
        }

        if (result.SegmentPaths.Count == 0)
        {
            logger.LogWarning("No audio was captured. Was anything playing?");
            return 1;
        }

        logger.LogInformation("Recorded {Duration} of audio", Durations.Format(result.Duration));
        if (!transcribe)
        {
            logger.LogInformation("The recording is in {Folder}. To transcribe it later, choose 3 in the menu, or run: {Command}", files.Folder, CommandLine.Transcribe(files.Segment(1)));
            if (recording.Value.OpenFolderWhenDone) desktop.OpenFolder(files.Folder);
            return 0;
        }

        if (!hasKey)
        {
            logger.LogWarning("Set {Variable} and open a new terminal (in VS Code, quit every VS Code window and start it again; a new terminal inside it keeps the old settings). Then start ClassScribe and choose 3 in the menu, or run: {Command}", ElevenLabsOptions.ApiKeyVariable, CommandLine.Transcribe(files.Segment(1)));
            return 1;
        }

        try
        {
            logger.LogInformation("Press Ctrl+C to cancel the upload. The recording is kept either way");
            var saved = await workflow.TranscribeRecordingAsync(files, result.SegmentPaths, signals.Cancellation);
            logger.LogInformation("Transcript saved to {Path}", saved.TranscriptPath);
            desktop.ShowTranscript(recording.Value, saved);
            return 0;
        }
        catch (NoAudioException)
        {
            // No hint to try again: a new try would find the same silence.
            logger.LogWarning(
                "Nothing audible was captured from \"{Source}\", so nothing was sent to ElevenLabs, and trying again would find the same silence. Check that the class plays through that device or app (menu choice 6 picks another) and that Windows is not muted",
                session.GetStatus().DeviceName);
            throw;
        }
        catch (RecordingTooLongException)
        {
            // No hint to try again: a new try would be refused for the same length. The error itself says how to send
            // the recording in pieces.
            logger.LogWarning(
                "The audio is safe in {Folder}. It is longer than ElevenLabs takes in one file, so trying again as it is would be refused the same way",
                files.Folder);
            throw;
        }
        catch (Exception ex)
        {
            // The workflow marks a failure that came after ElevenLabs had charged for its answer, and says where the
            // answer is kept, if anywhere, and its transcription ID when that is known: a new try is charged again, so
            // the hint must not pass for a free retry.
            var command = CommandLine.Transcribe(files.Segment(1));
            switch (ChargedAnswer.Of(ex))
            {
                case { KeptIn: { } answer }:
                    // On the command line, transcribe refuses a recording whose paid answer is kept unless --again is
                    // given, and offers the free rebuild instead (see TranscribeCommand). The commands given here are
                    // those two, so that each runs as printed.
                    logger.LogWarning(
                        "The audio is safe in {Folder}. ElevenLabs' answer, which has been paid for, is kept in {Answer}: look there for the transcript's text first, because transcribing again is charged again",
                        files.Folder, answer);
                    logger.LogInformation("To lay that answer out as a transcript for free, run: {Command}", CommandLine.Rebuild(answer));
                    logger.LogInformation("To transcribe it again all the same, choose 3 in the menu, or run: {Command}", CommandLine.Transcribe(files.Segment(1), again: true));
                    break;
                case { TranscriptionId: { } id }:
                    logger.LogWarning(
                        "The audio is safe in {Folder}. ElevenLabs has charged for this answer, but it could not be saved anywhere. Its transcription ID at ElevenLabs is {TranscriptionId}, with which the transcript can be fetched from ElevenLabs. Transcribing again is charged again. To do so all the same, choose 3 in the menu, or run: {Command}",
                        files.Folder, id, command);
                    break;
                case not null:
                    logger.LogWarning(
                        "The audio is safe in {Folder}. ElevenLabs has charged for this answer, but it could not be saved anywhere, and its transcription ID at ElevenLabs is unknown. Transcribing again is charged again. To do so all the same, choose 3 in the menu, or run: {Command}",
                        files.Folder, command);
                    break;
                default:
                    logger.LogWarning("The audio is safe in {Folder}. To try the transcript again, choose 3 in the menu, or run: {Command}", files.Folder, command);
                    break;
            }

            throw;
        }
    }

    /// <summary>Brings the file on disk up to date because the process is about to end.</summary>
    private void SaveNow()
    {
        try
        {
            session.Flush();
        }
        catch (Exception ex)
        {
            // Deliberately not rethrown: the process is already on its way out, and an exception from
            // a shutdown handler has nowhere to go.
            logger.LogError(ex, "Could not bring the recording on disk up to date before exiting");
        }
    }

    /// <summary>
    /// Waits for the stop signal. Meanwhile it acts on the recording keys, logs progress, and warns when
    /// nothing audible is arriving, unless the recording is paused or nothing can be recorded yet. Each tick
    /// also reports a time when ClassScribe did not run (see <see cref="FreezeWatch"/>, which also ticks on a timer
    /// of its own meanwhile) and watches that audio keeps arriving (see <see cref="WatchArrival"/>). The loop ticks
    /// once a second: the next second starts as soon as one ends, before that second's work, and a key pressed
    /// meanwhile does not put it off, so neither the work nor the keys lose a second or move the next. The intervals
    /// are measured on the steady clock (<see cref="TimeProvider.GetTimestamp"/>), so Windows setting its time of day
    /// neither stops nor hurries the status line or the warnings.
    /// </summary>
    private async Task WaitForStopAsync(RecordingFiles files)
    {
        var statusEvery = TimeSpan.FromSeconds(Math.Max(1, recording.Value.StatusIntervalSeconds));
        var silenceAfter = TimeSpan.FromSeconds(Math.Max(1, recording.Value.SilenceWarningSeconds));
        var lastStatus = time.GetTimestamp();
        long? lastSilenceWarning = null;

        // Starts the freeze watch, and its timer; the arrival watch and the silence warning count from here at most.
        using var freezeTimer = freezes.Start();
        _awakeSince = lastStatus;

        using var stopReading = new CancellationTokenSource();
        Task<ConsoleKeyInfo?>? nextKey = null;
        var tick = Task.Delay(FreezeWatch.TickInterval, time);
        try
        {
            while (!signals.StopRequested.IsCompleted)
            {
                if (ui.IsInteractive) nextKey ??= ui.ReadKeyAsync(stopReading.Token);
                if (nextKey is null)
                    await Task.WhenAny(signals.StopRequested, tick);
                else
                    await Task.WhenAny(signals.StopRequested, tick, nextKey);

                if (tick.IsCompleted) tick = Task.Delay(FreezeWatch.TickInterval, time);

                if (nextKey is { IsCompleted: true })
                {
                    var key = await nextKey;
                    nextKey = null;
                    if (key is { } pressed) HandleKey(pressed, files);
                }

                if (signals.StopRequested.IsCompleted) break;

                var now = time.GetTimestamp();
                if (freezes.Tick() is [_, ..] found)
                {
                    foreach (var freeze in found) ReportFreeze(freeze);

                    // No audio arrives while ClassScribe does not run, so the wait for it starts again now.
                    _awakeSince = now;
                }

                var status = session.GetStatus();
                WatchArrival(status, now);

                // Nothing is heard while ClassScribe does not run either, so the silence counts from the end of the last
                // freeze at most: the time asleep is no sign that the class does not reach the recording.
                var awake = time.GetElapsedTime(_awakeSince, now);
                var silence = status.SinceLastSound < awake ? status.SinceLastSound : awake;
                if (status.WaitingFor is null && !status.Paused && silence >= silenceAfter
                    && (lastSilenceWarning is not { } warned || time.GetElapsedTime(warned, now) >= silenceAfter))
                {
                    lastSilenceWarning = now;
                    logger.LogWarning(
                        "No sound for {Silence}. Check that the class is playing through \"{Device}\" and that Windows is not muted",
                        DescribeSilence(silence), status.DeviceName);
                }

                if (time.GetElapsedTime(lastStatus, now) >= statusEvery)
                {
                    lastStatus = now;
                    var captured = Durations.Format(status.Captured);
                    if (status.WaitingFor is { } waitingFor)
                        logger.LogInformation("Recording: {Captured} captured; waiting for {What}", captured, waitingFor);
                    else
                        logger.LogInformation("Recording: {Captured} captured from \"{Device}\"{Paused}", captured, status.DeviceName, status.Paused ? " (paused)" : "");
                }
            }
        }
        finally
        {
            // A cancelled read answers null within moments, so this only waits for it to let go of the keyboard.
            stopReading.Cancel();
            if (nextKey is not null) await nextKey;
        }
    }

    /// <summary>
    /// Reports a time when ClassScribe did not run, from the last tick before it to the first after it, in local time:
    /// as a sleep when the laptop slept, otherwise as Windows holding ClassScribe up while the laptop stayed awake.
    /// </summary>
    private void ReportFreeze(Freeze freeze)
    {
        var from = LocalTime(freeze.FromUtc);
        var to = LocalTime(freeze.ToUtc);
        if (freeze.Slept)
            logger.LogWarning("The laptop slept from {From} to {To}; nothing was recorded then", from, to);
        else
            logger.LogWarning("ClassScribe was held up from {From} to {To} (Windows paused it); nothing was recorded then", from, to);
    }

    /// <summary>A moment as the local time of day, to the second, such as 19:42:10.</summary>
    private string LocalTime(DateTimeOffset moment) =>
        TimeZoneInfo.ConvertTime(moment, time.LocalTimeZone).ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>
    /// Warns when audio has stopped arriving from the open capture although it should, and has the capture reopened at
    /// the session's next look. Silence is audio too: what counts is whether the capture hands over anything at all, never
    /// how loud it is, so a quiet class is no stall. A capture that hands over audio all the time, silence included, as an
    /// app's does, should never hand over nothing for <see cref="ContinuousArrivalLimit"/>. A device's capture hands over
    /// audio whenever something plays on it, so it is stalled when the device's meter has shown sound at every tick for
    /// <see cref="MeterArrivalLimit"/> while nothing arrived; a tick with the meter quiet starts that count again. At most
    /// once per <see cref="ArrivalWarningSpacing"/>. The time before the last freeze does not count: nothing arrives while
    /// ClassScribe does not run. <paramref name="now"/> is the tick's time on the steady clock.
    /// </summary>
    private void WatchArrival(RecordingStatus status, long now)
    {
        if (status.SinceLastPacket is not { } sinceLastPacket)
        {
            // No capture is open: nothing is awaited from one.
            _meterSoundSince = null;
            return;
        }

        var awake = time.GetElapsedTime(_awakeSince, now);
        var nothingArrived = sinceLastPacket < awake ? sinceLastPacket : awake;
        TimeSpan missing;
        if (status.DeliversContinuously)
        {
            _meterSoundSince = null;
            missing = nothingArrived;
            if (missing < ContinuousArrivalLimit) return;
        }
        else
        {
            if (status.RecordedLevel is { } level && level >= CaptureChooser.SoundLevel)
                _meterSoundSince ??= now;
            else
                _meterSoundSince = null;

            var playing = _meterSoundSince is { } since ? time.GetElapsedTime(since, now) : TimeSpan.Zero;
            missing = nothingArrived < playing ? nothingArrived : playing;
            if (missing < MeterArrivalLimit) return;
        }

        if (_lastArrivalWarning is { } last && time.GetElapsedTime(last, now) < ArrivalWarningSpacing) return;

        _lastArrivalWarning = now;
        var length = DescribeSilence(missing);
        if (!status.DeliversContinuously)
            logger.LogWarning(
                "No audio has arrived from \"{Source}\" for {Length} although Windows shows sound playing there; the recording has fallen behind the clock. Reopening it",
                status.DeviceName, length);
        else if (status.Paused)
            logger.LogWarning(
                "No audio has arrived from \"{Source}\" for {Length} although it should. Reopening it, so that the recording can go on when you press P again",
                status.DeviceName, length);
        else
            logger.LogWarning(
                "No audio has arrived from \"{Source}\" for {Length} although it should; the recording has fallen behind the clock. Reopening it",
                status.DeviceName, length);

        session.Reopen($"no audio has arrived for {length}");
    }

    /// <summary>
    /// How long a silence has lasted, in words that read at a glance: in seconds up to two minutes, then in
    /// whole minutes, and from an hour on in hours and minutes, such as "2 hours 30 minutes".
    /// </summary>
    internal static string DescribeSilence(TimeSpan silence)
    {
        var seconds = (long)silence.TotalSeconds;
        if (seconds <= 120) return Count(seconds, "second");

        var minutes = seconds / 60;
        if (minutes < 60) return Count(minutes, "minute");

        var hours = Count(minutes / 60, "hour");
        return minutes % 60 == 0 ? hours : $"{hours} {Count(minutes % 60, "minute")}";
    }

    /// <summary>A number with its unit, singular for one and plural otherwise, such as "1 minute" or "30 minutes".</summary>
    private static string Count(long number, string unit) =>
        string.Create(CultureInfo.InvariantCulture, $"{number} {unit}{(number == 1 ? "" : "s")}");

    /// <summary>
    /// Acts on a key pressed while recording: Q stops when it follows another Q within five seconds, timed
    /// on the steady clock, P pauses or resumes, M marks the moment. Other keys do nothing.
    /// </summary>
    private void HandleKey(ConsoleKeyInfo key, RecordingFiles files)
    {
        switch (key.Key)
        {
            case ConsoleKey.Q when _firstQ is { } first && time.GetElapsedTime(first) <= SecondQWithin:
                signals.RequestStop();
                break;
            case ConsoleKey.Q:
                _firstQ = time.GetTimestamp();
                logger.LogInformation("Press Q again within {Seconds} seconds to stop", SecondQWithin.TotalSeconds);
                break;
            case ConsoleKey.P when session.GetStatus().Paused:
                session.Resume();
                logger.LogInformation("Resumed");
                break;
            case ConsoleKey.P:
                session.Pause();
                logger.LogInformation("Paused. Nothing is recorded until you press P again");
                break;
            case ConsoleKey.M:
                Mark(files);
                break;
        }
    }

    /// <summary>Writes the current position in the recording to its marks file, so it shows in the transcript.</summary>
    private void Mark(RecordingFiles files)
    {
        var position = session.GetStatus().Captured;
        var shown = Durations.Format(position);
        try
        {
            var number = marks.Add(files, position);
            logger.LogInformation("Mark {Number} at {Position}", number, shown);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Deliberately not rethrown: a mark that cannot be saved must not end the recording.
            logger.LogError(ex, "Could not save the mark at {Position}", shown);
        }
    }
}
