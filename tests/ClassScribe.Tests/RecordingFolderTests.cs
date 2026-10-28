using ClassScribe.Audio;
using ClassScribe.Storage;

namespace ClassScribe.Tests;

/// <summary>
/// The contract of <see cref="IRecordingFolder"/> over its real adapter, in a temporary folder: a recording's files found
/// by the names its record gives, and nothing else, with a folder that is not there holding none.
/// </summary>
[TestFixture]
public class RecordingFolderTests
{
    private TempFolder _folder = null!;
    private RecordingFiles _files = null!;
    private IRecordingFolder _recordings = null!;

    /// <summary>A recording's names in a temporary folder, and the adapter.</summary>
    [SetUp]
    public void SetUp()
    {
        _folder = new TempFolder();
        _files = new RecordingFiles(_folder.Path, "Kalam_2026-10-15_20-55-27Z");
        _recordings = new RecordingFolder();
    }

    /// <summary>Deletes the folder.</summary>
    [TearDown]
    public void TearDown() => _folder.Dispose();

    /// <summary>Makes an empty file at <paramref name="path"/>.</summary>
    private static void Touch(string path) => File.WriteAllBytes(path, []);

    /// <summary>The segments of each track come in recording order, by part number, and neither track takes the other's files nor the upload copy.</summary>
    [Test]
    public void Finds_each_tracks_segments_in_order()
    {
        Touch(_files.Segment(3));
        Touch(_files.Segment(1));
        Touch(_files.Segment(2, Track.Microphone));
        Touch(_files.Segment(1, Track.Microphone));
        Touch(_files.Upload);
        Touch(new RecordingFiles(_folder.Path, "Kalam_2026-10-15_21-55-27Z").Segment(1));

        Assert.Multiple(() =>
        {
            Assert.That(_recordings.FindSegments(_files), Is.EqualTo(new[] { _files.Segment(1), _files.Segment(3) }));
            Assert.That(_recordings.FindSegments(_files, Track.Microphone), Is.EqualTo(new[] { _files.Segment(1, Track.Microphone), _files.Segment(2, Track.Microphone) }));
        });
    }

    /// <summary>A transcript counts by any copy, the first deleted or not; a file that only starts the same way is no transcript.</summary>
    [Test]
    public void Tells_whether_the_recording_has_a_transcript()
    {
        Assert.That(_recordings.HasTranscript(_files), Is.False, "nothing there yet");

        File.WriteAllText(_folder.File("Kalam_2026-10-15_20-55-27Z_Notes.md"), "notes");
        Assert.That(_recordings.HasTranscript(_files), Is.False, "notes are no transcript");

        File.WriteAllText(_files.Transcript(2), "# Kalam");
        Assert.That(_recordings.HasTranscript(_files), Is.True, "a later copy counts");
    }

    /// <summary>The kept answers are every unread answer and every answer whose transcript is missing, and the next unread name skips those taken.</summary>
    [Test]
    public void Finds_the_kept_answers_and_the_next_unread_name()
    {
        File.WriteAllText(_files.Json(1), "{}");
        File.WriteAllText(_files.Transcript(1), "# Kalam");
        File.WriteAllText(_files.Json(2), "{}");
        File.WriteAllText(_files.UnreadAnswer(1), "{}");

        Assert.Multiple(() =>
        {
            Assert.That(_recordings.FindKeptAnswers(_files), Is.EquivalentTo(new[] { _files.Json(2), _files.UnreadAnswer(1) }));
            Assert.That(_recordings.NextFreeUnreadAnswer(_files), Is.EqualTo(_files.UnreadAnswer(2)));
        });
    }

    /// <summary>The camera's pieces come in the order of where each begins on the clock, with its start read from its name.</summary>
    [Test]
    public void Finds_the_camera_pieces_by_their_start()
    {
        Touch(_files.CameraPiece(TimeSpan.FromMinutes(90)));
        Touch(_files.CameraPiece(TimeSpan.Zero));
        Touch(_files.LessonVideo);

        Assert.That(_recordings.FindCameraPieces(_files), Is.EqualTo(new[]
        {
            new CameraPieceFile(_files.CameraPiece(TimeSpan.Zero), TimeSpan.Zero),
            new CameraPieceFile(_files.CameraPiece(TimeSpan.FromMinutes(90)), TimeSpan.FromMinutes(90))
        }));
    }

    /// <summary>A folder that is not there holds no segments, pieces or answers, and has no transcript.</summary>
    [Test]
    public void A_folder_that_is_not_there_holds_nothing()
    {
        var gone = new RecordingFiles(_folder.File("gone"), "Kalam_2026-10-15_20-55-27Z");

        Assert.Multiple(() =>
        {
            Assert.That(_recordings.FindSegments(gone), Is.Empty);
            Assert.That(_recordings.FindSegments(gone, Track.Microphone), Is.Empty);
            Assert.That(_recordings.FindCameraPieces(gone), Is.Empty);
            Assert.That(_recordings.FindKeptAnswers(gone), Is.Empty);
            Assert.That(_recordings.HasTranscript(gone), Is.False);
        });
    }

    /// <summary>The upload copy is deleted when it is there, and nothing happens when it is not; a folder that is gone is the file system's error.</summary>
    [Test]
    public void Deletes_the_upload_copy()
    {
        Touch(_files.Upload);
        Touch(_files.Segment(1));

        _recordings.DeleteUploadCopy(_files);
        Assert.Multiple(() =>
        {
            Assert.That(File.Exists(_files.Upload), Is.False);
            Assert.That(File.Exists(_files.Segment(1)), Is.True, "only the upload copy goes");
            Assert.DoesNotThrow(() => _recordings.DeleteUploadCopy(_files), "a copy that is not there is no error");
            Assert.Throws<DirectoryNotFoundException>(() => _recordings.DeleteUploadCopy(new RecordingFiles(_folder.File("gone"), _files.BaseName)));
        });
    }
}
