using ClassScribe.Audio;
using ClassScribe.Storage;

namespace ClassScribe.Tests;

/// <summary>
/// A recording's files on disk as the tests ask of a recording they wrote, read through the real adapter of the storage port
/// (see <see cref="IRecordingFolder"/>), so that a test's look at the segments, pieces or answers of a recording checks
/// what the app's own reader finds.
/// </summary>
public static class OnDisk
{
    /// <summary>The real adapter.</summary>
    private static readonly RecordingFolder Recordings = new();

    /// <summary>The segment files of <paramref name="track"/> of <paramref name="files"/> that exist, in recording order.</summary>
    public static IReadOnlyList<string> FindSegments(RecordingFiles files, Track track = Track.Main) => Recordings.FindSegments(files, track);

    /// <summary>The camera's pieces of <paramref name="files"/> that exist, in the order of where each begins.</summary>
    public static IReadOnlyList<CameraPieceFile> FindCameraPieces(RecordingFiles files) => Recordings.FindCameraPieces(files);

    /// <summary>The answers paid for that did not become a transcript of <paramref name="files"/>.</summary>
    public static IReadOnlyList<string> FindKeptAnswers(RecordingFiles files) => Recordings.FindKeptAnswers(files);

    /// <summary>The first unread answer name of <paramref name="files"/> not taken yet.</summary>
    public static string NextFreeUnreadAnswer(RecordingFiles files) => Recordings.NextFreeUnreadAnswer(files);

    /// <summary>True when <paramref name="files"/> has a transcript, any copy of it.</summary>
    public static bool HasTranscript(RecordingFiles files) => Recordings.HasTranscript(files);
}
