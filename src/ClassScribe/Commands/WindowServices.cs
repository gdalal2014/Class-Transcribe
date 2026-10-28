using ClassScribe.Runs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ClassScribe.Commands;

/// <summary>
/// Registers the window's side of a run in the background: what shows one (the view of runs and the attach command), the
/// question whether to continue a recent recording, the camera's questions at a start, and the report at the start. The
/// recorder's host adds these too, since it is put together from the same registrations, and uses none of them. The starter,
/// the launcher of the recorder and the list, devices and camera-check commands are the use cases' (see
/// <see cref="CommandUseCases"/>), and the starter of the recorder and the reader of the job ClassScribe runs in are Windows'
/// (see <see cref="Desktop.DesktopServices"/>).
/// </summary>
public static class WindowServices
{
    /// <summary>
    /// Adds the window's services. What takes the settings of a run (the view, the questions) is made once per run, in the
    /// run's scope; the report, which reads the settings each time it uses them, is one for the process.
    /// </summary>
    public static IServiceCollection AddWindow(this IServiceCollection services)
    {
        services.AddScoped<IRunViewer, RecorderView>();
        services.AddScoped<ContinueQuestion>();
        services.AddScoped<CameraStart>();
        services.AddTransient<AttachCommand>();

        // The real report, in place of the one that reports nothing, which the commands' registration adds only when no
        // report is there yet: one report, so that a run is reported once.
        services.Replace(ServiceDescriptor.Singleton<IStartupReport, StartupReport>());
        return services;
    }
}