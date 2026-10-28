using ClassScribe.Recorder;
using Microsoft.Extensions.DependencyInjection;

namespace ClassScribe.Storage;

/// <summary>
/// Registers where recordings and transcripts go, the marks set while recording, the free space of a drive, and
/// ClassScribe's own folder: the lock that allows one recording at a time and the run files; and its roaming folder, which
/// holds the Google Drive sign-in.
/// </summary>
public static class StorageServices
{
    /// <summary>Adds the storage services, each one for the process.</summary>
    public static IServiceCollection AddStorage(this IServiceCollection services)
    {
        services.AddSingleton<ClassFolders>();

        // The recordings' library as the use cases reach it: the same instance behind its port.
        services.AddSingleton<IClassFolders>(provider => provider.GetRequiredService<ClassFolders>());
        services.AddSingleton<ClassMarks>();
        services.AddSingleton<IFreeSpace, FreeSpace>();

        // %LOCALAPPDATA%\ClassScribe: nothing is made there until a run needs it.
        services.AddSingleton(_ => new RuntimeFolder());
        services.AddSingleton<RecordingLock>();
        services.AddSingleton<RunJournal>();

        // %APPDATA%\ClassScribe: the Google Drive sign-in, made only when one is stored.
        services.AddSingleton(_ => new RoamingFolder());
        return services;
    }
}
