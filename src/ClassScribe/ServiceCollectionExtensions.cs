using ClassScribe.Commands;
using ClassScribe.Settings;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ClassScribe;

/// <summary>Registers the app's services, each project's own (architecture 5.2), then the window's.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds everything ClassScribe needs, bound to the given configuration: the clock; the use cases (see
    /// <see cref="ApplicationServices"/>); the adapters, the settings first (see <see cref="InfrastructureServices"/>); the
    /// commands' side that talks to the person; and the window's side of a run in the background, last, since it replaces
    /// the stand-in report the use cases add. The link to a recorder needs no registration: the view of a run makes
    /// its own client, and the server is the recorder's. The settings file and what reads it again before each run are added
    /// by AppHost (see <see cref="SettingsServices.AddSettingsReload"/>), which builds the configuration they work on; the
    /// gate, the menu and the app need them. The recorder's host adds its own services after these (see
    /// <see cref="AppHost.BuildRecorder"/>).
    /// </summary>
    public static IServiceCollection AddClassScribe(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton(TimeProvider.System);
        return services
            .AddClassScribeApplication()
            .AddClassScribeInfrastructure(configuration)
            .AddCommands()
            .AddWindow();
    }
}
