using ClassScribe.Desktop.Windows;
using ClassScribe.Recorder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ClassScribe.Desktop;

/// <summary>
/// Registers ClassScribe's own Windows integration: keeping the laptop awake while it records and uploads, where the
/// laptop takes its power from, the computer's clocks, and the watch for times when ClassScribe did not run.
/// </summary>
public static class DesktopServices
{
    /// <summary>
    /// Adds the Windows integration's services. Transcribing keeps the laptop awake too, so on a system other than Windows
    /// the requests ask for nothing and the power source is not read, and transcribing an existing file works there. The
    /// computer's clocks are made only when a recording needs them; recording needs Windows anyway. The freeze watch is
    /// one per run (one per scope).
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
        return services;
    }
}
