using ClassScribe.Audio.Microphones;

namespace ClassScribe.Camera;

/// <summary>
/// One recording's hold on a webcam's picture (architecture 4.3; camera-first 3.4): which webcam the picked microphone
/// belongs to, and, with Recording:WakeCameraMicrophone on, its picture held open, shared, from before the microphone opens
/// until the recording ends, so that a webcam that sleeps by itself cannot silence its microphone. Letting it go is its stop.
/// </summary>
public interface IWebcamHold : IDisposable
{
    /// <summary>The webcam the picked microphone belongs to, and its hold; null for a microphone of no webcam, and before the first pick.</summary>
    WebcamState? State { get; }

    /// <summary>Follows the pick of <paramref name="microphone"/> (null for none): finds its webcam, and holds its picture when the hold is on.</summary>
    void Follow(Microphone? microphone);

    /// <summary>Called before <paramref name="microphone"/>'s capture is opened, on the thread that opens it: waits for the held camera's first frame, within a limit.</summary>
    void BeforeOpen(Microphone microphone);

    /// <summary>Lets go of the hold: the camera process ends, and the picture with it.</summary>
    void Stop();
}

/// <summary>Makes each recording's webcam hold (architecture 4.3), over the app's camera list and camera process starter.</summary>
public interface IWebcamHolds
{
    /// <summary>A hold for one recording: it holds a webcam's picture only when <paramref name="wakeOn"/> (Recording:WakeCameraMicrophone).</summary>
    IWebcamHold Create(bool wakeOn);
}
