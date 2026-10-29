using ClassScribe.Camera.Windows;
using Microsoft.Extensions.DependencyInjection;

namespace ClassScribe.Camera;

/// <summary>
/// Registers the camera's services that reach Windows (teach-video 6, camera-first step 11; architecture 5.2). The set-up
/// lines the camera check shows, a lesson's picture and a lesson's video are the use cases' (see <see cref="CameraUseCases"/>).
/// </summary>
public static class CameraServices
{
    /// <summary>
    /// Adds the camera list over Windows' device lists, which opens nothing, and its <see cref="Lazy{T}"/> for the devices
    /// command, so that the cameras are listed only when the command runs; Windows' camera record and what Windows says the
    /// camera needs; each recording's webcam hold; and the start of a camera process.
    /// </summary>
    public static IServiceCollection AddCamera(this IServiceCollection services)
    {
        services.AddSingleton<ICameraProvider, WindowsCameraProvider>();
        services.AddSingleton(provider => new Lazy<ICameraProvider>(provider.GetRequiredService<ICameraProvider>));
        services.AddSingleton<ICameraUsageRecord, CameraUsageRecord>();
        services.AddSingleton<ICameraSystem, WindowsCameraSystem>();

        // Each recording's webcam hold (camera-first 3.4): which webcam the microphone belongs to, and with
        // Recording:WakeCameraMicrophone on its picture held open, shared, in a camera process.
        services.AddSingleton<WebcamHolds>();
        services.AddSingleton<IWebcamHolds>(provider => provider.GetRequiredService<WebcamHolds>());

        // The start of a camera process (--camera-process <job id>), in a kill-on-close job, from ClassScribe's own folder;
        // the webcam hold and the camera check start one, and a test run refuses the real start (DeviceGuards).
        services.AddSingleton<ICameraProcessStarter>(provider => OperatingSystem.IsWindows()
            ? new CameraProcessStarter(provider.GetRequiredService<Recorder.RuntimeFolder>(), provider.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>())
            : throw new PlatformNotSupportedException("Starting a camera process needs Windows."));
        return services;
    }
}
