namespace ClassScribe.Camera;

/// <summary>A camera process ClassScribe started, and the lines it speaks over its standard input and output.</summary>
public interface ICameraProcess : IAsyncDisposable
{
    /// <summary>Windows' id for the process.</summary>
    int ProcessId { get; }

    /// <summary>The lines to and from the process, message by message (see <see cref="CameraProtocol"/>).</summary>
    ICameraLink Link { get; }

    /// <summary>The process's exit code once it has ended; null while it runs.</summary>
    int? ExitCode { get; }

    /// <summary>Ends the process at once, as a stop that waited too long does.</summary>
    void Kill();
}

/// <summary>
/// Starts camera processes (teach-video 6.2, amendment 24): this very program, <c>--camera-process &lt;job id&gt;</c>, in a
/// kill-on-close job of the starter's, so that it never outlives the process that started it.
/// </summary>
public interface ICameraProcessStarter
{
    /// <summary>
    /// Starts a camera process for the job <paramref name="jobId"/>, a well-formed id (<see cref="Runs.RunIds"/>); throws
    /// an <see cref="ArgumentException"/> for any other before anything starts, and a
    /// <see cref="System.ComponentModel.Win32Exception"/> when Windows does not start it.
    /// </summary>
    ICameraProcess Start(string jobId);
}
