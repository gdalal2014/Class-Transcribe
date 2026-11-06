using System.Globalization;

namespace ClassScribe.Camera;

/// <summary>Lists the colour cameras Windows can record from. Listing reads Windows' device lists only and never opens a camera, so no light comes on.</summary>
public interface ICameraProvider
{
    /// <summary>The colour cameras connected now, or why they could not be listed; never throws but for <paramref name="cancellationToken"/>.</summary>
    Task<CameraList> GetCamerasAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Windows' camera list read for the window, within a limit (release 0.5.0 review, 3.1): a listing that hangs inside
/// Windows must not hold the window, where Esc, Q and Ctrl+C do nothing while it waits, so the start of a lesson with the
/// camera and menu choice 7's camera question wait for it at most <see cref="Limit"/>, the limit the webcam hold and a
/// lesson's camera track already use, and go on with a list that says it could not be read.
/// </summary>
public static class CameraListing
{
    /// <summary>The longest the window waits for Windows' camera list, or for the camera's set-up to be read.</summary>
    public static readonly TimeSpan Limit = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The cameras <paramref name="cameras"/> lists, read on a thread of the pool and waited for at most <paramref name="limit"/>
    /// on <paramref name="time"/>: a list Windows has not answered in time is a list that says it could not be read
    /// (<see cref="NotListedWithin"/>); the listing itself is left to end by itself, and nothing waits for it again. A list
    /// that throws throws here, as the provider's own promise is never to.
    /// </summary>
    public static async Task<CameraList> ListWithinAsync(this ICameraProvider cameras, TimeSpan limit, TimeProvider time, CancellationToken cancellationToken)
    {
        var listing = Task.Run(() => cameras.GetCamerasAsync(cancellationToken), cancellationToken);
        try
        {
            return await listing.WaitAsync(limit, time);
        }
        catch (TimeoutException)
        {
            // Deliberately not rethrown: the listing hangs inside Windows, and the window must not wait for it any longer;
            // the list says so, and whoever asked goes on as with any list that could not be read.
            return new CameraList([], NotListedWithin(limit));
        }
    }

    /// <summary>The list's problem when Windows did not answer within <paramref name="limit"/>.</summary>
    public static string NotListedWithin(TimeSpan limit) => string.Create(CultureInfo.InvariantCulture, $"Windows did not list the cameras within {limit.TotalSeconds:0} seconds");
}
