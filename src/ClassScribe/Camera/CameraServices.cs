using ClassScribe.Camera.Windows;
using Microsoft.Extensions.DependencyInjection;

namespace ClassScribe.Camera;

/// <summary>Registers the camera's services (teach-video 6, camera-first step 11).</summary>
public static class CameraServices
{
    /// <summary>
    /// Adds the camera list over Windows' device lists, which opens nothing, and its <see cref="Lazy{T}"/> for the devices
    /// command, so that the cameras are listed only when the command runs.
    /// </summary>
    public static IServiceCollection AddCamera(this IServiceCollection services)
    {
        services.AddSingleton<ICameraProvider, WindowsCameraProvider>();
        services.AddSingleton(provider => new Lazy<ICameraProvider>(provider.GetRequiredService<ICameraProvider>));
        return services;
    }
}
