using System.Globalization;
using ClassScribe.Audio;
using ClassScribe.Commands;
using ClassScribe.Desktop.Windows;
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
/// renamed, replacing an older video, the one file ClassScribe replaces, since it is made from the others (tried
/// <see cref="RenameTries"/> times while another program holds a file open, then failed with why). Then the camera's pieces
/// it read are deleted, with a line saying so, unless the job keeps them for Google Drive (<see cref="CameraJob.KeepPieces"/>:
/// only when the video goes there, amendment 47); a piece left out because what it holds cannot be read is in no video, so
/// it stays, named. A piece Windows cannot open now, or an older video that cannot be read, ends the job with every piece
/// kept, so that no picture is lost to a video made without it. A piece older than the older video and beginning before it
/// ends was there when that video was made, which holds its picture (a leftover an earlier making could not delete, or a
/// piece kept for Drive): with the older video's picture in use before the first newer piece, such a piece is left out, with
/// a line, and with no newer piece at all there is nothing to add, the job fails with why, and the older video stays, so that
/// a remake from a leftover alone never drops the picture only the older video held (review 3.2); a piece beginning at or
/// past the older video's end is fresh whatever its write time says. A damaged .wav part, which the mix skips as the upload
/// does, fails the job with the part named and the pieces kept, since the sound written would be shorter than the lesson's
/// and the picture after the part out of step with it, which the check cannot tell (review 3.14). A video that fails its check, or cannot be made, is deleted, the pieces
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
    /// <summary>The lesson's files on disk, by the names its record gives.</summary>
    private static readonly RecordingFolder Recordings = new();

    /// <summary>The sound's rate in the video: 48 kHz, as YouTube asks.</summary>
    public const int SoundRate = 48_000;

    /// <summary>How often the job looks at how far the writing has got.</summary>
    public static readonly TimeSpan LookEvery = TimeSpan.FromSeconds(1);

    /// <summary>How often the same percent is said again while the work moves below the next one, so that the starter's time limit never ends a slow but working job; a job that stands still says nothing more.</summary>
    public static readonly TimeSpan ProgressEvery = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How many times the checked video is tried in its place, <see cref="LookEvery"/> apart, while another program holds the
    /// older video or the new one open, as a scanner does for a moment, before the job fails with why.
    /// </summary>
    public const int RenameTries = 3;

    /// <summary>Ends the writing when the starter cancels, or its end of the lines closes.</summary>
    private readonly CancellationTokenSource _cancel = new();

    /// <summary>How far the writing has got, from 0 to 1, in ten-thousandths, as the media work says it.</summary>
    private int _progress;

    /// <summary>How many of the frames the check decodes it has decoded so far, as the media work says it.</summary>
    private int _checked;

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
        var tracks = new UploadTracks(Recordings.FindSegments(files), Recordings.FindSegments(files, Track.Microphone));
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
        foreach (var piece in Recordings.FindCameraPieces(files))
        {
            var (source, why, notNow) = await DescribeAsync(piece.Path, piece.Start);
            if (notNow)
                return await FailAsync(files, $"{Path.GetFileName(piece.Path)} could not be opened now ({why}), so the video was not made without its picture", kept: true);

            if (source is not null)
            {
                pieces.Add(source);
                continue;
            }

            await LogAsync("Warning", $"{Path.GetFileName(piece.Path)} cannot be read ({why}), so it is left out of the lesson's video");
        }

        if (pieces.Count == 0)
            return await FailAsync(files, $"{files.BaseName} has no camera piece that can be read, so it has no picture to make a video of", kept: true, retry: false);

        LessonVideoSource? older = null;
        List<LessonVideoSource> heldByOlder = [];
        if (File.Exists(files.LessonVideo))
        {
            var video = Path.GetFileName(files.LessonVideo);
            var (source, why, notNow) = await DescribeAsync(files.LessonVideo, TimeSpan.Zero);
            if (source is null && notNow)
                return await FailAsync(files, $"{video}, the older video, could not be opened now ({why}), so the video was not made without its picture", kept: true);

            if (source is null && pieces.Min(piece => piece.Start) > TimeSpan.Zero)
            {
                // Its picture before the first piece is in no piece any more, so a video made without it would lose it for good.
                return await FailAsync(files,
                    $"{video}, the older video, cannot be read ({why}), and its picture before the first camera piece is in no piece, so a new video would lose it. Move the older video out of the folder to make one from the pieces alone",
                    kept: true, retry: false);
            }

            if (source is not null)
            {
                // A piece older than the video, beginning before the video ends, was there when the video was made, so the video
                // holds its picture (review 3.2): a leftover of an earlier making that could not be deleted then, or a piece kept
                // for Drive. A video made from such a piece alone would hold its last frame where the older video has picture, so
                // the older video's picture stands for it, and only pieces newer than the video, a continued lesson's, add
                // anything. One beginning at or past the older video's end is in no part of it, whatever its write time says (the
                // Windows clock set back between the makings, or pieces restored with their times kept), so it is fresh.
                older = source;
                var madeUtc = File.GetLastWriteTimeUtc(files.LessonVideo);
                heldByOlder = [.. pieces.Where(piece => piece.Start < source.Facts.Length && File.GetLastWriteTimeUtc(piece.Path) <= madeUtc)];
                var fresh = pieces.Except(heldByOlder).ToList();
                if (fresh.Count == 0)
                {
                    return await FailAsync(files,
                        $"{video}, the older video, holds the picture of every camera piece here already ({Named(heldByOlder)} older than it), so there is nothing to add to it, and it stays as it is. To make the video again from the pieces alone, move the older video out of the folder first (keep it: the pieces here may not hold all of its picture)",
                        kept: true, retry: false);
                }

                if (heldByOlder.Count > 0 && fresh.Min(piece => piece.Start) > TimeSpan.Zero)
                {
                    foreach (var piece in heldByOlder)
                        await LogAsync("Information", $"{Path.GetFileName(piece.Path)} is older than {video}, which holds its picture already, so it is left out; the older video's picture is used instead, up to the first newer piece");
                    pieces = fresh;
                }
            }
        }

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

        IReadOnlySet<string> skipped;
        try
        {
            using var sound = new AudioPreparer(new LineLogger<AudioPreparer>(this)).OpenMix(tracks, SoundRate, _cancel.Token);
            if (sound.MicrophoneLeftOut) await LogAsync("Information", "Your microphone stayed silent; the video's sound is the main track's alone");
            await WriteAsync(plan, sound, files.LessonVideoPartial);
            skipped = sound.Skipped;
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

        if (skipped.Count > 0)
        {
            // The mix passed over a damaged .wav part, as the upload does, so the sound written is shorter than the lesson's and
            // every piece after the part is out of step with it; the check cannot tell, since a damaged part measures as nothing.
            TryDelete(files.LessonVideoPartial);
            var names = skipped.Select(path => Path.GetFileName(path)).Order(StringComparer.OrdinalIgnoreCase).ToList();
            return await FailAsync(files,
                $"The lesson's sound could not all be read: {Listed(names)} {(names.Count == 1 ? "is not a readable WAV file" : "are not readable WAV files")}, so the video was not made; its picture would be out of step with the sound after {(names.Count == 1 ? "it" : "them")}",
                kept: true);
        }

        await SendAsync(CameraProtocol.Progress, new CameraProgress(99), CameraJson.Default.CameraProgress);
        if (await CheckAsync(files.LessonVideoPartial, new LessonVideoCheck(soundLength, plan.FramesToCheck)) is { } problem)
        {
            TryDelete(files.LessonVideoPartial);
            return await FailAsync(files, $"The lesson's video did not pass its check ({problem})", kept: true);
        }

        if (await PutInPlaceAsync(files) is { } held)
        {
            var partial = Path.GetFileName(files.LessonVideoPartial);
            return await FailAsync(files, File.Exists(files.LessonVideo)
                ? $"{Path.GetFileName(files.LessonVideo)}, the older video, is open in another program (a video player, or ClassScribe sending it to Google Drive), so the new one, which passed its check, could not take its place ({held}). It stays as {partial} until the next making. Close the player, or wait for the upload to end, then make the video again"
                : $"The new video, which passed its check, could not be named {Path.GetFileName(files.LessonVideo)} ({held}): another program, such as a scanner, holds it. It stays as {partial} until the next making. Make the video again",
                kept: true);
        }

        var bytes = new FileInfo(files.LessonVideo).Length;
        await LogAsync("Information", string.Create(CultureInfo.InvariantCulture,
            $"{Path.GetFileName(files.LessonVideo)} passed its check: {Durations.Format(soundLength)}, picture and sound, a frame decoded at the start, at each of {plan.Joins.Count} {(plan.Joins.Count == 1 ? "join" : "joins")} and at the end"));
        await DealWithThePiecesAsync(files, [.. pieces.Select(piece => piece.Path), .. heldByOlder.Select(piece => piece.Path)]);
        await SendAsync(CameraProtocol.Made, new CameraMade(files.LessonVideo, Math.Round(soundLength.TotalSeconds, 3), bytes), CameraJson.Default.CameraMade);
        return CameraProtocol.DoneExitCode;
    }

    /// <summary>
    /// Renames the checked <c>.partial</c> to the lesson's video, replacing an older one, trying <see cref="RenameTries"/>
    /// times <see cref="LookEvery"/> apart while another program holds either file open (a player the older video, a
    /// scanner either for a moment), with a line before each new try. Null once it is in place; otherwise Windows' reason,
    /// the <c>.partial</c> left as it is.
    /// </summary>
    private async Task<string?> PutInPlaceAsync(RecordingFiles files)
    {
        for (var tries = 1; ; tries++)
        {
            try
            {
                File.Move(files.LessonVideoPartial, files.LessonVideo, overwrite: true);
                return null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Deliberately not rethrown: a file another program holds open, as a player holds the older video, keeps the
                // new one from its place; it is tried again a little later, and after the last try the caller fails the job
                // with this reason, the checked video kept as its .partial, rather than ending the camera process unhandled.
                if (tries >= RenameTries) return Words(ex);

                await LogAsync("Debug", $"{Path.GetFileName(files.LessonVideo)} is open in another program; trying again in a second ({Words(ex)})");
                await Task.Delay(LookEvery, time);
            }
        }
    }

    /// <summary>
    /// After the video passed its check: the camera's pieces the job read, <paramref name="read"/>, deleted, said in a line
    /// with why, unless the job keeps them for Google Drive, which is said too. Any other piece, as one left out because it
    /// could not be read, is in no video, so it stays, named in a line. A piece that cannot be deleted stays, with a line.
    /// </summary>
    private async Task DealWithThePiecesAsync(RecordingFiles files, IReadOnlyList<string> read)
    {
        List<string> pieces = [];
        foreach (var piece in Recordings.FindCameraPieces(files).Select(piece => piece.Path))
        {
            if (read.Contains(piece, StringComparer.OrdinalIgnoreCase))
                pieces.Add(piece);
            else
                await LogAsync("Warning", $"{Path.GetFileName(piece)} could not be read, so its picture is in no video; it stays on this laptop. Delete it by hand once you no longer need it");
        }

        if (pieces.Count == 0) return;

        var count = pieces.Count == 1 ? "1 piece" : $"{pieces.Count} pieces";
        if (job.KeepPieces)
        {
            await ListThePiecesAsync(files, pieces);
            await LogAsync("Information", $"The camera's {count} of {files.BaseName} stay on this laptop until free-space finds the video safe on Google Drive (Drive:LessonVideos)");
            return;
        }

        // The pieces go now, so a list an earlier making left for free-space has nothing left to name.
        TryDelete(files.LessonVideoPieces);
        var deleted = 0;
        foreach (var piece in pieces)
        {
            try
            {
                File.Delete(piece);
                deleted++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Deliberately not rethrown: the video is made and checked; a piece that cannot be deleted now, as one a scanner
                // holds, only takes room, and the line says which stayed and why.
                await LogAsync("Warning", $"{Path.GetFileName(piece)} could not be deleted ({Words(ex)}); it stays on this laptop");
            }
        }

        if (deleted > 0)
            await LogAsync("Information", $"Deleted the camera's {(deleted == 1 ? "1 piece" : $"{deleted} pieces")} of {files.BaseName}: the lesson's video passed its check, and lesson videos do not go to Google Drive (Drive:LessonVideos), so the video is the one copy of the picture kept");
    }

    /// <summary>
    /// Writes the list of <paramref name="pieces"/>, those the video holds, beside the video (see
    /// <see cref="RecordingFiles.LessonVideoPieces"/>), so that free-space deletes only those once the video is safe on Drive
    /// and keeps a piece left out of the video (review 3.15). A list that cannot be written is a warning: free-space then goes
    /// by the pieces' times, as it did before the list.
    /// </summary>
    private async Task ListThePiecesAsync(RecordingFiles files, IReadOnlyList<string> pieces)
    {
        try
        {
            File.WriteAllLines(files.LessonVideoPieces, pieces.Select(piece => Path.GetFileName(piece)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Deliberately not rethrown: the list only tells free-space which pieces the video holds; the video is made and
            // checked, and without the list free-space goes by the pieces' times, which the line says.
            await LogAsync("Warning", $"{Path.GetFileName(files.LessonVideoPieces)}, the list of the pieces the video was made from, could not be written ({Words(ex)}); free-space will go by the pieces' times instead, and may delete a piece left out of the video");
        }
    }

    /// <summary>
    /// The source at <paramref name="path"/>, which begins at <paramref name="start"/>; when it cannot be read, no source, why,
    /// and whether only because Windows could not open it now (see <see cref="CouldNotOpenNow"/>), as when another program
    /// holds it, so that a later making may read it.
    /// </summary>
    private async Task<(LessonVideoSource? Source, string? Why, bool NotNow)> DescribeAsync(string path, TimeSpan start)
    {
        try
        {
            return (new LessonVideoSource(path, start, await Task.Run(() => media.Describe(path))), null, false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Deliberately not rethrown: the caller deals with a source that cannot be read by this reason. A piece that holds
            // nothing readable, as one whose camera process was killed before its first fragment, is left out and kept; one
            // Windows cannot open now, or an older video, ends the job with every piece kept.
            return (null, Words(ex), CouldNotOpenNow(ex));
        }
    }

    /// <summary>
    /// True when <paramref name="ex"/> says Windows could not open the file now (another program holds it, access is denied, it
    /// is gone), rather than that what it holds cannot be read: an IOException or an UnauthorizedAccessException, or a COM
    /// error of Windows' own (FACILITY_WIN32, as 0x80070020, a sharing violation), which Media Foundation passes on.
    /// </summary>
    private static bool CouldNotOpenNow(Exception ex) =>
        ex is IOException or UnauthorizedAccessException || (ex is System.Runtime.InteropServices.COMException com && ((com.HResult >> 16) & 0x1FFF) == 7);

    /// <summary>Writes the video on the thread pool, saying how far it has got (see <see cref="FollowAsync"/>): the percent from the writer's ten-thousandths, which also tell whether it moves.</summary>
    private async Task WriteAsync(LessonVideoPlan plan, MixedSound sound, string path)
    {
        await SendAsync(CameraProtocol.Progress, new CameraProgress(0), CameraJson.Default.CameraProgress);
        await FollowAsync(Task.Run(() => media.Write(plan, sound, path, fraction => Volatile.Write(ref _progress, (int)Math.Clamp(fraction * 10_000, 0, 10_000)), _cancel.Token)),
            () => Math.Min(98, Volatile.Read(ref _progress) / 100), () => Volatile.Read(ref _progress));
    }

    /// <summary>
    /// Waits for <paramref name="work"/>, saying how far the job has got, as <paramref name="percent"/> gives it, at every new
    /// percent, and the same percent again every <see cref="ProgressEvery"/> only while <paramref name="moved"/>, the work's own
    /// finer count, has changed since the last line: a slow but working job is never ended by the starter's time limit, while
    /// one stuck inside a call that never returns goes quiet, and the limit ends it (review 3.3). Then returns, or throws
    /// what <paramref name="work"/> threw.
    /// </summary>
    private async Task FollowAsync(Task work, Func<int> percent, Func<long> moved)
    {
        var lastPercent = -1;
        var lastMoved = long.MinValue;
        var lastSent = time.GetTimestamp();
        while (!work.IsCompleted)
        {
            await Task.WhenAny(work, Task.Delay(LookEvery, time));
            var now = percent();
            var sofar = moved();
            if (now == lastPercent && (sofar == lastMoved || time.GetElapsedTime(lastSent) < ProgressEvery)) continue;

            lastPercent = now;
            lastMoved = sofar;
            lastSent = time.GetTimestamp();
            await SendAsync(CameraProtocol.Progress, new CameraProgress(now), CameraJson.Default.CameraProgress);
        }

        await work;
    }

    /// <summary>
    /// Checks the video on the thread pool, saying 99% once and again every <see cref="ProgressEvery"/> while its frames
    /// decode, since a check that must decode from an IDR far back takes its time; a check that throws is a check failed, in words.
    /// </summary>
    private async Task<string?> CheckAsync(string path, LessonVideoCheck check)
    {
        try
        {
            var checking = Task.Run(() => media.Check(path, check, decoded => Volatile.Write(ref _checked, decoded)));
            await FollowAsync(checking, () => 99, () => Volatile.Read(ref _checked));
            return await checking;
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

    /// <summary><paramref name="pieces"/> by name, with the verb: "a.cam.mp4 is", "a.cam.mp4 and b.cam.mp4 are".</summary>
    private static string Named(IReadOnlyList<LessonVideoSource> pieces)
    {
        var names = pieces.Select(piece => Path.GetFileName(piece.Path)).ToList();
        return $"{Listed(names)} {(names.Count == 1 ? "is" : "are")}";
    }

    /// <summary><paramref name="names"/> in a list: "a", "a and b", "a, b and c".</summary>
    private static string Listed(IReadOnlyList<string> names) =>
        names.Count == 1 ? names[0] : $"{string.Join(", ", names.Take(names.Count - 1))} and {names[^1]}";

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
        if (kept && Recordings.FindCameraPieces(files).Count > 0) words += ". The camera's pieces are kept";
        if (retry) words += $". To make it again, free: {new CommandLine(ThisProgram.Current).FinishVideo(Recordings.FindSegments(files).FirstOrDefault() ?? Recordings.FindSegments(files, Track.Microphone).FirstOrDefault() ?? files.Folder)}";
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
