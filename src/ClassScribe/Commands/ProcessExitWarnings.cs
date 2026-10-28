using System.Runtime.InteropServices;

namespace ClassScribe.Commands;

/// <summary>
/// The warnings Windows and .NET give before a console process ends: the window being closed (SIGHUP,
/// which .NET raises for Windows' CTRL_CLOSE_EVENT; the process then has a few seconds left) and an
/// unhandled exception anywhere in the process.
/// </summary>
/// <remarks>
/// A shutdown, a restart for updates or a sign-out gives no such warning to a program like this one.
/// .NET's SIGTERM is Windows' CTRL_SHUTDOWN_EVENT, and Windows sends neither that nor CTRL_LOGOFF_EVENT
/// to a process that has user32.dll loaded, which every .NET process has, so SIGTERM is not registered.
/// SIGHUP may still come then, when the terminal window closes before the process is ended. Otherwise
/// at most the packet being written is lost, because each packet is handed to Windows as it arrives, and
/// the WAV header can be up to Recording:FlushSeconds old; that does not matter, because preparing for
/// upload reads every segment to its end, whatever its header says.
/// </remarks>
public sealed class ProcessExitWarnings : IExitWarnings
{
    /// <inheritdoc />
    public IDisposable Register(Action onExit)
    {
        var windowClosing = PosixSignalRegistration.Create(PosixSignal.SIGHUP, _ => onExit());
        UnhandledExceptionEventHandler crashing = (_, _) => onExit();
        AppDomain.CurrentDomain.UnhandledException += crashing;
        return new Registration(windowClosing, crashing);
    }

    /// <summary>Both warnings for one command; disposing it unhooks them.</summary>
    private sealed class Registration(PosixSignalRegistration windowClosing, UnhandledExceptionEventHandler crashing) : IDisposable
    {
        /// <summary>Unhooks both warnings.</summary>
        public void Dispose()
        {
            AppDomain.CurrentDomain.UnhandledException -= crashing;
            windowClosing.Dispose();
        }
    }
}
