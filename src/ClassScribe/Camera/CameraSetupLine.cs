namespace ClassScribe.Camera;

/// <summary>How a set-up line stands.</summary>
public enum CameraSetupState
{
    /// <summary>As the camera needs it.</summary>
    Ok,

    /// <summary>The camera cannot be used until this is mended.</summary>
    Problem,

    /// <summary>Worth knowing; nothing to mend.</summary>
    Note,

    /// <summary>A part that is switched off.</summary>
    Off
}

/// <summary>One line of the camera's set-up, as camera-check shows it and setup-check will.</summary>
/// <param name="State">How it stands.</param>
/// <param name="Text">What it says, in plain words, without a full stop.</param>
public sealed record CameraSetupLine(CameraSetupState State, string Text)
{
    /// <summary>The line as a window shows it: its state in capitals, padded, then the words.</summary>
    public override string ToString() => $"{State switch { CameraSetupState.Ok => "OK", CameraSetupState.Problem => "PROBLEM", CameraSetupState.Note => "NOTE", _ => "OFF" },-8} {Text}";
}
