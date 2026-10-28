namespace ClassScribe.Camera;

/// <summary>What a camera process says when a camera cannot be opened: the kind, and the cause in words.</summary>
public static class CameraProblems
{
    /// <summary>
    /// The problem for an open that failed as <paramref name="failure"/> says: Windows' camera privacy settings or a security
    /// program for E_ACCESSDENIED, another app holding the camera for a sharing violation or a busy camera, a camera not
    /// connected, or the call and its code for anything else. The words have no full stop.
    /// </summary>
    public static CameraProblem ForFailedOpen(CameraCallFailedException failure)
    {
        var detail = $"{failure.Call} failed: {failure.Error}";
        return failure.Code switch
        {
            CameraHresults.AccessDenied => new(CameraProblemKind.Denied,
                $"Windows' camera privacy settings or a security program (such as Bitdefender's Webcam Protection) refused it ({detail})"),
            CameraHresults.SharingViolation or CameraHresults.Busy => new(CameraProblemKind.InUse, $"another app may be holding it alone ({detail})"),
            CameraHresults.DeviceNotConnected => new(CameraProblemKind.Lost, $"it is not connected ({detail})"),
            null when failure.Call == CameraRoute.Source => new(CameraProblemKind.Lost, $"Windows does not list it now; is it unplugged? ({failure.Error})"),
            _ => new(CameraProblemKind.Lost, $"Windows did not open it ({detail})")
        };
    }

    /// <summary>The words of a problem when the picture stopped coming during a hold.</summary>
    public const string Stopped = "it stopped sending its picture";
}
