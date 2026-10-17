using ClassScribe.Commands;
using ClassScribe.Desktop.Windows;
using ClassScribe.Recorder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ClassScribe.Desktop;

/// <summary>
/// Registers ClassScribe's own Windows integration: keeping the laptop awake while it records and uploads, where the
/// laptop takes its power from, the computer's clocks, the watch for times when ClassScribe did not run, whether the
/// process a run file names still runs, the window's title, taskbar button and flash, and Windows' end of the session.
/// </summary>
public static class DesktopServices
{
    /// <summary>
    /// Adds the Windows integration's services. Transcribing keeps the laptop awake too, so on a system other than Windows
    /// the requests ask for nothing and the power source is not read, and transcribing an existing file works there; the
    /// window's signals show nothing there. What only recording needs (the computer's clocks, whether a process still runs,
    /// and Windows' end of the session) is made only when a run asks for it, and needs Windows. The freeze watch, the status
    /// sink and the session's window are one per run (one per scope).
    /// </summary>
    public static IServiceCollection AddDesktop(this IServiceCollection services)
    {
        services.AddSingleton<IPowerRequests>(provider => OperatingSystem.IsWindows()
            ? new WindowsPowerRequests(provider.GetRequiredService<TimeProvider>(), provider.GetRequiredService<ILogger<WindowsPowerRequests>>())
            : new NoPowerRequests());
        services.AddSingleton<IPowerSource>(provider => OperatingSystem.IsWindows()
            ? new WindowsPowerSource(provider.GetRequiredService<ILogger<WindowsPowerSource>>())
            : new UnknownPowerSource());
        services.AddSingleton<ISystemTimes>(provider => OperatingSystem.IsWindows()
            ? new WindowsSystemTimes(provider.GetRequiredService<TimeProvider>())
            : throw new PlatformNotSupportedException("Reading Windows' interrupt times needs Windows."));
        services.AddScoped<FreezeWatch>();

        // Whether the process a run file names still runs: one for the process.
        services.AddSingleton<IProcessLiveness>(provider => OperatingSystem.IsWindows()
            ? new ProcessLiveness(provider.GetRequiredService<TimeProvider>(), provider.GetRequiredService<ILogger<ProcessLiveness>>())
            : throw new PlatformNotSupportedException("Looking at the processes that record needs Windows."));

        // The window ClassScribe runs in: its title, its taskbar button and the flash, one for the process, which puts the
        // window back as it was when ClassScribe ends. A run in this window shows its states there, and flashes once for
        // each new Alert, with a status sink of its own.
        services.AddSingleton<IWindowSignals>(provider => OperatingSystem.IsWindows()
            ? new ConsoleWindowSignals(provider.GetRequiredService<ILogger<ConsoleWindowSignals>>())
            : new NoWindowSignals());
        services.AddScoped<IRecorderStatusSink, WindowStatusSink>();

        // Windows' end of the session: a hidden window of its own for each run that records, made when the run is.
        services.AddScoped<ISessionEvents>(provider => OperatingSystem.IsWindows()
            ? new SessionWindow(provider.GetRequiredService<ILogger<SessionWindow>>())
            : throw new PlatformNotSupportedException("Hearing of Windows' end of the session needs Windows."));
        return services;
    }
}
