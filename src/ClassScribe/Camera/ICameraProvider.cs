namespace ClassScribe.Camera;

/// <summary>Lists the colour cameras Windows can record from. Listing reads Windows' device lists only and never opens a camera, so no light comes on.</summary>
public interface ICameraProvider
{
    /// <summary>The colour cameras connected now, or why they could not be listed; never throws but for <paramref name="cancellationToken"/>.</summary>
    Task<CameraList> GetCamerasAsync(CancellationToken cancellationToken);
}
