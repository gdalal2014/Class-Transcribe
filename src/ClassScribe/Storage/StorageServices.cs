using Microsoft.Extensions.DependencyInjection;

namespace ClassScribe.Storage;

/// <summary>Registers where recordings and transcripts go, and the marks set while recording.</summary>
public static class StorageServices
{
    /// <summary>Adds the storage services.</summary>
    public static IServiceCollection AddStorage(this IServiceCollection services)
    {
        services.AddSingleton<ClassFolders>();
        services.AddSingleton<ClassMarks>();
        return services;
    }
}
