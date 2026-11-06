namespace ClassScribe.Recorder;

/// <summary>
/// The yield of a YouTube video's run to a class (design 2.5): a class that starts while a video records asks the video's run
/// to give way, and the run stops as Q pressed twice stops it, noting that a class stopped it, so that its end is not
/// confirmed and the same command records it again. Behind an interface so that the starter's and the run's tests need no
/// real event; the real one is <c>WindowsVideoYield</c>.
/// </summary>
public interface IVideoYield
{
    /// <summary>
    /// Listens for the yield of run <paramref name="runId"/>, a video's, and calls <paramref name="onYield"/> once a class asks,
    /// on another thread, until the result is disposed; null when it cannot listen, which is said, and the run goes on: Q
    /// pressed twice still stops it.
    /// </summary>
    IDisposable? Listen(string runId, Action onYield);

    /// <summary>Asks run <paramref name="runId"/>, a video's, to give way to a class; false when nothing listens for it.</summary>
    bool Ask(string runId);
}
