using System.Globalization;
using System.Text.RegularExpressions;
using ClassScribe.Audio;

namespace ClassScribe.Storage;

/// <summary>
/// A recording's files on disk, read from its folder: the adapter of <see cref="IRecordingFolder"/>, which looks for each
/// file by the names the recording's record gives (see <see cref="RecordingFiles"/>). It holds nothing of its own.
/// </summary>
public sealed class RecordingFolder : IRecordingFolder
{
    /// <inheritdoc />
    public bool HasTranscript(RecordingFiles files)
    {
        if (!Directory.Exists(files.Folder)) return false;

        var transcript = new Regex($"^{Regex.Escape(files.BaseName)}(?: \\([0-9]{{1,9}}\\))?\\.md$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return Directory.EnumerateFiles(files.Folder, $"{files.BaseName}*.md").Any(file => transcript.IsMatch(Path.GetFileName(file)));
    }

    /// <inheritdoc />
    public string NextFreeUnreadAnswer(RecordingFiles files)
    {
        for (var copy = 1; ; copy++)
        {
            var path = files.UnreadAnswer(copy);
            if (!File.Exists(path)) return path;
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<string> FindKeptAnswers(RecordingFiles files)
    {
        if (!Directory.Exists(files.Folder)) return [];

        var answer = new Regex($"^{Regex.Escape(files.BaseName)}(?: \\([0-9]{{1,9}}\\))?(?<unread>\\.unread)?\\.json$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return Directory.EnumerateFiles(files.Folder, $"{files.BaseName}*.json")
            .Where(file => answer.Match(Path.GetFileName(file)) is { Success: true } match
                && (match.Groups["unread"].Success || !File.Exists(Path.ChangeExtension(file, ".md"))))
            .ToList();
    }

    /// <inheritdoc />
    public IReadOnlyList<string> FindSegments(RecordingFiles files, Track track = Track.Main)
    {
        if (!Directory.Exists(files.Folder)) return [];

        var segment = new Regex($"^{Regex.Escape(files.BaseName)}(?:_part(?<number>[0-9]{{1,9}}))?{Regex.Escape(RecordingFiles.SegmentExtension(track))}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return Directory.EnumerateFiles(files.Folder, $"{files.BaseName}*.wav")
            .Select(file => (Path: file, Match: segment.Match(Path.GetFileName(file))))
            .Where(file => file.Match.Success)
            .OrderBy(file => file.Match.Groups["number"].Success ? int.Parse(file.Match.Groups["number"].Value, CultureInfo.InvariantCulture) : 1)
            .Select(file => file.Path)
            .ToList();
    }

    /// <inheritdoc />
    public IReadOnlyList<CameraPieceFile> FindCameraPieces(RecordingFiles files)
    {
        if (!Directory.Exists(files.Folder)) return [];

        var prefix = files.BaseName.Length;
        return Directory.EnumerateFiles(files.Folder, $"{files.BaseName}.cam*.mp4")
            .Select(file => (Path: file, Name: Path.GetFileName(file)))
            .Where(file => file.Name.Length > prefix && string.Equals(file.Name[..prefix], files.BaseName, StringComparison.OrdinalIgnoreCase))
            .Select(file => (file.Path, Start: RecordingFiles.PieceStart(file.Name[prefix..])))
            .Where(file => file.Start is not null)
            .Select(file => new CameraPieceFile(file.Path, file.Start!.Value))
            .OrderBy(piece => piece.Start)
            .ToList();
    }

    /// <inheritdoc />
    public void DeleteUploadCopy(RecordingFiles files) => File.Delete(files.Upload);
}
