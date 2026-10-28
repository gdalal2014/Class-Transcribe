using System.Text.Json;
using ClassScribe.Audio;
using ClassScribe.Settings;
using ClassScribe.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NAudio.Wave;

namespace ClassScribe.YouTube;

/// <summary>The note of a conversion in progress, &lt;RuntimeFolder&gt;\youtube\converting.json: the recording whose files it is writing.</summary>
/// <param name="VideoId">The video's ID.</param>
/// <param name="Title">The video's title, for the line the clean-up writes.</param>
/// <param name="Folder">The recording's folder.</param>
/// <param name="BaseName">The recording's base name.</param>
public sealed record ConversionNote(string VideoId, string Title, string Folder, string BaseName);

/// <summary>
/// Turns the WAV FFmpeg made of a video's audio into ClassScribe's own (design 3.7, with Gulzar's answer 5), since every
/// reader measures a recording by ClassScribe's own 44-byte header and counts any other WAV as empty. The staged file is read
/// with NAudio, which passes over FFmpeg's extra chunks, made mono 16-bit if it is not, and written with the recorder's own
/// writer at the source's own rate, split at Recording:MaxSegmentMegabytes as the recorder splits. The parts are written as
/// &lt;segment&gt;.partial, which nothing lists, measures or uploads; their length must be within 2 seconds of the video's,
/// or nothing of them is kept; only then are they renamed to their segments' names. A note names the recording before
/// anything is written, and goes once the parts are renamed, so that a crash in between leaves what the next batch needs to
/// clean up (<see cref="CleanUp"/>), under the batch's lock. The note, the staging folders and the cache are under
/// ClassScribe's own folder.
/// </summary>
public sealed class DownloadedAudio(YouTubeFolders folders, IOptionsSnapshot<RecordingOptions> recording, TimeProvider time, ILogger<DownloadedAudio> logger)
{
    /// <summary>How an unfinished part's name ends, after its segment's name.</summary>
    public const string PartialEnding = ".partial";

    /// <summary>How far the audio's length may be from the video's: yt-dlp gives whole seconds, and the ends of the audio differ a little.</summary>
    public static readonly TimeSpan LengthTolerance = TimeSpan.FromSeconds(2);

    /// <summary>How long a staging folder is kept untouched before it is deleted: a failed download goes on from where it stopped until then.</summary>
    public static readonly TimeSpan KeepStagingFor = TimeSpan.FromDays(7);

    /// <summary>
    /// Writes the audio of <paramref name="staged"/> as the main track of <paramref name="target"/>, the video
    /// <paramref name="title"/>, <paramref name="expected"/> long, and returns its length. The staged file is left where it is.
    /// </summary>
    /// <exception cref="IncompleteAudioException">The audio is more than 2 seconds from the video's length; nothing of it is kept.</exception>
    public TimeSpan Convert(string staged, RecordingFiles target, TimeSpan expected, string title, CancellationToken cancellationToken)
    {
        WriteNote(new ConversionNote(Path.GetFileName(Path.GetDirectoryName(staged)) ?? "", title, target.Folder, target.BaseName));
        List<string> partials = [];
        try
        {
            DeleteLeftovers(target);
            var length = WriteParts(staged, target, partials, cancellationToken);
            if ((length - expected).Duration() > LengthTolerance)
            {
                throw new IncompleteAudioException(
                    $"The audio of \"{title}\" is {Durations.Format(length)} long, but the video is {Durations.Format(expected)}: the download is incomplete, so nothing of it was kept. The downloaded file stays in {Path.GetDirectoryName(staged)} for a look; run the same command again to fetch it again.");
            }

            for (var part = 1; part <= partials.Count; part++) File.Move(partials[part - 1], target.Segment(part), overwrite: false);
            partials.Clear();
            RemoveNote();
            logger.LogDebug("Wrote {Parts} part(s) of {Recording}, {Length}, from {Staged}", target.FindSegments().Count, target.BaseName, Durations.Format(length), staged);
            return length;
        }
        catch (Exception ex)
        {
            if (ex is not (OperationCanceledException or IncompleteAudioException))
                logger.LogDebug(ex, "Could not turn {Staged} into the audio of {Recording}", staged, target.BaseName);

            foreach (var partial in partials) File.Delete(partial);
            RemoveNote();
            throw;
        }
    }

    /// <summary>
    /// Cleans up what an earlier batch left half done, as the next batch begins, under its lock: the files of a conversion a
    /// crash cut short, as its note names them (its unfinished parts, and any part already renamed, since the note goes only
    /// once all are), with a line that says so; and the staging folders untouched for more than 7 days. What cannot be deleted
    /// now, as a file another program holds, is said and left for the next batch: the video is fetched again either way, into
    /// a new recording, since its source file says its end was never confirmed.
    /// </summary>
    public void CleanUp()
    {
        if (ReadNote() is { } note)
        {
            var files = new RecordingFiles(note.Folder, note.BaseName);
            List<string> deleted = [.. files.FindSegments(), .. Partials(files)];
            try
            {
                foreach (var file in deleted) File.Delete(file);
                RemoveNote();
                logger.LogInformation("Deleted the unfinished conversion of \"{Title}\" left by an earlier batch: {Files}. It is fetched again by the same command",
                    note.Title, deleted.Count == 0 ? "nothing was written yet" : string.Join(", ", deleted.Select(Path.GetFileName)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Deliberately not rethrown: what an earlier batch left half done must not keep this one from going on; the
                // note stays for the next batch, and the video is fetched again into a new recording either way.
                logger.LogWarning("The unfinished conversion of \"{Title}\" left by an earlier batch could not be deleted ({Reason}); the next batch tries again",
                    note.Title, ex.Message);
            }
        }

        if (!Directory.Exists(folders.Staging)) return;

        foreach (var folder in Directory.EnumerateDirectories(folders.Staging))
        {
            try
            {
                var touched = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Select(File.GetLastWriteTimeUtc).Append(Directory.GetLastWriteTimeUtc(folder)).Max();
                if (time.GetUtcNow() - new DateTimeOffset(touched, TimeSpan.Zero) <= KeepStagingFor) continue;

                Directory.Delete(folder, recursive: true);
                logger.LogDebug("Deleted the staging folder {Folder}, untouched since {Touched:u}", folder, touched);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Deliberately not rethrown: pruning old downloads is housekeeping; a folder that cannot be deleted now is
                // tried again by the next batch.
                logger.LogDebug(ex, "Could not delete the staging folder {Folder}; the next batch tries again", folder);
            }
        }
    }

    /// <summary>
    /// Writes the staged audio into .partial parts of <paramref name="target"/>, each named into <paramref name="partials"/>
    /// as it is made, and returns the length written. 16-bit mono is copied as it is; anything else is read as samples, its
    /// channels mixed, and written as 16-bit.
    /// </summary>
    private TimeSpan WriteParts(string staged, RecordingFiles target, List<string> partials, CancellationToken cancellationToken)
    {
        using var file = new FileStream(staged, FileMode.Open, FileAccess.Read, FileShare.Read);
        WaveFormat format;
        long start, declared;
        using (var reader = new WaveFileReader(file))
        {
            format = AudioPreparer.Plain(reader.WaveFormat);
            start = file.Position;
            declared = reader.Length;
        }

        if (format.Channels < 1 || format.SampleRate < 1 || format.BlockAlign < 1)
            throw new FormatException($"The format block of {Path.GetFileName(staged)} is damaged.");

        using var wave = new WaveData(file, start, Math.Min(file.Length, start + declared), format);
        var maxBytes = Math.Max(1, recording.Value.MaxSegmentMegabytes) * 1024L * 1024L;
        WavSegmentWriter? writer = null;
        long bytes = 0;
        try
        {
            foreach (var chunk in Mono16(wave, format))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var rest = chunk.AsMemory();
                while (rest.Length > 0)
                {
                    if (writer is null || writer.DataBytes >= maxBytes)
                    {
                        writer?.Dispose();
                        var partial = target.Segment(partials.Count + 1) + PartialEnding;
                        partials.Add(partial);
                        writer = new WavSegmentWriter(partial, format.SampleRate);
                    }

                    var room = (int)Math.Min(rest.Length, (maxBytes - writer.DataBytes) / 2 * 2);
                    writer.Write(rest.Span[..room]);
                    bytes += room;
                    rest = rest[room..];
                }
            }
        }
        finally
        {
            writer?.Dispose();
        }

        return TimeSpan.FromSeconds(bytes / 2.0 / format.SampleRate);
    }

    /// <summary>The audio of <paramref name="wave"/> as 16-bit mono, chunk by chunk: copied as it is when it is that already, else read as samples, its channels mixed.</summary>
    private static IEnumerable<byte[]> Mono16(WaveStream wave, WaveFormat format)
    {
        if (format is { Encoding: WaveFormatEncoding.Pcm, BitsPerSample: 16, Channels: 1 })
        {
            var buffer = new byte[64 * 1024];
            int read;
            while ((read = wave.Read(buffer, 0, buffer.Length)) > 0) yield return buffer[..(read / 2 * 2)];
            yield break;
        }

        ISampleProvider samples = new FiniteSamples(wave.ToSampleProvider());
        if (samples.WaveFormat.Channels > 1) samples = new MonoDownmix(samples);
        var floats = new float[32 * 1024];
        int count;
        while ((count = samples.Read(floats, 0, floats.Length)) > 0)
        {
            var chunk = new byte[count * 2];
            for (var i = 0; i < count; i++)
            {
                var value = (short)Math.Clamp(MathF.Round(floats[i] * short.MaxValue), short.MinValue, short.MaxValue);
                chunk[2 * i] = (byte)value;
                chunk[2 * i + 1] = (byte)(value >> 8);
            }

            yield return chunk;
        }
    }

    /// <summary>The unfinished parts of <paramref name="files"/> in its folder.</summary>
    private static IEnumerable<string> Partials(RecordingFiles files) =>
        Directory.Exists(files.Folder) ? Directory.EnumerateFiles(files.Folder, $"{files.BaseName}*{PartialEnding}") : [];

    /// <summary>Deletes what an earlier try at <paramref name="target"/> left: its unfinished parts.</summary>
    private static void DeleteLeftovers(RecordingFiles target)
    {
        foreach (var partial in Partials(target).ToList()) File.Delete(partial);
    }

    /// <summary>Writes the note of the conversion in progress.</summary>
    private void WriteNote(ConversionNote note)
    {
        Directory.CreateDirectory(folders.Root);
        File.WriteAllText(folders.ConvertingNote, JsonSerializer.Serialize(note, YouTubeJson.Files.ConversionNote));
    }

    /// <summary>The note of a conversion a crash cut short; null when there is none. A note that cannot be read is said, deleted, and counts as none.</summary>
    private ConversionNote? ReadNote()
    {
        if (!File.Exists(folders.ConvertingNote)) return null;

        try
        {
            return JsonSerializer.Deserialize(File.ReadAllText(folders.ConvertingNote), YouTubeJson.Files.ConversionNote) is { Folder: not null, BaseName: not null } note ? note : null;
        }
        catch (JsonException ex)
        {
            // Deliberately not rethrown: a note that is not one names no files to delete; it is said and removed, so that it
            // never holds up a batch.
            logger.LogWarning("The note of an earlier conversion, {Path}, cannot be read ({Reason}); it is removed", folders.ConvertingNote, ex.Message);
            RemoveNote();
            return null;
        }
    }

    /// <summary>Removes the note of the conversion.</summary>
    private void RemoveNote() => File.Delete(folders.ConvertingNote);
}
