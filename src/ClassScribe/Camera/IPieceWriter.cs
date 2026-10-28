namespace ClassScribe.Camera;

/// <summary>
/// A lesson's piece being written (teach-video 6.6): a fragmented, video-only MP4 of H.264, whose first sample is a black
/// frame at time 0, written when it is made, so that the piece's time 0 is the position its name holds whatever comes next.
/// Each frame lasts until the next; the last lasts to the end given at <see cref="Finish"/>. Called from one thread at a
/// time.
/// </summary>
public interface IPieceWriter : IDisposable
{
    /// <summary>The piece's file.</summary>
    string Path { get; }

    /// <summary>The encoder in words, such as "hardware encoder (Intel® Quick Sync Video H.264 Encoder MFT)".</summary>
    string Encoder { get; }

    /// <summary>Writes a frame of NV12 pixels (the brightness plane, then the colour plane, each row as wide as the picture) at <paramref name="at"/> from the piece's start, later than the last.</summary>
    void Write(byte[] nv12, TimeSpan at);

    /// <summary>Writes a black frame at <paramref name="at"/>, later than the last: the picture while the camera is away.</summary>
    void WriteBlack(TimeSpan at);

    /// <summary>Lets the last frame last to <paramref name="end"/> from the piece's start and finishes the file.</summary>
    void Finish(TimeSpan end);
}

/// <summary>Makes a lesson's piece writer (see <see cref="IPieceWriter"/>).</summary>
public interface IPieceWriterFactory
{
    /// <summary>
    /// A new piece at <paramref name="path"/>, which must not be there yet, encoded as <paramref name="settings"/> say, its
    /// black frame at 0 written. Throws when the file cannot be made or the encoder refuses its settings.
    /// </summary>
    IPieceWriter Create(string path, PieceSettings settings);
}
