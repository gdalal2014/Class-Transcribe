using System.Globalization;
using ClassScribe.Audio;
using ClassScribe.Storage;
using Microsoft.Extensions.Logging;

namespace ClassScribe.YouTube;

/// <summary>
/// The videos fetched before (design 2.6): every video's source file under the recordings folder, in every course folder and
/// its date folders, read once per batch and found by the video's ID. A video is done when a recording of it holds the whole
/// video (a download, or a recording whose end was confirmed), transcribed or not; otherwise, a recording that may be cut
/// short included, transcribed or not, it is fetched again as a new recording, the old one staying as it is.
/// With --again every video is fetched again. So the same command run again, after a crash, a closed window, YouTube's bot
/// check or ElevenLabs' refusal, picks up where it stopped, and nothing is fetched or billed twice unless asked.
/// </summary>
public sealed class VideoIndex
{
    /// <summary>A recording's files on disk, by the names its record gives.</summary>
    private static readonly RecordingFolder Recordings = new();

    /// <summary>A recording's transcripts on disk.</summary>
    private static readonly TranscriptCopies Copies = new();

    /// <summary>The recordings of each video, by its ID, newest first.</summary>
    private readonly Dictionary<string, List<PastVideo>> _byVideo = new(StringComparer.Ordinal);

    /// <summary>An index of nothing.</summary>
    private VideoIndex()
    {
    }

    /// <summary>
    /// Reads the source file of every video's recording under <paramref name="root"/>, as <paramref name="folders"/> keeps
    /// them. A folder that cannot be looked into, and a source file that cannot be read, are passed over, the second said at
    /// Debug: that video is then fetched again, which costs time, never a recording.
    /// </summary>
    public static VideoIndex Read(string root, IClassFolders folders, ILogger logger)
    {
        var index = new VideoIndex();
        if (!Directory.Exists(root)) return index;

        var listing = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0 };
        foreach (var course in Directory.EnumerateDirectories(root, "*", listing))
        {
            IEnumerable<string> sources;
            try
            {
                sources = ClassFolders.RecordingFoldersIn(course).SelectMany(folder => Directory.EnumerateFiles(folder, "*.source.json", listing)).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Deliberately not rethrown: a course folder that cannot be read holds no video this batch can know of; its
                // videos are fetched again, which costs time, never a recording.
                logger.LogDebug(ex, "Could not look for videos fetched before in {Folder}", course);
                continue;
            }

            foreach (var path in sources)
            {
                if (RecordingFiles.TryParse(path) is not { } files) continue;

                try
                {
                    if (VideoSourceFile.Read(path) is not { } source) continue;

                    var past = new PastVideo(files, source, Recordings.FindSegments(files).Count > 0 || Recordings.FindSegments(files, Track.Microphone).Count > 0, Recordings.HasTranscript(files),
                        folders.KeptAnswerOf(files));
                    if (!index._byVideo.TryGetValue(source.VideoId, out var recordings)) index._byVideo[source.VideoId] = recordings = [];
                    recordings.Add(past);
                }
                catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
                {
                    // Deliberately not rethrown: a source file that cannot be read tells nothing of its video, which is then
                    // fetched again; the reason is noted for a look.
                    logger.LogDebug(ex, "Passing over {Path}: it cannot be read", path);
                }
            }
        }

        foreach (var recordings in index._byVideo.Values)
            recordings.Sort((a, b) => Nullable.Compare(b.Files.StartedUtc, a.Files.StartedUtc));
        return index;
    }

    /// <summary>The recordings of video <paramref name="videoId"/> made before, newest first.</summary>
    public IReadOnlyList<PastVideo> Of(string videoId) => _byVideo.TryGetValue(videoId, out var recordings) ? recordings : [];

    /// <summary>
    /// What was done before with video <paramref name="videoId"/>, the dates as the local day of <paramref name="zone"/>; with
    /// <paramref name="again"/> (--again), it is fetched again in every case.
    /// </summary>
    public PastVerdict Judge(string videoId, bool again, TimeZoneInfo zone)
    {
        var recordings = Of(videoId);
        if (recordings.Count == 0) return new(PastKind.None, null, "");
        if (again) return new(PastKind.None, null, "fetched before; --again fetches it again");

        // A transcript counts only for a recording that holds the whole video: one that may be cut short is recorded again,
        // as its summary said, its transcript staying as it is. The audio need not be there still: Drive may hold it alone.
        if (recordings.FirstOrDefault(past => past.HasTranscript && past.Source.EndConfirmed) is { } transcribed)
        {
            var transcript = Copies.Transcripts(transcribed.Files).LastOrDefault() ?? transcribed.Files.Transcript();
            var on = TimeZoneInfo.ConvertTime(new DateTimeOffset(File.GetLastWriteTimeUtc(transcript), TimeSpan.Zero), zone).ToString("d MMM", CultureInfo.InvariantCulture);
            return new(PastKind.Transcribed, transcribed, $"already transcribed on {on}: {Path.GetFileName(transcript)}");
        }

        if (recordings.FirstOrDefault(past => past.IsWhole) is { } whole)
            return new(PastKind.Whole, whole, $"fetched before, not transcribed yet: {whole.Files.BaseName}");

        var newest = recordings[0];
        var why = newest.HasAudio ? $"{newest.Source.Ended ?? "its end is not known"}, so it may be cut short" : "it holds no audio";
        return new(PastKind.Unfinished, null, $"fetched before ({newest.Files.BaseName}), but {why}: fetched again");
    }
}
