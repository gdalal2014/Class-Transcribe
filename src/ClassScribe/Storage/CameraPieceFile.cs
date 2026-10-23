namespace ClassScribe.Storage;

/// <summary>A camera's piece of a lesson on disk (see <see cref="RecordingFiles.CameraPiece"/>): the file, and where on the run's clock its time 0 is, as its name says.</summary>
/// <param name="Path">The piece's file.</param>
/// <param name="Start">Where on the clock the piece begins.</param>
public sealed record CameraPieceFile(string Path, TimeSpan Start);
