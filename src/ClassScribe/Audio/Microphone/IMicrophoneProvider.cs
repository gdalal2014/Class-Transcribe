namespace ClassScribe.Audio.Microphones;

/// <summary>
/// Lists the microphones Windows can record from, tells when they change, and opens a capture of one. Listing reads
/// the endpoints' property stores only, and never opens a stream on any microphone.
/// </summary>
public interface IMicrophoneProvider : IDisposable
{
    /// <summary>
    /// Raised when Windows reports a change among microphones: one added, removed or changing state, or a new default.
    /// It can also be raised when nothing changed for the recording, so listeners look at <see cref="GetMicrophones"/>
    /// rather than at the event.
    /// </summary>
    event EventHandler? MicrophonesChanged;

    /// <summary>The active microphones right now, from their property stores alone: no stream is opened.</summary>
    IReadOnlyList<Microphone> GetMicrophones();

    /// <summary>Creates a capture of <paramref name="microphone"/>, asking for <paramref name="processing"/>; Raw falls back to Default when it is not available.</summary>
    IAudioCapture CreateCapture(Microphone microphone, MicrophoneProcessing processing);

    /// <summary>
    /// Whether the microphone with <paramref name="id"/> is muted in Windows: the switch in Sound settings, and the
    /// keyboard's microphone key on most laptops. Null when that cannot be read. Opens no stream.
    /// </summary>
    bool? IsMuted(string id);
}

/// <summary>Opens a capture of a microphone by its endpoint id, asking Windows for the processing wanted.</summary>
public interface IMicrophoneCaptureFactory
{
    /// <summary>
    /// Opens a capture of the microphone <paramref name="endpointId"/>, named <paramref name="name"/> in the log, with
    /// <paramref name="processing"/>; Raw falls back to Default when it is not available.
    /// </summary>
    IAudioCapture Open(string endpointId, string name, MicrophoneProcessing processing);
}
