using Microsoft.Extensions.DependencyInjection;

namespace ClassScribe.Camera;

/// <summary>
/// Registers the camera's use cases and the services they drive (teach-video 6; architecture 5.2): the set-up lines the
/// camera check shows, the recorder of a lesson's picture and the maker of a lesson's video. What reaches Windows is
/// registered with the adapters.
/// </summary>
public static class CameraUseCases
{
    /// <summary>Adds the set-up lines the camera check shows, the recorder of a lesson's picture and the maker of a lesson's video.</summary>
    public static IServiceCollection AddCameraUseCases(this IServiceCollection services)
    {
        services.AddSingleton<CameraSetupFacts>();

        // A lesson's picture (slice 7): each run's camera track starts camera processes with record jobs.
        services.AddSingleton<ICameraRecorder, CameraRecorder>();

        // A lesson's video (slice 8): a camera process with a finish job, for a lesson that stopped, finish-video and the Vs.
        services.AddTransient<LessonVideoMaker>();
        return services;
    }
}