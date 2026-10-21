using ClassScribe.Desktop;
using ClassScribe.Desktop.Windows;
using ClassScribe.Recorder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace ClassScribe.Commands;

/// <summary>
/// Registers the window's side of a run in the background: what starts a run (the starter, the launcher of the recorder,
/// the question whether to continue a recent recording), what shows one (the view of runs and the attach command), the
/// report at the start, the list, devices and camera-check commands, and the two things that reach Windows for them: the starter of the
/// recorder and the alert window, and the reader of the job ClassScribe runs in. The recorder's host adds these too, since
/// it is put together from the same registrations, and uses none of them.
/// </summary>
public static class WindowServices
{
    /// <summary>
    /// Adds the window's services. What takes the settings of a run (the starter, the view, the question) is made once per
    /// run, in the run's scope; the launcher and the report, which read the settings each time they use them, are one for
    /// the process, as are the two that reach Windows, which only Windows can provide.
    /// </summary>
    public static IServiceCollection AddWindow(this IServiceCollection services)
    {
        services.AddScoped<IRecordStarter, RecordStarter>();
        services.AddScoped<IRunViewer, RecorderView>();
        services.AddScoped<ContinueQuestion>();
        services.AddScoped<CameraStart>();
        services.AddTransient<AttachCommand>();
        services.AddTransient<ListCommand>();
        services.AddTransient<DevicesCommand>();
        services.AddTransient<CameraCheckCommand>();
        services.AddSingleton<RecorderLauncher>();

        // The real report, in place of the one that reports nothing, which the commands' registration adds only when no
        // report is there yet: one report, so that a run is reported once.
        services.Replace(ServiceDescriptor.Singleton<IStartupReport, StartupReport>());

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
