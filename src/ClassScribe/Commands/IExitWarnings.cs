namespace ClassScribe.Commands;

/// <summary>Warns a running command that the process is about to end without the usual stop.</summary>
public interface IExitWarnings
{
    /// <summary>
    /// Calls <paramref name="onExit"/> when the process is about to end: its window is being closed, or
    /// an exception nobody caught is taking it down. Disposing the result stops the warnings.
    /// </summary>
    IDisposable Register(Action onExit);
}
