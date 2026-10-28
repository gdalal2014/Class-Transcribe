using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace ClassScribe.Recorder;

/// <summary>Registers the recorder's log, to which every logger writes as well as to the console (architecture 5.2).</summary>
public static class RecorderServices
{
    /// <summary>Adds the recorder's log and the logging provider that writes into it.</summary>
    public static IServiceCollection AddRecorderLog(this IServiceCollection services)
    {
        // The recorder's log, to which every logger writes as well as to the console: the run names its file beside the
        // recording, <base>.log. A window keeps the lines between runs in memory only; a recorder's host replaces it with
        // one that writes those to recorder.log. One provider however often this is called.
        services.AddSingleton(provider => new RecorderLog(provider.GetRequiredService<TimeProvider>()));

        // The run's log as the use cases reach it: the same singleton behind its port, resolved by the adapter's type, so
        // the log a recorder's host puts in its place is followed.
        services.AddSingleton<IRunLog>(provider => provider.GetRequiredService<RecorderLog>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ILoggerProvider, RecorderLogProvider>());
        return services;
    }
}