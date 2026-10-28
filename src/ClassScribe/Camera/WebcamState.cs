namespace ClassScribe.Camera;

/// <summary>What the run knows of the webcam whose microphone is picked now, and of its hold (camera-first 3.4).</summary>
/// <param name="CameraName">The webcam's name, as Windows lists it.</param>
/// <param name="Holding">True while ClassScribe holds its picture open (Recording:WakeCameraMicrophone on).</param>
/// <param name="HasFrames">True while the held picture's frames arrive: the camera is awake.</param>
/// <param name="Problem">What is wrong with the hold, in words, while it lasts: the picture stopped, or the camera could not be opened; null otherwise.</param>
/// <param name="ProblemShort">The problem in a few words, for the title; null with no problem.</param>
public sealed record WebcamState(string CameraName, bool Holding, bool HasFrames, string? Problem = null, string? ProblemShort = null);
