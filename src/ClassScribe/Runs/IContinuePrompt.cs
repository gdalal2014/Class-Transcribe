using ClassScribe.Audio;
using ClassScribe.Storage;

namespace ClassScribe.Runs;

/// <summary>What was decided about continuing a recording of the same name that stopped recently without a transcript.</summary>
/// <param name="GoBack">True when Esc was pressed in the menu's question: nothing is to start.</param>
/// <param name="Continue">The recording to continue as its next part; null for a new recording.</param>
/// <param name="Hint">A line to show before the run starts, such as how to continue the other recording instead; null for none.</param>
public sealed record ContinueAnswer(bool GoBack, RecordingFiles? Continue, string? Hint);

/// <summary>
/// Asks whether a new run continues, as its next part, a recording of the same name that stopped recently without a
/// transcript, as after a crash, a closed window or a restart (architecture 4.3; section 1.14 of the design): the menu's
/// question, C, N or Esc, and the command line's flags, which never ask. The window answers it; a recorder is never asked,
/// since the request it is given already says what was decided.
/// </summary>
public interface IContinuePrompt
{
    /// <summary>
    /// The menu's question, for a run of <paramref name="name"/> recording what <paramref name="mode"/> names, with the camera
    /// when <paramref name="camera"/>, into <paramref name="root"/>: when there is a recording to continue, it is described
    /// and C continues it, N starts a new recording, Esc or Ctrl+C goes back. With none, nothing is asked and a new
    /// recording starts.
    /// </summary>
    Task<ContinueAnswer> AskAsync(string name, RecordingMode mode, string root, bool camera = false);

    /// <summary>
    /// The command line's answer, for <paramref name="choice"/>, which never asks: --continue (<see cref="ContinueChoice.Continue"/>)
    /// continues the recording, or says there is none and starts a new one; --new starts a new one without a word; neither
    /// (<see cref="ContinueChoice.Ask"/>) starts a new one, with a line saying how to continue the other one instead. With
    /// <paramref name="camera"/>, only a lesson recorded with the camera is continued.
    /// </summary>
    ContinueAnswer Resolve(string name, RecordingMode mode, string root, ContinueChoice choice, bool camera = false);
}
