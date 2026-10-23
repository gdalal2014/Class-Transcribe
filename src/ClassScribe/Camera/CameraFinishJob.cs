using System.Globalization;
using ClassScribe.Audio;
using ClassScribe.Link;
using ClassScribe.Storage;
using Microsoft.Extensions.Logging;

namespace ClassScribe.Camera;

/// <summary>
/// The finish job (teach-video 6.9, slice 8): a lesson's camera pieces and the sound of its .wav tracks joined into its
/// finished video, <c>&lt;base&gt;.mp4</c>, in the camera process, which opens no camera for it. The pieces in position
/// order and, before the first of them, an older finished video's picture (a continued lesson whose first pieces were
/// deleted) make the picture, passed through when every source's H.264 sequence header and size are the same and encoded
/// again whole otherwise (<see cref="LessonVideoPlan"/>); the sound is the tracks mixed as the upload is, at 48 kHz. It is
/// written as <c>&lt;base&gt;.mp4.partial</c>, checked (it opens, has both streams, is as long as the longest .wav track
/// within a tenth of a second, and a frame decodes at the start, on each side of every join and at the end), and only then
/// renamed, replacing an older video, the one file ClassScribe replaces, since it is made from the others. Then the
/// camera's pieces are deleted, with a line saying so, unless the job keeps them for Google Drive (<see cref="CameraJob.KeepPieces"/>:
/// only when the video goes there, amendment 47). A video that fails its check, or cannot be made, is deleted, the pieces
/// kept, and failed says why and names finish-video; a cancel does the same and ends with 130. A stale .partial, one that
/// nothing holds open, is deleted first; one held open means another camera process is making the video, and the job is
/// refused. Its media work goes through <see cref="ILessonVideoMedia"/>.
/// </summary>
/// <param name="channel">The lines to and from the starter.</param>
/// <param name="job">The job: the recording's folder and base name, and whether the pieces are kept.</param>
/// <param name="media">Media Foundation's work, or a fake.</param>
/// <param name="time">The clock the progress lines are timed on.</param>
public sealed class CameraFinishJob(LineChannel channel, CameraJob job, ILessonVideoMedia media, TimeProvider time)
{
    /// <summary>The sound's rate in the video: 48 kHz, as YouTube asks.</summary>
    public const int SoundRate = 48_000;

    /// <summary>How often the job looks at how far the writing has got.</summary>
    public static readonly TimeSpan LookEvery = TimeSpan.FromSeconds(1);

    /// <summary>How often a progress line goes out while the writing goes on, even when the percent stands still, so that the starter's time limit never ends a slow but working job.</summary>
    public static readonly TimeSpan ProgressEvery = TimeSpan.FromSeconds(10);

    /// <summary>Ends the writing when the starter cancels, or its end of the lines closes.</summary>
    private readonly CancellationTokenSource _cancel = new();

    /// <summary>How far the writing has got, from 0 to 1, in ten-thousandths, as the media work says it.</summary>
    private int _progress;

    /// <summary>True once a send found the starter gone: nothing more is sent.</summary>
    private volatile bool _starterGone;

    /// <summary>Runs the job and returns the camera process's exit code: 0 made, 1 failed, 4 not taken, 130 cancelled.</summary>
    public async Task<int> RunAsync()
    {
        if (string.IsNullOrWhiteSpace(job.Folder) || string.IsNullOrWhiteSpace(job.BaseName))
        {
            await SendAsync(CameraProtocol.Failed, new CameraFailed("The Finish job names no recording to make the lesson's video of"), CameraJson.Default.CameraFailed);
            return CameraProtocol.JobNotTakenExitCode;
        }

        var files = new RecordingFiles(job.Folder, job.BaseName);
        var control = ReadControlAsync();
        try
        {
            return await FinishAsync(files);
        }
        finally
        {
            await _cancel.CancelAsync();
            await control;
            _cancel.Dispose();
        }
    }

    /// <summary>The job itself, once its recording is known (see the class).</summary>
    private async Task<int> FinishAsync(RecordingFiles files)
    {
        var tracks = new UploadTracks(files.FindSegments(), files.FindSegments(Track.Microphone));
        if (tracks.Main.Count == 0 && tracks.Microphone.Count == 0)
            return await FailAsync(files, $"The sound of {files.BaseName} is not on this laptop, so its video cannot be made here", kept: true);

        if (ClassFolders.IsLessonVideoBeingMade(files))
            return await FailAsync(files, $"The video of {files.BaseName} is being made already, by another ClassScribe", kept: true, retry: false);

        if (File.Exists(files.LessonVideoPartial))
        {
            try
            {
                File.Delete(files.LessonVideoPartial);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Deliberately not rethrown: an unfinished video that another program holds, as a scanner might, cannot make way
                // for the new one; the job says so, and finish-video tries again later.
                return await FailAsync(files, $"{Path.GetFileName(files.LessonVideoPartial)}, an unfinished video an earlier making left, cannot be deleted ({Words(ex)})", kept: true);
            }

            await LogAsync("Information", $"Deleted {Path.GetFileName(files.LessonVideoPartial)}, an unfinished video that an earlier making left");
        }

        var pieces = new List<LessonVideoSource>();
        foreach (var piece in files.FindCameraPieces())
        {
            if (await DescribeAsync(piece.Path, piece.Start) is { } source) pieces.Add(source);
        }

        if (pieces.Count == 0)
            return await FailAsync(files, $"{files.BaseName} has no camera piece that can be read, so it has no picture to make a video of", kept: true, retry: false);

        var older = pieces.Min(piece => piece.Start) > TimeSpan.Zero && File.Exists(files.LessonVideo) ? await DescribeAsync(files.LessonVideo, TimeSpan.Zero) : null;
        var soundLength = Longest(tracks);
        LessonVideoPlan plan;
        try
        {
            plan = LessonVideoPlan.Make(pieces, older, soundLength);
        }
        catch (InvalidDataException ex)
        {
            // Deliberately not rethrown: a lesson with nothing to join (no sound, or no piece before its end) is the job's
            // answer, in words, with the pieces kept.
            return await FailAsync(files, ex.Message, kept: true, retry: false);
        }

        await LogAsync("Information", Describe(files, plan, older is not null && plan.Slices[0].Path == older.Path));
        if (plan.EncodeAgain) await LogAsync("Information", $"{plan.WhyEncodeAgain}, so the whole picture is encoded again, which takes longer than joining the pieces as they are");

        try
        {
            using var sound = new AudioPreparer(new LineLogger<AudioPreparer>(this)).OpenMix(tracks, SoundRate, _cancel.Token);
            if (sound.MicrophoneLeftOut) await LogAsync("Information", "Your microphone stayed silent; the video's sound is the main track's alone");
            await WriteAsync(plan, sound, files.LessonVideoPartial);
        }
        catch (OperationCanceledException)
        {
            // Deliberately not rethrown: the starter's cancel (Ctrl+C after the stop) is the job's end, said in its exit code; the
            // unfinished video goes, and the pieces stay for finish-video.
            TryDelete(files.LessonVideoPartial);
            await LogAsync("Information", $"The making of {Path.GetFileName(files.LessonVideo)} was cancelled; the camera's pieces are kept");
            return CameraProtocol.CancelledExitCode;
        }
        catch (Exception ex)
        {
            // Deliberately not rethrown: a video that cannot be written (the encoder, the disk, a source Windows cannot read)
            // costs nothing else; the unfinished file goes, the pieces stay, and the starter hears why.
            TryDelete(files.LessonVideoPartial);
            return await FailAsync(files, $"The lesson's video could not be written ({Words(ex)})", kept: true);
        }

        await SendAsync(CameraProtocol.Progress, new CameraProgress(99), CameraJson.Default.CameraProgress);
        if (await CheckAsync(files.LessonVideoPartial, new LessonVideoCheck(soundLength, plan.FramesToCheck)) is { } problem)
        {
            TryDelete(files.LessonVideoPartial);
            return await FailAsync(files, $"The lesson's video did not pass its check ({problem})", kept: true);
        }

        File.Move(files.LessonVideoPartial, files.LessonVideo, overwrite: true);
        var bytes = new FileInfo(files.LessonVideo).Length;
        await LogAsync("Information", string.Create(CultureInfo.InvariantCulture,
            $"{Path.GetFileName(files.LessonVideo)} passed its check: {Durations.Format(soundLength)}, picture and sound, a frame decoded at the start, at each of {plan.Joins.Count} {(plan.Joins.Count == 1 ? "join" : "joins")} and at the end"));
        await DealWithThePiecesAsync(files);
        await SendAsync(CameraProtocol.Made, new CameraMade(files.LessonVideo, Math.Round(soundLength.TotalSeconds, 3), bytes), CameraJson.Default.CameraMade);
        return CameraProtocol.DoneExitCode;
    }

    /// <summary>
    /// After the video passed its check: the camera's pieces deleted, said in a line with why, unless the job keeps them for
    /// Google Drive, which is said too. A piece that cannot be deleted stays, with a line.
    /// </summary>
    private async Task DealWithThePiecesAsync(RecordingFiles files)
    {
        var pieces = files.FindCameraPieces();
        if (pieces.Count == 0) return;

        var count = pieces.Count == 1 ? "1 piece" : $"{pieces.Count} pieces";
        if (job.KeepPieces)
        {
            await LogAsync("Information", $"The camera's {count} of {files.BaseName} stay on this laptop until free-space finds the video safe on Google Drive (Drive:LessonVideos)");
            return;
        }

        var deleted = 0;
        foreach (var piece in pieces)
        {
            try
            {
                File.Delete(piece.Path);
                deleted++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Deliberately not rethrown: the video is made and checked; a piece that cannot be deleted now, as one a scanner
                // holds, only takes room, and the line says which stayed and why.
                await LogAsync("Warning", $"{Path.GetFileName(piece.Path)} could not be deleted ({Words(ex)}); it stays on this laptop");
            }
        }

        if (deleted > 0)
            await LogAsync("Information", $"Deleted the camera's {(deleted == 1 ? "1 piece" : $"{deleted} pieces")} of {files.BaseName}: the lesson's video passed its check, and lesson videos do not go to Google Drive (Drive:LessonVideos), so the video is the one copy of the picture kept");
    }

    /// <summary>The source at <paramref name="path"/>, which begins at <paramref name="start"/>; null, with a line, when it cannot be read.</summary>
    private async Task<LessonVideoSource?> DescribeAsync(string path, TimeSpan start)
    {
        try
        {
            return new LessonVideoSource(path, start, await Task.Run(() => media.Describe(path)));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Deliberately not rethrown: a piece that holds nothing readable, as one whose camera process was killed before its
            // first fragment, is left out, and the video is made from the others, with this line naming it.
            await LogAsync("Warning", $"{Path.GetFileName(path)} cannot be read ({Words(ex)}), so it is left out of the lesson's video");
            return null;
        }
    }

    /// <summary>Writes the video on the thread pool, saying how far it has got at least every <see cref="ProgressEvery"/>, and at every new percent.</summary>
    private async Task WriteAsync(LessonVideoPlan plan, MixedSound sound, string path)
    {
        var writing = Task.Run(() => media.Write(plan, sound, path, fraction => Volatile.Write(ref _progress, (int)Math.Clamp(fraction * 10_000, 0, 10_000)), _cancel.Token));
        var lastPercent = -1;
        var lastSent = time.GetTimestamp();
        await SendAsync(CameraProtocol.Progress, new CameraProgress(0), CameraJson.Default.CameraProgress);
        while (!writing.IsCompleted)
        {
            await Task.WhenAny(writing, Task.Delay(LookEvery, time));
            var percent = Math.Min(98, Volatile.Read(ref _progress) / 100);
            if (percent == lastPercent && time.GetElapsedTime(lastSent) < ProgressEvery) continue;

            lastPercent = percent;
            lastSent = time.GetTimestamp();
            await SendAsync(CameraProtocol.Progress, new CameraProgress(percent), CameraJson.Default.CameraProgress);
        }

        await writing;
    }

    /// <summary>Checks the video on the thread pool; a check that throws is a check failed, in words.</summary>
    private async Task<string?> CheckAsync(string path, LessonVideoCheck check)
    {
        try
        {
            return await Task.Run(() => media.Check(path, check));
        }
        catch (Exception ex)
        {
            // Deliberately not rethrown: a video Windows cannot even open for the check has failed it; the caller deletes it and
            // keeps the pieces, with this as why.
            return $"it could not be read back: {Words(ex)}";
        }
    }

    /// <summary>The length of the longer of the two tracks, each the sum of its segments read to their ends, as the check compares it.</summary>
    private static TimeSpan Longest(UploadTracks tracks)
    {
        var main = tracks.Main.Aggregate(TimeSpan.Zero, (length, segment) => length + ClassFolders.MeasureSegment(segment));
        var microphone = tracks.Microphone.Aggregate(TimeSpan.Zero, (length, segment) => length + ClassFolders.MeasureSegment(segment));
        return main >= microphone ? main : microphone;
    }

    /// <summary>The line that says what the video is made from: the pieces, an older video before them, and whether the picture is passed through.</summary>
    private static string Describe(RecordingFiles files, LessonVideoPlan plan, bool withOlder)
    {
        var pieces = plan.Slices.Count - (withOlder ? 1 : 0);
        return $"Making {Path.GetFileName(files.LessonVideo)} from the camera's {(pieces == 1 ? "1 piece" : $"{pieces} pieces")}{(withOlder ? " and the older video before them" : "")} and the lesson's sound ({Durations.Format(plan.End)}), {plan.Size.Width}x{plan.Size.Height}, the picture {(plan.EncodeAgain ? "encoded again" : "passed through as it is")}";
    }

    /// <summary>
    /// Sends failed with <paramref name="why"/>, the pieces said to be kept when <paramref name="kept"/>, and how to make the
    /// video again for free when <paramref name="retry"/>; returns the failed exit code.
    /// </summary>
    private async Task<int> FailAsync(RecordingFiles files, string why, bool kept, bool retry = true)
    {
        var words = why.TrimEnd('.');
        if (kept && files.FindCameraPieces().Count > 0) words += ". The camera's pieces are kept";
        if (retry) words += $". To make it again, free: {Commands.CommandLine.FinishVideo(files.FindSegments().FirstOrDefault() ?? files.FindSegments(Track.Microphone).FirstOrDefault() ?? files.Folder)}";
        await SendAsync(CameraProtocol.Failed, new CameraFailed(words), CameraJson.Default.CameraFailed);
        return CameraProtocol.FailedExitCode;
    }

    /// <summary>
    /// Reads the starter's lines until its cancel, or its end closing, either of which cancels the writing; a line this job
    /// does not take is passed over.
    /// </summary>
    private async Task ReadControlAsync()
    {
        try
        {
            while (await channel.ReceiveAsync(_cancel.Token) is { } line)
            {
                if (line.Type != CameraProtocol.Cancel) continue;

                await _cancel.CancelAsync();
                return;
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or InvalidDataException or ObjectDisposedException)
        {
            // Deliberately not rethrown: the job is ending, or the starter is gone or spoke what cannot be read; the job's own end,
            // or the cancel below, follows.
        }

        await _cancel.CancelAsync();
    }

    /// <summary>Sends a log line.</summary>
    private Task LogAsync(string level, string text) => SendAsync(CameraProtocol.Log, new CameraLog(level, text), CameraJson.Default.CameraLog);

    /// <summary>Sends a line to the starter; a starter that is gone cancels the job, and nothing more is sent.</summary>
    private async Task SendAsync<T>(string type, T body, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info)
    {
        if (_starterGone) return;
        try
        {
            await channel.SendAsync(type, body, info, CancellationToken.None);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // Deliberately not rethrown: the starter is gone, so nobody is left to tell; the job is cancelled, which deletes its
            // unfinished video.
            _starterGone = true;
            await _cancel.CancelAsync();
        }
    }

    /// <summary>Deletes <paramref name="path"/> if it is there; one that cannot be deleted is left for the next making, which deletes it first.</summary>
    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Deliberately not rethrown: the failure being said is the job's own; a stale .partial blocks nothing and is
            // deleted by the next making.
        }
    }

    /// <summary>An exception in a few words: Windows' HRESULT with its name and words for a COM failure, otherwise its own words' first line.</summary>
    private static string Words(Exception ex) =>
        ex is System.Runtime.InteropServices.COMException ? CameraCallLog.Describe(ex).Error : ex.Message.Split('\n')[0].Trim().TrimEnd('.');

    /// <summary>
    /// A logger whose lines go to the starter as the job's log lines, at their levels, for the sound's reader (which says the
    /// segments it skips). It never throws: a line that cannot be sent is lost with the starter.
    /// </summary>
    /// <typeparam name="T">The category's type.</typeparam>
    /// <param name="owner">The job whose lines these are.</param>
    private sealed class LineLogger<T>(CameraFinishJob owner) : ILogger<T>
    {
        /// <inheritdoc />
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        /// <inheritdoc />
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Debug;

        /// <inheritdoc />
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var text = formatter(state, exception);
            if (exception is not null) text += $" ({Words(exception)})";
            var level = logLevel switch
            {
                >= LogLevel.Error => "Error",
                LogLevel.Warning => "Warning",
                LogLevel.Information => "Information",
                _ => "Debug"
            };
            _ = owner.LogAsync(level, text);
        }
    }
}
