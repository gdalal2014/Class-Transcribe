namespace ClassScribe.Audio;

/// <summary>Builds the file that gets uploaded.</summary>
public interface IAudioPreparer
{
    /// <summary>
    /// Mixes the tracks of a recording into one 16 kHz mono 16-bit WAV at <paramref name="outputPath"/>: the class and
    /// your microphone summed sample for sample from their common start, the shorter padded with silence at its end, so
    /// that both are heard where the recording placed them. A microphone track with no files is left out without a
    /// word; one with files that never rose above <c>AudioPreparer.MicrophoneSoundThreshold</c> is left out and
    /// the log says that your microphone stayed silent. Either way the main track is then prepared exactly as
    /// <see cref="Prepare(IReadOnlyList{string}, string, CancellationToken)"/> prepares it. A preparation that fails
    /// or is cancelled leaves no file behind.
    /// </summary>
    /// <param name="tracks">The segments of each track, in the order they are joined.</param>
    /// <param name="outputPath">Where the upload file goes. Nothing may be there yet.</param>
    /// <param name="cancellationToken">Checked after every second of audio, so Ctrl+C stops even a long class at once.</param>
    /// <exception cref="NoAudioException">The microphone's track was left out, and the main track holds no samples, or nothing but digital silence.</exception>
    /// <exception cref="FormatException">The microphone's track was left out, and none of the main track's files is a WAV file this app can read.</exception>
    /// <exception cref="IOException">A file is already at <paramref name="outputPath"/>. It is left as it is.</exception>
    /// <exception cref="OperationCanceledException">The token was cancelled.</exception>
    void Prepare(UploadTracks tracks, string outputPath, CancellationToken cancellationToken);

    /// <summary>
    /// Joins the WAV files into one 16 kHz mono 16-bit WAV at <paramref name="outputPath"/>. A preparation
    /// that fails or is cancelled leaves no file behind.
    /// </summary>
    /// <param name="wavPaths">The WAV files, in the order they are joined.</param>
    /// <param name="outputPath">Where the upload file goes. Nothing may be there yet.</param>
    /// <param name="cancellationToken">Checked after every second of audio, so Ctrl+C stops even a long file at once.</param>
    /// <exception cref="NoAudioException">The files could be read, but they hold no samples, or nothing but digital silence.</exception>
    /// <exception cref="FormatException">None of the files is a WAV file this app can read.</exception>
    /// <exception cref="IOException">A file is already at <paramref name="outputPath"/>. It is left as it is.</exception>
    /// <exception cref="OperationCanceledException">The token was cancelled.</exception>
    void Prepare(IReadOnlyList<string> wavPaths, string outputPath, CancellationToken cancellationToken);
}
