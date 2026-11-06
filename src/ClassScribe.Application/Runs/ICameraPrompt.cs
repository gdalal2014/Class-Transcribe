using ClassScribe.Commands;

namespace ClassScribe.Runs;

/// <summary>
/// The camera's questions at the start of a run whose plan has a camera (architecture 4.3; teach-video 1.4 and 1.6): the
/// picture's size resolved into the request, what keeps the camera from being used on this computer looked for, and in the
/// menu Enter or S; a plan without a camera goes on as it is. The window answers them; where no prompt is registered, the
/// starter refuses a run with a camera as a build without the camera refuses it, with <see cref="NothingStarted"/>.
/// </summary>
public interface ICameraPrompt
{
    /// <summary>What a refusal says anywhere but the menu, which offers the sound only instead.</summary>
    public const string NothingStarted = "Nothing was recorded; leave out --camera to record the sound alone.";

    /// <summary>
    /// Decides the start of <paramref name="request"/>, from <paramref name="origin"/>, when its plan has a camera: the run
    /// to start, with the camera or the sound only, Esc going back, or a refusal with its exit code. A plan without a camera
    /// goes on as it is.
    /// </summary>
    Task<CameraDecision> DecideAsync(RecordRequest request, StartOrigin origin);
}
