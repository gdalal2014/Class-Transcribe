using ClassScribe.Audio.Microphones;
using ClassScribe.Commands;
using ClassScribe.Desktop.Windows;
using ClassScribe.Recorder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ClassScribe.Desktop;

/// <summary>
/// Registers ClassScribe's own Windows integration (architecture 5.2): keeping the laptop awake while it records and
/// uploads, where the laptop takes its power from, the computer's clocks, the registry values the microphone advice reads,
/// whether the process a run file names still runs, Windows' record of which apps use the microphone, the names of
/// processes and which of them are ClassScribe's own, the processes and windows of a YouTube video's Chrome, the window's
/// title, taskbar button and flash, Windows' end of the session, the desktop, the warnings of an exit and of an error
/// nobody caught, and the starter of the recorder and the alert window with the reader of the job ClassScribe runs in. The
/// watch for times when ClassScribe did not run is the use cases' (see <see cref="Commands.CommandUseCases"/>), and the
/// window's status sink the window's.
/// </summary>
public static class DesktopServices
{
    /// <summary>
    /// Adds the Windows integration's services. Transcribing keeps the laptop awake too, so on a system other than Windows
    /// the requests ask for nothing and the power source is not read, and transcribing an existing file works there; the
    /// window's signals show nothing there. What only recording needs (the computer's clocks, the registry, whether a process
    /// still runs, the microphone usage record, the process names, and Windows' end of the session) is made only when a run
    /// asks for it, and needs Windows. The session's window is one per run (one per scope).
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

        // The registry, where Lenovo Vantage keeps its microphone mode and Windows its ducking preference: read only, for
        // the advice about the microphone, which only a recording needs.
        services.AddSingleton<IRegistryValues>(provider => OperatingSystem.IsWindows()
            ? new RegistryValues(provider.GetRequiredService<ILogger<RegistryValues>>())
            : throw new PlatformNotSupportedException("Reading Windows' registry needs Windows."));

        // Whether the process a run file names still runs: one for the process.
        services.AddSingleton<IProcessLiveness>(provider => OperatingSystem.IsWindows()
            ? new ProcessLiveness(provider.GetRequiredService<TimeProvider>(), provider.GetRequiredService<ILogger<ProcessLiveness>>())
            : throw new PlatformNotSupportedException("Looking at the processes that record needs Windows."));

        // What the watch on the class app's microphone use and the microphone check read beside the sessions: Windows'
        // record of which apps use the microphone, read from the registry; the program behind a process id; and which
        // processes are ClassScribe's own, whose capture holds the microphone during a recording. Each is read only, and
        // nothing is touched before the first read.
        services.AddSingleton<IMicrophoneUsageRecord>(provider => OperatingSystem.IsWindows()
            ? new MicrophoneUsageRecord(provider.GetRequiredService<ILogger<MicrophoneUsageRecord>>())
            : throw new PlatformNotSupportedException("Reading Windows' microphone usage record needs Windows."));
        services.AddSingleton<IProcessNames>(_ => OperatingSystem.IsWindows()
            ? new ProcessNames()
            : throw new PlatformNotSupportedException("Naming the processes that use a microphone needs Windows."));
        services.AddSingleton<IOwnProcesses, OwnProcesses>();

        // A YouTube video's window (the record path): the tree of processes its browser heads, from Windows' process list, and
        // its windows' titles and close, from Windows' windows. Each only reads, but the close, which is posted as a click on
        // the close button is; nothing is touched before the first use.
        services.AddSingleton<IProcessFamily>(_ => OperatingSystem.IsWindows()
            ? new ProcessFamily(NativeProcessTree.Instance)
            : throw new PlatformNotSupportedException("Reading the processes a video's window started needs Windows."));
        services.AddSingleton<IVideoWindows>(_ => OperatingSystem.IsWindows()
            ? new ChromeWindows()
            : throw new PlatformNotSupportedException("Reading the windows of a video's Chrome needs Windows."));

        // The window ClassScribe runs in: its title, its taskbar button and the flash, one for the process, which puts the
        // window back as it was when ClassScribe ends.
        services.AddSingleton<IWindowSignals>(provider => OperatingSystem.IsWindows()
            ? new ConsoleWindowSignals(provider.GetRequiredService<ILogger<ConsoleWindowSignals>>())
            : new NoWindowSignals());

        // Windows' end of the session: a hidden window of its own for each run that records, made when the run is.
        services.AddScoped<ISessionEvents>(provider => OperatingSystem.IsWindows()
            ? new SessionWindow(provider.GetRequiredService<ILogger<SessionWindow>>())
            : throw new PlatformNotSupportedException("Hearing of Windows' end of the session needs Windows."));

        // The desktop a run opens its results with, and the warnings of an exit.
        services.AddSingleton<IDesktop, WindowsDesktop>();
        services.AddSingleton<IExitWarnings, ProcessExitWarnings>();

        // .NET's warning of an error nobody caught, which a run hears before the exit warnings, to write down the right cause.
        services.AddSingleton<ICrashWarnings, AppDomainCrashWarnings>();

        // The recorder and the alert window run in ClassScribe's own folder, which the launcher makes before any start
        // by writing the request there.
        services.AddSingleton<IProcessStarter>(provider => OperatingSystem.IsWindows()
            ? new ProcessStarter(provider.GetRequiredService<RuntimeFolder>().Root, provider.GetRequiredService<ILogger<ProcessStarter>>())
            : throw new PlatformNotSupportedException("Starting the recorder in the background needs Windows."));
        services.AddSingleton<IJobInspector>(provider => OperatingSystem.IsWindows()
            ? new JobInspector(provider.GetRequiredService<ILogger<JobInspector>>())
            : throw new PlatformNotSupportedException("Reading the job ClassScribe runs in needs Windows."));
        return services;
    }
}
