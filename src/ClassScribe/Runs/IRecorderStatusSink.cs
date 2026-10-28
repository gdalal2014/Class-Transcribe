namespace ClassScribe.Runs;

/// <summary>Takes a run's state on every change and once a second, for the window's title and taskbar, or for the windows that show the run.</summary>
public interface IRecorderStatusSink
{
    /// <summary>Passes <paramref name="state"/> on.</summary>
    void Publish(RecorderState state);
}
